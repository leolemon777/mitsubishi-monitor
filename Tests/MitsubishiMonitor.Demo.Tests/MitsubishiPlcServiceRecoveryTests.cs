using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HslCommunication;
using MitsubishiMonitor.Demo.Models;
using MitsubishiMonitor.Demo.Services;
using Xunit;

namespace MitsubishiMonitor.Demo.Tests
{
    public class MitsubishiPlcServiceRecoveryTests
    {
        [Theory]
        [InlineData(80f, 100f, true)]
        [InlineData(100f, 80f, false)]
        public async Task AuxiliaryCompletion_DoesNotOverwriteLatestTemperatureAlarm(float previous, float latest, bool expectedAlarm)
        {
            var transport = new FakeTransport();
            using var service = CreateService(new QueueTransportFactory(transport), 5000);
            service.Config.TemperatureThreshold = 90;
            service.Config.TargetTemperatureDefinition = new TemperatureRegisterDefinition
            {
                Address = "D20", DataType = PlcRegisterDataType.Int16, Divisor = 10
            };
            Assert.True(await service.ConnectAsync());
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var type = typeof(MitsubishiPlcService);
            var generation = service.ConnectionSnapshot.Generation;
            type.GetField("_isAcquiring", flags).SetValue(service, true);
            type.GetField("_activeAcquisitionToken", flags).SetValue(service, 1L);
            type.GetField("_acquisitionConnectionGeneration", flags).SetValue(service, generation);
            var commit = type.GetMethod("CommitPrimaryTemperatureSample", flags);
            var definition = service.Config.ResolveActualTemperatureDefinition();
            commit.Invoke(service, new object[] { generation, 1L, previous, (long)(previous * 10), definition });
            transport.BlockAuxiliaryRead();
            var auxiliary = (Task)type.GetMethod("TryUpdateAuxiliaryTelemetryAsync", flags)
                .Invoke(service, new object[] { generation, 1L, previous });
            try
            {
                Assert.True(await WaitUntilAsync(() => transport.AuxiliaryReadStarted.IsSet, TimeSpan.FromSeconds(2)));
                commit.Invoke(service, new object[] { generation, 1L, latest, (long)(latest * 10), definition });
                Assert.Equal(expectedAlarm, service.CurrentStatus.IsAlarm);
            }
            finally { transport.ReleaseAuxiliaryRead(); }
            await auxiliary.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(latest, service.CurrentStatus.Temperature);
            Assert.Equal(expectedAlarm, service.CurrentStatus.IsAlarm);
        }

