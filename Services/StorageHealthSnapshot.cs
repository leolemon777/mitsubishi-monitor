using System;

namespace MitsubishiMonitor.Demo.Services
{
    public sealed class StorageHealthSnapshot : EventArgs
    {
        public bool IsReady { get; init; }
        public bool IsHealthy { get; init; }
        public string Message { get; init; } = "";
        public int PendingCount { get; init; }
        public long SpoolCount { get; init; }
        public long DroppedCount { get; init; }
        public long DeadLetterCount { get; init; }
        public DateTime? LastSuccessfulWriteTime { get; init; }
    }
}
