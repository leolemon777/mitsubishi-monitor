using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MitsubishiMonitor.Demo.Models
{
    /// <summary>
    /// 设备模型
    /// </summary>
    public partial class Device : ObservableObject
    {
        /// <summary>
        /// 设备ID
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// 设备名称
        /// </summary>
        public string Name { get; set; } = "设备1";

        /// <summary>
        /// 设备位置/描述
        /// </summary>
        public string Location { get; set; } = "滤芯车间一楼";

        /// <summary>
        /// PLC IP地址
        /// </summary>
        public string IpAddress { get; set; } = "192.168.0.10";

        /// <summary>
        /// PLC端口
        /// </summary>
        public int Port { get; set; } = 5000;

        /// <summary>
        /// 是否在线
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusDisplay))]
        [NotifyPropertyChangedFor(nameof(StatusColor))]
        private bool _isOnline;

        /// <summary>
        /// 当前温度
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TemperatureDisplay))]
        private float _currentTemperature;

        /// <summary>
        /// 是否已经收到过一轮完整、可信的温度采样。
        /// 不能用温度是否大于 0 判断，因为 0°C 和负温度同样是合法值。
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TemperatureDisplay))]
        private bool _hasTemperatureSample;

        /// <summary>
        /// 是否有异常（温度超过阈值）
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusColor))]
        private bool _hasAlert;

        /// <summary>
        /// 今日操作次数
        /// </summary>
        [ObservableProperty]
        private int _todayOperationCount;

        /// <summary>
        /// 最后更新时间
        /// </summary>
        [ObservableProperty]
        private DateTime _lastUpdateTime;

        /// <summary>
        /// 温度显示文本
        /// </summary>
        public string TemperatureDisplay => HasTemperatureSample ? $"{CurrentTemperature:F1}°C" : "--.-°C";

        /// <summary>
        /// 状态显示文本
        /// </summary>
        public string StatusDisplay => IsOnline ? "在线" : "离线";

        /// <summary>
        /// 状态颜色
        /// </summary>
        public string StatusColor => HasAlert ? "#F44336" : (IsOnline ? "#4CAF50" : "#757575");

        /// <summary>
        /// 是否是占位卡片（后续拓展）
        /// </summary>
        public bool IsPlaceholder { get; set; }
    }
}
