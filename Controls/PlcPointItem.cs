using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MitsubishiMonitor.Demo.Controls
{
    /// <summary>
    /// PLC 单个点位显示控件
    /// 用于显示单个 X/Y/M 点位的状态
    /// </summary>
    public class PlcPointItem : Control
    {
        static PlcPointItem()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(PlcPointItem),
                new FrameworkPropertyMetadata(typeof(PlcPointItem)));
        }

        #region 依赖属性

        /// <summary>
        /// 点位地址（如 X0, Y15, M2553）
        /// </summary>
        public static readonly DependencyProperty AddressProperty =
            DependencyProperty.Register(nameof(Address), typeof(string), typeof(PlcPointItem),
                new PropertyMetadata(string.Empty));

        /// <summary>
        /// 点位标签（中文名称）
        /// </summary>
        public static readonly DependencyProperty LabelProperty =
            DependencyProperty.Register(nameof(Label), typeof(string), typeof(PlcPointItem),
                new PropertyMetadata(string.Empty));

        /// <summary>
        /// 点位状态（ON/OFF）
        /// </summary>
        public static readonly DependencyProperty IsOnProperty =
            DependencyProperty.Register(nameof(IsOn), typeof(bool), typeof(PlcPointItem),
                new PropertyMetadata(false, OnIsOnChanged));

        /// <summary>
        /// 点位类型（X/Y/M）
        /// </summary>
        public static readonly DependencyProperty PointTypeProperty =
            DependencyProperty.Register(nameof(PointType), typeof(string), typeof(PlcPointItem),
                new PropertyMetadata("X"));

        /// <summary>
        /// ON 状态颜色
        /// </summary>
        public static readonly DependencyProperty OnColorProperty =
            DependencyProperty.Register(nameof(OnColor), typeof(Brush), typeof(PlcPointItem),
                new PropertyMetadata(new SolidColorBrush(Color.FromRgb(76, 175, 80))));

        /// <summary>
        /// OFF 状态颜色
        /// </summary>
        public static readonly DependencyProperty OffColorProperty =
            DependencyProperty.Register(nameof(OffColor), typeof(Brush), typeof(PlcPointItem),
                new PropertyMetadata(new SolidColorBrush(Color.FromRgb(66, 66, 66))));

        #endregion

        #region 属性

        public string Address
        {
            get => (string)GetValue(AddressProperty);
            set => SetValue(AddressProperty, value);
        }

        public string Label
        {
            get => (string)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        public bool IsOn
        {
            get => (bool)GetValue(IsOnProperty);
            set => SetValue(IsOnProperty, value);
        }

        public string PointType
        {
            get => (string)GetValue(PointTypeProperty);
            set => SetValue(PointTypeProperty, value);
        }

        public Brush OnColor
        {
            get => (Brush)GetValue(OnColorProperty);
            set => SetValue(OnColorProperty, value);
        }

        public Brush OffColor
        {
            get => (Brush)GetValue(OffColorProperty);
            set => SetValue(OffColorProperty, value);
        }

        #endregion

        #region 事件处理

        private static void OnIsOnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PlcPointItem item)
            {
                item.UpdateVisualState();
            }
        }

        #endregion

        #region 方法

        private void UpdateVisualState()
        {
            VisualStateManager.GoToState(this, IsOn ? "OnState" : "OffState", true);
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            UpdateVisualState();
            UpdateToolTip();
        }

        private void UpdateToolTip()
        {
            var status = IsOn ? "ON" : "OFF";
            var tooltip = string.IsNullOrEmpty(Label) 
                ? $"{Address}: {status}" 
                : $"{Label} ({Address}): {status}";
            ToolTip = tooltip;
        }

        #endregion
    }
}
