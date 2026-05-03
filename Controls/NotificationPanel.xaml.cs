using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using MitsubishiMonitor.Demo.Models;

namespace MitsubishiMonitor.Demo.Controls
{
    /// <summary>
    /// 通知/警报面板控件
    /// </summary>
    public partial class NotificationPanel : System.Windows.Controls.UserControl
    {
        public NotificationPanel()
        {
            InitializeComponent();
        }

        #region 依赖属性

        public static readonly DependencyProperty NotificationsProperty =
            DependencyProperty.Register(nameof(Notifications), typeof(ObservableCollection<Notification>), typeof(NotificationPanel),
                new PropertyMetadata(null));

        public static readonly DependencyProperty UnreadCountProperty =
            DependencyProperty.Register(nameof(UnreadCount), typeof(int), typeof(NotificationPanel),
                new PropertyMetadata(0));

        public static readonly DependencyProperty DismissCommandProperty =
            DependencyProperty.Register(nameof(DismissCommand), typeof(ICommand), typeof(NotificationPanel),
                new PropertyMetadata(null));

        public static readonly DependencyProperty ClearAllCommandProperty =
            DependencyProperty.Register(nameof(ClearAllCommand), typeof(ICommand), typeof(NotificationPanel),
                new PropertyMetadata(null));

        #endregion

        #region 属性

        public ObservableCollection<Notification> Notifications
        {
            get => (ObservableCollection<Notification>)GetValue(NotificationsProperty);
            set => SetValue(NotificationsProperty, value);
        }

        public int UnreadCount
        {
            get => (int)GetValue(UnreadCountProperty);
            set => SetValue(UnreadCountProperty, value);
        }

        public ICommand DismissCommand
        {
            get => (ICommand)GetValue(DismissCommandProperty);
            set => SetValue(DismissCommandProperty, value);
        }

        public ICommand ClearAllCommand
        {
            get => (ICommand)GetValue(ClearAllCommandProperty);
            set => SetValue(ClearAllCommandProperty, value);
        }

        #endregion
    }
}
