using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using MitsubishiMonitor.Demo.Data;
using MitsubishiMonitor.Demo.Models;

namespace MitsubishiMonitor.Demo.Services
{
    /// <summary>
    /// 日志可靠写入服务：内存批处理、溢出持久化 spool、约束坏记录隔离、
    /// 基础设施故障重试，以及可供 UI/三色灯使用的健康状态。
    /// </summary>
    public sealed class LogBufferService : IDisposable
    {
        private readonly ConcurrentQueue<OperationLog> _operationLogQueue = new();
        private readonly ConcurrentQueue<TemperatureLog> _temperatureLogQueue = new();
        private readonly System.Timers.Timer _flushTimer;
        private readonly DurableLogSpool _spool;
        private int _isFlushing;
        private bool _isDisposed;
        private volatile bool _isDbReady;
        private volatile bool _isHealthy;
        private string _healthMessage = "数据库尚未初始化";
        private long _droppedCount;
        private long _deadLetterCount;
        private DateTime? _lastSuccessfulWriteTime;
        private int _consecutiveWriteFailures;
        private DateTime _nextRetryUtc;

        public event EventHandler<StorageHealthSnapshot> HealthChanged;

        public int FlushIntervalMs { get; set; } = 3000;
        public int MaxBatchSize { get; set; } = 1000;
        public int MaxQueueSize { get; set; } = 50000;

        public bool IsDbReady => _isDbReady;
        public bool IsHealthy => _isHealthy;
        public string HealthMessage => _healthMessage;
        public int PendingCount => _operationLogQueue.Count + _temperatureLogQueue.Count;
        public long SpoolCount => _spool?.PendingCount ?? 0;
        public long DroppedCount => Interlocked.Read(ref _droppedCount);
        public long DeadLetterCount => Interlocked.Read(ref _deadLetterCount);
        public DateTime? LastSuccessfulWriteTime => _lastSuccessfulWriteTime;

        public LogBufferService()
        {
            try
            {
                _spool = new DurableLogSpool(AppConfig.DatabasePath);
            }
            catch (Exception ex)
            {
                _healthMessage = $"无法初始化日志持久化缓冲：{ex.Message}";
            }

            _flushTimer = new System.Timers.Timer(FlushIntervalMs);
            _flushTimer.Elapsed += OnFlushTimerElapsed;
            _flushTimer.AutoReset = true;
            _flushTimer.Start();
        }

        public void SetDatabaseReady()
        {
            _isDbReady = true;
            _consecutiveWriteFailures = 0;
            _nextRetryUtc = default;
            _isHealthy = _spool != null;
            _healthMessage = _spool == null
                ? "数据库已就绪，但可靠磁盘缓冲不可用"
                : SpoolCount > 0 ? "数据库已就绪，正在回放磁盘积压" : "数据库已就绪";
            PublishHealth();
        }

        public void SetDatabaseUnavailable(string error)
        {
            _isDbReady = false;
            _isHealthy = false;
            _healthMessage = string.IsNullOrWhiteSpace(error) ? "数据库不可用" : error;
            PublishHealth();
        }

        public void EnqueueOperationLog(OperationLog log)
        {
            if (log == null) return;
            _operationLogQueue.Enqueue(log);
            SpillOverflow(_operationLogQueue, "operation");
        }

        public void EnqueueTemperatureLog(TemperatureLog log)
        {
            if (log == null) return;
            _temperatureLogQueue.Enqueue(log);
            SpillOverflow(_temperatureLogQueue, "temperature");
        }

        private void SpillOverflow<T>(ConcurrentQueue<T> queue, string kind)
        {
            var changed = false;
            while (queue.Count > MaxQueueSize && queue.TryDequeue(out var value))
            {
                var persisted = value switch
                {
                    OperationLog operation => _spool?.TryAppend(operation) == true,
                    TemperatureLog temperature => _spool?.TryAppend(temperature) == true,
                    _ => false
                };

                if (!persisted)
                    Interlocked.Increment(ref _droppedCount);
                changed = true;
            }

            if (!changed) return;

            _isHealthy = DroppedCount == 0;
            _healthMessage = DroppedCount == 0
                ? $"内存队列已溢写到磁盘 spool（{kind}）"
                : $"日志队列溢出且持久化失败，已丢弃 {DroppedCount} 条";
            PublishHealth();
        }

        private void OnFlushTimerElapsed(object sender, ElapsedEventArgs e)
        {
            _ = FlushOnceAsync();
        }

