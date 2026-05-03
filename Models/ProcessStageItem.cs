using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MitsubishiMonitor.Demo.Models
{
    /// <summary>
    /// 工艺阶段显示项（用于 UI 绑定，展示 M 点工艺流程状态）
    /// </summary>
    public class ProcessStageItem : INotifyPropertyChanged
    {
        private bool _isActive;

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// 阶段名称（如 "反应槽进水"）
        /// </summary>
        public string StageName { get; set; }

        /// <summary>
        /// 对应的 M 地址（如 "M110"）
        /// </summary>
        public string Address { get; set; }

        /// <summary>
        /// 是否正在激活：变化时通知绑定
        /// </summary>
        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive == value) return;
                _isActive = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ActiveColor));  // 依赖 IsActive
                OnPropertyChanged(nameof(StatusText));   // 依赖 IsActive
            }
        }

        /// <summary>
        /// 激活时的显示颜色
        /// </summary>
        public string ActiveColor => IsActive ? "#00FF88" : "#3D4450";

        /// <summary>
        /// 状态文本
        /// </summary>
        public string StatusText => IsActive ? "运行中" : "待机";

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
