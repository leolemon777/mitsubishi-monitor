using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MitsubishiMonitor.Demo.Data;
using MitsubishiMonitor.Demo.Models;
using MitsubishiMonitor.Demo.Services;
using Xunit;

namespace MitsubishiMonitor.Demo.Tests
{
    public class ReviewReliabilityRegressionTests
    {
        [Fact]
        public async Task ShutdownWithoutDatabase_PersistsWholeQueue_AndReplaysAfterRestart()
        {
            using var workspace = new ReviewWorkspace();
            var path = Path.Combine(workspace.Path, "monitor.db");
            var buffer = new LogBufferService(path, startTimer: false) { MaxBatchSize = 2 };
            for (var i = 0; i < 7; i++) buffer.EnqueueOperationLog(new OperationLog { PointAddress = "X" + i });
            buffer.EnqueueTemperatureLog(new TemperatureLog { Temperature = 42 });
            buffer.SetDatabaseUnavailable("initialization failed");
            buffer.Dispose();
            buffer.Dispose();
            Assert.Equal(0, buffer.PendingCount);
            Assert.Equal(8, new DurableLogSpool(path).PendingCount);
            Assert.Equal(0, buffer.DroppedCount);

            using var data = new DataService(path);
            await data.InitializeAsync();
            using var restarted = new LogBufferService(path, startTimer: false);
            restarted.SetDatabaseReady();
            Assert.True(await restarted.FlushOnceAsync());
            Assert.Equal(0, restarted.SpoolCount);
            using var context = new MonitorDbContext(path);
            Assert.Equal(7, await context.OperationLogs.CountAsync());
            Assert.Equal(1, await context.TemperatureLogs.CountAsync());
        }

        [Fact]
        public async Task ShutdownDuringBlockedBatch_PreservesInFlightAndQueuedRecords()
        {
            using var workspace = new ReviewWorkspace();
            var path = Path.Combine(workspace.Path, "blocked.db");
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var buffer = new LogBufferService(path, false, async _ =>
            {
                entered.TrySetResult(true);
                await release.Task;
                throw new IOException("blocked writer failed");
            }, TimeSpan.FromMilliseconds(100));
            buffer.SetDatabaseReady();
            buffer.EnqueueOperationLog(new OperationLog { PointAddress = "X0" });
            var flush = buffer.FlushOnceAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            buffer.EnqueueTemperatureLog(new TemperatureLog { Temperature = 65 });
            try
            {
                var watch = Stopwatch.StartNew();
                buffer.Dispose();
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
                var recovered = new DurableLogSpool(path);
                Assert.Single(recovered.PeekOperations(10));
                Assert.Single(recovered.PeekTemperatures(10));
            }
            finally { release.TrySetResult(true); }
            Assert.False(await flush);
            Assert.Equal(2, new DurableLogSpool(path).PendingCount);
        }

        [Fact]
        public void LateEventAfterDispose_IsPersistedInsteadOfLeftInMemory()
        {
            using var workspace = new ReviewWorkspace();
            var path = Path.Combine(workspace.Path, "late.db");
            var buffer = new LogBufferService(path, false);
            buffer.Dispose();
            buffer.EnqueueOperationLog(new OperationLog { PointAddress = "X0" });
            Assert.Equal(0, buffer.PendingCount);
            Assert.Single(new DurableLogSpool(path).PeekOperations(10));
        }

        [Fact]
        public async Task InvalidTemperature_IsIsolatedWithoutBlockingValidBatch()
        {
            using var workspace = new ReviewWorkspace();
            var path = Path.Combine(workspace.Path, "invalid.db");
            using var data = new DataService(path);
            await data.InitializeAsync();
            using var buffer = new LogBufferService(path, false);
            buffer.SetDatabaseReady();
            buffer.EnqueueTemperatureLog(new TemperatureLog { Temperature = float.NaN });
            buffer.EnqueueTemperatureLog(new TemperatureLog { Temperature = 52 });
            Assert.True(await buffer.FlushOnceAsync());
            Assert.Equal(1, buffer.DeadLetterCount);
            Assert.Equal(0, buffer.SpoolCount);
            using var context = new MonitorDbContext(path);
            Assert.Equal(52f, (await context.TemperatureLogs.SingleAsync()).Temperature);
        }

