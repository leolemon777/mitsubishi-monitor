using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MitsubishiMonitor.Demo.Models;

namespace MitsubishiMonitor.Demo.Services
{
    /// <summary>
    /// 原软件视频录制专用数据源。
    /// 不创建网络、串口或 HslCommunication 对象，只更新现有 PlcStatus 绑定模型。
    /// </summary>
    public sealed class DemoPlcService : IPlcService
    {
        private readonly int _deviceId;
        private readonly PlcStatus _status;
        private readonly object _stateSync = new();
        private PlcConnectionSnapshot _connectionSnapshot =
            new PlcConnectionSnapshot(0, PlcConnectionPhase.Disconnected, "演示尚未连接", 0, null, null);
        private System.Threading.Timer _timer;
        private int _tick;
        private int _isUpdating;

        public DemoPlcService(PlcConfig config, int deviceId)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            _deviceId = deviceId;
            _status = new PlcStatus(Config.XCount, Config.YCount, Config.ActualMCount);
        }

        public PlcStatus CurrentStatus => _status;
        public PlcConfig Config { get; }
        public bool IsAcquiring { get; private set; }
        public PlcConnectionSnapshot ConnectionSnapshot => Volatile.Read(ref _connectionSnapshot);

        public event EventHandler<bool> ConnectionStateChanged;
        public event EventHandler<PlcConnectionChangedEventArgs> ConnectionStateChangedDetailed;
        public event EventHandler<StateChangeEvent> StateChanged;
        public event EventHandler<TemperatureSampleEventArgs> TemperatureSampled;

        private bool TryTransition(
            long generation,
            PlcConnectionPhase phase,
            string reason,
            DateTimeOffset? lastProtocolSuccessAt = null,
            DateTimeOffset? lastTemperatureSampleAt = null,
            bool notifyDetailed = true)
        {
            PlcConnectionSnapshot snapshot;
            lock (_stateSync)
            {
                var current = Volatile.Read(ref _connectionSnapshot);
                if (!PlcConnectionTransitionPolicy.CanTransition(
                        current.Generation,
                        current.Phase,
                        generation,
                        phase))
                    return false;

                snapshot = new PlcConnectionSnapshot(
                    generation,
                    phase,
                    reason,
                    0,
                    lastProtocolSuccessAt ?? current.LastProtocolSuccessAt,
                    lastTemperatureSampleAt ?? current.LastTemperatureSampleAt);
                Volatile.Write(ref _connectionSnapshot, snapshot);
                if (_status.IsConnected != snapshot.IsTransportUsable)
                    _status.IsConnected = snapshot.IsTransportUsable;
            }

            if (!ReferenceEquals(ConnectionSnapshot, snapshot))
                return false;

            if (notifyDetailed)
            {
                SafeEventDispatcher.Invoke(
                    this,
                    ConnectionStateChangedDetailed,
                    new PlcConnectionChangedEventArgs(snapshot));
            }

            return ReferenceEquals(ConnectionSnapshot, snapshot);
        }

        public Task<bool> ConnectAsync()
        {
            var current = ConnectionSnapshot;
            if (current.IsTransportUsable)
                return Task.FromResult(true);

            var generation = current.Generation + 1;
            if (!TryTransition(generation, PlcConnectionPhase.TcpConnecting, "演示 TCP 连接") ||
                !TryTransition(generation, PlcConnectionPhase.ProtocolVerifying, "演示协议验证") ||
                !TryTransition(
                    generation,
                    PlcConnectionPhase.AwaitingFirstSample,
                    "演示等待首个样本",
                    lastProtocolSuccessAt: DateTimeOffset.UtcNow))
                return Task.FromResult(false);

            ApplyFrame(raisePointEvents: false);
            var connectedSnapshot = ConnectionSnapshot;
            if (!connectedSnapshot.IsDataFresh || connectedSnapshot.Generation != generation)
                return Task.FromResult(false);

            SafeEventDispatcher.Invoke(this, ConnectionStateChanged, true);
            return Task.FromResult(
                ReferenceEquals(ConnectionSnapshot, connectedSnapshot) &&
                CurrentStatus.IsConnected);
        }

