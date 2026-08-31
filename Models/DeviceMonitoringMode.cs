namespace MitsubishiMonitor.Demo.Models
{
    /// <summary>
    /// 每台设备的运行策略。现场设备允许独立、随机开关机，因此“未开机”不能一律视为故障。
    /// </summary>
    public enum DeviceMonitoringMode
    {
        /// <summary>默认模式：关机时进入待机并低频探测，开机后自动恢复采集。</summary>
        AutoStandby,

        /// <summary>生产要求该设备保持在线；掉线、数据过期时参与故障与三色灯判断。</summary>
        RequiredOnline,

        /// <summary>停用该设备，不连接、不探测，也不参与三色灯判断。</summary>
        Disabled
    }
}
