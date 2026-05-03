using System;
using System.Windows.Media;

namespace MitsubishiMonitor.Demo.Models
{
    public enum NotificationType { Info, Warning, Error, Success }

    /// <summary>
    /// 通知/警报模型
    /// </summary>
    public class Notification
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public NotificationType Type { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public bool IsRead { get; set; }
        public int DeviceId { get; set; }

        public string TypeIcon => Type switch
        {
            NotificationType.Info => "ℹ",
            NotificationType.Warning => "⚠",
            NotificationType.Error => "✗",
            NotificationType.Success => "✓",
            _ => "•"
        };

        public string TimeDisplay => Timestamp.ToString("HH:mm:ss");
    }
}
