using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using MitsubishiMonitor.Demo.Data;
using MitsubishiMonitor.Demo.Models;

namespace MitsubishiMonitor.Demo.Services
{
    /// <summary>
    /// 日志缓冲服务：将高频的日志写入请求攒批后统一提交数据库，
    /// 避免 6 台 PLC 并发状态变化时频繁写库导致的锁竞争和性能问题。
    /// </summary>
    public class LogBufferService : IDisposable
    {
        private readonly ConcurrentQueue<OperationLog> _operationLogQueue = new();
        private readonly ConcurrentQueue<TemperatureLog> _temperatureLogQueue = new();
        private readonly System.Timers.Timer _flushTimer;
        private int _isFlushing;
        private bool _isDisposed;

        /// <summary>
        /// DB 初始化是否完成（由外部 DeviceManagerService 设置，写入前检查）
        /// </summary>
        public volatile bool IsDbReady = false;

        /// <summary>
        /// 批量写入间隔（毫秒），默认 3 秒
        /// </summary>
        public int FlushIntervalMs { get; set; } = 3000;

        /// <summary>
        /// 单次写库最大批量，避免长时间运行后积压日志形成一次超大 SQLite 事务。
        /// </summary>
        public int MaxBatchSize { get; set; } = 1000;

        /// <summary>
        /// 内存中允许的最大队列长度。超出后入队会丢弃最早的一条，避免极端情况下
        /// （如 SQLite 长时间写不动 + PLC 状态高频闪变）日志队列无限增长，
        /// 最终撑爆托管堆引发大 GC 暂停甚至 OOM，把 UI 拖死。
        /// 50000 条对每秒几十条变化的工况已是数十分钟堆积量，足够覆盖偶发慢盘场景。
        /// </summary>
        public int MaxQueueSize { get; set; } = 50000;

        // 上一次因队列超限发出告警的时间（防止刷屏）
        private long _lastQueueOverflowReportMs = 0;

        public LogBufferService()
        {
            _flushTimer = new System.Timers.Timer(FlushIntervalMs);
            _flushTimer.Elapsed += OnFlushTimerElapsed;
            _flushTimer.AutoReset = true;
            _flushTimer.Start();
        }

        /// <summary>
        /// 入队一条操作日志（线程安全，不阻塞）。超过 MaxQueueSize 时丢弃最早的一条。
        /// </summary>
        public void EnqueueOperationLog(OperationLog log)
        {
            _operationLogQueue.Enqueue(log);
            TrimIfOverflow(_operationLogQueue, "operation");
        }

        /// <summary>
        /// 入队一条温度日志（线程安全，不阻塞）。超过 MaxQueueSize 时丢弃最早的一条。
        /// </summary>
        public void EnqueueTemperatureLog(TemperatureLog log)
        {
            _temperatureLogQueue.Enqueue(log);
            TrimIfOverflow(_temperatureLogQueue, "temperature");
        }

        /// <summary>
        /// 队列溢出时丢弃 FIFO 头部，最多剪 100 条避免一次入队卡太久。
        /// 同一类型 60s 内只发一次告警，避免日志洪水。
        /// </summary>
        private void TrimIfOverflow<T>(ConcurrentQueue<T> queue, string kind)
        {
            if (queue.Count <= MaxQueueSize) return;

            int dropped = 0;
            while (queue.Count > MaxQueueSize && dropped < 100 && queue.TryDequeue(out _))
                dropped++;

            if (dropped == 0) return;

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var last = System.Threading.Interlocked.Read(ref _lastQueueOverflowReportMs);
            if (nowMs - last > 60_000)
            {
                System.Threading.Interlocked.Exchange(ref _lastQueueOverflowReportMs, nowMs);
                Views.MainWindow.DbgLog("LogBufferService:QueueOverflow", "日志队列溢出已丢弃", new
                {
                    kind, dropped, queueLen = queue.Count, max = MaxQueueSize
                }, "B");
            }
        }

