using System;

namespace MitsubishiMonitor.Demo.Models
{
    /// <summary>
    /// 连接状态变更的不可变快照。旧的 bool 事件继续保留给兼容实现，
    /// 新代码应使用此快照判断通信状态。
    /// </summary>
    public sealed class PlcConnectionSnapshot
    {
        public PlcConnectionSnapshot(
            long generation,
            PlcConnectionPhase phase,
            string reason,
            int consecutiveFailures,
            DateTimeOffset? lastProtocolSuccessAt,
            DateTimeOffset? lastTemperatureSampleAt)
        {
            Generation = generation;
            Phase = phase;
            Reason = reason ?? "";
            ConsecutiveFailures = Math.Max(0, consecutiveFailures);
            LastProtocolSuccessAt = lastProtocolSuccessAt;
            LastTemperatureSampleAt = lastTemperatureSampleAt;
        }

        public long Generation { get; }
        public PlcConnectionPhase Phase { get; }
        public string Reason { get; }
        public int ConsecutiveFailures { get; }
        public DateTimeOffset? LastProtocolSuccessAt { get; }
        public DateTimeOffset? LastTemperatureSampleAt { get; }
        public bool IsTransportUsable => Phase is PlcConnectionPhase.AwaitingFirstSample
            or PlcConnectionPhase.OnlineFresh;
        public bool IsDataFresh => Phase == PlcConnectionPhase.OnlineFresh;
    }

    public sealed class PlcConnectionChangedEventArgs : EventArgs
    {
        public PlcConnectionChangedEventArgs(PlcConnectionSnapshot snapshot)
        {
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        }

        public PlcConnectionSnapshot Snapshot { get; }
    }
}
