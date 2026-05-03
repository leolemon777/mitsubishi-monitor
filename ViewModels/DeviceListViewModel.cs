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

        [ObservableProperty]
        private ObservableCollection<Device> _devices = new();

        [ObservableProperty]
        private string _currentTime;

        [ObservableProperty]
        private string _currentDate;

        /// <summary>
        /// 在线设备数量
        /// </summary>
        public int OnlineCount => Devices.Count(d => d.IsOnline);

        /// <summary>
        /// 获取设备管理服务（供详情页使用）
        /// </summary>
        public DeviceManagerService DeviceManager => _deviceManager;

        public DeviceListViewModel()
        {
            // 创建设备管理服务
            _deviceManager = new DeviceManagerService();

            // 从设备管理服务获取设备列表
            foreach (var device in _deviceManager.Devices)
            {
                Devices.Add(device);
            }

            // 订阅设备状态变化事件
            _deviceManager.DeviceStatusChanged += OnDeviceStatusChanged;

            // 启动时钟 - 改为5秒更新一次，减少UI压力
            UpdateTime();
            _timer = new Timer(5000);
            _timer.Elapsed += (s, e) =>
            {
                // 必须在UI线程上更新属性，否则可能导致UI冻结
                App.Current.Dispatcher.BeginInvoke(() =>
                {
                    UpdateTime();
                    OnPropertyChanged(nameof(OnlineCount));
                });
            };
            _timer.AutoReset = true;
            _timer.Start();

            // 不再自动连接设备，由用户手动点击连接按钮
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
            try
            {
                var (successCount, failedReasons) = await _deviceManager.ConnectAllDevicesAsync();
                int total = Devices.Count;
                int failCount = total - successCount;

                // 连接结果仅在全部成功时提示，失败的设备静默跳过
                if (failCount == 0)
                {
                    MessageBox.Show($"已连接全部 {successCount} 台设备。", "连接成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"连接设备失败:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 打开设备详情页
        /// </summary>
        [RelayCommand]
        private void OpenDeviceDetail(Device device)
        {
            try
            {
                if (device == null)
                {
                    MessageBox.Show("设备信息为空", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                var success = await _deviceManager.ConnectDeviceAsync(device.Id);
                if (success)
                {
                    System.Diagnostics.Debug.WriteLine($"[连接] {device.Name} 连接成功");
                }
                else
                {
                    var error = _deviceManager.GetPlcService(device.Id) is MitsubishiPlcService plc
                        ? plc.LastConnectionError
                        : "未知错误";
                    MessageBox.Show($"连接 {device.Name} 失败:\n{error}", "连接失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
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
                System.Diagnostics.Debug.WriteLine($"[断开] {device.Name} 已断开");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"断开设备异常:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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
