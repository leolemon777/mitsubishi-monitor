using System.Collections.Generic;
using System.Linq;

namespace MitsubishiMonitor.Demo.Models
{
    /// <summary>
    /// M 点读取块定义（起始地址 + 数量）
    /// </summary>
    public class MReadBlock
    {
        public string StartAddress { get; set; }
        public ushort Count { get; set; }

        public MReadBlock(string startAddress, ushort count)
        {
            StartAddress = startAddress;
            Count = count;
        }
    }

    /// <summary>
    /// C 寄存器（计数器）定义
    /// </summary>
    public class CRegisterDef
    {
        public string Address { get; set; }
        public string Label { get; set; }
        public string Unit { get; set; }

        /// <summary>
        /// D 寄存器按 16 位读取（否则 D 默认按 32 位字读）
        /// </summary>
        public bool PreferInt16 { get; set; }

        public CRegisterDef(string address, string label, string unit = "")
        {
            Address = address;
            Label = label;
            Unit = unit;
        }
    }

    /// <summary>
    /// 工艺阶段定义（M 地址 → 阶段名 + 图标）
    /// </summary>
    public class ProcessStageDef
    {
        public string Address { get; set; }
        public string Name { get; set; }
        public string Icon { get; set; }

        public ProcessStageDef(string address, string name, string icon = "🔹")
        {
            Address = address;
            Name = name;
            Icon = icon;
        }
    }

    /// <summary>
    /// PLC连接配置
    /// </summary>
    public class PlcConfig
    {
        /// <summary>
        /// 设备名称
        /// </summary>
        public string Name { get; set; } = "FX3U-设备1";

        /// <summary>
        /// IP地址
        /// </summary>
        public string IpAddress { get; set; } = "192.168.1.10";

        /// <summary>
        /// 端口号
        /// </summary>
        public int Port { get; set; } = 5000;

        /// <summary>
        /// 连接超时时间(毫秒)。工控机启动时不能被离线 PLC 长时间拖住，默认控制在 3 秒。
        /// </summary>
        public int ConnectTimeout { get; set; } = 3000;

        /// <summary>
        /// X点起始地址 (A系列1E帧: X0, X1...)
        /// </summary>
        public string XStartAddress { get; set; } = "X0";

        /// <summary>
        /// X点数量 (X0-X11共12个)
        /// </summary>
        public int XCount { get; set; } = 12;

        /// <summary>
        /// Y点起始地址 (A系列1E帧: Y0, Y1...)
        /// </summary>
        public string YStartAddress { get; set; } = "Y0";

        /// <summary>
        /// Y点数量 (Y0-Y7 + Y10-Y17共16个，没有Y8/Y9/Y18/Y19)
        /// </summary>
        public int YCount { get; set; } = 16;

        /// <summary>
        /// 温度地址 D12 (浮点数，实际读取D12和D13)
        /// </summary>
        public string TemperatureAddress { get; set; } = "D12";

        /// <summary>
        /// 温度寄存器是否为16位Word（false=默认读32位DINT，true=只读单个16位D寄存器）
        /// 设备2/默认为 false（D12+D13 组成DINT）
        /// 设备3等用单个D寄存器存温度的设备设为 true
        /// </summary>
        public bool TemperatureIsWord { get; set; } = false;

        /// <summary>
        /// 热电偶A电压地址（空字符串表示该设备无此数据）
        /// </summary>
        public string ThermocoupleAAddress { get; set; } = "D17";

        /// <summary>
        /// 热电偶B电压地址（空字符串表示该设备无此数据）
        /// </summary>
        public string ThermocoupleBAddress { get; set; } = "D19";

        /// <summary>
        /// 热电偶C电压地址（空字符串表示该设备无此数据）
        /// </summary>
        public string ThermocoupleCAddress { get; set; } = "D21";

        /// <summary>
        /// 是否有热电偶电压数据
        /// </summary>
        public bool HasVoltage => !string.IsNullOrEmpty(ThermocoupleAAddress);

        /// <summary>
        /// 目标温度地址 D210 (从PLC读取，设定温度)
        /// </summary>
        public string TargetTemperatureAddress { get; set; } = "D210";

        /// <summary>
        /// 钉钉机器人Webhook地址
        /// </summary>
        public string DingTalkWebhook { get; set; } = "";

        /// <summary>
        /// M点起始地址 (辅助继电器) - 留作兼容，优先使用 MReadBlocks
        /// </summary>
        public string MStartAddress { get; set; } = "M2009";

        /// <summary>
        /// M点数量 - 留作兼容，实际由 MAddressList.Count 决定
        /// </summary>
        public int MCount { get; set; } = 10;

        /// <summary>
        /// M 点有序地址列表（索引 → 地址的完整映射）
        /// 如果为空（null），则使用旧的 MStartAddress/MCount 逻辑
        /// </summary>
        public List<string> MAddressList { get; set; }

        /// <summary>
        /// M 点批量读取块定义（用于优化非连续 M 点的 PLC 通信）
        /// 如果为空（null），则使用旧的硬编码读取逻辑
        /// </summary>
        public List<MReadBlock> MReadBlocks { get; set; }

        /// <summary>
        /// C 寄存器（计数器）定义列表
        /// 为空或 null 表示该设备没有 C 寄存器
        /// </summary>
        public List<CRegisterDef> CRegisters { get; set; }

        /// <summary>
        /// 是否有 C 寄存器数据
        /// </summary>
        public bool HasCRegisters => CRegisters != null && CRegisters.Count > 0;

