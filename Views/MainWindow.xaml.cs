using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MitsubishiMonitor.Demo.Models;
using MitsubishiMonitor.Demo.ViewModels;

namespace MitsubishiMonitor.Demo.Views
{
    public partial class MainWindow : Window
    {
        private readonly DeviceListViewModel _viewModel;
        private readonly DispatcherTimer _clockTimer;
        private long _lastUiTickUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        private int _startupHealthTicks;

        // 诊断：UI 线程卡死监控
        private System.Timers.Timer _heartbeatTimer;
        private static readonly object _dbgLogLock = new();

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        /// <summary>
        /// 诊断日志路径：放在 exe 同目录下的 logs\ 子目录，按日期分文件。
        /// 这样部署到工控机后，无论谁登录都能在 exe 旁边一眼找到，方便运维拷贝/排错。
        /// 失败回退桌面（特殊场景下 exe 目录可能没有写权限，比如 Program Files）。
        /// </summary>
        private static readonly string _dbgLog = ResolveDbgLogPath();

        private static string ResolveDbgLogPath()
        {
            string fileName = $"diagnostic-{DateTime.Now:yyyyMMdd}.log";

            // 候选目录按优先级：
            //   1) Environment.ProcessPath 同目录\logs   —— 最常规，部署到工控机时就是 publish\logs
            //      （ProcessPath 在 PublishSingleFile=true 自解压场景下也返回原 exe 位置，比 MainModule.FileName 稳）
            //   2) AppContext.BaseDirectory\logs         —— ProcessPath 不可用时的兜底
            //   3) 用户桌面                              —— 工控机上 exe 目录无写权限时（如装到 Program Files）
            //   4) %TEMP%                                —— 极端兜底
            var candidates = new System.Collections.Generic.List<string>();
            try
            {
                var processPath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(processPath))
                {
                    var dir = System.IO.Path.GetDirectoryName(processPath);
                    if (!string.IsNullOrEmpty(dir))
                        candidates.Add(System.IO.Path.Combine(dir, "logs"));
                }
            }
            catch { }

            try
            {
                if (!string.IsNullOrEmpty(AppContext.BaseDirectory))
                    candidates.Add(System.IO.Path.Combine(AppContext.BaseDirectory, "logs"));
            }
            catch { }

            try
            {
                candidates.Add(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
            }
            catch { }

            try
            {
                candidates.Add(System.IO.Path.GetTempPath());
            }
            catch { }

            foreach (var dir in candidates)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                try
                {
                    System.IO.Directory.CreateDirectory(dir);
                    var path = System.IO.Path.Combine(dir, fileName);
                    // 试写一次，确认目录有写权限（空字符串不会改文件内容）
                    System.IO.File.AppendAllText(path, "");
                    return path;
                }
                catch { /* 这个目录写不进就试下一个 */ }
            }

            // 全部失败：返回 temp 路径，调用方自己 try/catch 处理
            return System.IO.Path.Combine(System.IO.Path.GetTempPath(), fileName);
        }

        public static string DiagnosticLogPath => _dbgLog;

        // UI 卡顿告警阈值（秒）：超过即捕获完整诊断快照
        private const double UiFreezeAlertSeconds = 5.0;
        // 同一次卡死内不重复记录的去抖窗口（秒）：避免一次卡死刷十几条相同记录
        private const double FreezeReportDebounceSeconds = 30.0;
        private long _lastFreezeReportUnixMs = 0;

