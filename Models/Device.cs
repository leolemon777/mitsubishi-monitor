using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MitsubishiMonitor.Demo.Models
{
    /// <summary>
    /// 设备模型
    /// </summary>
    public partial class Device : ObservableObject
    {
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(RuntimeState))]
        [NotifyPropertyChangedFor(nameof(StatusDisplay))]
        [NotifyPropertyChangedFor(nameof(StatusColor))]
        [NotifyPropertyChangedFor(nameof(TemperatureDisplay))]
        [NotifyPropertyChangedFor(nameof(IsLiveTemperature))]
        [NotifyPropertyChangedFor(nameof(MonitoringModeDisplay))]
        private DeviceMonitoringMode _monitoringMode = DeviceMonitoringMode.AutoStandby;

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
        [NotifyPropertyChangedFor(nameof(TemperatureDisplay))]
        [NotifyPropertyChangedFor(nameof(IsLiveTemperature))]
        [NotifyPropertyChangedFor(nameof(RuntimeState))]
        private bool _isOnline;

        /// <summary>
        /// 当前温度
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TemperatureDisplay))]
        [NotifyPropertyChangedFor(nameof(IsLiveTemperature))]
        [NotifyPropertyChangedFor(nameof(LastValidTemperatureDisplay))]
        [NotifyPropertyChangedFor(nameof(TemperatureFreshnessDisplay))]
        private float _currentTemperature;

        /// <summary>
        /// 是否已经收到过一轮完整、可信的温度采样。
        /// 不能用温度是否大于 0 判断，因为 0°C 和负温度同样是合法值。
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TemperatureDisplay))]
        [NotifyPropertyChangedFor(nameof(IsLiveTemperature))]
        [NotifyPropertyChangedFor(nameof(StatusDisplay))]
        [NotifyPropertyChangedFor(nameof(StatusColor))]
        [NotifyPropertyChangedFor(nameof(RuntimeState))]
        private bool _hasTemperatureSample;

        /// <summary>
        /// 已超过一个正常刷新宽限窗口，但尚未达到强制断线阈值。
        /// 旧值可以短暂保留用于观察，但必须明确标为滞后，不能伪装成实时值。
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TemperatureDisplay))]
        [NotifyPropertyChangedFor(nameof(IsLiveTemperature))]
        [NotifyPropertyChangedFor(nameof(TemperatureFreshnessDisplay))]
        [NotifyPropertyChangedFor(nameof(StatusDisplay))]
        [NotifyPropertyChangedFor(nameof(StatusColor))]
        [NotifyPropertyChangedFor(nameof(RuntimeState))]
        private bool _isTemperatureStale;

        /// <summary>
        /// 后台正在为该设备建立新连接。
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusDisplay))]
        [NotifyPropertyChangedFor(nameof(StatusColor))]
        [NotifyPropertyChangedFor(nameof(TemperatureDisplay))]
        [NotifyPropertyChangedFor(nameof(IsLiveTemperature))]
        [NotifyPropertyChangedFor(nameof(TemperatureFreshnessDisplay))]
        [NotifyPropertyChangedFor(nameof(RuntimeState))]
        private bool _isReconnecting;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusDisplay))]
        [NotifyPropertyChangedFor(nameof(StatusColor))]
        [NotifyPropertyChangedFor(nameof(TemperatureDisplay))]
        [NotifyPropertyChangedFor(nameof(IsLiveTemperature))]
        [NotifyPropertyChangedFor(nameof(TemperatureFreshnessDisplay))]
        [NotifyPropertyChangedFor(nameof(RuntimeState))]
        private bool _isConnecting;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusDisplay))]
        [NotifyPropertyChangedFor(nameof(StatusColor))]
        [NotifyPropertyChangedFor(nameof(IsLiveTemperature))]
        [NotifyPropertyChangedFor(nameof(RuntimeState))]
        private bool _hasCommunicationFault;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusDisplay))]
        [NotifyPropertyChangedFor(nameof(StatusColor))]
        [NotifyPropertyChangedFor(nameof(TemperatureDisplay))]
        [NotifyPropertyChangedFor(nameof(IsLiveTemperature))]
        [NotifyPropertyChangedFor(nameof(RuntimeState))]
        private bool _isDemoMode;

        /// <summary>
        /// 是否有异常（温度超过阈值）
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusDisplay))]
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
        [NotifyPropertyChangedFor(nameof(TemperatureFreshnessDisplay))]
        private DateTime _lastUpdateTime;

        /// <summary>
        /// 最后一次有效温度样本时间。与 LastUpdateTime 分离，后者可能被其他状态刷新。
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TemperatureDisplay))]
        [NotifyPropertyChangedFor(nameof(LastValidTemperatureDisplay))]
        [NotifyPropertyChangedFor(nameof(TemperatureFreshnessDisplay))]
        private DateTime _lastTemperatureSampleTime;

        [ObservableProperty]
        private long _lastTemperatureSampleSequence;

        [ObservableProperty]
        private long _lastTemperatureConnectionGeneration;

        [ObservableProperty]
        private long _lastTemperatureRawValue;

        [ObservableProperty]
        private TemperatureSampleQuality _temperatureQuality;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusDisplay))]
        [NotifyPropertyChangedFor(nameof(StatusColor))]
        private int _reconnectAttempt;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusDisplay))]
        private DateTime? _nextReconnectAt;

        /// <summary>
        /// 温度显示文本
        /// </summary>
        public bool IsLiveTemperature => HasTemperatureSample &&
            (IsDemoMode || (IsOnline && !IsTemperatureStale && RuntimeState == DeviceRuntimeState.OnlineFresh));

        /// <summary>
        /// 过期数据不能继续以大号实时值显示。最后有效值由单独的次要文本展示。
        /// </summary>
        public string TemperatureDisplay => IsLiveTemperature
            ? $"{CurrentTemperature:F1}°C"
            : "--.-°C";

        public string LastValidTemperatureDisplay => HasTemperatureSample
            ? $"最后有效 {CurrentTemperature:F1}°C"
            : "尚无有效温度";

        public string TemperatureFreshnessDisplay
        {
            get
            {
                if (!HasTemperatureSample)
                    return "无有效样本";

                // 兼容旧会话/旧持久化对象：在没有独立样本时间时，
                // LastUpdateTime 仍可作为“最后已知时间”，但不会让主数值变成实时值。
                var sampleTime = LastTemperatureSampleTime == default
                    ? LastUpdateTime
                    : LastTemperatureSampleTime;
                if (sampleTime == default)
                    return "数据已过期 · 时间未知";

                var age = DateTime.Now - sampleTime;
                if (age < TimeSpan.Zero) age = TimeSpan.Zero;
                var ageText = age.TotalHours >= 1
                    ? $"{age.TotalHours:F1}小时前"
                    : age.TotalMinutes >= 1
                        ? $"{age.TotalMinutes:F0}分钟前"
                        : $"{Math.Max(0, age.TotalSeconds):F0}秒前";
                return IsLiveTemperature ? $"样本 {ageText}" : $"数据已过期 · {ageText}";
            }
        }

        /// <summary>由低频 UI/监控节拍调用，使“几秒前/几分钟前”不会停留在旧文本。</summary>
        public void RefreshTemperatureFreshness()
            => OnPropertyChanged(nameof(TemperatureFreshnessDisplay));

        public DeviceRuntimeState RuntimeState => IsDemoMode
            ? DeviceRuntimeState.Demo
            : MonitoringMode == DeviceMonitoringMode.Disabled
                ? DeviceRuntimeState.Disabled
                : MonitoringMode == DeviceMonitoringMode.AutoStandby && !IsOnline
                    ? DeviceRuntimeState.Standby
            : IsConnecting
                ? DeviceRuntimeState.Connecting
                : IsReconnecting
                    ? DeviceRuntimeState.Reconnecting
                    : IsOnline
                        ? HasTemperatureSample && !IsTemperatureStale
                            ? DeviceRuntimeState.OnlineFresh
                            : DeviceRuntimeState.OnlineStale
                        : HasCommunicationFault
                            ? DeviceRuntimeState.CommunicationFault
                            : DeviceRuntimeState.Disconnected;

        /// <summary>
        /// 状态显示文本
        /// </summary>
        public string StatusDisplay => HasAlert && RuntimeState == DeviceRuntimeState.OnlineFresh
            ? "超温报警"
            : RuntimeState switch
            {
                DeviceRuntimeState.Connecting => "连接中",
                DeviceRuntimeState.Reconnecting => "重连中",
                DeviceRuntimeState.OnlineFresh => "在线 · 数据新鲜",
                DeviceRuntimeState.OnlineStale => "在线 · 数据过期",
                DeviceRuntimeState.CommunicationFault => "通信故障",
                DeviceRuntimeState.Standby => IsConnecting || IsReconnecting
                    ? "待机 · 正在探测"
                    : "待机 · 等待开机",
                DeviceRuntimeState.Disabled => "已停用",
                DeviceRuntimeState.Demo => "演示 · 虚拟数据",
                _ => "未连接"
            };

        public string MonitoringModeDisplay => MonitoringMode switch
        {
            DeviceMonitoringMode.RequiredOnline => "要求在线",
            DeviceMonitoringMode.Disabled => "已停用",
            _ => "自动待机"
        };

        /// <summary>
        /// 状态颜色
        /// </summary>
        public string StatusColor => HasAlert && RuntimeState == DeviceRuntimeState.OnlineFresh
            ? "#F44336"
            : RuntimeState switch
            {
                DeviceRuntimeState.OnlineFresh => "#4CAF50",
                DeviceRuntimeState.Demo => "#00BCD4",
                DeviceRuntimeState.Connecting or
                DeviceRuntimeState.Reconnecting or
                DeviceRuntimeState.OnlineStale => "#F2C94C",
                DeviceRuntimeState.CommunicationFault => "#FF7043",
                DeviceRuntimeState.Standby => "#607D8B",
                DeviceRuntimeState.Disabled => "#455A64",
                _ => "#757575"
            };

        /// <summary>
        /// 是否是占位卡片（后续拓展）
        /// </summary>
        public bool IsPlaceholder { get; set; }
    }
}
