using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MitsubishiMonitor.Demo.Models;
using MitsubishiMonitor.Demo.Services;

namespace MitsubishiMonitor.Demo.ViewModels
{
    /// <summary>
    /// 设备列表页ViewModel（主界面，卡片式布局）
    /// </summary>
    public partial class DeviceListViewModel : ObservableObject, IDisposable
    {
        private bool _disposed;
        private readonly DeviceManagerService _deviceManager;
        private readonly Timer _timer;
        private int _autoConnectStarted;
        private int _connectAllRunning;

        [ObservableProperty]
        private ObservableCollection<Device> _devices = new();

        [ObservableProperty]
        private string _currentTime;

        [ObservableProperty]
        private string _currentDate;

        [ObservableProperty]
        private string _connectionStatusText = "启动中";

        /// <summary>
        /// 在线设备数量
        /// </summary>
        public int OnlineCount => Devices.Count(d => d.IsOnline && !d.IsPlaceholder);

        /// <summary>
        /// 真实设备数量（不含占位符）
        /// </summary>
        public int TotalCount => Devices.Count(d => !d.IsPlaceholder);

        /// <summary>
        /// 获取设备管理服务（供详情页使用）
        /// </summary>
        public DeviceManagerService DeviceManager => _deviceManager;
        public bool IsDemoVideoMode => App.IsDemoVideoMode;
        public Visibility DemoModeVisibility => App.IsDemoVideoMode ? Visibility.Visible : Visibility.Collapsed;

        public DeviceListViewModel()
        {
            // 创建设备管理服务
            _deviceManager = new DeviceManagerService();

            // 从设备管理服务获取设备列表
            foreach (var device in _deviceManager.Devices)
            {
                Devices.Add(device);
            }

            // 增加两个占位卡片
            Devices.Add(new Device { Id = 998, Name = "后续拓展", Location = "预留位", IsPlaceholder = true });
            Devices.Add(new Device { Id = 999, Name = "后续拓展", Location = "预留位", IsPlaceholder = true });

            // 订阅设备状态变化事件
            _deviceManager.DeviceStatusChanged += OnDeviceStatusChanged;

            // 时钟显示完全由 MainWindow 的 DispatcherTimer 负责（直接操作 TextBlock，1s 精度）
            // OnlineCount 已通过 DeviceStatusChanged 事件实时更新，30s 轮询只是保底兜底
            UpdateTime();
            _timer = new Timer(30_000);
            _timer.Elapsed += (s, e) =>
            {
                App.Current.Dispatcher.BeginInvoke(() =>
                {
                    // 30s 保底刷新 OnlineCount（正常情况事件驱动已覆盖）
                    OnPropertyChanged(nameof(OnlineCount));
                });
            };
            _timer.AutoReset = true;
            _timer.Start();

            SetConnectionStatus(App.IsDemoVideoMode
                ? "视频演示模式：独立临时数据，不连接 PLC"
                : AppConfig.IsConfigurationValid
                    ? "主界面初始化完成，等待连接 PLC..."
                    : $"配置故障，已禁止连接 PLC：{AppConfig.ConfigurationError}");
        }

        private void OnDeviceStatusChanged(object sender, DeviceStatusChangeEventArgs e)
        {
            // 设备状态变化时更新界面
            App.Current.Dispatcher.BeginInvoke(() =>
            {
                OnPropertyChanged(nameof(OnlineCount));
            });
        }

        private void UpdateTime()
        {
            var now = DateTime.Now;
            CurrentDate = now.ToString("yyyy年MM月dd日 dddd");
            CurrentTime = now.ToString("HH:mm:ss");
            OnPropertyChanged(nameof(CurrentDate));
            OnPropertyChanged(nameof(CurrentTime));
        }

        private void SetConnectionStatus(string text)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                ConnectionStatusText = text;
                return;
            }

