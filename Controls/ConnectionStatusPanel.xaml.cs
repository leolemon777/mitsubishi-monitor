using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace MitsubishiMonitor.Demo.Controls
{
    /// <summary>
    /// 连接状态面板：显示连接时长、数据包统计、最后通信时间
    /// </summary>
    public partial class ConnectionStatusPanel : System.Windows.Controls.UserControl
    {
        private DispatcherTimer _durationTimer;
        private DateTime _connectionStartTime;

        public ConnectionStatusPanel()
        {
            InitializeComponent();
        }

        #region 依赖属性

        public static readonly DependencyProperty IsConnectedProperty =
            DependencyProperty.Register(nameof(IsConnected), typeof(bool), typeof(ConnectionStatusPanel),
                new PropertyMetadata(false, OnIsConnectedChanged));

        public static readonly DependencyProperty ConnectionDurationDisplayProperty =
            DependencyProperty.Register(nameof(ConnectionDurationDisplay), typeof(string), typeof(ConnectionStatusPanel),
                new PropertyMetadata("--"));

        public static readonly DependencyProperty PacketsSentProperty =
            DependencyProperty.Register(nameof(PacketsSent), typeof(int), typeof(ConnectionStatusPanel),
                new PropertyMetadata(0));

        public static readonly DependencyProperty LastCommunicationDisplayProperty =
            DependencyProperty.Register(nameof(LastCommunicationDisplay), typeof(string), typeof(ConnectionStatusPanel),
                new PropertyMetadata("--"));

        public static readonly DependencyProperty ReconnectCommandProperty =
            DependencyProperty.Register(nameof(ReconnectCommand), typeof(ICommand), typeof(ConnectionStatusPanel),
                new PropertyMetadata(null));

        #endregion

        #region 属性

        public bool IsConnected
        {
            get => (bool)GetValue(IsConnectedProperty);
            set => SetValue(IsConnectedProperty, value);
        }

        public string ConnectionDurationDisplay
        {
            get => (string)GetValue(ConnectionDurationDisplayProperty);
            set => SetValue(ConnectionDurationDisplayProperty, value);
        }

        public int PacketsSent
        {
            get => (int)GetValue(PacketsSentProperty);
            set => SetValue(PacketsSentProperty, value);
        }

        public string LastCommunicationDisplay
        {
            get => (string)GetValue(LastCommunicationDisplayProperty);
            set => SetValue(LastCommunicationDisplayProperty, value);
        }

        public ICommand ReconnectCommand
        {
            get => (ICommand)GetValue(ReconnectCommandProperty);
            set => SetValue(ReconnectCommandProperty, value);
        }

        #endregion

        private static void OnIsConnectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ConnectionStatusPanel panel)
            {
                if ((bool)e.NewValue)
                    panel.StartTimer();
                else
                    panel.StopTimer();
            }
        }

        private void StartTimer()
        {
            _connectionStartTime = DateTime.Now;
            _durationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _durationTimer.Tick += (s, e) =>
            {
                var dur = DateTime.Now - _connectionStartTime;
                ConnectionDurationDisplay = dur.TotalHours >= 1
                    ? $"{(int)dur.TotalHours}h {dur.Minutes}m {dur.Seconds}s"
                    : dur.TotalMinutes >= 1
                        ? $"{(int)dur.TotalMinutes}m {dur.Seconds}s"
                        : $"{dur.Seconds}s";
                LastCommunicationDisplay = DateTime.Now.ToString("HH:mm:ss");
                PacketsSent++;
            };
            _durationTimer.Start();
        }

        private void StopTimer()
        {
            _durationTimer?.Stop();
            _durationTimer = null;
            ConnectionDurationDisplay = "--";
        }
    }
}
