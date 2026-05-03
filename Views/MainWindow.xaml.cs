using System;
using System.IO;
using System.Text.Json;
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

        // #region agent log
        private System.Timers.Timer _heartbeatTimer;
        private static readonly string _dbgLog =
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "debug-f8e8e9.log");
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
                    File.AppendAllText(_dbgLog, entry + "\n");
                }
                catch { }  // 文件写入失败静默忽略，不弹窗、不阻塞任何线程
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

            // #region agent log - 改用后台线程计时器，不占用UI线程
            _heartbeatTimer = new System.Timers.Timer(10000);
            _heartbeatTimer.Elapsed += (s, e) =>
            {
                var mem = GC.GetTotalMemory(false);
                DbgLog("MainWindow:heartbeat", "后台心跳(非UI线程)", new
                {
                    memoryMB = Math.Round(mem / 1048576.0, 1),
                    stateChanges10s = Services.DeviceManagerService.DbgStateChangeCount,
                    time = DateTime.Now.ToString("HH:mm:ss")
                }, "A/C/E");
                System.Threading.Interlocked.Exchange(ref Services.DeviceManagerService.DbgStateChangeCount, 0);
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
        }

        private void UpdateClock(object sender, EventArgs e)
        {
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
            // 弹出设置对话框
            var dialog = new SettingsDialog
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
