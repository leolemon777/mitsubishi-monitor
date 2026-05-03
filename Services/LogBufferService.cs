using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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
        private readonly Timer _flushTimer;
        private bool _isFlushing;
        private bool _isDisposed;

        /// <summary>
        /// 批量写入间隔（毫秒），默认 3 秒
        /// </summary>
        public int FlushIntervalMs { get; set; } = 3000;

        public LogBufferService()
        {
            _flushTimer = new Timer(FlushIntervalMs);
            _flushTimer.Elapsed += OnFlushTimerElapsed;
            _flushTimer.AutoReset = true;
            _flushTimer.Start();
        }

        /// <summary>
        /// 入队一条操作日志（线程安全，不阻塞）
        /// </summary>
        public void EnqueueOperationLog(OperationLog log)
        {
            _operationLogQueue.Enqueue(log);
        }

        /// <summary>
        /// 入队一条温度日志（线程安全，不阻塞）
        /// </summary>
        public void EnqueueTemperatureLog(TemperatureLog log)
        {
            _temperatureLogQueue.Enqueue(log);
        }

        /// <summary>
        /// 定时器回调：批量写入数据库
        /// </summary>
        private void OnFlushTimerElapsed(object sender, ElapsedEventArgs e)
        {
            // 防重入
            if (_isFlushing) return;
            _isFlushing = true;

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
                finally
                {
                    _isFlushing = false;
                }
            });
        }

        /// <summary>
        /// 将队列中的所有日志批量写入数据库
        /// </summary>
        public async Task FlushAsync()
        {
            // #region agent log - Hypothesis B: SQLite写入耗时
            var _dbgFlushSw = System.Diagnostics.Stopwatch.StartNew();
            // #endregion
            var operationLogs = new List<OperationLog>();
            var temperatureLogs = new List<TemperatureLog>();

            // 取出所有待写入的日志
            while (_operationLogQueue.TryDequeue(out var opLog))
            {
                operationLogs.Add(opLog);
            }

            while (_temperatureLogQueue.TryDequeue(out var tempLog))
            {
                temperatureLogs.Add(tempLog);
            }

            // 无数据则跳过
            if (!operationLogs.Any() && !temperatureLogs.Any())
                return;

            try
            {
                using var context = new MonitorDbContext();
                // EnsureCreated 只在 DataService.InitializeAsync 中调用一次即可
                // 每 3 秒都调用会产生不必要的 SQLite 文件锁竞争

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
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LogBuffer] 批量写入数据库失败: {ex.Message}");
                // #region agent log - Hypothesis B: 写库异常
                Views.MainWindow.DbgLog("LogBufferService:FlushAsync", "SQLite写入异常", new
                {
                    error = ex.Message
                }, "B");
                // #endregion
            }
        }

        /// <summary>
        /// 同步刷新：程序退出时调用，确保剩余日志全部写入
        /// </summary>
        public void Flush()
        {
            try
            {
                FlushAsync().GetAwaiter().GetResult();
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
