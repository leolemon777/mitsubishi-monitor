using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MitsubishiMonitor.Demo.Models;
using MitsubishiMonitor.Demo.Services;

namespace MitsubishiMonitor.Demo.Services
{
    /// <summary>
    /// 单个设备的PLC包装器
    /// </summary>
    public class DevicePlcWrapper : ObservableObject
    {
        public Device Device { get; }
        public IPlcService PlcService { get; }

        public DevicePlcWrapper(Device device, IPlcService plcService)
        {
            Device = device;
            PlcService = plcService;
        }

        /// <summary>
        /// 更新设备状态（从PLC读取）
        /// </summary>
        public Task UpdateStatusAsync()
        {
            var wasOnline = Device.IsOnline;
            var status = PlcService.CurrentStatus;

            if (status.IsConnected)
            {
                Device.IsOnline = true;
                var hasCurrentSample = status.LastTemperatureSampleTime != default;
                if (hasCurrentSample)
                {
                    Device.CurrentTemperature = status.Temperature;
                    Device.HasTemperatureSample = true;
                    // 温度卡片的更新时间只能来自真实温度采样，不能用手动刷新时间伪造。
                    Device.LastUpdateTime = status.LastTemperatureSampleTime;
                    Device.LastTemperatureSampleTime = status.LastTemperatureSampleTime;
                    Device.LastTemperatureSampleSequence = status.LastTemperatureSampleSequence;
                    Device.LastTemperatureConnectionGeneration = status.LastTemperatureConnectionGeneration;
                    Device.LastTemperatureRawValue = status.LastTemperatureRawValue;
                    Device.TemperatureQuality = status.TemperatureQuality;
                    Device.HasAlert = status.IsAlarm;
                }

                Device.IsTemperatureStale = !hasCurrentSample ||
                    (PlcService is MitsubishiPlcService mitsubishi &&
                     mitsubishi.IsTemperatureSampleDelayed(out _));
            }
            else
            {
                Device.IsOnline = false;
                if (Device.HasTemperatureSample)
                    Device.IsTemperatureStale = true;
            }

            // 如果状态变化，触发通知
            if (wasOnline != Device.IsOnline)
            {
                OnPropertyChanged(nameof(Device));
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 多设备PLC管理服务
    /// 管理4台设备的PLC连接（通过无线网桥）
    /// </summary>
    public partial class DeviceManagerService : ObservableObject
    {
        private readonly ObservableCollection<DevicePlcWrapper> _wrappers;
        private readonly ObservableCollection<Device> _devices;
        private readonly System.Timers.Timer _monitorTimer;
        private readonly System.Timers.Timer _cleanupTimer;
        private readonly System.Timers.Timer _operationCountFlushTimer;
        private int _isMonitoring;
        private int _isCleaning;
        private int _isFlushingOperationCounts;
        private int _monitorUiUpdatePending;
        private int _isUpdatingTowerLight;
        private readonly IDataService _dataService;
        private readonly LogBufferService _logBuffer;
        private readonly AutoExportService _autoExport;
        private TowerLightService _towerLight;
        private string _lastTowerLightState = "";
        private readonly CancellationTokenSource _lifecycleCts = new();
        private Task _databaseInitializationTask = Task.CompletedTask;
        private Task _towerInitializationTask = Task.CompletedTask;
        private Task _startupCleanupTask = Task.CompletedTask;

        /// <summary>
        /// 当前允许连接/发现的设备 Id 集合。自动待机与要求在线设备在内，停用设备排除。
        /// </summary>
        private readonly ConcurrentDictionary<int, byte> _autoReconnectIds = new();

        /// <summary>
        /// 正在执行重连任务的设备 Id（防止同一设备并发重连）
        /// </summary>
        private readonly ConcurrentDictionary<int, byte> _reconnectingIds = new();

        /// <summary>正在执行手动/启动探测的设备 Id，防止监控节拍并发安排第二条连接链。</summary>
        private readonly ConcurrentDictionary<int, byte> _connectingIds = new();

        internal sealed class ReconnectState
        {
            public int Attempt;
            public long NextRetryTimestamp;
            public DateTimeOffset? NextRetryAt;
            public string LastReason = "";
            public long ScheduleVersion;
        }

        private readonly ConcurrentDictionary<int, ReconnectState> _reconnectStates = new();
        private readonly SemaphoreSlim _reconnectGate = new(1, 1);
        private const int ReconnectJitterPercent = 20;
        private static readonly ThreadLocal<Random> ReconnectRandom = new(() => new Random());

        /// <summary>
        /// PLC 点位变化可能很频繁，主界面只需要展示累计次数。
        /// 这里先在线程安全字典里累计，再由低频定时器批量刷到 UI，避免 Dispatcher 队列被单条更新淹没。
        /// </summary>
        private readonly ConcurrentDictionary<int, int> _pendingOperationCountDeltas = new();

        // #region agent log - Hypothesis A: 统计10秒内状态变化次数
        public static int DbgStateChangeCount = 0;
        public int PendingOperationCountUpdateDevices => _pendingOperationCountDeltas.Count;
        public bool HasPendingMonitorUiUpdate => _monitorUiUpdatePending == 1;
        // #endregion
        /// <summary>
        /// id → Device 快速查找表，避免在线程池线程中遍历 ObservableCollection
        /// </summary>
        private readonly Dictionary<int, Device> _deviceMap = new();

        public ReadOnlyObservableCollection<Device> Devices { get; }

        /// <summary>
        /// 仅供视频录制的隔离演示模式。启用时不会创建 PLC 传输或三色灯连接。
        /// </summary>
        public bool IsDemoVideoMode { get; }

        /// <summary>
        /// 设备状态变化事件（掉线或恢复）
        /// </summary>
        public event EventHandler<DeviceStatusChangeEventArgs> DeviceStatusChanged;

        /// <summary>
        /// 掉线设备列表
        /// </summary>
        public ObservableCollection<Device> OfflineDevices { get; } = new();

        /// <summary>
        /// 是否有设备掉线
        /// </summary>
        [ObservableProperty]
        private bool _hasOfflineDevices;

        /// <summary>
        /// 掉线设备数量
        /// </summary>
        [ObservableProperty]
        private int _offlineDeviceCount;

        /// <summary>
        /// 当前是否有温度报警（红灯/黄灯）。用于驱动主界面"消音/复位"按钮的可见性。
        /// </summary>
        [ObservableProperty]
        private bool _hasActiveAlarm;

        [ObservableProperty]
        private bool _isAlarmAcknowledged;

        [ObservableProperty]
        private bool _isDatabaseHealthy;

        [ObservableProperty]
        private string _databaseHealthText = "数据库启动中";

        [ObservableProperty]
        private int _pendingDatabaseLogCount;

        [ObservableProperty]
        private long _spooledDatabaseLogCount;

        [ObservableProperty]
        private long _droppedDatabaseLogCount;

        [ObservableProperty]
        private long _deadLetterDatabaseLogCount;

        [ObservableProperty]
        private DateTime? _lastDatabaseWriteTime;

        [ObservableProperty]
        private bool _isAutoExportEnabled;

        [ObservableProperty]
        private bool _isAutoExportHealthy = true;

        [ObservableProperty]
        private string _autoExportHealthText = "自动导出未启用";

        [ObservableProperty]
        private int _pendingAutoExportLogCount;

        [ObservableProperty]
        private long _droppedAutoExportLogCount;

        [ObservableProperty]
        private DateTime? _lastAutoExportWriteTime;

        private bool _isBuzzerMuted; // 方式B：消音标志

        /// <summary>
        /// 三色灯复位：方式B
        /// 仅关闭蜂鸣器，如果当前超温，红灯继续保持常亮。
        /// 直到所有设备温度降回正常，才会自动清除消音状态，下次再超温时重新响铃。
        /// </summary>
        public void AcknowledgeAlarm()
        {
            if (!HasActiveAlarm)
                return;
            _isBuzzerMuted = true;
            IsAlarmAcknowledged = true;
            _lastTowerLightState = ""; // 强制下次更新重新下发指令

            // 立即触发一次状态更新
            _ = UpdateTowerLightAsync();
        }

        public DeviceManagerService()
        {
            IsDemoVideoMode = App.IsDemoVideoMode;
            _wrappers = new ObservableCollection<DevicePlcWrapper>();
            _devices = new ObservableCollection<Device>();
            Devices = new ReadOnlyObservableCollection<Device>(_devices);

            _dataService = new DataService();
            _logBuffer = new LogBufferService();
            _logBuffer.HealthChanged += OnStorageHealthChanged;
            _autoExport = new AutoExportService();
            _autoExport.HealthChanged += OnAutoExportHealthChanged;
            IsAutoExportEnabled = !string.IsNullOrWhiteSpace(_autoExport.ExportPath);
            AutoExportHealthText = IsAutoExportEnabled ? "自动导出等待首次写入" : "自动导出未启用";

            // DB 初始化完成后才允许 LogBuffer 写入，避免表不存在导致数据丢失
            _databaseInitializationTask = Task.Run(async () =>
            {
                try
                {
                    await _dataService.InitializeAsync(_lifecycleCts.Token);
                    _lifecycleCts.Token.ThrowIfCancellationRequested();
                    _logBuffer.SetDatabaseReady();
                    System.Diagnostics.Debug.WriteLine("[DeviceManager] DB 初始化完成，LogBuffer 写入已启用");
                }
                catch (OperationCanceledException) when (_lifecycleCts.IsCancellationRequested)
                {
                    // 程序退出时不再发布初始化结果。
                }
                catch (Exception ex)
                {
                    _logBuffer.SetDatabaseUnavailable($"数据库初始化失败：{ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"[DeviceManager] DB 初始化失败: {ex.Message}");
                }
            });

            // 三色灯初始化移到后台线程，WMI 串口扫描可能耗时数秒甚至数十秒，不能阻塞 UI
            if (!IsDemoVideoMode)
            {
                _towerInitializationTask = Task.Run(() =>
                {
                    TowerLightService initialized = null;
                    try
                    {
                        initialized = InitializeTowerLight();
                        if (_stopped || _lifecycleCts.IsCancellationRequested)
                        {
                            initialized?.Dispose();
                            return;
                        }

                        var previous = Interlocked.Exchange(ref _towerLight, initialized);
                        initialized = null;
                        previous?.Dispose();

                        // 关闭动作可能恰好发生在上面的检查与发布之间，再检查一次消除尾部泄漏。
                        if (_stopped || _lifecycleCts.IsCancellationRequested)
                            Interlocked.Exchange(ref _towerLight, null)?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        initialized?.Dispose();
                        System.Diagnostics.Debug.WriteLine($"[三色灯] 后台初始化异常: {ex.Message}");
                    }
                });
            }

            InitializeDevices();

            // 启动监控定时器（每5秒检查一次）
            _monitorTimer = new System.Timers.Timer(5000);
            _monitorTimer.Elapsed += OnMonitorTimerElapsed;
            _monitorTimer.AutoReset = true;
            _monitorTimer.Start();

            // 数据库历史数据清理（每小时一次，删除 15 天前的温度/操作日志）
            _cleanupTimer = new System.Timers.Timer(TimeSpan.FromHours(1).TotalMilliseconds);
            _cleanupTimer.Elapsed += OnCleanupTimerElapsed;
            _cleanupTimer.AutoReset = true;
            _cleanupTimer.Start();

            // 操作次数 UI 刷新节流：PLC 变化日志仍然逐条入库，但主界面计数 1 秒批量刷新一次即可。
            _operationCountFlushTimer = new System.Timers.Timer(1000);
            _operationCountFlushTimer.Elapsed += OnOperationCountFlushTimerElapsed;
            _operationCountFlushTimer.AutoReset = true;
            _operationCountFlushTimer.Start();

            // 启动后立即异步清理一次，避免长期未运行的实例堆积大量历史
            _startupCleanupTask = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), _lifecycleCts.Token);
                    await CleanupOldDataSafelyAsync(_lifecycleCts.Token);
                }
                catch (OperationCanceledException) when (_lifecycleCts.IsCancellationRequested)
                {
                    // 正常退出。
                }
            });
        }

