using System.Collections.Generic;
using MitsubishiMonitor.Demo.Models;

namespace MitsubishiMonitor.Demo.Services
{
    public enum TowerLightDecision
    {
        Off,
        Green,
        Yellow,
        RedBuzzerOn,
        RedBuzzerOff
    }

    /// <summary>三色灯纯决策规则，便于覆盖全部在线、离线、过期和报警组合测试。</summary>
    public static class TowerLightPolicy
    {
        public readonly record struct DeviceState(
            DeviceMonitoringMode MonitoringMode,
            bool IsOnline,
            bool IsFresh,
            bool HasActiveAlarm);

        /// <summary>
        /// 自动待机设备关机时不参与灯态；一旦在线就参与新鲜度和报警判断。
        /// 要求在线设备始终参与；停用设备始终排除。
        /// </summary>
        public static TowerLightDecision Decide(
            IEnumerable<DeviceState> devices,
            bool hasSystemFault,
            bool isBuzzerMuted)
        {
            var monitoredDeviceCount = 0;
            var onlineFreshDeviceCount = 0;
            var hasActiveAlarm = false;

            if (devices != null)
            {
                foreach (var device in devices)
                {
                    if (!DeviceMonitoringPolicy.ParticipatesInTowerLight(
                            device.MonitoringMode,
                            device.IsOnline))
                        continue;

                    monitoredDeviceCount++;
                    if (!device.IsOnline || !device.IsFresh)
                        continue;

                    onlineFreshDeviceCount++;
                    if (device.HasActiveAlarm)
                        hasActiveAlarm = true;
                }
            }

            return Decide(
                monitoredDeviceCount,
                onlineFreshDeviceCount,
                hasActiveAlarm,
                hasSystemFault,
                isBuzzerMuted);
        }

        public static TowerLightDecision Decide(
            int monitoredDeviceCount,
            int onlineFreshDeviceCount,
            bool hasActiveAlarm,
            bool hasSystemFault,
            bool isBuzzerMuted)
        {
            if (hasActiveAlarm)
                return isBuzzerMuted
                    ? TowerLightDecision.RedBuzzerOff
                    : TowerLightDecision.RedBuzzerOn;

            if (monitoredDeviceCount <= 0)
                return TowerLightDecision.Off;

            if (hasSystemFault || onlineFreshDeviceCount != monitoredDeviceCount)
                return TowerLightDecision.Yellow;

            return TowerLightDecision.Green;
        }
    }
}
