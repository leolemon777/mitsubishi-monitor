using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MitsubishiMonitor.Demo.Controls
{
    /// <summary>
    /// DeviceCard.xaml 的交互逻辑
    /// 设备卡片用户控件
    /// </summary>
    public partial class DeviceCard : UserControl
    {
        private bool _cardMouseOver;
        private bool _cardPressed;

        public DeviceCard()
        {
            InitializeComponent();

            RootBorder.MouseEnter += (_, _) =>
            {
                _cardMouseOver = true;
                UpdateCardChrome();
            };
            RootBorder.MouseLeave += (_, _) =>
            {
                _cardMouseOver = false;
                _cardPressed = false;
                UpdateCardChrome();
            };
            RootBorder.PreviewMouseLeftButtonDown += OnRootPreviewMouseLeftButtonDown;
            RootBorder.MouseLeftButtonUp += (_, _) =>
            {
                _cardPressed = false;
                UpdateCardChrome();
            };

            // 点击卡片触发详情命令
            RootBorder.MouseLeftButtonDown += OnCardClick;
        }

        /// <summary>
        /// 悬停：青框 + 光晕 + 轻微放大；按下：略缩小反馈。不与「连接/断开」按钮抢交互。
        /// </summary>
        private void UpdateCardChrome()
        {
            bool isPlaceholder = DataContext is Models.Device { IsPlaceholder: true };

            const double scaleHover = 1.022;
            const double scalePress = 0.985;
            double s = 1.0;
            if (_cardMouseOver)
                s = _cardPressed ? scalePress : scaleHover;
            CardScale.ScaleX = CardScale.ScaleY = s;

            if (!_cardMouseOver)
            {
                if (isPlaceholder)
                {
                    RootBorder.ClearValue(Border.BorderBrushProperty);
                    CardShadow.Color = Colors.Black;
                    CardShadow.Opacity = 0.15;
                    CardShadow.BlurRadius = 6;
                    CardShadow.ShadowDepth = 1;
                }
                else
                {
                    RootBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x2D, 0x3D));
                    CardShadow.Color = Colors.Black;
                    CardShadow.Opacity = 0.3;
                    CardShadow.BlurRadius = 10;
                    CardShadow.ShadowDepth = 2;
                }
            }
            else if (_cardPressed)
            {
                if (isPlaceholder)
                {
                    RootBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x4A, 0x5A));
                    CardShadow.Color = Color.FromRgb(0x3A, 0x4A, 0x5A);
                    CardShadow.Opacity = 0.15;
                    CardShadow.BlurRadius = 10;
                    CardShadow.ShadowDepth = 0;
                }
                else
                {
                    RootBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 0xB4, 0xE6));
                    CardShadow.Color = Color.FromRgb(0, 0xD4, 0xFF);
                    CardShadow.Opacity = 0.28;
                    CardShadow.BlurRadius = 16;
                    CardShadow.ShadowDepth = 0;
                }
            }
            else
            {
                if (isPlaceholder)
                {
                    RootBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x5A, 0x6A));
                    CardShadow.Color = Color.FromRgb(0x4A, 0x5A, 0x6A);
                    CardShadow.Opacity = 0.25;
                    CardShadow.BlurRadius = 12;
                    CardShadow.ShadowDepth = 0;
                }
                else
                {
                    RootBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 0xD4, 0xFF));
                    CardShadow.Color = Color.FromRgb(0, 0xD4, 0xFF);
                    CardShadow.Opacity = 0.38;
                    CardShadow.BlurRadius = 22;
                    CardShadow.ShadowDepth = 0;
                }
            }
        }

        private void OnRootPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject src) return;
            if (src is FrameworkElement fe && FindParent<Button>(fe) != null)
                return;
            _cardPressed = true;
            UpdateCardChrome();
        }

        #region 依赖属性

        /// <summary>
        /// 设备名称
        /// </summary>
        public static readonly DependencyProperty DeviceNameProperty =
            DependencyProperty.Register(nameof(DeviceName), typeof(string), typeof(DeviceCard),
                new PropertyMetadata(string.Empty));

        /// <summary>
        /// 位置/产线说明（主列表即展示，无需进详情）
        /// </summary>
        public static readonly DependencyProperty LocationProperty =
            DependencyProperty.Register(nameof(Location), typeof(string), typeof(DeviceCard),
                new PropertyMetadata(string.Empty));

        /// <summary>
        /// 是否在线
        /// </summary>
        public static readonly DependencyProperty IsOnlineProperty =
            DependencyProperty.Register(nameof(IsOnline), typeof(bool), typeof(DeviceCard),
                new PropertyMetadata(false));

        /// <summary>
        /// 状态显示文本
        /// </summary>
        public static readonly DependencyProperty StatusDisplayProperty =
            DependencyProperty.Register(nameof(StatusDisplay), typeof(string), typeof(DeviceCard),
                new PropertyMetadata("离线"));

        /// <summary>
        /// 温度显示
        /// </summary>
        public static readonly DependencyProperty TemperatureDisplayProperty =
            DependencyProperty.Register(nameof(TemperatureDisplay), typeof(string), typeof(DeviceCard),
                new PropertyMetadata("--.-°C"));

        /// <summary>
        /// 今日操作次数
        /// </summary>
        public static readonly DependencyProperty TodayOperationCountProperty =
            DependencyProperty.Register(nameof(TodayOperationCount), typeof(int), typeof(DeviceCard),
                new PropertyMetadata(0));

        /// <summary>
        /// IP地址
        /// </summary>
        public static readonly DependencyProperty IpAddressProperty =
            DependencyProperty.Register(nameof(IpAddress), typeof(string), typeof(DeviceCard),
                new PropertyMetadata("---.---.---.---"));

        /// <summary>
        /// 最后更新时间
        /// </summary>
        public static readonly DependencyProperty LastUpdateTimeProperty =
            DependencyProperty.Register(nameof(LastUpdateTime), typeof(System.DateTime), typeof(DeviceCard),
                new PropertyMetadata(System.DateTime.Now));

        /// <summary>
        /// 是否有警告
        /// </summary>
        public static readonly DependencyProperty HasAlertProperty =
            DependencyProperty.Register(nameof(HasAlert), typeof(bool), typeof(DeviceCard),
                new PropertyMetadata(false));

        /// <summary>
        /// 连接命令
        /// </summary>
        public static readonly DependencyProperty ConnectCommandProperty =
            DependencyProperty.Register(nameof(ConnectCommand), typeof(ICommand), typeof(DeviceCard),
                new PropertyMetadata(null));

        /// <summary>
        /// 断开命令
        /// </summary>
        public static readonly DependencyProperty DisconnectCommandProperty =
            DependencyProperty.Register(nameof(DisconnectCommand), typeof(ICommand), typeof(DeviceCard),
                new PropertyMetadata(null));

        /// <summary>
        /// 详情命令
        /// </summary>
        public static readonly DependencyProperty DetailCommandProperty =
            DependencyProperty.Register(nameof(DetailCommand), typeof(ICommand), typeof(DeviceCard),
                new PropertyMetadata(null));

        #endregion

        #region 属性

        public string DeviceName
        {
            get => (string)GetValue(DeviceNameProperty);
            set => SetValue(DeviceNameProperty, value);
        }

        public string Location
        {
            get => (string)GetValue(LocationProperty);
            set => SetValue(LocationProperty, value);
        }

        public bool IsOnline
        {
            get => (bool)GetValue(IsOnlineProperty);
            set => SetValue(IsOnlineProperty, value);
        }

        public string StatusDisplay
        {
            get => (string)GetValue(StatusDisplayProperty);
            set => SetValue(StatusDisplayProperty, value);
        }

        public string TemperatureDisplay
        {
            get => (string)GetValue(TemperatureDisplayProperty);
            set => SetValue(TemperatureDisplayProperty, value);
        }

        public int TodayOperationCount
        {
            get => (int)GetValue(TodayOperationCountProperty);
            set => SetValue(TodayOperationCountProperty, value);
        }

        public string IpAddress
        {
            get => (string)GetValue(IpAddressProperty);
            set => SetValue(IpAddressProperty, value);
        }

        public System.DateTime LastUpdateTime
        {
            get => (System.DateTime)GetValue(LastUpdateTimeProperty);
            set => SetValue(LastUpdateTimeProperty, value);
        }

        public bool HasAlert
        {
            get => (bool)GetValue(HasAlertProperty);
            set => SetValue(HasAlertProperty, value);
        }

        public ICommand ConnectCommand
        {
            get => (ICommand)GetValue(ConnectCommandProperty);
            set => SetValue(ConnectCommandProperty, value);
        }

        public ICommand DisconnectCommand
        {
            get => (ICommand)GetValue(DisconnectCommandProperty);
            set => SetValue(DisconnectCommandProperty, value);
        }

        public ICommand DetailCommand
        {
            get => (ICommand)GetValue(DetailCommandProperty);
            set => SetValue(DetailCommandProperty, value);
        }

        #endregion

        #region 事件处理

        private void OnCardClick(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is Models.Device device && device.IsPlaceholder) return;

            // 如果点击的是按钮,不触发详情命令
            if (e.OriginalSource is FrameworkElement element)
            {
                // 检查是否点击在按钮上
                var button = FindParent<Button>(element);
                if (button != null)
                {
                    return;
                }
            }

            // 触发详情命令
            if (DetailCommand != null && DetailCommand.CanExecute(DataContext))
            {
                DetailCommand.Execute(DataContext);
            }
        }

        /// <summary>
        /// 查找父级元素
        /// </summary>
        private T FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            var parent = System.Windows.Media.VisualTreeHelper.GetParent(child);
            if (parent == null) return null;
            if (parent is T typedParent) return typedParent;
            return FindParent<T>(parent);
        }

        #endregion
    }
}
