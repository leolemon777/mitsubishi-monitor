using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MitsubishiMonitor.Demo.Models
{
    /// <summary>
    /// C 寄存器显示项（用于 UI 绑定）
    /// </summary>
    public class CRegisterDisplayItem : INotifyPropertyChanged
    {
        private int _value;

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// 显示标签（如 "反应槽循环时间"）
        /// </summary>
        public string Label { get; set; }

        /// <summary>
        /// 当前值：变化时通知绑定
        /// </summary>
        public int Value
        {
            get => _value;
            set
            {
                if (_value == value) return;
                _value = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayValue)); // DisplayValue 依赖 Value
            }
        }

        /// <summary>
        /// 单位（如 "分钟"、"小时"）
        /// </summary>
        public string Unit { get; set; }

        /// <summary>
        /// PLC 地址（如 "C10"）
        /// </summary>
        public string Address { get; set; }

        /// <summary>
        /// 格式化的显示值
        /// </summary>
        public string DisplayValue => $"{Value} {Unit}";

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
