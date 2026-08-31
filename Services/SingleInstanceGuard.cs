using System;
using System.Threading;

namespace MitsubishiMonitor.Demo.Services
{
    /// <summary>
    /// 防止同一台 Windows 主机同时运行两个监控实例，避免重复 PLC 连接、
    /// SQLite 重复记账、HTML 文件竞争和三色灯串口争用。
    /// </summary>
    public sealed class SingleInstanceGuard : IDisposable
    {
        internal const string MutexName = @"Global\MitsubishiMonitor.Demo.Production.SingleInstance";

        private Mutex _mutex;
        private bool _ownsMutex;

        private SingleInstanceGuard(Mutex mutex)
        {
            _mutex = mutex;
            _ownsMutex = true;
        }

        public static bool TryAcquire(out SingleInstanceGuard guard, out string error)
        {
            guard = null;
            error = "";

            try
            {
                var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
                if (!createdNew)
                {
                    mutex.Dispose();
                    return false;
                }

                guard = new SingleInstanceGuard(mutex);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                // 全局对象由另一个 Windows 用户创建时，也应视为已有实例，禁止继续运行。
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public void Dispose()
        {
            var mutex = Interlocked.Exchange(ref _mutex, null);
            if (mutex == null)
                return;

            try
            {
                if (_ownsMutex)
                    mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 退出兜底路径中所有权可能已被运行时回收。
            }
            finally
            {
                _ownsMutex = false;
                mutex.Dispose();
            }
        }
    }
}
