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
        /// 兼容老库的历史阈值列。新代码同时写入 AlarmThreshold，不再把 PLC 目标温度写入此字段。
        /// </summary>
        public float Threshold { get; set; } = 90f;

        /// <summary>本条温度用于报警判断的软件阈值。</summary>
        public float AlarmThreshold { get; set; } = 90f;

        /// <summary>PLC 最近一次有效目标温度；与报警阈值是两个独立概念。</summary>
        public float TargetTemperature { get; set; }

        /// <summary>三相电压等辅助遥测实际采样时间；为空表示历史库无此信息。</summary>
        public DateTime? AuxiliarySampleTime { get; set; }

        /// <summary>写入时辅助遥测是否满足本轮新鲜度要求。</summary>
        public bool HasFreshAuxiliaryData { get; set; }

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
