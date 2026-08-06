using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
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
            public long IoFailureVersion;
            public int GeneralFailures;
            public int TemperatureFailures;
            public int AuxiliaryFailures;
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
        private volatile bool _isConnected;

        private System.Timers.Timer _xyTimer;
        private System.Timers.Timer _tempTimer;
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
        private readonly SemaphoreSlim _connectLock = new(1, 1);
        private readonly object _sessionSync = new();
        private readonly object _acquisitionSync = new();
        private PlcSession _activeSession;
        private long _connectionGeneration;
        private long _lastSlowIoLogMs;
        private long _lastIoFailureLogMs;
        private long _lastIoTimeoutLogMs;
        private int _isDisposed;

        // 无线网桥偶发丢一两个包很常见，连续失败达到阈值才判离线。
        // 失败计数保存在每代 PlcSession 内并按采集通道分开，旧代/其他通道的成功不能清零本通道故障。
        private const int OfflineAfterConsecutiveFailures = 2;

        public event EventHandler<bool> ConnectionStateChanged;
        public event EventHandler<StateChangeEvent> StateChanged;

        /// <summary>
        /// 一次温度采样完成事件（每次 TemperatureInterval 触发一次，包含温度与三相电压）
        /// </summary>
        public event EventHandler<TemperatureSampleEventArgs> TemperatureSampled;

        public PlcStatus CurrentStatus => _status;
        public PlcConfig Config => _config;
        public bool IsAcquiring => _isAcquiring;

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

            // TODO: 钉钉报警暂时禁用，后续启用时取消注释
            // if (!string.IsNullOrEmpty(_config.DingTalkWebhook))
            // {
            //     DingTalkService.Instance.SetWebhook(_config.DingTalkWebhook);
            // }
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
                LogSlowIo(operationName, sw.ElapsedMilliseconds);

                // 外部订阅者不能在持有本代 I/O 锁时同步回调，避免未来订阅者
                // 再进入连接 API 后形成新的锁循环。
                if (notifyDisconnectedAfterUnlock)
                {
                    try
                    {
                        ConnectionStateChanged?.Invoke(this, false);
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
            _ = task.ContinueWith(
                completed =>
                {
                    if (completed.IsFaulted)
                        _ = completed.Exception;

                    System.Diagnostics.Debug.WriteLine(
                        $"[PLC会话] 旧代迟到任务已结束: generation={session.Generation}, operation={operationName}");
                    ScheduleSessionClose(session, $"迟到任务终结清理: {operationName}", terminalPass: true);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void LogSlowIo(string operationName, long elapsedMs)
        {
            if (elapsedMs < 1000) return;

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var lastMs = Interlocked.Read(ref _lastSlowIoLogMs);
            if (nowMs - lastMs < 5000) return;
            Interlocked.Exchange(ref _lastSlowIoLogMs, nowMs);

            Views.MainWindow.DbgLog("MitsubishiPlcService:SlowIo", "PLC 请求耗时过长", new
            {
                device = _config.Name,
                _config.IpAddress,
                operationName,
                elapsedMs,
                generation = Interlocked.Read(ref _connectionGeneration)
            }, "PLC_IO");
        }

        private void LogIoFailure(string reason, int failures, bool immediate)
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var lastMs = Interlocked.Read(ref _lastIoFailureLogMs);
            if (nowMs - lastMs < 5000) return;
            Interlocked.Exchange(ref _lastIoFailureLogMs, nowMs);

            Views.MainWindow.DbgLog("MitsubishiPlcService:IoFailure", "PLC 通信失败", new
            {
                device = _config.Name,
                _config.IpAddress,
                reason,
                failures,
                immediate,
                generation = Interlocked.Read(ref _connectionGeneration)
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

            // Abort 和 ConnectClose 分别在独立任务中执行。即使某个第三方关闭 API
            // 永不返回，看门狗、新会话连接与 Dispose 也不会被它拖死。
            _ = Task.Run(() =>
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

            _ = Task.Run(() =>
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
            _isConnected = false;
            _status.IsConnected = false;
            LastConnectionError = reason ?? "";
            return true;
        }

        private void CompleteDisconnectedState(
            PlcSession sessionToClose,
            string reason,
            bool notify,
            bool wasConnected)
        {
            _ = StartBestEffortClose(sessionToClose, reason);
            if (notify && wasConnected)
                ConnectionStateChanged?.Invoke(this, false);
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
                _status.LastTemperatureSampleTime = default;
                return true;
            }
        }

        public async Task<bool> ConnectAsync()
        {
            if (Volatile.Read(ref _isDisposed) == 1)
                return false;

            await _connectLock.WaitAsync().ConfigureAwait(false);
            PlcSession session = null;
            try
            {
                if (_isConnected && _status.IsConnected)
                    return true;

                PlcSession previousSession;
                lock (_sessionSync)
                {
                    if (Volatile.Read(ref _isDisposed) == 1)
                        return false;

                    previousSession = _activeSession;
                    var generation = Interlocked.Increment(ref _connectionGeneration);
                    session = CreateSession(generation);
                    _activeSession = session;
                    _isConnected = false;
                    _status.IsConnected = false;
                }

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
                        _isConnected = true;
                        _status.IsConnected = true;
                    }

                    // 每个新 TCP 会话都必须从“尚无本代温度样本”开始。
                    // 即使采集尚未启动，也不能让上一个会话的时间戳被 UI 当成实时数据。
                    ResetTemperatureFreshness(session.Generation);

                    if (!IsConnectionCurrent(session.Generation))
                        return false;

                    ConnectionStateChanged?.Invoke(this, true);
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

                if (result.IsSuccess)
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
                        $"读取X点 {_config.XStartAddress}×{_config.XCount} 失败: {result.Message}",
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

                if (result.IsSuccess)
                {
                    return result.Content;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[Y点读取] ✗ 失败: {result.Message}");
                    HandleConnectionFailure(
                        $"读取Y点 {_config.YStartAddress}×{_config.YCount} 失败: {result.Message}",
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
                            if (result.IsSuccess)
                            {
                                if (!TryParseMAddress(block.StartAddress, out var startNumber))
                                {
                                    System.Diagnostics.Debug.WriteLine($"读取M块 {block.StartAddress}×{block.Count} 失败: 起始地址格式无效");
                                    continue;
                                }

                                int copyLen = Math.Min(result.Content.Length, block.Count);
                                for (int i = 0; i < copyLen; i++)
                                {
                                    valuesByAddress[$"M{startNumber + i}"] = result.Content[i];
                                }
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine($"读取M块 {block.StartAddress}×{block.Count} 失败: {result.Message}");
                                HandleConnectionFailure(
                                    $"读取M点块 {block.StartAddress}×{block.Count} 失败: {result.Message}",
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
                        if (result.IsSuccess)
                        {
                            int copyLen = Math.Min(result.Content.Length, totalCount - offset);
                            Array.Copy(result.Content, 0, combined, offset, copyLen);
                            offset += block.Count;
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"读取M块 {block.StartAddress}×{block.Count} 失败: {result.Message}");
                            HandleConnectionFailure(
                                $"读取M点块 {block.StartAddress}×{block.Count} 失败: {result.Message}",
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
                if (!result1.IsSuccess)
                {
                    HandleConnectionFailure(
                        $"读取M2009×8失败: {result1.Message}",
                        expectedGeneration: connectionGeneration);
                    return new bool[totalCount];
                }
                var result2 = await RunPlcCallAsync(
                    "ReadBool M2451×2",
                    connectionGeneration,
                    transport => transport.ReadBool("M2451", 2)).ConfigureAwait(false);
                if (!result2.IsSuccess)
                {
                    HandleConnectionFailure(
                        $"读取M2451×2失败: {result2.Message}",
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
                            if (result16.IsSuccess && result16.Content.Length >= 1)
                                values[reg.Address] = result16.Content[0];
                            else
                            {
                                System.Diagnostics.Debug.WriteLine($"读取D寄存器 {reg.Address} 失败");
                                HandleConnectionFailure(
                                    $"读取D寄存器 {addr} 失败: {result16.Message}",
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
                            if (result.IsSuccess && result.Content.Length >= 1)
                                values[reg.Address] = result.Content[0];
                            else
                            {
                                System.Diagnostics.Debug.WriteLine($"读取D寄存器 {reg.Address} 失败");
                                HandleConnectionFailure(
                                    $"读取D寄存器 {addr} 失败: {result.Message}",
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
                        if (result.IsSuccess && result.Content.Length >= 1)
                            values[reg.Address] = result.Content[0];
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"读取T寄存器 {reg.Address} 失败");
                            HandleConnectionFailure(
                                $"读取T寄存器 {addr} 失败: {result.Message}",
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
                        if (result.IsSuccess && result.Content.Length >= 1)
                        {
                            values[reg.Address] = result.Content[0];
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"读取寄存器 {reg.Address} 失败");
                            HandleConnectionFailure(
                                $"读取寄存器 {reg.Address} 失败: {result.Message}",
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
            var connectionGeneration = Interlocked.Read(ref _connectionGeneration);
            try
            {
                if (!IsConnectionCurrent(connectionGeneration))
                {
                    System.Diagnostics.Debug.WriteLine($"[温度读取] ⚠ 未连接，跳过读取");
                    return float.NaN;
                }

                if (_config.TemperatureIsWord)
                {
                    // 16位 Word 读取（设备3/4等单D寄存器存温度的设备）
                    var result16 = await RunPlcCallAsync(
                        $"ReadInt16 {_config.TemperatureAddress}",
                        connectionGeneration,
                        transport => transport.ReadInt16(_config.TemperatureAddress, 1)).ConfigureAwait(false);
                    if (result16.IsSuccess && result16.Content.Length >= 1)
                    {
                        short wordValue = result16.Content[0];
                        float temp = wordValue / _config.TemperatureDivisor;
                        System.Diagnostics.Debug.WriteLine($"[温度读取] {_config.TemperatureAddress} Word值={wordValue}, 除数={_config.TemperatureDivisor}, 温度={temp:F1}°C");
                        return float.IsFinite(temp) ? temp : float.NaN;
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[温度读取] ✗ 16位读取失败: {result16.Message} (错误码: {result16.ErrorCode})");
                        HandleConnectionFailure(
                            $"读取Word温度 {_config.TemperatureAddress} 失败: {result16.Message}",
                            expectedGeneration: connectionGeneration,
                            lane: IoFailureLane.Temperature);
                    }
                }
                else
                {
                    // 32位 DINT 读取（默认，读取D地址及下一个D组成32位整数）
                    var result = await RunPlcCallAsync(
                        $"ReadInt32 {_config.TemperatureAddress}",
                        connectionGeneration,
                        transport => transport.ReadInt32(_config.TemperatureAddress, 1)).ConfigureAwait(false);
                    if (result.IsSuccess && result.Content?.Length >= 1)
                    {
                        int dintValue = result.Content[0];
                        float temp = dintValue / _config.TemperatureDivisor;
                        System.Diagnostics.Debug.WriteLine($"[温度读取] {_config.TemperatureAddress} DINT值={dintValue}, 除数={_config.TemperatureDivisor}, 温度={temp:F1}°C");
                        return float.IsFinite(temp) ? temp : float.NaN;
                    }
                    else
                    {
                        var failure = result.IsSuccess ? "PLC 返回的温度数据为空" : result.Message;
                        System.Diagnostics.Debug.WriteLine($"[温度读取] ✗ 失败: {failure} (错误码: {result.ErrorCode})");
                        System.Diagnostics.Debug.WriteLine($"[温度读取] 地址: {_config.TemperatureAddress}");
                        HandleConnectionFailure(
                            $"读取DINT温度 {_config.TemperatureAddress} 失败: {failure}",
                            expectedGeneration: connectionGeneration,
                            lane: IoFailureLane.Temperature);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[温度读取] ✗ 异常: {ex.Message}");
                HandleConnectionFailure(
                    $"读取温度 {_config.TemperatureAddress} 异常: {ex.Message}",
                    expectedGeneration: connectionGeneration,
                    lane: IoFailureLane.Temperature);
            }

            // 0°C 和负温都是合法现场值，不能用 0 表示读取失败。
            // NaN 仅作为服务内部的“无有效采样”信号，所有提交点都必须拦截。
            return float.NaN;
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

                if (!_isAcquiring || _acquisitionConnectionGeneration != connectionGeneration)
                {
                    Volatile.Write(ref _acquisitionConnectionGeneration, connectionGeneration);
                    Volatile.Write(
                        ref _activeAcquisitionToken,
                        Interlocked.Increment(ref _nextAcquisitionToken));
                    Interlocked.Exchange(ref _xyReadToken, 0);
                    Interlocked.Exchange(ref _temperatureReadToken, 0);
                    Interlocked.Exchange(ref _auxiliaryReadToken, 0);
                }

                if (!_isAcquiring)
                {
                    _isAcquiring = true;

                    // X/Y/M点快速采集定时器
                    _xyTimer = new System.Timers.Timer(_config.XYInterval);
                    _xyTimer.Elapsed += OnXYTimerElapsed;
                    _xyTimer.AutoReset = true;
                    _xyTimer.Start();

                    // 实际温度定时器
                    _tempTimer = new System.Timers.Timer(_config.TemperatureInterval);
                    _tempTimer.Elapsed += OnTempTimerElapsed;
                    _tempTimer.AutoReset = true;
                    _tempTimer.Start();
                }
            }

            // 连接成功后立即采一次温度，不必先等待完整的 TemperatureInterval。
            OnTempTimerElapsed(null, null);
        }

        public void StopAcquisition()
        {
            System.Timers.Timer xyTimer;
            System.Timers.Timer tempTimer;
            lock (_acquisitionSync)
            {
                _isAcquiring = false;
                Volatile.Write(ref _acquisitionConnectionGeneration, 0);
                Volatile.Write(ref _activeAcquisitionToken, 0);
                Interlocked.Exchange(ref _xyReadToken, 0);
                Interlocked.Exchange(ref _temperatureReadToken, 0);
                Interlocked.Exchange(ref _auxiliaryReadToken, 0);
                xyTimer = _xyTimer;
                tempTimer = _tempTimer;
                _xyTimer = null;
                _tempTimer = null;
            }

            xyTimer?.Stop();
            xyTimer?.Dispose();
            tempTimer?.Stop();
            tempTimer?.Dispose();
        }

        private void OnXYTimerElapsed(object sender, ElapsedEventArgs e)
        {
            if (!_isAcquiring)
                return;

            var connectionGeneration = Interlocked.Read(ref _connectionGeneration);
            var acquisitionToken = Volatile.Read(ref _activeAcquisitionToken);
            if (!IsAcquisitionCurrent(connectionGeneration, acquisitionToken) ||
                Interlocked.CompareExchange(
                    ref _xyReadToken,
                    acquisitionToken,
                    0) != 0)
                return;

            Task.Run(async () =>
            {
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
                    lock (_sessionSync)
                    {
                        if (!ReferenceEquals(_activeSession, session) ||
                            _connectionGeneration != session.Generation ||
                            session.IoFailureVersion != failureVersion ||
                            !IsAcquisitionCurrent(connectionGeneration, acquisitionToken))
                            return;

                        session.GeneralFailures = 0;
                        previousX = _lastX;
                        previousY = _lastY;
                        previousM = _lastM;
                        _lastX = (bool[])xValues.Clone();
                        _lastY = (bool[])yValues.Clone();
                        _lastM = (bool[])mValues.Clone();
                        _status.X = xValues;
                        _status.Y = yValues;
                        _status.M = mValues;
                        _status.LastUpdateTime = DateTime.Now;
                    }

                // 首次读取时输出日志
                if (previousX.All(x => !x) && previousY.All(y => !y) && previousM.All(m => !m))
                {
                    System.Diagnostics.Debug.WriteLine($"[数据采集] 首次读取成功 - X点数:{xValues.Length}, Y点数:{yValues.Length}, M点数:{mValues.Length}");
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
                        StateChanged?.Invoke(this, evt);
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
                        StateChanged?.Invoke(this, evt);
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
                        StateChanged?.Invoke(this, evt);
                        System.Diagnostics.Debug.WriteLine($"[IO变化] {label} ({evt.Address}): {previousM[i]} → {mValues[i]}");
                    }
                }

            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"XY采集异常: {ex.Message}");
            }
                finally
                {
                    Interlocked.CompareExchange(
                        ref _xyReadToken,
                        0,
                        acquisitionToken);
                }
        });  // Task.Run 结束
        }

        private void OnTempTimerElapsed(object sender, ElapsedEventArgs e)
        {
            if (!_isAcquiring)
                return;

            var connectionGeneration = Interlocked.Read(ref _connectionGeneration);
            var acquisitionToken = Volatile.Read(ref _activeAcquisitionToken);
            if (!IsAcquisitionCurrent(connectionGeneration, acquisitionToken) ||
                Interlocked.CompareExchange(
                    ref _temperatureReadToken,
                    acquisitionToken,
                    0) != 0)
                return;

            _ = Task.Run(() => RunTemperatureRoundAsync(connectionGeneration, acquisitionToken));
        }

        private async Task RunTemperatureRoundAsync(long connectionGeneration, long acquisitionToken)
        {
            try
            {
                if (!IsAcquisitionCurrent(connectionGeneration, acquisitionToken))
                    return;

                // 实际温度是安全关键数据：只要这一条读取成功就立即提交并通知 UI/入库。
                // 目标温度、三相电压或 C/T/D 辅助寄存器失败，不得再把真实温度整轮丢弃。
                var temperature = await ReadTemperatureAsync();
                if (!IsAcquisitionCurrent(connectionGeneration, acquisitionToken) ||
                    !float.IsFinite(temperature))
                    return;

                if (!CommitPrimaryTemperatureSample(
                        connectionGeneration,
                        acquisitionToken,
                        temperature))
                    return;

                System.Diagnostics.Debug.WriteLine($"[温度采集] {_config.Name} 实际温度:{temperature:F1}°C");

                // 辅助寄存器与主温度分离 single-flight。辅助链再慢，
                // 也不再占用温度主读的 single-flight，而让后续实际温度周期全部被跳过。
                if (Interlocked.CompareExchange(
                        ref _auxiliaryReadToken,
                        acquisitionToken,
                        0) == 0)
                    _ = Task.Run(() => RunAuxiliaryRoundAsync(
                        connectionGeneration,
                        acquisitionToken,
                        temperature));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"温度采集异常: {ex.Message}");
            }
            finally
            {
                Interlocked.CompareExchange(
                    ref _temperatureReadToken,
                    0,
                    acquisitionToken);
            }
        }

        private bool CommitPrimaryTemperatureSample(
            long connectionGeneration,
            long acquisitionToken,
            float temperature)
        {
            DateTime sampleTime;
            lock (_sessionSync)
            {
                var session = _activeSession;
                if (!_isConnected || session == null ||
                    session.Generation != connectionGeneration ||
                    !IsAcquisitionCurrent(connectionGeneration, acquisitionToken) ||
                    !float.IsFinite(temperature))
                    return false;

                session.TemperatureFailures = 0;
                sampleTime = DateTime.Now;
                _status.Temperature = temperature;
                _status.LastUpdateTime = sampleTime;
                _status.LastTemperatureSampleTime = sampleTime;
                Interlocked.Exchange(
                    ref _lastTemperatureSampleTimestamp,
                    System.Diagnostics.Stopwatch.GetTimestamp());

                UpdateTemperatureAlarmState(temperature);
            }

            // 温度采样事件（外部订阅者负责同步主界面并入队数据库）。
            // 辅助数据使用最近一次有效值；不能因为辅助寄存器失败而阻止实际温度发布。
            try
            {
                TemperatureSampled?.Invoke(this, new TemperatureSampleEventArgs
                {
                    Temperature = temperature,
                    TargetTemperature = _status.TargetTemperature,
                    ThermocoupleA = _status.ThermocoupleA,
                    ThermocoupleB = _status.ThermocoupleB,
                    ThermocoupleC = _status.ThermocoupleC,
                    IsAbnormal = _status.IsAlarm,
                    SampleTime = sampleTime,
                    DeviceName = _config.Name
                });
            }
            catch (Exception evtEx)
            {
                System.Diagnostics.Debug.WriteLine($"[温度采集] TemperatureSampled 订阅者抛异常: {evtEx.Message}");
            }

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
            if (!TryCommitAuxiliaryValue(
                    session,
                    failureVersion,
                    connectionGeneration,
                    acquisitionToken,
                    () => _status.TargetTemperature = targetTemperature))
                return;

            if (_config.HasVoltage)
            {
                failureVersion = Volatile.Read(ref session.IoFailureVersion);
                var thermoA = await ReadThermocoupleAAsync();
                if (!TryCommitAuxiliaryValue(
                        session,
                        failureVersion,
                        connectionGeneration,
                        acquisitionToken,
                        () => _status.ThermocoupleA = thermoA))
                    return;

                failureVersion = Volatile.Read(ref session.IoFailureVersion);
                var thermoB = await ReadThermocoupleBAsync();
                if (!TryCommitAuxiliaryValue(
                        session,
                        failureVersion,
                        connectionGeneration,
                        acquisitionToken,
                        () => _status.ThermocoupleB = thermoB))
                    return;

                failureVersion = Volatile.Read(ref session.IoFailureVersion);
                var thermoC = await ReadThermocoupleCAsync();
                if (!TryCommitAuxiliaryValue(
                        session,
                        failureVersion,
                        connectionGeneration,
                        acquisitionToken,
                        () => _status.ThermocoupleC = thermoC))
                    return;
            }

            if (_config.HasCRegisters)
            {
                failureVersion = Volatile.Read(ref session.IoFailureVersion);
                var cValues = await ReadCRegistersAsync();
                if (!TryCommitAuxiliaryValue(
                        session,
                        failureVersion,
                        connectionGeneration,
                        acquisitionToken,
                        () => _status.CValues = cValues))
                    return;
            }

            lock (_sessionSync)
            {
                if (!ReferenceEquals(_activeSession, session) ||
                    _connectionGeneration != session.Generation ||
                    !IsAcquisitionCurrent(connectionGeneration, acquisitionToken))
                    return;

                session.AuxiliaryFailures = 0;
                UpdateTemperatureAlarmState(temperature);
            }
        }

        private bool TryCommitAuxiliaryValue(
            PlcSession session,
            long expectedFailureVersion,
            long connectionGeneration,
            long acquisitionToken,
            Action update)
        {
            lock (_sessionSync)
            {
                if (!ReferenceEquals(_activeSession, session) ||
                    _connectionGeneration != session.Generation ||
                    session.IoFailureVersion != expectedFailureVersion ||
                    !IsAcquisitionCurrent(connectionGeneration, acquisitionToken))
                    return false;

                update();
                return true;
            }
        }

        private void UpdateTemperatureAlarmState(float temperature)
        {
            // 报警阈值：使用 PlcConfig.TemperatureThreshold（设备详情页设置）。
            float threshold = _config.TemperatureThreshold > 0 ? _config.TemperatureThreshold : 90f;
            bool isAlarm = temperature > threshold;
            bool isSsrFault = false;

            if (_config.HasVoltage && threshold > 0)
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

            // TODO: 钉钉温度/SSR 报警暂时禁用，后续启用时在状态翻转处发送。
        }

        private async Task<float> ReadTargetTemperatureAsync()
        {
            var connectionGeneration = Interlocked.Read(ref _connectionGeneration);
            try
            {
                if (!IsConnectionCurrent(connectionGeneration))
                    return float.NaN;

                if (_config.TemperatureIsWord)
                {
                    // 16 位 Word 读取（与实际温度一致，如设备1的 D280）
                    var result16 = await RunPlcCallAsync(
                        $"ReadInt16 {_config.TargetTemperatureAddress}",
                        connectionGeneration,
                        transport => transport.ReadInt16(_config.TargetTemperatureAddress, 1)).ConfigureAwait(false);
                    if (result16.IsSuccess && result16.Content.Length >= 1)
                    {
                        short wordValue = result16.Content[0];
                        float targetTemp = wordValue / _config.TemperatureDivisor;
                        System.Diagnostics.Debug.WriteLine($"[目标温度] {_config.TargetTemperatureAddress} Word值={wordValue}, 除数={_config.TemperatureDivisor}, 目标温度={targetTemp:F1}°C");
                        return targetTemp;
                    }
                    else
                    {
                        HandleConnectionFailure(
                            $"读取Word目标温度 {_config.TargetTemperatureAddress} 失败: {result16.Message}",
                            expectedGeneration: connectionGeneration,
                            lane: IoFailureLane.Auxiliary);
                    }
                }
                else
                {
                    // 32 位 DINT 读取（默认，与实际温度一致）
                    var result = await RunPlcCallAsync(
                        $"ReadInt32 {_config.TargetTemperatureAddress}",
                        connectionGeneration,
                        transport => transport.ReadInt32(_config.TargetTemperatureAddress, 1)).ConfigureAwait(false);
                    if (result.IsSuccess && result.Content.Length >= 1)
                    {
                        int dintValue = result.Content[0];
                        float targetTemp = dintValue / _config.TemperatureDivisor;
                        System.Diagnostics.Debug.WriteLine($"[目标温度] {_config.TargetTemperatureAddress} DINT值={dintValue}, 除数={_config.TemperatureDivisor}, 目标温度={targetTemp:F1}°C");
                        return targetTemp;
                    }
                    else
                    {
                        HandleConnectionFailure(
                            $"读取DINT目标温度 {_config.TargetTemperatureAddress} 失败: {result.Message}",
                            expectedGeneration: connectionGeneration,
                            lane: IoFailureLane.Auxiliary);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"读取目标温度异常: {ex.Message}");
                HandleConnectionFailure(
                    $"读取目标温度 {_config.TargetTemperatureAddress} 异常: {ex.Message}",
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
        public string DeviceName { get; set; } = "";
    }
}
