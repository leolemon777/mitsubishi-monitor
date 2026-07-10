using System;
using System.IO.Ports;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using MitsubishiMonitor.Demo.Services;

namespace MitsubishiMonitor.Demo.Views
{
    /// <summary>
    /// 系统设置对话框（数据库路径 + 三色灯诊断）
    /// </summary>
    public partial class SettingsDialog : Window
    {
        // 当前检测到的三色灯串口名，null 表示未找到
        private string _detectedPortName = null;

        // DeviceManagerService 引用，用于保存设置后立即生效
        private readonly DeviceManagerService _deviceManager;

        public SettingsDialog(DeviceManagerService deviceManager = null)
        {
            InitializeComponent();
            _deviceManager = deviceManager;
            LoadCurrentSettings();
        }

        /// <summary>
        /// 加载当前配置到界面，并预填COM口列表
        /// </summary>
        private void LoadCurrentSettings()
        {
            DbPathTextBox.Text = AppConfig.SavedDatabasePath ?? "";
            CurrentPathText.Text = AppConfig.DatabasePath;
            AutoExportPathTextBox.Text = AppConfig.AutoExportPath ?? "";

            // 窗口打开时自动列出当前系统所有串口，不用先点检测
            RefreshPortList();
        }

        /// <summary>
        /// 刷新手动选口下拉框（列出系统所有串口）
        /// </summary>
        private void RefreshPortList()
        {
            var ports = SerialPort.GetPortNames();
            ManualPortComboBox.Items.Clear();
            if (ports.Length == 0)
            {
                ManualPortComboBox.Items.Add("（未检测到串口，请插好USB后点[检测]）");
                ManualPortComboBox.SelectedIndex = 0;
                ManualTestButton.IsEnabled = false;
            }
            else
            {
                foreach (var p in ports)
                    ManualPortComboBox.Items.Add(p);
                ManualPortComboBox.SelectedIndex = 0;
                ManualTestButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// 浏览按钮：弹出文件保存对话框选择数据库文件位置
        /// </summary>
        private void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "选择数据库保存位置",
                Filter = "SQLite数据库|*.db|所有文件|*.*",
                FileName = "Monitor.db",
                DefaultExt = ".db"
            };

            var currentPath = DbPathTextBox.Text.Trim();
            if (!string.IsNullOrEmpty(currentPath))
            {
                try
                {
                    dialog.InitialDirectory = System.IO.Path.GetDirectoryName(currentPath);
                }
                catch { /* 路径非法时忽略 */ }
            }

            if (dialog.ShowDialog() == true)
            {
                DbPathTextBox.Text = dialog.FileName;
            }
        }

        /// <summary>
        /// 自动导出文件夹浏览按钮：弹出文件夹选择对话框
        /// </summary>
        private void BrowseAutoExportFolder_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择自动导出 HTML 的目标文件夹",
                ShowNewFolderButton = true
            };