            dispatcher.BeginInvoke(new Action(() => ConnectionStatusText = text));
        }

        public void StartAutoConnectAfterUiReady()
        {
            if (_disposed) return;
            if (System.Threading.Interlocked.Exchange(ref _autoConnectStarted, 1) == 1) return;

            if (!App.IsDemoVideoMode && !AppConfig.IsConfigurationValid)
            {
                SetConnectionStatus($"配置故障，未执行自动连接：{AppConfig.ConfigurationError}");
                return;
            }

            var delayMs = App.IsDemoVideoMode ? 300 : 5000;
            SetConnectionStatus(App.IsDemoVideoMode
                ? "视频演示模式：本机虚拟数据，不连接 PLC"
                : "界面已显示，5 秒后自动探测已启用 PLC...");
            Views.MainWindow.DbgLog("DeviceListVM:AutoConnect", "主界面加载完成，延迟启动自动连接", new
            {
                delayMs,
                demoVideoMode = App.IsDemoVideoMode,
                total = TotalCount
            }, "CONNECT");

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delayMs);
                    if (_disposed) return;
                    await RunConnectAllAsync("AutoConnect", false);
                }
                catch (Exception ex)
                {
                    SetConnectionStatus($"自动连接异常：{ex.Message}");
                    Views.MainWindow.DbgLog("DeviceListVM:AutoConnect", "自动连接任务异常", new
                    {
                        error = ex.Message,
                        stack = ex.StackTrace
                    }, "CONNECT");
                }
            });
        }

        private async Task<bool> RunConnectAllAsync(string source, bool showSuccessDialog)
        {
            if (_disposed) return false;
            if (!App.IsDemoVideoMode && !AppConfig.IsConfigurationValid)
            {
                var message = $"配置未通过校验，禁止连接 PLC：\n{AppConfig.ConfigurationError}";
                SetConnectionStatus(message.Replace("\n", " "));
                if (showSuccessDialog)
                    MessageBox.Show(message, "配置故障", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (System.Threading.Interlocked.Exchange(ref _connectAllRunning, 1) == 1)
            {
                SetConnectionStatus("已有连接任务正在执行，请稍等...");
                Views.MainWindow.DbgLog("DeviceListVM:ConnectAll", "跳过重复连接任务", new
                {
                    source
                }, "CONNECT");
                return false;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                SetConnectionStatus(App.IsDemoVideoMode
                    ? "正在启动本机演示数据..."
                    : source == "AutoConnect" ? "正在后台逐台探测已启用 PLC..." : "正在探测已启用 PLC...");
                Views.MainWindow.DbgLog("DeviceListVM:ConnectAll", "连接任务开始", new
                {
                    source,
                    total = TotalCount
                }, "CONNECT");

                var (successCount, failedReasons) = await _deviceManager.ConnectAllDevicesAsync();
                int total = TotalCount;
                int failCount = failedReasons.Count;
                int standbyCount = Devices.Count(device =>
                    device.MonitoringMode == DeviceMonitoringMode.AutoStandby &&
                    !device.IsOnline);
                int disabledCount = Devices.Count(device =>
                    device.MonitoringMode == DeviceMonitoringMode.Disabled);
                int requiredOfflineCount = Devices.Count(device =>
                    device.MonitoringMode == DeviceMonitoringMode.RequiredOnline &&
                    !device.IsOnline);

                SetConnectionStatus(App.IsDemoVideoMode
                    ? $"视频演示模式：{successCount}/{total} 台演示设备在线（未连接 PLC）"
                    : requiredOfflineCount > 0
                        ? $"探测完成：{successCount} 台在线，{standbyCount} 台待机，{disabledCount} 台停用，{requiredOfflineCount} 台要求在线但未连接"
                        : $"探测完成：{successCount} 台在线，{standbyCount} 台待机，{disabledCount} 台停用");
                Views.MainWindow.DbgLog("DeviceListVM:ConnectAll", "连接任务结束", new
                {
                    source,
                    elapsedMs = sw.ElapsedMilliseconds,
                    successCount,
                    failCount,
                    total,
                    failedReasons = failedReasons.ToArray()
                }, "CONNECT");

                if (showSuccessDialog && failCount == 0)
                {
                    MessageBox.Show(
                        $"探测完成：{successCount} 台在线，{standbyCount} 台待机，{disabledCount} 台停用。",
                        "设备探测完成",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else if (showSuccessDialog && failCount > 0)
                {
                    MessageBox.Show(
                        $"以下设备设置为“要求在线”，但本次未连接：\n{string.Join("\n", failedReasons)}",
                        "要求在线设备异常",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                return true;
            }
            catch (Exception ex)
            {
                SetConnectionStatus($"连接异常：{ex.Message}");
                Views.MainWindow.DbgLog("DeviceListVM:ConnectAll", "连接任务异常", new
                {
                    source,
                    elapsedMs = sw.ElapsedMilliseconds,
                    error = ex.Message,
                    stack = ex.StackTrace
                }, "CONNECT");

                if (showSuccessDialog)
                {
                    MessageBox.Show($"连接设备失败:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                return false;
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _connectAllRunning, 0);
            }
        }

        /// <summary>
        /// 刷新所有设备状态
        /// </summary>
        [RelayCommand]
        private async Task RefreshAllAsync()
        {
            try
            {
                await _deviceManager.RefreshAllDevicesAsync();

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Devices.Clear();
                    foreach (var device in _deviceManager.Devices)
                    {
                        Devices.Add(device);
                    }
                    Devices.Add(new Device { Id = 998, Name = "后续拓展", Location = "预留位", IsPlaceholder = true });
                    Devices.Add(new Device { Id = 999, Name = "后续拓展", Location = "预留位", IsPlaceholder = true });
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"刷新设备状态失败:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 连接所有设备
        /// </summary>
        [RelayCommand]
        private async Task ConnectAllDevicesAsync()
        {
            await RunConnectAllAsync("ManualButton", true);
        }

        /// <summary>
        /// 打开设备详情页
        /// </summary>
        [RelayCommand]
        private void OpenDeviceDetail(Device device)
        {
            try
            {
                if (device == null || device.IsPlaceholder)
                {
                    if (device == null)
                    {
                        MessageBox.Show("设备信息为空", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    return;
                }

                // 打开详情窗口，传入设备和设备管理器
                var detailWindow = new Views.DeviceDetailWindow(device, _deviceManager);

                // 设置Owner为当前活动窗口，确保详情窗口显示在主窗口前面
                detailWindow.Owner = Application.Current.MainWindow;
                detailWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;

                detailWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"打开设备详情失败:\n{ex.Message}\n\n堆栈:\n{ex.StackTrace}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                System.Diagnostics.Debug.WriteLine($"打开设备详情异常: {ex}");
            }
        }

        /// <summary>
        /// 连接单个设备
        /// </summary>
        [RelayCommand]
        private async Task ConnectDeviceAsync(Device device)
        {
            if (device == null) return;

            try
            {
                SetConnectionStatus($"正在连接 {device.Name}...");
                var success = await _deviceManager.ConnectDeviceAsync(device.Id);
                if (success)
                {
                    SetConnectionStatus($"{device.Name} 已连接");
                    System.Diagnostics.Debug.WriteLine($"[连接] {device.Name} 连接成功");
                }
                else
                {
                    var error = _deviceManager.GetPlcService(device.Id) is MitsubishiPlcService plc
                        ? plc.LastConnectionError
                        : "未知错误";
                    if (device.MonitoringMode == DeviceMonitoringMode.AutoStandby)
                    {
                        SetConnectionStatus($"{device.Name} 当前未开机，已进入自动待机");
                    }
                    else
                    {
                        SetConnectionStatus($"{device.Name} 连接失败");
                        MessageBox.Show($"连接 {device.Name} 失败:\n{error}", "连接失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                SetConnectionStatus($"连接异常：{ex.Message}");
                MessageBox.Show($"连接设备异常:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 断开单个设备
        /// </summary>
        [RelayCommand]
        private void DisconnectDevice(Device device)
        {
            if (device == null) return;

            try
            {
                _deviceManager.DisconnectDevice(device.Id);
                SetConnectionStatus($"{device.Name} 已停用，不再后台探测");
                System.Diagnostics.Debug.WriteLine($"[停用] {device.Name} 已停用");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"停用设备异常:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 报警确认/消音：关闭蜂鸣器，活动超温仍保持红灯，直到温度恢复正常。
        /// 仅在 HasActiveAlarm 为 true 时界面上该按钮可见。
        /// </summary>
        [RelayCommand]
        private void AcknowledgeAlarm()
        {
            try
            {
                _deviceManager.AcknowledgeAlarm();
                System.Diagnostics.Debug.WriteLine("[UI] 操作员点击消音/复位");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UI] 消音/复位异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 程序退出时调用：停止时钟定时器、解除事件订阅、关闭所有 PLC、刷新日志缓冲。
        /// 必须在主窗口 OnClosed 中调用，否则 LogBufferService 队列尾部 0–3 秒数据会丢。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_timer != null)
                {
                    _timer.Stop();
                    _timer.Dispose();
                }
            }
            catch { }

            try { _deviceManager.DeviceStatusChanged -= OnDeviceStatusChanged; } catch { }
            try { _deviceManager.StopMonitoring(); } catch { }
        }
    }
}