        public async Task<bool> FlushOnceAsync()
        {
            if (_nextRetryUtc != default && DateTime.UtcNow < _nextRetryUtc)
                return false;
            if (Interlocked.Exchange(ref _isFlushing, 1) == 1)
                return false;

            try
            {
                return await FlushCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _isFlushing, 0);
            }
        }

        private async Task<bool> FlushCoreAsync()
        {
            if (!_isDbReady)
            {
                _isHealthy = false;
                if (string.IsNullOrWhiteSpace(_healthMessage))
                    _healthMessage = "数据库未就绪";
                PublishHealth();
                return false;
            }

            var operationLogs = _spool?.PeekOperations(MaxBatchSize) ?? new List<OperationLog>();
            var temperatureLogs = _spool?.PeekTemperatures(MaxBatchSize) ?? new List<TemperatureLog>();
            var operationSpoolCount = operationLogs.Count;
            var temperatureSpoolCount = temperatureLogs.Count;

            while (operationLogs.Count < MaxBatchSize && _operationLogQueue.TryDequeue(out var operation))
                operationLogs.Add(operation);
            while (temperatureLogs.Count < MaxBatchSize && _temperatureLogQueue.TryDequeue(out var temperature))
                temperatureLogs.Add(temperature);

            if (operationLogs.Count == 0 && temperatureLogs.Count == 0)
            {
                MarkWriteHealthy("数据库正常，无待写日志");
                return true;
            }

            var operationComplete = operationLogs.Count == 0;
            var temperatureComplete = temperatureLogs.Count == 0;
            try
            {
                if (operationLogs.Count > 0)
                {
                    await SaveValidatedOperationBatchAsync(operationLogs).ConfigureAwait(false);
                    _spool?.AcknowledgeOperations(operationSpoolCount);
                    operationComplete = true;
                }

                if (temperatureLogs.Count > 0)
                {
                    await SaveValidatedTemperatureBatchAsync(temperatureLogs).ConfigureAwait(false);
                    _spool?.AcknowledgeTemperatures(temperatureSpoolCount);
                    temperatureComplete = true;
                }

                MarkWriteHealthy($"写入成功：操作 {operationLogs.Count} 条，温度 {temperatureLogs.Count} 条");
                return true;
            }
            catch (Exception ex)
            {
                if (!operationComplete)
                    foreach (var log in operationLogs.Skip(operationSpoolCount)) _operationLogQueue.Enqueue(log);
                if (!temperatureComplete)
                    foreach (var log in temperatureLogs.Skip(temperatureSpoolCount)) _temperatureLogQueue.Enqueue(log);

                _isHealthy = false;
                _consecutiveWriteFailures++;
                var retrySeconds = Math.Min(60, 3 * (1 << Math.Min(4, _consecutiveWriteFailures - 1)));
                _nextRetryUtc = DateTime.UtcNow.AddSeconds(retrySeconds);
                _healthMessage = $"SQLite 写入失败，数据已保留，{retrySeconds} 秒后重试：{ex.Message}";
                Views.MainWindow.DbgLog("LogBufferService:Flush", "SQLite 写入异常", new
                {
                    error = ex.ToString(),
                    pending = PendingCount,
                    spool = SpoolCount
                }, "DB");
                PublishHealth();
                return false;
            }
        }

        private async Task SaveValidatedOperationBatchAsync(List<OperationLog> records)
        {
            var valid = new List<OperationLog>(records.Count);
            foreach (var record in records)
            {
                var validationError = ValidateOperationLog(record);
                if (validationError == null)
                    valid.Add(record);
                else
                    MoveToDeadLetter("operation", record, validationError);
            }

            if (valid.Count > 0)
                await SaveOperationBatchAsync(valid).ConfigureAwait(false);
        }

        private async Task SaveValidatedTemperatureBatchAsync(List<TemperatureLog> records)
        {
            var valid = new List<TemperatureLog>(records.Count);
            foreach (var record in records)
            {
                var validationError = ValidateTemperatureLog(record);
                if (validationError == null)
                    valid.Add(record);
                else
                    MoveToDeadLetter("temperature", record, validationError);
            }

            if (valid.Count > 0)
                await SaveTemperatureBatchAsync(valid).ConfigureAwait(false);
        }

