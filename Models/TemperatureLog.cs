using System;

namespace MitsubishiMonitor.Demo.Models
{
    /// <summary>
    /// 温度日志模型
    /// </summary>
    public class TemperatureLog
    {
        /// <summary>
        /// 主键ID
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// 设备ID (Demo固定为1)
        /// </summary>
        public int DeviceId { get; set; } = 1;

        /// <summary>
        /// 设备名称（写日志时的快照，避免日后改名导致历史数据失忆）
        /// </summary>
        public string DeviceName { get; set; } = string.Empty;

        /// <summary>
        /// 温度值 (浮点数)
        /// </summary>
        public float Temperature { get; set; }

        /// <summary>
        /// 热电偶A电压
        /// </summary>
        public float ThermocoupleA { get; set; }

        /// <summary>
        /// 热电偶B电压
        /// </summary>
        public float ThermocoupleB { get; set; }

        /// <summary>
        /// 热电偶C电压
        /// </summary>
        public float ThermocoupleC { get; set; }

        /// <summary>
        /// 记录时间
        /// </summary>
        public DateTime RecordTime { get; set; } = DateTime.Now;

        /// <summary>
        /// 是否异常 (温度超过阈值)
        /// </summary>
        public bool IsAbnormal { get; set; }

        /// <summary>
        /// 异常阈值
        /// </summary>
        public float Threshold { get; set; } = 50f;

        /// <summary>
        /// 格式化的温度显示
        /// </summary>
        public string TemperatureDisplay => $"{Temperature:F1}°C";

        /// <summary>
        /// 格式化的时间显示
        /// </summary>
        public string TimeDisplay => RecordTime.ToString("HH:mm:ss");
    }
}
