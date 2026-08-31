using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MitsubishiMonitor.Demo.Services
{
    /// <summary>
    /// TC60 USB 三色灯控制服务
    /// 通信：USB转串口（CH340），9600 波特率，8E1
    /// 协议：Modbus RTU 功能码 0x05，设备地址 0x01
    /// </summary>
    public sealed class TowerLightService : IDisposable
    {
        private readonly SerialPort _serialPort;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly object _stateQueueSync = new();
        private readonly SemaphoreSlim _stateQueueSignal = new(0, 1);
        private readonly CancellationTokenSource _stateQueueCts = new();
        private Task _stateQueueTask;
        private string _queuedState = "";
        private bool _stateSignalPending;
        private string _appliedState = "";
        private int _disposed;

        private static readonly Dictionary<string, byte[]> Commands = new Dictionary<string, byte[]>
        {
            ["Off"] = FromHex("01 05 00 00 00 00 CD CA"),
            ["Red"] = FromHex("01 05 00 01 FF 00 DD FA"),
            ["Yellow"] = FromHex("01 05 00 02 FF 00 2D FA"),
            ["Green"] = FromHex("01 05 00 03 FF 00 7C 3A"),
            ["BuzzerOn"] = FromHex("01 05 00 04 FF 00 CD FB"),
            ["BuzzerOff"] = FromHex("01 05 00 04 00 00 8C 0B"),
            ["RedFlash"] = FromHex("01 05 00 06 FF 00 6C 3B"),
            ["YellowFlash"] = FromHex("01 05 00 07 FF 00 3D FB"),
            ["GreenFlash"] = FromHex("01 05 00 08 FF 00 0D F8"),
            ["BuzzerFlash"] = FromHex("01 05 00 09 FF 00 5C 38"),
        };

        public bool IsConnected => _serialPort?.IsOpen == true;
        public string PortName => _serialPort?.PortName ?? "";
        public string LastError { get; private set; } = "";
        public string AppliedState => _appliedState;
        public bool HasQueuedState
        {
            get
            {
                lock (_stateQueueSync)
                    return !string.IsNullOrEmpty(_queuedState);
            }
        }

        public TowerLightService(string portName = null)
        {
            var actualPort = string.IsNullOrWhiteSpace(portName) ? FindLikelyPortName() : portName;
            _serialPort = new SerialPort(actualPort, 9600, Parity.Even, 8, StopBits.One)
            {
                ReadTimeout = 500,
                WriteTimeout = 1000
            };
        }

        public static string[] GetAvailablePortNames()
        {
            return SerialPort.GetPortNames();
        }

        /// <summary>
        /// 自动扫描识别 CH340 串口
        /// 优先匹配厂商 ID，其次匹配常见驱动名称关键词
        /// </summary>
        public static string FindLikelyPortName()
        {
            try
            {
                var keywords = new[]
                {
                    "CH340", "CH341",
                    "USB-SERIAL", "USB Serial", "USB2.0-Serial",
                    "USB to Serial", "USB_SERIAL",
                    "VID_1A86",
                    "VID:PID=1A86",
                    "1A86&PID_7523",
                    "1A86&PID_7522",
                    "1A86&PID_55D4",
                    "HL-340", "HL340",
                };

                var searcher = new ManagementObjectSearcher(
                    "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");

                var allComPorts = new List<string>();

                foreach (ManagementObject device in searcher.Get())
                {
                    var name = Convert.ToString(device["Name"]) ?? "";
                    var pnpId = Convert.ToString(device["PNPDeviceID"]) ?? "";
                    var text = name + " " + pnpId;

                    var portMatch = Regex.Match(name, @"\((COM\d+)\)");
                    if (portMatch.Success)
                        allComPorts.Add(portMatch.Groups[1].Value);

                    foreach (var kw in keywords)
                    {
                        if (text.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (portMatch.Success)
                                return portMatch.Groups[1].Value;
                        }
                    }
                }

                var allSystemPorts = SerialPort.GetPortNames();
                if (allSystemPorts.Length == 1)
                    return allSystemPorts[0];
            }
            catch { }

            throw new InvalidOperationException("未能自动识别 TC60 三色灯串口，请手动指定 COM 口。");
        }

        /// <summary>
        /// 诊断为何找不到 COM 口：检查是否存在未安装驱动的 CH340 USB 设备
        /// 返回诊断信息字符串，帮助用户定位问题
        /// </summary>
        public static string DiagnoseNoPortFound()
        {
            try
            {
                var ch340Vids = new[] { "VID_1A86", "1A86" };

                var searcher = new ManagementObjectSearcher(
                    "SELECT Name, DeviceID, Status, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB%'");

                var unrecognizedDevices = new List<string>();

                foreach (ManagementObject device in searcher.Get())
                {
                    var deviceId = Convert.ToString(device["DeviceID"]) ?? "";
                    var name = Convert.ToString(device["Name"]) ?? "";
                    var errorCode = Convert.ToInt32(device["ConfigManagerErrorCode"]);

                    bool isCh340 = false;
                    foreach (var vid in ch340Vids)
                    {
                        if (deviceId.IndexOf(vid, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            isCh340 = true;
                            break;
                        }
                    }

                    if (!isCh340) continue;

                    if (errorCode != 0)
                    {
                        unrecognizedDevices.Add(
                            $"  设备: {name}\n  ID: {deviceId}\n  错误码: {errorCode}（缺少驱动）");
                    }
                    else
                    {
                        unrecognizedDevices.Add(
                            $"  设备: {name}\n  ID: {deviceId}\n  状态: 驱动已装但未映射 COM 口");
                    }
                }

                if (unrecognizedDevices.Count > 0)
                {
                    return "⚠️ 检测到 USB 三色灯硬件已插入，但系统缺少 CH340 驱动！\n\n"
                         + string.Join("\n\n", unrecognizedDevices)
                         + "\n\n解决方法：安装 CH340 驱动后重新检测。\n"
                         + "请通过设置页打开 WCH 官方下载页面，并核对数字签名后手动安装。";
                }

                var unknownSearcher = new ManagementObjectSearcher(
                    "SELECT Name, DeviceID FROM Win32_PnPEntity WHERE ConfigManagerErrorCode != 0 AND DeviceID LIKE 'USB%'");

                var unknownList = new List<string>();
                foreach (ManagementObject device in unknownSearcher.Get())
                {
                    var name = Convert.ToString(device["Name"]) ?? "";
                    var deviceId = Convert.ToString(device["DeviceID"]) ?? "";
                    unknownList.Add($"  {name} ({deviceId})");
                }

                if (unknownList.Count > 0)
                {
                    return "未识别到 CH340 芯片，但发现以下未知 USB 设备：\n"
                         + string.Join("\n", unknownList)
                         + "\n\n如果其中包含三色灯，请安装 CH340 驱动后重试。";
                }

                return "未检测到任何 USB 串口设备。\n请确认：\n"
                     + "  1. 三色灯 USB 线已插好\n"
                     + "  2. 工控机 USB 口供电正常\n"
                     + "  3. 已从 WCH 官方渠道安装并核验 CH340 驱动";
            }
            catch (Exception ex)
            {
                return $"诊断过程异常：{ex.Message}";
            }
        }

        public bool TryConnect()
        {
            try
            {
                if (!_serialPort.IsOpen)
                    _serialPort.Open();
                LastError = "";
                return true;
            }
            catch (Exception ex)
            {
                LastError = $"三色灯连接失败 ({PortName}): {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"[三色灯] {LastError}");
                return false;
            }
        }

        // 异步发送命令。即使调用方是 WPF Dispatcher，也必须把同步 SerialPort
        // Open/Write/Read 放入后台线程；ConfigureAwait(false) 只影响 await 之后，
        // 不能解决 await 之前已同步完成的锁和串口操作。
        public async Task<bool> SendAsync(string commandName)
        {
            if (!Commands.TryGetValue(commandName, out var command))
            {
                LastError = "不支持的命令: " + commandName;
                return false;
            }

            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                return await Task.Run(() => SendCore(command), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LastError = "三色灯发送失败: " + ex.Message;
                System.Diagnostics.Debug.WriteLine("[三色灯] " + LastError);
                return false;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private bool SendCore(byte[] command)
        {
            if (!_serialPort.IsOpen)
                _serialPort.Open();

            _serialPort.DiscardInBuffer();
            _serialPort.Write(command, 0, command.Length);
            Thread.Sleep(100);

            var count = _serialPort.BytesToRead;
            if (count > 0)
            {
                var response = new byte[count];
                _serialPort.Read(response, 0, count);
            }

            return true;
        }

        /// <summary>
        /// 提交期望灯状态，采用“最新状态覆盖旧状态”队列。
        /// 调用方只入队，串口工作由专用后台泵执行，避免 UI/监控线程阻塞。
        /// </summary>
        public void QueueDesiredState(string desiredState)
        {
            if (string.IsNullOrWhiteSpace(desiredState))
                return;

            if (!Enum.TryParse<TowerLightDecision>(desiredState, out _))
                return;

            var signal = false;
            lock (_stateQueueSync)
            {
                if (Volatile.Read(ref _disposed) == 1)
                    return;

                _queuedState = desiredState;
                if (!_stateSignalPending)
                {
                    _stateSignalPending = true;
                    signal = true;
                }

                _stateQueueTask ??= Task.Run(ProcessStateQueueAsync);
            }

            if (signal)
                _stateQueueSignal.Release();
        }

        private async Task ProcessStateQueueAsync()
        {
            try
            {
                while (!_stateQueueCts.IsCancellationRequested)
                {
                    await _stateQueueSignal.WaitAsync(_stateQueueCts.Token).ConfigureAwait(false);
                    string desired;
                    lock (_stateQueueSync)
                    {
                        desired = _queuedState;
                        _queuedState = "";
                        _stateSignalPending = false;
                    }

                    if (string.IsNullOrEmpty(desired) ||
                        string.Equals(desired, _appliedState, StringComparison.Ordinal))
                        continue;

                    var ok = await SendDesiredStateAsync(desired).ConfigureAwait(false);
                    if (ok)
                    {
                        _appliedState = desired;
                        continue;
                    }

                    // 驱动/USB 瞬断时保留最新期望状态，稍后只重试最新值，
                    // 不把已经过时的红/黄/绿命令排队堆积。
                    lock (_stateQueueSync)
                    {
                        if (string.IsNullOrEmpty(_queuedState))
                            _queuedState = desired;
                        if (!_stateSignalPending)
                        {
                            _stateSignalPending = true;
                            _stateQueueSignal.Release();
                        }
                    }
                    await Task.Delay(1000, _stateQueueCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_stateQueueCts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                LastError = "三色灯状态泵异常: " + ex.Message;
                System.Diagnostics.Debug.WriteLine("[三色灯] " + LastError);
            }
        }

        private async Task<bool> SendDesiredStateAsync(string desiredState)
        {
            switch (desiredState)
            {
                case nameof(TowerLightDecision.RedBuzzerOn):
                    return await SendAsync("Red").ConfigureAwait(false) &
                           await SendAsync("BuzzerOn").ConfigureAwait(false);
                case nameof(TowerLightDecision.RedBuzzerOff):
                    return await SendAsync("Red").ConfigureAwait(false) &
                           await SendAsync("BuzzerOff").ConfigureAwait(false);
                case nameof(TowerLightDecision.Green):
                    return await SendAsync("Green").ConfigureAwait(false) &
                           await SendAsync("BuzzerOff").ConfigureAwait(false);
                case nameof(TowerLightDecision.Yellow):
                    return await SendAsync("Yellow").ConfigureAwait(false) &
                           await SendAsync("BuzzerOff").ConfigureAwait(false);
                default:
                    return await SendAsync("Off").ConfigureAwait(false);
            }
        }

        // 同步版 Send：内部阻塞等待 SendAsync，禁止在 UI 线程调用。
        // 目前用于 Dispose/TurnOff 关闭路径和设置页后台线程的点灯测试。
        public bool Send(string commandName)
        {
            return SendAsync(commandName).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 根据系统状态设置灯光
        /// </summary>
        public async Task UpdateLightStateAsync(bool hasAlarm, bool hasOffline, bool allNormal)
        {
            if (hasAlarm)
            {
                await SendAsync("Red");
                await SendAsync("BuzzerOn");
            }
            else if (hasOffline)
            {
                await SendAsync("Yellow");
                await SendAsync("BuzzerOff");
            }
            else if (allNormal)
            {
                await SendAsync("Green");
                await SendAsync("BuzzerOff");
            }
            else
            {
                await SendAsync("Off");
            }
        }

        public void TurnOff()
        {
            Send("Off");
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;
            _stateQueueCts.Cancel();
            // Dispose 可能由 WPF Dispatcher 调用，关闭串口也必须异步完成。
            // 先尝试下发 Off，再释放驱动；调用方不需要等待 USB 驱动返回。
            _ = Task.Run(async () =>
            {
                try { await SendAsync("Off").ConfigureAwait(false); } catch { }
                try
                {
                    if (_serialPort?.IsOpen == true)
                        _serialPort.Close();
                    _serialPort?.Dispose();
                }
                catch { }
            });
            // 不同步 Dispose 这些信号量：队列中的最后一个后台串口任务可能仍在
            // finally 中释放 _sendLock。它们随服务对象回收，避免退出路径抛出二次异常。
        }

        private static byte[] FromHex(string hex)
        {
            var clean = hex.Replace(" ", "");
            var bytes = new byte[clean.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(clean.Substring(i * 2, 2), 16);
            return bytes;
        }
    }
}
