using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MitsubishiMonitor.Demo.Models;
using MitsubishiMonitor.Demo.Services;

namespace MitsubishiMonitor.Demo.Services
{
    /// <summary>
    /// 三菱PLC服务实现 (使用HslCommunication)
    /// FX3U-ENET-ADP 使用 MC协议 1E帧 (MelsecA1ENet)，IP 与端口 5000 需与模块一致
    /// </summary>
    public class MitsubishiPlcService : IPlcService, IDisposable
    {
        private sealed class PlcSession
        {
            public PlcSession(long generation, IMitsubishiPlcTransport transport)
            {
                Generation = generation;
                Transport = transport;
            }

            public long Generation { get; }
            public IMitsubishiPlcTransport Transport { get; }
            public SemaphoreSlim IoLock { get; } = new(1, 1);
            public int CloseStarted;
            public int TerminalCloseStarted;
            public long IoFailureVersion;
            public int GeneralFailures;
            public int TemperatureFailures;
            public int AuxiliaryFailures;
        }

        private readonly struct TemperatureReadValue
        {
            public TemperatureReadValue(float value, long rawValue, TemperatureRegisterDefinition definition)
            {
                Value = value;
                RawValue = rawValue;
                Definition = definition;
            }

            public float Value { get; }
            public long RawValue { get; }
            public TemperatureRegisterDefinition Definition { get; }
        }

        private enum IoFailureLane
        {
            General,
            Temperature,
            Auxiliary
        }

        private readonly PlcConfig _config;
        private readonly PlcStatus _status;
        private readonly IMitsubishiPlcTransportFactory _transportFactory;
        private bool[] _lastX;
        private bool[] _lastY;
        private bool[] _lastM;
        private long _ioBaselineGeneration;
        private volatile bool _isConnected;

        private CancellationTokenSource _acquisitionCts;
        private Task _acquisitionLoopTask;
        private volatile bool _isAcquiring;
        // 0=空闲；非 0=正在采集的采集周期令牌。连接换代或 Stop→Start 会得到
        // 全新令牌，旧任务结束时只能清除自己的令牌，不能覆盖新周期的 single-flight。
        private long _xyReadToken;
        private long _temperatureReadToken;
        private long _auxiliaryReadToken;
        private long _acquisitionConnectionGeneration;
        private long _activeAcquisitionToken;
        private long _nextAcquisitionToken;
        private long _acquisitionStartedTimestamp;
        private long _lastTemperatureSampleTimestamp;
        private long _lastAuxiliarySampleTimestamp;
        private readonly SemaphoreSlim _connectLock = new(1, 1);
        private readonly object _sessionSync = new();
        private readonly object _acquisitionSync = new();
        private PlcSession _activeSession;
        private long _connectionGeneration;
        private long _lastSlowIoLogMs;
        private long _lastIoFailureLogMs;
        private long _lastIoTimeoutLogMs;
        private readonly object _diagnosticAggregateSync = new();
        private string _slowIoSignature = "";
        private int _slowIoCount;
        private long _slowIoFirstMs;
        private long _slowIoLastMs;
        private string _failureSignature = "";
        private int _failureCount;
        private long _failureFirstMs;
        private long _failureLastMs;
        private int _isDisposed;
        private int _outstandingDetachedOperations;
        private static readonly SemaphoreSlim NativeCallGate = new(8, 8);
        private static int _nativeCallsInFlight;
        private int _circuitBreakerOpen;
        private string _circuitBreakerReason = "";

        private PlcConnectionPhase _connectionPhase = PlcConnectionPhase.Disconnected;
        private PlcConnectionSnapshot _connectionSnapshot =
            new PlcConnectionSnapshot(0, PlcConnectionPhase.Disconnected, "尚未连接", 0, null, null, null);
        private int _connectionFailureCount;
        private DateTimeOffset? _lastProtocolSuccessAt;
        private DateTimeOffset? _nextRetryAt;
        private long _temperatureSampleSequence;

        // 无线网桥偶发丢一两个包很常见，连续失败达到阈值才判离线。
        // 失败计数保存在每代 PlcSession 内并按采集通道分开，旧代/其他通道的成功不能清零本通道故障。
        private const int OfflineAfterConsecutiveFailures = 2;
        private const int MaxOutstandingDetachedOperations = 6;
        private const int CircuitBreakerRecoveryThreshold = 2;

        public event EventHandler<bool> ConnectionStateChanged;
        public event EventHandler<PlcConnectionChangedEventArgs> ConnectionStateChangedDetailed;
        public event EventHandler<StateChangeEvent> StateChanged;

        /// <summary>
        /// 一次温度采样完成事件（每次 TemperatureInterval 触发一次，包含温度与三相电压）
        /// </summary>
        public event EventHandler<TemperatureSampleEventArgs> TemperatureSampled;

        public PlcStatus CurrentStatus => _status;
        public PlcConfig Config => _config;
        public bool IsAcquiring => _isAcquiring;
        public bool IsCircuitBreakerOpen => Volatile.Read(ref _circuitBreakerOpen) == 1;
        public int OutstandingDetachedOperations => Math.Max(0, Volatile.Read(ref _outstandingDetachedOperations));
        public string CircuitBreakerReason => _circuitBreakerReason;
        public PlcConnectionSnapshot ConnectionSnapshot => Volatile.Read(ref _connectionSnapshot);
        public int NativeCallsInFlight => Math.Max(0, Volatile.Read(ref _nativeCallsInFlight));
        public long LastTemperatureSampleSequence => Interlocked.Read(ref _temperatureSampleSequence);

        /// <summary>
        /// 最近一次连接失败的原因（供界面提示用）
        /// </summary>
        public string LastConnectionError { get; private set; } = "";

        public bool IsTemperatureSampleStale(out TimeSpan age)
        {
            lock (_sessionSync)
                return IsTemperatureSampleStaleLocked(out age);
        }

        public bool IsTemperatureSampleDelayed(out TimeSpan age)
        {
            lock (_sessionSync)
            {
                if (!TryGetTemperatureSampleAgeLocked(out age) ||
                    _status.LastTemperatureSampleTime == default)
                    return false;

                var delayedAfterMs = Math.Max(
                    _config.TemperatureInterval + 5000,
                    _config.TemperatureInterval * 3 / 2);
                return age.TotalMilliseconds > delayedAfterMs;
            }
        }

        private bool TryGetTemperatureSampleAgeLocked(out TimeSpan age)
        {
            age = TimeSpan.Zero;
            if (!_isAcquiring || !_isConnected)
                return false;

            var baseline = Interlocked.Read(ref _lastTemperatureSampleTimestamp);
            if (baseline == 0)
                baseline = Interlocked.Read(ref _acquisitionStartedTimestamp);

            if (baseline == 0)
                return false;

            // 使用单调时钟判断新鲜度，避免 Windows 校时或时区调整造成误判。
            age = System.Diagnostics.Stopwatch.GetElapsedTime(
                baseline,
                System.Diagnostics.Stopwatch.GetTimestamp());
            return true;
        }

        private bool IsTemperatureSampleStaleLocked(out TimeSpan age)
        {
            if (!TryGetTemperatureSampleAgeLocked(out age))
                return false;

            var staleAfterMs = Math.Max(
                _config.TemperatureInterval * 2,
                _config.TemperatureStaleTimeout);
            return age.TotalMilliseconds > staleAfterMs;
        }

        /// <summary>
        /// 在同一个温度新鲜度临界区内重新判断并断开过期会话。
        /// 这样新采样或新一代连接不会被监控线程的旧 stale 判定误断。
        /// </summary>
        public bool TryDisconnectIfTemperatureStale(out TimeSpan age)
        {
            age = TimeSpan.Zero;
            PlcSession sessionToClose;
            bool wasConnected;
            string reason;
            int failures;

            lock (_sessionSync)
            {
                var expectedGeneration = _connectionGeneration;
                if (!_isConnected ||
                    !IsTemperatureSampleStaleLocked(out age))
                    return false;

                reason = $"温度采样超过 {age.TotalSeconds:F0} 秒未更新";
                var session = _activeSession;
                if (session == null || session.Generation != expectedGeneration)
                    return false;

                session.IoFailureVersion++;
                failures = session.TemperatureFailures;
                if (!TrySetDisconnectedStateLocked(
                        reason,
                        expectedGeneration,
                        out sessionToClose,
                        out wasConnected))
                    return false;
            }

            LogIoFailure(reason, failures, immediate: true);
            CompleteDisconnectedState(sessionToClose, reason, notify: true, wasConnected: wasConnected);
            return true;
        }

        public MitsubishiPlcService()
            : this(new PlcConfig(), HslMitsubishiPlcTransportFactory.Instance)
        {
        }

        /// <summary>
        /// 使用指定配置创建PLC服务（FX3U MC协议 1E帧，端口默认5000）
        /// </summary>
        public MitsubishiPlcService(PlcConfig config)
            : this(config, HslMitsubishiPlcTransportFactory.Instance)
        {
        }

        internal MitsubishiPlcService(
            PlcConfig config,
            IMitsubishiPlcTransportFactory transportFactory)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
            _status = new PlcStatus(_config.XCount, _config.YCount, _config.ActualMCount);
            _lastX = new bool[_config.XCount];
            _lastY = new bool[_config.YCount];
            _lastM = new bool[_config.ActualMCount];

        }

        private void PublishConnectionPhase(
            PlcConnectionPhase phase,
            string reason,
            long? generation = null,
            DateTimeOffset? nextRetryAt = null)
        {
            var snapshot = new PlcConnectionSnapshot(
                generation ?? Interlocked.Read(ref _connectionGeneration),
                phase,
                reason,
                Volatile.Read(ref _connectionFailureCount),
                nextRetryAt ?? _nextRetryAt,
                _lastProtocolSuccessAt,
                _status.LastTemperatureSampleTime == default
                    ? null
                    : new DateTimeOffset(_status.LastTemperatureSampleTime));

            Volatile.Write(ref _connectionSnapshot, snapshot);
            SafeEventDispatcher.Invoke(
                this,
                ConnectionStateChangedDetailed,
                new PlcConnectionChangedEventArgs(snapshot),
                ex => System.Diagnostics.Debug.WriteLine(
                    $"[PLC连接] 结构化状态订阅者异常: {_config.Name} - {ex.Message}"));
        }

        private void SetConnectionPhase(
            PlcConnectionPhase phase,
            string reason,
            long? generation = null,
            DateTimeOffset? nextRetryAt = null)
        {
            var previousPhase = _connectionPhase;
            _connectionPhase = phase;
            if (nextRetryAt.HasValue)
                _nextRetryAt = nextRetryAt;
            else if (phase != PlcConnectionPhase.Backoff)
                _nextRetryAt = null;
            PublishConnectionPhase(phase, reason, generation, nextRetryAt);

            // 持久化关键状态跃迁，现场拿到 diagnostic 日志即可区分 TCP、MC
            // 验证、首样本等待和真正的数据新鲜，而不必依赖 Debug 输出。
            if (previousPhase != phase ||
                phase is PlcConnectionPhase.CommunicationFault or PlcConnectionPhase.Disconnected)
            {
                Views.MainWindow.DbgLog("MitsubishiPlcService:ConnectionPhase", "PLC 连接阶段变化", new
                {
                    device = _config.Name,
                    _config.IpAddress,
                    generation = generation ?? Interlocked.Read(ref _connectionGeneration),
                    previousPhase = previousPhase.ToString(),
                    phase = phase.ToString(),
                    reason,
                    consecutiveFailures = Volatile.Read(ref _connectionFailureCount),
                    nextRetryAt = nextRetryAt ?? _nextRetryAt
                }, "CONNECT");
            }
        }

