using System;
using System.Windows;
using System.Windows.Input;
using MitsubishiMonitor.Demo.Services;
using MitsubishiMonitor.Demo.ViewModels;

namespace MitsubishiMonitor.Demo.Views
{
    public partial class LogQueryWindow : Window
    {
        private readonly LogQueryViewModel _viewModel;

        public LogQueryWindow(DeviceManagerService deviceManager, int? defaultDeviceId = null)
        {
            InitializeComponent();
            _viewModel = new LogQueryViewModel(deviceManager);

            // 如果带了默认设备 id（例如从详情页"今日操作次数"卡片打开），定位到该设备
            if (defaultDeviceId.HasValue)
            {
                foreach (var item in _viewModel.DeviceOptions)
                {
                    if (item.DeviceId == defaultDeviceId.Value)
                    {
                        _viewModel.SelectedDevice = item;
                        break;
                    }
                }
            }

            DataContext = _viewModel;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized
                    ? WindowState.Normal : WindowState.Maximized;
            }
            else
            {
                try { DragMove(); } catch { }
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void Maximize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal : WindowState.Maximized;

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosed(EventArgs e)
        {
            try { _viewModel?.Dispose(); } catch { }
            base.OnClosed(e);
        }
    }
}
