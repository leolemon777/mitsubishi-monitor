using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MitsubishiMonitor.Demo.Controls
{
    /// <summary>
    /// ThresholdEditor.xaml 的交互逻辑
    /// 温度阈值编辑器用户控件
    /// </summary>
    public partial class ThresholdEditor : UserControl
    {
        public ThresholdEditor()
        {
            InitializeComponent();
        }

        #region 依赖属性

        /// <summary>
        /// 阈值
        /// </summary>
        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(nameof(Value), typeof(float), typeof(ThresholdEditor),
                new PropertyMetadata(50.0f));

        /// <summary>
        /// 是否处于编辑模式
        /// </summary>
        public static readonly DependencyProperty IsEditingProperty =
            DependencyProperty.Register(nameof(IsEditing), typeof(bool), typeof(ThresholdEditor),
                new PropertyMetadata(false));

        /// <summary>
        /// 描述文本
        /// </summary>
        public static readonly DependencyProperty DescriptionProperty =
            DependencyProperty.Register(nameof(Description), typeof(string), typeof(ThresholdEditor),
                new PropertyMetadata("超过此值自动标记异常"));

        /// <summary>
        /// 编辑命令
        /// </summary>
        public static readonly DependencyProperty EditCommandProperty =
            DependencyProperty.Register(nameof(EditCommand), typeof(ICommand), typeof(ThresholdEditor),
                new PropertyMetadata(null));

        /// <summary>
        /// 保存命令
        /// </summary>
        public static readonly DependencyProperty SaveCommandProperty =
            DependencyProperty.Register(nameof(SaveCommand), typeof(ICommand), typeof(ThresholdEditor),
                new PropertyMetadata(null));

        /// <summary>
        /// 取消命令
        /// </summary>
        public static readonly DependencyProperty CancelCommandProperty =
            DependencyProperty.Register(nameof(CancelCommand), typeof(ICommand), typeof(ThresholdEditor),
                new PropertyMetadata(null));

        #endregion

        #region 属性

        public float Value
        {
            get => (float)GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public bool IsEditing
        {
            get => (bool)GetValue(IsEditingProperty);
            set => SetValue(IsEditingProperty, value);
        }

        public string Description
        {
            get => (string)GetValue(DescriptionProperty);
            set => SetValue(DescriptionProperty, value);
        }

        public ICommand EditCommand
        {
            get => (ICommand)GetValue(EditCommandProperty);
            set => SetValue(EditCommandProperty, value);
        }

        public ICommand SaveCommand
        {
            get => (ICommand)GetValue(SaveCommandProperty);
            set => SetValue(SaveCommandProperty, value);
        }

        public ICommand CancelCommand
        {
            get => (ICommand)GetValue(CancelCommandProperty);
            set => SetValue(CancelCommandProperty, value);
        }

        #endregion

        #region 输入验证

        /// <summary>
        /// 输入验证：只允许数字和小数点
        /// </summary>
        private void ValueTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            // 只允许数字和小数点
            Regex regex = new Regex("[^0-9.]+");
            e.Handled = regex.IsMatch(e.Text);

            // 防止输入多个小数点
            if (e.Text == "." && ((TextBox)sender).Text.Contains("."))
            {
                e.Handled = true;
            }
        }

        #endregion
    }
}