        [Fact]
        public async Task DatabaseStartupFailure_RetriesAndEnablesBufferedWrites()
        {
            using var workspace = new ReviewWorkspace();
            var path = Path.Combine(workspace.Path, "recovery.db");
            using var data = new DataService(path);
            using var buffer = new LogBufferService(path, false);
            buffer.EnqueueOperationLog(new OperationLog { PointAddress = "Y0" });
            var attempts = 0;
            var failures = 0;
            await DatabaseStartupRecovery.RunAsync(async token =>
            {
                if (++attempts == 1) throw new IOException("transient initialization failure");
                await data.InitializeAsync(token);
            }, buffer.SetDatabaseReady, ex =>
            {
                failures++;
                buffer.SetDatabaseUnavailable(ex.Message);
            }, CancellationToken.None, _ => TimeSpan.Zero);
            Assert.Equal(2, attempts);
            Assert.Equal(1, failures);
            Assert.True(buffer.IsDbReady);
            Assert.True(await buffer.FlushOnceAsync());
            using var context = new MonitorDbContext(path);
            Assert.Single(await context.OperationLogs.ToListAsync());
        }

        [Fact]
        public async Task DatabaseStartupRetry_IsCancelledOnShutdown()
        {
            using var cancellation = new CancellationTokenSource();
            var failed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var ready = false;
            var task = DatabaseStartupRecovery.RunAsync(_ => Task.FromException(new IOException("offline")),
                () => ready = true, _ => failed.TrySetResult(true), cancellation.Token, _ => TimeSpan.FromMinutes(1));
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.False(ready);
        }

        [Theory]
        [InlineData("Green")]
        [InlineData("Yellow")]
        [InlineData("RedBuzzerOn")]
        [InlineData("RedBuzzerOff")]
        public async Task TowerTest_RestoresSameDesiredStateAfterDirectCommands(string desired)
        {
            var writes = new ConcurrentQueue<string>();
            using var light = new TowerLightService(bytes =>
            {
                writes.Enqueue(Convert.ToHexString(bytes));
                return Task.FromResult(true);
            });
            light.QueueDesiredState(desired);
            await WaitUntilAsync(() => light.AppliedState == desired);
            var expected = writes.ToArray();
            foreach (var command in new[] { "Red", "Yellow", "Green", "Off" })
                Assert.True(await light.SendAsync(command));
            Assert.Equal("", light.AppliedState);
            var count = writes.Count;
            light.QueueDesiredState(desired);
            await WaitUntilAsync(() => light.AppliedState == desired);
            Assert.Equal(expected, writes.Skip(count).ToArray());
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task BatchConnect_DoesNotConnectDeviceDisabledWhileWaiting(bool throughSettings)
        {
            var first = new ReviewPlcService(blockConnect: true);
            var second = new ReviewPlcService();
            var manager = new DeviceManagerService(new[] { Wrapper(1, first), Wrapper(2, second) });
            try
            {
                var batch = manager.ConnectAllDevicesAsync();
                await first.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                if (throughSettings)
                    manager.ApplyMonitoringModes(new[] { DeviceMonitoringMode.AutoStandby, DeviceMonitoringMode.Disabled });
                else manager.DisconnectDevice(2);
                first.ReleaseConnect();
                await batch.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Equal(0, second.ConnectCalls);
                Assert.Equal(0, second.AcquisitionStarts);
                Assert.False(second.IsAcquiring);
                Assert.Equal(DeviceRuntimeState.Disabled, manager.GetDevice(2).RuntimeState);
            }
            finally { first.ReleaseConnect(); manager.StopMonitoring(); }
        }

        [Fact]
        public async Task DisabledDuringPendingConnect_RejectsLatePositiveResult()
        {
            var plc = new ReviewPlcService(blockConnect: true);
            var manager = new DeviceManagerService(new[] { Wrapper(1, plc) });
            try
            {
                var connect = manager.ConnectDeviceAsync(1);
                await plc.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                manager.DisconnectDevice(1);
                plc.ReleaseConnect();
                Assert.False(await connect.WaitAsync(TimeSpan.FromSeconds(2)));
                Assert.Equal(0, plc.AcquisitionStarts);
                Assert.False(plc.CurrentStatus.IsConnected);
            }
            finally { plc.ReleaseConnect(); manager.StopMonitoring(); }
        }

        [Theory]
        [InlineData(true, false, 70f)]
        [InlineData(false, true, 80f)]
        [InlineData(false, false, 90f)]
        public void ConfigSave_KeepsOnlySemanticallyValidBackup(bool validPrimary, bool validBackup, float expectedThreshold)
        {
            using var workspace = new ReviewWorkspace();
            var path = Path.Combine(workspace.Path, "config.json");
            File.WriteAllText(path, validPrimary ? SerializeConfig(70) : "{broken-primary");
            File.WriteAllText(path + ".last-known-good", validBackup ? SerializeConfig(80) : "{broken-backup");
            AppConfig.WriteDocumentAtomic(CreateConfig(90), path);
            var options = new JsonSerializerOptions();
            options.Converters.Add(new JsonStringEnumConverter());
            var backup = JsonSerializer.Deserialize<AppConfig.ConfigurationDocument>(File.ReadAllText(path + ".last-known-good"), options);
            var current = JsonSerializer.Deserialize<AppConfig.ConfigurationDocument>(File.ReadAllText(path), options);
            Assert.True(AppConfig.TryValidateDocument(backup, out _));
            Assert.Equal(expectedThreshold, backup.DeviceThresholds[0]);
            Assert.Equal(90f, current.DeviceThresholds[0]);
            Assert.Empty(Directory.GetFiles(workspace.Path, "*.pending-*"));
        }

        private static string SerializeConfig(float threshold)
            => JsonSerializer.Serialize(CreateConfig(threshold));
        private static AppConfig.ConfigurationDocument CreateConfig(float threshold) => new()
        {
            DeviceIPs = new[] { "192.168.1.5", "192.168.1.10", "192.168.1.15", "192.168.1.20" },
            DeviceThresholds = new[] { threshold, threshold, threshold, threshold },
            DeviceMonitoringModes = Enumerable.Repeat(DeviceMonitoringMode.AutoStandby, 4).ToArray()
        };
        private static DevicePlcWrapper Wrapper(int id, IPlcService plc)
            => new(new Device { Id = id, Name = "Device" + id, MonitoringMode = DeviceMonitoringMode.AutoStandby }, plc);
        internal static async Task WaitUntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.True(condition());
        }
    }

