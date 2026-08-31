using System.Threading;
using System.Linq;
using System.Windows;
using MitsubishiMonitor.Demo.Services;
using MitsubishiMonitor.Demo.ViewModels;
using MitsubishiMonitor.Demo.Views;

namespace MitsubishiMonitor.Demo
{
    public partial class App : Application
    {
        private int _globalExceptionLoggingRegistered;
        private SingleInstanceGuard _singleInstanceGuard;

        /// <summary>
        /// 视频演示模式只由显式命令行参数启用；生产包不会因目录中的普通文件误入演示状态。
        /// </summary>
        public static bool IsDemoVideoMode { get; private set; }

        /// <summary>
        /// HslCommunication 使用同步阻塞 API，4 路 PLC 加上数据库/串口任务可能暂时占用多个工作线程。
        /// 将 worker 最小值适度提高到 16，减少冷启动时的线程注入延迟；不修改 IO completion-port
        /// 最小值，因为当前 PLC 调用并不使用异步 IO completion port。WPF Dispatcher 仍是独立 UI 线程。
        /// </summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            IsDemoVideoMode = e.Args.Any(arg =>
                string.Equals(arg, "--demo-video", System.StringComparison.OrdinalIgnoreCase));

            if (!SingleInstanceGuard.TryAcquire(out _singleInstanceGuard, out var singleInstanceError))
            {
                var message = string.IsNullOrWhiteSpace(singleInstanceError)
                    ? "监控程序已经在本机运行。为避免重复连接 PLC 和重复写入日志，本实例将退出。"
                    : $"无法建立单实例保护，本实例已停止启动：\n{singleInstanceError}";
                MessageBox.Show(message, "禁止重复运行", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(1);
                return;
            }

            if (IsDemoVideoMode)
                AppConfig.EnableDemoIsolation();

            RegisterGlobalExceptionLogging();

            try
            {
                ThreadPool.GetMinThreads(out var workerMin, out var ioMin);
                ThreadPool.SetMinThreads(System.Math.Max(workerMin, 16), ioMin);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] SetMinThreads 失败: {ex.Message}");
            }
            base.OnStartup(e);
        }

        private void RegisterGlobalExceptionLogging()
        {
            if (Interlocked.Exchange(ref _globalExceptionLoggingRegistered, 1) == 1) return;

            DispatcherUnhandledException += (sender, args) =>
            {
                LogUnhandledException("DispatcherUnhandledException", args.Exception, false);
                // 只吞掉明确可恢复的取消异常。其他 UI 异常继续走 WPF 默认终止流程，
                // 避免图表/绑定更新失败后程序仍“在线”但界面永久停在旧状态。
                args.Handled = args.Exception is System.OperationCanceledException;
            };

            System.AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                LogUnhandledException("AppDomainUnhandledException", args.ExceptionObject as System.Exception, args.IsTerminating);
            };

            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (sender, args) =>
            {
                LogUnhandledException("UnobservedTaskException", args.Exception, false);
                args.SetObserved();
            };
        }

        private static void LogUnhandledException(string source, System.Exception ex, bool isTerminating)
        {
            try
            {
                Views.MainWindow.DbgLog("App:" + source, "捕获到未处理异常", new
                {
                    isTerminating,
                    error = ex?.Message ?? "非 Exception 异常对象",
                    type = ex?.GetType().FullName ?? "",
                    stack = ex?.StackTrace ?? ""
                }, "EXCEPTION");
            }
            catch
            {
                // 异常日志不能再影响主程序启动。
            }
        }

        /// <summary>
        /// 应用退出时兜底清理：即使主窗口被异常关闭，也确保 PLC 断开、日志缓冲刷写、三色灯熄灭。
        /// </summary>
        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                if (MainWindow is MainWindow mw && mw.DataContext is DeviceListViewModel vm)
                {
                    vm.Dispose();
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] OnExit 清理异常: {ex.Message}");
            }
            _singleInstanceGuard?.Dispose();
            _singleInstanceGuard = null;
            base.OnExit(e);
        }
    }
}
