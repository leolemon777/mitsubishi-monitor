using System;

namespace MitsubishiMonitor.Demo.Services
{
    /// <summary>
    /// 逐个调用事件订阅者，避免一个界面/日志订阅者抛异常后阻断其余订阅者和采集循环。
    /// </summary>
    internal static class SafeEventDispatcher
    {
        public static void Invoke<T>(
            object sender,
            EventHandler<T> handlers,
            T args,
            Action<Exception> onSubscriberError = null)
        {
            if (handlers == null)
                return;

            foreach (EventHandler<T> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(sender, args);
                }
                catch (Exception ex)
                {
                    try { onSubscriberError?.Invoke(ex); } catch { }
                }
            }
        }
    }
}