            var currentPath = AutoExportPathTextBox.Text.Trim();
            if (!string.IsNullOrEmpty(currentPath) && System.IO.Directory.Exists(currentPath))
                dialog.SelectedPath = currentPath;

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                AutoExportPathTextBox.Text = dialog.SelectedPath;
        }

        /// <summary>
        /// 保存按钮：将数据库路径和自动导出路径写入 config.json
        /// </summary>
        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var newPath = DbPathTextBox.Text.Trim();

            if (!string.IsNullOrEmpty(newPath))
            {
                try
                {
                    var fullPath = System.IO.Path.GetFullPath(newPath);
                    if (!fullPath.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                    {
                        MessageBox.Show("路径必须以 .db 结尾，例如：E:\\MonitorData\\Monitor.db",
                            "格式错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
                catch
                {
                    MessageBox.Show("路径格式不正确，请重新输入。\n示例：E:\\MonitorData\\Monitor.db",
                        "路径无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            AppConfig.SaveDatabasePath(newPath);

            // 保存自动导出路径，并立即生效
            var autoExportPath = AutoExportPathTextBox.Text.Trim();
            AppConfig.SaveAutoExportPath(autoExportPath);
            _deviceManager?.UpdateAutoExportPath(autoExportPath);

            var dbMsg = string.IsNullOrEmpty(newPath)
                ? "数据库路径：已恢复默认（重启生效）"
                : $"数据库路径：{newPath}（重启生效）";
            var exportMsg = string.IsNullOrEmpty(autoExportPath)
                ? "自动导出：已关闭"
                : $"自动导出文件夹：{autoExportPath}（已即刻生效）";

            MessageBox.Show(dbMsg + "\n" + exportMsg,
                "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);

            DialogResult = true;
            Close();
        }

        /// <summary>
        /// 判断指定串口是否正被主程序的三色灯连接占用。
        /// 串口是独占资源，被占用时设置页不能再开第二个实例，点灯测试需经主程序连接转发。
        /// </summary>
        private bool IsPortHeldByMainService(string portName)
        {
            return _deviceManager != null
                && _deviceManager.IsTowerLightSerialOpen
                && string.Equals(_deviceManager.TowerLightPortName, portName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 检测按钮：扫描系统串口，自动识别 CH340 三色灯
        /// </summary>
        private async void ScanTowerLight_Click(object sender, RoutedEventArgs e)
        {
            ScanTowerLightButton.IsEnabled = false;
            TowerLightDot.Fill = new SolidColorBrush(Color.FromRgb(0x3A, 0x4B, 0x5C));
            TowerLightStatusText.Text = "正在扫描 USB 串口...";
            TowerLightStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x6B, 0x7C));
            TestTowerLightButton.IsEnabled = false;

            string foundPort = null;
            string[] allPorts = Array.Empty<string>();
            string errorMsg = "";

            (foundPort, allPorts, errorMsg) = await Task.Run(() =>
            {
                string found = null;
                string err = "";
                string[] all = TowerLightService.GetAvailablePortNames();

                try
                {
                    found = TowerLightService.FindLikelyPortName();
                }
                catch (Exception ex)
                {
                    err = ex.Message;
                }
                return (found, all, err);
            });

            // 显示所有COM口列表
            AllPortsText.Text = allPorts.Length == 0
                ? "当前系统 COM 口：（无）"
                : "当前系统 COM 口：" + string.Join("  ", allPorts);

            // 刷新手动选口下拉框
            RefreshPortList();

            if (!string.IsNullOrEmpty(foundPort))
            {
                // 主程序已占用该串口时不再新开实例验证：串口独占，二次打开必然失败并误报
                if (IsPortHeldByMainService(foundPort))
                {
                    _detectedPortName = foundPort;
                    TowerLightDot.Fill = new SolidColorBrush(Color.FromRgb(0x00, 0xCC, 0x66));
                    TowerLightStatusText.Text = $"已识别：{foundPort}（CH340，主程序连接使用中）";
                    TowerLightStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xCC, 0x66));
                    TestTowerLightButton.IsEnabled = true;
                    ScanTowerLightButton.IsEnabled = true;
                    return;
                }

                // 找到CH340串口，尝试打开验证
                var (connected, connectErr) = await Task.Run(() =>
                {
                    try
                    {
                        var svc = new TowerLightService(foundPort);
                        bool ok = svc.TryConnect();
                        string cerr = ok ? "" : svc.LastError;
                        svc.Dispose();
                        return (ok, cerr);
                    }
                    catch (Exception ex)
                    {
                        return (false, ex.Message);
                    }
                });

                if (connected)
                {
                    _detectedPortName = foundPort;
                    TowerLightDot.Fill = new SolidColorBrush(Color.FromRgb(0x00, 0xCC, 0x66));
                    TowerLightStatusText.Text = $"已识别：{foundPort}（CH340，连接正常）";
                    TowerLightStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xCC, 0x66));
                    TestTowerLightButton.IsEnabled = true;
                }
                else
                {
                    TowerLightDot.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x88, 0x00));
                    TowerLightStatusText.Text = $"识别到 {foundPort}，但打开失败：{connectErr}";
                    TowerLightStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x88, 0x00));
                }
            }
            else
            {
                // 未找到串口时，执行深度诊断（检查是否有未装驱动的 CH340 USB 设备）
                var diagMsg = await Task.Run(() => TowerLightService.DiagnoseNoPortFound());

                TowerLightDot.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x44));
                TowerLightStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x44));

                // 判断是否为驱动缺失问题
                bool isDriverIssue = diagMsg.Contains("CH340") && diagMsg.Contains("驱动");
                if (isDriverIssue)
                {
                    TowerLightDot.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x88, 0x00));
                    TowerLightStatusText.Text = "检测到硬件已插入，但缺少 CH340 驱动";
                    TowerLightStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x88, 0x00));
                    // 显示诊断详情
                    AllPortsText.Text = diagMsg;
                    AllPortsText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x88, 0x00));
                    InstallDriverButton.Visibility = Visibility.Visible;
                }
                else
                {
                    TowerLightStatusText.Text = string.IsNullOrEmpty(errorMsg)
                        ? "未检测到 USB 三色灯"
                        : errorMsg;
                    AllPortsText.Text = diagMsg;
                }
            }

            ScanTowerLightButton.IsEnabled = true;
        }

        /// <summary>
        /// 点灯测试按钮：红→黄→绿→全灭
        /// </summary>
        private async void TestTowerLight_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_detectedPortName)) return;

            TestTowerLightButton.IsEnabled = false;
            TowerLightStatusText.Text = $"测试中：{_detectedPortName}...";

            string result;
            if (IsPortHeldByMainService(_detectedPortName))
            {
                // 串口被主程序占用：复用主程序连接做点灯测试，结束后自动恢复真实灯态
                var err = await _deviceManager.TestTowerLightAsync();
                result = err == null ? "测试完成：红→黄→绿→灭（经主程序连接）" : $"测试异常：{err}";
            }
            else
            {
                result = await Task.Run(() =>
                {
                    try
                    {
                        using var svc = new TowerLightService(_detectedPortName);
                        if (!svc.TryConnect()) return $"打开失败：{svc.LastError}";
                        svc.Send("Red");
                        System.Threading.Thread.Sleep(800);
                        svc.Send("Yellow");
                        System.Threading.Thread.Sleep(800);
                        svc.Send("Green");
                        System.Threading.Thread.Sleep(800);
                        svc.Send("Off");
                        return "测试完成：红→黄→绿→灭";
                    }
                    catch (Exception ex)
                    {
                        return $"测试异常：{ex.Message}";
                    }
                });
            }

            TowerLightStatusText.Text = $"{_detectedPortName} — {result}";
            TestTowerLightButton.IsEnabled = true;
        }

        /// <summary>
        /// 手动测试按钮：对用户手动选择的 COM 口执行点灯测试
        /// </summary>
        private async void ManualTest_Click(object sender, RoutedEventArgs e)
        {
            var selectedPort = ManualPortComboBox.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(selectedPort) || selectedPort.StartsWith("（"))
            {
                MessageBox.Show("请先从下拉框选择一个 COM 口。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ManualTestButton.IsEnabled = false;
            TowerLightStatusText.Text = $"手动测试 {selectedPort}...";
            TowerLightStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xC0, 0x40));

            string result;
            if (IsPortHeldByMainService(selectedPort))
            {
                // 串口被主程序占用：复用主程序连接做点灯测试，结束后自动恢复真实灯态
                var err = await _deviceManager.TestTowerLightAsync();
                result = err == null
                    ? $"{selectedPort} 测试成功：红→黄→绿→灭（经主程序连接）"
                    : $"{selectedPort} 测试异常：{err}";
            }
            else
            {
                result = await Task.Run(() =>
                {
                    try
                    {
                        using var svc = new TowerLightService(selectedPort);
                        if (!svc.TryConnect()) return $"无法打开 {selectedPort}：{svc.LastError}";
                        svc.Send("Red");
                        System.Threading.Thread.Sleep(600);
                        svc.Send("Yellow");
                        System.Threading.Thread.Sleep(600);
                        svc.Send("Green");
                        System.Threading.Thread.Sleep(600);
                        svc.Send("Off");
                        return $"{selectedPort} 测试成功：红→黄→绿→灭";
                    }
                    catch (Exception ex)
                    {
                        return $"{selectedPort} 测试异常：{ex.Message}";
                    }
                });
            }

            bool success = result.Contains("成功");
            if (success)
            {
                _detectedPortName = selectedPort;
                TowerLightDot.Fill = new SolidColorBrush(Color.FromRgb(0x00, 0xCC, 0x66));
                TowerLightStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xCC, 0x66));
                TestTowerLightButton.IsEnabled = true;
            }
            else
            {
                TowerLightDot.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x44));
                TowerLightStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x44));
            }
            TowerLightStatusText.Text = result;
            ManualTestButton.IsEnabled = true;
        }

        /// <summary>
        /// 安装驱动按钮：尝试启动程序目录下的 CH340 驱动安装程序
        /// </summary>
        private void InstallDriver_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 在程序所在目录的 Drivers 子目录下查找驱动安装包
                var exeDir = System.IO.Path.GetDirectoryName(
                    System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
                var driverPath = System.IO.Path.Combine(exeDir, "Drivers", "CH341SER.EXE");

                if (System.IO.File.Exists(driverPath))
                {
                    // 以管理员权限运行驱动安装程序
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = driverPath,
                        UseShellExecute = true,
                        Verb = "runas"  // 请求管理员权限
                    };
                    System.Diagnostics.Process.Start(psi);
                    TestResultText.Text = "驱动安装程序已启动，安装完成后请重新点击【检测】";
                    TestResultText.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xCC, 0x66));
                }
                else
                {
                    // 驱动文件不存在，提示用户手动获取
                    var driversDir = System.IO.Path.Combine(exeDir, "Drivers");
                    MessageBox.Show(
                        $"未找到驱动文件：\n{driverPath}\n\n" +
                        "请从以下途径获取 CH340 驱动：\n" +
                        "1. 三色灯随附的驱动光盘/U盘\n" +
                        "2. 官网下载：https://www.wch.cn/downloads/CH341SER_EXE.html\n\n" +
                        $"下载后请将 CH341SER.EXE 放入：\n{driversDir}",
                        "驱动文件缺失", MessageBoxButton.OK, MessageBoxImage.Information);

                    // 尝试打开 Drivers 目录（如果不存在则创建）
                    if (!System.IO.Directory.Exists(driversDir))
                        System.IO.Directory.CreateDirectory(driversDir);
                    System.Diagnostics.Process.Start("explorer.exe", driversDir);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"启动驱动安装程序失败：{ex.Message}",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 打开诊断日志所在文件夹（用 Explorer 直接选中今天的日志文件）。
        /// 工控机上现场卡死时，运维点这个就能拿到日志，不用让人手敲路径。
        /// </summary>
        private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var logPath = MainWindow.DiagnosticLogPath;
                var logDir = System.IO.Path.GetDirectoryName(logPath);

                if (string.IsNullOrEmpty(logDir))
                {
                    MessageBox.Show("无法解析日志目录路径", "提示",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 目录可能还没建（极少见，但保险起见兜一下）
                if (!System.IO.Directory.Exists(logDir))
                {
                    System.IO.Directory.CreateDirectory(logDir);
                }

                // 优先 explorer /select, 直接定位到今天那条日志；文件不存在时退化为打开目录
                if (System.IO.File.Exists(logPath))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{logPath}\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = logDir,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"打开日志文件夹失败：{ex.Message}\n\n日志路径：{MainWindow.DiagnosticLogPath}",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { DragMove(); } catch { }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
