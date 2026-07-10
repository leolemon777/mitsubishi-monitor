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

            if (PlcService.CurrentStatus.IsConnected)
            {
                Device.IsOnline = true;
                Device.CurrentTemperature = PlcService.CurrentStatus.Temperature;
                // 使用 PlcStatus.IsAlarm（由 MitsubishiPlcService 按设定温度计算，非硬编码）
                Device.HasAlert = PlcService.CurrentStatus.IsAlarm;
                Device.LastUpdateTime = DateTime.Now;
            }
            else
            {
                Device.IsOnline = false;
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

        /// <summary>
        /// 已用户主动连接过的设备 Id 集合（仅这些设备会触发后台自动重连，避免应用启动后无脑连接）
        /// </summary>
        private readonly ConcurrentDictionary<int, byte> _autoReconnectIds = new();

        /// <summary>
        /// 正在执行重连任务的设备 Id（防止同一设备并发重连）
        /// </summary>
        private readonly ConcurrentDictionary<int, byte> _reconnectingIds = new();

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

        private bool _isBuzzerMuted; // 方式B：消音标志

        /// <summary>
        /// 三色灯复位：方式B
        /// 仅关闭蜂鸣器，如果当前超温，红灯继续保持常亮。
        /// 直到所有设备温度降回正常，才会自动清除消音状态，下次再超温时重新响铃。
        /// </summary>
        public void AcknowledgeAlarm()
        {
            _isBuzzerMuted = true;
            _lastTowerLightState = ""; // 强制下次更新重新下发指令

            // 立即触发一次状态更新
            _ = UpdateTowerLightAsync();
        }

        public DeviceManagerService()
        {
            _wrappers = new ObservableCollection<DevicePlcWrapper>();
            _devices = new ObservableCollection<Device>();
            Devices = new ReadOnlyObservableCollection<Device>(_devices);

            _dataService = new DataService();
            _logBuffer = new LogBufferService();
            _autoExport = new AutoExportService();

            // DB 初始化完成后才允许 LogBuffer 写入，避免表不存在导致数据丢失
            Task.Run(async () =>
            {
                try
                {
                    await _dataService.InitializeAsync();
                    _logBuffer.IsDbReady = true;
                    System.Diagnostics.Debug.WriteLine("[DeviceManager] DB 初始化完成，LogBuffer 写入已启用");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DeviceManager] DB 初始化失败: {ex.Message}");
                }
            });

            // 三色灯初始化移到后台线程，WMI 串口扫描可能耗时数秒甚至数十秒，不能阻塞 UI
            Task.Run(() =>
            {
                _towerLight = InitializeTowerLight();
            });

            InitializeDevices();

            // 启动监控定时器（每5秒检查一次）
            _monitorTimer = new System.Timers.Timer(5000);
            _monitorTimer.Elapsed += OnMonitorTimerElapsed;
            _monitorTimer.AutoReset = true;
            _monitorTimer.Start();

            // 数据库历史数据清理（每小时一次，删除 30 天前的温度/操作日志）
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
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMinutes(1));
                await CleanupOldDataSafelyAsync();
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

        private async Task CleanupOldDataSafelyAsync()
        {
            try
            {
                await _dataService.CleanOldDataAsync();
                System.Diagnostics.Debug.WriteLine($"[数据清理] 已删除 30 天前的历史数据");
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

            var snapshots = new List<(DevicePlcWrapper Wrapper, bool IsConnected, float Temperature)>();
            foreach (var wrapper in _wrappers.ToList())
            {
                var status = wrapper.PlcService.CurrentStatus;
                var isCurrentlyConnected = status.IsConnected;
                if (isCurrentlyConnected
                    && wrapper.PlcService is MitsubishiPlcService mitsubishi
                    && mitsubishi.IsTemperatureSampleStale(DateTime.Now, out var tempAge))
                {
                    mitsubishi.MarkTemperatureSampleStale(tempAge);
                    isCurrentlyConnected = false;
                    Views.MainWindow.DbgLog("DeviceManagerService:TemperatureStale", "温度采样长时间未更新，触发自动重连", new
                    {
                        device = wrapper.Device.Name,
                        wrapper.Device.IpAddress,
                        ageSeconds = Math.Round(tempAge.TotalSeconds, 1),
                        intervalMs = mitsubishi.Config.TemperatureInterval,
                        lastTemperatureSampleTime = status.LastTemperatureSampleTime
                    }, "TEMP");
                }

                var temp = wrapper.PlcService.CurrentStatus.Temperature;
                snapshots.Add((wrapper, isCurrentlyConnected, temp));

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

                                if (snapshot.IsConnected)
                                {
                                    // 只在温度真变化时才赋值（CommunityToolkit 的 SetProperty 内部已比较，
                                    // 但显式比较可以省去 SetProperty 调用本身 + 让代码意图更清晰）。
                                    // 注意必须在赋值前算好，赋值后再比较永远为 false。
                                    var tempChanged = Math.Abs(device.CurrentTemperature - snapshot.Temperature) > 0.05f;
                                    if (tempChanged)
                                        device.CurrentTemperature = snapshot.Temperature;
                                    // 使用 PlcStatus.IsAlarm（已按设定温度判断，非硬编码 90°C）
                                    var hasAlert = snapshot.Wrapper.PlcService.CurrentStatus.IsAlarm;
                                    if (device.HasAlert != hasAlert)
                                        device.HasAlert = hasAlert;
                                    if (!device.IsOnline)
                                        device.IsOnline = true;

                                    // LastUpdateTime 只在数据有变化（温度/在线翻转）或上次刷新已超 30s 时更新，
                                    // 避免 5s 一次无脑写 DateTime.Now 触发不必要的 PropertyChanged + 模板字符串重算。
                                    var now = DateTime.Now;
                                    if (!wasOnline ||
                                        tempChanged ||
                                        (now - device.LastUpdateTime).TotalSeconds > 30)
                                    {
                                        device.LastUpdateTime = now;
                                    }

                                    if (!wasOnline)
                                    {
                                        DeviceStatusChanged?.Invoke(this, new DeviceStatusChangeEventArgs
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
                                    offlineList.Add(device);
                                    if (device.IsOnline)
                                        device.IsOnline = false;
                                    if (device.HasTemperatureSample)
                                        device.HasTemperatureSample = false;

                                    if (wasOnline)
                                    {
                                        DeviceStatusChanged?.Invoke(this, new DeviceStatusChangeEventArgs
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
        /// 后台异步重连一台离线设备：
        /// - 仅对已通过 ConnectDeviceAsync/ConnectAllDevicesAsync 成功连过的设备生效；
        /// - 同一设备同一时刻只允许一个重连任务（_reconnectingIds 互斥）；
        /// - 成功后自动重启采集；失败则等下一次监控周期再尝试。
        /// </summary>
        private void TryScheduleReconnect(DevicePlcWrapper wrapper)
        {
            int id = wrapper.Device.Id;
            if (!_autoReconnectIds.ContainsKey(id))
                return; // 用户未连接过或主动断开过

            if (!_reconnectingIds.TryAdd(id, 0))
                return; // 已经有重连任务在跑

            var w = wrapper;
            _ = Task.Run(async () =>
            {
                try
                {
                    if (_stopped || !_autoReconnectIds.ContainsKey(id))
                        return;

                    System.Diagnostics.Debug.WriteLine($"[自动重连] 尝试重连 {w.Device.Name} ({w.Device.IpAddress})");
                    var ok = await w.PlcService.ConnectAsync();
                    if (ok)
                    {
                        // 用户可能在 ConnectAsync 等待期间点击了主动断开。
                        // 此时不能让已在途的自动重连把设备重新连上。
                        if (_stopped || !_autoReconnectIds.ContainsKey(id))
                        {
                            w.PlcService.Disconnect();
                            return;
                        }

                        try { w.PlcService.StartAcquisition(); }
                        catch (Exception sx)
                        {
                            System.Diagnostics.Debug.WriteLine($"[自动重连] {w.Device.Name} StartAcquisition 失败: {sx.Message}");
                        }
                        UpdateDeviceOnlineState(w, true);
                        System.Diagnostics.Debug.WriteLine($"[自动重连] ✓ {w.Device.Name} 已恢复");
                    }
                    else
                    {
                        var err = (w.PlcService as MitsubishiPlcService)?.LastConnectionError ?? "";
                        System.Diagnostics.Debug.WriteLine($"[自动重连] ✗ {w.Device.Name} 失败: {err}");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[自动重连] {w.Device.Name} 异常: {ex.Message}");
                }
                finally
                {
                    _reconnectingIds.TryRemove(id, out _);
                }
            });
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
        /// 任意设备 temp > 报警阈值 → 红灯 + 蜂鸣器
        /// 所有在线设备无超温       → 绿灯，蜂鸣器关
        /// 未连接任何设备           → 灭灯
        /// </summary>
        public async Task UpdateTowerLightAsync()
        {
            if (_towerLight == null) return;
            if (Interlocked.Exchange(ref _isUpdatingTowerLight, 1) == 1)
                return;

            try
            {
                bool anyOnline = false;
                bool anyAlarm  = false;  // 温度超过报警阈值 → 红灯

                foreach (var wrapper in _wrappers.ToList())
                {
                    if (!wrapper.Device.IsOnline)
                        continue;

                    anyOnline = true;
                    float temp = wrapper.PlcService.CurrentStatus.Temperature;
                    float threshold = wrapper.PlcService.Config.TemperatureThreshold;
                    if (threshold <= 0) threshold = 90f;

                    if (temp > threshold) anyAlarm = true;
                }

                // 更新 HasActiveAlarm（供 UI 复位按钮显示）
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
                }

                // 优先级：红灯（超温+可能蜂鸣）> 绿灯（正常）> 灭灯（未连接）
                string desiredState;
                if (anyAlarm)
                    desiredState = _isBuzzerMuted ? "Red+BuzzerOff" : "Red+BuzzerOn";
                else if (anyOnline)
                    desiredState = "Green+BuzzerOff";
                else
                    desiredState = "Off";

                // 状态没变不重复写串口
                if (string.Equals(_lastTowerLightState, desiredState, StringComparison.Ordinal))
                    return;

                bool ok;
                switch (desiredState)
                {
                    case "Red+BuzzerOn":
                        ok = (await _towerLight.SendAsync("Red")) & (await _towerLight.SendAsync("BuzzerOn"));
                        break;
                    case "Red+BuzzerOff":
                        ok = (await _towerLight.SendAsync("Red")) & (await _towerLight.SendAsync("BuzzerOff"));
                        break;
                    case "Green+BuzzerOff":
                        ok = (await _towerLight.SendAsync("Green")) & (await _towerLight.SendAsync("BuzzerOff"));
                        break;
                    default:
                        ok = await _towerLight.SendAsync("Off");
                        break;
                }

                if (ok)
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
        /// 配置6台设备，IP地址按 192.168.1.10/15/20/25/30/35 排列
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
                        Port = device.Port
                    };

                // 从持久化配置中恢复报警阈值（deviceIndex = Id-1）
                int deviceIndex = config.Id - 1;
                if (deviceIndex >= 0 && deviceIndex < AppConfig.DeviceThresholds.Length)
                    plcConfig.TemperatureThreshold = AppConfig.DeviceThresholds[deviceIndex];

                var plcService = new MitsubishiPlcService(plcConfig);

                // 订阅该设备的IO点变化事件，写入数据库日志
                int capturedId = device.Id;
                plcService.StateChanged += (s, e) => OnPlcStateChanged(capturedId, e);

                // 订阅温度采样事件，写入温度日志
                plcService.TemperatureSampled += (s, e) => OnTemperatureSampled(capturedId, e);

                _devices.Add(device);
                _wrappers.Add(new DevicePlcWrapper(device, plcService));
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
            var wrapper = _wrappers.FirstOrDefault(d => d.Device.Id == deviceId);
            if (wrapper == null) return false;

            // 只要触发了连接，都记入自动重连白名单，保证即使当前PLC未开机，后续开机时也会自动连上。
            _autoReconnectIds.TryAdd(deviceId, 0);

            var success = await wrapper.PlcService.ConnectAsync();
            if (success)
            {
                wrapper.PlcService.StartAcquisition();
                UpdateDeviceOnlineState(wrapper, true);
            }
            return success;
        }

        /// <summary>
        /// 断开指定设备
        /// </summary>
        public void DisconnectDevice(int deviceId)
        {
            var wrapper = _wrappers.FirstOrDefault(d => d.Device.Id == deviceId);
            if (wrapper != null)
            {
                // 用户主动断开，移出自动重连白名单
                _autoReconnectIds.TryRemove(deviceId, out _);
                wrapper.PlcService.StopAcquisition();
                wrapper.PlcService.Disconnect();
                UpdateDeviceOnlineState(wrapper, false);
            }
        }

        /// <summary>
        /// 连接所有设备，返回 (成功数, 失败原因列表，用于界面提示)
        /// </summary>
        public async Task<(int successCount, List<string> failedReasons)> ConnectAllDevicesAsync()
        {
            var failedReasons = new List<string>();
            // 保留锁参数，兼容 ConnectOneAsync；当前启动链路改为顺序错峰连接。
            var failedReasonsLock = new object();

            var wrapperList = _wrappers.ToList();
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
                Views.MainWindow.DbgLog("DeviceManagerService:ConnectOne", "开始连接 PLC", new
                {
                    device = wrapper.Device.Name,
                    wrapper.Device.IpAddress,
                    orderIndex
                }, "CONNECT");

                // 只要触发了连接（无论是启动自动连接还是手动连接全部），都记入自动重连白名单，保证即使当前PLC未开机，后续开机时也会自动连上。
                _autoReconnectIds.TryAdd(wrapper.Device.Id, 0);

                var success = await wrapper.PlcService.ConnectAsync();
                if (success)
                {
                    // 错峰：每台设备的采集启动延迟 orderIndex * 250ms。
                    // 4 台叠加 = 0/250/500/750ms，1s 周期内被打散，10s 周期同样均匀分布。
                    if (orderIndex > 0)
                        await Task.Delay(orderIndex * 250);

                    wrapper.PlcService.StartAcquisition();
                    UpdateDeviceOnlineState(wrapper, true);
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
                    var err = (wrapper.PlcService as MitsubishiPlcService)?.LastConnectionError;
                    if (!string.IsNullOrEmpty(err))
                    {
                        lock (failedReasonsLock)
                            failedReasons.Add($"{wrapper.Device.Name}: {err}");
                    }
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
                sw.Stop();
                System.Diagnostics.Debug.WriteLine($"连接设备 {wrapper.Device.Name} 失败: {ex.Message}");
                Views.MainWindow.DbgLog("DeviceManagerService:ConnectOne", "PLC 连接异常", new
                {
                    device = wrapper.Device.Name,
                    wrapper.Device.IpAddress,
                    elapsedMs = sw.ElapsedMilliseconds,
                    error = ex.Message
                }, "CONNECT");
                lock (failedReasonsLock)
                    failedReasons.Add($"{wrapper.Device.Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 断开所有设备
        /// </summary>
        public void DisconnectAllDevices()
        {
            // 程序退出/批量断开时，清空自动重连白名单，避免后台 Task 继续重连
            _autoReconnectIds.Clear();

            foreach (var wrapper in _wrappers)
            {
                try
                {
                    wrapper.PlcService.StopAcquisition();
                    wrapper.PlcService.Disconnect();
                    UpdateDeviceOnlineState(wrapper, false);
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
        private void UpdateDeviceOnlineState(DevicePlcWrapper wrapper, bool isOnline)
        {
            void Update()
            {
                var device = wrapper.Device;
                var wasOnline = device.IsOnline;
                device.IsOnline = isOnline;
                if (!isOnline)
                    device.HasTemperatureSample = false;
                device.LastUpdateTime = DateTime.Now;
                if (wasOnline != isOnline)
                {
                    DeviceStatusChanged?.Invoke(this, new DeviceStatusChangeEventArgs
                    {
                        Device = device,
                        WasOnline = wasOnline,
                        IsOnline = isOnline,
                        ChangeTime = DateTime.Now
                    });
                }
            }

            if (Application.Current?.Dispatcher.CheckAccess() == true)
                Update();
            else
                Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Update));

            // 串口发送含 Thread.Sleep(100)，不阻塞 UI 线程
            _ = UpdateTowerLightAsync();
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
                    Threshold = e.TargetTemperature
                };
                _logBuffer.EnqueueTemperatureLog(log);
                // 入队自动导出 HTML（后台 3 秒批量落盘，不在事件线程做磁盘 IO）
                _autoExport.AppendTemperatureLog(log);
                UpdateDeviceTemperatureFromSample(deviceId, e);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[温度入库] 设备{deviceId} 入队失败: {ex.Message}");
            }
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

                device.CurrentTemperature = e.Temperature;
                device.HasTemperatureSample = true;
                // IsAbnormal 由 MitsubishiPlcService 按设定温度判断，直接使用，无需硬编码 90°C
                device.HasAlert = e.IsAbnormal;
                device.LastUpdateTime = e.SampleTime;
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

        private bool _stopped;

        /// <summary>
        /// 停止监控（程序退出时调用）。多次调用安全。
        /// </summary>
        public void StopMonitoring()
        {
            if (_stopped) return;
            _stopped = true;

            try { _monitorTimer?.Stop(); _monitorTimer?.Dispose(); } catch { }
            try { _cleanupTimer?.Stop(); _cleanupTimer?.Dispose(); } catch { }
            try { _operationCountFlushTimer?.Stop(); _operationCountFlushTimer?.Dispose(); } catch { }
            try { FlushOperationCountDeltasOnShutdown(); } catch { }

            DisconnectAllDevices();

            try { _towerLight?.Dispose(); } catch { }
            try { _logBuffer?.Dispose(); } catch { }
            try { _autoExport?.Dispose(); } catch { }
            try { (_dataService as IDisposable)?.Dispose(); } catch { }
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