        public void Disconnect()
        {
            StopAcquisition();
            var current = ConnectionSnapshot;
            if (current.Phase == PlcConnectionPhase.Disconnected)
                return;

            var wasConnected = current.IsTransportUsable;
            if (TryTransition(
                    current.Generation + 1,
                    PlcConnectionPhase.Disconnected,
                    "演示断开") &&
                wasConnected)
                SafeEventDispatcher.Invoke(this, ConnectionStateChanged, false);
        }

        public Task<bool[]> ReadXPointsAsync() => Task.FromResult((bool[])_status.X.Clone());
        public Task<bool[]> ReadYPointsAsync() => Task.FromResult((bool[])_status.Y.Clone());
        public Task<float> ReadTemperatureAsync() => Task.FromResult(_status.Temperature);
        public Task<PlcStatus> ReadAllAsync() => Task.FromResult(_status);

        public void StartAcquisition()
        {
            if (IsAcquiring) return;
            IsAcquiring = true;
            _timer = new System.Threading.Timer(_ => ApplyFrame(true), null, 0, 850);
        }

        public void StopAcquisition()
        {
            IsAcquiring = false;
            Interlocked.Exchange(ref _timer, null)?.Dispose();
        }

        private void ApplyFrame(bool raisePointEvents)
        {
            if (!_status.IsConnected || Interlocked.Exchange(ref _isUpdating, 1) == 1)
                return;

            try
            {
                var tick = Interlocked.Increment(ref _tick);
                var stageIndex = (tick / 6 + _deviceId - 1) % Math.Max(1, Config.ProcessStages?.Count ?? 1);
                var now = DateTime.Now;
                var targetTemperature = _deviceId >= 3 ? 85f : 78f;
                var baseTemperature = _deviceId switch
                {
                    1 => 72.6f,
                    2 => 75.2f,
                    3 => 83.8f,
                    _ => 84.3f
                };
                var temperature = baseTemperature + (float)Math.Sin(tick / 5.0) * 0.35f + (stageIndex == 3 ? 0.18f : 0f);

                var nextX = BuildXFrame(tick);
                var nextY = BuildYFrame(stageIndex);
                var nextM = BuildMFrame(stageIndex);

                ApplyPointArray("X", _status.X, nextX, Config.GetXAddress, Config.GetXLabel, raisePointEvents);
                ApplyPointArray("Y", _status.Y, nextY, Config.GetYAddress, Config.GetYLabel, raisePointEvents);
                ApplyPointArray("M", _status.M, nextM, Config.GetMAddress, Config.GetMLabel, raisePointEvents);

                _status.X = nextX;
                _status.Y = nextY;
                _status.M = nextM;
                _status.Temperature = temperature;
                _status.TargetTemperature = targetTemperature;
                _status.ThermocoupleA = Config.HasVoltage ? 2.41f + (float)Math.Sin(tick / 8.0) * .03f : 0;
                _status.ThermocoupleB = Config.HasVoltage ? 2.39f + (float)Math.Sin(tick / 7.0) * .03f : 0;
                _status.ThermocoupleC = Config.HasVoltage ? 2.42f + (float)Math.Sin(tick / 6.0) * .03f : 0;
                _status.IsAlarm = false;
                _status.IsSsrFault = false;
                _status.CValues = BuildRegisterFrame(tick);
                _status.LastUpdateTime = now;
                _status.LastTemperatureSampleTime = now;
                _status.LastAuxiliarySampleTime = now;
                _status.LastTemperatureSampleSequence++;
                _status.LastTemperatureConnectionGeneration = ConnectionSnapshot.Generation;
                _status.TemperatureQuality = TemperatureSampleQuality.Valid;
                var currentSnapshot = ConnectionSnapshot;
                if (!TryTransition(
                        currentSnapshot.Generation,
                        PlcConnectionPhase.OnlineFresh,
                        "演示数据持续刷新",
                        currentSnapshot.LastProtocolSuccessAt ?? DateTimeOffset.UtcNow,
                        new DateTimeOffset(now),
                        notifyDetailed: currentSnapshot.Phase != PlcConnectionPhase.OnlineFresh))
                    return;

                SafeEventDispatcher.Invoke(this, TemperatureSampled, new TemperatureSampleEventArgs
                {
                    DeviceName = Config.Name,
                    Temperature = temperature,
                    TargetTemperature = targetTemperature,
                    ThermocoupleA = _status.ThermocoupleA,
                    ThermocoupleB = _status.ThermocoupleB,
                    ThermocoupleC = _status.ThermocoupleC,
                    IsAbnormal = false,
                    SampleTime = now,
                    AuxiliarySampleTime = now,
                    HasFreshAuxiliaryData = true,
                    ConnectionGeneration = ConnectionSnapshot.Generation,
                    SampleSequence = _status.LastTemperatureSampleSequence,
                    Quality = TemperatureSampleQuality.Valid
                });
            }
            finally
            {
                Interlocked.Exchange(ref _isUpdating, 0);
            }
        }