        private static async Task SaveOperationBatchAsync(List<OperationLog> records)
        {
            using var context = new MonitorDbContext();
            context.OperationLogs.AddRange(records);
            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        private static async Task SaveTemperatureBatchAsync(List<TemperatureLog> records)
        {
            using var context = new MonitorDbContext();
            context.TemperatureLogs.AddRange(records);
            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        private void MoveToDeadLetter(string kind, object record, string validationError)
        {
            if (_spool == null)
                throw new InvalidDataException(
                    $"发现无效 {kind} 日志，但 dead-letter 存储不可用：{validationError}");

            _spool.AppendDeadLetter(kind, record, new InvalidDataException(validationError));
            Interlocked.Increment(ref _deadLetterCount);
        }

        private static string ValidateOperationLog(OperationLog record)
        {
            if (record == null) return "记录为空";
            if (record.DeviceId <= 0) return "DeviceId 必须大于 0";
            if (string.IsNullOrWhiteSpace(record.LogType) || record.LogType.Length > 10)
                return "LogType 必须为 1～10 个字符";
            if ((record.PointAddress?.Length ?? 0) > 10) return "PointAddress 超过 10 个字符";
            if ((record.PointLabel?.Length ?? 0) > 100) return "PointLabel 超过 100 个字符";
            if ((record.DeviceName?.Length ?? 0) > 50) return "DeviceName 超过 50 个字符";
            if ((record.Action?.Length ?? 0) > 50) return "Action 超过 50 个字符";
            if ((record.Description?.Length ?? 0) > 200) return "Description 超过 200 个字符";
            if (record.LogTime == default) return "LogTime 不能为空";
            return null;
        }

        private static string ValidateTemperatureLog(TemperatureLog record)
        {
            if (record == null) return "记录为空";
            if (record.DeviceId <= 0) return "DeviceId 必须大于 0";
            if ((record.DeviceName?.Length ?? 0) > 50) return "DeviceName 超过 50 个字符";
            if (record.RecordTime == default) return "RecordTime 不能为空";
            if (!float.IsFinite(record.Temperature) ||
                !float.IsFinite(record.ThermocoupleA) ||
                !float.IsFinite(record.ThermocoupleB) ||
                !float.IsFinite(record.ThermocoupleC) ||
                !float.IsFinite(record.Threshold) ||
                !float.IsFinite(record.AlarmThreshold) ||
                !float.IsFinite(record.TargetTemperature))
                return "温度或辅助遥测包含非有限数值";
            if (record.AlarmThreshold < 0 || record.AlarmThreshold > 500)
                return "AlarmThreshold 超出 0～500 范围";
            return null;
        }

        private void MarkWriteHealthy(string message)
        {
            _lastSuccessfulWriteTime = DateTime.Now;
            _consecutiveWriteFailures = 0;
            _nextRetryUtc = default;
            _isHealthy = DroppedCount == 0 && DeadLetterCount == 0 && _spool != null;
            var details = message;
            if (_spool == null)
                details += "；可靠磁盘缓冲不可用";
            if (DeadLetterCount > 0)
                details += $"；有 {DeadLetterCount} 条坏记录已隔离";
            _healthMessage = details;
            PublishHealth();
        }

        private void PublishHealth()
        {
            SafeEventDispatcher.Invoke(
                this,
                HealthChanged,
                new StorageHealthSnapshot
                {
                    IsReady = _isDbReady,
                    IsHealthy = _isHealthy,
                    Message = _healthMessage,
                    PendingCount = PendingCount,
                    SpoolCount = SpoolCount,
                    DroppedCount = DroppedCount,
                    DeadLetterCount = DeadLetterCount,
                    LastSuccessfulWriteTime = _lastSuccessfulWriteTime
                },
                ex => System.Diagnostics.Debug.WriteLine(
                    $"[LogBuffer] 健康事件订阅者异常: {ex.Message}"));
        }

        public void Flush()
        {
            try
            {
                var deadline = Environment.TickCount64 + 5000;
                while (!_operationLogQueue.IsEmpty || !_temperatureLogQueue.IsEmpty || SpoolCount > 0)
                {
                    if (Environment.TickCount64 > deadline) break;
                    if (Interlocked.Exchange(ref _isFlushing, 1) == 1)
                    {
                        Thread.Sleep(50);
                        continue;
                    }

                    bool success;
                    try
                    {
                        success = FlushCoreAsync().GetAwaiter().GetResult();
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _isFlushing, 0);
                    }
                    if (!success) break;
                }
            }
            catch (Exception ex)
            {
                _isHealthy = false;
                _healthMessage = $"退出刷新失败：{ex.Message}";
                PublishHealth();
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _flushTimer.Stop();
            _flushTimer.Dispose();
            Flush();
        }
    }
}
