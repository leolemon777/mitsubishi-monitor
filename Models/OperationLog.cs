using System;

namespace MitsubishiMonitor.Demo.Models
{
    /// <summary>
    /// 操作日志模型 (数据库实体)
    /// </summary>
    public class OperationLog
    {
        /// <summary>
        /// 主键ID
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// 设备ID
        /// </summary>
        public int DeviceId { get; set; } = 1;

        /// <summary>
        /// 设备名称（写日志时的快照，避免日后改名导致历史数据失忆）
        /// </summary>
        public string DeviceName { get; set; } = string.Empty;

        /// <summary>
        /// 日志类型 (X, Y, TEMP)
        /// </summary>
        public string LogType { get; set; } = "X";

        /// <summary>
        /// 地址 (如 "X0", "Y1")
        /// </summary>
        public string PointAddress { get; set; }

        /// <summary>
        /// 中文点位标签（如 "反应槽加热信号"），便于直接在 SQL/Excel 中阅读
        /// </summary>
        public string PointLabel { get; set; } = string.Empty;

        /// <summary>
        /// 动作描述
        /// </summary>
        public string Action { get; set; } = string.Empty;

        /// <summary>
        /// 完整描述
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// 操作员
        /// </summary>
        public string Operator { get; set; } = "-";

        /// <summary>
        /// 记录时间
        /// </summary>
        public DateTime LogTime { get; set; } = DateTime.Now;

        /// <summary>
        /// 格式化时间显示
        /// </summary>
        public string TimeDisplay => LogTime.ToString("HH:mm:ss");

        /// <summary>
        /// 日志图标
        /// </summary>
        public string Icon => LogType switch
        {
            "X" => "▶",
            "Y" => "◀",
            "TEMP" => "⚠",
            _ => "•"
        };

        /// <summary>
        /// 从StateChangeEvent创建OperationLog
        /// </summary>
        public static OperationLog FromChangeEvent(StateChangeEvent evt)
        {
            // 使用中文标签作为显示名称
            string displayName = string.IsNullOrEmpty(evt.PointLabel) || evt.PointLabel == evt.Address
                ? evt.Address
                : evt.PointLabel;

            string action = evt.NewValue switch
            {
                true when evt.PointType == "X" => "上升沿",
                false when evt.PointType == "X" => "下降沿",
                true when evt.PointType == "Y" => "置位",
                false when evt.PointType == "Y" => "复位",
                _ => "变化"
            };

            return new OperationLog
            {
                LogType = evt.PointType,
                PointAddress = evt.Address,
                PointLabel = evt.PointLabel ?? string.Empty,
                Action = action,
                Description = $"{displayName} {action} ({(evt.OldValue ? "ON" : "OFF")} → {(evt.NewValue ? "ON" : "OFF")})",
                LogTime = evt.EventTime
            };
        }
    }
}