    internal sealed class ReviewWorkspace : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MitsubishiMonitor.Tests", Guid.NewGuid().ToString("N"));
        public ReviewWorkspace() { Directory.CreateDirectory(Path); }
        public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(Path, true); }
    }

    internal sealed class ReviewPlcService : IPlcService
    {
        private readonly TaskCompletionSource<bool> _connectGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ConnectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ConnectCalls { get; private set; }
        public int AcquisitionStarts { get; private set; }
        public ReviewPlcService(bool blockConnect = false) { if (!blockConnect) _connectGate.TrySetResult(true); }
        public void ReleaseConnect() => _connectGate.TrySetResult(true);
        public PlcStatus CurrentStatus { get; } = new();
        public PlcConfig Config { get; } = new() { IpAddress = "127.0.0.1" };
        public PlcConnectionSnapshot ConnectionSnapshot => new(1,
            CurrentStatus.IsConnected ? PlcConnectionPhase.OnlineFresh : PlcConnectionPhase.Disconnected, "test", 0, null, null);
        public bool IsAcquiring { get; private set; }
        public event EventHandler<bool> ConnectionStateChanged { add { } remove { } }
        public event EventHandler<PlcConnectionChangedEventArgs> ConnectionStateChangedDetailed { add { } remove { } }
        public event EventHandler<StateChangeEvent> StateChanged { add { } remove { } }
        public event EventHandler<TemperatureSampleEventArgs> TemperatureSampled { add { } remove { } }
        public async Task<bool> ConnectAsync()
        {
            ConnectCalls++;
            ConnectStarted.TrySetResult(true);
            await _connectGate.Task;
            CurrentStatus.IsConnected = true; // 刻意允许迟到成功，检验管理层授权边界。
            return true;
        }
        public void Disconnect() { CurrentStatus.IsConnected = false; StopAcquisition(); }
        public void StartAcquisition() { AcquisitionStarts++; IsAcquiring = true; }
        public void StopAcquisition() => IsAcquiring = false;
        public Task<bool[]> ReadXPointsAsync() => Task.FromResult(Array.Empty<bool>());
        public Task<bool[]> ReadYPointsAsync() => Task.FromResult(Array.Empty<bool>());
        public Task<float> ReadTemperatureAsync() => Task.FromResult(CurrentStatus.Temperature);
        public Task<PlcStatus> ReadAllAsync() => Task.FromResult(CurrentStatus);
    }
}
