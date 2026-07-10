using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Linq;

namespace MitsubishiMonitor.Demo.Models
{
    /// <summary>
    /// PLC状态模型
    /// </summary>
    public class PlcStatus : INotifyPropertyChanged
    {
        private bool[] _x;
        private bool[] _y;
        private bool[] _m;
        private float _temperature;
        private float _targetTemperature;
        private float _thermocoupleA;
        private float _thermocoupleB;
        private float _thermocoupleC;
        private bool _isConnected;
        private DateTime _lastUpdateTime;
        private DateTime _lastTemperatureSampleTime;
        private bool _isAlarm;
        private bool _isSsrFault;
        private Dictionary<string, int> _cValues = new();

        /// <summary>
        /// 默认构造函数（兼容旧代码）
        /// </summary>
        public PlcStatus() : this(12, 16, 10) { }

        /// <summary>
        /// 根据配置创建指定大小的状态模型
        /// </summary>
        public PlcStatus(int xCount, int yCount, int mCount)
        {
            _x = new bool[xCount];
            _y = new bool[yCount];
            _m = new bool[mCount];
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// X输入点状态 X0-X11 (12个)
        /// </summary>
        public bool[] X
        {
            get => _x;
            set
            {
                // 内容未变则跳过所有通知，避免每秒向 Dispatcher 队列灌入无用消息
                if (value != null && _x != null && value.Length == _x.Length && value.SequenceEqual(_x))
                    return;
                _x = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(X0));
                OnPropertyChanged(nameof(X1));
                OnPropertyChanged(nameof(X2));
                OnPropertyChanged(nameof(X3));
                OnPropertyChanged(nameof(X4));
                OnPropertyChanged(nameof(X5));
                OnPropertyChanged(nameof(X6));
                OnPropertyChanged(nameof(X7));
                OnPropertyChanged(nameof(X10));
                OnPropertyChanged(nameof(X11));
            }
        }

        // X点独立属性用于数据绑定
        public bool X0 { get => _x.Length > 0 ? _x[0] : false; }  // 启动按钮
        public bool X1 { get => _x.Length > 1 ? _x[1] : false; }  // 停止按钮
        public bool X2 { get => _x.Length > 2 ? _x[2] : false; }  // 复位按钮
        public bool X3 { get => _x.Length > 3 ? _x[3] : false; }  // 反应槽下限位
        public bool X4 { get => _x.Length > 4 ? _x[4] : false; }  // 反应槽上限位
        public bool X5 { get => _x.Length > 5 ? _x[5] : false; }  // 反应槽下限位
        public bool X6 { get => _x.Length > 6 ? _x[6] : false; }  // 反应槽上限位
        public bool X7 { get => _x.Length > 7 ? _x[7] : false; }  // 储存槽下限位
        public bool X10 { get => _x.Length > 8 ? _x[8] : false; } // 储存槽上限位
        public bool X11 { get => _x.Length > 9 ? _x[9] : false; } // 储存槽上限位

