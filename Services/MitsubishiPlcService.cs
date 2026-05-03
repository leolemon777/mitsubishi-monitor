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

        public MitsubishiPlcService()
        {
            _config = new PlcConfig();
            _status = new PlcStatus(_config.XCount, _config.YCount, _config.ActualMCount);
            _plc = new MelsecA1ENet(_config.IpAddress, _config.Port);
            _plc.ReceiveTimeOut = 3000;  // 读取超时3秒，防止无线丢包时TCP读挂死
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
            _plc.ReceiveTimeOut = 3000;  // 读取超时3秒，防止无线丢包时TCP读挂死
            _lastX = new bool[_config.XCount];
            _lastY = new bool[_config.YCount];
            _lastM = new bool[_config.ActualMCount];

            // TODO: 钉钉报警暂时禁用，后续启用时取消注释
            // if (!string.IsNullOrEmpty(_config.DingTalkWebhook))
            // {
            //     DingTalkService.Instance.SetWebhook(_config.DingTalkWebhook);
            // }
        }

        public async Task<bool> ConnectAsync()
        {
            try
            {
                _plc.IpAddress = _config.IpAddress;
                _plc.Port = _config.Port;
                _plc.ConnectTimeOut = _config.ConnectTimeout;

                System.Diagnostics.Debug.WriteLine($"[PLC连接] 尝试连接 {_config.Name} ({_config.IpAddress}:{_config.Port})");

                var result = await Task.Run(() => _plc.ConnectServer());

                if (result.IsSuccess)
                {
                    LastConnectionError = "";
                    _isConnected = true;
                    _status.IsConnected = true;
                    ConnectionStateChanged?.Invoke(this, true);
                    System.Diagnostics.Debug.WriteLine($"[PLC连接] ✓ 连接成功: {_config.Name}");
                    return true;
                }
                else
                {
                    LastConnectionError = result.Message ?? "未知错误";
                    _isConnected = false;
                    _status.IsConnected = false;
                    ConnectionStateChanged?.Invoke(this, false);
                    System.Diagnostics.Debug.WriteLine($"[PLC连接] ✗ 连接失败: {_config.Name} - {LastConnectionError}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                LastConnectionError = ex.Message ?? "未知异常";
                System.Diagnostics.Debug.WriteLine($"[PLC连接] ✗ 连接异常: {_config.Name} - {LastConnectionError}");
                _status.IsConnected = false;
                ConnectionStateChanged?.Invoke(this, false);
                return false;
            }
        }

        public void Disconnect()
        {
            StopAcquisition();
            _plc.ConnectClose();
            _isConnected = false;
            _status.IsConnected = false;
            ConnectionStateChanged?.Invoke(this, false);
        }

        public async Task<bool[]> ReadXPointsAsync()
        {
            try
            {
                if (!_isConnected)
                {
                    System.Diagnostics.Debug.WriteLine($"[X点读取] ⚠ 未连接，跳过读取");
                    return new bool[_config.XCount];
                }

                var result = await Task.Run(() => _plc.ReadBool(_config.XStartAddress, (ushort)_config.XCount));

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

                    if (result.ErrorCode == 1013)
                    {
                        _isConnected = false;
                        _status.IsConnected = false;
                        ConnectionStateChanged?.Invoke(this, false);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[X点读取] ✗ 异常: {ex.Message}");
            }

            return new bool[_config.XCount];
        }

        public async Task<bool[]> ReadYPointsAsync()
        {
            try
            {
                if (!_isConnected)
                    return new bool[_config.YCount];

                var result = await Task.Run(() => _plc.ReadBool(_config.YStartAddress, (ushort)_config.YCount));

                if (result.IsSuccess)
                {
                    return result.Content;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[Y点读取] ✗ 失败: {result.Message}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Y点读取] ✗ 异常: {ex.Message}");
            }

            return new bool[_config.YCount];
        }

        public async Task<bool[]> ReadMPointsAsync()
        {
            int totalCount = _config.ActualMCount;
            try
            {
                if (!_isConnected)
                    return new bool[totalCount];

                // 使用 MReadBlocks 配置驱动的读取
                if (_config.MReadBlocks != null && _config.MReadBlocks.Count > 0)
                {
                    var combined = new bool[totalCount];
                    int offset = 0;
                    foreach (var block in _config.MReadBlocks)
                    {
                        var result = await Task.Run(() => _plc.ReadBool(block.StartAddress, block.Count));
                        if (result.IsSuccess)
                        {
                            int copyLen = Math.Min(result.Content.Length, totalCount - offset);
                            Array.Copy(result.Content, 0, combined, offset, copyLen);
                            offset += block.Count;
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"读取M块 {block.StartAddress}×{block.Count} 失败: {result.Message}");
                            offset += block.Count;
                        }
                    }
                    return combined;
                }

                // 旧逻辑兼容：M2009-M2016 + M2451-M2452
                var result1 = await Task.Run(() => _plc.ReadBool("M2009", 8));
                var result2 = await Task.Run(() => _plc.ReadBool("M2451", 2));

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
            }

            return new bool[totalCount];
        }

        /// <summary>
        /// 读取 C/D/T 等寄存器（配置在 CRegisters 列表中，按地址前缀选择读法）
        /// </summary>
        public async Task<Dictionary<string, int>> ReadCRegistersAsync()
        {
            var values = new Dictionary<string, int>();
            if (_config.CRegisters == null || _config.CRegisters.Count == 0)
                return values;

            try
            {
                if (!_isConnected)
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
                            var result16 = await Task.Run(() => _plc.ReadInt16(addr, 1));
                            if (result16.IsSuccess && result16.Content.Length >= 1)
                                values[reg.Address] = result16.Content[0];
                            else
                            {
                                System.Diagnostics.Debug.WriteLine($"读取D寄存器 {reg.Address} 失败");
                                values[reg.Address] = 0;
                            }
                        }
                        else
                        {
                            var result = await Task.Run(() => _plc.ReadInt32(addr, 1));
                            if (result.IsSuccess && result.Content.Length >= 1)
                                values[reg.Address] = result.Content[0];
                            else
                            {
                                System.Diagnostics.Debug.WriteLine($"读取D寄存器 {reg.Address} 失败");
                                values[reg.Address] = 0;
                            }
                        }
                    }
                    else if (addr.StartsWith("T", StringComparison.OrdinalIgnoreCase))
                    {
                        var result = await Task.Run(() => _plc.ReadInt16(addr, 1));
                        if (result.IsSuccess && result.Content.Length >= 1)
                            values[reg.Address] = result.Content[0];
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"读取T寄存器 {reg.Address} 失败");
                            values[reg.Address] = 0;
                        }
                    }
                    else
                    {
                        var result = await Task.Run(() => _plc.ReadInt16(reg.Address, 1));
                        if (result.IsSuccess && result.Content.Length >= 1)
                        {
                            values[reg.Address] = result.Content[0];
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"读取寄存器 {reg.Address} 失败");
                            values[reg.Address] = 0;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"读取寄存器异常: {ex.Message}");
            }

            return values;
        }

        public async Task<float> ReadTemperatureAsync()
        {
            try
            {
                if (!_isConnected)
                {
                    System.Diagnostics.Debug.WriteLine($"[温度读取] ⚠ 未连接，跳过读取");
                    return 0f;
                }

                if (_config.TemperatureIsWord)
                {
                    // 16位 Word 读取（设备3/4等单D寄存器存温度的设备）
                    var result16 = await Task.Run(() => _plc.ReadInt16(_config.TemperatureAddress, 1));
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
                    }
                }
                else
                {
                    // 32位 DINT 读取（默认，读取D地址及下一个D组成32位整数）
                    var result = await Task.Run(() => _plc.ReadInt32(_config.TemperatureAddress, 1));
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
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[温度读取] ✗ 异常: {ex.Message}");
            }

            return 0f;
        }

        public async Task<float> ReadThermocoupleAAsync()
        {
            try
            {
                if (!_isConnected)
                    return 0f;

                // 读取D17-D18作为DINT（32位有符号整数）
                var result = await Task.Run(() => _plc.ReadInt32(_config.ThermocoupleAAddress, 1));

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
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"读取热电偶A异常: {ex.Message}");
            }

            return 0f;
        }

        public async Task<float> ReadThermocoupleBAsync()
        {
            try
            {
                if (!_isConnected)
                    return 0f;

                // 读取D19-D20作为DINT（32位有符号整数）
                var result = await Task.Run(() => _plc.ReadInt32(_config.ThermocoupleBAddress, 1));

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
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"读取热电偶B异常: {ex.Message}");
            }

            return 0f;
        }

        public async Task<float> ReadThermocoupleCAsync()
        {
            try
            {
                if (!_isConnected)
                    return 0f;

                // 读取D21-D22作为DINT（32位有符号整数）
                var result = await Task.Run(() => _plc.ReadInt32(_config.ThermocoupleCAddress, 1));

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
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"读取热电偶C异常: {ex.Message}");
            }

            return 0f;
        }

        public async Task<PlcStatus> ReadAllAsync()
        {
            try
            {
                if (!_isConnected)
                    return null;

                // 并行读取所有数据
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
                return;

            if (!_isConnected)
            {
                throw new InvalidOperationException("请先连接PLC");
            }

            _isAcquiring = true;

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
                    var xValues = await ReadXPointsAsync();
                    var yValues = await ReadYPointsAsync();
                    var mValues = await ReadMPointsAsync();

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
            if (Interlocked.Exchange(ref _isReadingTemp, 1) == 1)
                return;

            Task.Run(async () =>
            {
            try
            {
                var temperature = await ReadTemperatureAsync();
                var targetTemp = await ReadTargetTemperatureAsync();

                _status.Temperature = temperature;
                _status.TargetTemperature = targetTemp;
                _status.LastUpdateTime = DateTime.Now;

                // 仅在有电压配置时读取热电偶数据
                float thermoA = 0, thermoB = 0, thermoC = 0;
                if (_config.HasVoltage)
                {
                    thermoA = await ReadThermocoupleAAsync();
                    thermoB = await ReadThermocoupleBAsync();
                    thermoC = await ReadThermocoupleCAsync();
                    _status.ThermocoupleA = thermoA;
                    _status.ThermocoupleB = thermoB;
                    _status.ThermocoupleC = thermoC;
                }

                // 读取 C 寄存器
                if (_config.HasCRegisters)
                {
                    var cValues = await ReadCRegistersAsync();
                    _status.CValues = cValues;
                }

                bool isAlarm = targetTemp > 0 && temperature > targetTemp;
                bool isSsrFault = false;

                // SSR 故障检测仅在有电压数据时执行
                if (_config.HasVoltage && targetTemp > 0)
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
                    bool tempKeepRising = temperature > targetTemp + 5;

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
                        SampleTime = DateTime.Now,
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
            try
            {
                if (!_isConnected)
                    return 0f;

                var result = await Task.Run(() => _plc.ReadInt32(_config.TargetTemperatureAddress, 1));

                if (result.IsSuccess && result.Content.Length >= 1)
                {
                    int dintValue = result.Content[0];
                    float targetTemp = dintValue / 10.0f;
                    System.Diagnostics.Debug.WriteLine($"[目标温度] D210 DINT值={dintValue}, 目标温度={targetTemp:F1}°C");
                    return targetTemp;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"读取目标温度异常: {ex.Message}");
            }
            return 0f;
        }

        public void Dispose()
        {
            StopAcquisition();
            _plc?.ConnectClose();
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
