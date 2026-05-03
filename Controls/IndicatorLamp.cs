using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MitsubishiMonitor.Demo.Controls
{
    /// <summary>
    /// 状态指示灯控件（增强版）
    /// 支持闪烁动画、工具提示、渐变色等功能
    /// </summary>
    public class IndicatorLamp : Control
    {
        static IndicatorLamp()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(IndicatorLamp),
                new FrameworkPropertyMetadata(typeof(IndicatorLamp)));
        }

        #region 依赖属性

        public static readonly DependencyProperty IsOnProperty =
            DependencyProperty.Register(nameof(IsOn), typeof(bool), typeof(IndicatorLamp),
                new PropertyMetadata(false, OnIsOnChanged));

        public static readonly DependencyProperty OnColorProperty =
            DependencyProperty.Register(nameof(OnColor), typeof(Color), typeof(IndicatorLamp),
                new PropertyMetadata(Color.FromRgb(0, 200, 83)));

        public static readonly DependencyProperty OffColorProperty =
            DependencyProperty.Register(nameof(OffColor), typeof(Color), typeof(IndicatorLamp),
                new PropertyMetadata(Color.FromRgb(66, 66, 66)));

        public static readonly DependencyProperty LabelProperty =
            DependencyProperty.Register(nameof(Label), typeof(string), typeof(IndicatorLamp),
                new PropertyMetadata(string.Empty));

        /// <summary>
        /// 是否处于警告状态（闪烁）
        /// </summary>
        public static readonly DependencyProperty IsWarningProperty =
            DependencyProperty.Register(nameof(IsWarning), typeof(bool), typeof(IndicatorLamp),
                new PropertyMetadata(false, OnIsWarningChanged));

        /// <summary>
        /// 警告颜色
        /// </summary>
        public static readonly DependencyProperty WarningColorProperty =
            DependencyProperty.Register(nameof(WarningColor), typeof(Color), typeof(IndicatorLamp),
                new PropertyMetadata(Color.FromRgb(255, 152, 0)));

        /// <summary>
        /// 工具提示文本
        /// </summary>
        public static readonly DependencyProperty StatusTextProperty =
            DependencyProperty.Register(nameof(StatusText), typeof(string), typeof(IndicatorLamp),
                new PropertyMetadata(string.Empty));

        /// <summary>
        /// 是否使用渐变色
        /// </summary>
        public static readonly DependencyProperty UseGradientProperty =
            DependencyProperty.Register(nameof(UseGradient), typeof(bool), typeof(IndicatorLamp),
                new PropertyMetadata(false));

        /// <summary>
        /// 指示灯大小
        /// </summary>
        public static readonly DependencyProperty LampSizeProperty =
            DependencyProperty.Register(nameof(LampSize), typeof(double), typeof(IndicatorLamp),
                new PropertyMetadata(50.0));

        #endregion

        #region 属性

        public bool IsOn
        {
            get => (bool)GetValue(IsOnProperty);
            set => SetValue(IsOnProperty, value);
        }

        public Color OnColor
        {
            get => (Color)GetValue(OnColorProperty);
            set => SetValue(OnColorProperty, value);
        }

        public Color OffColor
        {
            get => (Color)GetValue(OffColorProperty);
            set => SetValue(OffColorProperty, value);
        }

        public string Label
        {
            get => (string)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        public bool IsWarning
        {
            get => (bool)GetValue(IsWarningProperty);
            set => SetValue(IsWarningProperty, value);
        }

        public Color WarningColor
        {
            get => (Color)GetValue(WarningColorProperty);
            set => SetValue(WarningColorProperty, value);
        }

        public string StatusText
        {
            get => (string)GetValue(StatusTextProperty);
            set => SetValue(StatusTextProperty, value);
        }

        public bool UseGradient
        {
            get => (bool)GetValue(UseGradientProperty);
            set => SetValue(UseGradientProperty, value);
        }

        public double LampSize
        {
            get => (double)GetValue(LampSizeProperty);
            set => SetValue(LampSizeProperty, value);
        }

        #endregion

        #region 事件处理

        private static void OnIsOnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is IndicatorLamp lamp)
            {
                lamp.UpdateVisualState();
            }
        }

        private static void OnIsWarningChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is IndicatorLamp lamp)
            {
                lamp.UpdateVisualState();
            }
        }

        #endregion

        #region 方法

        private void UpdateVisualState()
        {
            if (IsWarning)
            {
                VisualStateManager.GoToState(this, "WarningState", true);
            }
            else if (IsOn)
            {
                VisualStateManager.GoToState(this, "OnState", true);
            }
            else
            {
                VisualStateManager.GoToState(this, "OffState", true);
            }
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            UpdateVisualState();
            
            // 设置工具提示
            UpdateToolTip();
        }

        private void UpdateToolTip()
        {
            if (!string.IsNullOrEmpty(StatusText))
            {
                ToolTip = StatusText;
            }
            else if (!string.IsNullOrEmpty(Label))
            {
                var status = IsWarning ? "警告" : (IsOn ? "开启" : "关闭");
                ToolTip = $"{Label}: {status}";
            }
        }

        #endregion
    }
}
