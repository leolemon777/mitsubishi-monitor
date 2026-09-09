using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
        private static readonly ConcurrentQueue<string> _dbgLogQueue = new();
        private static int _dbgLogWriterRunning;
        private static int _dbgLogQueuedCount;
        private static long _dbgLogDroppedCount;
        private const int MaxDiagnosticQueueSize = 10000;
        private const long MaxDiagnosticFileBytes = 32L * 1024 * 1024;

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

            if (App.IsUiSmokeMode)
            {
                try
                {
                    var smokeDirectory = Path.Combine(
                        Path.GetTempPath(),
                        "MitsubishiMonitor",
                        "UiSmoke",
                        Environment.ProcessId.ToString());
                    Directory.CreateDirectory(smokeDirectory);
                    return Path.Combine(smokeDirectory, fileName);
                }
                catch
                {
                    // 继续走常规回退链；日志失败不能掩盖实际 UI 冒烟结果。
                }
            }

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
                    CleanupOldDiagnosticLogs(dir);
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

        private static void CleanupOldDiagnosticLogs(string directory)
        {
            var cutoff = DateTime.UtcNow.AddDays(-30);
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "diagnostic-*.log*"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < cutoff)
                            File.Delete(file);
                    }
                    catch
                    {
                        // 单个历史诊断文件被占用时跳过，不影响主程序启动。
                    }
                }
            }
            catch
            {
                // 诊断日志清理失败不影响监控启动。
            }
        }

        // UI 卡顿告警阈值（秒）：超过即捕获完整诊断快照
        private const double UiFreezeAlertSeconds = 5.0;
        // 同一次卡死内不重复记录的去抖窗口（秒）：避免一次卡死刷十几条相同记录
        private const double FreezeReportDebounceSeconds = 30.0;
        private long _lastFreezeReportUnixMs = 0;

        internal static void DbgLog(string location, string msg, object data, string hyp)
        {
            try
            {
                var entry = System.Text.Json.JsonSerializer.Serialize(new
                {
                    processId = Environment.ProcessId,
                    hypothesisId = hyp,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    location,
                    message = msg,
                    data
                });
                _dbgLogQueue.Enqueue(entry);
                var queued = Interlocked.Increment(ref _dbgLogQueuedCount);
                while (queued > MaxDiagnosticQueueSize && _dbgLogQueue.TryDequeue(out _))
                {
                    queued = Interlocked.Decrement(ref _dbgLogQueuedCount);
                    Interlocked.Increment(ref _dbgLogDroppedCount);
                }
                StartDiagnosticWriter();
            }
            catch
            {
                Interlocked.Increment(ref _dbgLogDroppedCount);
            }
        }

        private static void StartDiagnosticWriter()
        {
            if (Interlocked.Exchange(ref _dbgLogWriterRunning, 1) == 1)
                return;

            _ = System.Threading.Tasks.Task.Run(DrainDiagnosticLogQueue);
        }

        private static void DrainDiagnosticLogQueue()
        {
            try
            {
                while (!_dbgLogQueue.IsEmpty)
                {
                    var batch = new StringBuilder();
                    var dropped = Interlocked.Exchange(ref _dbgLogDroppedCount, 0);
                    if (dropped > 0)
                    {
                        batch.AppendLine(System.Text.Json.JsonSerializer.Serialize(new
                        {
                            processId = Environment.ProcessId,
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            location = "DiagnosticLog",
                            message = "诊断队列溢出或写入失败",
                            dropped
                        }));
                    }

                    var batchCount = 0;
                    while (batchCount < 500 && _dbgLogQueue.TryDequeue(out var entry))
                    {
                        Interlocked.Decrement(ref _dbgLogQueuedCount);
                        batch.AppendLine(entry);
                        batchCount++;
                    }

                    if (batch.Length == 0)
                        continue;

                    try
                    {
                        lock (_dbgLogLock)
                        {
                            RotateDiagnosticLogIfNeeded();
                            File.AppendAllText(_dbgLog, batch.ToString(), new UTF8Encoding(false));
                        }
                    }
                    catch
                    {
                        Interlocked.Add(ref _dbgLogDroppedCount, Math.Max(1, batchCount));
                        break;
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _dbgLogWriterRunning, 0);
                if (!_dbgLogQueue.IsEmpty)
                    StartDiagnosticWriter();
            }
        }

        private static void RotateDiagnosticLogIfNeeded()
        {
            if (!File.Exists(_dbgLog) || new FileInfo(_dbgLog).Length < MaxDiagnosticFileBytes)
                return;

            File.Move(_dbgLog, _dbgLog + ".1", overwrite: true);
        }

        private static void FlushDiagnosticLogQueue()
        {
            var deadline = Environment.TickCount64 + 2000;
            while (!_dbgLogQueue.IsEmpty || Volatile.Read(ref _dbgLogWriterRunning) != 0)
            {
                StartDiagnosticWriter();
                if (Environment.TickCount64 >= deadline)
                    break;
                Thread.Sleep(20);
            }
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
                DbgLog("MainWindow:Loaded", App.IsUiSmokeMode
                    ? "主窗口已加载，开始隔离 UI 冒烟"
                    : "主窗口已加载，启动 UI 空闲后的自动连接", new
                {
                    actualWidth = Math.Round(ActualWidth, 1),
                    actualHeight = Math.Round(ActualHeight, 1),
                    windowState = WindowState.ToString(),
                    uiSmokeMode = App.IsUiSmokeMode
                }, "STARTUP");

                if (App.IsUiSmokeMode)
                    _ = RunUiSmokeAsync();
                else
                    _viewModel.StartAutoConnectAfterUiReady();
            }), DispatcherPriority.ApplicationIdle);
        }

        private async Task RunUiSmokeAsync()
        {
            var total = Stopwatch.StartNew();
            try
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                EnsureSmokeWindowReady(this, "主窗口");
                DbgLog("UI_SMOKE:MainWindow", "主窗口加载与布局通过", new
                {
                    elapsedMs = total.ElapsedMilliseconds
                }, "UI_SMOKE");

                var device = _viewModel.Devices.FirstOrDefault(item => !item.IsPlaceholder)
                    ?? throw new InvalidOperationException("UI 冒烟找不到可用设备");

                await SmokeDeviceDetailCommandAsync(device);
                await SmokeWindowAsync("系统设置", () =>
                    new SettingsDialog(_viewModel.DeviceManager));
                await SmokeWindowAsync("日志查询", () =>
                    new LogQueryWindow(_viewModel.DeviceManager));

                DbgLog("UI_SMOKE:PASS", "主要点击开窗链路全部通过", new
                {
                    elapsedMs = total.ElapsedMilliseconds,
                    windows = new[] { "MainWindow", "DeviceDetailWindow", "SettingsDialog", "LogQueryWindow" },
                    productionConfigTouched = false,
                    plcConnectionAttempted = false
                }, "UI_SMOKE");
                FlushDiagnosticLogQueue();
                Application.Current.Shutdown(0);
            }
            catch (Exception ex)
            {
                DbgLog("UI_SMOKE:FAIL", "UI 冒烟失败", new
                {
                    elapsedMs = total.ElapsedMilliseconds,
                    error = ex.Message,
                    type = ex.GetType().FullName,
                    stack = ex.StackTrace
                }, "UI_SMOKE");
                FlushDiagnosticLogQueue();
                Application.Current.Shutdown(2);
            }
        }

        private async Task SmokeDeviceDetailCommandAsync(Device device)
        {
            var elapsed = Stopwatch.StartNew();
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            // 先排入一个低优先级关闭动作，再执行真实 RelayCommand。ShowDialog 进入嵌套
            // Dispatcher 后，该动作会在窗口完成 Loaded/Render 后验证并安全关闭它。
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                DeviceDetailWindow detailWindow = null;
                try
                {
                    detailWindow = Application.Current.Windows
                        .OfType<DeviceDetailWindow>()
                        .FirstOrDefault(window => window.IsVisible);
                    if (detailWindow == null)
                        throw new InvalidOperationException("设备详情命令未显示详情窗口");

                    detailWindow.UpdateLayout();
                    EnsureSmokeWindowReady(detailWindow, "设备详情");
                    DbgLog("UI_SMOKE:DeviceDetailWindow", "设备详情真实模态命令加载与布局通过", new
                    {
                        elapsedMs = elapsed.ElapsedMilliseconds,
                        actualWidth = Math.Round(detailWindow.ActualWidth, 1),
                        actualHeight = Math.Round(detailWindow.ActualHeight, 1)
                    }, "UI_SMOKE");
                    detailWindow.Close();
                    completion.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    try { detailWindow?.Close(); } catch { }
                    completion.TrySetException(ex);
                }
            }), DispatcherPriority.ApplicationIdle);

            _viewModel.OpenDeviceDetailCommand.Execute(device);
            await completion.Task;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }

        private async Task SmokeWindowAsync(string name, Func<Window> factory)
        {
            Window window = null;
            var elapsed = Stopwatch.StartNew();
            try
            {
                window = factory();
                window.Owner = this;
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                window.ShowInTaskbar = false;
                window.Show();

                await Dispatcher.Yield(DispatcherPriority.Loaded);
                window.UpdateLayout();
                await Task.Delay(150);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                EnsureSmokeWindowReady(window, name);

                DbgLog($"UI_SMOKE:{window.GetType().Name}", $"{name}加载与布局通过", new
                {
                    elapsedMs = elapsed.ElapsedMilliseconds,
                    actualWidth = Math.Round(window.ActualWidth, 1),
                    actualHeight = Math.Round(window.ActualHeight, 1)
                }, "UI_SMOKE");
            }
            finally
            {
                if (window != null)
                {
                    try { window.Close(); }
                    catch (Exception closeError)
                    {
                        DbgLog("UI_SMOKE:Close", $"关闭{name}时出现异常", new
                        {
                            error = closeError.Message
                        }, "UI_SMOKE");
                    }
                }

                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            }
        }

        private static void EnsureSmokeWindowReady(Window window, string name)
        {
            if (!window.IsLoaded || !window.IsVisible || PresentationSource.FromVisual(window) == null)
            {
                throw new InvalidOperationException(
                    $"{name}未完成 WPF 加载/可见/呈现源检查（Loaded={window.IsLoaded}, Visible={window.IsVisible}）");
            }
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
            var elapsed = Stopwatch.StartNew();
            try
            {
                DbgLog("MainWindow:Settings", "开始打开系统设置", new { }, "WINDOW_OPEN");
                var dialog = new SettingsDialog(_viewModel.DeviceManager)
                {
                    Owner = this,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };
                dialog.ShowDialog();
                DbgLog("MainWindow:Settings", "系统设置窗口已关闭", new
                {
                    elapsedMs = elapsed.ElapsedMilliseconds
                }, "WINDOW_OPEN");
            }
            catch (Exception ex)
            {
                DbgLog("MainWindow:Settings", "打开系统设置失败", new
                {
                    elapsedMs = elapsed.ElapsedMilliseconds,
                    error = ex.Message,
                    stack = ex.StackTrace
                }, "WINDOW_OPEN");
                MessageBox.Show($"打开系统设置失败:\n{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LogQuery_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                DbgLog("MainWindow:LogQuery", "开始打开日志查询", new { }, "WINDOW_OPEN");
                var win = new LogQueryWindow(_viewModel.DeviceManager)
                {
                    Owner = this
                };
                win.Show();
                DbgLog("MainWindow:LogQuery", "日志查询窗口已显示", new { }, "WINDOW_OPEN");
            }
            catch (Exception ex)
            {
                DbgLog("MainWindow:LogQuery", "打开日志查询失败", new
                {
                    error = ex.Message,
                    stack = ex.StackTrace
                }, "WINDOW_OPEN");
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
            FlushDiagnosticLogQueue();

            base.OnClosed(e);
        }
    }
}