        [Fact]
        public async Task StaleWatchdog_DoesNotWaitForBlockedOldRead_WhenReconnecting()
        {
            var blocked = new FakeTransport { TemperatureRaw = 250 };
            blocked.BlockTemperatureRead();
            var recovered = new FakeTransport { TemperatureRaw = 500 };
            var factory = new QueueTransportFactory(blocked, recovered);
            using var service = CreateService(factory, ioTimeoutMs: 5000);
            var recoveredSample = new TaskCompletionSource<TemperatureSampleEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            service.TemperatureSampled += (_, sample) =>
            {
                if (sample.Temperature == 50f)
                    recoveredSample.TrySetResult(sample);
            };

            Assert.True(await service.ConnectAsync());
            service.StartAcquisition();
            Assert.True(blocked.TemperatureReadStarted.Wait(TimeSpan.FromSeconds(1)));

            var sw = Stopwatch.StartNew();
            Assert.True(await WaitUntilAsync(
                () => service.TryDisconnectIfTemperatureStale(out _),
                TimeSpan.FromSeconds(2)));
            Assert.True(await service.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1)));
            service.StartAcquisition();
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1));
            Assert.True(service.CurrentStatus.IsConnected);
            Assert.Equal(50f, (await recoveredSample.Task.WaitAsync(TimeSpan.FromSeconds(1))).Temperature);

            blocked.ReleaseTemperatureRead();
            await Task.Delay(100);
            Assert.Equal(50f, service.CurrentStatus.Temperature);
        }

        [Fact]
        public async Task HardTimeout_AbandonsSession_AndFreshSessionCanRead()
        {
            var blocked = new FakeTransport { TemperatureRaw = 250 };
            blocked.BlockTemperatureRead();
            var recovered = new FakeTransport { TemperatureRaw = 505 };
            var factory = new QueueTransportFactory(blocked, recovered);
            using var service = CreateService(factory, ioTimeoutMs: 150);

            Assert.True(await service.ConnectAsync());

            var sw = Stopwatch.StartNew();
            Assert.True(float.IsNaN(await service.ReadTemperatureAsync().WaitAsync(TimeSpan.FromSeconds(2))));
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
            Assert.False(service.CurrentStatus.IsConnected);
            Assert.True(await service.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal(50.5f, await service.ReadTemperatureAsync());

            blocked.ReleaseTemperatureRead();
        }

        [Fact]
        public async Task RepeatedPermanentNativeBlocks_OpenCircuitBreaker_AndStopCreatingSessions()
        {
            var first = new FakeTransport();
            first.BlockTemperatureRead();
            first.BlockAbort();
            first.BlockClose();
            var second = new FakeTransport();
            second.BlockTemperatureRead();
            second.BlockAbort();
            second.BlockClose();
            var unusedThird = new FakeTransport();
            var factory = new QueueTransportFactory(first, second, unusedThird);
            var service = CreateService(factory, ioTimeoutMs: 120);

            try
            {
                Assert.True(await service.ConnectAsync());
                Assert.True(float.IsNaN(await service.ReadTemperatureAsync().WaitAsync(TimeSpan.FromSeconds(2))));
                Assert.True(await service.ConnectAsync());
                Assert.True(float.IsNaN(await service.ReadTemperatureAsync().WaitAsync(TimeSpan.FromSeconds(2))));

                Assert.True(service.IsCircuitBreakerOpen);
                Assert.True(service.OutstandingDetachedOperations >= 6);
                Assert.False(await service.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1)));
                Assert.Equal(2, factory.CreateCount);
                Assert.Contains("暂停新连接", service.LastConnectionError);
            }
            finally
            {
                first.ReleaseTemperatureRead();
                first.ReleaseAbort();
                first.ReleaseClose();
                second.ReleaseTemperatureRead();
                second.ReleaseAbort();
                second.ReleaseClose();
                service.Dispose();
            }
        }

        [Fact]
        public async Task DisconnectDuringConnect_RejectsLateSuccess()
        {
            var delayed = new FakeTransport();
            delayed.BlockConnect();
            var recovered = new FakeTransport();
            var factory = new QueueTransportFactory(delayed, recovered);
            using var service = CreateService(factory, ioTimeoutMs: 2000);

            var firstConnect = service.ConnectAsync();
            Assert.True(delayed.ConnectStarted.Wait(TimeSpan.FromSeconds(1)));

            service.Disconnect();
            delayed.ReleaseConnect();

            Assert.False(await firstConnect.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.False(service.CurrentStatus.IsConnected);
            Assert.True(await WaitUntilAsync(
                () => Volatile.Read(ref delayed.CloseCount) >= 2,
                TimeSpan.FromSeconds(1)));
            Assert.True(await service.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.True(service.CurrentStatus.IsConnected);
        }

        [Fact]
        public async Task PrimaryTemperature_IsPublished_WhenAuxiliaryRegisterFails()
        {
            var transport = new FakeTransport
            {
                TemperatureRaw = 500,
                FailingInt16Address = "D20"
            };
            var factory = new QueueTransportFactory(transport);
            using var service = CreateService(factory, ioTimeoutMs: 1000);
            service.Config.TargetTemperatureAddress = "D20";
            var sampleReceived = new TaskCompletionSource<TemperatureSampleEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            service.TemperatureSampled += (_, sample) => sampleReceived.TrySetResult(sample);

            Assert.True(await service.ConnectAsync());
            service.StartAcquisition();

            var received = await sampleReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
            service.StopAcquisition();

            Assert.Equal(50f, received.Temperature);
            Assert.False(received.HasFreshAuxiliaryData);
            Assert.Null(received.AuxiliarySampleTime);
            Assert.Equal(50f, service.CurrentStatus.Temperature);
            Assert.NotEqual(default, service.CurrentStatus.LastTemperatureSampleTime);
            Assert.True(service.CurrentStatus.IsConnected);
        }

        [Fact]
        public async Task FirstIoSnapshot_EstablishesBaselineWithoutCreatingFalseOperationEvents()
        {
            var transport = new FakeTransport { BoolValue = true, TemperatureRaw = 500 };
            var factory = new QueueTransportFactory(transport);
            using var service = CreateService(factory, ioTimeoutMs: 1000);
            var eventCount = 0;
            service.StateChanged += (_, _) => Interlocked.Increment(ref eventCount);

            Assert.True(await service.ConnectAsync());
            service.StartAcquisition();
            Assert.True(await WaitUntilAsync(
                () => Volatile.Read(ref transport.BoolReadCount) >= 3,
                TimeSpan.FromSeconds(1)));
            Assert.Equal(0, Volatile.Read(ref eventCount));

            transport.BoolValue = false;
            Assert.True(await WaitUntilAsync(
                () => Volatile.Read(ref eventCount) > 0,
                TimeSpan.FromSeconds(1)));
            service.StopAcquisition();
        }

        [Fact]
        public async Task ShortBoolPayload_IsRejectedWithoutShrinkingBoundState()
        {
            var transport = new FakeTransport { ShortBoolPayloadAfterVerification = true };
            var factory = new QueueTransportFactory(transport);
            using var service = CreateService(factory, ioTimeoutMs: 1000);

            Assert.True(await service.ConnectAsync());
            var values = await service.ReadXPointsAsync();

            Assert.Equal(service.Config.XCount, values.Length);
            Assert.Equal(service.Config.XCount, service.CurrentStatus.X.Length);
            Assert.True(service.CurrentStatus.IsConnected);
        }

        [Fact]
        public async Task BlockedClose_DoesNotBlockFreshConnection()
        {
            var oldTransport = new FakeTransport();
            oldTransport.BlockClose();
            var recovered = new FakeTransport();
            var factory = new QueueTransportFactory(oldTransport, recovered);
            using var service = CreateService(factory, ioTimeoutMs: 1000);

            Assert.True(await service.ConnectAsync());
            service.Disconnect();
            Assert.True(oldTransport.CloseStarted.Wait(TimeSpan.FromSeconds(1)));

            Assert.True(await service.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.True(service.CurrentStatus.IsConnected);

            oldTransport.ReleaseClose();
        }

        [Fact]
        public async Task BlockedAbortAndClose_DoNotBlockFreshConnectionOrDispose()
        {
            var oldTransport = new FakeTransport();
            oldTransport.BlockAbort();
            oldTransport.BlockClose();
            var recovered = new FakeTransport();
            var factory = new QueueTransportFactory(oldTransport, recovered);
            var service = CreateService(factory, ioTimeoutMs: 1000);

            Assert.True(await service.ConnectAsync());

            var sw = Stopwatch.StartNew();
            service.Disconnect();
            Assert.True(await service.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1)));
            service.Dispose();
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1));
            oldTransport.ReleaseAbort();
            oldTransport.ReleaseClose();
        }

        [Fact]
        public async Task EmptyTemperaturePayload_IsNeverPublishedAsZero()
        {
            var transport = new FakeTransport { ReturnEmptyTemperature = true };
            var factory = new QueueTransportFactory(transport);
            using var service = CreateService(factory, ioTimeoutMs: 1000);
            var sampleCount = 0;
            service.TemperatureSampled += (_, _) => Interlocked.Increment(ref sampleCount);

            Assert.True(await service.ConnectAsync());
            Assert.True(float.IsNaN(await service.ReadTemperatureAsync()));
            Assert.Equal(0, Volatile.Read(ref sampleCount));
            Assert.Equal(default, service.CurrentStatus.LastTemperatureSampleTime);
        }

        [Fact]
        public async Task EmptyDintTemperaturePayload_IsNeverPublishedAsZero()
        {
            var transport = new FakeTransport { ReturnEmptyDintTemperature = true };
            var factory = new QueueTransportFactory(transport);
            using var service = CreateService(factory, ioTimeoutMs: 1000);
            service.Config.TemperatureIsWord = false;

            Assert.True(await service.ConnectAsync());
            Assert.True(float.IsNaN(await service.ReadTemperatureAsync()));
            Assert.Equal(default, service.CurrentStatus.LastTemperatureSampleTime);
        }

        [Fact]
        public async Task RepeatedAuxiliaryFailures_DoNotDisconnectOrStopPrimarySamples()
        {
            var transport = new FakeTransport
            {
                TemperatureRaw = 500,
                FailingInt16Address = "D20"
            };
            var factory = new QueueTransportFactory(transport);
            using var service = CreateService(factory, ioTimeoutMs: 1000);
            service.Config.TargetTemperatureAddress = "D20";
            var sampleCount = 0;
            service.TemperatureSampled += (_, _) => Interlocked.Increment(ref sampleCount);

            Assert.True(await service.ConnectAsync());
            service.StartAcquisition();

            Assert.True(await WaitUntilAsync(
                () => Volatile.Read(ref sampleCount) >= 4,
                TimeSpan.FromSeconds(2)));
            service.StopAcquisition();

            Assert.True(service.CurrentStatus.IsConnected);
            Assert.Equal(50f, service.CurrentStatus.Temperature);
        }

        [Fact]
        public async Task RepeatedTemperatureFailures_AreNotMaskedBySuccessfulXyPolling()
        {
            var transport = new FakeTransport { FailTemperatureRead = true };
            var factory = new QueueTransportFactory(transport);
            using var service = CreateService(factory, ioTimeoutMs: 1000);

            Assert.True(await service.ConnectAsync());
            service.StartAcquisition();

            Assert.True(await WaitUntilAsync(
                () => !service.CurrentStatus.IsConnected,
                TimeSpan.FromSeconds(2)));
            Assert.True(Volatile.Read(ref transport.BoolReadCount) > 0);
            Assert.True(Volatile.Read(ref transport.TemperatureReadCount) >= 2);
            Assert.Equal(default, service.CurrentStatus.LastTemperatureSampleTime);
        }

        [Fact]
        public async Task ConcurrentStartAndStop_DoNotLeakPollingTimers()
        {
            var transport = new FakeTransport { TemperatureRaw = 250 };
            var factory = new QueueTransportFactory(transport);
            using var service = CreateService(factory, ioTimeoutMs: 1000);

            Assert.True(await service.ConnectAsync());
            Parallel.For(0, 16, _ => service.StartAcquisition());
            await Task.Delay(180);
            service.StopAcquisition();

            await Task.Delay(100);
            var readsAfterSettling = Volatile.Read(ref transport.TotalReadCount);
            await Task.Delay(150);

            Assert.Equal(readsAfterSettling, Volatile.Read(ref transport.TotalReadCount));
        }

        [Fact]
        public async Task StopThenStart_SameConnection_DiscardsPreviousAcquisitionCycle()
        {
            var transport = new FakeTransport { TemperatureRaw = 250 };
            transport.BlockTemperatureRead();
            var factory = new QueueTransportFactory(transport);
            using var service = CreateService(factory, ioTimeoutMs: 1000);
            var samples = new ConcurrentQueue<float>();
            service.TemperatureSampled += (_, sample) => samples.Enqueue(sample.Temperature);

            Assert.True(await service.ConnectAsync());
            service.StartAcquisition();
            Assert.True(transport.TemperatureReadStarted.Wait(TimeSpan.FromSeconds(1)));

            service.StopAcquisition();
            transport.TemperatureRaw = 500;
            service.StartAcquisition();
            transport.ReleaseTemperatureRead();

            Assert.True(await WaitUntilAsync(
                () => samples.Contains(50f),
                TimeSpan.FromSeconds(2)));
            service.StopAcquisition();

            Assert.DoesNotContain(25f, samples);
            Assert.Equal(50f, service.CurrentStatus.Temperature);
        }

        [Fact]
        public async Task Reconnect_ClearsPreviousSessionFreshness_BeforeAcquisitionStarts()
        {
            var first = new FakeTransport { TemperatureRaw = 250 };
            var second = new FakeTransport { TemperatureRaw = 500 };
            var factory = new QueueTransportFactory(first, second);
            using var service = CreateService(factory, ioTimeoutMs: 1000);

            Assert.True(await service.ConnectAsync());
            service.StartAcquisition();
            Assert.True(await WaitUntilAsync(
                () => service.CurrentStatus.LastTemperatureSampleTime != default,
                TimeSpan.FromSeconds(1)));

            service.Disconnect();
            Assert.True(await service.ConnectAsync());

            Assert.True(service.CurrentStatus.IsConnected);
            Assert.Equal(default, service.CurrentStatus.LastTemperatureSampleTime);
        }

        [Fact]
        public async Task Connect_TcpSuccessButMcVerificationFails_DoesNotPublishOnline()
        {
            var transport = new FakeTransport { FailProtocolVerification = true };
            var factory = new QueueTransportFactory(transport);
            using var service = CreateService(factory, ioTimeoutMs: 1000);
            var detailedStates = new ConcurrentQueue<PlcConnectionPhase>();
            service.ConnectionStateChangedDetailed += (_, args) => detailedStates.Enqueue(args.Snapshot.Phase);

            Assert.False(await service.ConnectAsync());
            Assert.False(service.CurrentStatus.IsConnected);
            Assert.Equal(PlcConnectionPhase.CommunicationFault, service.ConnectionSnapshot.Phase);
            Assert.Contains(PlcConnectionPhase.ProtocolVerifying, detailedStates);
            Assert.DoesNotContain(PlcConnectionPhase.OnlineFresh, detailedStates);
        }

        [Fact]
        public async Task ValidTemperatureSample_CarriesGenerationSequenceAndRawValue()
        {
            var transport = new FakeTransport { TemperatureRaw = 845 };
            var factory = new QueueTransportFactory(transport);
            using var service = CreateService(factory, ioTimeoutMs: 1000);
            var sampleTcs = new TaskCompletionSource<TemperatureSampleEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            service.TemperatureSampled += (_, sample) => sampleTcs.TrySetResult(sample);

            Assert.True(await service.ConnectAsync());
            service.StartAcquisition();
            var sample = await sampleTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(84.5f, sample.Temperature);
            Assert.Equal(845, sample.RawValue);
            Assert.Equal(1, sample.SampleSequence);
            Assert.Equal(sample.ConnectionGeneration, service.CurrentStatus.LastTemperatureConnectionGeneration);
            Assert.Equal(TemperatureSampleQuality.Valid, sample.Quality);
            Assert.Equal(845, service.CurrentStatus.LastTemperatureRawValue);
            service.StopAcquisition();
        }

        [Fact]
        public async Task FiniteButImpossibleTemperature_IsRejectedWithoutPublishingSample()
        {
            var transport = new FakeTransport { TemperatureRaw = 30000 };
            var factory = new QueueTransportFactory(transport);
            using var service = CreateService(factory, ioTimeoutMs: 1000);
            var sampleCount = 0;
            service.TemperatureSampled += (_, _) => Interlocked.Increment(ref sampleCount);

            Assert.True(await service.ConnectAsync());
            Assert.True(float.IsNaN(await service.ReadTemperatureAsync()));
            Assert.Equal(0, Volatile.Read(ref sampleCount));
            Assert.Equal(TemperatureSampleQuality.OutOfRange, service.CurrentStatus.TemperatureQuality);
            Assert.Equal(30000, service.CurrentStatus.LastTemperatureRawValue);
            Assert.True(service.CurrentStatus.IsConnected);
        }

        [Fact]
        public async Task ReentrantDisconnectDuringSampleCommit_CannotRestoreOldGenerationOnlineState()
        {
            var transport = new FakeTransport { TemperatureRaw = 500 };
            var factory = new QueueTransportFactory(transport);
            using var service = CreateService(factory, ioTimeoutMs: 1000);
            var snapshots = new ConcurrentQueue<PlcConnectionSnapshot>();
            var disconnectTriggered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var triggerCount = 0;

            service.ConnectionStateChangedDetailed += (_, args) => snapshots.Enqueue(args.Snapshot);
            service.CurrentStatus.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(PlcStatus.LastTemperatureSampleTime) &&
                    service.CurrentStatus.LastTemperatureSampleTime != default &&
                    Interlocked.Exchange(ref triggerCount, 1) == 0)
                {
                    // PlcStatus 在样本提交临界区内通知。利用同步回调在旧实现的
                    // “状态数据已写入、OnlineFresh 尚未发布”窗口中主动断线。
                    service.Disconnect();
                    disconnectTriggered.TrySetResult();
                }
            };

            Assert.True(await service.ConnectAsync());
            service.StartAcquisition();
            await disconnectTriggered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(await WaitUntilAsync(
                () => service.ConnectionSnapshot.Phase == PlcConnectionPhase.Disconnected,
                TimeSpan.FromSeconds(1)));

            var finalSnapshot = service.ConnectionSnapshot;
            Assert.False(service.CurrentStatus.IsConnected);
            Assert.Equal(PlcConnectionPhase.Disconnected, finalSnapshot.Phase);
            Assert.DoesNotContain(
                snapshots,
                snapshot => snapshot.Generation < finalSnapshot.Generation &&
                            snapshot.Phase == PlcConnectionPhase.OnlineFresh);
        }

        [Fact]
        public async Task ReentrantDisconnectFromOnlineProjection_CannotOverwriteNewerDisconnectedSnapshot()
        {
            var transport = new FakeTransport { TemperatureRaw = 500 };
            var factory = new QueueTransportFactory(transport);
            using var service = CreateService(factory, ioTimeoutMs: 1000);
            var snapshots = new ConcurrentQueue<PlcConnectionSnapshot>();
            var triggerCount = 0;

            service.ConnectionStateChangedDetailed += (_, args) => snapshots.Enqueue(args.Snapshot);
            service.CurrentStatus.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(PlcStatus.IsConnected) &&
                    service.CurrentStatus.IsConnected &&
                    Interlocked.Exchange(ref triggerCount, 1) == 0)
                {
                    service.Disconnect();
                }
            };

            Assert.False(await service.ConnectAsync());

            var finalSnapshot = service.ConnectionSnapshot;
            Assert.False(service.CurrentStatus.IsConnected);
            Assert.Equal(PlcConnectionPhase.Disconnected, finalSnapshot.Phase);
            Assert.DoesNotContain(
                snapshots,
                snapshot => snapshot.Generation < finalSnapshot.Generation &&
                            snapshot.IsTransportUsable);
        }

        private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                if (condition())
                    return true;
                await Task.Delay(20);
            }

            return condition();
        }

        private static MitsubishiPlcService CreateService(
            IMitsubishiPlcTransportFactory factory,
            int ioTimeoutMs)
        {
            return new MitsubishiPlcService(
                new PlcConfig
                {
                    Name = "测试PLC",
                    IpAddress = "127.0.0.1",
                    TemperatureAddress = "D10",
                    TemperatureIsWord = true,
                    TemperatureDivisor = 10f,
                    ReceiveTimeout = Math.Min(100, ioTimeoutMs),
                    IoOperationTimeout = ioTimeoutMs,
                    IoLockWaitTimeout = Math.Max(ioTimeoutMs, 500),
                    ConnectTimeout = Math.Max(ioTimeoutMs, 500),
                    XYInterval = 20,
                    TemperatureInterval = 50,
                    TemperatureStaleTimeout = 120,
                    ThermocoupleAAddress = "",
                    ThermocoupleBAddress = "",
                    ThermocoupleCAddress = "",
                    CRegisters = null
                },
                factory);
        }

        private sealed class QueueTransportFactory : IMitsubishiPlcTransportFactory
        {
            private readonly ConcurrentQueue<IMitsubishiPlcTransport> _transports;
            private int _createCount;

            public QueueTransportFactory(params IMitsubishiPlcTransport[] transports)
            {
                _transports = new ConcurrentQueue<IMitsubishiPlcTransport>(transports);
            }

            public IMitsubishiPlcTransport Create(PlcConfig config)
            {
                Interlocked.Increment(ref _createCount);
                if (_transports.TryDequeue(out var transport))
                    return transport;
                throw new InvalidOperationException("测试没有准备下一代 PLC transport");
            }

            public int CreateCount => Volatile.Read(ref _createCount);
        }

        private sealed class FakeTransport : IMitsubishiPlcTransport
        {
            private readonly ManualResetEventSlim _connectGate = new(initialState: true);
            private readonly ManualResetEventSlim _temperatureGate = new(initialState: true);
            private readonly ManualResetEventSlim _closeGate = new(initialState: true);
            private readonly ManualResetEventSlim _abortGate = new(initialState: true);
            private readonly ManualResetEventSlim _auxiliaryGate = new(initialState: true);
            public ManualResetEventSlim AuxiliaryReadStarted { get; } = new(false);

            public ManualResetEventSlim ConnectStarted { get; } = new(false);
            public ManualResetEventSlim TemperatureReadStarted { get; } = new(false);
            public ManualResetEventSlim CloseStarted { get; } = new(false);
            public short TemperatureRaw { get; set; }
            public string FailingInt16Address { get; set; } = "";
            public bool FailTemperatureRead { get; set; }
            public bool ReturnEmptyTemperature { get; set; }
            public bool ReturnEmptyDintTemperature { get; set; }
            public bool FailProtocolVerification { get; set; }
            public bool ShortBoolPayloadAfterVerification { get; set; }
            public volatile bool BoolValue;
            public int ReceiveTimeOut { get; set; }
            public int ConnectTimeOut { get; set; }
            public int CloseCount;
            public int AbortCount;
            public int BoolReadCount;
            public int TemperatureReadCount;
            public int TotalReadCount;

            public void BlockConnect() => _connectGate.Reset();
            public void ReleaseConnect() => _connectGate.Set();
            public void BlockTemperatureRead() => _temperatureGate.Reset();
            public void ReleaseTemperatureRead() => _temperatureGate.Set();
            public void BlockClose() => _closeGate.Reset();
            public void ReleaseClose() => _closeGate.Set();
            public void BlockAbort() => _abortGate.Reset();
            public void ReleaseAbort() => _abortGate.Set();
            public void BlockAuxiliaryRead() => _auxiliaryGate.Reset();
            public void ReleaseAuxiliaryRead() => _auxiliaryGate.Set();

            public OperateResult ConnectServer()
            {
                ConnectStarted.Set();
                _connectGate.Wait();
                return OperateResult.CreateSuccessResult();
            }

            public OperateResult ConnectClose()
            {
                Interlocked.Increment(ref CloseCount);
                CloseStarted.Set();
                _closeGate.Wait();
                return OperateResult.CreateSuccessResult();
            }

            public void Abort()
            {
                Interlocked.Increment(ref AbortCount);
                _abortGate.Wait();
            }

            public OperateResult<bool[]> ReadBool(string address, ushort length)
            {
                Interlocked.Increment(ref BoolReadCount);
                Interlocked.Increment(ref TotalReadCount);
                if (FailProtocolVerification &&
                    string.Equals(address, "X0", StringComparison.OrdinalIgnoreCase))
                    return new OperateResult<bool[]>("注入 MC 协议验证失败");
                if (ShortBoolPayloadAfterVerification &&
                    Volatile.Read(ref BoolReadCount) > 1)
                    return OperateResult.CreateSuccessResult(
                        Enumerable.Repeat(BoolValue, Math.Max(0, length - 1)).ToArray());
                return OperateResult.CreateSuccessResult(
                    Enumerable.Repeat(BoolValue, length).ToArray());
            }

            public OperateResult<short[]> ReadInt16(string address, ushort length)
            {
                Interlocked.Increment(ref TotalReadCount);
                if (address == "D20")
                {
                    AuxiliaryReadStarted.Set();
                    _auxiliaryGate.Wait();
                }
                if (string.Equals(address, "D10", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref TemperatureReadCount);
                    TemperatureReadStarted.Set();
                    var temperatureRaw = TemperatureRaw;
                    _temperatureGate.Wait();
                    if (FailTemperatureRead)
                        return new OperateResult<short[]>("注入温度读取失败");
                    if (ReturnEmptyTemperature)
                        return OperateResult.CreateSuccessResult(Array.Empty<short>());
                    return OperateResult.CreateSuccessResult(new[] { temperatureRaw });
                }

                if (string.Equals(address, FailingInt16Address, StringComparison.OrdinalIgnoreCase))
                    return new OperateResult<short[]>($"注入读取失败: {address}");
                return OperateResult.CreateSuccessResult(new[] { TemperatureRaw });
            }

            public OperateResult<int[]> ReadInt32(string address, ushort length)
            {
                Interlocked.Increment(ref TotalReadCount);
                if (string.Equals(address, "D10", StringComparison.OrdinalIgnoreCase) &&
                    ReturnEmptyDintTemperature)
                    return OperateResult.CreateSuccessResult(Array.Empty<int>());
                return OperateResult.CreateSuccessResult(new int[length]);
            }
        }
    }
}
