using System.Threading;
using System.Windows;
using MitsubishiMonitor.Demo.ViewModels;
using MitsubishiMonitor.Demo.Views;

namespace MitsubishiMonitor.Demo
{
    public partial class App : Application
    {
        private int _globalExceptionLoggingRegistered;

        /// <summary>
        /// HslCommunication 使用同步阻塞 API，4 路 PLC 加上数据库/串口任务可能暂时占用多个工作线程。
        /// 将 worker 最小值适度提高到 16，减少冷启动时的线程注入延迟；不修改 IO completion-port
        /// 最小值，因为当前 PLC 调用并不使用异步 IO completion port。WPF Dispatcher 仍是独立 UI 线程。
        /// </summary>
        protected override void OnStartup(StartupEventArgs e)
        {
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
            base.OnExit(e);
        }
    }
}
