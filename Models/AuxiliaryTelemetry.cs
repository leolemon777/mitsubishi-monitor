using System;

namespace MitsubishiMonitor.Demo.Models
{
    public static class AuxiliaryTelemetry
    {
        public static bool IsFresh(PlcStatus status, int intervalMs, DateTime referenceTime)
        {
            if (status == null || !status.IsConnected || status.LastAuxiliarySampleTime == default)
                return false;
            var age = referenceTime - status.LastAuxiliarySampleTime;
            return age >= TimeSpan.Zero &&
                   age.TotalMilliseconds <= Math.Max(5000d, intervalMs * 2.5d) &&
                   float.IsFinite(status.TargetTemperature) && float.IsFinite(status.ThermocoupleA) &&
                   float.IsFinite(status.ThermocoupleB) && float.IsFinite(status.ThermocoupleC);
        }
    }
}