        /// <summary>
        /// Y输出点状态 Y0-Y17 (18个)
        /// </summary>
        public bool[] Y
        {
            get => _y;
            set
            {
                // 内容未变则跳过所有通知
                if (value != null && _y != null && value.Length == _y.Length && value.SequenceEqual(_y))
                    return;
                _y = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Y0));
                OnPropertyChanged(nameof(Y1));
                OnPropertyChanged(nameof(Y2));
                OnPropertyChanged(nameof(Y3));
                OnPropertyChanged(nameof(Y4));
                OnPropertyChanged(nameof(Y5));
                OnPropertyChanged(nameof(Y6));
                OnPropertyChanged(nameof(Y7));
                OnPropertyChanged(nameof(Y14));
                OnPropertyChanged(nameof(Y15));
                OnPropertyChanged(nameof(Y16));
                OnPropertyChanged(nameof(Y17));
            }
        }

        // Y点独立属性用于数据绑定
        // 注意：三菱 Y 点为八进制，下标 0-7 对应 Y0-Y7，下标 8-15 对应 Y10-Y17（无 Y8/Y9）。
        // 因此 Y10 在数组下标 8、Y14 在 12、Y17 在 15。
        public bool Y0 { get => _y.Length > 0 ? _y[0] : false; }   // 水泵开启
        public bool Y1 { get => _y.Length > 1 ? _y[1] : false; }   // 反应槽进水
        public bool Y2 { get => _y.Length > 2 ? _y[2] : false; }   // 反应槽进水
        public bool Y3 { get => _y.Length > 3 ? _y[3] : false; }   // 反应槽进水循环
        public bool Y4 { get => _y.Length > 4 ? _y[4] : false; }   // 反应槽出水循环
        public bool Y5 { get => _y.Length > 5 ? _y[5] : false; }   // 储存槽进水循环
        public bool Y6 { get => _y.Length > 6 ? _y[6] : false; }   // 储存槽出水循环
        public bool Y7 { get => _y.Length > 7 ? _y[7] : false; }   // 储存槽进水
        public bool Y14 { get => _y.Length > 12 ? _y[12] : false; } // 黄灯（Y14=数组下标12）
        public bool Y15 { get => _y.Length > 13 ? _y[13] : false; } // 绿灯（Y15=数组下标13）
        public bool Y16 { get => _y.Length > 14 ? _y[14] : false; } // 红灯（Y16=数组下标14）
        public bool Y17 { get => _y.Length > 15 ? _y[15] : false; } // PID控制（Y17=数组下标15）

        /// <summary>
        /// M辅助继电器状态 M2009-M2016 + M2451-M2452 (10个)
        /// </summary>
        public bool[] M
        {
            get => _m;
            set
            {
                // 内容未变则跳过所有通知
                if (value != null && _m != null && value.Length == _m.Length && value.SequenceEqual(_m))
                    return;
                _m = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(M2009_AddWater));
                OnPropertyChanged(nameof(M2010_Cycle));
                OnPropertyChanged(nameof(M2011_StoreToReact));
                OnPropertyChanged(nameof(M2012_Heat));
                OnPropertyChanged(nameof(M2013_ReactToStore));
                OnPropertyChanged(nameof(M2014_ReactAddWater));
                OnPropertyChanged(nameof(M2015_Wash));
                OnPropertyChanged(nameof(M2016_Drain));
                OnPropertyChanged(nameof(M2451_Auto));
                OnPropertyChanged(nameof(M2452_Manual));
            }
        }

        // M点独立属性用于数据绑定
        public bool M2009_AddWater { get => _m.Length > 0 ? _m[0] : false; }   // 储存槽加水
        public bool M2010_Cycle { get => _m.Length > 1 ? _m[1] : false; }       // 储存槽循环
        public bool M2011_StoreToReact { get => _m.Length > 2 ? _m[2] : false; } // 储存槽转反应槽
        public bool M2012_Heat { get => _m.Length > 3 ? _m[3] : false; }        // 循环加温
        public bool M2013_ReactToStore { get => _m.Length > 4 ? _m[4] : false; } // 反应槽转储存槽
        public bool M2014_ReactAddWater { get => _m.Length > 5 ? _m[5] : false; } // 反应槽加水
        public bool M2015_Wash { get => _m.Length > 6 ? _m[6] : false; }        // 循环冲洗
        public bool M2016_Drain { get => _m.Length > 7 ? _m[7] : false; }       // 排水
        public bool M2451_Auto { get => _m.Length > 8 ? _m[8] : false; }        // 自动
        public bool M2452_Manual { get => _m.Length > 9 ? _m[9] : false; }       // 手动

        /// <summary>
        /// 温度值 (D12浮点数)
        /// </summary>
        public float Temperature
        {
            get => _temperature;
            set
            {
                _temperature = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 目标温度 (D210，从PLC读取)
        /// </summary>
        public float TargetTemperature
        {
            get => _targetTemperature;
            set
            {
                _targetTemperature = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 是否报警 (温度超过目标温度)
        /// </summary>
        public bool IsAlarm
        {
            get => _isAlarm;
            set
            {
                _isAlarm = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 固态继电器故障
        /// </summary>
        public bool IsSsrFault
        {
            get => _isSsrFault;
            set
            {
                _isSsrFault = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 热电偶A电压 (D17)
        /// </summary>
        public float ThermocoupleA
        {
            get => _thermocoupleA;
            set
            {
                _thermocoupleA = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 热电偶B电压 (D19)
        /// </summary>
        public float ThermocoupleB
        {
            get => _thermocoupleB;
            set
            {
                _thermocoupleB = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 热电偶C电压 (D21)
        /// </summary>
        public float ThermocoupleC
        {
            get => _thermocoupleC;
            set
            {
                _thermocoupleC = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 是否连接
        /// </summary>
        public bool IsConnected
        {
            get => _isConnected;
            set
            {
                _isConnected = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 最后更新时间
        /// </summary>
        public DateTime LastUpdateTime
        {
            get => _lastUpdateTime;
            set
            {
                _lastUpdateTime = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 最后一次成功完成温度采样的时间。
        /// LastUpdateTime 会被 X/Y/M 轮询刷新，不能用来判断温度是否仍在更新。
        /// </summary>
        public DateTime LastTemperatureSampleTime
        {
            get => _lastTemperatureSampleTime;
            set
            {
                _lastTemperatureSampleTime = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// C 寄存器（计数器）当前值（地址 → 值）
        /// </summary>
        public Dictionary<string, int> CValues
        {
            get => _cValues;
            set
            {
                _cValues = value;
                OnPropertyChanged();
            }
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// 获取X点的当前值
        /// </summary>
        public bool GetX(int index)
        {
            return index >= 0 && index < _x.Length ? _x[index] : false;
        }

        /// <summary>
        /// 设置X点的值
        /// </summary>
        public void SetX(int index, bool value)
        {
            if (index >= 0 && index < _x.Length)
            {
                _x[index] = value;
                OnPropertyChanged(nameof(X));
            }
        }

        /// <summary>
        /// 获取Y点的当前值
        /// </summary>
        public bool GetY(int index)
        {
            return index >= 0 && index < _y.Length ? _y[index] : false;
        }

        /// <summary>
        /// 设置Y点的值
        /// </summary>
        public void SetY(int index, bool value)
        {
            if (index >= 0 && index < _y.Length)
            {
                _y[index] = value;
                OnPropertyChanged(nameof(Y));
            }
        }

        /// <summary>
        /// 获取M点的当前值
        /// </summary>
        public bool GetM(int index)
        {
            return index >= 0 && index < _m.Length ? _m[index] : false;
        }

        /// <summary>
        /// 设置M点的值
        /// </summary>
        public void SetM(int index, bool value)
        {
            if (index >= 0 && index < _m.Length)
            {
                _m[index] = value;
                OnPropertyChanged(nameof(M));
            }
        }
    }
}
