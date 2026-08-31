using System;
using MitsubishiMonitor.Demo.Models;

namespace MitsubishiMonitor.Demo.Services
{
    /// <summary>设备模式对应的连接授权、报警参与范围和重连节奏。</summary>
    public static class DeviceMonitoringPolicy
    {
        private const int AutoStandbyBaseDelaySeconds = 30;
        private const int RequiredOnlineBaseDelaySeconds = 5;
        private const int MaximumDelaySeconds = 60;

        public static bool IsConnectionAuthorized(DeviceMonitoringMode mode)
            => mode != DeviceMonitoringMode.Disabled;

        public static bool IsExpectedOnline(DeviceMonitoringMode mode)
            => mode == DeviceMonitoringMode.RequiredOnline;

        public static bool ParticipatesInTowerLight(DeviceMonitoringMode mode, bool isOnline)
            => mode == DeviceMonitoringMode.RequiredOnline ||
               (mode == DeviceMonitoringMode.AutoStandby && isOnline);

        /// <summary>
        /// 返回未加随机抖动的指数退避。自动待机从 30 秒开始，避免关机设备形成高频探测；
        /// 要求在线从 5 秒开始，以便生产掉线时更快恢复。
        /// </summary>
        public static TimeSpan GetReconnectDelay(DeviceMonitoringMode mode, int attempt)
        {
            if (!IsConnectionAuthorized(mode))
                return System.Threading.Timeout.InfiniteTimeSpan;

            var baseDelaySeconds = mode == DeviceMonitoringMode.RequiredOnline
                ? RequiredOnlineBaseDelaySeconds
                : AutoStandbyBaseDelaySeconds;
            var exponent = Math.Min(6, Math.Max(0, attempt - 1));
            var seconds = Math.Min(
                MaximumDelaySeconds,
                baseDelaySeconds * Math.Pow(2, exponent));
            return TimeSpan.FromSeconds(seconds);
        }
    }
}