        /// <summary>
        /// 工艺阶段定义列表（用于 UI 工艺流程面板）
        /// null 或空 = 不显示工艺流程面板
        /// </summary>
        public List<ProcessStageDef> ProcessStages { get; set; } = new()
        {
            new ProcessStageDef("M2009", "储存槽加水", "1"),
            new ProcessStageDef("M2010", "储存槽循环", "2"),
            new ProcessStageDef("M2011", "储存槽转反应槽", "3"),
            new ProcessStageDef("M2012", "循环加温", "4"),
            new ProcessStageDef("M2013", "反应槽转储存槽", "5"),
            new ProcessStageDef("M2014", "反应槽加水", "6"),
            new ProcessStageDef("M2015", "循环冲洗", "7"),
            new ProcessStageDef("M2016", "排水", "8"),
        };

        /// <summary>
        /// 是否有工艺阶段定义
        /// </summary>
        public bool HasProcessStages => ProcessStages != null && ProcessStages.Count > 0;

        /// <summary>
        /// 实际 M 点数量（优先用 MAddressList，否则用 MCount）
        /// </summary>
        public int ActualMCount => MAddressList?.Count ?? MCount;

        /// <summary>
        /// 温度采集间隔(毫秒)
        /// </summary>
        public int TemperatureInterval { get; set; } = 10000;

        /// <summary>
        /// XY点采集间隔(毫秒)
        /// </summary>
        public int XYInterval { get; set; } = 1000;

        /// <summary>
        /// 温度值除数（默认 10：PLC 存储 temp×10；若存 temp×100 则设为 100）
        /// </summary>
        public float TemperatureDivisor { get; set; } = 10f;

        /// <summary>
        /// 温度异常阈值
        /// </summary>
        public float TemperatureThreshold { get; set; } = 90f;

        /// <summary>
        /// X点标签映射（地址 -> 中文名称）
        /// </summary>
        public Dictionary<string, string> XPointLabels { get; set; } = new()
        {
            { "X0", "急停按钮" },
            { "X1", "启动按钮" },
            { "X2", "停止按钮" },
            { "X3", "反应槽低液位" },
            { "X4", "反应槽中液位" },
            { "X5", "反应槽高液位" },
            { "X6", "反应槽极限液位" },
            { "X7", "暂存槽低液位" },
            { "X10", "暂存槽高液位" },
            { "X11", "暂存槽极限液位" },
        };

        /// <summary>
        /// Y点标签映射（地址 -> 中文名称）
        /// </summary>
        public Dictionary<string, string> YPointLabels { get; set; } = new()
        {
            { "Y0", "水泵开启" },
            { "Y1", "反应槽进水" },
            { "Y2", "储存槽进水" },
            { "Y3", "反应槽进水循环" },
            { "Y4", "反应槽出水循环" },
            { "Y5", "储存槽进水循环" },
            { "Y6", "储存槽出水循环" },
            { "Y7", "储存槽排水" },
            { "Y14", "三色灯黄灯" },
            { "Y15", "三色灯绿灯" },
            { "Y16", "三色灯红灯" },
            { "Y17", "PID输出" },
        };

        /// <summary>
        /// M点标签映射（地址 -> 中文名称）
        /// </summary>
        public Dictionary<string, string> MPointLabels { get; set; } = new()
        {
            { "M2009", "储存槽加水" },
            { "M2010", "储存槽循环" },
            { "M2011", "储存槽转反应槽" },
            { "M2012", "循环加温" },
            { "M2013", "反应槽转储存槽" },
            { "M2014", "反应槽加水" },
            { "M2015", "循环冲洗" },
            { "M2016", "排水" },
            { "M2451", "自动" },
            { "M2452", "手动" },
        };

        /// <summary>
        /// 获取 X 点的地址字符串（三菱八进制：下标8→X10，9→X11，无X8/X9）
        /// </summary>
        public string GetXAddress(int index)
        {
            return "X" + System.Convert.ToString(index, 8);
        }

        /// <summary>
        /// 获取X点中文名称
        /// </summary>
        public string GetXLabel(int index)
        {
            var addr = GetXAddress(index);
            return XPointLabels.TryGetValue(addr, out var label) ? label : addr;
        }

        /// <summary>
        /// 获取 Y 点的地址字符串（三菱八进制）
        /// </summary>
        public string GetYAddress(int index)
        {
            return "Y" + System.Convert.ToString(index, 8);
        }

        /// <summary>
        /// 获取Y点中文名称
        /// </summary>
        public string GetYLabel(int index)
        {
            var addr = GetYAddress(index);
            return YPointLabels.TryGetValue(addr, out var label) ? label : addr;
        }

        /// <summary>
        /// 获取 M 点的地址字符串
        /// 优先使用 MAddressList（适配不连续地址），否则使用旧逻辑
        /// </summary>
        public string GetMAddress(int index)
        {
            if (MAddressList != null && index >= 0 && index < MAddressList.Count)
            {
                return MAddressList[index];
            }

            // 旧逻辑兼容
            if (index >= 0 && index <= 7)
                return $"M{2009 + index}";
            if (index == 8)
                return "M2451";
            if (index == 9)
                return "M2452";
            return $"M{index}";
        }

        /// <summary>
        /// 获取M点中文名称
        /// </summary>
        public string GetMLabel(int index)
        {
            var addr = GetMAddress(index);
            return MPointLabels.TryGetValue(addr, out var label) ? label : addr;
        }
    }
}
