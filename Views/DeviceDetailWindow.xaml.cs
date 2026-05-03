using System;
using System.Windows;
using System.Windows.Media;
using LiveCharts;
using LiveCharts.Wpf;
using MitsubishiMonitor.Demo.Models;
using MitsubishiMonitor.Demo.ViewModels;
using MitsubishiMonitor.Demo.Services;

namespace MitsubishiMonitor.Demo.Views
{
    /// <summary>
    /// DeviceDetailWindow.xaml 的交互逻辑
    /// </summary>
    public partial class DeviceDetailWindow : Window
    {
        private DeviceDetailViewModel _viewModel;
        private readonly DeviceManagerService _deviceManager;
        private readonly Device _device;

        public DeviceDetailWindow(Device device, DeviceManagerService deviceManager)
        {
            try
            {
                InitializeComponent();
                _device = device;
                _deviceManager = deviceManager;
                _viewModel = new DeviceDetailViewModel(device, deviceManager);
                DataContext = _viewModel;

                BindChartData();

                this.Closing += DeviceDetailWindow_Closing;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"初始化设备详情窗口失败:\n{ex.Message}\n\n{ex.StackTrace}",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                throw;
            }
        }

        private void BindChartData()
        {
            if (_viewModel.HasVoltage)
            {
                CombinedChart.Series = _viewModel.CombinedSeries;
                if (XAxis != null) XAxis.Labels = _viewModel.TimeLabels;
            }
            else
            {
                TempOnlyChart.Series = _viewModel.CombinedSeries;
                if (XAxisNoVoltage != null) XAxisNoVoltage.Labels = _viewModel.TimeLabels;
            }

            _viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(_viewModel.TimeLabels))
                {
                    if (_viewModel.HasVoltage)
                    {
                        if (XAxis != null) XAxis.Labels = _viewModel.TimeLabels;
                    }
                    else
                    {
                        if (XAxisNoVoltage != null) XAxisNoVoltage.Labels = _viewModel.TimeLabels;
                    }
                }
            };
        }

        private void DeviceDetailWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _viewModel?.Dispose();
            System.Diagnostics.Debug.WriteLine("[DeviceDetailWindow] 窗口关闭，资源已释放");
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
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
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"拖动窗口异常: {ex.Message}");
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

        private void ToggleMaximize()
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// 点击"总操作次数"卡片，打开"日志查询"页并预选当前设备。
        /// （原本是滚动到本页操作日志区域，现已迁移到独立窗口。）
        /// </summary>
        private void OperationCountCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                var win = new LogQueryWindow(_deviceManager, _device?.Id)
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
    }
}
