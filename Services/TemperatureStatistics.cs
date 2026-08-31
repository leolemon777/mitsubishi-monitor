namespace MitsubishiMonitor.Demo.Services
{
    public sealed class TemperatureStatistics
    {
        public int Count { get; init; }
        public int AbnormalCount { get; init; }
        public float Minimum { get; init; }
        public float Maximum { get; init; }
        public float Average { get; init; }
    }
}