        /// <summary>
        /// 每个连接代次拥有独立的客户端和串行锁。旧连接即使有同步调用永久不返回，
        /// 新连接也会使用全新的客户端/锁，不会再被旧代阻塞。
        /// </summary>
        private PlcSession CreateSession(long generation)
        {
            var transport = _transportFactory.Create(_config);
            transport.ReceiveTimeOut = Math.Max(1, _config.ReceiveTimeout);
            transport.ConnectTimeOut = Math.Max(1, _config.ConnectTimeout);
            return new PlcSession(generation, transport);
        }

        private PlcSession GetActiveSession(long generation)
        {
            var session = Volatile.Read(ref _activeSession);
            return session != null && session.Generation == generation ? session : null;
        }

        private bool IsSessionActive(PlcSession session)
            => session != null
               && ReferenceEquals(Volatile.Read(ref _activeSession), session)
               && Interlocked.Read(ref _connectionGeneration) == session.Generation;

        private async Task<T> RunPlcCallAsync<T>(
            string operationName,
            long generation,
            Func<IMitsubishiPlcTransport, T> action,
            int? hardTimeoutMs = null)
        {
            var session = GetActiveSession(generation);
            if (session == null)
                throw new OperationCanceledException($"PLC 会话已被替换: {operationName}");

            return await RunPlcCallAsync(session, operationName, action, hardTimeoutMs).ConfigureAwait(false);
        }

