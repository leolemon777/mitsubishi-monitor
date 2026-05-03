using System;
using System.Collections.ObjectModel;
using MitsubishiMonitor.Demo.Models;

namespace MitsubishiMonitor.Demo.Services
{
    /// <summary>
    /// 通知服务：管理全局通知，提供温度超限/连接断开警报
    /// </summary>
    public class NotificationService
    {
        private static NotificationService _instance;
        public static NotificationService Instance => _instance ??= new NotificationService();

        public ObservableCollection<Notification> Notifications { get; } = new();
        public event EventHandler<Notification> NotificationAdded;

        private int _nextId = 1;

        private NotificationService() { }

        public void Show(string title, string message, NotificationType type, int deviceId = 0)
        {
            var n = new Notification
            {
                Id = _nextId++,
                Title = title,
                Message = message,
                Type = type,
                Timestamp = DateTime.Now,
                IsRead = false,
                DeviceId = deviceId
            };

            // 最多保留 50 条
            if (Notifications.Count >= 50)
                Notifications.RemoveAt(Notifications.Count - 1);

            Notifications.Insert(0, n);
            NotificationAdded?.Invoke(this, n);
        }

        public void ShowTemperatureAlert(Device device, float temperature)
            => Show("温度超限警报", $"{device.Name} 温度 {temperature:F1}°C 超过阈值！", NotificationType.Warning, device.Id);

        public void ShowConnectionAlert(Device device, bool isConnected)
        {
            if (isConnected)
                Show("设备已连接", $"{device.Name} 连接成功", NotificationType.Success, device.Id);
            else
                Show("连接断开", $"{device.Name} 连接已断开", NotificationType.Error, device.Id);
        }

        public void Dismiss(Notification n) => Notifications.Remove(n);

        public void ClearAll() => Notifications.Clear();
    }
}
