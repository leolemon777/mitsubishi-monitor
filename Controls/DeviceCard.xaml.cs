using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

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
            RootBorder.MouseLeftButtonUp += OnRootMouseLeftButtonUp;
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
            if (FindParent<Button>(src) != null)
                return;
            _cardPressed = true;
            UpdateCardChrome();
        }

        private void OnRootMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var shouldOpen = _cardPressed && e.ChangedButton == MouseButton.Left;
            _cardPressed = false;
            UpdateCardChrome();

            if (!shouldOpen || e.OriginalSource is not DependencyObject source)
                return;
            if (FindParent<Button>(source) != null)
                return;

            var parameter = DataContext;
            if (parameter is Models.Device { IsPlaceholder: true })
                return;

            var command = DetailCommand;
            if (command == null || !command.CanExecute(parameter))
                return;

            // 详情窗口是模态窗口。必须等本次 MouseUp 完整退栈后再进入嵌套消息循环，
            // 否则按压状态与输入路由停留在半完成状态，看起来像“点击后卡死”。
            Dispatcher.BeginInvoke(new System.Action(() =>
            {
                if (command.CanExecute(parameter))
                    command.Execute(parameter);
            }), DispatcherPriority.Background);
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

        /// <summary>
        /// 查找当前元素或父级元素；兼容 TextBlock、Run 等可成为点击源的内容元素。
        /// </summary>
        private T FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T typedParent)
                    return typedParent;

                child = child switch
                {
                    ContentElement content => ContentOperations.GetParent(content),
                    FrameworkElement element =>
                        element.Parent ?? System.Windows.Media.VisualTreeHelper.GetParent(element),
                    _ => System.Windows.Media.VisualTreeHelper.GetParent(child)
                };
            }

            return null;
        }

        #endregion
    }
}
