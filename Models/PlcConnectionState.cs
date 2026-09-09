namespace MitsubishiMonitor.Demo.Models
{
    /// <summary>
    /// PLC 会话的真实生命周期。TCP 建立不等于 MC 协议可用，也不等于数据新鲜。
    /// </summary>
    public enum PlcConnectionPhase
    {
        Disconnected,
        TcpConnecting,
        ProtocolVerifying,
        AwaitingFirstSample,
        OnlineFresh,
        CommunicationFault,
        Disposed
    }

    /// <summary>温度样本质量，供 UI、报警和数据库区分“没有样本”和“样本不可信”。</summary>
    public enum TemperatureSampleQuality
    {
        Valid,
        InvalidPayload,
        OutOfRange,
        ExcessiveStep,
        Stale
    }
}
