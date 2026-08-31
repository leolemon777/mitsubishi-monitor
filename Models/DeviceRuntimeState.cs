namespace MitsubishiMonitor.Demo.Models
{
    /// <summary>操作员可见的设备运行状态，避免把 TCP 在线等同于数据可信。</summary>
    public enum DeviceRuntimeState
    {
        Disconnected,
        Standby,
        Disabled,
        Connecting,
        Reconnecting,
        OnlineFresh,
        OnlineStale,
        CommunicationFault,
        Demo
    }
}
