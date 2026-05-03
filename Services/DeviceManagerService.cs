using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
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
                Device.HasAlert = Device.CurrentTemperature > 90f; // 阈值
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
        private int _isMonitoring;
        private int _isCleaning;
        private readonly IDataService _dataService;
        private readonly LogBufferService _logBuffer;
        private TowerLightService _towerLight;

        /// <summary>
        /// 已用户主动连接过的设备 Id 集合（仅这些设备会触发后台自动重连，避免应用启动后无脑连接）
        /// </summary>
        private readonly ConcurrentDictionary<int, byte> _autoReconnectIds = new();

        /// <summary>
        /// 正在执行重连任务的设备 Id（防止同一设备并发重连）
        /// </summary>
        private readonly ConcurrentDictionary<int, byte> _reconnectingIds = new();

        // #region agent log - Hypothesis A: 统计10秒内状态变化次数
        public static int DbgStateChangeCount = 0;
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

        public DeviceManagerService()
        {
            _wrappers = new ObservableCollection<DevicePlcWrapper>();
            _devices = new ObservableCollection<Device>();
            Devices = new ReadOnlyObservableCollection<Device>(_devices);

            _dataService = new DataService();
            _logBuffer = new LogBufferService();
            Task.Run(async () => await _dataService.InitializeAsync());

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

            // 数据库历史数据清理（每小时一次，删除 15 天前的温度/操作日志）
            _cleanupTimer = new System.Timers.Timer(TimeSpan.FromHours(1).TotalMilliseconds);
            _cleanupTimer.Elapsed += OnCleanupTimerElapsed;
            _cleanupTimer.AutoReset = true;
            _cleanupTimer.Start();

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
                System.Diagnostics.Debug.WriteLine($"[数据清理] 已删除 15 天前的历史数据");
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
        private async Task MonitorDeviceStatusAsync()
        {
            // #region agent log - Hypothesis B/E: 监控轮询耗时
            var _dbgMonitorSw = System.Diagnostics.Stopwatch.StartNew();
            // #endregion
            var offlineList = new List<Device>();

            foreach (var wrapper in _wrappers)
            {
                var wasOnline = wrapper.Device.IsOnline;
                var isCurrentlyConnected = wrapper.PlcService.CurrentStatus.IsConnected;

                // 检测连接状态变化
                if (wasOnline && !isCurrentlyConnected)
                {
                    // 设备掉线
                    wrapper.Device.IsOnline = false;

                    DeviceStatusChanged?.Invoke(this, new DeviceStatusChangeEventArgs
                    {
                        Device = wrapper.Device,
                        WasOnline = true,
                        IsOnline = false,
                        ChangeTime = DateTime.Now
                    });

                    System.Diagnostics.Debug.WriteLine($"[掉线] {wrapper.Device.Name} ({wrapper.Device.IpAddress})");

                    // TODO: 钉钉掉线报警暂时禁用，后续启用时取消注释
                    // _ = DingTalkService.Instance.SendDeviceOfflineAlertAsync(
                    //     wrapper.Device.Name,
                    //     wrapper.Device.IpAddress);
                }
                else if (!wasOnline && isCurrentlyConnected)
                {
                    // 设备恢复
                    wrapper.Device.IsOnline = true;

                    DeviceStatusChanged?.Invoke(this, new DeviceStatusChangeEventArgs
                    {
                        Device = wrapper.Device,
                        WasOnline = false,
                        IsOnline = true,
                        ChangeTime = DateTime.Now
                    });

                    System.Diagnostics.Debug.WriteLine($"[恢复] {wrapper.Device.Name} ({wrapper.Device.IpAddress})");
                }

                wrapper.Device.IsOnline = isCurrentlyConnected;

                if (isCurrentlyConnected)
                {
                    // 从 PLC 状态同步到 Device，列表页才能显示温度等
                    await wrapper.UpdateStatusAsync();
                }
                else
                {
                    offlineList.Add(wrapper.Device);

                    // 后台自动重连：仅对"用户曾主动连接成功过"的设备生效
                    TryScheduleReconnect(wrapper);
                }
            }

            // 更新掉线设备列表
            App.Current?.Dispatcher.BeginInvoke(() =>
            {
                OfflineDevices.Clear();
                foreach (var device in offlineList)
                {
                    OfflineDevices.Add(device);
                }

                OfflineDeviceCount = offlineList.Count;
                HasOfflineDevices = offlineList.Count > 0;
            });

            // 串口发送含 Thread.Sleep(100)，不阻塞当前监控线程
            Task.Run(() => UpdateTowerLight());

            // #region agent log - Hypothesis B/E: 监控耗时超过1秒报警
            _dbgMonitorSw.Stop();
            if (_dbgMonitorSw.ElapsedMilliseconds > 1000)
            {
                Views.MainWindow.DbgLog("DeviceManagerService:Monitor", "监控轮询耗时过长", new
                {
                    elapsedMs = _dbgMonitorSw.ElapsedMilliseconds,
                    offlineCount = offlineList.Count
                }, "B/E");
            }
            // #endregion
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
                    System.Diagnostics.Debug.WriteLine($"[自动重连] 尝试重连 {w.Device.Name} ({w.Device.IpAddress})");
                    var ok = await w.PlcService.ConnectAsync();
                    if (ok)
                    {
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
        /// 根据所有在线设备的实时温度更新三色灯状态：
        /// 任意设备温度 > 90°C  → 红灯 + 蜂鸣器
        /// 任意设备温度 > 86°C  → 黄灯（警告），蜂鸣器关
        /// 至少一台设备在线     → 绿灯（正常），蜂鸣器关
        /// 全部设备离线         → 灭灯
        /// </summary>
        private void UpdateTowerLight()
        {
            if (_towerLight == null) return;

            bool anyOnline = false;
            bool anyWarning = false;   // 温度 > 86°C
            bool anyAlarm   = false;   // 温度 > 90°C

            foreach (var wrapper in _wrappers)
            {
                if (!wrapper.Device.IsOnline)
                    continue;

                anyOnline = true;
                float temp = wrapper.PlcService.CurrentStatus.Temperature;

                if (temp > 90f)
                    anyAlarm = true;
                else if (temp > 86f)
                    anyWarning = true;
            }

            // 优先级：报警 > 警告 > 正常 > 全灭
            if (anyAlarm)
            {
                _towerLight.Send("Red");
                _towerLight.Send("BuzzerOn");
            }
            else if (anyWarning)
            {
                _towerLight.Send("Yellow");
                _towerLight.Send("BuzzerOff");
            }
            else if (anyOnline)
            {
                _towerLight.Send("Green");
                _towerLight.Send("BuzzerOff");
            }
            else
            {
                _towerLight.Send("Off");
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
                // MAddressList 与 MReadBlocks 严格对齐：总条目数 = 各块 Count 之和
                // 合并饰屏中间地址后 TCP 请求从 10 次减少到 4 次
                MAddressList = Enumerable.Range(1, 12)      // M1-M12  (12个)
                    .Select(i => $"M{i}")
                    .Concat(Enumerable.Range(102, 2)         // M102-M103 (2个)
                        .Select(i => $"M{i}"))
                    .Concat(Enumerable.Range(110, 21)        // M110-M130 (21个)
                        .Select(i => $"M{i}"))
                    .Concat(Enumerable.Range(160, 21)        // M160-M180 (21个)
                        .Select(i => $"M{i}"))
                    .ToList(),
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

                // MAddressList 与 MReadBlocks 严格对齐：总条目数 = 各块 Count 之和
                // 合并后 TCP 请求从 12 次减少到 3 次
                MAddressList = Enumerable.Range(64, 70)      // M64-M133  (70个)
                    .Select(i => $"M{i}")
                    .Concat(Enumerable.Range(204, 9)          // M204-M212 (9个)
                        .Select(i => $"M{i}"))
                    .Concat(Enumerable.Range(600, 102)        // M600-M701 (102个)
                        .Select(i => $"M{i}"))
                    .ToList(),
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
                TemperatureIsWord = true,  // D10为16位Word寄存器，非DINT，用ReadInt16读取
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

                // MAddressList 与 MReadBlocks 严格对齐：总条目数(28) = 各块 Count 之和(28)
                // 只列出用户指定的点，按散点分小块读取，面板不显示多余 M 点
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
                    new MReadBlock("M64",   1),   // 步骤三指示灯
                    new MReadBlock("M74",   1),   // 步骤八指示灯
                    new MReadBlock("M104",  1),   // 步骤六指示灯
                    new MReadBlock("M111",  1),   // 步骤一指示灯
                    new MReadBlock("M124",  1),   // 步骤二指示灯
                    new MReadBlock("M127",  2),   // 步骤五指示灯(M127) + 步骤七指示灯(M128)
                    new MReadBlock("M133",  1),   // 步骤四指示灯
                    new MReadBlock("M204",  9),   // 手动控制 M204-M212
                    new MReadBlock("M600",  2),   // 系统停止(M600) + 步骤一执行(M601)
                    new MReadBlock("M603",  8),   // 步骤二-八执行(M603-M609) + 系统复位(M610)
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
                TemperatureDivisor = 100f,     // D10 存储 temp×100（如 7000=70.0°C），显示需除以100
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

        /// <summary>
        /// 连接指定设备
        /// </summary>
        public async Task<bool> ConnectDeviceAsync(int deviceId)
        {
            var wrapper = _wrappers.FirstOrDefault(d => d.Device.Id == deviceId);
            if (wrapper == null) return false;

            var success = await wrapper.PlcService.ConnectAsync();
            if (success)
            {
                wrapper.PlcService.StartAcquisition();
                UpdateDeviceOnlineState(wrapper, true);
                // 记入自动重连白名单：掉线后允许后台自动恢复
                _autoReconnectIds.TryAdd(deviceId, 0);
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
            var tasks = _wrappers.Select(async wrapper =>
            {
                try
                {
                    var success = await wrapper.PlcService.ConnectAsync();
                    if (success)
                    {
                        wrapper.PlcService.StartAcquisition();
                        UpdateDeviceOnlineState(wrapper, true);
                        // 记入自动重连白名单：掉线后允许后台自动恢复
                        _autoReconnectIds.TryAdd(wrapper.Device.Id, 0);
                    }
                    else
                    {
                        var err = (wrapper.PlcService as MitsubishiPlcService)?.LastConnectionError;
                        if (!string.IsNullOrEmpty(err))
                            failedReasons.Add($"{wrapper.Device.Name}: {err}");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"连接设备 {wrapper.Device.Name} 失败: {ex.Message}");
                    failedReasons.Add($"{wrapper.Device.Name}: {ex.Message}");
                }
            });

            await Task.WhenAll(tasks);
            int successCount = _wrappers.Count(w => w.PlcService.CurrentStatus.IsConnected);
            return (successCount, failedReasons);
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
                Application.Current?.Dispatcher.BeginInvoke(Update);

            // 串口发送含 Thread.Sleep(100)，不阻塞 UI 线程
            Task.Run(() => UpdateTowerLight());
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
                App.Current?.Dispatcher.BeginInvoke(() => device.TodayOperationCount++);
            }

            _logBuffer.EnqueueOperationLog(log);
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
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[温度入库] 设备{deviceId} 入队失败: {ex.Message}");
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

            DisconnectAllDevices();

            try { _towerLight?.Dispose(); } catch { }
            try { _logBuffer?.Dispose(); } catch { }
            try { (_dataService as IDisposable)?.Dispose(); } catch { }
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