        private bool[] BuildXFrame(int tick)
        {
            var values = new bool[Config.XCount];
            Set(values, 0, true);
            Set(values, 3, true);
            Set(values, 7, true);
            Set(values, 1, tick % 18 >= 7);
            Set(values, 4, tick % 24 >= 12);
            return values;
        }

        private bool[] BuildYFrame(int stageIndex)
        {
            var values = new bool[Config.YCount];
            Set(values, 7, true);
            SetByAddress(values, "Y5", stageIndex <= 2);
            SetByAddress(values, "Y12", stageIndex == 3);
            SetByAddress(values, "Y14", true);
            return values;
        }

        private bool[] BuildMFrame(int stageIndex)
        {
            var values = new bool[Config.ActualMCount];
            if (Config.ProcessStages != null && Config.ProcessStages.Count > 0)
            {
                var address = Config.ProcessStages[Math.Min(stageIndex, Config.ProcessStages.Count - 1)].Address;
                var index = Config.MAddressList?.IndexOf(address) ?? -1;
                if (index >= 0) Set(values, index, true);
            }
            return values;
        }

        private Dictionary<string, int> BuildRegisterFrame(int tick)
        {
            var values = new Dictionary<string, int>();
            foreach (var register in Config.CRegisters ?? Enumerable.Empty<CRegisterDef>())
            {
                values[register.Address] = register.Address switch
                {
                    "D280" => 85,
                    "D260" => 82,
                    "D53" => 3,
                    _ => 30 + tick % 20
                };
            }
            return values;
        }

        private void SetByAddress(bool[] values, string address, bool value)
        {
            if (string.IsNullOrWhiteSpace(address) || address.Length < 2) return;
            try
            {
                var index = Convert.ToInt32(address.Substring(1), 8);
                Set(values, index, value);
            }
            catch
            {
                // 演示数据中遇到非八进制 Y 地址时忽略，不影响其他点位。
            }
        }

        private static void Set(bool[] values, int index, bool value)
        {
            if (index >= 0 && index < values.Length)
                values[index] = value;
        }

        private void ApplyPointArray(
            string pointType,
            bool[] previous,
            bool[] next,
            Func<int, string> addressSelector,
            Func<int, string> labelSelector,
            bool raiseEvents)
        {
            if (!raiseEvents) return;
            for (var index = 0; index < Math.Min(previous.Length, next.Length); index++)
            {
                if (previous[index] == next[index]) continue;
                SafeEventDispatcher.Invoke(this, StateChanged, new StateChangeEvent
                {
                    PointType = pointType,
                    PointIndex = index,
                    OldValue = previous[index],
                    NewValue = next[index],
                    Address = addressSelector(index),
                    PointLabel = labelSelector(index),
                    EventTime = DateTime.Now,
                    Operator = "视频演示"
                });
            }
        }
    }
}