        internal static void DbgLog(string location, string msg, object data, string hyp)
        {
            // 在后台线程执行文件I/O，绝不阻塞UI线程
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var entry = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        sessionId = "f8e8e9", runId = "run2", hypothesisId = hyp,
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        location, message = msg, data
                    });
                    lock (_dbgLogLock)
                        File.AppendAllText(_dbgLog, entry + "\n");
                }
                catch { }  // 文件写入失败静默忽略，不弹窗、不阻塞任何线程
            });
        }

        /// <summary>
        /// UI 卡顿超阈值时调用：在后台线程采集所有可获得的进程内诊断信息（ThreadPool/GC/Process.Threads 等）
        /// 并写入 debug 日志。注意整个采集过程都在后台线程，绝不触碰 UI 线程，避免被同样的卡顿连累。
        /// </summary>
        private static void CaptureFreezeSnapshot(double uiLagSeconds, int uiThreadId)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    ThreadPool.GetAvailableThreads(out var avWorker, out var avIo);
                    ThreadPool.GetMinThreads(out var minWorker, out var minIo);
                    ThreadPool.GetMaxThreads(out var maxWorker, out var maxIo);

                    var proc = Process.GetCurrentProcess();
                    proc.Refresh();

                    // 收集所有线程状态：可看出 UI 线程在等什么、有几个线程在 WaitReason=ExecutionDelay 等
                    var threadInfos = new System.Collections.Generic.List<object>();
                    foreach (ProcessThread t in proc.Threads)
                    {
                        try
                        {
                            string waitReason = "";
                            if (t.ThreadState == System.Diagnostics.ThreadState.Wait)
                            {
                                try { waitReason = t.WaitReason.ToString(); } catch { waitReason = "?"; }
                            }
                            threadInfos.Add(new
                            {
                                tid = t.Id,
                                state = t.ThreadState.ToString(),
                                waitReason,
                                cpuMs = (long)t.TotalProcessorTime.TotalMilliseconds,
                                isUi = t.Id == uiThreadId
                            });
                        }
                        catch { /* 部分线程可能在采集瞬间结束 */ }
                    }

                    DbgLog("MainWindow:FREEZE", "UI 卡顿超阈值，捕获诊断快照", new
                    {
                        uiLagSeconds,
                        uiThreadId,
                        memoryMB = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1),
                        gcGen0 = GC.CollectionCount(0),
                        gcGen1 = GC.CollectionCount(1),
                        gcGen2 = GC.CollectionCount(2),
                        threadPool = new
                        {
                            workerAvailable = avWorker, ioAvailable = avIo,
                            workerMin = minWorker, ioMin = minIo,
                            workerMax = maxWorker, ioMax = maxIo,
                            workerInUse = maxWorker - avWorker,
                            ioInUse = maxIo - avIo
                        },
                        process = new
                        {
                            handles = proc.HandleCount,
                            threadsTotal = proc.Threads.Count,
                            wsMB = Math.Round(proc.WorkingSet64 / 1048576.0, 1),
                            privateMB = Math.Round(proc.PrivateMemorySize64 / 1048576.0, 1)
                        },
                        threads = threadInfos
                    }, "FREEZE");
                }
                catch (Exception ex)
                {
                    DbgLog("MainWindow:FREEZE", "诊断快照采集失败", new { ex = ex.Message }, "FREEZE");
                }
            });
        }
        // #endregion

        public MainWindow()
        {
            InitializeComponent();
            DataContext = _viewModel = new DeviceListViewModel();

            // 系统时间更新定时器
            _clockTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _clockTimer.Tick += UpdateClock;
            _clockTimer.Start();
            UpdateClock(null, null);

            // #region agent log - 启动验证
            DbgLog("MainWindow:ctor", "程序已启动(日志系统正常)", new { time = DateTime.Now.ToString("HH:mm:ss"), logPath = _dbgLog }, "INIT");
            Console.WriteLine($"[DEBUG] 日志路径: {_dbgLog}");
            // #endregion

            // 后台心跳：每 2s 检查 UI 线程是否还在跳，超阈值即采集诊断快照
            // 间隔从 10s 缩短到 2s：让"卡顿—快照"延迟最多 2s，避免错过短暂卡顿窗口
            // ProcessThread.Id 是操作系统线程 ID，必须与 Win32 GetCurrentThreadId 对比；
            // ManagedThreadId 与其不是同一套编号。
            var uiThreadId = unchecked((int)GetCurrentThreadId());
            _heartbeatTimer = new System.Timers.Timer(2000);
            _heartbeatTimer.Elapsed += (s, e) =>
            {
                var mem = GC.GetTotalMemory(false);
                var uiLagSeconds = Math.Round(
                    (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() -
                     System.Threading.Interlocked.Read(ref _lastUiTickUnixMs)) / 1000.0,
                    1);

                var startupTick = System.Threading.Interlocked.Increment(ref _startupHealthTicks);
                if (startupTick <= 60 && startupTick % 5 == 0)
                {
                    DbgLog("MainWindow:startup-health", "启动阶段健康心跳", new
                    {
                        tick = startupTick,
                        memoryMB = Math.Round(mem / 1048576.0, 1),
                        connectionStatus = _viewModel?.ConnectionStatusText,
                        pendingOperationUiDevices = _viewModel?.DeviceManager?.PendingOperationCountUpdateDevices ?? 0,
                        pendingMonitorUiUpdate = _viewModel?.DeviceManager?.HasPendingMonitorUiUpdate ?? false,
                        uiThreadLagSeconds = uiLagSeconds,
                        time = DateTime.Now.ToString("HH:mm:ss")
                    }, "STARTUP");
                }

                // 只在 lag > 2s 时才写心跳日志，避免正常情况刷一堆无价值条目
                if (uiLagSeconds > 2.0)
                {
                    DbgLog("MainWindow:heartbeat", "UI 线程跳动滞后", new
                    {
                        memoryMB = Math.Round(mem / 1048576.0, 1),
                        stateChanges10s = Services.DeviceManagerService.DbgStateChangeCount,
                        pendingOperationUiDevices = _viewModel?.DeviceManager?.PendingOperationCountUpdateDevices ?? 0,
                        pendingMonitorUiUpdate = _viewModel?.DeviceManager?.HasPendingMonitorUiUpdate ?? false,
                        uiThreadLagSeconds = uiLagSeconds,
                        time = DateTime.Now.ToString("HH:mm:ss")
                    }, "A/C/E");
                    System.Threading.Interlocked.Exchange(ref Services.DeviceManagerService.DbgStateChangeCount, 0);
                }

                // 触发完整诊断快照：lag 超阈值 且 距上次快照已超去抖窗口
                if (uiLagSeconds > UiFreezeAlertSeconds)
                {
                    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var lastReport = System.Threading.Interlocked.Read(ref _lastFreezeReportUnixMs);
                    if (nowMs - lastReport > FreezeReportDebounceSeconds * 1000)
                    {
                        System.Threading.Interlocked.Exchange(ref _lastFreezeReportUnixMs, nowMs);
                        CaptureFreezeSnapshot(uiLagSeconds, uiThreadId);
                    }
                }
            };
            _heartbeatTimer.AutoReset = true;
            _heartbeatTimer.Start();
            // #endregion

            // 窗口加载时确保不超出屏幕
            this.Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 确保窗口在屏幕范围内
            var screenWidth = SystemParameters.PrimaryScreenWidth;
            var screenHeight = SystemParameters.PrimaryScreenHeight;

            if (this.ActualWidth > screenWidth || this.ActualHeight > screenHeight)
            {
                // 窗口太大，调整大小
                this.Width = Math.Min(this.ActualWidth, screenWidth - 50);
                this.Height = Math.Min(this.ActualHeight, screenHeight - 100);
            }

            // 确保窗口不超出屏幕边界
            if (this.Top + this.ActualHeight > screenHeight)
            {
                this.Top = screenHeight - this.ActualHeight;
            }
            if (this.Left + this.ActualWidth > screenWidth)
            {
                this.Left = screenWidth - this.ActualWidth;
            }
            if (this.Top < 0) this.Top = 0;
            if (this.Left < 0) this.Left = 0;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                DbgLog("MainWindow:Loaded", "主窗口已加载，启动 UI 空闲后的自动连接", new
                {
                    actualWidth = Math.Round(ActualWidth, 1),
                    actualHeight = Math.Round(ActualHeight, 1),
                    windowState = WindowState.ToString()
                }, "STARTUP");
                _viewModel.StartAutoConnectAfterUiReady();
            }), DispatcherPriority.ApplicationIdle);
        }

        private void UpdateClock(object sender, EventArgs e)
        {
            System.Threading.Interlocked.Exchange(
                ref _lastUiTickUnixMs,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var now = DateTime.Now;
            CurrentDateText.Text = now.ToString("yyyy年MM月dd日 dddd");
            CurrentTimeText.Text = now.ToString("HH:mm:ss");
        }

        #region 窗口控制

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleMaximize();
            }
            else
            {
                DragMove();
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void Maximize_Click(object sender, RoutedEventArgs e)
        {
            ToggleMaximize();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void ToggleMaximize()
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            // 弹出设置对话框，传入 DeviceManager 以便保存后立即生效
            var dialog = new SettingsDialog(_viewModel.DeviceManager)
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            dialog.ShowDialog();
        }

        private void LogQuery_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var win = new LogQueryWindow(_viewModel.DeviceManager)
                {
                    Owner = this
                };
                win.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"打开日志查询页失败:\n{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        protected override void OnClosed(EventArgs e)
        {
            _clockTimer?.Stop();
            // #region agent log
            _heartbeatTimer?.Stop();
            _heartbeatTimer?.Dispose();
            // #endregion

            // 释放 ViewModel：停 PLC 采集、关三色灯、同步刷写日志缓冲，避免数据丢失
            try { _viewModel?.Dispose(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow] 关闭清理异常: {ex.Message}");
            }

            base.OnClosed(e);
        }
    }
}