        private void OnCleanupTimerElapsed(object sender, ElapsedEventArgs e)
        {
            // 防重入：上一轮清理未完成时跳过
            if (Interlocked.Exchange(ref _isCleaning, 1) == 1)
                return;

            _ = CleanupOldDataSafelyAsync().ContinueWith(_ =>
                Interlocked.Exchange(ref _isCleaning, 0));
        }

        private async Task CleanupOldDataSafelyAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await _dataService.CleanOldDataAsync(cancellationToken);
                System.Diagnostics.Debug.WriteLine($"[数据清理] 已删除 15 天前的历史数据");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[数据清理] 失败: {ex.Message}");
            }
        }

        private void OnMonitorTimerElapsed(object sender, ElapsedEventArgs e)
        {
            // 防重入：_isMonitoring 在 MonitorDeviceStatusAsync 完成后才重置
            if (Interlocked.Exchange(ref _isMonitoring, 1) == 1)
                return;

            _ = MonitorDeviceStatusAsync().ContinueWith(_ =>
                Interlocked.Exchange(ref _isMonitoring, 0));
        }

        /// <summary>
        /// 监控所有设备状态（异步执行）
        /// </summary>
        private Task MonitorDeviceStatusAsync()
        {
            // #region agent log - Hypothesis B/E: 监控轮询耗时
            var _dbgMonitorSw = System.Diagnostics.Stopwatch.StartNew();
            // #endregion
            if (_stopped)
                return Task.CompletedTask;

            var snapshots = new List<(DevicePlcWrapper Wrapper, bool IsConnected, float Temperature, bool IsTemperatureDelayed, DateTime LastTemperatureSampleTime)>();
            foreach (var wrapper in _wrappers.ToList())
            {
                var status = wrapper.PlcService.CurrentStatus;
                var isCurrentlyConnected = status.IsConnected;
                var isTemperatureDelayed = false;
                if (isCurrentlyConnected && wrapper.PlcService is MitsubishiPlcService mitsubishi)
                {
                    isTemperatureDelayed = mitsubishi.IsTemperatureSampleDelayed(out _);
                    if (mitsubishi.TryDisconnectIfTemperatureStale(out var tempAge))
                    {
                        isCurrentlyConnected = false;
                        isTemperatureDelayed = false;
                        Views.MainWindow.DbgLog("DeviceManagerService:TemperatureStale", "温度采样长时间未更新，触发自动重连", new
                        {
                            device = wrapper.Device.Name,
                            wrapper.Device.IpAddress,
                            ageSeconds = Math.Round(tempAge.TotalSeconds, 1),
                            intervalMs = mitsubishi.Config.TemperatureInterval,
                            staleTimeoutMs = mitsubishi.Config.TemperatureStaleTimeout,
                            lastTemperatureSampleTime = status.LastTemperatureSampleTime
                        }, "TEMP");
                    }
                }

                var temp = wrapper.PlcService.CurrentStatus.Temperature;
                snapshots.Add((wrapper, isCurrentlyConnected, temp, isTemperatureDelayed, status.LastTemperatureSampleTime));

                if (!isCurrentlyConnected)
                {
                    TryScheduleReconnect(wrapper);
                }
            }

            var dispatcher = App.Current?.Dispatcher;
            if (dispatcher != null && Interlocked.Exchange(ref _monitorUiUpdatePending, 1) == 0)
            {
                try
                {
                    dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        try
                        {
                            if (_stopped) return;

                            var offlineList = new List<Device>();
                            foreach (var snapshot in snapshots)
                            {
                                var device = snapshot.Wrapper.Device;
                                var wasOnline = device.IsOnline;
                                var currentStatus = snapshot.Wrapper.PlcService.CurrentStatus;

                                if (currentStatus.IsConnected)
                                {
                                    // 重连刚建立但本代还没有采到温度时，不得把旧值重新包装成实时值。
                                    var hasCurrentSample = currentStatus.LastTemperatureSampleTime != default;
                                    var tempChanged = hasCurrentSample &&
                                        Math.Abs(device.CurrentTemperature - currentStatus.Temperature) > 0.05f;
                                    if (hasCurrentSample)
                                    {
                                        if (tempChanged)
                                            device.CurrentTemperature = currentStatus.Temperature;
                                        if (!device.HasTemperatureSample)
                                            device.HasTemperatureSample = true;
                                        if (device.LastUpdateTime != currentStatus.LastTemperatureSampleTime)
                                            device.LastUpdateTime = currentStatus.LastTemperatureSampleTime;
                                    }
                                    // 使用 PlcStatus.IsAlarm（已按设定温度判断，非硬编码 90°C）
                                    var hasAlert = snapshot.Wrapper.PlcService.CurrentStatus.IsAlarm;
                                    if (device.HasAlert != hasAlert)
                                        device.HasAlert = hasAlert;
                                    if (!device.IsOnline)
                                        device.IsOnline = true;
                                    device.IsConnecting = false;
                                    device.HasCommunicationFault = false;
                                    if (device.IsReconnecting)
                                        device.IsReconnecting = false;
                                    var isTemperatureDelayedNow = snapshot.Wrapper.PlcService is MitsubishiPlcService currentMitsubishi &&
                                        currentMitsubishi.IsTemperatureSampleDelayed(out _);
                                    var shouldShowStale = isTemperatureDelayedNow || !hasCurrentSample;
                                    if (device.IsTemperatureStale != shouldShowStale)
                                        device.IsTemperatureStale = shouldShowStale;

                                    var now = DateTime.Now;

                                    if (!wasOnline)
                                    {
                                        SafeEventDispatcher.Invoke(this, DeviceStatusChanged, new DeviceStatusChangeEventArgs
                                        {
                                            Device = device,
                                            WasOnline = false,
                                            IsOnline = true,
                                            ChangeTime = now
                                        });
                                        System.Diagnostics.Debug.WriteLine($"[恢复] {device.Name} ({device.IpAddress})");
                                    }
                                }
                                else
                                {
                                    if (DeviceMonitoringPolicy.IsExpectedOnline(device.MonitoringMode))
                                        offlineList.Add(device);
                                    if (device.IsOnline)
                                        device.IsOnline = false;
                                    // 保留最后一个有效温度供现场判断，但必须明确打上过期标记。
                                    if (device.HasTemperatureSample && !device.IsTemperatureStale)
                                        device.IsTemperatureStale = true;
                                    var isReconnecting = _reconnectingIds.ContainsKey(device.Id);
                                    if (device.IsReconnecting != isReconnecting)
                                        device.IsReconnecting = isReconnecting;
                                    var isConnecting = _connectingIds.ContainsKey(device.Id);
                                    if (device.IsConnecting != isConnecting)
                                        device.IsConnecting = isConnecting;
                                    device.HasCommunicationFault =
                                        DeviceMonitoringPolicy.IsExpectedOnline(device.MonitoringMode) &&
                                        _autoReconnectIds.ContainsKey(device.Id) &&
                                        !isReconnecting &&
                                        !isConnecting;

                                    if (wasOnline)
                                    {
                                        SafeEventDispatcher.Invoke(this, DeviceStatusChanged, new DeviceStatusChangeEventArgs
                                        {
                                            Device = device,
                                            WasOnline = true,
                                            IsOnline = false,
                                            ChangeTime = DateTime.Now
                                        });
                                        System.Diagnostics.Debug.WriteLine($"[掉线] {device.Name} ({device.IpAddress})");
                                    }
                                }
                            }

                            UpdateOfflineDevicesOnUiThread(offlineList);
                        }
                        finally
                        {
                            Interlocked.Exchange(ref _monitorUiUpdatePending, 0);
                        }
                    }));
                }
                catch
                {
                    Interlocked.Exchange(ref _monitorUiUpdatePending, 0);
                }
            }

            // 串口发送含 Thread.Sleep(100)，不阻塞当前监控线程
            _ = UpdateTowerLightAsync();

            // #region agent log - Hypothesis B/E: 监控耗时超过1秒报警
            _dbgMonitorSw.Stop();
            if (_dbgMonitorSw.ElapsedMilliseconds > 1000)
            {
                Views.MainWindow.DbgLog("DeviceManagerService:Monitor", "监控轮询耗时过长", new
                {
                    elapsedMs = _dbgMonitorSw.ElapsedMilliseconds,
                    offlineCount = snapshots.Count(s => !s.IsConnected)
                }, "B/E");
            }
            // #endregion

            return Task.CompletedTask;
        }

        private void UpdateOfflineDevicesOnUiThread(List<Device> offlineList)
        {
            var changed = OfflineDevices.Count != offlineList.Count;
            if (!changed)
            {
                for (int i = 0; i < offlineList.Count; i++)
                {
                    if (!ReferenceEquals(OfflineDevices[i], offlineList[i]))
                    {
                        changed = true;
                        break;
                    }
                }
            }

            if (changed)
            {
                OfflineDevices.Clear();
                foreach (var device in offlineList)
                {
                    OfflineDevices.Add(device);
                }
            }

            OfflineDeviceCount = offlineList.Count;
            HasOfflineDevices = offlineList.Count > 0;
        }

        private void OnOperationCountFlushTimerElapsed(object sender, ElapsedEventArgs e)
        {
            if (_stopped || Interlocked.Exchange(ref _isFlushingOperationCounts, 1) == 1)
                return;

            var deltas = DrainOperationCountDeltas();
            if (deltas.Count == 0)
            {
                Interlocked.Exchange(ref _isFlushingOperationCounts, 0);
                return;
            }

            var dispatcher = App.Current?.Dispatcher;
            if (dispatcher == null)
            {
                RestoreOperationCountDeltas(deltas);
                Interlocked.Exchange(ref _isFlushingOperationCounts, 0);
                return;
            }

            try
            {
                dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    try
                    {
                        foreach (var kvp in deltas)
                        {
                            if (_deviceMap.TryGetValue(kvp.Key, out var device))
                                device.TodayOperationCount += kvp.Value;
                        }
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _isFlushingOperationCounts, 0);
                    }
                }));
            }
            catch
            {
                RestoreOperationCountDeltas(deltas);
                Interlocked.Exchange(ref _isFlushingOperationCounts, 0);
            }
        }

        private Dictionary<int, int> DrainOperationCountDeltas()
        {
            var deltas = new Dictionary<int, int>();
            foreach (var key in _pendingOperationCountDeltas.Keys)
            {
                if (_pendingOperationCountDeltas.TryRemove(key, out var count) && count > 0)
                    deltas[key] = count;
            }
            return deltas;
        }

        private void RestoreOperationCountDeltas(Dictionary<int, int> deltas)
        {
            foreach (var kvp in deltas)
            {
                _pendingOperationCountDeltas.AddOrUpdate(
                    kvp.Key,
                    kvp.Value,
                    (_, current) => current + kvp.Value);
            }
        }

        /// <summary>
        /// 后台异步重连一台离线设备。断线事件和监控定时器都只会唤醒同一个
        /// 带指数退避的调度器，不能因为一次 2 秒超时就立即创建下一代连接。
        /// </summary>
        private void TryScheduleReconnect(DevicePlcWrapper wrapper)
        {
            int id = wrapper.Device.Id;
            var mode = wrapper.Device.MonitoringMode;
            if (!DeviceMonitoringPolicy.IsConnectionAuthorized(mode) ||
                !_autoReconnectIds.ContainsKey(id))
                return;
            if (_connectingIds.ContainsKey(id))
                return;

            // 底层同步调用已经达到熔断上限时不再制造无意义的重连任务；
            // 迟到任务释放后熔断器会自动闭合，下一轮监控再尝试。
            if (wrapper.PlcService is MitsubishiPlcService mitsubishi &&
                mitsubishi.IsCircuitBreakerOpen)
            {
                SetDeviceConnectionActivity(
                    wrapper.Device,
                    connecting: false,
                    reconnecting: false,
                    communicationFault: DeviceMonitoringPolicy.IsExpectedOnline(mode));
                return;
            }

            if (!_reconnectingIds.TryAdd(id, 0))
                return; // 已经有重连任务或退避等待在跑

            var reconnectState = _reconnectStates.GetOrAdd(id, _ => new ReconnectState());
            int attempt;
            TimeSpan delay;
            long scheduleVersion;
            DateTimeOffset? scheduledAt;
            lock (reconnectState)
            {
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (reconnectState.NextRetryTimestamp > now)
                {
                    _reconnectingIds.TryRemove(id, out _);
                    return;
                }

                attempt = Math.Min(1000, reconnectState.Attempt + 1);
                delay = ComputeReconnectDelay(mode, attempt);
                reconnectState.Attempt = attempt;
                scheduleVersion = ++reconnectState.ScheduleVersion;
                reconnectState.NextRetryAt = DateTimeOffset.UtcNow + delay;
                reconnectState.NextRetryTimestamp = now + (long)(delay.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
                scheduledAt = reconnectState.NextRetryAt;
            }

            SetReconnectInfo(wrapper.Device, attempt, scheduledAt?.LocalDateTime);
            SetDeviceConnectionActivity(wrapper.Device, connecting: false, reconnecting: true, communicationFault: false);

            var w = wrapper;
            _ = Task.Run(async () =>
            {
                var restored = false;
                var failureReason = "";
                try
                {
                    if (_stopped || !_autoReconnectIds.ContainsKey(id) ||
                        !IsReconnectScheduleCurrent(reconnectState, scheduleVersion))
                        return;

                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay, _lifecycleCts.Token).ConfigureAwait(false);

                    await _reconnectGate.WaitAsync(_lifecycleCts.Token).ConfigureAwait(false);
                    try
                    {
                        if (_stopped || !_autoReconnectIds.ContainsKey(id) ||
                            !IsReconnectScheduleCurrent(reconnectState, scheduleVersion))
                            return;

                        System.Diagnostics.Debug.WriteLine($"[自动重连] 第{attempt}次尝试 {w.Device.Name} ({w.Device.IpAddress})");
                        var ok = await w.PlcService.ConnectAsync().ConfigureAwait(false);
                        if (ok)
                        {
                            restored = TryStartAcquisitionIfStillAuthorized(w, id, "自动重连");
                            if (restored)
                                System.Diagnostics.Debug.WriteLine($"[自动重连] ✓ {w.Device.Name} 已建立并等待有效样本");
                        }
                        else
                        {
                            failureReason = (w.PlcService as MitsubishiPlcService)?.LastConnectionError ?? "连接失败";
                            System.Diagnostics.Debug.WriteLine($"[自动重连] ✗ {w.Device.Name} 第{attempt}次失败: {failureReason}");
                        }
                    }
                    finally
                    {
                        _reconnectGate.Release();
                    }
                }
                catch (OperationCanceledException) when (_lifecycleCts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    failureReason = ex.Message;
                    System.Diagnostics.Debug.WriteLine($"[自动重连] {w.Device.Name} 异常: {ex.Message}");
                }
                finally
                {
                    var scheduleCurrent = IsReconnectScheduleCurrent(reconnectState, scheduleVersion);
                    _reconnectingIds.TryRemove(id, out _);

                    // finally 中不能 return；过期任务只清理自己的占位，不得覆盖新一代
                    // 调度状态，也不得在生命周期已停止后再次安排重试。
                    if (scheduleCurrent && !_stopped &&
                        !_lifecycleCts.IsCancellationRequested &&
                        _autoReconnectIds.ContainsKey(id))
                    {
                        // 失败后立即开放调度闸门。退避只由下一次调度任务内的
                        // Task.Delay 应用一次；这里若再推迟一个退避时长，同一份
                        // 延迟会被闸门和任务内等待各消耗一次，实际重连间隔
                        // 会变成策略值的约两倍。下一次真实重试时间由下一轮
                        // 调度（≤5 秒监控节拍）计算并刷新到界面。
                        var nextAttempt = ApplyReconnectOutcome(
                            reconnectState,
                            restored,
                            failureReason);
                        SetReconnectInfo(w.Device, nextAttempt, null);
                        UpdateDeviceOnlineState(w);
                    }
                }
            });
        }

        /// <summary>
        /// 推进一次重连尝试结束后的调度状态。失败时必须立即开放闸门，
        /// 退避由下一次调度任务内的延迟单独承担；恢复时清零等待重试。
        /// 返回当前尝试次数供界面显示。
        /// </summary>
        internal static int ApplyReconnectOutcome(
            ReconnectState state,
            bool restored,
            string failureReason)
        {
            lock (state)
            {
                state.LastReason = failureReason ?? "";
                state.NextRetryTimestamp = restored ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
                state.NextRetryAt = null;
                return state.Attempt;
            }
        }

        private static bool IsReconnectScheduleCurrent(ReconnectState state, long scheduleVersion)
        {
            lock (state)
                return state.ScheduleVersion == scheduleVersion;
        }

        private void CancelScheduledReconnect(int deviceId)
        {
            _reconnectingIds.TryRemove(deviceId, out _);
            if (!_reconnectStates.TryGetValue(deviceId, out var state))
                return;

            lock (state)
            {
                state.ScheduleVersion++;
                state.NextRetryTimestamp = 0;
                state.NextRetryAt = null;
                state.Attempt = 0;
                state.LastReason = "";
            }

            if (GetDevice(deviceId) is { } device)
                SetReconnectInfo(device, 0, null);
        }

        private static TimeSpan ComputeReconnectDelay(DeviceMonitoringMode mode, int attempt)
        {
            var policyDelay = DeviceMonitoringPolicy.GetReconnectDelay(mode, attempt);
            if (policyDelay == Timeout.InfiniteTimeSpan)
                return policyDelay;
            var jitter = 1d + ((ReconnectRandom.Value?.NextDouble() ?? 0.5d) * 2d - 1d) *
                ReconnectJitterPercent / 100d;
            return TimeSpan.FromSeconds(Math.Max(1d, policyDelay.TotalSeconds * jitter));
        }

        private void MarkReconnectHealthy(int deviceId, TemperatureSampleEventArgs sample)
        {
            if (sample == null || sample.Quality != TemperatureSampleQuality.Valid)
                return;

            if (!_reconnectStates.TryGetValue(deviceId, out var state))
                return;

            lock (state)
            {
                state.Attempt = 0;
                state.NextRetryTimestamp = 0;
                state.NextRetryAt = null;
                state.LastReason = "";
            }

            var device = GetDevice(deviceId);
            if (device != null)
                SetReconnectInfo(device, 0, null);
        }

        private static void SetReconnectInfo(Device device, int attempt, DateTime? nextRetryAt)
        {
            if (device == null)
                return;

            void Apply()
            {
                device.ReconnectAttempt = Math.Max(0, attempt);
                device.NextReconnectAt = nextRetryAt;
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
                Apply();
            else
                dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Apply));
        }

        private void OnPlcConnectionStateChanged(
            DevicePlcWrapper wrapper,
            bool isConnected)
        {
            UpdateDeviceOnlineState(wrapper);
            if (!isConnected && !_stopped)
                TryScheduleReconnect(wrapper);
        }

        private void OnPlcConnectionSnapshotChanged(
            DevicePlcWrapper wrapper,
            PlcConnectionSnapshot snapshot)
        {
            if (snapshot == null)
                return;

            UpdateDeviceOnlineState(wrapper);
            // 事件在后台线程发布，旧代的迟到关闭/异常可能晚于新代上线到达。
            // 仅允许当前服务代次触发自动重连，避免新连接刚建立就被旧事件踢回退避。
            var currentSnapshot = wrapper.PlcService.ConnectionSnapshot;
            if (currentSnapshot == null || !ReferenceEquals(currentSnapshot, snapshot))
                return;

            if (!_stopped &&
                snapshot.Phase is PlcConnectionPhase.CommunicationFault or PlcConnectionPhase.Disconnected)
                TryScheduleReconnect(wrapper);
        }

        private bool TryStartAcquisitionIfStillAuthorized(
            DevicePlcWrapper wrapper,
            int deviceId,
            string source)
        {
            bool IsAuthorizedAndConnected()
                => !_stopped &&
                   _autoReconnectIds.ContainsKey(deviceId) &&
                   wrapper.PlcService.CurrentStatus.IsConnected;

            if (!IsAuthorizedAndConnected())
            {
                if (wrapper.PlcService.CurrentStatus.IsConnected)
                    wrapper.PlcService.Disconnect();
                return false;
            }

            try
            {
                wrapper.PlcService.StartAcquisition();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[{source}] {wrapper.Device.Name} StartAcquisition 失败: {ex.Message}");
                wrapper.PlcService.Disconnect();
                return false;
            }

            // StartAcquisition 与用户断开可能并发，启动后再次校验。
            if (!IsAuthorizedAndConnected())
            {
                wrapper.PlcService.Disconnect();
                return false;
            }

            return true;
        }

        /// <summary>
        /// 初始化 USB 三色灯（TC60），自动识别串口，失败时不影响主功能
        /// </summary>
        private TowerLightService InitializeTowerLight()
        {
            try
            {
                var light = new TowerLightService();
                if (light.TryConnect())
                {
                    System.Diagnostics.Debug.WriteLine($"[三色灯] 已连接 {light.PortName}");
                    return light;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[三色灯] 初始化失败: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// 强制刷新三色灯状态（阈值修改后调用，重置状态缓存避免被防重入跳过）
        /// </summary>
        public void ForceUpdateTowerLight()
        {
            _lastTowerLightState = ""; // 强制下次重新下发指令
            _ = UpdateTowerLightAsync();
        }

        /// <summary>
        /// 主程序三色灯当前占用的串口名（未连接成功时为 null）
        /// </summary>
        public string TowerLightPortName => _towerLight?.PortName;

        /// <summary>
        /// 主程序三色灯串口是否已打开（设置页用来判断该口是否被本程序占用）
        /// </summary>
        public bool IsTowerLightSerialOpen => _towerLight?.IsConnected ?? false;

        /// <summary>
        /// 设置页点灯测试：复用主程序常驻的三色灯实例（串口独占，新开实例会打开失败）。
        /// 红→黄→绿→灭各停留 800ms，结束后强制按真实状态恢复灯色。
        /// 返回 null 表示成功，否则为错误信息。
        /// </summary>
        public async Task<string> TestTowerLightAsync()
        {
            var light = _towerLight;
            if (light == null || !light.IsConnected)
                return "主程序三色灯未连接";

            try
            {
                foreach (var cmd in new[] { "Red", "Yellow", "Green", "Off" })
                {
                    if (!await light.SendAsync(cmd).ConfigureAwait(false))
                        return light.LastError;
                    await Task.Delay(800).ConfigureAwait(false);
                }
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
            finally
            {
                // 测试改变了实际灯色，清掉状态缓存让监控立即按真实状态重新下发
                ForceUpdateTowerLight();
            }
        }

        /// <summary>
        /// 根据所有设备状态更新三色灯：
        /// 任意参与监控且新鲜的温度超过报警阈值 → 红灯 + 蜂鸣器；
        /// 要求在线设备离线/过期，或已在线的自动待机设备数据过期 → 黄灯；
        /// 自动待机设备正常关机、停用设备均不参与灯态。
        /// </summary>
        public async Task UpdateTowerLightAsync()
        {
            await Task.CompletedTask.ConfigureAwait(false);
            if (_towerLight == null) return;
            if (Interlocked.Exchange(ref _isUpdatingTowerLight, 1) == 1)
                return;

            try
            {
                var towerStates = new List<TowerLightPolicy.DeviceState>();

                foreach (var wrapper in _wrappers.ToList())
                {
                    var device = wrapper.Device;
                    var isFresh = device.IsOnline &&
                                  device.HasTemperatureSample &&
                                  !device.IsTemperatureStale &&
                                  !device.IsReconnecting;
                    float temp = wrapper.PlcService.CurrentStatus.Temperature;
                    float threshold = wrapper.PlcService.Config.TemperatureThreshold;
                    towerStates.Add(new TowerLightPolicy.DeviceState(
                        device.MonitoringMode,
                        device.IsOnline,
                        isFresh,
                        isFresh && temp > threshold));
                }

                var anyAlarm = towerStates.Any(state =>
                    DeviceMonitoringPolicy.ParticipatesInTowerLight(
                        state.MonitoringMode,
                        state.IsOnline) &&
                    state.IsFresh &&
                    state.HasActiveAlarm);

                var alarmWasActive = HasActiveAlarm;
                if (anyAlarm && !alarmWasActive)
                {
                    _isBuzzerMuted = false;
                    IsAlarmAcknowledged = false;
                }

                // 更新 HasActiveAlarm（供 UI 确认/消音按钮显示）
                if (HasActiveAlarm != anyAlarm)
                {
                    var dispatcher = App.Current?.Dispatcher;
                    if (dispatcher != null)
                        _ = dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                            new Action(() => HasActiveAlarm = anyAlarm));
                }

                // 如果所有设备都不在报警状态，清除消音标志，准备迎接下一次报警
                if (!anyAlarm)
                {
                    _isBuzzerMuted = false;
                    IsAlarmAcknowledged = false;
                }

                var decision = TowerLightPolicy.Decide(
                    towerStates,
                    hasSystemFault: !IsDatabaseHealthy ||
                                    DroppedDatabaseLogCount > 0 ||
                                    DeadLetterDatabaseLogCount > 0 ||
                                    (IsAutoExportEnabled &&
                                     (!IsAutoExportHealthy || DroppedAutoExportLogCount > 0)),
                    isBuzzerMuted: _isBuzzerMuted);
                var desiredState = decision.ToString();

                // 状态没变不重复写串口
                if (string.Equals(_lastTowerLightState, desiredState, StringComparison.Ordinal))
                    return;

                // 只提交最新期望状态；串口 Open/Write/Read 由 TowerLightService 的
                // 专用后台状态泵执行，不能在 Dispatcher 或监控线程上同步操作。
                _towerLight.QueueDesiredState(desiredState);
                _lastTowerLightState = desiredState;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[三色灯] 更新异常: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _isUpdatingTowerLight, 0);
            }
        }

        /// <summary>
        /// 初始化设备配置
        /// 配置4台设备，IP地址从已验证的 config.json 读取
        /// </summary>
        private void InitializeDevices()
        {
            // 配置4台设备（IP 从 config.json 的 DeviceIPs 字段读取，默认 192.168.1.5/10/15/20）
            var ips = AppConfig.DeviceIPs;
            var deviceConfigs = new[]
            {
                new { Id = 1, Name = "一号4.0改性设备", Location = "滤芯车间一楼", Ip = ips.Length > 0 ? ips[0] : "192.168.1.5"  },
                new { Id = 2, Name = "二号4.0改性设备", Location = "滤芯车间一楼", Ip = ips.Length > 1 ? ips[1] : "192.168.1.10" },
                new { Id = 3, Name = "三号4.0改性设备", Location = "滤芯车间一楼", Ip = ips.Length > 2 ? ips[2] : "192.168.1.15" },
                new { Id = 4, Name = "四号4.0改性设备", Location = "滤芯车间一楼", Ip = ips.Length > 3 ? ips[3] : "192.168.1.20" },
            };

            foreach (var config in deviceConfigs)
            {
                var device = new Device
                {
                    Id = config.Id,
                    Name = config.Name,
                    Location = config.Location,
                    IpAddress = config.Ip,
                    Port = 5000,
                    IsOnline = false,
                    IsDemoMode = IsDemoVideoMode,
                    MonitoringMode = config.Id - 1 < AppConfig.DeviceMonitoringModes.Length
                        ? AppConfig.DeviceMonitoringModes[config.Id - 1]
                        : DeviceMonitoringMode.AutoStandby,
                    CurrentTemperature = 0,
                    HasAlert = false,
                    TodayOperationCount = 0,
                    LastUpdateTime = DateTime.Now
                };

                // 为每台设备创建独立的PLC配置
                var plcConfig = config.Id == 1
                    ? CreateDevice1Config(device)
                    : config.Id == 3
                    ? CreateDevice3Config(device)
                    : config.Id == 4
                    ? CreateDevice4Config(device)
                    : new PlcConfig
                    {
                        Name = device.Name,
                        IpAddress = device.IpAddress,
                        Port = device.Port,
                        ActualTemperatureDefinition = new TemperatureRegisterDefinition
                        {
                            Address = "D12",
                            DataType = PlcRegisterDataType.Int32,
                            Divisor = 10f
                        },
                        TargetTemperatureDefinition = new TemperatureRegisterDefinition
                        {
                            Address = "D210",
                            DataType = PlcRegisterDataType.Int32,
                            Divisor = 10f
                        }
                    };

                // 从持久化配置中恢复报警阈值（deviceIndex = Id-1）
                int deviceIndex = config.Id - 1;
                if (deviceIndex >= 0 && deviceIndex < AppConfig.DeviceThresholds.Length)
                    plcConfig.TemperatureThreshold = AppConfig.DeviceThresholds[deviceIndex];

                IPlcService plcService = IsDemoVideoMode
                    ? new DemoPlcService(plcConfig, device.Id)
                    : new MitsubishiPlcService(plcConfig);
                var wrapper = new DevicePlcWrapper(device, plcService);

                // 订阅该设备的IO点变化事件，写入数据库日志
                int capturedId = device.Id;
                plcService.StateChanged += (s, e) => OnPlcStateChanged(capturedId, e);

                // 订阅统一温度采样契约，管理层不依赖真实/演示实现类型。
                plcService.TemperatureSampled += (s, e) => OnTemperatureSampled(capturedId, e);

                // 使用结构化状态，区分 TCP、MC 协议验证、等待首样本和真正新鲜在线。
                plcService.ConnectionStateChangedDetailed += (s, args) =>
                    OnPlcConnectionSnapshotChanged(wrapper, args?.Snapshot);

                _devices.Add(device);
                _wrappers.Add(wrapper);
            }

            // 建立 id → Device 字典，供线程池回调中安全查找
            foreach (var d in _devices)
                _deviceMap[d.Id] = d;
        }

        /// <summary>
        /// 创建 1 号设备（一号 4.0 改性设备）的专用 PLC 配置
        /// </summary>
        private PlcConfig CreateDevice1Config(Device device)
        {
            return new PlcConfig
            {
                Name = device.Name,
                IpAddress = device.IpAddress,
                Port = device.Port,

                // --- X 输入点 (5 个) ---
                XStartAddress = "X0",
                XCount = 5,
                XPointLabels = new()
                {
                    { "X0", "启动" },
                    { "X1", "停止" },
                    { "X2", "反应槽极限液位" },
                    { "X3", "反应槽上限液位" },
                    { "X4", "反应槽下限液位" },
                },

                // --- Y 输出点 (6 个) ---
                YStartAddress = "Y0",
                YCount = 6,
                YPointLabels = new()
                {
                    { "Y0", "水泵运行" },
                    { "Y1", "反应槽加热" },
                    { "Y2", "反应槽纯水电磁阀" },
                    { "Y3", "反应槽出水电磁阀" },
                    { "Y4", "反应槽进水电磁阀" },
                    { "Y5", "排水电磁阀" },
                },

                // --- M 辅助继电器 ---
                // MAddressList 只保留界面需要显示/记录的业务散点；
                // MReadBlocks 可以覆盖中间地址以减少 TCP 请求，但中间位不能进入操作日志。
                MAddressList = new()
                {
                    "M1", "M2", "M3", "M4", "M5", "M6", "M11", "M12",
                    "M102", "M103",
                    "M110", "M115", "M120", "M130",
                    "M160", "M170", "M180",
                },
                MReadBlocks = new()
                {
                    new MReadBlock("M1",   12),   // M1-M12（合并自 M6+M11）
                    new MReadBlock("M102",  2),   // M102-M103
                    new MReadBlock("M110", 21),   // M110-M130（合并 4 个工艺阶段点）
                    new MReadBlock("M160", 21),   // M160-M180（合并 3 个工艺阶段点）
                },
                MPointLabels = new()
                {
                    { "M1", "水泵手动启动" },
                    { "M2", "反应槽手动加热" },
                    { "M3", "反应槽电磁阀手动纯水进水" },
                    { "M4", "反应槽电磁阀手动溶液出水" },
                    { "M5", "反应槽电磁阀手动溶液进水" },
                    { "M6", "排水电磁阀手动开启" },
                    { "M11", "反应槽手动循环" },
                    { "M12", "反应槽手动排水" },
                    { "M102", "自动启动" },
                    { "M103", "自动停止" },
                    { "M110", "反应槽进水" },
                    { "M115", "反应槽循环" },
                    { "M120", "反应槽加热" },
                    { "M130", "反应槽反应" },
                    { "M160", "冷水循环冲洗一" },
                    { "M170", "冷水循环冲洗二" },
                    { "M180", "反应结束" },
                },

                // --- 温度地址 ---
                TemperatureAddress = "D320",
                TargetTemperatureAddress = "D420", // 反应槽设定温度（用于超温报警判断）
                ActualTemperatureDefinition = new TemperatureRegisterDefinition
                {
                    Address = "D320",
                    DataType = PlcRegisterDataType.Int32,
                    Divisor = 10f
                },
                TargetTemperatureDefinition = new TemperatureRegisterDefinition
                {
                    Address = "D420",
                    DataType = PlcRegisterDataType.Int32,
                    Divisor = 10f
                },

                // --- 无热电偶电压 ---
                ThermocoupleAAddress = "",
                ThermocoupleBAddress = "",
                ThermocoupleCAddress = "",

                // --- C 寄存器（计数器/定时器） ---
                CRegisters = new()
                {
                    new CRegisterDef("C10", "反应槽循环时间", "分钟"),
                    new CRegisterDef("C20", "反应槽加热时间", "小时"),
                    new CRegisterDef("C30", "反应槽反应时间", "小时"),
                    new CRegisterDef("C40", "冷水冲洗时间一", "分钟"),
                    new CRegisterDef("C50", "冷水冲洗时间二", "分钟"),
                },
            };
        }

        /// <summary>
        /// 创建 3 号设备（三号 4.0 改性设备）的专用 PLC 配置
        /// </summary>
        private PlcConfig CreateDevice3Config(Device device)
        {
            return new PlcConfig
            {
                Name = device.Name,
                IpAddress = device.IpAddress,
                Port = device.Port,

                // X0-X7 连续读 8 点（X6 未接线，标签标为未用）
                XStartAddress = "X0",
                XCount = 8,
                XPointLabels = new()
                {
                    { "X0", "反应槽下限液位" },
                    { "X1", "反应槽上限液位" },
                    { "X2", "反应槽极限液位" },
                    { "X3", "暂存槽下限液位" },
                    { "X4", "暂存槽上限液位" },
                    { "X5", "暂存槽极限液位" },
                    { "X6", "急停开关" },
                    { "X7", "反应槽中线液位" },
                },

                // Y0-Y7 + Y10-Y12（连续读 11 点，Y10 未用）
                YStartAddress = "Y0",
                YCount = 11,
                YPointLabels = new()
                {
                    { "Y0", "暂存槽出水循环阀" },
                    { "Y1", "反应槽出水循环阀" },
                    { "Y2", "反应槽排水球阀" },
                    { "Y3", "暂存槽进水循环阀" },
                    { "Y4", "反应槽进水循环阀" },
                    { "Y5", "反应槽进水球阀" },
                    { "Y6", "暂存槽进水球阀" },
                    { "Y7", "水泵运行中信号" },
                    { "Y10", "（未用）" },
                    { "Y11", "反应槽排水泵" },
                    { "Y12", "反应槽加热信号" },
                },

                // 仅保留业务散点；批量读取块仍覆盖连续区间，读取后按地址映射回来。
                MAddressList = new()
                {
                    "M64", "M74", "M101", "M111", "M124", "M127", "M128", "M133",
                    "M204", "M205", "M206", "M207", "M208", "M209", "M210", "M211", "M212",
                    "M600", "M610", "M701",
                },
                MReadBlocks = new()
                {
                    new MReadBlock("M64",  70),   // M64-M133（合并 8 个散点）
                    new MReadBlock("M204",  9),   // M204-M212（保持不变）
                    new MReadBlock("M600", 102),  // M600-M701（合并 M600/M610/M701）
                },
                MPointLabels = new()
                {
                    { "M64", "步骤三 暂存槽转反应槽" },
                    { "M74", "步骤八 排水" },
                    { "M101", "步骤六 反应槽加水" },
                    { "M111", "步骤一 暂存槽加水指示灯" },
                    { "M124", "步骤二 暂存槽循环" },
                    { "M127", "步骤五 反应槽转暂存槽" },
                    { "M128", "步骤七 循环冲洗" },
                    { "M133", "步骤四 循环加温" },
                    { "M204", "反应槽手动加水" },
                    { "M205", "暂存槽手动加水" },
                    { "M206", "反应槽手动排水" },
                    { "M207", "暂存槽电磁阀溶液出水（手动）" },
                    { "M208", "反应槽电磁阀溶液出水（手动）" },
                    { "M209", "暂存槽电磁阀溶液进水（手动）" },
                    { "M210", "反应槽电磁阀溶液进水（手动）" },
                    { "M211", "手动反应槽水泵开启" },
                    { "M212", "手动反应槽加热" },
                    { "M600", "停止" },
                    { "M610", "复位" },
                    { "M701", "允许启动灯" },
                },

                ProcessStages = new()
                {
                    new ProcessStageDef("M111", "步骤一 暂存槽加水指示灯", "1"),
                    new ProcessStageDef("M124", "步骤二 暂存槽循环", "2"),
                    new ProcessStageDef("M64", "步骤三 暂存槽转反应槽", "3"),
                    new ProcessStageDef("M133", "步骤四 循环加温", "4"),
                    new ProcessStageDef("M127", "步骤五 反应槽转暂存槽", "5"),
                    new ProcessStageDef("M101", "步骤六 反应槽加水", "6"),
                    new ProcessStageDef("M128", "步骤七 循环冲洗", "7"),
                    new ProcessStageDef("M74", "步骤八 排水", "8"),
                },

                TemperatureAddress = "D10",
                TemperatureIsWord = true,        // D10为16位Word寄存器，非DINT，用ReadInt16读取
                TargetTemperatureAddress = "D280", // 与设备4一致，反应槽第一道设定温度
                ActualTemperatureDefinition = new TemperatureRegisterDefinition
                {
                    Address = "D10",
                    DataType = PlcRegisterDataType.Int16,
                    Divisor = 10f
                },
                TargetTemperatureDefinition = new TemperatureRegisterDefinition
                {
                    Address = "D280",
                    DataType = PlcRegisterDataType.Int16,
                    Divisor = 10f
                },
                ThermocoupleAAddress = "",
                ThermocoupleBAddress = "",
                ThermocoupleCAddress = "",

                CRegisters = new()
                {
                    new CRegisterDef("D1000", "暂存槽循环时间", ""),
                    new CRegisterDef("D1050", "恒温浸泡时间", ""),
                    new CRegisterDef("D1030", "循环冲洗时间", ""),
                    new CRegisterDef("D53", "循环冲洗次数（步骤六七八）", "次") { PreferInt16 = true },
                    new CRegisterDef("T8", "反应槽转暂存槽水泵延时", ""),
                    new CRegisterDef("T32", "反应结束排水延时时间", ""),
                    new CRegisterDef("T39", "暂存槽转反应槽水泵延时", ""),
                },
            };
        }

        /// <summary>
        /// 创建 4 号设备（四号 4.0 改性设备）的专用 PLC 配置
        /// 与 3 号设备点表基本一致，步骤六指示灯为 M104（3 号为 M101）
        /// </summary>
        private PlcConfig CreateDevice4Config(Device device)
        {
            return new PlcConfig
            {
                Name = device.Name,
                IpAddress = device.IpAddress,
                Port = device.Port,

                // X0-X7 连续读 8 点
                XStartAddress = "X0",
                XCount = 8,
                XPointLabels = new()
                {
                    { "X0", "反应槽下限液位" },
                    { "X1", "反应槽上限液位" },
                    { "X2", "反应槽极限液位" },
                    { "X3", "暂存槽下限液位" },
                    { "X4", "暂存槽上限液位" },
                    { "X5", "暂存槽极限液位" },
                    { "X6", "急停开关" },
                    { "X7", "反应槽中线液位" },
                },

                // Y0-Y7 + Y10-Y16（连续读 15 点，Y10/Y13 未用）
                YStartAddress = "Y0",
                YCount = 15,
                YPointLabels = new()
                {
                    { "Y0",  "暂存槽出水循环阀" },
                    { "Y1",  "反应槽出水循环" },
                    { "Y2",  "反应槽排水球阀" },
                    { "Y3",  "暂存槽进水球阀/循环阀" },
                    { "Y4",  "反应槽进水循环" },
                    { "Y5",  "反应槽进水球阀" },
                    { "Y6",  "暂存槽进水球阀" },
                    { "Y7",  "水泵运行中信号" },
                    { "Y10", "（未用）" },
                    { "Y11", "反应槽排水泵" },
                    { "Y12", "反应槽加热信号" },
                    { "Y13", "（未用）" },
                    { "Y14", "三色灯绿灯" },
                    { "Y15", "三色灯黄灯" },
                    { "Y16", "三色灯红灯" },
                },

                // MAddressList 只列出界面需要显示/记录的散点。
                // MReadBlocks 按通信效率合并成大块读取，再由 MitsubishiPlcService 按地址映射回散点数组。
                MAddressList = new List<string>
                {
                    // 步骤指示灯（8个）
                    "M64",  "M74",  "M104", "M111",
                    "M124", "M127", "M128", "M133",
                    // 手动控制（9个）
                    "M204", "M205", "M206", "M207", "M208",
                    "M209", "M210", "M211", "M212",
                    // 系统 + 步骤执行按钮（11个）
                    "M600", "M601",
                    "M603", "M604", "M605", "M606", "M607", "M608", "M609", "M610",
                    "M701",
                },
                MReadBlocks = new()
                {
                    new MReadBlock("M64",  70),   // 覆盖步骤指示灯 M64-M133
                    new MReadBlock("M204",  9),   // 手动控制 M204-M212
                    new MReadBlock("M600", 11),   // 系统/步骤执行 M600-M610
                    new MReadBlock("M701",  1),   // 允许启动指示灯
                },
                MPointLabels = new()
                {
                    { "M64",  "步骤三 储槽转反应槽" },
                    { "M74",  "步骤八 排水" },
                    { "M104", "步骤六 反应槽加水" },   // 四号设备步骤六指示灯，区别于三号的 M101
                    { "M111", "步骤一 储槽加水指示灯" },
                    { "M124", "步骤二 储槽循环" },
                    { "M127", "步骤五 反应槽转储槽" },
                    { "M128", "步骤七 循环冲洗" },
                    { "M133", "步骤四 循环加温/恒温浸泡" },
                    { "M204", "手动反应槽加水" },
                    { "M205", "手动储槽加水" },
                    { "M206", "手动反应槽排水" },
                    { "M207", "手动储槽出水循环" },
                    { "M208", "手动反应槽出水循环" },
                    { "M209", "手动储槽进水循环" },
                    { "M210", "手动反应槽进水循环" },
                    { "M211", "手动反应槽水泵开启" },
                    { "M212", "手动反应槽加热" },
                    { "M600", "系统停止" },
                    { "M601", "步骤一执行" },
                    { "M603", "步骤二执行" },
                    { "M604", "步骤三启动" },
                    { "M605", "步骤三总启动" },
                    { "M606", "步骤四启动" },
                    { "M607", "步骤五启动" },
                    { "M608", "步骤六七执行" },
                    { "M609", "步骤八执行" },
                    { "M610", "系统复位" },
                    { "M701", "允许启动指示灯" },
                },

                ProcessStages = new()
                {
                    new ProcessStageDef("M111", "步骤一 储槽加水",       "1"),
                    new ProcessStageDef("M124", "步骤二 储槽循环",       "2"),
                    new ProcessStageDef("M64",  "步骤三 储槽转反应槽",   "3"),
                    new ProcessStageDef("M133", "步骤四 循环加温",       "4"),
                    new ProcessStageDef("M127", "步骤五 反应槽转储槽",   "5"),
                    new ProcessStageDef("M104", "步骤六 反应槽加水",     "6"),
                    new ProcessStageDef("M128", "步骤七 循环冲洗",       "7"),
                    new ProcessStageDef("M74",  "步骤八 排水",           "8"),
                },

                TemperatureAddress = "D10",
                TemperatureIsWord = true,      // D10 为 16 位 Word 寄存器
                TemperatureDivisor = 10f,      // D10 存储 temp×10（如 845=84.5°C），显示需除以10
                TargetTemperatureAddress = "D280", // 反应槽第一道设定温度（用于报警判断）
                ActualTemperatureDefinition = new TemperatureRegisterDefinition
                {
                    Address = "D10",
                    DataType = PlcRegisterDataType.Int16,
                    Divisor = 10f
                },
                TargetTemperatureDefinition = new TemperatureRegisterDefinition
                {
                    Address = "D280",
                    DataType = PlcRegisterDataType.Int16,
                    Divisor = 10f
                },
                ThermocoupleAAddress = "",
                ThermocoupleBAddress = "",
                ThermocoupleCAddress = "",

                CRegisters = new()
                {
                    new CRegisterDef("D280",  "第一道设定温度",               "°C") { PreferInt16 = true },
                    new CRegisterDef("D260",  "第二道设定温度",               "°C") { PreferInt16 = true },
                    new CRegisterDef("D1000", "储槽循环时间",                 ""),
                    new CRegisterDef("D1050", "恒温浸泡时间",                 ""),
                    new CRegisterDef("D1030", "循环冲洗时间",                 ""),
                    new CRegisterDef("D53",   "循环冲洗次数（步骤六七八）",   "次") { PreferInt16 = true },
                    new CRegisterDef("T8",    "反应槽转储槽水泵延时",         ""),
                    new CRegisterDef("T32",   "反应结束排水延时",             ""),
                    new CRegisterDef("T39",   "储槽转反应槽水泵延时",         ""),
                },
            };
        }

        public async Task<bool> ConnectDeviceAsync(int deviceId)
        {
            if (!IsDemoVideoMode && !AppConfig.IsConfigurationValid)
                return false;

            var wrapper = _wrappers.FirstOrDefault(d => d.Device.Id == deviceId);
            if (wrapper == null) return false;

            // 从“停用”按钮重新启用时，恢复为现场默认的自动待机策略。
            if (wrapper.Device.MonitoringMode == DeviceMonitoringMode.Disabled)
            {
                if (!IsDemoVideoMode)
                    AppConfig.SaveDeviceMonitoringMode(deviceId - 1, DeviceMonitoringMode.AutoStandby);
                wrapper.Device.MonitoringMode = DeviceMonitoringMode.AutoStandby;
            }

            CancelScheduledReconnect(deviceId);
            if (!_connectingIds.TryAdd(deviceId, 0))
                return wrapper.PlcService.CurrentStatus.IsConnected;
            SetDeviceConnectionActivity(wrapper.Device, connecting: true, reconnecting: false, communicationFault: false);

            _autoReconnectIds.TryAdd(deviceId, 0);

            var success = false;
            try
            {
                success = await wrapper.PlcService.ConnectAsync();
                if (success)
                    success = TryStartAcquisitionIfStillAuthorized(wrapper, deviceId, "手动连接");
                return success;
            }
            finally
            {
                _connectingIds.TryRemove(deviceId, out _);
                UpdateDeviceOnlineState(wrapper);
                if (!success && !_stopped)
                    TryScheduleReconnect(wrapper);
            }
        }

        /// <summary>
        /// 停用指定设备。停用是持久化策略，不再后台探测；程序退出使用 DisconnectAllDevices，
        /// 不会改变用户配置。
        /// </summary>
        public void DisconnectDevice(int deviceId)
        {
            var wrapper = _wrappers.FirstOrDefault(d => d.Device.Id == deviceId);
            if (wrapper != null)
            {
                if (!IsDemoVideoMode)
                    AppConfig.SaveDeviceMonitoringMode(deviceId - 1, DeviceMonitoringMode.Disabled);
                wrapper.Device.MonitoringMode = DeviceMonitoringMode.Disabled;
                _autoReconnectIds.TryRemove(deviceId, out _);
                _connectingIds.TryRemove(deviceId, out _);
                CancelScheduledReconnect(deviceId);
                SetDeviceConnectionActivity(wrapper.Device, connecting: false, reconnecting: false, communicationFault: false);
                wrapper.PlcService.StopAcquisition();
                wrapper.PlcService.Disconnect();
                UpdateDeviceOnlineState(wrapper);
            }
        }

        /// <summary>
        /// 连接所有设备，返回 (成功数, 失败原因列表，用于界面提示)
        /// </summary>
        public async Task<(int successCount, List<string> failedReasons)> ConnectAllDevicesAsync()
        {
            var failedReasons = new List<string>();
            if (!IsDemoVideoMode && !AppConfig.IsConfigurationValid)
            {
                failedReasons.Add($"配置未通过校验：{AppConfig.ConfigurationError}");
                return (0, failedReasons);
            }
            // 保留锁参数，兼容 ConnectOneAsync；当前启动链路改为顺序错峰连接。
            var failedReasonsLock = new object();

            var wrapperList = _wrappers
                .Where(wrapper => DeviceMonitoringPolicy.IsConnectionAuthorized(
                    wrapper.Device.MonitoringMode))
                .ToList();
            var totalSw = System.Diagnostics.Stopwatch.StartNew();
            Views.MainWindow.DbgLog("DeviceManagerService:ConnectAll", "开始顺序连接全部 PLC", new
            {
                count = wrapperList.Count
            }, "CONNECT");

            // 工控机现场更怕启动瞬间卡死，不追求 4 台同时抢连。
            // 按顺序连接 + 每台采集定时器错峰，避免开机就并发打满 PLC/TCP/线程池。
            for (int i = 0; i < wrapperList.Count; i++)
            {
                await ConnectOneAsync(wrapperList[i], i, failedReasons, failedReasonsLock);

                if (i < wrapperList.Count - 1)
                    await Task.Delay(300);
            }

            int successCount = wrapperList.Count(w => w.PlcService.CurrentStatus.IsConnected);
            totalSw.Stop();
            Views.MainWindow.DbgLog("DeviceManagerService:ConnectAll", "全部 PLC 连接流程结束", new
            {
                elapsedMs = totalSw.ElapsedMilliseconds,
                successCount,
                total = wrapperList.Count,
                failedReasons = failedReasons.ToArray()
            }, "CONNECT");
            return (successCount, failedReasons);
        }

        private async Task ConnectOneAsync(
            DevicePlcWrapper wrapper, int orderIndex,
            List<string> failedReasons, object failedReasonsLock)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                CancelScheduledReconnect(wrapper.Device.Id);
                if (!_connectingIds.TryAdd(wrapper.Device.Id, 0))
                    return;
                SetDeviceConnectionActivity(wrapper.Device, connecting: true, reconnecting: false, communicationFault: false);
                Views.MainWindow.DbgLog("DeviceManagerService:ConnectOne", "开始连接 PLC", new
                {
                    device = wrapper.Device.Name,
                    wrapper.Device.IpAddress,
                    orderIndex
                }, "CONNECT");

                _autoReconnectIds.TryAdd(wrapper.Device.Id, 0);

                var success = await wrapper.PlcService.ConnectAsync();
                if (success)
                {
                    // 错峰：每台设备的采集启动延迟 orderIndex * 250ms。
                    // 4 台叠加 = 0/250/500/750ms，1s 周期内被打散，10s 周期同样均匀分布。
                    if (orderIndex > 0)
                        await Task.Delay(orderIndex * 250);

                    success = TryStartAcquisitionIfStillAuthorized(
                        wrapper,
                        wrapper.Device.Id,
                        "批量连接");
                }

                if (success)
                {
                    _connectingIds.TryRemove(wrapper.Device.Id, out _);
                    UpdateDeviceOnlineState(wrapper);
                    sw.Stop();
                    Views.MainWindow.DbgLog("DeviceManagerService:ConnectOne", "PLC 连接成功并启动采集", new
                    {
                        device = wrapper.Device.Name,
                        wrapper.Device.IpAddress,
                        elapsedMs = sw.ElapsedMilliseconds
                    }, "CONNECT");
                }
                else
                {
                    _connectingIds.TryRemove(wrapper.Device.Id, out _);
                    var err = (wrapper.PlcService as MitsubishiPlcService)?.LastConnectionError;
                    if (DeviceMonitoringPolicy.IsExpectedOnline(wrapper.Device.MonitoringMode))
                    {
                        lock (failedReasonsLock)
                            failedReasons.Add($"{wrapper.Device.Name}: {err ?? "连接失败"}");
                    }
                    UpdateDeviceOnlineState(wrapper);
                    if (!_stopped)
                        TryScheduleReconnect(wrapper);
                    sw.Stop();
                    Views.MainWindow.DbgLog("DeviceManagerService:ConnectOne", "PLC 连接失败", new
                    {
                        device = wrapper.Device.Name,
                        wrapper.Device.IpAddress,
                        elapsedMs = sw.ElapsedMilliseconds,
                        error = err
                    }, "CONNECT");
                }
            }
            catch (Exception ex)
            {
                _connectingIds.TryRemove(wrapper.Device.Id, out _);
                sw.Stop();
                System.Diagnostics.Debug.WriteLine($"连接设备 {wrapper.Device.Name} 失败: {ex.Message}");
                Views.MainWindow.DbgLog("DeviceManagerService:ConnectOne", "PLC 连接异常", new
                {
                    device = wrapper.Device.Name,
                    wrapper.Device.IpAddress,
                    elapsedMs = sw.ElapsedMilliseconds,
                    error = ex.Message
                }, "CONNECT");
                if (DeviceMonitoringPolicy.IsExpectedOnline(wrapper.Device.MonitoringMode))
                {
                    lock (failedReasonsLock)
                        failedReasons.Add($"{wrapper.Device.Name}: {ex.Message}");
                }
                UpdateDeviceOnlineState(wrapper);
                if (!_stopped)
                    TryScheduleReconnect(wrapper);
            }
        }

        /// <summary>
        /// 设置页保存后应用四台设备策略。自动待机/要求在线设备进入发现范围；
        /// 停用设备立即撤销正在等待的重连并断开传输。
        /// </summary>
        public void ApplyMonitoringModes(DeviceMonitoringMode[] modes)
        {
            if (modes == null || modes.Length != _wrappers.Count)
                throw new ArgumentException("设备运行模式必须与设备数量一致", nameof(modes));
            if (modes.Any(mode => !Enum.IsDefined(mode)))
                throw new ArgumentException("设备运行模式包含无效值", nameof(modes));

            foreach (var wrapper in _wrappers)
            {
                var device = wrapper.Device;
                var mode = modes[device.Id - 1];
                device.MonitoringMode = mode;
                CancelScheduledReconnect(device.Id);

                if (!DeviceMonitoringPolicy.IsConnectionAuthorized(mode))
                {
                    _autoReconnectIds.TryRemove(device.Id, out _);
                    SetDeviceConnectionActivity(
                        device,
                        connecting: false,
                        reconnecting: false,
                        communicationFault: false);
                    wrapper.PlcService.StopAcquisition();
                    wrapper.PlcService.Disconnect();
                    UpdateDeviceOnlineState(wrapper);
                    continue;
                }

                _autoReconnectIds.TryAdd(device.Id, 0);
                if (!wrapper.PlcService.CurrentStatus.IsConnected)
                    TryScheduleReconnect(wrapper);
                else
                    UpdateDeviceOnlineState(wrapper);
            }

            var requiredOffline = _wrappers
                .Select(wrapper => wrapper.Device)
                .Where(device =>
                    DeviceMonitoringPolicy.IsExpectedOnline(device.MonitoringMode) &&
                    !device.IsOnline)
                .ToList();
            UpdateOfflineDevicesOnUiThread(requiredOffline);
            _lastTowerLightState = "";
            _ = UpdateTowerLightAsync();
        }

        /// <summary>
        /// 断开所有设备
        /// </summary>
        public void DisconnectAllDevices()
        {
            // 程序退出/批量断开时，清空自动重连白名单，避免后台 Task 继续重连
            _autoReconnectIds.Clear();
            foreach (var state in _reconnectStates.Values)
            {
                lock (state)
                    state.ScheduleVersion++;
            }
            _reconnectingIds.Clear();
            _connectingIds.Clear();

            foreach (var wrapper in _wrappers)
            {
                try
                {
                    wrapper.PlcService.StopAcquisition();
                    wrapper.PlcService.Disconnect();
                    UpdateDeviceOnlineState(wrapper);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"断开设备 {wrapper.Device.Name} 失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 在 UI 线程上更新设备在线状态，连接/断开后立即刷新界面
        /// </summary>
        private void UpdateDeviceOnlineState(DevicePlcWrapper wrapper)
        {
            void Update()
            {
                var device = wrapper.Device;
                var wasOnline = device.IsOnline;
                // Dispatcher 真正执行时再读一次服务状态，不信任排队前的过期 true。
                var actualOnline = wrapper.PlcService.CurrentStatus.IsConnected;
                device.IsOnline = actualOnline;
                device.IsReconnecting = !actualOnline && _reconnectingIds.ContainsKey(device.Id);
                device.IsConnecting = !actualOnline && _connectingIds.ContainsKey(device.Id);
                device.HasCommunicationFault = !actualOnline &&
                    DeviceMonitoringPolicy.IsExpectedOnline(device.MonitoringMode) &&
                    _autoReconnectIds.ContainsKey(device.Id) &&
                    !device.IsReconnecting &&
                    !device.IsConnecting;

                var sampleTime = wrapper.PlcService.CurrentStatus.LastTemperatureSampleTime;
                if (sampleTime != default)
                {
                    device.CurrentTemperature = wrapper.PlcService.CurrentStatus.Temperature;
                    device.HasTemperatureSample = true;
                    device.LastUpdateTime = sampleTime;
                    device.LastTemperatureSampleTime = sampleTime;
                    device.LastTemperatureSampleSequence = wrapper.PlcService.CurrentStatus.LastTemperatureSampleSequence;
                    device.LastTemperatureConnectionGeneration = wrapper.PlcService.CurrentStatus.LastTemperatureConnectionGeneration;
                    device.LastTemperatureRawValue = wrapper.PlcService.CurrentStatus.LastTemperatureRawValue;
                    device.TemperatureQuality = wrapper.PlcService.CurrentStatus.TemperatureQuality;
                    device.IsTemperatureStale = !actualOnline ||
                        (wrapper.PlcService is MitsubishiPlcService mitsubishi &&
                         mitsubishi.IsTemperatureSampleDelayed(out _));
                }
                else
                {
                    // 新一代连接尚无样本时，历史值不能被包装成实时值；离线时也保留
                    // 最后有效值并明确标记过期。
                    device.IsTemperatureStale = actualOnline || device.HasTemperatureSample;
                }
                device.RefreshTemperatureFreshness();

                if (wasOnline != actualOnline)
                {
                    SafeEventDispatcher.Invoke(this, DeviceStatusChanged, new DeviceStatusChangeEventArgs
                    {
                        Device = device,
                        WasOnline = wasOnline,
                        IsOnline = actualOnline,
                        ChangeTime = DateTime.Now
                    });
                }

                // 串口发送内部含短暂等待，放到后台且必须在 UI 状态落地后计算。
                _ = UpdateTowerLightAsync();
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
                Update();
            else
                dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Update));
        }

        private static void SetDeviceConnectionActivity(
            Device device,
            bool connecting,
            bool reconnecting,
            bool communicationFault)
        {
            if (device == null)
                return;

            void Apply()
            {
                device.IsConnecting = connecting;
                device.IsReconnecting = reconnecting;
                device.HasCommunicationFault = communicationFault;
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
                Apply();
            else
                dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Apply));
        }

        private void OnStorageHealthChanged(object sender, StorageHealthSnapshot snapshot)
        {
            void Apply()
            {
                IsDatabaseHealthy = snapshot.IsReady && snapshot.IsHealthy && snapshot.DroppedCount == 0;
                DatabaseHealthText = snapshot.Message;
                PendingDatabaseLogCount = snapshot.PendingCount;
                SpooledDatabaseLogCount = snapshot.SpoolCount;
                DroppedDatabaseLogCount = snapshot.DroppedCount;
                DeadLetterDatabaseLogCount = snapshot.DeadLetterCount;
                LastDatabaseWriteTime = snapshot.LastSuccessfulWriteTime;
                _lastTowerLightState = "";
                _ = UpdateTowerLightAsync();
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
                Apply();
            else
                dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Apply));
        }

        private void OnAutoExportHealthChanged(object sender, StorageHealthSnapshot snapshot)
        {
            void Apply()
            {
                IsAutoExportEnabled = snapshot.IsReady;
                IsAutoExportHealthy = !snapshot.IsReady ||
                                      (snapshot.IsHealthy && snapshot.DroppedCount == 0);
                AutoExportHealthText = snapshot.Message;
                PendingAutoExportLogCount = snapshot.PendingCount;
                DroppedAutoExportLogCount = snapshot.DroppedCount;
                LastAutoExportWriteTime = snapshot.LastSuccessfulWriteTime;
                _lastTowerLightState = "";
                _ = UpdateTowerLightAsync();
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
                Apply();
            else
                dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Apply));
        }

        /// <summary>
        /// 刷新所有设备状态
        /// </summary>
        public async Task RefreshAllDevicesAsync()
        {
            var tasks = _wrappers.Select(wrapper => wrapper.UpdateStatusAsync());
            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// PLC IO点变化时写入操作日志到数据库，并更新设备今日操作计数
        /// </summary>
        private void OnPlcStateChanged(int deviceId, StateChangeEvent evt)
        {
            // #region agent log - Hypothesis A: 记录状态变化频率
            System.Threading.Interlocked.Increment(ref DbgStateChangeCount);
            // #endregion
            var log = OperationLog.FromChangeEvent(evt);
            log.DeviceId = deviceId;

            // 用字典直接查找，避免在线程池线程中遍历 ObservableCollection 导致竞态
            if (_deviceMap.TryGetValue(deviceId, out var device))
            {
                log.DeviceName = device.Name ?? string.Empty;
                _pendingOperationCountDeltas.AddOrUpdate(deviceId, 1, (_, current) => current + 1);
            }

            _logBuffer.EnqueueOperationLog(log);
            // 入队自动导出 HTML（后台 3 秒批量落盘，不在事件线程做磁盘 IO）
            _autoExport.AppendOperationLog(log);
        }

        /// <summary>
        /// 温度采样完成时入库（每个设备 TemperatureInterval 周期触发一次，通过 LogBufferService 攒批写入）
        /// </summary>
        private void OnTemperatureSampled(int deviceId, TemperatureSampleEventArgs e)
        {
            try
            {
                // DeviceName 优先用事件里的（PLC 配置侧），兜底用 Device 列表里的
                string deviceName = !string.IsNullOrEmpty(e.DeviceName)
                    ? e.DeviceName
                    : (_deviceMap.TryGetValue(deviceId, out var dev) ? dev.Name ?? "" : "");

                var log = new TemperatureLog
                {
                    DeviceId = deviceId,
                    DeviceName = deviceName,
                    Temperature = e.Temperature,
                    ThermocoupleA = e.ThermocoupleA,
                    ThermocoupleB = e.ThermocoupleB,
                    ThermocoupleC = e.ThermocoupleC,
                    RecordTime = e.SampleTime,
                    IsAbnormal = e.IsAbnormal,
                    Threshold = GetAlarmThreshold(deviceId),
                    AlarmThreshold = GetAlarmThreshold(deviceId),
                    TargetTemperature = e.TargetTemperature,
                    AuxiliarySampleTime = e.AuxiliarySampleTime,
                    HasFreshAuxiliaryData = e.HasFreshAuxiliaryData
                };
                _logBuffer.EnqueueTemperatureLog(log);
                // 入队自动导出 HTML（后台 3 秒批量落盘，不在事件线程做磁盘 IO）
                _autoExport.AppendTemperatureLog(log);
                MarkReconnectHealthy(deviceId, e);
                UpdateDeviceTemperatureFromSample(deviceId, e);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[温度入库] 设备{deviceId} 入队失败: {ex.Message}");
            }
        }

        private float GetAlarmThreshold(int deviceId)
        {
            var threshold = GetPlcService(deviceId)?.Config?.TemperatureThreshold ?? 90f;
            return float.IsFinite(threshold) && threshold >= 0f ? threshold : 90f;
        }

        /// <summary>
        /// 温度采样已经成功返回时，直接同步到主界面设备卡片。
        /// 不再只依赖 5s 监控轮询从 CurrentStatus 抄数，避免“后台已入库但主界面仍显示 --.-°C”。
        /// </summary>
        private void UpdateDeviceTemperatureFromSample(int deviceId, TemperatureSampleEventArgs e)
        {
            if (!_deviceMap.TryGetValue(deviceId, out var device))
                return;

            var dispatcher = App.Current?.Dispatcher;
            if (dispatcher == null)
                return;

            void Update()
            {
                if (_stopped) return;

                var wrapper = _wrappers.FirstOrDefault(w => w.Device.Id == deviceId);
                var currentStatus = wrapper?.PlcService.CurrentStatus;
                if (currentStatus == null ||
                    currentStatus.LastTemperatureConnectionGeneration != e.ConnectionGeneration ||
                    currentStatus.LastTemperatureSampleSequence != e.SampleSequence)
                {
                    // Dispatcher 排队期间可能已经换代并收到更新样本，旧事件不得倒灌。
                    return;
                }

                device.CurrentTemperature = e.Temperature;
                device.HasTemperatureSample = true;
                device.IsTemperatureStale = !currentStatus.IsConnected;
                device.IsReconnecting = !currentStatus.IsConnected &&
                    _reconnectingIds.ContainsKey(deviceId);
                // IsAbnormal 由 MitsubishiPlcService 按设定温度判断，直接使用，无需硬编码 90°C
                device.HasAlert = e.IsAbnormal;
                device.LastUpdateTime = e.SampleTime;
                device.LastTemperatureSampleTime = e.SampleTime;
                device.LastTemperatureSampleSequence = e.SampleSequence;
                device.LastTemperatureConnectionGeneration = e.ConnectionGeneration;
                device.LastTemperatureRawValue = e.RawValue;
                device.TemperatureQuality = e.Quality;
            }

            try
            {
                if (dispatcher.CheckAccess())
                    Update();
                else
                    _ = dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Update));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[温度显示] 设备{deviceId} 主界面同步失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取指定设备的PLC服务
        /// </summary>
        public IPlcService GetPlcService(int deviceId)
        {
            var wrapper = _wrappers.FirstOrDefault(d => d.Device.Id == deviceId);
            return wrapper?.PlcService;
        }

        /// <summary>
        /// 获取指定设备
        /// </summary>
        public Device GetDevice(int deviceId)
        {
            return _devices.FirstOrDefault(d => d.Id == deviceId);
        }

        /// <summary>
        /// 运行时更新自动导出目录（设置保存后立即生效，无需重启）
        /// </summary>
        public void UpdateAutoExportPath(string path)
        {
            _autoExport.UpdateExportPath(path);
        }

        private volatile bool _stopped;

        /// <summary>
        /// 停止监控（程序退出时调用）。多次调用安全。
        /// </summary>
        public void StopMonitoring()
        {
            if (_stopped) return;
            _stopped = true;
            _lifecycleCts.Cancel();

            try { _monitorTimer?.Stop(); _monitorTimer?.Dispose(); } catch { }
            try { _cleanupTimer?.Stop(); _cleanupTimer?.Dispose(); } catch { }
            try { _operationCountFlushTimer?.Stop(); _operationCountFlushTimer?.Dispose(); } catch { }
            try { FlushOperationCountDeltasOnShutdown(); } catch { }

            DisconnectAllDevices();

            try { Interlocked.Exchange(ref _towerLight, null)?.Dispose(); } catch { }
            try { _logBuffer.HealthChanged -= OnStorageHealthChanged; } catch { }
            try { _autoExport.HealthChanged -= OnAutoExportHealthChanged; } catch { }
            try { _logBuffer?.Dispose(); } catch { }
            try { _autoExport?.Dispose(); } catch { }
            try { (_dataService as IDisposable)?.Dispose(); } catch { }
            _ = Task.WhenAll(
                    _databaseInitializationTask,
                    _towerInitializationTask,
                    _startupCleanupTask)
                .ContinueWith(task =>
                {
                    if (task.IsFaulted)
                        System.Diagnostics.Debug.WriteLine($"[DeviceManager] 后台初始化收尾异常: {task.Exception}");
                    _lifecycleCts.Dispose();
                }, TaskScheduler.Default);
            System.Diagnostics.Debug.WriteLine("[DeviceManager] 已停止，AutoExport 文件已关闭");
        }

        private void FlushOperationCountDeltasOnShutdown()
        {
            var deltas = DrainOperationCountDeltas();
            foreach (var kvp in deltas)
            {
                if (_deviceMap.TryGetValue(kvp.Key, out var device))
                    device.TodayOperationCount += kvp.Value;
            }
        }
    }

    /// <summary>
    /// 设备状态变化事件参数
    /// </summary>
    public class DeviceStatusChangeEventArgs : EventArgs
    {
        public Device Device { get; set; } = null!;
        public bool WasOnline { get; set; }
        public bool IsOnline { get; set; }
        public DateTime ChangeTime { get; set; }
    }
}