        private async Task<T> RunPlcCallAsync<T>(
            PlcSession session,
            string operationName,
            Func<IMitsubishiPlcTransport, T> action,
            int? hardTimeoutMs = null)
        {
            int operationTimeout = Math.Max(100, hardTimeoutMs ?? _config.IoOperationTimeout);
            int lockTimeout = Math.Max(operationTimeout, _config.IoLockWaitTimeout);
            bool lockTaken = false;
            bool nativeGateTaken = false;
            bool notifyDisconnectedAfterUnlock = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                lockTaken = await session.IoLock
                    .WaitAsync(TimeSpan.FromMilliseconds(lockTimeout))
                    .ConfigureAwait(false);
                if (!lockTaken)
                {
                    notifyDisconnectedAfterUnlock = HandleHardIoTimeout(
                        session,
                        operationName,
                        lockTimeout,
                        "等待连接串行锁");
                    throw new TimeoutException($"{operationName} 等待 PLC I/O 锁超过 {lockTimeout}ms");
                }

                // 等锁期间可能已经发生断线/换代，旧调用不得触碰新连接。
                if (!IsSessionActive(session))
                    throw new OperationCanceledException($"PLC 会话已被替换: {operationName}");

                // HslCommunication 调用是同步 native/socket 工作。进程级闸门避免四台
                // PLC 同时超时后无限堆积线程和底层调用；释放时机覆盖迟到任务真正结束。
                nativeGateTaken = await NativeCallGate
                    .WaitAsync(TimeSpan.FromMilliseconds(lockTimeout))
                    .ConfigureAwait(false);
                if (!nativeGateTaken)
                {
                    notifyDisconnectedAfterUnlock = HandleHardIoTimeout(
                        session,
                        operationName,
                        lockTimeout,
                        "等待进程级 PLC 调用配额");
                    throw new TimeoutException($"{operationName} 等待进程级 PLC 调用配额超过 {lockTimeout}ms");
                }
                Interlocked.Increment(ref _nativeCallsInFlight);

                // HslCommunication 是同步 API。放到独立任务后使用应用层硬截止，
                // 即使底层 ReceiveTimeOut 失效，本方法也能按时返回并释放上层采集标志。
                var callTask = Task.Run(() => action(session.Transport));
                var completed = await Task.WhenAny(
                    callTask,
                    Task.Delay(operationTimeout)).ConfigureAwait(false);

                if (!ReferenceEquals(completed, callTask))
                {
                    notifyDisconnectedAfterUnlock = HandleHardIoTimeout(
                        session,
                        operationName,
                        operationTimeout,
                        "执行 PLC 指令");
                    ObserveLateTask(callTask, operationName, session);
                    nativeGateTaken = false;
                    throw new TimeoutException($"{operationName} 执行超过硬截止 {operationTimeout}ms");
                }

                T result;
                try
                {
                    result = await callTask.ConfigureAwait(false);
                }
                catch
                {
                    if (!IsSessionActive(session))
                        ScheduleSessionClose(session, $"旧代异常任务结束: {operationName}", terminalPass: true);
                    throw;
                }

                if (!IsSessionActive(session))
                {
                    // 首次断链可能发生在 ConnectServer 尚未发布 Socket 之前。
                    // 迟到调用结束后必须再做一次不受 CloseStarted 限制的终结关闭。
                    ScheduleSessionClose(session, $"丢弃迟到结果: {operationName}", terminalPass: true);
                    throw new OperationCanceledException($"PLC 会话已被替换，丢弃迟到结果: {operationName}");
                }

                return result;
            }
            finally
            {
                sw.Stop();
                if (lockTaken)
                    session.IoLock.Release();
                if (nativeGateTaken)
                {
                    Interlocked.Decrement(ref _nativeCallsInFlight);
                    NativeCallGate.Release();
                }
                LogSlowIo(operationName, sw.ElapsedMilliseconds);

                // 外部订阅者不能在持有本代 I/O 锁时同步回调，避免未来订阅者
                // 再进入连接 API 后形成新的锁循环。
                if (notifyDisconnectedAfterUnlock)
                {
                    try
                    {
                        SafeEventDispatcher.Invoke(
                            this,
                            ConnectionStateChanged,
                            false,
                            ex => System.Diagnostics.Debug.WriteLine(
                                $"[PLC连接] 断线事件订阅者异常: {_config.Name} - {ex.Message}"));
                    }
                    catch (Exception eventEx)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[PLC连接] 断线事件订阅者异常: {_config.Name} - {eventEx.Message}");
                    }
                }
            }
        }

        private void ObserveLateTask<T>(Task<T> task, string operationName, PlcSession session)
        {
            TrackDetachedTask(
                task,
                $"迟到 PLC 调用: {operationName}",
                completed =>
                {
                    Interlocked.Decrement(ref _nativeCallsInFlight);
                    NativeCallGate.Release();
                    System.Diagnostics.Debug.WriteLine(
                        $"[PLC会话] 旧代迟到任务已结束: generation={session.Generation}, operation={operationName}");
                    ScheduleSessionClose(session, $"迟到任务终结清理: {operationName}", terminalPass: true);
                });
        }

        private void TrackDetachedTask(
            Task task,
            string operationName,
            Action<Task> afterCompletion = null)
        {
            var outstanding = Interlocked.Increment(ref _outstandingDetachedOperations);
            if (outstanding >= MaxOutstandingDetachedOperations)
                OpenCircuitBreaker(operationName, outstanding);

            _ = task.ContinueWith(
                completed =>
                {
                    if (completed.IsFaulted)
                        _ = completed.Exception;

                    var remaining = Math.Max(0, Interlocked.Decrement(ref _outstandingDetachedOperations));
                    if (remaining <= CircuitBreakerRecoveryThreshold)
                        TryCloseCircuitBreaker(remaining);

                    try { afterCompletion?.Invoke(completed); }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[PLC熔断] 迟到任务完成回调异常: {operationName} - {ex.Message}");
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void OpenCircuitBreaker(string operationName, int outstanding)
        {
            var reason =
                $"检测到 {outstanding} 个未结束的底层 PLC 任务，已暂停新连接；请检查网络/驱动，任务释放或重启程序后恢复";
            _circuitBreakerReason = reason;
            LastConnectionError = reason;
            if (Interlocked.Exchange(ref _circuitBreakerOpen, 1) == 0)
            {
                Views.MainWindow.DbgLog("MitsubishiPlcService:CircuitBreaker", "PLC 底层阻塞任务熔断", new
                {
                    device = _config.Name,
                    _config.IpAddress,
                    operationName,
                    outstanding,
                    limit = MaxOutstandingDetachedOperations
                }, "PLC_IO");
            }
        }

        private void TryCloseCircuitBreaker(int remaining)
        {
            if (Interlocked.CompareExchange(ref _circuitBreakerOpen, 0, 1) != 1)
                return;

            _circuitBreakerReason = "";
            if (LastConnectionError.Contains("底层 PLC 任务", StringComparison.Ordinal))
                LastConnectionError = "";
            System.Diagnostics.Debug.WriteLine(
                $"[PLC熔断] {_config.Name} 阻塞任务降至 {remaining}，允许重新连接");
        }

        private void LogSlowIo(string operationName, long elapsedMs)
        {
            if (elapsedMs < 1000) return;

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var signature = operationName ?? "未知操作";
            int count;
            long firstMs;
            bool emit;
            lock (_diagnosticAggregateSync)
            {
                if (!string.Equals(_slowIoSignature, signature, StringComparison.Ordinal))
                {
                    _slowIoSignature = signature;
                    _slowIoCount = 0;
                    _slowIoFirstMs = nowMs;
                }

                _slowIoCount++;
                _slowIoLastMs = nowMs;
                emit = _slowIoCount == 1 || nowMs - _lastSlowIoLogMs >= 30000;
                if (emit)
                    _lastSlowIoLogMs = nowMs;
                count = _slowIoCount;
                firstMs = _slowIoFirstMs;
            }

            if (!emit) return;

            Views.MainWindow.DbgLog("MitsubishiPlcService:SlowIo", "PLC 请求耗时过长", new
            {
                device = _config.Name,
                _config.IpAddress,
                operationName,
                elapsedMs,
                generation = Interlocked.Read(ref _connectionGeneration),
                aggregateCount = count,
                firstTimestamp = firstMs,
                lastTimestamp = nowMs
            }, "PLC_IO");
        }

        private void LogIoFailure(string reason, int failures, bool immediate)
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var signature = reason ?? "未知通信失败";
            int count;
            long firstMs;
            bool emit;
            lock (_diagnosticAggregateSync)
            {
                if (!string.Equals(_failureSignature, signature, StringComparison.Ordinal))
                {
                    _failureSignature = signature;
                    _failureCount = 0;
                    _failureFirstMs = nowMs;
                }

                _failureCount++;
                _failureLastMs = nowMs;
                emit = immediate || _failureCount == 1 || nowMs - _lastIoFailureLogMs >= 30000;
                if (emit)
                    _lastIoFailureLogMs = nowMs;
                count = _failureCount;
                firstMs = _failureFirstMs;
            }

            if (!emit) return;

            Views.MainWindow.DbgLog("MitsubishiPlcService:IoFailure", "PLC 通信失败", new
            {
                device = _config.Name,
                _config.IpAddress,
                reason,
                failures,
                immediate,
                generation = Interlocked.Read(ref _connectionGeneration),
                aggregateCount = count,
                firstTimestamp = firstMs,
                lastTimestamp = nowMs
            }, "PLC_IO");
        }

        private void LogIoTimeout(
            string operationName,
            int timeoutMs,
            string phase,
            long generation)
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var lastMs = Interlocked.Read(ref _lastIoTimeoutLogMs);
            if (nowMs - lastMs < 1000) return;
            Interlocked.Exchange(ref _lastIoTimeoutLogMs, nowMs);

            var lastSample = _status.LastTemperatureSampleTime;
            var sampleAgeSeconds = lastSample == default
                ? (double?)null
                : Math.Round((DateTime.Now - lastSample).TotalSeconds, 1);

            Views.MainWindow.DbgLog("MitsubishiPlcService:IoTimeout", "PLC 指令硬超时，废弃旧连接会话", new
            {
                device = _config.Name,
                _config.IpAddress,
                operationName,
                phase,
                timeoutMs,
                receiveTimeoutMs = _config.ReceiveTimeout,
                generation,
                sampleAgeSeconds,
                acquisitionToken = Volatile.Read(ref _activeAcquisitionToken),
                xyReadToken = Volatile.Read(ref _xyReadToken),
                temperatureReadToken = Volatile.Read(ref _temperatureReadToken),
                auxiliaryReadToken = Volatile.Read(ref _auxiliaryReadToken)
            }, "PLC_IO");
        }

        private bool HandleHardIoTimeout(
            PlcSession session,
            string operationName,
            int timeoutMs,
            string phase)
        {
            LogIoTimeout(operationName, timeoutMs, phase, session.Generation);
            var reason = $"{operationName} {phase}超过 {timeoutMs}ms";
            PlcSession sessionToClose;
            bool wasConnected;
            bool changed;

            lock (_sessionSync)
            {
                if (!ReferenceEquals(_activeSession, session) ||
                    _connectionGeneration != session.Generation)
                    return false;

                session.IoFailureVersion++;
                changed = TrySetDisconnectedStateLocked(
                    reason,
                    session.Generation,
                    out sessionToClose,
                    out wasConnected);
            }

            if (changed)
                CompleteDisconnectedState(sessionToClose, reason, notify: false, wasConnected: wasConnected);
            return changed && wasConnected;
        }

        private Task StartBestEffortClose(PlcSession session, string reason)
        {
            if (session == null || Interlocked.Exchange(ref session.CloseStarted, 1) == 1)
                return Task.CompletedTask;

            ScheduleSessionClose(session, reason, terminalPass: false);
            return Task.CompletedTask;
        }

        private void ScheduleSessionClose(PlcSession session, string reason, bool terminalPass)
        {
            if (session == null)
                return;

            // 常规关闭由 CloseStarted 去重；迟到 I/O 结束后的 terminal pass
            // 只允许补做一次。否则每个迟到结果都会再次调用第三方 Abort/Close，
            // 反而可能制造新的线程堆积和串口/Socket 竞争。
            if (terminalPass && Interlocked.Exchange(ref session.TerminalCloseStarted, 1) == 1)
                return;

            // Abort 和 ConnectClose 分别在独立任务中执行。即使某个第三方关闭 API
            // 永不返回，看门狗、新会话连接与 Dispose 也不会被它拖死。
            var abortTask = Task.Run(() =>
            {
                try
                {
                    session.Transport.Abort();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[PLC连接] 强制中止旧会话异常: {_config.Name} - {ex.Message}");
                }
            });
            TrackDetachedTask(abortTask, $"Abort generation={session.Generation}");

            var closeTask = Task.Run(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    session.Transport.ConnectClose();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[PLC连接] 关闭旧会话异常: {_config.Name} - {ex.Message}");
                }
                finally
                {
                    sw.Stop();
                    if (sw.ElapsedMilliseconds >= 1000)
                    {
                        Views.MainWindow.DbgLog("MitsubishiPlcService:SlowClose", "关闭旧 PLC 会话耗时过长", new
                        {
                            device = _config.Name,
                            _config.IpAddress,
                            session.Generation,
                            reason,
                            terminalPass,
                            elapsedMs = sw.ElapsedMilliseconds
                        }, "PLC_IO");
                    }
                }
            });
            TrackDetachedTask(closeTask, $"ConnectClose generation={session.Generation}");
        }

        private bool SetDisconnectedState(
            string reason,
            bool notify,
            long? expectedGeneration = null)
        {
            PlcSession sessionToClose;
            bool wasConnected;
            bool changed;

            lock (_sessionSync)
            {
                changed = TrySetDisconnectedStateLocked(
                    reason,
                    expectedGeneration,
                    out sessionToClose,
                    out wasConnected);
            }

            if (changed)
                CompleteDisconnectedState(sessionToClose, reason, notify, wasConnected);
            return changed;
        }

        /// <summary>
        /// 调用方必须已持有 _sessionSync。只做代际切换，不执行第三方关闭或外部事件。
        /// </summary>
        private bool TrySetDisconnectedStateLocked(
            string reason,
            long? expectedGeneration,
            out PlcSession sessionToClose,
            out bool wasConnected)
        {
            sessionToClose = null;
            wasConnected = false;

            if (expectedGeneration.HasValue &&
                _connectionGeneration != expectedGeneration.Value)
                return false;

            wasConnected = _isConnected || _status.IsConnected;
            sessionToClose = _activeSession;
            _activeSession = null;
            _connectionGeneration++;
            // 这是连接代次边界，不是同一连接的 Stop→Start。新代可以和
            // 旧代已脱离的同步调用并行恢复；旧 lane 的 finally 只会清掉
            // 自己的单调 acquisition token。
            Interlocked.Exchange(ref _xyReadToken, 0);
            Interlocked.Exchange(ref _temperatureReadToken, 0);
            Interlocked.Exchange(ref _auxiliaryReadToken, 0);
            _isConnected = false;
            _status.IsConnected = false;
            LastConnectionError = reason ?? "";
            _connectionFailureCount = Math.Min(1000, _connectionFailureCount + 1);
            return true;
        }

        private void CompleteDisconnectedState(
            PlcSession sessionToClose,
            string reason,
            bool notify,
            bool wasConnected)
        {
            _ = StartBestEffortClose(sessionToClose, reason);
            var phase = Volatile.Read(ref _isDisposed) == 1
                ? PlcConnectionPhase.Disposed
                : reason != null && reason.Contains("用户主动断开", StringComparison.Ordinal)
                    ? PlcConnectionPhase.Disconnected
                    : PlcConnectionPhase.CommunicationFault;
            SetConnectionPhase(phase, reason ?? "连接已断开");
            if (notify && wasConnected)
                SafeEventDispatcher.Invoke(
                    this,
                    ConnectionStateChanged,
                    false,
                    ex => System.Diagnostics.Debug.WriteLine(
                        $"[PLC连接] 断线事件订阅者异常: {_config.Name} - {ex.Message}"));
        }

        private bool IsConnectionCurrent(long generation)
            => _isConnected && Interlocked.Read(ref _connectionGeneration) == generation;

        private bool IsAcquisitionCurrent(long connectionGeneration, long acquisitionToken)
            => acquisitionToken != 0 &&
               _isAcquiring &&
               Volatile.Read(ref _activeAcquisitionToken) == acquisitionToken &&
               Volatile.Read(ref _acquisitionConnectionGeneration) == connectionGeneration &&
               IsConnectionCurrent(connectionGeneration);

        private bool ResetTemperatureFreshness(long expectedGeneration)
        {
            lock (_sessionSync)
            {
                if (!IsConnectionCurrent(expectedGeneration))
                    return false;

                Interlocked.Exchange(
                    ref _acquisitionStartedTimestamp,
                    System.Diagnostics.Stopwatch.GetTimestamp());
                Interlocked.Exchange(ref _lastTemperatureSampleTimestamp, 0);
                Interlocked.Exchange(ref _lastAuxiliarySampleTimestamp, 0);
                _status.LastTemperatureSampleTime = default;
                _status.LastTemperatureSampleSequence = 0;
                _status.LastTemperatureConnectionGeneration = expectedGeneration;
                _status.LastTemperatureRawValue = 0;
                _status.TemperatureQuality = TemperatureSampleQuality.Stale;
                _status.LastTemperatureQualityReason = "新连接尚未取得温度样本";
                _status.LastAuxiliarySampleTime = default;
                _ioBaselineGeneration = 0;
                return true;
            }
        }

        public async Task<bool> ConnectAsync()
        {
            if (Volatile.Read(ref _isDisposed) == 1)
                return false;
            if (IsCircuitBreakerOpen)
            {
                LastConnectionError = _circuitBreakerReason;
                return false;
            }

            await _connectLock.WaitAsync().ConfigureAwait(false);
            PlcSession session = null;
            try
            {
                if (IsCircuitBreakerOpen)
                {
                    LastConnectionError = _circuitBreakerReason;
                    return false;
                }
                if (_isConnected && _status.IsConnected)
                    return true;

                PlcSession previousSession;
                lock (_sessionSync)
                {
                    if (Volatile.Read(ref _isDisposed) == 1)
                        return false;

                    previousSession = _activeSession;
                    var generation = Interlocked.Increment(ref _connectionGeneration);
                    // 连接代次切换后允许新会话立即采集；旧代 lane 仍由自身
                    // finally 持有并释放旧令牌，且 acquisition token 单调递增，
                    // 不会误清新代令牌。
                    Interlocked.Exchange(ref _xyReadToken, 0);
                    Interlocked.Exchange(ref _temperatureReadToken, 0);
                    Interlocked.Exchange(ref _auxiliaryReadToken, 0);
                    session = CreateSession(generation);
                    _activeSession = session;
                    _isConnected = false;
                    _status.IsConnected = false;
                }

                SetConnectionPhase(
                    PlcConnectionPhase.TcpConnecting,
                    "正在建立 TCP 会话",
                    session.Generation);

                // 关闭旧会话不等待旧会话的 I/O 锁，也不阻塞新连接。
                _ = StartBestEffortClose(previousSession, "建立新连接前废弃旧会话");

                System.Diagnostics.Debug.WriteLine($"[PLC连接] 尝试连接 {_config.Name} ({_config.IpAddress}:{_config.Port})");

                int connectHardTimeout = Math.Max(
                    Math.Max(100, _config.IoOperationTimeout),
                    Math.Max(100, _config.ConnectTimeout + 1000));
                var result = await RunPlcCallAsync(
                    session,
                    "ConnectServer",
                    transport => transport.ConnectServer(),
                    connectHardTimeout).ConfigureAwait(false);

                if (result.IsSuccess)
                {
                    SetConnectionPhase(
                        PlcConnectionPhase.ProtocolVerifying,
                        "TCP 已建立，正在验证 MC 协议",
                        session.Generation);

                    // 只读验证：不能仅凭 ConnectServer 把 Socket 在线当作 PLC 在线。
                    // 读取一个配置定义的 X 点不会改写现场状态，也能覆盖 MC 1E 请求/响应链路。
                    var verificationAddress = string.IsNullOrWhiteSpace(_config.XStartAddress)
                        ? "X0"
                        : _config.XStartAddress;
                    var verification = await RunPlcCallAsync(
                        session,
                        $"ProtocolVerify {verificationAddress}",
                        transport => transport.ReadBool(verificationAddress, 1),
                        Math.Max(100, _config.IoOperationTimeout)).ConfigureAwait(false);
                    if (!verification.IsSuccess ||
                        verification.Content == null ||
                        verification.Content.Length < 1)
                    {
                        var verificationReason =
                            $"MC 协议验证失败: {verification.Message ?? "空响应"}";
                        SetDisconnectedState(
                            verificationReason,
                            notify: true,
                            expectedGeneration: session.Generation);
                        return false;
                    }

                    lock (_sessionSync)
                    {
                        // ConnectServer 等待期间可能发生用户断开、超时废弃或 Dispose。
                        // 迟到的成功结果没有资格把服务重新标为在线。
                        if (Volatile.Read(ref _isDisposed) == 1 || !IsSessionActive(session))
                        {
                            _ = StartBestEffortClose(session, "丢弃迟到的连接成功结果");
                            return false;
                        }

                        LastConnectionError = "";
                        _lastProtocolSuccessAt = DateTimeOffset.UtcNow;
                        _isConnected = true;
                        _status.IsConnected = true;
                    }

                    // 每个新 TCP 会话都必须从“尚无本代温度样本”开始。
                    // 即使采集尚未启动，也不能让上一个会话的时间戳被 UI 当成实时数据。
                    ResetTemperatureFreshness(session.Generation);

                    if (!IsConnectionCurrent(session.Generation))
                        return false;

                    SetConnectionPhase(
                        PlcConnectionPhase.AwaitingFirstSample,
                        "MC 协议验证通过，等待本代首个温度样本",
                        session.Generation);

                    SafeEventDispatcher.Invoke(
                        this,
                        ConnectionStateChanged,
                        true,
                        ex => System.Diagnostics.Debug.WriteLine(
                            $"[PLC连接] 上线事件订阅者异常: {_config.Name} - {ex.Message}"));
                    System.Diagnostics.Debug.WriteLine($"[PLC连接] ✓ 连接成功: {_config.Name}");
                    return IsConnectionCurrent(session.Generation);
                }
                else
                {
                    SetDisconnectedState(
                        result.Message ?? "未知错误",
                        notify: true,
                        expectedGeneration: session.Generation);
                    System.Diagnostics.Debug.WriteLine($"[PLC连接] ✗ 连接失败: {_config.Name} - {LastConnectionError}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                // 若会话已被用户断开或被看门狗换代，不覆盖更准确的断开原因。
                if (session != null && IsSessionActive(session))
                {
                    SetDisconnectedState(
                        ex.Message ?? "未知异常",
                        notify: true,
                        expectedGeneration: session.Generation);
                }

                System.Diagnostics.Debug.WriteLine($"[PLC连接] ✗ 连接异常: {_config.Name} - {ex.Message}");
                return false;
            }
            finally
            {
                _connectLock.Release();
            }
        }

        public void Disconnect()
        {
            SetDisconnectedState("用户主动断开", notify: true);
            StopAcquisition();
        }

        /// <summary>
        /// 读取失败统一处理。默认按"连续失败计数"容错：未达到阈值只记录不断线；
        /// immediate=true 用于温度采样长时间停滞这类已经累积多个周期的判定，直接断线。
        /// </summary>
        private void HandleConnectionFailure(
            string reason,
            bool immediate = false,
            long? expectedGeneration = null,
            IoFailureLane lane = IoFailureLane.General)
        {
            PlcSession sessionToClose = null;
            bool wasConnected = false;
            bool shouldDisconnect = false;
            int failures;

            lock (_sessionSync)
            {
                var session = _activeSession;
                if (!_isConnected || session == null ||
                    (expectedGeneration.HasValue && session.Generation != expectedGeneration.Value))
                    return;

                session.IoFailureVersion++;
                failures = GetFailureCountLocked(session, lane);
                if (lane == IoFailureLane.Auxiliary)
                {
                    // 目标温度、热电偶或扩展寄存器不是连接活性的依据。
                    // 地址配置错误或单个辅助寄存器不可读时，只记录诊断，不能把
                    // 实际温度仍在正常刷新的主连接反复踢下线。
                    failures = IncrementFailureCountLocked(session, lane);
                }
                else if (!immediate)
                {
                    failures = IncrementFailureCountLocked(session, lane);
                    if (failures >= OfflineAfterConsecutiveFailures)
                    {
                        shouldDisconnect = TrySetDisconnectedStateLocked(
                            reason,
                            session.Generation,
                            out sessionToClose,
                            out wasConnected);
                    }
                    else
                    {
                        LastConnectionError = reason;
                    }
                }
                else
                {
                    shouldDisconnect = TrySetDisconnectedStateLocked(
                        reason,
                        session.Generation,
                        out sessionToClose,
                        out wasConnected);
                }
            }

            LogIoFailure(reason, failures, immediate);
            if (!shouldDisconnect)
            {
                if (lane == IoFailureLane.Auxiliary)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[PLC辅助] ⚠ 辅助读取连续失败 {failures} 次，主连接保持在线: {_config.Name} - {reason}");
                }
                else if (!immediate)
                {
                    System.Diagnostics.Debug.WriteLine($"[PLC连接] ⚠ 读取失败 {failures}/{OfflineAfterConsecutiveFailures}，暂不判离线: {_config.Name} - {reason}");
                }
                return;
            }

            CompleteDisconnectedState(sessionToClose, reason, notify: true, wasConnected: wasConnected);
            System.Diagnostics.Debug.WriteLine($"[PLC连接] ✗ 自动检测离线: {_config.Name} - {reason}");
        }

        private static int GetFailureCountLocked(PlcSession session, IoFailureLane lane)
            => lane switch
            {
                IoFailureLane.Temperature => session.TemperatureFailures,
                IoFailureLane.Auxiliary => session.AuxiliaryFailures,
                _ => session.GeneralFailures
            };

        private static int IncrementFailureCountLocked(PlcSession session, IoFailureLane lane)
        {
            return lane switch
            {
                IoFailureLane.Temperature => ++session.TemperatureFailures,
                IoFailureLane.Auxiliary => ++session.AuxiliaryFailures,
                _ => ++session.GeneralFailures
            };
        }

        public async Task<bool[]> ReadXPointsAsync()
        {
            var connectionGeneration = Interlocked.Read(ref _connectionGeneration);
            try
            {
                if (!IsConnectionCurrent(connectionGeneration))
                {
                    System.Diagnostics.Debug.WriteLine($"[X点读取] ⚠ 未连接，跳过读取");
                    return new bool[_config.XCount];
                }

                var result = await RunPlcCallAsync(
                    $"ReadBool {_config.XStartAddress}×{_config.XCount}",
                    connectionGeneration,
                    transport => transport.ReadBool(_config.XStartAddress, (ushort)_config.XCount)).ConfigureAwait(false);

                if (result.IsSuccess && result.Content != null &&
                    result.Content.Length == _config.XCount)
                {
                    var data = result.Content;
                    var hasData = data.Any(x => x);
                    if (hasData)
                    {
                        var onPoints = string.Join(", ", data.Select((val, idx) => val ? $"X{idx}" : null).Where(s => s != null));
                        System.Diagnostics.Debug.WriteLine($"[X点读取] ON的点: {(string.IsNullOrEmpty(onPoints) ? "无" : onPoints)}");
                    }
                    return result.Content;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[X点读取] ✗ 失败: {result.Message} (错误码: {result.ErrorCode})");
                    HandleConnectionFailure(
                        $"读取X点 {_config.XStartAddress}×{_config.XCount} 失败: " +
                        (result.IsSuccess
                            ? $"返回长度 {result.Content?.Length ?? 0}，期望 {_config.XCount}"
                            : result.Message),
                        expectedGeneration: connectionGeneration);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[X点读取] ✗ 异常: {ex.Message}");
                HandleConnectionFailure(
                    $"读取X点 {_config.XStartAddress}×{_config.XCount} 异常: {ex.Message}",
                    expectedGeneration: connectionGeneration);
            }

            return new bool[_config.XCount];
        }

        public async Task<bool[]> ReadYPointsAsync()
        {
            var connectionGeneration = Interlocked.Read(ref _connectionGeneration);
            try
            {
                if (!IsConnectionCurrent(connectionGeneration))
                    return new bool[_config.YCount];

                var result = await RunPlcCallAsync(
                    $"ReadBool {_config.YStartAddress}×{_config.YCount}",
                    connectionGeneration,
                    transport => transport.ReadBool(_config.YStartAddress, (ushort)_config.YCount)).ConfigureAwait(false);

                if (result.IsSuccess && result.Content != null &&
                    result.Content.Length == _config.YCount)
                {
                    return result.Content;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[Y点读取] ✗ 失败: {result.Message}");
                    HandleConnectionFailure(
                        $"读取Y点 {_config.YStartAddress}×{_config.YCount} 失败: " +
                        (result.IsSuccess
                            ? $"返回长度 {result.Content?.Length ?? 0}，期望 {_config.YCount}"
                            : result.Message),
                        expectedGeneration: connectionGeneration);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Y点读取] ✗ 异常: {ex.Message}");
                HandleConnectionFailure(
                    $"读取Y点 {_config.YStartAddress}×{_config.YCount} 异常: {ex.Message}",
                    expectedGeneration: connectionGeneration);
            }

            return new bool[_config.YCount];
        }

        public async Task<bool[]> ReadMPointsAsync()
        {
            int totalCount = _config.ActualMCount;
            var connectionGeneration = Interlocked.Read(ref _connectionGeneration);
            try
            {
                if (!IsConnectionCurrent(connectionGeneration))
                    return new bool[totalCount];

                // 使用 MReadBlocks 配置驱动的读取
                if (_config.MReadBlocks != null && _config.MReadBlocks.Count > 0)
                {
                    var combined = new bool[totalCount];

                    if (_config.MAddressList != null && _config.MAddressList.Count > 0)
                    {
                        var valuesByAddress = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                        foreach (var block in _config.MReadBlocks)
                        {
                            var result = await RunPlcCallAsync(
                                $"ReadBool {block.StartAddress}×{block.Count}",
                                connectionGeneration,
                                transport => transport.ReadBool(block.StartAddress, block.Count)).ConfigureAwait(false);
                            if (result.IsSuccess && result.Content != null &&
                                result.Content.Length >= block.Count)
                            {
                                if (!TryParseMAddress(block.StartAddress, out var startNumber))
                                {
                                    System.Diagnostics.Debug.WriteLine($"读取M块 {block.StartAddress}×{block.Count} 失败: 起始地址格式无效");
                                    continue;
                                }

                                int copyLen = block.Count;
                                for (int i = 0; i < copyLen; i++)
                                {
                                    valuesByAddress[$"M{startNumber + i}"] = result.Content[i];
                                }
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine($"读取M块 {block.StartAddress}×{block.Count} 失败: {result.Message}");
                                HandleConnectionFailure(
                                    $"读取M点块 {block.StartAddress}×{block.Count} 失败: " +
                                    (result.IsSuccess
                                        ? $"返回长度 {result.Content?.Length ?? 0}，期望至少 {block.Count}"
                                        : result.Message),
                                    expectedGeneration: connectionGeneration);
                                return new bool[totalCount];
                            }
                        }

                        for (int i = 0; i < Math.Min(totalCount, _config.MAddressList.Count); i++)
                        {
                            if (valuesByAddress.TryGetValue(_config.MAddressList[i], out var value))
                                combined[i] = value;
                        }

                        return combined;
                    }

                    int offset = 0;
                    foreach (var block in _config.MReadBlocks)
                    {
                        var result = await RunPlcCallAsync(
                            $"ReadBool {block.StartAddress}×{block.Count}",
                            connectionGeneration,
                            transport => transport.ReadBool(block.StartAddress, block.Count)).ConfigureAwait(false);
                        if (result.IsSuccess && result.Content != null &&
                            result.Content.Length >= block.Count)
                        {
                            int copyLen = Math.Min(block.Count, totalCount - offset);
                            Array.Copy(result.Content, 0, combined, offset, copyLen);
                            offset += block.Count;
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"读取M块 {block.StartAddress}×{block.Count} 失败: {result.Message}");
                            HandleConnectionFailure(
                                $"读取M点块 {block.StartAddress}×{block.Count} 失败: " +
                                (result.IsSuccess
                                    ? $"返回长度 {result.Content?.Length ?? 0}，期望至少 {block.Count}"
                                    : result.Message),
                                expectedGeneration: connectionGeneration);
                            return new bool[totalCount];
                        }
                    }
                    return combined;
                }

                // 旧逻辑兼容：M2009-M2016 + M2451-M2452
                var result1 = await RunPlcCallAsync(
                    "ReadBool M2009×8",
                    connectionGeneration,
                    transport => transport.ReadBool("M2009", 8)).ConfigureAwait(false);
                if (!result1.IsSuccess || result1.Content == null || result1.Content.Length < 8)
                {
                    HandleConnectionFailure(
                        $"读取M2009×8失败: " +
                        (result1.IsSuccess
                            ? $"返回长度 {result1.Content?.Length ?? 0}，期望至少 8"
                            : result1.Message),
                        expectedGeneration: connectionGeneration);
                    return new bool[totalCount];
                }
                var result2 = await RunPlcCallAsync(
                    "ReadBool M2451×2",
                    connectionGeneration,
                    transport => transport.ReadBool("M2451", 2)).ConfigureAwait(false);
                if (!result2.IsSuccess || result2.Content == null || result2.Content.Length < 2)
                {
                    HandleConnectionFailure(
                        $"读取M2451×2失败: " +
                        (result2.IsSuccess
                            ? $"返回长度 {result2.Content?.Length ?? 0}，期望至少 2"
                            : result2.Message),
                        expectedGeneration: connectionGeneration);
                    return new bool[totalCount];
                }

                if (result1.IsSuccess && result2.IsSuccess)
                {
                    var combined = new bool[10];
                    Array.Copy(result1.Content, 0, combined, 0, 8);
                    Array.Copy(result2.Content, 0, combined, 8, 2);
                    return combined;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"读取M点异常: {ex.Message}");
                HandleConnectionFailure(
                    $"读取M点异常: {ex.Message}",
                    expectedGeneration: connectionGeneration);
            }

            return new bool[totalCount];
        }

        private static bool TryParseMAddress(string address, out int number)
        {
            number = 0;
            if (string.IsNullOrWhiteSpace(address))
                return false;

            address = address.Trim();
            if (!address.StartsWith("M", StringComparison.OrdinalIgnoreCase))
                return false;

            return int.TryParse(address.Substring(1), out number);
        }

        /// <summary>
        /// 读取 C/D/T 等寄存器（配置在 CRegisters 列表中，按地址前缀选择读法）
        /// </summary>
        public async Task<Dictionary<string, int>> ReadCRegistersAsync()
        {
            var values = new Dictionary<string, int>();
            var connectionGeneration = Interlocked.Read(ref _connectionGeneration);
            if (_config.CRegisters == null || _config.CRegisters.Count == 0)
                return values;

            try
            {
                if (!IsConnectionCurrent(connectionGeneration))
                    return values;

                foreach (var reg in _config.CRegisters)
                {
                    var addr = reg.Address?.Trim() ?? "";
                    if (addr.Length == 0)
                        continue;

                    if (addr.StartsWith("D", StringComparison.OrdinalIgnoreCase))
                    {
                        if (reg.PreferInt16)
                        {
                            var result16 = await RunPlcCallAsync(
                                $"ReadInt16 {addr}",
                                connectionGeneration,
                                transport => transport.ReadInt16(addr, 1)).ConfigureAwait(false);
                            if (result16.IsSuccess && result16.Content?.Length >= 1)
                                values[reg.Address] = result16.Content[0];
                            else
                            {
                                System.Diagnostics.Debug.WriteLine($"读取D寄存器 {reg.Address} 失败");
                                HandleConnectionFailure(
                                    $"读取D寄存器 {addr} 失败: " +
                                    (result16.IsSuccess ? "返回空数据" : result16.Message),
                                    expectedGeneration: connectionGeneration,
                                    lane: IoFailureLane.Auxiliary);
                                return values;
                            }
                        }
                        else
                        {
                            var result = await RunPlcCallAsync(
                                $"ReadInt32 {addr}",
                                connectionGeneration,
                                transport => transport.ReadInt32(addr, 1)).ConfigureAwait(false);
                            if (result.IsSuccess && result.Content?.Length >= 1)
                                values[reg.Address] = result.Content[0];
                            else
                            {
                                System.Diagnostics.Debug.WriteLine($"读取D寄存器 {reg.Address} 失败");
                                HandleConnectionFailure(
                                    $"读取D寄存器 {addr} 失败: " +
                                    (result.IsSuccess ? "返回空数据" : result.Message),
                                    expectedGeneration: connectionGeneration,
                                    lane: IoFailureLane.Auxiliary);
                                return values;
                            }
                        }
                    }
                    else if (addr.StartsWith("T", StringComparison.OrdinalIgnoreCase))
                    {
                        var result = await RunPlcCallAsync(
                            $"ReadInt16 {addr}",
                            connectionGeneration,
                            transport => transport.ReadInt16(addr, 1)).ConfigureAwait(false);
                        if (result.IsSuccess && result.Content?.Length >= 1)
                            values[reg.Address] = result.Content[0];
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"读取T寄存器 {reg.Address} 失败");
                            HandleConnectionFailure(
                                $"读取T寄存器 {addr} 失败: " +
                                (result.IsSuccess ? "返回空数据" : result.Message),
                                expectedGeneration: connectionGeneration,
                                lane: IoFailureLane.Auxiliary);
                            return values;
                        }
                    }
                    else
                    {
                        var result = await RunPlcCallAsync(
                            $"ReadInt16 {reg.Address}",
                            connectionGeneration,
                            transport => transport.ReadInt16(reg.Address, 1)).ConfigureAwait(false);
                        if (result.IsSuccess && result.Content?.Length >= 1)
                        {
                            values[reg.Address] = result.Content[0];
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"读取寄存器 {reg.Address} 失败");
                            HandleConnectionFailure(
                                $"读取寄存器 {reg.Address} 失败: " +
                                (result.IsSuccess ? "返回空数据" : result.Message),
                                expectedGeneration: connectionGeneration,
                                lane: IoFailureLane.Auxiliary);
                            return values;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"读取寄存器异常: {ex.Message}");
                HandleConnectionFailure(
                    $"读取寄存器异常: {ex.Message}",
                    expectedGeneration: connectionGeneration,
                    lane: IoFailureLane.Auxiliary);
            }

            return values;
        }

        public async Task<float> ReadTemperatureAsync()
        {
            var result = await ReadTemperatureValueAsync().ConfigureAwait(false);
            return result.HasValue ? result.Value.Value : float.NaN;
        }

        private async Task<TemperatureReadValue?> ReadTemperatureValueAsync()
        {
            var connectionGeneration = Interlocked.Read(ref _connectionGeneration);
            var definition = _config.ResolveActualTemperatureDefinition();
            try
            {
                if (!IsConnectionCurrent(connectionGeneration))
                {
                    System.Diagnostics.Debug.WriteLine("[温度读取] ⚠ 未连接，跳过读取");
                    return null;
                }

                long rawValue;
                switch (definition.DataType)
                {
                    case PlcRegisterDataType.Int16:
                    {
                        var result16 = await RunPlcCallAsync(
                            $"ReadInt16 {definition.Address}",
                            connectionGeneration,
                            transport => transport.ReadInt16(definition.Address, 1)).ConfigureAwait(false);
                        if (!result16.IsSuccess || result16.Content?.Length < 1)
                        {
                            var failure = result16.IsSuccess ? "PLC 返回的温度数据为空" : result16.Message;
                            if (result16.IsSuccess)
                                RecordInvalidTemperatureSample(
                                    connectionGeneration,
                                    0,
                                    failure,
                                    TemperatureSampleQuality.InvalidPayload);
                            HandleConnectionFailure(
                                $"读取Int16温度 {definition.Address} 失败: {failure}",
                                expectedGeneration: connectionGeneration,
                                lane: IoFailureLane.Temperature);
                            return null;
                        }
                        rawValue = result16.Content[0];
                        break;
                    }
                    case PlcRegisterDataType.UInt16:
                    {
                        var result16 = await RunPlcCallAsync(
                            $"ReadInt16 {definition.Address}",
                            connectionGeneration,
                            transport => transport.ReadInt16(definition.Address, 1)).ConfigureAwait(false);
                        if (!result16.IsSuccess || result16.Content?.Length < 1)
                        {
                            var failure = result16.IsSuccess ? "PLC 返回的温度数据为空" : result16.Message;
                            if (result16.IsSuccess)
                                RecordInvalidTemperatureSample(
                                    connectionGeneration,
                                    0,
                                    failure,
                                    TemperatureSampleQuality.InvalidPayload);
                            HandleConnectionFailure(
                                $"读取UInt16温度 {definition.Address} 失败: {failure}",
                                expectedGeneration: connectionGeneration,
                                lane: IoFailureLane.Temperature);
                            return null;
                        }
                        rawValue = (ushort)result16.Content[0];
                        break;
                    }
                    default:
                    {
                        var result32 = await RunPlcCallAsync(
                            $"ReadInt32 {definition.Address}",
                            connectionGeneration,
                            transport => transport.ReadInt32(definition.Address, 1)).ConfigureAwait(false);
                        if (!result32.IsSuccess || result32.Content?.Length < 1)
                        {
                            var failure = result32.IsSuccess ? "PLC 返回的温度数据为空" : result32.Message;
                            if (result32.IsSuccess)
                                RecordInvalidTemperatureSample(
                                    connectionGeneration,
                                    0,
                                    failure,
                                    TemperatureSampleQuality.InvalidPayload);
                            HandleConnectionFailure(
                                $"读取Int32温度 {definition.Address} 失败: {failure}",
                                expectedGeneration: connectionGeneration,
                                lane: IoFailureLane.Temperature);
                            return null;
                        }
                        rawValue = result32.Content[0];
                        break;
                    }
                }

                if (!definition.TryConvert(rawValue, out var value, out var reason))
                {
                    RecordInvalidTemperatureSample(connectionGeneration, rawValue, reason,
                        TemperatureSampleQuality.OutOfRange);
                    return null;
                }

                var previous = _status.LastTemperatureSampleTime == default
                    ? float.NaN
                    : _status.Temperature;
                if (float.IsFinite(previous) &&
                    float.IsFinite(definition.MaximumStep) &&
                    definition.MaximumStep > 0 &&
                    Math.Abs(value - previous) > definition.MaximumStep)
                {
                    reason = $"温度变化 {Math.Abs(value - previous):F3} 超过单次上限 {definition.MaximumStep:F3}";
                    RecordInvalidTemperatureSample(connectionGeneration, rawValue, reason,
                        TemperatureSampleQuality.ExcessiveStep);
                    return null;
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[温度读取] {definition.Address} {definition.DataType}原始值={rawValue}, 除数={definition.Divisor}, 温度={value:F1}°C");
                return new TemperatureReadValue(value, rawValue, definition);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[温度读取] ✗ 异常: {ex.Message}");
                HandleConnectionFailure(
                    $"读取温度 {definition.Address} 异常: {ex.Message}",
                    expectedGeneration: connectionGeneration,
                    lane: IoFailureLane.Temperature);
                return null;
            }
        }

        private void RecordInvalidTemperatureSample(
            long generation,
            long rawValue,
            string reason,
            TemperatureSampleQuality quality)
        {
            lock (_sessionSync)
            {
                if (_activeSession == null || _activeSession.Generation != generation)
                    return;

                _status.LastTemperatureRawValue = rawValue;
                _status.TemperatureQuality = quality;
                _status.LastTemperatureQualityReason = reason ?? "温度样本不可信";
            }

            Views.MainWindow.DbgLog("MitsubishiPlcService:TemperatureRejected", "拒绝不可信温度样本", new
            {
                device = _config.Name,
                _config.IpAddress,
                generation,
                rawValue,
                quality = quality.ToString(),
                reason
            }, "TEMP");
        }

        public async Task<float> ReadThermocoupleAAsync()
        {
            var connectionGeneration = Interlocked.Read(ref _connectionGeneration);
            try
            {
                if (!IsConnectionCurrent(connectionGeneration))
                    return float.NaN;

                // 读取D17-D18作为DINT（32位有符号整数）
                var result = await RunPlcCallAsync(
                    $"ReadInt32 {_config.ThermocoupleAAddress}",
                    connectionGeneration,
                    transport => transport.ReadInt32(_config.ThermocoupleAAddress, 1)).ConfigureAwait(false);

                if (result.IsSuccess && result.Content?.Length >= 1)
                {
                    int dintValue = result.Content[0];
                    float voltage = dintValue / 100.0f;  // 除以100
                    System.Diagnostics.Debug.WriteLine($"[A相电压] D17-D18 DINT值={dintValue}, 电压={voltage:F2}V");
                    return voltage;
                }
                else
                {
                    var failure = result.IsSuccess ? "PLC 返回的 A 相电压数据为空" : result.Message;
                    HandleConnectionFailure(
                        $"读取热电偶A {_config.ThermocoupleAAddress} 失败: {failure}",
                        expectedGeneration: connectionGeneration,
                        lane: IoFailureLane.Auxiliary);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"读取热电偶A异常: {ex.Message}");
                HandleConnectionFailure(
                    $"读取热电偶A {_config.ThermocoupleAAddress} 异常: {ex.Message}",
                    expectedGeneration: connectionGeneration,
                    lane: IoFailureLane.Auxiliary);
            }

            return float.NaN;
        }

        public async Task<float> ReadThermocoupleBAsync()
        {
            var connectionGeneration = Interlocked.Read(ref _connectionGeneration);
            try
            {
                if (!IsConnectionCurrent(connectionGeneration))
                    return float.NaN;

                // 读取D19-D20作为DINT（32位有符号整数）
                var result = await RunPlcCallAsync(
                    $"ReadInt32 {_config.ThermocoupleBAddress}",
                    connectionGeneration,
                    transport => transport.ReadInt32(_config.ThermocoupleBAddress, 1)).ConfigureAwait(false);

                if (result.IsSuccess && result.Content?.Length >= 1)
                {
                    int dintValue = result.Content[0];
                    float voltage = dintValue / 100.0f;  // 除以100
                    System.Diagnostics.Debug.WriteLine($"[B相电压] D19-D20 DINT值={dintValue}, 电压={voltage:F2}V");
                    return voltage;
                }
                else
                {
                    var failure = result.IsSuccess ? "PLC 返回的 B 相电压数据为空" : result.Message;
                    HandleConnectionFailure(
                        $"读取热电偶B {_config.ThermocoupleBAddress} 失败: {failure}",
                        expectedGeneration: connectionGeneration,
                        lane: IoFailureLane.Auxiliary);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"读取热电偶B异常: {ex.Message}");
                HandleConnectionFailure(
                    $"读取热电偶B {_config.ThermocoupleBAddress} 异常: {ex.Message}",
                    expectedGeneration: connectionGeneration,
                    lane: IoFailureLane.Auxiliary);
            }

            return float.NaN;
        }

        public async Task<float> ReadThermocoupleCAsync()
        {
            var connectionGeneration = Interlocked.Read(ref _connectionGeneration);
            try
            {
                if (!IsConnectionCurrent(connectionGeneration))
                    return float.NaN;

                // 读取D21-D22作为DINT（32位有符号整数）
                var result = await RunPlcCallAsync(
                    $"ReadInt32 {_config.ThermocoupleCAddress}",
                    connectionGeneration,
                    transport => transport.ReadInt32(_config.ThermocoupleCAddress, 1)).ConfigureAwait(false);

                if (result.IsSuccess && result.Content?.Length >= 1)
                {
                    int dintValue = result.Content[0];
                    float voltage = dintValue / 100.0f;  // 除以100
                    System.Diagnostics.Debug.WriteLine($"[C相电压] D21-D22 DINT值={dintValue}, 电压={voltage:F2}V");
                    return voltage;
                }
                else
                {
                    var failure = result.IsSuccess ? "PLC 返回的 C 相电压数据为空" : result.Message;
                    HandleConnectionFailure(
                        $"读取热电偶C {_config.ThermocoupleCAddress} 失败: {failure}",
                        expectedGeneration: connectionGeneration,
                        lane: IoFailureLane.Auxiliary);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"读取热电偶C异常: {ex.Message}");
                HandleConnectionFailure(
                    $"读取热电偶C {_config.ThermocoupleCAddress} 异常: {ex.Message}",
                    expectedGeneration: connectionGeneration,
                    lane: IoFailureLane.Auxiliary);
            }

            return float.NaN;
        }

        public async Task<PlcStatus> ReadAllAsync()
        {
            var connectionGeneration = Interlocked.Read(ref _connectionGeneration);
            var session = GetActiveSession(connectionGeneration);
            if (session == null)
                return null;
            var failureVersion = Volatile.Read(ref session.IoFailureVersion);
            try
            {
                if (!IsConnectionCurrent(connectionGeneration))
                    return null;

                // 调用可并行创建，但底层由当前会话自己的 IoLock 串行访问同一条 TCP 连接。
                var tasks = new List<Task>();
                var xTask = ReadXPointsAsync(); tasks.Add(xTask);
                var yTask = ReadYPointsAsync(); tasks.Add(yTask);
                var mTask = ReadMPointsAsync(); tasks.Add(mTask);
                var tempTask = ReadTemperatureAsync(); tasks.Add(tempTask);

                Task<float> thermoATask = null, thermoBTask = null, thermoCTask = null;
                if (_config.HasVoltage)
                {
                    thermoATask = ReadThermocoupleAAsync(); tasks.Add(thermoATask);
                    thermoBTask = ReadThermocoupleBAsync(); tasks.Add(thermoBTask);
                    thermoCTask = ReadThermocoupleCAsync(); tasks.Add(thermoCTask);
                }

                Task<Dictionary<string, int>> cTask = null;
                if (_config.HasCRegisters)
                {
                    cTask = ReadCRegistersAsync(); tasks.Add(cTask);
                }

                await Task.WhenAll(tasks);

                var xValues = await xTask;
                var yValues = await yTask;
                var mValues = await mTask;
                var temperature = await tempTask;
                if (!float.IsFinite(temperature))
                    return null;

                var thermoA = thermoATask == null ? 0f : await thermoATask;
                var thermoB = thermoBTask == null ? 0f : await thermoBTask;
                var thermoC = thermoCTask == null ? 0f : await thermoCTask;
                var cValues = cTask == null ? null : await cTask;

                lock (_sessionSync)
                {
                    if (!ReferenceEquals(_activeSession, session) ||
                        _connectionGeneration != session.Generation ||
                        session.IoFailureVersion != failureVersion)
                        return null;

                    session.GeneralFailures = 0;
                    session.TemperatureFailures = 0;
                    session.AuxiliaryFailures = 0;
                    _status.X = xValues;
                    _status.Y = yValues;
                    _status.M = mValues;
                    _status.Temperature = temperature;

                    if (_config.HasVoltage)
                    {
                        _status.ThermocoupleA = thermoA;
                        _status.ThermocoupleB = thermoB;
                        _status.ThermocoupleC = thermoC;
                    }

                    if (cValues != null)
                        _status.CValues = cValues;

                    _status.LastUpdateTime = DateTime.Now;
                    return _status;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"读取全部数据失败: {ex.Message}");
                return null;
            }
        }

        public void StartAcquisition()
        {
            long connectionGeneration;
            lock (_acquisitionSync)
            {
                connectionGeneration = Interlocked.Read(ref _connectionGeneration);
                if (!IsConnectionCurrent(connectionGeneration))
                    throw new InvalidOperationException("请先连接PLC");

                // 自动重连后定时器仍然存在，但新连接代必须重新开始新鲜度计时。
                if (!ResetTemperatureFreshness(connectionGeneration))
                    throw new InvalidOperationException("PLC 连接已在启动采集前失效");

                var needsNewLoop = !_isAcquiring ||
                                   _acquisitionConnectionGeneration != connectionGeneration ||
                                   _acquisitionLoopTask == null ||
                                   _acquisitionLoopTask.IsCompleted;
                if (needsNewLoop)
                {
                    Volatile.Write(ref _acquisitionConnectionGeneration, connectionGeneration);
                    Volatile.Write(
                        ref _activeAcquisitionToken,
                        Interlocked.Increment(ref _nextAcquisitionToken));
                    // 不清零正在执行的 lane 令牌。旧循环可能仍在第三方同步
                    // I/O 中；令牌由各 lane 的 finally 释放，避免 Stop→Start
                    // 在同一 PLC 会话上制造并发请求。新代会在旧 lane 退出后
                    // 自然取得令牌，且所有结果仍会经过 acquisition token 校验。
                    _isAcquiring = true;
                    var oldCts = _acquisitionCts;
                    oldCts?.Cancel();
                    _acquisitionCts = new CancellationTokenSource();
                    var schedulerCts = _acquisitionCts;
                    var generation = connectionGeneration;
                    var acquisitionToken = Volatile.Read(ref _activeAcquisitionToken);
                    _acquisitionLoopTask = Task.Run(
                        () => RunAcquisitionLoopAsync(generation, acquisitionToken, schedulerCts.Token),
                        schedulerCts.Token);
                }
            }
        }

        public void StopAcquisition()
        {
            CancellationTokenSource acquisitionCts;
            lock (_acquisitionSync)
            {
                _isAcquiring = false;
                Volatile.Write(ref _acquisitionConnectionGeneration, 0);
                Volatile.Write(ref _activeAcquisitionToken, 0);
                // 不清零 lane 令牌：Stop 只撤销调度资格，正在执行的 lane
                // 必须在自身 finally 中释放令牌，防止立即 Start 时并发重入。
                acquisitionCts = _acquisitionCts;
                _acquisitionCts = null;
            }
            try { acquisitionCts?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private async Task RunAcquisitionLoopAsync(
            long connectionGeneration,
            long acquisitionToken,
            CancellationToken cancellationToken)
        {
            var startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            var nextTemperature = startTimestamp +
                (long)(GetAcquisitionStartOffset().TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
            var nextXy = nextTemperature;
            var temperatureInterval = Math.Max(100, _config.TemperatureInterval);
            var xyInterval = Math.Max(100, _config.XYInterval);
            var temperatureTicks = (long)(temperatureInterval / 1000d * System.Diagnostics.Stopwatch.Frequency);
            var xyTicks = (long)(xyInterval / 1000d * System.Diagnostics.Stopwatch.Frequency);
            Task temperatureTask = null;
            Task xyTask = null;
            Task auxiliaryTask = null;
            var auxiliaryInterval = Math.Max(1000, temperatureInterval);
            var auxiliaryTicks = Math.Max(1L,
                (long)(auxiliaryInterval / 1000d * System.Diagnostics.Stopwatch.Frequency));
            var nextAuxiliary = nextTemperature + auxiliaryTicks;

            try
            {
                System.Diagnostics.Debug.WriteLine($"[采集调度] {_config.Name} 启动 generation={connectionGeneration}, token={acquisitionToken}, offsetMs={GetAcquisitionStartOffset().TotalMilliseconds}");
                while (!cancellationToken.IsCancellationRequested &&
                       IsAcquisitionCurrent(connectionGeneration, acquisitionToken))
                {
                    var now = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (temperatureTask?.IsCompleted == true) temperatureTask = null;
                    if (xyTask?.IsCompleted == true) xyTask = null;
                    if (auxiliaryTask?.IsCompleted == true) auxiliaryTask = null;

                    // 温度是最高优先级：到期或即将到期时不再启动新的 XY 轮。
                    if (temperatureTask == null && now >= nextTemperature)
                    {
                        System.Diagnostics.Debug.WriteLine($"[采集调度] {_config.Name} 发起温度轮 generation={connectionGeneration}, token={acquisitionToken}");
                        temperatureTask = RunTemperatureLaneAsync(connectionGeneration, acquisitionToken);
                        do { nextTemperature += temperatureTicks; }
                        while (nextTemperature <= now);
                    }

                    var temperatureDueSoon = nextTemperature - now <=
                        (long)(Math.Min(100, Math.Max(10, temperatureInterval / 5d)) / 1000d * System.Diagnostics.Stopwatch.Frequency);
                    var delayed = IsTemperatureSampleDelayed(out _);
                    var effectiveXyInterval = delayed
                        ? Math.Max(xyInterval * 3, 3000)
                        : xyInterval;
                    xyTicks = Math.Max(1, (long)(effectiveXyInterval / 1000d * System.Diagnostics.Stopwatch.Frequency));

                    if (xyTask == null && now >= nextXy && !temperatureDueSoon)
                    {
                        xyTask = RunIoRoundAsync(connectionGeneration, acquisitionToken);
                        do { nextXy += xyTicks; }
                        while (nextXy <= now);
                    }

                    if (auxiliaryTask == null && xyTask == null && temperatureTask == null &&
                        now >= nextAuxiliary && !temperatureDueSoon)
                    {
                        var auxiliaryTemperature = _status.Temperature;
                        auxiliaryTask = RunAuxiliaryLaneAsync(
                            connectionGeneration,
                            acquisitionToken,
                            auxiliaryTemperature);
                        do { nextAuxiliary += auxiliaryTicks; }
                        while (nextAuxiliary <= now);
                    }

                    await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 正常停止。
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[采集调度] {_config.Name} 调度循环异常: {ex.Message}");
            }
        }

        private TimeSpan GetAcquisitionStartOffset()
        {
            // 默认四台 PLC 的地址尾数为 5/10/15/20；错开 0/250/500/750ms。
            // 无法解析时使用稳定的设备名哈希，并限制在 1 秒内。
            if (System.Net.IPAddress.TryParse(_config.IpAddress, out var address))
            {
                var bytes = address.GetAddressBytes();
                if (bytes.Length == 4)
                    return TimeSpan.FromMilliseconds((bytes[3] % 4) * 250);
            }

            return TimeSpan.FromMilliseconds(
                (Math.Abs((_config.Name ?? "").GetHashCode()) % 4) * 250);
        }

        private async Task RunIoRoundAsync(long connectionGeneration, long acquisitionToken)
        {
            if (Interlocked.CompareExchange(ref _xyReadToken, acquisitionToken, 0) != 0)
                return;

            try
            {
                await RunXYRoundAsync(connectionGeneration, acquisitionToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.CompareExchange(ref _xyReadToken, 0, acquisitionToken);
            }
        }

        private async Task RunTemperatureLaneAsync(
            long connectionGeneration,
            long acquisitionToken)
        {
            // Stop→Start 或自动重连时，旧采集循环可能尚未从 await 返回。
            // 令牌必须真正占用，避免旧代与新代同时向同一 PLC 发起温度请求。
            if (Interlocked.CompareExchange(ref _temperatureReadToken, acquisitionToken, 0) != 0)
                return;

            try
            {
                await RunTemperatureRoundAsync(
                    connectionGeneration,
                    acquisitionToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.CompareExchange(
                    ref _temperatureReadToken,
                    0,
                    acquisitionToken);
            }
        }

        private async Task RunAuxiliaryLaneAsync(
            long connectionGeneration,
            long acquisitionToken,
            float temperature)
        {
            if (Interlocked.CompareExchange(ref _auxiliaryReadToken, acquisitionToken, 0) != 0)
                return;

            try
            {
                await RunAuxiliaryRoundAsync(
                    connectionGeneration,
                    acquisitionToken,
                    temperature).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.CompareExchange(ref _auxiliaryReadToken, 0, acquisitionToken);
            }
        }

        private async Task RunXYRoundAsync(long connectionGeneration, long acquisitionToken)
        {
            if (!IsAcquisitionCurrent(connectionGeneration, acquisitionToken))
                return;

            try
            {
                    var session = GetActiveSession(connectionGeneration);
                    if (!IsAcquisitionCurrent(connectionGeneration, acquisitionToken) || session == null)
                        return;
                    var failureVersion = Volatile.Read(ref session.IoFailureVersion);

                    var xValues = await ReadXPointsAsync();
                    var yValues = await ReadYPointsAsync();
                    var mValues = await ReadMPointsAsync();

                    // 本轮任一读失败时数据不完整（失败的读返回全 false 数组），
                    // 即使容错期内连接还保留，也必须跳过比较，否则会比出一堆假"IO变化"
                    bool[] previousX;
                    bool[] previousY;
                    bool[] previousM;
                    bool isFirstSnapshot;
                    lock (_sessionSync)
                    {
                        if (!ReferenceEquals(_activeSession, session) ||
                            _connectionGeneration != session.Generation ||
                            session.IoFailureVersion != failureVersion ||
                            !IsAcquisitionCurrent(connectionGeneration, acquisitionToken))
                            return;

                        session.GeneralFailures = 0;
                        isFirstSnapshot = _ioBaselineGeneration != session.Generation;
                        previousX = isFirstSnapshot ? (bool[])xValues.Clone() : _lastX;
                        previousY = isFirstSnapshot ? (bool[])yValues.Clone() : _lastY;
                        previousM = isFirstSnapshot ? (bool[])mValues.Clone() : _lastM;
                        _lastX = (bool[])xValues.Clone();
                        _lastY = (bool[])yValues.Clone();
                        _lastM = (bool[])mValues.Clone();
                        _ioBaselineGeneration = session.Generation;
                        _status.X = xValues;
                        _status.Y = yValues;
                        _status.M = mValues;
                        _status.LastUpdateTime = DateTime.Now;
                    }

                // 每个新连接代的首帧只建立真实基线，不能把“默认全 false → 当前状态”写成操作日志。
                if (isFirstSnapshot)
                {
                    System.Diagnostics.Debug.WriteLine($"[数据采集] 首次读取成功 - X点数:{xValues.Length}, Y点数:{yValues.Length}, M点数:{mValues.Length}");
                    return;
                }

                // 检测X点变化（三菱X为八进制：下标0-7→X0-X7，8→X10，9→X11…）
                for (int i = 0; i < Math.Min(xValues.Length, previousX.Length); i++)
                {
                    if (xValues[i] != previousX[i])
                    {
                        var label = _config.GetXLabel(i);
                        var evt = new StateChangeEvent
                        {
                            PointType = "X",
                            PointIndex = i,
                            Address = _config.GetXAddress(i),
                            OldValue = previousX[i],
                            NewValue = xValues[i],
                            EventTime = DateTime.Now,
                            PointLabel = label
                        };
                        SafeEventDispatcher.Invoke(
                            this,
                            StateChanged,
                            evt,
                            ex => System.Diagnostics.Debug.WriteLine(
                                $"[IO变化] X事件订阅者异常: {_config.Name} - {ex.Message}"));
                        System.Diagnostics.Debug.WriteLine($"[IO变化] {label} ({evt.Address}): {previousX[i]} → {xValues[i]}");
                    }
                }

                // 检测Y点变化（三菱Y为八进制：下标0-7→Y0-Y7，8→Y10…）
                for (int i = 0; i < Math.Min(yValues.Length, previousY.Length); i++)
                {
                    if (yValues[i] != previousY[i])
                    {
                        var label = _config.GetYLabel(i);
                        var evt = new StateChangeEvent
                        {
                            PointType = "Y",
                            PointIndex = i,
                            Address = _config.GetYAddress(i),
                            OldValue = previousY[i],
                            NewValue = yValues[i],
                            EventTime = DateTime.Now,
                            PointLabel = label
                        };
                        SafeEventDispatcher.Invoke(
                            this,
                            StateChanged,
                            evt,
                            ex => System.Diagnostics.Debug.WriteLine(
                                $"[IO变化] Y事件订阅者异常: {_config.Name} - {ex.Message}"));
                        System.Diagnostics.Debug.WriteLine($"[IO变化] {label} ({evt.Address}): {previousY[i]} → {yValues[i]}");
                    }
                }

                // 检测M点变化（M 地址来自每台设备的 MAddressList，可能不连续）
                for (int i = 0; i < Math.Min(mValues.Length, previousM.Length); i++)
                {
                    if (mValues[i] != previousM[i])
                    {
                        var label = _config.GetMLabel(i);
                        var evt = new StateChangeEvent
                        {
                            PointType = "M",
                            PointIndex = i,
                            Address = _config.GetMAddress(i),
                            OldValue = previousM[i],
                            NewValue = mValues[i],
                            EventTime = DateTime.Now,
                            PointLabel = label
                        };
                        SafeEventDispatcher.Invoke(
                            this,
                            StateChanged,
                            evt,
                            ex => System.Diagnostics.Debug.WriteLine(
                                $"[IO变化] M事件订阅者异常: {_config.Name} - {ex.Message}"));
                        System.Diagnostics.Debug.WriteLine($"[IO变化] {label} ({evt.Address}): {previousM[i]} → {mValues[i]}");
                    }
                }

            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"XY采集异常: {ex.Message}");
            }
        }

        private async Task RunTemperatureRoundAsync(long connectionGeneration, long acquisitionToken)
        {
            try
            {
                if (!IsAcquisitionCurrent(connectionGeneration, acquisitionToken))
                    return;

                // 实际温度是安全关键数据：只要这一条读取成功就立即提交并通知 UI/入库。
                // 目标温度、三相电压或 C/T/D 辅助寄存器失败，不得再把真实温度整轮丢弃。
                var temperatureResult = await ReadTemperatureValueAsync();
                if (!IsAcquisitionCurrent(connectionGeneration, acquisitionToken) ||
                    !temperatureResult.HasValue)
                    return;

                var temperature = temperatureResult.Value.Value;
                System.Diagnostics.Debug.WriteLine($"[温度采集] {_config.Name} 读取结果 {temperature:F1} generation={connectionGeneration}, token={acquisitionToken}");

                if (!CommitPrimaryTemperatureSample(
                        connectionGeneration,
                        acquisitionToken,
                        temperature,
                        temperatureResult.Value.RawValue,
                        temperatureResult.Value.Definition))
                    return;

                System.Diagnostics.Debug.WriteLine($"[温度采集] {_config.Name} 实际温度:{temperature:F1}°C");

                // 辅助寄存器由采集调度器的低优先级 lane 负责，不能在这里
                // 直接 Task.Run 抢占下一次实际温度读取。
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"温度采集异常: {ex.Message}");
            }
        }

        private bool CommitPrimaryTemperatureSample(
            long connectionGeneration,
            long acquisitionToken,
            float temperature,
            long rawValue,
            TemperatureRegisterDefinition definition)
        {
            TemperatureSampleEventArgs sampleEvent;
            lock (_sessionSync)
            {
                var session = _activeSession;
                if (!_isConnected || session == null ||
                    session.Generation != connectionGeneration ||
                    !IsAcquisitionCurrent(connectionGeneration, acquisitionToken) ||
                    !float.IsFinite(temperature))
                    return false;

                session.TemperatureFailures = 0;
                var sampleTime = DateTime.Now;
                var sampleSequence = Interlocked.Increment(ref _temperatureSampleSequence);
                _status.Temperature = temperature;
                _status.LastUpdateTime = sampleTime;
                _status.LastTemperatureSampleTime = sampleTime;
                _status.LastTemperatureSampleSequence = sampleSequence;
                _status.LastTemperatureConnectionGeneration = connectionGeneration;
                _status.LastTemperatureRawValue = rawValue;
                _status.TemperatureQuality = TemperatureSampleQuality.Valid;
                _status.LastTemperatureQualityReason = "";
                Interlocked.Exchange(
                    ref _lastTemperatureSampleTimestamp,
                    System.Diagnostics.Stopwatch.GetTimestamp());

                UpdateTemperatureAlarmState(temperature);

                var auxiliarySampleTime = _status.LastAuxiliarySampleTime;
                var auxiliaryMaxAge = TimeSpan.FromMilliseconds(
                    Math.Max(5000d, _config.TemperatureInterval * 2.5d));
                var hasFreshAuxiliaryData = auxiliarySampleTime != default &&
                                            auxiliarySampleTime <= sampleTime &&
                                            sampleTime - auxiliarySampleTime <= auxiliaryMaxAge;

                // 在同一锁内制作快照，避免事件字段来自不同辅助采集时刻。
                sampleEvent = new TemperatureSampleEventArgs
                {
                    Temperature = temperature,
                    TargetTemperature = _status.TargetTemperature,
                    ThermocoupleA = _status.ThermocoupleA,
                    ThermocoupleB = _status.ThermocoupleB,
                    ThermocoupleC = _status.ThermocoupleC,
                    IsAbnormal = _status.IsAlarm,
                    SampleTime = sampleTime,
                    AuxiliarySampleTime = auxiliarySampleTime == default
                        ? null
                        : auxiliarySampleTime,
                    HasFreshAuxiliaryData = hasFreshAuxiliaryData,
                    DeviceName = _config.Name,
                    ConnectionGeneration = connectionGeneration,
                    SampleSequence = sampleSequence,
                    RawValue = rawValue,
                    RawDataType = definition?.DataType ?? PlcRegisterDataType.Int32,
                    Quality = TemperatureSampleQuality.Valid
                };
            }

            // 只有真正取得有效温度样本才说明连接已恢复，不能在 TCP 成功时清零失败状态。
            Interlocked.Exchange(ref _connectionFailureCount, 0);
            SetConnectionPhase(
                PlcConnectionPhase.OnlineFresh,
                "本代首个有效温度样本已到达",
                connectionGeneration);

            // 温度采样事件（外部订阅者负责同步主界面并入队数据库）。
            // 辅助数据明确携带自己的时间与新鲜度；辅助失败不能阻止主温度发布。
            SafeEventDispatcher.Invoke(
                this,
                TemperatureSampled,
                sampleEvent,
                ex => System.Diagnostics.Debug.WriteLine(
                    $"[温度采集] TemperatureSampled 订阅者异常: {_config.Name} - {ex.Message}"));

            return true;
        }

        private async Task RunAuxiliaryRoundAsync(
            long connectionGeneration,
            long acquisitionToken,
            float temperature)
        {
            try
            {
                if (!IsAcquisitionCurrent(connectionGeneration, acquisitionToken))
                    return;

                await TryUpdateAuxiliaryTelemetryAsync(
                    connectionGeneration,
                    acquisitionToken,
                    temperature);

                if (_config.HasVoltage)
                    System.Diagnostics.Debug.WriteLine($"[辅助采集] {_config.Name} 目标:{_status.TargetTemperature:F1}°C, A相:{_status.ThermocoupleA:F3}V, B相:{_status.ThermocoupleB:F3}V, C相:{_status.ThermocoupleC:F3}V");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"辅助采集异常: {ex.Message}");
            }
            finally
            {
                Interlocked.CompareExchange(
                    ref _auxiliaryReadToken,
                    0,
                    acquisitionToken);
            }
        }

        private async Task TryUpdateAuxiliaryTelemetryAsync(
            long connectionGeneration,
            long acquisitionToken,
            float temperature)
        {
            var session = GetActiveSession(connectionGeneration);
            if (session == null)
                return;

            var failureVersion = Volatile.Read(ref session.IoFailureVersion);
            var targetTemperature = await ReadTargetTemperatureAsync();
            if (!float.IsFinite(targetTemperature))
                return;

            var thermoA = 0f;
            var thermoB = 0f;
            var thermoC = 0f;
            if (_config.HasVoltage)
            {
                thermoA = await ReadThermocoupleAAsync();
                if (!float.IsFinite(thermoA))
                    return;

                thermoB = await ReadThermocoupleBAsync();
                if (!float.IsFinite(thermoB))
                    return;

                thermoC = await ReadThermocoupleCAsync();
                if (!float.IsFinite(thermoC))
                    return;
            }

            Dictionary<string, int> cValues = null;
            if (_config.HasCRegisters)
            {
                cValues = await ReadCRegistersAsync();
            }

            lock (_sessionSync)
            {
                if (!ReferenceEquals(_activeSession, session) ||
                    _connectionGeneration != session.Generation ||
                    session.IoFailureVersion != failureVersion ||
                    !IsAcquisitionCurrent(connectionGeneration, acquisitionToken))
                    return;

                _status.TargetTemperature = targetTemperature;
                if (_config.HasVoltage)
                {
                    _status.ThermocoupleA = thermoA;
                    _status.ThermocoupleB = thermoB;
                    _status.ThermocoupleC = thermoC;
                }
                if (cValues != null)
                    _status.CValues = cValues;
                _status.LastAuxiliarySampleTime = DateTime.Now;
                Interlocked.Exchange(
                    ref _lastAuxiliarySampleTimestamp,
                    System.Diagnostics.Stopwatch.GetTimestamp());
                session.AuxiliaryFailures = 0;
                UpdateTemperatureAlarmState(temperature);
            }
        }

        private void UpdateTemperatureAlarmState(float temperature)
        {
            // 报警阈值：使用 PlcConfig.TemperatureThreshold（设备详情页设置）。
            float threshold = float.IsFinite(_config.TemperatureThreshold) &&
                              _config.TemperatureThreshold >= 0
                ? _config.TemperatureThreshold
                : 90f;
            bool isAlarm = temperature > threshold;
            bool isSsrFault = false;

            var auxiliaryMaxAge = TimeSpan.FromMilliseconds(
                Math.Max(5000d, _config.TemperatureInterval * 2.5d));
            var auxiliaryTimestamp = Interlocked.Read(ref _lastAuxiliarySampleTimestamp);
            var hasFreshVoltage = auxiliaryTimestamp > 0 &&
                                  System.Diagnostics.Stopwatch.GetElapsedTime(auxiliaryTimestamp) <= auxiliaryMaxAge;
            if (_config.HasVoltage && hasFreshVoltage)
            {
                float avgVoltage =
                    (_status.ThermocoupleA + _status.ThermocoupleB + _status.ThermocoupleC) / 3f;
                bool hasVoltageOutput = avgVoltage > 0.1f;

                int pidIdx = -1;
                for (int yi = 0; yi < _config.YCount; yi++)
                {
                    if (_config.GetYAddress(yi) == "Y17")
                    {
                        pidIdx = yi;
                        break;
                    }
                }

                bool pidOutput = pidIdx >= 0 && pidIdx < _status.Y.Length && _status.Y[pidIdx];
                isSsrFault = hasVoltageOutput && !pidOutput && temperature > threshold + 5;
            }

            _status.IsAlarm = isAlarm;
            _status.IsSsrFault = isSsrFault;

        }

        private async Task<float> ReadTargetTemperatureAsync()
        {
            var connectionGeneration = Interlocked.Read(ref _connectionGeneration);
            var definition = _config.ResolveTargetTemperatureDefinition();
            try
            {
                if (!IsConnectionCurrent(connectionGeneration))
                    return float.NaN;

                if (definition.DataType is PlcRegisterDataType.Int16 or PlcRegisterDataType.UInt16)
                {
                    var result16 = await RunPlcCallAsync(
                        $"ReadInt16 {definition.Address}",
                        connectionGeneration,
                        transport => transport.ReadInt16(definition.Address, 1)).ConfigureAwait(false);
                    if (result16.IsSuccess && result16.Content?.Length >= 1)
                    {
                        long rawValue = definition.DataType == PlcRegisterDataType.UInt16
                            ? (ushort)result16.Content[0]
                            : result16.Content[0];
                        if (definition.TryConvert(rawValue, out var targetTemp, out var reason))
                        {
                            System.Diagnostics.Debug.WriteLine($"[目标温度] {definition.Address} {definition.DataType}值={rawValue}, 除数={definition.Divisor}, 目标温度={targetTemp:F1}°C");
                            return targetTemp;
                        }

                        Views.MainWindow.DbgLog("MitsubishiPlcService:TargetTemperatureRejected", "拒绝不可信目标温度", new
                        {
                            device = _config.Name,
                            _config.IpAddress,
                            address = definition.Address,
                            rawValue,
                            reason
                        }, "TEMP");
                    }
                    else
                    {
                        HandleConnectionFailure(
                            $"读取Word目标温度 {definition.Address} 失败: {result16.Message}",
                            expectedGeneration: connectionGeneration,
                            lane: IoFailureLane.Auxiliary);
                    }
                }
                else
                {
                    var result = await RunPlcCallAsync(
                        $"ReadInt32 {definition.Address}",
                        connectionGeneration,
                        transport => transport.ReadInt32(definition.Address, 1)).ConfigureAwait(false);
                    if (result.IsSuccess && result.Content?.Length >= 1)
                    {
                        int dintValue = result.Content[0];
                        if (definition.TryConvert(dintValue, out var targetTemp, out var reason))
                        {
                            System.Diagnostics.Debug.WriteLine($"[目标温度] {definition.Address} Int32值={dintValue}, 除数={definition.Divisor}, 目标温度={targetTemp:F1}°C");
                            return targetTemp;
                        }

                        Views.MainWindow.DbgLog("MitsubishiPlcService:TargetTemperatureRejected", "拒绝不可信目标温度", new
                        {
                            device = _config.Name,
                            _config.IpAddress,
                            address = definition.Address,
                            rawValue = dintValue,
                            reason
                        }, "TEMP");
                    }
                    else
                    {
                        HandleConnectionFailure(
                            $"读取DINT目标温度 {definition.Address} 失败: {result.Message}",
                            expectedGeneration: connectionGeneration,
                            lane: IoFailureLane.Auxiliary);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"读取目标温度异常: {ex.Message}");
                HandleConnectionFailure(
                    $"读取目标温度 {definition.Address} 异常: {ex.Message}",
                    expectedGeneration: connectionGeneration,
                    lane: IoFailureLane.Auxiliary);
            }
            return float.NaN;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
                return;

            SetDisconnectedState("服务已释放", notify: false);
            StopAcquisition();

            // 不释放连接锁：已触发但尚未退出的任务可能仍在 Wait/Release。
            // 每代会话对象会在迟到任务退出后随 GC 回收。
        }
    }

    /// <summary>
    /// 一次温度采样事件参数（每个温度定时器周期触发一次）
    /// </summary>
    public class TemperatureSampleEventArgs : EventArgs
    {
        public float Temperature { get; set; }
        public float TargetTemperature { get; set; }
        public float ThermocoupleA { get; set; }
        public float ThermocoupleB { get; set; }
        public float ThermocoupleC { get; set; }
        public bool IsAbnormal { get; set; }
        public DateTime SampleTime { get; set; } = DateTime.Now;
        public DateTime? AuxiliarySampleTime { get; set; }
        public bool HasFreshAuxiliaryData { get; set; }
        public string DeviceName { get; set; } = "";
        public long ConnectionGeneration { get; set; }
        public long SampleSequence { get; set; }
        public long RawValue { get; set; }
        public PlcRegisterDataType RawDataType { get; set; }
        public TemperatureSampleQuality Quality { get; set; } = TemperatureSampleQuality.Valid;
        public string QualityReason { get; set; } = "";
    }
}
