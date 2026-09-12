using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using MitsubishiMonitor.Demo.Models;

namespace MitsubishiMonitor.Demo.Services
{
    /// <summary>四条曲线与时间标签共用样本窗口，缺失辅助值用 null 形成断点。</summary>
    internal sealed class TemperatureChartWindow
    {
        private readonly int _capacity;
        private readonly List<TemperatureLog> _samples = new();
        public TemperatureChartWindow(int capacity = 60) { _capacity = capacity; }
        public bool FollowLive { get; set; } = true;
        public ObservableCollection<double?> Temperatures { get; } = new();
        public ObservableCollection<double?> PhaseA { get; } = new();
        public ObservableCollection<double?> PhaseB { get; } = new();
        public ObservableCollection<double?> PhaseC { get; } = new();
        public string[] Labels => _samples.Select(sample => sample.RecordTime.ToString("MM-dd HH:mm:ss")).ToArray();

        public void ReplaceHistory(IEnumerable<TemperatureLog> history, DateTime queryEnd)
        {
            // 查询期间已经收到的新样本必须保留，历史浏览则严格限制在选定范围内。
            var tail = FollowLive ? _samples.Where(sample => sample.RecordTime > queryEnd).ToArray() : Array.Empty<TemperatureLog>();
            var combined = history.Concat(tail).OrderBy(sample => sample.RecordTime)
                .DistinctBy(sample => sample.RecordTime).TakeLast(_capacity).ToArray();
            _samples.Clear();
            _samples.AddRange(combined);
            Publish();
        }

        public void AppendLive(TemperatureLog sample)
        {
            if (!FollowLive) return;
            var index = _samples.FindIndex(previous => previous.RecordTime == sample.RecordTime);
            if (index >= 0) _samples[index] = sample;
            else _samples.Add(sample);
            _samples.Sort((left, right) => left.RecordTime.CompareTo(right.RecordTime));
            if (_samples.Count > _capacity) _samples.RemoveRange(0, _samples.Count - _capacity);
            Publish();
        }

        private void Publish()
        {
            Temperatures.Clear(); PhaseA.Clear(); PhaseB.Clear(); PhaseC.Clear();
            foreach (var sample in _samples)
            {
                Temperatures.Add(sample.Temperature);
                PhaseA.Add(sample.HasUsableAuxiliaryData ? sample.ThermocoupleA : null);
                PhaseB.Add(sample.HasUsableAuxiliaryData ? sample.ThermocoupleB : null);
                PhaseC.Add(sample.HasUsableAuxiliaryData ? sample.ThermocoupleC : null);
            }
        }
    }
}
