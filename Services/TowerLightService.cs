using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;

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
        private readonly object _sendLock = new();

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

        public static string FindLikelyPortName()
        {
            try
            {
                var keywords = new[] { "CH340", "CH341", "USB-SERIAL", "USB Serial", "VID_1A86", "VID:PID=1A86" };
                var searcher = new ManagementObjectSearcher(
                    "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");

                foreach (ManagementObject device in searcher.Get())
                {
                    var name = Convert.ToString(device["Name"]) ?? "";
                    var pnpId = Convert.ToString(device["PNPDeviceID"]) ?? "";
                    var text = name + " " + pnpId;

                    foreach (var kw in keywords)
                    {
                        if (text.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var match = Regex.Match(name, @"\((COM\d+)\)");
                            if (match.Success)
                                return match.Groups[1].Value;
                        }
                    }
                }
            }
            catch { }

            var ports = SerialPort.GetPortNames();
            if (ports.Length == 1)
                return ports[0];

            throw new InvalidOperationException("未能自动识别 TC60 三色灯串口，请手动指定 COM 口。");
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
                LastError = $"三色灯连接失败({PortName}): {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"[三色灯] {LastError}");
                return false;
            }
        }

        public bool Send(string commandName)
        {
            if (!Commands.TryGetValue(commandName, out var command))
            {
                LastError = $"不支持的命令: {commandName}";
                return false;
            }

            lock (_sendLock)
            {
                try
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
                catch (Exception ex)
                {
                    LastError = $"三色灯发送失败: {ex.Message}";
                    System.Diagnostics.Debug.WriteLine($"[三色灯] {LastError}");
                    return false;
                }
            }
        }

        /// <summary>
        /// 根据系统状态设置灯光
        /// </summary>
        /// <param name="hasAlarm">是否有温度报警或SSR故障</param>
        /// <param name="hasOffline">是否有设备掉线</param>
        /// <param name="allNormal">全部正常</param>
        public void UpdateLightState(bool hasAlarm, bool hasOffline, bool allNormal)
        {
            if (hasAlarm)
            {
                Send("Red");
                Send("BuzzerOn");
            }
            else if (hasOffline)
            {
                Send("Yellow");
                Send("BuzzerOff");
            }
            else if (allNormal)
            {
                Send("Green");
                Send("BuzzerOff");
            }
            else
            {
                Send("Off");
            }
        }

        public void TurnOff()
        {
            Send("Off");
        }

        public void Dispose()
        {
            try { TurnOff(); } catch { }
            try
            {
                if (_serialPort?.IsOpen == true)
                    _serialPort.Close();
                _serialPort?.Dispose();
            }
            catch { }
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
