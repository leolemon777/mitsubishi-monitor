using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace MitsubishiMonitor.Demo.Controls
{
    /// <summary>
    /// 诊断信息卡片：状态图标、效率进度条、趋势箭头
    /// </summary>
    public partial class DiagnosticCard : System.Windows.Controls.UserControl
    {
        public DiagnosticCard()
        {
            InitializeComponent();
        }

        #region 依赖属性

        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(nameof(Title), typeof(string), typeof(DiagnosticCard), new PropertyMetadata("诊断"));

        public static readonly DependencyProperty StatusTextProperty =
            DependencyProperty.Register(nameof(StatusText), typeof(string), typeof(DiagnosticCard), new PropertyMetadata("--"));

        public static readonly DependencyProperty StatusIconProperty =
            DependencyProperty.Register(nameof(StatusIcon), typeof(string), typeof(DiagnosticCard),
                new PropertyMetadata("•", OnStatusIconChanged));

        public static readonly DependencyProperty StatusIconColorProperty =
            DependencyProperty.Register(nameof(StatusIconColor), typeof(Brush), typeof(DiagnosticCard),
                new PropertyMetadata(new SolidColorBrush(Color.FromRgb(158, 158, 158))));

        public static readonly DependencyProperty EfficiencyValueProperty =
            DependencyProperty.Register(nameof(EfficiencyValue), typeof(double), typeof(DiagnosticCard),
                new PropertyMetadata(0.0, OnEfficiencyChanged));

        public static readonly DependencyProperty EfficiencyColorProperty =
            DependencyProperty.Register(nameof(EfficiencyColor), typeof(Brush), typeof(DiagnosticCard),
                new PropertyMetadata(new SolidColorBrush(Color.FromRgb(76, 175, 80))));

        public static readonly DependencyProperty TrendArrowProperty =
            DependencyProperty.Register(nameof(TrendArrow), typeof(string), typeof(DiagnosticCard),
                new PropertyMetadata("→", OnTrendChanged));

        public static readonly DependencyProperty TrendColorProperty =
            DependencyProperty.Register(nameof(TrendColor), typeof(Brush), typeof(DiagnosticCard),
                new PropertyMetadata(new SolidColorBrush(Color.FromRgb(125, 133, 144))));

        public static readonly DependencyProperty ShowDetailsCommandProperty =
            DependencyProperty.Register(nameof(ShowDetailsCommand), typeof(ICommand), typeof(DiagnosticCard), new PropertyMetadata(null));

        #endregion

        #region 属性

        public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
        public string StatusText { get => (string)GetValue(StatusTextProperty); set => SetValue(StatusTextProperty, value); }
        public string StatusIcon { get => (string)GetValue(StatusIconProperty); set => SetValue(StatusIconProperty, value); }
        public Brush StatusIconColor { get => (Brush)GetValue(StatusIconColorProperty); set => SetValue(StatusIconColorProperty, value); }
        public double EfficiencyValue { get => (double)GetValue(EfficiencyValueProperty); set => SetValue(EfficiencyValueProperty, value); }
        public Brush EfficiencyColor { get => (Brush)GetValue(EfficiencyColorProperty); set => SetValue(EfficiencyColorProperty, value); }
        public string TrendArrow { get => (string)GetValue(TrendArrowProperty); set => SetValue(TrendArrowProperty, value); }
        public Brush TrendColor { get => (Brush)GetValue(TrendColorProperty); set => SetValue(TrendColorProperty, value); }
        public ICommand ShowDetailsCommand { get => (ICommand)GetValue(ShowDetailsCommandProperty); set => SetValue(ShowDetailsCommandProperty, value); }

        #endregion

        private static void OnStatusIconChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is DiagnosticCard card)
            {
                card.StatusIconColor = (string)e.NewValue switch
                {
                    "✓" => new SolidColorBrush(Color.FromRgb(63, 185, 80)),
                    "⚠" => new SolidColorBrush(Color.FromRgb(210, 153, 34)),
                    "✗" => new SolidColorBrush(Color.FromRgb(248, 81, 73)),
                    _ => new SolidColorBrush(Color.FromRgb(125, 133, 144))
                };
            }
        }

        private static void OnEfficiencyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is DiagnosticCard card)
            {
                var val = (double)e.NewValue;
                card.EfficiencyColor = val >= 80
                    ? new SolidColorBrush(Color.FromRgb(63, 185, 80))
                    : val >= 50
                        ? new SolidColorBrush(Color.FromRgb(210, 153, 34))
                        : new SolidColorBrush(Color.FromRgb(248, 81, 73));
            }
        }

        private static void OnTrendChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is DiagnosticCard card)
            {
                card.TrendColor = (string)e.NewValue switch
                {
                    "↑" => new SolidColorBrush(Color.FromRgb(248, 81, 73)),
                    "↓" => new SolidColorBrush(Color.FromRgb(63, 185, 80)),
                    _ => new SolidColorBrush(Color.FromRgb(125, 133, 144))
                };
            }
        }
    }
}
