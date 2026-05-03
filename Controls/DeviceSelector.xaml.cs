using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using MitsubishiMonitor.Demo.Models;

namespace MitsubishiMonitor.Demo.Controls
{
    /// <summary>
    /// 设备选择器：搜索、分组筛选、快速切换设备
    /// </summary>
    public partial class DeviceSelector : System.Windows.Controls.UserControl
    {
        private ICollectionView _filteredView;

        public DeviceSelector()
        {
            InitializeComponent();
            Groups = new ObservableCollection<string> { "全部设备", "在线设备", "离线设备" };
            SelectedGroup = "全部设备";
        }

        #region 依赖属性

        public static readonly DependencyProperty DevicesProperty =
            DependencyProperty.Register(nameof(Devices), typeof(ObservableCollection<Device>), typeof(DeviceSelector),
                new PropertyMetadata(null, OnDevicesChanged));

        public static readonly DependencyProperty SelectedDeviceProperty =
            DependencyProperty.Register(nameof(SelectedDevice), typeof(Device), typeof(DeviceSelector),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public static readonly DependencyProperty SearchTextProperty =
            DependencyProperty.Register(nameof(SearchText), typeof(string), typeof(DeviceSelector),
                new PropertyMetadata(string.Empty, OnFilterChanged));

        public static readonly DependencyProperty GroupsProperty =
            DependencyProperty.Register(nameof(Groups), typeof(ObservableCollection<string>), typeof(DeviceSelector),
                new PropertyMetadata(null));

        public static readonly DependencyProperty SelectedGroupProperty =
            DependencyProperty.Register(nameof(SelectedGroup), typeof(string), typeof(DeviceSelector),
                new PropertyMetadata("全部设备", OnFilterChanged));

        #endregion

        #region 属性

        public ObservableCollection<Device> Devices { get => (ObservableCollection<Device>)GetValue(DevicesProperty); set => SetValue(DevicesProperty, value); }
        public Device SelectedDevice { get => (Device)GetValue(SelectedDeviceProperty); set => SetValue(SelectedDeviceProperty, value); }
        public string SearchText { get => (string)GetValue(SearchTextProperty); set => SetValue(SearchTextProperty, value); }
        public ObservableCollection<string> Groups { get => (ObservableCollection<string>)GetValue(GroupsProperty); set => SetValue(GroupsProperty, value); }
        public string SelectedGroup { get => (string)GetValue(SelectedGroupProperty); set => SetValue(SelectedGroupProperty, value); }

        #endregion

        private static void OnDevicesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is DeviceSelector selector && e.NewValue is ObservableCollection<Device> devices)
            {
                selector._filteredView = CollectionViewSource.GetDefaultView(devices);
                selector._filteredView.Filter = selector.FilterDevice;
            }
        }

        private static void OnFilterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is DeviceSelector selector)
                selector._filteredView?.Refresh();
        }

        private bool FilterDevice(object item)
        {
            if (item is not Device device) return false;

            var group = SelectedGroup ?? "全部设备";
            if (group == "在线设备" && !device.IsOnline) return false;
            if (group == "离线设备" && device.IsOnline) return false;

            var kw = SearchText?.Trim().ToLower();
            if (!string.IsNullOrEmpty(kw))
                return device.Name.ToLower().Contains(kw) || device.IpAddress.ToLower().Contains(kw);

            return true;
        }
    }
}
