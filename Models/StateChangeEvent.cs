using System;

namespace MitsubishiMonitor.Demo.Models
{
    /// <summary>
    /// 状态变化事件模型
    /// </summary>
    public class StateChangeEvent
    {
        /// <summary>
        /// 点类型 (X或Y)
        /// </summary>
        public string PointType { get; set; } = "X";

        /// <summary>
        /// 点索引 (0-5)
        /// </summary>
        public int PointIndex { get; set; }

        /// <summary>
        /// 旧值
        /// </summary>
        public bool OldValue { get; set; }

        /// <summary>
        /// 新值
        /// </summary>
        public bool NewValue { get; set; }

        /// <summary>
        /// 事件时间
        /// </summary>
        public DateTime EventTime { get; set; } = DateTime.Now;

        /// <summary>
        /// 点位中文标签
        /// </summary>
        public string PointLabel { get; set; } = "";

        /// <summary>
        /// 完整 PLC 地址（X/Y 走八进制，M 取自 PlcConfig.MAddressList）。
        /// 必须由发起事件方（MitsubishiPlcService）通过 PlcConfig.GetXAddress/GetYAddress/GetMAddress 显式填入，
        /// 否则会回退到 PointType+PointIndex 的拼接，仅作降级用途。
        /// </summary>
        public string Address
        {
            get => string.IsNullOrEmpty(_address) ? FallbackAddress(PointType, PointIndex) : _address;
            set => _address = value;
        }

        private string _address;

        private static string FallbackAddress(string pointType, int index)
        {
            if (pointType == "X" || pointType == "Y")
                return pointType + Convert.ToString(index, 8);
            return $"{pointType}{index}";
        }

        /// <summary>
        /// 事件描述（包含中文标签）
        /// </summary>
        public string Description
        {
            get
            {
                string action = NewValue switch
                {
                    true when PointType == "X" => "上升沿 ↑",
                    false when PointType == "X" => "下降沿 ↓",
                    true when PointType == "Y" => "置位 →",
                    false when PointType == "Y" => "复位 ↓",
                    _ => "变化"
                };

                // 如果有中文标签，显示"中文名 (地址)"，否则只显示地址
                string displayName = string.IsNullOrEmpty(PointLabel) || PointLabel == Address
                    ? Address
                    : $"{PointLabel} ({Address})";

                return $"{displayName} {action} ({(OldValue ? "ON" : "OFF")} → {(NewValue ? "ON" : "OFF")})";
            }
        }

        /// <summary>
        /// 日志类型 (用于图标显示)
        /// </summary>
        public string LogType => PointType;

        /// <summary>
        /// 操作员 (Demo版本暂无登录系统)
        /// </summary>
        public string Operator { get; set; } = "-";
    }
}
