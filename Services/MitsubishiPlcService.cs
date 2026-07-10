using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using HslCommunication.Profinet.Melsec;
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
        private readonly MelsecA1ENet _plc;
        private readonly PlcConfig _config;
        private readonly PlcStatus _status;
        private bool[] _lastX;
        private bool[] _lastY;
        private bool[] _lastM;
        private bool _isConnected = false;

        private System.Timers.Timer _xyTimer;
        private System.Timers.Timer _tempTimer;
        private bool _isAcquiring;
        private int _isReadingXY = 0;   // 0=空闲 1=采集中，Interlocked 原子操作防重入
        private int _isReadingTemp = 0;  // 0=空闲 1=采集中，Interlocked 原子操作防重入
        private DateTime _acquisitionStartedAt;
        private readonly SemaphoreSlim _plcIoLock = new(1, 1);
        private readonly SemaphoreSlim _connectLock = new(1, 1);
        private readonly object _connectionCloseSync = new();
        private Task _pendingConnectionClose = Task.CompletedTask;
        private long _connectionGeneration;
        private long _ioFailureVersion;
        private long _lastSlowIoLogMs;
        private long _lastIoFailureLogMs;
        private int _isDisposed;

        // 连续读取失败计数：无线网桥偶发丢一两个包很常见，连续失败达到阈值才判离线，
        // 避免单次抖动触发"掉线→重连"振荡和误报警。整轮采集成功后清零。
        private int _consecutiveIoFailures = 0;
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

        public bool IsTemperatureSampleStale(DateTime now, out TimeSpan age)
        {
            age = TimeSpan.Zero;
            if (!_isAcquiring || !_isConnected)
                return false;

            var baseline = _status.LastTemperatureSampleTime == default
                ? _acquisitionStartedAt
                : _status.LastTemperatureSampleTime;

            if (baseline == default)
                return false;

            age = now - baseline;
            var staleAfterMs = Math.Max(_config.TemperatureInterval * 4, _config.TemperatureInterval + 30_000);
            return age.TotalMilliseconds > staleAfterMs;
        }

        public void MarkTemperatureSampleStale(TimeSpan age)
        {
            // 已经停滞了多个温度周期，不属于单次抖动，跳过容错计数直接断线进入重连
            HandleConnectionFailure($"温度采样超过 {age.TotalSeconds:F0} 秒未更新", immediate: true);
        }

        public MitsubishiPlcService()
        {
            _config = new PlcConfig();
            _status = new PlcStatus(_config.XCount, _config.YCount, _config.ActualMCount);
            _plc = new MelsecA1ENet(_config.IpAddress, _config.Port);
            _plc.ReceiveTimeOut = 2000;  // 读取超时2秒，防止无线丢包时TCP读挂死
            _lastX = new bool[_config.XCount];
            _lastY = new bool[_config.YCount];
            _lastM = new bool[_config.ActualMCount];
        }

        /// <summary>
        /// 使用指定配置创建PLC服务（FX3U MC协议 1E帧，端口默认5000）
        /// </summary>
        public MitsubishiPlcService(PlcConfig config)
        {
            _config = config;
            _status = new PlcStatus(_config.XCount, _config.YCount, _config.ActualMCount);
            _plc = new MelsecA1ENet(_config.IpAddress, _config.Port);
            _plc.ReceiveTimeOut = 2000;  // 读取超时2秒，防止无线丢包时TCP读挂死
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
        /// HslCommunication 的同一个 MelsecA1ENet 实例底层复用同一条 TCP 连接。
        /// 同一台 PLC 的 X/Y/M/温度/C 寄存器如果并发读，现场网络抖动时容易互相抢连接、
        /// 堵住线程池，最终表现成主界面卡死。这里把每台 PLC 内部的阻塞通信串行化。
        /// </summary>
        private async Task<T> RunPlcCallAsync<T>(string operationName, Func<T> action)
        {
            await _plcIoLock.WaitAsync().ConfigureAwait(false);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // 手动点击“连接”可能从 UI 线程进入这里，HslCommunication 是同步阻塞 API。
                // UI 线程上必须丢到后台跑；采集定时器本来就在后台线程，直接执行可避免双层 Task.Run。
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher != null && dispatcher.CheckAccess())
                    return await Task.Run(action).ConfigureAwait(false);

                return action();
            }
            finally
            {
                sw.Stop();
                _plcIoLock.Release();
                LogSlowIo(operationName, sw.ElapsedMilliseconds);
            }
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

        private Task GetPendingConnectionClose()
        {
            lock (_connectionCloseSync)
                return _pendingConnectionClose;
        }

        private Task ScheduleConnectionClose(long expectedGeneration)
        {
            lock (_connectionCloseSync)
            {
                var previous = _pendingConnectionClose;
                _pendingConnectionClose = CloseConnectionAfterAsync(previous, expectedGeneration);
                return _pendingConnectionClose;
            }
        }

        private async Task CloseConnectionAfterAsync(Task previous, long expectedGeneration)
        {
            try { await previous.ConfigureAwait(false); } catch { }

            await _plcIoLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // 旧连接的延迟关闭不能误伤已经建立的新连接。
                if (!_isConnected && Interlocked.Read(ref _connectionGeneration) == expectedGeneration)
                    _plc.ConnectClose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PLC连接] 关闭连接异常: {_config.Name} - {ex.Message}");
            }
            finally
            {
                _plcIoLock.Release();
            }
        }

        private long SetDisconnectedState(string reason, bool notify)
        {
            var wasConnected = _isConnected || _status.IsConnected;
            var generation = Interlocked.Increment(ref _connectionGeneration);
            _isConnected = false;
            _status.IsConnected = false;
            LastConnectionError = reason ?? "";
            if (notify && wasConnected)
                ConnectionStateChanged?.Invoke(this, false);
            return generation;
        }

        private bool IsConnectionCurrent(long generation)
            => _isConnected && Interlocked.Read(ref _connectionGeneration) == generation;

        private void ResetTemperatureFreshness()
        {
            _acquisitionStartedAt = DateTime.Now;
            _status.LastTemperatureSampleTime = default;
        }

        public async Task<bool> ConnectAsync()
        {
            if (Volatile.Read(ref _isDisposed) == 1)
                return false;

            await _connectLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_isConnected && _status.IsConnected)
                    return true;

                // 等待上一代连接完成关闭，避免 ConnectServer 与延迟 ConnectClose 交错。
                await GetPendingConnectionClose().ConfigureAwait(false);

                _plc.IpAddress = _config.IpAddress;
                _plc.Port = _config.Port;
                _plc.ConnectTimeOut = _config.ConnectTimeout;

                System.Diagnostics.Debug.WriteLine($"[PLC连接] 尝试连接 {_config.Name} ({_config.IpAddress}:{_config.Port})");

                var result = await RunPlcCallAsync("ConnectServer", () => _plc.ConnectServer()).ConfigureAwait(false);

                if (result.IsSuccess)
                {
                    LastConnectionError = "";
                    Interlocked.Exchange(ref _consecutiveIoFailures, 0);
                    Interlocked.Increment(ref _connectionGeneration);
                    _isConnected = true;
                    _status.IsConnected = true;
                    if (_isAcquiring)
                        ResetTemperatureFreshness();
                    ConnectionStateChanged?.Invoke(this, true);
                    System.Diagnostics.Debug.WriteLine($"[PLC连接] ✓ 连接成功: {_config.Name}");
                    return true;
                }
                else
                {
                    var generation = SetDisconnectedState(result.Message ?? "未知错误", notify: true);
                    _ = ScheduleConnectionClose(generation);
                    System.Diagnostics.Debug.WriteLine($"[PLC连接] ✗ 连接失败: {_config.Name} - {LastConnectionError}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                LastConnectionError = ex.Message ?? "未知异常";
                System.Diagnostics.Debug.WriteLine($"[PLC连接] ✗ 连接异常: {_config.Name} - {LastConnectionError}");
                var generation = SetDisconnectedState(LastConnectionError, notify: true);
                _ = ScheduleConnectionClose(generation);
                return false;
            }
            finally
            {
                _connectLock.Release();
            }
        }

        public void Disconnect()
        {
            StopAcquisition();
            var generation = SetDisconnectedState("用户主动断开", notify: true);
            _ = ScheduleConnectionClose(generation);
        }

        /// <summary>
        /// 读取失败统一处理。默认按"连续失败计数"容错：未达到阈值只记录不断线；
        /// immediate=true 用于温度采样长时间停滞这类已经累积多个周期的判定，直接断线。
        /// </summary>
        private void HandleConnectionFailure(string reason, bool immediate = false, long? expectedGeneration = null)
        {
            if (expectedGeneration.HasValue &&
                Interlocked.Read(ref _connectionGeneration) != expectedGeneration.Value)
                return;

            Interlocked.Increment(ref _ioFailureVersion);

            if (!_isConnected)
                return;

            int failures = Volatile.Read(ref _consecutiveIoFailures);
            if (!immediate)
            {
                failures = Interlocked.Increment(ref _consecutiveIoFailures);
                LogIoFailure(reason, failures, immediate: false);
                if (failures < OfflineAfterConsecutiveFailures)
                {
                    LastConnectionError = reason;
                    System.Diagnostics.Debug.WriteLine($"[PLC连接] ⚠ 读取失败 {failures}/{OfflineAfterConsecutiveFailures}，暂不判离线: {_config.Name} - {reason}");
                    return;
                }
            }
            else
            {
                LogIoFailure(reason, failures, immediate: true);
            }

            var generation = SetDisconnectedState(reason, notify: true);
            System.Diagnostics.Debug.WriteLine($"[PLC连接] ✗ 自动检测离线: {_config.Name} - {reason}");
            _ = ScheduleConnectionClose(generation);
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
                    () => _plc.ReadBool(_config.XStartAddress, (ushort)_config.XCount)).ConfigureAwait(false);

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
                    () => _plc.ReadBool(_config.YStartAddress, (ushort)_config.YCount)).ConfigureAwait(false);

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
                                () => _plc.ReadBool(block.StartAddress, block.Count)).ConfigureAwait(false);
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
                            () => _plc.ReadBool(block.StartAddress, block.Count)).ConfigureAwait(false);
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
                    "ReadBool M2009×8", () => _plc.ReadBool("M2009", 8)).ConfigureAwait(false);
                if (!result1.IsSuccess)
                {
                    HandleConnectionFailure(
                        $"读取M2009×8失败: {result1.Message}",
                        expectedGeneration: connectionGeneration);
                    return new bool[totalCount];
                }
                var result2 = await RunPlcCallAsync(
                    "ReadBool M2451×2", () => _plc.ReadBool("M2451", 2)).ConfigureAwait(false);
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
                                $"ReadInt16 {addr}", () => _plc.ReadInt16(addr, 1)).ConfigureAwait(false);
                            if (result16.IsSuccess && result16.Content.Length >= 1)
                                values[reg.Address] = result16.Content[0];
                            else
                            {
                                System.Diagnostics.Debug.WriteLine($"读取D寄存器 {reg.Address} 失败");
                                HandleConnectionFailure(
                                    $"读取D寄存器 {addr} 失败: {result16.Message}",
                                    expectedGeneration: connectionGeneration);
                                return values;
                            }
                        }
                        else
                        {
                            var result = await RunPlcCallAsync(
                                $"ReadInt32 {addr}", () => _plc.ReadInt32(addr, 1)).ConfigureAwait(false);
                            if (result.IsSuccess && result.Content.Length >= 1)
                                values[reg.Address] = result.Content[0];
                            else
                            {
                                System.Diagnostics.Debug.WriteLine($"读取D寄存器 {reg.Address} 失败");
                                HandleConnectionFailure(
                                    $"读取D寄存器 {addr} 失败: {result.Message}",
                                    expectedGeneration: connectionGeneration);
                                return values;
                            }
                        }
                    }
                    else if (addr.StartsWith("T", StringComparison.OrdinalIgnoreCase))
                    {
                        var result = await RunPlcCallAsync(
                            $"ReadInt16 {addr}", () => _plc.ReadInt16(addr, 1)).ConfigureAwait(false);
                        if (result.IsSuccess && result.Content.Length >= 1)
                            values[reg.Address] = result.Content[0];
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"读取T寄存器 {reg.Address} 失败");
                            HandleConnectionFailure(
                                $"读取T寄存器 {addr} 失败: {result.Message}",
                                expectedGeneration: connectionGeneration);
                            return values;
                        }
                    }
                    else
                    {
                        var result = await RunPlcCallAsync(
                            $"ReadInt16 {reg.Address}", () => _plc.ReadInt16(reg.Address, 1)).ConfigureAwait(false);
                        if (result.IsSuccess && result.Content.Length >= 1)
                        {
                            values[reg.Address] = result.Content[0];
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"读取寄存器 {reg.Address} 失败");
                            HandleConnectionFailure(
                                $"读取寄存器 {reg.Address} 失败: {result.Message}",
                                expectedGeneration: connectionGeneration);
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
                    expectedGeneration: connectionGeneration);
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
                    return 0f;
                }

                if (_config.TemperatureIsWord)
                {
                    // 16位 Word 读取（设备3/4等单D寄存器存温度的设备）
                    var result16 = await RunPlcCallAsync(
                        $"ReadInt16 {_config.TemperatureAddress}",
                        () => _plc.ReadInt16(_config.TemperatureAddress, 1)).ConfigureAwait(false);
                    if (result16.IsSuccess && result16.Content.Length >= 1)
                    {
                        short wordValue = result16.Content[0];
                        float temp = wordValue / _config.TemperatureDivisor;
                        System.Diagnostics.Debug.WriteLine($"[温度读取] {_config.TemperatureAddress} Word值={wordValue}, 除数={_config.TemperatureDivisor}, 温度={temp:F1}°C");
                        return temp;
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[温度读取] ✗ 16位读取失败: {result16.Message} (错误码: {result16.ErrorCode})");
                        HandleConnectionFailure(
                            $"读取Word温度 {_config.TemperatureAddress} 失败: {result16.Message}",
                            expectedGeneration: connectionGeneration);
                    }
                }
                else
                {
                    // 32位 DINT 读取（默认，读取D地址及下一个D组成32位整数）
                    var result = await RunPlcCallAsync(
                        $"ReadInt32 {_config.TemperatureAddress}",
                        () => _plc.ReadInt32(_config.TemperatureAddress, 1)).ConfigureAwait(false);
                    if (result.IsSuccess)
                    {
                        var values = result.Content;
                        if (values.Length >= 1)
                        {
                            int dintValue = values[0];
                            float temp = dintValue / _config.TemperatureDivisor;
                            System.Diagnostics.Debug.WriteLine($"[温度读取] {_config.TemperatureAddress} DINT值={dintValue}, 除数={_config.TemperatureDivisor}, 温度={temp:F1}°C");
                            return temp;
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[温度读取] ✗ 失败: {result.Message} (错误码: {result.ErrorCode})");
                        System.Diagnostics.Debug.WriteLine($"[温度读取] 地址: {_config.TemperatureAddress}");
                        HandleConnectionFailure(
                            $"读取DINT温度 {_config.TemperatureAddress} 失败: {result.Message}",
                            expectedGeneration: connectionGeneration);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[温度读取] ✗ 异常: {ex.Message}");
                HandleConnectionFailure(
                    $"读取温度 {_config.TemperatureAddress} 异常: {ex.Message}",
                    expectedGeneration: connectionGeneration);
            }

            return 0f;
        }

        public async Task<float> ReadThermocoupleAAsync()
        {
            var connectionGeneration = Interlocked.Read(ref _connectionGeneration);
            try
            {
                if (!IsConnectionCurrent(connectionGeneration))
                    return 0f;

                // 读取D17-D18作为DINT（32位有符号整数）
                var result = await RunPlcCallAsync(
                    $"ReadInt32 {_config.ThermocoupleAAddress}",
                    () => _plc.ReadInt32(_config.ThermocoupleAAddress, 1)).ConfigureAwait(false);

                if (result.IsSuccess)
                {
                    var values = result.Content;
                    if (values.Length >= 1)
                    {
                        int dintValue = values[0];
                        float voltage = dintValue / 100.0f;  // 除以100
                        System.Diagnostics.Debug.WriteLine($"[A相电压] D17-D18 DINT值={dintValue}, 电压={voltage:F2}V");
                        return voltage;
                    }
                }
                else
                {
                    HandleConnectionFailure(
                        $"读取热电偶A {_config.ThermocoupleAAddress} 失败: {result.Message}",
                        expectedGeneration: connectionGeneration);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"读取热电偶A异常: {ex.Message}");
                HandleConnectionFailure(
                    $"读取热电偶A {_config.ThermocoupleAAddress} 异常: {ex.Message}",
                    expectedGeneration: connectionGeneration);
            }

            return 0f;
        }

        public async Task<float> ReadThermocoupleBAsync()
        {
            var connectionGeneration = Interlocked.Read(ref _connectionGeneration);
            try
            {
                if (!IsConnectionCurrent(connectionGeneration))
                    return 0f;

                // 读取D19-D20作为DINT（32位有符号整数）
                var result = await RunPlcCallAsync(
                    $"ReadInt32 {_config.ThermocoupleBAddress}",
                    () => _plc.ReadInt32(_config.ThermocoupleBAddress, 1)).ConfigureAwait(false);

                if (result.IsSuccess)
                {
                    var values = result.Content;
                    if (values.Length >= 1)
                    {
                        int dintValue = values[0];
                        float voltage = dintValue / 100.0f;  // 除以100
                        System.Diagnostics.Debug.WriteLine($"[B相电压] D19-D20 DINT值={dintValue}, 电压={voltage:F2}V");
                        return voltage;
                    }
                }
                else
                {
                    HandleConnectionFailure(
                        $"读取热电偶B {_config.ThermocoupleBAddress} 失败: {result.Message}",
                        expectedGeneration: connectionGeneration);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"读取热电偶B异常: {ex.Message}");
                HandleConnectionFailure(
                    $"读取热电偶B {_config.ThermocoupleBAddress} 异常: {ex.Message}",
                    expectedGeneration: connectionGeneration);
            }

            return 0f;
        }

        public async Task<float> ReadThermocoupleCAsync()
        {
            var connectionGeneration = Interlocked.Read(ref _connectionGeneration);
            try
            {
                if (!IsConnectionCurrent(connectionGeneration))
                    return 0f;

                // 读取D21-D22作为DINT（32位有符号整数）
                var result = await RunPlcCallAsync(
                    $"ReadInt32 {_config.ThermocoupleCAddress}",
                    () => _plc.ReadInt32(_config.ThermocoupleCAddress, 1)).ConfigureAwait(false);

                if (result.IsSuccess)
                {
                    var values = result.Content;
                    if (values.Length >= 1)
                    {
                        int dintValue = values[0];
                        float voltage = dintValue / 100.0f;  // 除以100
                        System.Diagnostics.Debug.WriteLine($"[C相电压] D21-D22 DINT值={dintValue}, 电压={voltage:F2}V");
                        return voltage;
                    }
                }
                else
                {
                    HandleConnectionFailure(
                        $"读取热电偶C {_config.ThermocoupleCAddress} 失败: {result.Message}",
                        expectedGeneration: connectionGeneration);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"读取热电偶C异常: {ex.Message}");
                HandleConnectionFailure(
                    $"读取热电偶C {_config.ThermocoupleCAddress} 异常: {ex.Message}",
                    expectedGeneration: connectionGeneration);
            }

            return 0f;
        }

        public async Task<PlcStatus> ReadAllAsync()
        {
            var connectionGeneration = Interlocked.Read(ref _connectionGeneration);
            var failureVersion = Interlocked.Read(ref _ioFailureVersion);
            try
            {
                if (!IsConnectionCurrent(connectionGeneration))
                    return null;

                // 调用可并行创建，但底层由 _plcIoLock 串行访问同一条 TCP 连接。
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

                if (!IsConnectionCurrent(connectionGeneration) ||
                    Interlocked.Read(ref _ioFailureVersion) != failureVersion)
                    return null;

                _status.X = await xTask;
                _status.Y = await yTask;
                _status.M = await mTask;
                _status.Temperature = await tempTask;

                if (_config.HasVoltage)
                {
                    _status.ThermocoupleA = await thermoATask;
                    _status.ThermocoupleB = await thermoBTask;
                    _status.ThermocoupleC = await thermoCTask;
                }

                if (cTask != null)
                {
                    _status.CValues = await cTask;
                }

                _status.LastUpdateTime = DateTime.Now;
                return _status;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"读取全部数据失败: {ex.Message}");
                return null;
            }
        }

        public void StartAcquisition()
        {
            if (_isAcquiring)
            {
                // 自动重连后定时器仍然存在，但必须重新开始温度新鲜度计时，
                // 否则旧 LastTemperatureSampleTime 会让下一次监控立即再次断线。
                ResetTemperatureFreshness();
                OnTempTimerElapsed(null, null);
                return;
            }

            if (!_isConnected)
            {
                throw new InvalidOperationException("请先连接PLC");
            }

            _isAcquiring = true;
            ResetTemperatureFreshness();

            // X/Y/M点快速采集定时器 (200ms)
            _xyTimer = new System.Timers.Timer(_config.XYInterval);
            _xyTimer.Elapsed += OnXYTimerElapsed;
            _xyTimer.AutoReset = true;
            _xyTimer.Start();

            // 温度慢速采集定时器 (10秒)
            _tempTimer = new System.Timers.Timer(_config.TemperatureInterval);
            _tempTimer.Elapsed += OnTempTimerElapsed;
            _tempTimer.AutoReset = true;
            _tempTimer.Start();

            // 连接成功后立即采一次温度，不必先等待完整的 TemperatureInterval。
            OnTempTimerElapsed(null, null);
        }

        public void StopAcquisition()
        {
            _isAcquiring = false;

            _xyTimer?.Stop();
            _xyTimer?.Dispose();
            _xyTimer = null;

            _tempTimer?.Stop();
            _tempTimer?.Dispose();
            _tempTimer = null;
        }

        private void OnXYTimerElapsed(object sender, ElapsedEventArgs e)
        {
            // 防止重入：用 Interlocked 保证原子性，多个线程池线程同时触发时只有一个能进入
            if (!_isAcquiring || Interlocked.Exchange(ref _isReadingXY, 1) == 1)
                return;

            Task.Run(async () =>
            {
                try
                {
                    var connectionGeneration = Interlocked.Read(ref _connectionGeneration);
                    var failureVersion = Interlocked.Read(ref _ioFailureVersion);
                    if (!_isAcquiring || !IsConnectionCurrent(connectionGeneration))
                        return;

                    var xValues = await ReadXPointsAsync();
                    var yValues = await ReadYPointsAsync();
                    var mValues = await ReadMPointsAsync();

                    // 本轮任一读失败时数据不完整（失败的读返回全 false 数组），
                    // 即使容错期内连接还保留，也必须跳过比较，否则会比出一堆假"IO变化"
                    if (!IsConnectionCurrent(connectionGeneration) ||
                        Interlocked.Read(ref _ioFailureVersion) != failureVersion)
                        return;

                    // 整轮 X/Y/M 全部读取成功，清零连续失败计数
                    Interlocked.Exchange(ref _consecutiveIoFailures, 0);

                // 更新当前状态
                _status.X = xValues;
                _status.Y = yValues;
                _status.M = mValues;
                _status.LastUpdateTime = DateTime.Now;

                // 首次读取时输出日志
                if (_lastX.All(x => !x) && _lastY.All(y => !y) && _lastM.All(m => !m))
                {
                    System.Diagnostics.Debug.WriteLine($"[数据采集] 首次读取成功 - X点数:{xValues.Length}, Y点数:{yValues.Length}, M点数:{mValues.Length}");
                }

                // 检测X点变化（三菱X为八进制：下标0-7→X0-X7，8→X10，9→X11…）
                for (int i = 0; i < Math.Min(xValues.Length, _lastX.Length); i++)
                {
                    if (xValues[i] != _lastX[i])
                    {
                        var label = _config.GetXLabel(i);
                        var evt = new StateChangeEvent
                        {
                            PointType = "X",
                            PointIndex = i,
                            Address = _config.GetXAddress(i),
                            OldValue = _lastX[i],
                            NewValue = xValues[i],
                            EventTime = DateTime.Now,
                            PointLabel = label
                        };
                        StateChanged?.Invoke(this, evt);
                        System.Diagnostics.Debug.WriteLine($"[IO变化] {label} ({evt.Address}): {_lastX[i]} → {xValues[i]}");
                    }
                }

                // 检测Y点变化（三菱Y为八进制：下标0-7→Y0-Y7，8→Y10…）
                for (int i = 0; i < Math.Min(yValues.Length, _lastY.Length); i++)
                {
                    if (yValues[i] != _lastY[i])
                    {
                        var label = _config.GetYLabel(i);
                        var evt = new StateChangeEvent
                        {
                            PointType = "Y",
                            PointIndex = i,
                            Address = _config.GetYAddress(i),
                            OldValue = _lastY[i],
                            NewValue = yValues[i],
                            EventTime = DateTime.Now,
                            PointLabel = label
                        };
                        StateChanged?.Invoke(this, evt);
                        System.Diagnostics.Debug.WriteLine($"[IO变化] {label} ({evt.Address}): {_lastY[i]} → {yValues[i]}");
                    }
                }

                // 检测M点变化（M 地址来自每台设备的 MAddressList，可能不连续）
                for (int i = 0; i < Math.Min(mValues.Length, _lastM.Length); i++)
                {
                    if (mValues[i] != _lastM[i])
                    {
                        var label = _config.GetMLabel(i);
                        var evt = new StateChangeEvent
                        {
                            PointType = "M",
                            PointIndex = i,
                            Address = _config.GetMAddress(i),
                            OldValue = _lastM[i],
                            NewValue = mValues[i],
                            EventTime = DateTime.Now,
                            PointLabel = label
                        };
                        StateChanged?.Invoke(this, evt);
                        System.Diagnostics.Debug.WriteLine($"[IO变化] {label} ({evt.Address}): {_lastM[i]} → {mValues[i]}");
                    }
                }

                // 保存上次状态
                _lastX = (bool[])xValues.Clone();
                _lastY = (bool[])yValues.Clone();
                _lastM = (bool[])mValues.Clone();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"XY采集异常: {ex.Message}");
            }
            finally
            {
                // 重要：必须重置标志，否则后续采集都会被跳过！
                Interlocked.Exchange(ref _isReadingXY, 0);
            }
        });  // Task.Run 结束
        }

        private void OnTempTimerElapsed(object sender, ElapsedEventArgs e)
        {
            // 防止重入：上次温度读取未完成时跳过，避免 async void 异常吞噬风险
            if (!_isAcquiring || Interlocked.Exchange(ref _isReadingTemp, 1) == 1)
                return;

            Task.Run(async () =>
            {
            try
            {
                var connectionGeneration = Interlocked.Read(ref _connectionGeneration);
                var failureVersion = Interlocked.Read(ref _ioFailureVersion);
                if (!_isAcquiring || !IsConnectionCurrent(connectionGeneration))
                    return;

                var temperature = await ReadTemperatureAsync();
                var targetTemp = await ReadTargetTemperatureAsync();

                // 容错期内单次读失败不断线，但失败的读返回 0，本轮数据不可信，直接丢弃
                if (!IsConnectionCurrent(connectionGeneration) ||
                    Interlocked.Read(ref _ioFailureVersion) != failureVersion)
                    return;

                // 仅在有电压配置时读取热电偶数据
                float thermoA = 0, thermoB = 0, thermoC = 0;
                if (_config.HasVoltage)
                {
                    thermoA = await ReadThermocoupleAAsync();
                    thermoB = await ReadThermocoupleBAsync();
                    thermoC = await ReadThermocoupleCAsync();
                    if (!IsConnectionCurrent(connectionGeneration) ||
                        Interlocked.Read(ref _ioFailureVersion) != failureVersion) return;
                }

                // 读取 C 寄存器
                if (_config.HasCRegisters)
                {
                    var cValues = await ReadCRegistersAsync();
                    if (!IsConnectionCurrent(connectionGeneration) ||
                        Interlocked.Read(ref _ioFailureVersion) != failureVersion) return;
                    _status.CValues = cValues;
                }

                // 整轮温度采样链路全部成功，清零连续失败计数
                Interlocked.Exchange(ref _consecutiveIoFailures, 0);

                var sampleTime = DateTime.Now;
                _status.Temperature = temperature;
                _status.TargetTemperature = targetTemp;
                _status.LastUpdateTime = sampleTime;
                _status.LastTemperatureSampleTime = sampleTime;
                if (_config.HasVoltage)
                {
                    _status.ThermocoupleA = thermoA;
                    _status.ThermocoupleB = thermoB;
                    _status.ThermocoupleC = thermoC;
                }

                // 报警阈值：使用 PlcConfig.TemperatureThreshold（设备详情页"温度报警阈值"输入框设置）
                float threshold = _config.TemperatureThreshold > 0 ? _config.TemperatureThreshold : 90f;
                bool isAlarm = temperature > threshold;
                bool isSsrFault = false;

                // SSR 故障检测仅在有电压数据时执行
                if (_config.HasVoltage && threshold > 0)
                {
                    float avgVoltage = (thermoA + thermoB + thermoC) / 3f;
                    bool hasVoltageOutput = avgVoltage > 0.1f;

                    // Y17（PID 输出）在数组里的真实下标取决于设备的 YStartAddress；
                    // 这里通过 GetYAddress 反查，YCount 不够则视作未配置 Y17。
                    int pidIdx = -1;
                    for (int yi = 0; yi < _config.YCount; yi++)
                    {
                        if (_config.GetYAddress(yi) == "Y17") { pidIdx = yi; break; }
                    }
                    bool pidOutput = pidIdx >= 0 && pidIdx < _status.Y.Length && _status.Y[pidIdx];
                    bool tempKeepRising = temperature > threshold + 5;

                    if (hasVoltageOutput && !pidOutput && tempKeepRising)
                    {
                        isSsrFault = true;
                    }
                }

                bool wasAlarm = _status.IsAlarm;
                bool wasSsrFault = _status.IsSsrFault;
                _status.IsAlarm = isAlarm;
                _status.IsSsrFault = isSsrFault;

                if (isAlarm && !wasAlarm)
                {
                    // TODO: 钉钉温度报警暂时禁用，后续启用时取消注释
                // _ = DingTalkService.Instance.SendTemperatureAlarmAsync(
                //     _config.Name, temperature, targetTemp);
                }

                if (isSsrFault && !wasSsrFault)
                {
                // TODO: 钉钉 SSR 故障报警暂时禁用，后续启用时取消注释
                // _ = DingTalkService.Instance.SendSsrFaultAlertAsync(_config.Name);
                }

                // 温度采样事件（外部订阅者负责入库到 LogBufferService）
                try
                {
                    TemperatureSampled?.Invoke(this, new TemperatureSampleEventArgs
                    {
                        Temperature = temperature,
                        TargetTemperature = targetTemp,
                        ThermocoupleA = thermoA,
                        ThermocoupleB = thermoB,
                        ThermocoupleC = thermoC,
                        IsAbnormal = isAlarm,
                        SampleTime = sampleTime,
                        DeviceName = _config.Name
                    });
                }
                catch (Exception evtEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[温度采集] TemperatureSampled 订阅者抛异常: {evtEx.Message}");
                }

                if (_config.HasVoltage)
                    System.Diagnostics.Debug.WriteLine($"[温度采集] {_config.Name} 温度:{temperature:F1}°C, 目标:{targetTemp:F1}°C, A相:{thermoA:F3}V, B相:{thermoB:F3}V, C相:{thermoC:F3}V");
                else
                    System.Diagnostics.Debug.WriteLine($"[温度采集] {_config.Name} 温度:{temperature:F1}°C, 目标:{targetTemp:F1}°C");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"温度采集异常: {ex.Message}");
            }
            finally
            {
                // 重置标志，允许下次采集
                Interlocked.Exchange(ref _isReadingTemp, 0);
            }
            });  // Task.Run 结束
        }

        private async Task<float> ReadTargetTemperatureAsync()
        {
            var connectionGeneration = Interlocked.Read(ref _connectionGeneration);
            try
            {
                if (!IsConnectionCurrent(connectionGeneration))
                    return 0f;

                if (_config.TemperatureIsWord)
                {
                    // 16 位 Word 读取（与实际温度一致，如设备1的 D280）
                    var result16 = await RunPlcCallAsync(
                        $"ReadInt16 {_config.TargetTemperatureAddress}",
                        () => _plc.ReadInt16(_config.TargetTemperatureAddress, 1)).ConfigureAwait(false);
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
                            expectedGeneration: connectionGeneration);
                    }
                }
                else
                {
                    // 32 位 DINT 读取（默认，与实际温度一致）
                    var result = await RunPlcCallAsync(
                        $"ReadInt32 {_config.TargetTemperatureAddress}",
                        () => _plc.ReadInt32(_config.TargetTemperatureAddress, 1)).ConfigureAwait(false);
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
                            expectedGeneration: connectionGeneration);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"读取目标温度异常: {ex.Message}");
                HandleConnectionFailure(
                    $"读取目标温度 {_config.TargetTemperatureAddress} 异常: {ex.Message}",
                    expectedGeneration: connectionGeneration);
            }
            return 0f;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
                return;

            StopAcquisition();
            var generation = SetDisconnectedState("服务已释放", notify: false);
            _ = ScheduleConnectionClose(generation);

            // 不释放 SemaphoreSlim：已触发但尚未退出的定时器任务可能仍在 Wait/Release。
            // 它们是纯托管对象，随 MitsubishiPlcService 一起由 GC 回收即可。
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
