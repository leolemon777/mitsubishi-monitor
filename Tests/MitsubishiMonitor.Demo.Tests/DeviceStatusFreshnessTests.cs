using System;
using System.Threading.Tasks;
using MitsubishiMonitor.Demo.Models;
using MitsubishiMonitor.Demo.Services;
using Xunit;

namespace MitsubishiMonitor.Demo.Tests
{
    public class DeviceStatusFreshnessTests
    {
        [Fact]
        public void AutoStandbyAndDisabled_HideHistoricalTemperature_AndUseDistinctStatus()
        {
            var device = new Device
            {
                MonitoringMode = DeviceMonitoringMode.AutoStandby,
                IsOnline = false,
                HasTemperatureSample = true,
                CurrentTemperature = 26.5f,
                IsTemperatureStale = true
            };

            Assert.Equal(DeviceRuntimeState.Standby, device.RuntimeState);
            Assert.Equal("待机 · 等待开机", device.StatusDisplay);
            Assert.Equal("--.-°C", device.TemperatureDisplay);

            device.IsReconnecting = true;
            Assert.Equal("待机 · 正在探测", device.StatusDisplay);

            device.MonitoringMode = DeviceMonitoringMode.Disabled;
            Assert.Equal(DeviceRuntimeState.Disabled, device.RuntimeState);
            Assert.Equal("已停用", device.StatusDisplay);
            Assert.Equal("--.-°C", device.TemperatureDisplay);
        }

        [Fact]
        public async Task ManualRefresh_DoesNotPresentPreviousGenerationTemperatureAsFresh()
        {
            var oldSampleTime = DateTime.Now.AddMinutes(-2);
            var device = new Device
            {
                IsOnline = false,
                HasTemperatureSample = true,
                CurrentTemperature = 25f,
                LastUpdateTime = oldSampleTime
            };
            var status = new PlcStatus(1, 1, 1)
            {
                IsConnected = true,
                Temperature = 50f,
                LastTemperatureSampleTime = default
            };
            var wrapper = new DevicePlcWrapper(device, new StubPlcService(status));

            await wrapper.UpdateStatusAsync();

            Assert.True(device.IsOnline);
            Assert.True(device.IsTemperatureStale);
            Assert.Equal(25f, device.CurrentTemperature);
            Assert.Equal(oldSampleTime, device.LastUpdateTime);
            Assert.Equal("--.-°C", device.TemperatureDisplay);
            Assert.Equal("最后有效 25.0°C", device.LastValidTemperatureDisplay);
            Assert.Contains("数据已过期", device.TemperatureFreshnessDisplay);
        }

        [Fact]
        public async Task ManualRefresh_UsesOnlyRealTemperatureSampleTimestamp()
        {
            var sampleTime = DateTime.Now.AddSeconds(-1);
            var device = new Device();
            var status = new PlcStatus(1, 1, 1)
            {
                IsConnected = true,
                Temperature = 50f,
                LastTemperatureSampleTime = sampleTime
            };
            var wrapper = new DevicePlcWrapper(device, new StubPlcService(status));

            await wrapper.UpdateStatusAsync();

            Assert.True(device.HasTemperatureSample);
            Assert.Equal(50f, device.CurrentTemperature);
            Assert.Equal(sampleTime, device.LastUpdateTime);
        }

        private sealed class StubPlcService : IPlcService
        {
            public StubPlcService(PlcStatus status)
            {
                CurrentStatus = status;
            }

            public PlcStatus CurrentStatus { get; }
            public PlcConfig Config { get; } = new();
            public bool IsAcquiring => false;
            public event EventHandler<bool> ConnectionStateChanged
            {
                add { }
                remove { }
            }
            public event EventHandler<PlcConnectionChangedEventArgs> ConnectionStateChangedDetailed
            {
                add { }
                remove { }
            }
            public PlcConnectionSnapshot ConnectionSnapshot { get; } =
                new PlcConnectionSnapshot(0, PlcConnectionPhase.Disconnected, "测试", 0, null, null, null);
            public event EventHandler<StateChangeEvent> StateChanged
            {
                add { }
                remove { }
            }
            public Task<bool> ConnectAsync() => Task.FromResult(true);
            public void Disconnect() { }
            public Task<bool[]> ReadXPointsAsync() => Task.FromResult(Array.Empty<bool>());
            public Task<bool[]> ReadYPointsAsync() => Task.FromResult(Array.Empty<bool>());
            public Task<float> ReadTemperatureAsync() => Task.FromResult(float.NaN);
            public Task<PlcStatus> ReadAllAsync() => Task.FromResult(CurrentStatus);
            public void StartAcquisition() { }
            public void StopAcquisition() { }
        }
    }
}