        /// <summary>
        /// 定时器回调：批量写入数据库
        /// </summary>
        private void OnFlushTimerElapsed(object sender, ElapsedEventArgs e)
        {
            Task.Run(async () =>
            {
                try
                {
                    await FlushAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[LogBuffer] 批量写入异常: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 将队列中的所有日志批量写入数据库
        /// </summary>
        public async Task FlushAsync()
        {
            if (Interlocked.Exchange(ref _isFlushing, 1) == 1)
                return;

            try
            {
                await FlushCoreAsync();
            }
            finally
            {
                Interlocked.Exchange(ref _isFlushing, 0);
            }
        }

        /// <summary>
        /// 单批写库。返回 false 表示本批没写成功（DB 未就绪或写库异常，日志已放回队列），
        /// 退出路径的循环刷新据此停止重试，避免原地打转。
        /// </summary>
        private async Task<bool> FlushCoreAsync()
        {
            // DB 未就绪时不取出队列，让日志继续积压，等 DB 初始化完成后再批量写入
            if (!IsDbReady)
            {
                System.Diagnostics.Debug.WriteLine("[LogBuffer] DB 未就绪，跳过本次 flush");
                return false;
            }

            // #region agent log - Hypothesis B: SQLite写入耗时
            var _dbgFlushSw = System.Diagnostics.Stopwatch.StartNew();
            // #endregion
            var operationLogs = new List<OperationLog>();
            var temperatureLogs = new List<TemperatureLog>();

            // 取出所有待写入的日志
            while (operationLogs.Count < MaxBatchSize && _operationLogQueue.TryDequeue(out var opLog))
            {
                operationLogs.Add(opLog);
            }

            while (temperatureLogs.Count < MaxBatchSize && _temperatureLogQueue.TryDequeue(out var tempLog))
            {
                temperatureLogs.Add(tempLog);
            }

            // 无数据则跳过
            if (!operationLogs.Any() && !temperatureLogs.Any())
                return true;

            try
            {
                using var context = new MonitorDbContext();

                if (operationLogs.Any())
                {
                    context.OperationLogs.AddRange(operationLogs);
                }

                if (temperatureLogs.Any())
                {
                    context.TemperatureLogs.AddRange(temperatureLogs);
                }

                await context.SaveChangesAsync();

                System.Diagnostics.Debug.WriteLine(
                    $"[LogBuffer] 批量写入完成: 操作日志 {operationLogs.Count} 条, 温度日志 {temperatureLogs.Count} 条");
                // #region agent log - Hypothesis B: 记录flush耗时，超过500ms标记
                _dbgFlushSw.Stop();
                if (_dbgFlushSw.ElapsedMilliseconds > 500)
                {
                    Views.MainWindow.DbgLog("LogBufferService:FlushAsync", "SQLite写入耗时过长", new
                    {
                        elapsedMs = _dbgFlushSw.ElapsedMilliseconds,
                        opLogs = operationLogs.Count,
                        tempLogs = temperatureLogs.Count
                    }, "B");
                }
                // #endregion
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LogBuffer] 批量写入数据库失败: {ex.Message}");

                // 写入失败时将日志放回队列，防止数据丢失
                foreach (var log in operationLogs)
                    _operationLogQueue.Enqueue(log);
                foreach (var log in temperatureLogs)
                    _temperatureLogQueue.Enqueue(log);

                System.Diagnostics.Debug.WriteLine($"[LogBuffer] 已将 {operationLogs.Count + temperatureLogs.Count} 条日志放回队列，等待下次重试");

                // #region agent log - Hypothesis B: 写库异常
                Views.MainWindow.DbgLog("LogBufferService:FlushAsync", "SQLite写入异常", new
                {
                    error = ex.Message
                }, "B");
                // #endregion
                return false;
            }
        }

        /// <summary>
        /// 同步刷新：程序退出时调用，确保剩余日志全部写入。
        /// 单批最多 MaxBatchSize 条，这里循环驱动直到两个队列清空；
        /// 定时器触发的 flush 还在写时等它结束，写库失败或超过 5 秒兜底超时则放弃，避免退出卡死。
        /// </summary>
        public void Flush()
        {
            try
            {
                var deadline = Environment.TickCount64 + 5000;
                while (!_operationLogQueue.IsEmpty || !_temperatureLogQueue.IsEmpty)
                {
                    if (Environment.TickCount64 > deadline) break;

                    if (Interlocked.Exchange(ref _isFlushing, 1) == 1)
                    {
                        // 另一个 flush 正在写库，稍等后重查队列
                        Thread.Sleep(50);
                        continue;
                    }

                    bool ok;
                    try
                    {
                        ok = FlushCoreAsync().GetAwaiter().GetResult();
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _isFlushing, 0);
                    }

                    if (!ok) break; // DB 未就绪或写库失败，继续循环只会原地重试
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LogBuffer] 退出时刷新失败: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            _flushTimer?.Stop();
            _flushTimer?.Dispose();

            // 退出前写完剩余日志
            Flush();
        }
    }
}
