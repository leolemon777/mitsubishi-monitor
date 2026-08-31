using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MitsubishiMonitor.Demo.Data;
using MitsubishiMonitor.Demo.Models;

namespace MitsubishiMonitor.Demo.Services
{
    /// <summary>
    /// 短生命周期 DbContext 数据服务。初始化任务按数据库绝对路径隔离，所有查询都支持
    /// CancellationToken，分页排序包含主键作为稳定的次级键。
    /// </summary>
    public sealed class DataService : IDataService, IDisposable
    {
        private static readonly ConcurrentDictionary<string, Lazy<Task>> InitializationTasks =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly string _databasePath;

        public DataService() : this(AppConfig.DatabasePath)
        {
        }

        internal DataService(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("数据库路径不能为空", nameof(databasePath));
            _databasePath = Path.GetFullPath(databasePath);
        }

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            var lazy = InitializationTasks.GetOrAdd(
                _databasePath,
                path => new Lazy<Task>(
                    () => InitializeCoreAsync(path),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            try
            {
                await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 只取消当前等待者；共享初始化仍继续，不能从字典移除并并发启动第二次迁移。
                throw;
            }
            catch
            {
                if (InitializationTasks.TryGetValue(_databasePath, out var current) &&
                    ReferenceEquals(current, lazy))
                {
                    InitializationTasks.TryRemove(_databasePath, out _);
                }
                throw;
            }
        }

        private static async Task InitializeCoreAsync(string databasePath)
        {
            using var context = new MonitorDbContext(databasePath);
            await context.Database.EnsureCreatedAsync().ConfigureAwait(false);

            // WAL、同步级别和结构升级均是权威日志可用的前置条件，任何失败都向上抛出。
            await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;").ConfigureAwait(false);
            await context.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;").ConfigureAwait(false);
            context.EnsureSchemaUpgraded();
            await context.Database.ExecuteSqlRawAsync("SELECT 1;").ConfigureAwait(false);

            System.Diagnostics.Debug.WriteLine($"[DB] 初始化完成：{databasePath}");
        }

        public async Task CleanOldDataAsync(CancellationToken cancellationToken = default)
        {
            var cutoffDate = DateTime.Now.AddDays(-15);
            using var context = CreateContext();
            var temperatureDeleted = await context.Database.ExecuteSqlRawAsync(
                "DELETE FROM TemperatureLog WHERE RecordTime < {0}",
                new object[] { cutoffDate },
                cancellationToken).ConfigureAwait(false);
            var operationDeleted = await context.Database.ExecuteSqlRawAsync(
                "DELETE FROM OperationLog WHERE LogTime < {0}",
                new object[] { cutoffDate },
                cancellationToken).ConfigureAwait(false);

            System.Diagnostics.Debug.WriteLine(
                $"[数据清理] 删除温度 {temperatureDeleted} 条、操作 {operationDeleted} 条，保留 15 天");
        }

        public async Task<List<OperationLog>> GetOperationLogsPagedAsync(
            int? deviceId,
            DateTime startTime,
            DateTime endTime,
            int pageIndex,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            ValidateRange(startTime, endTime);
            ValidateDeviceId(deviceId);
            ValidatePage(pageIndex, pageSize);
            using var context = CreateContext();
            var query = context.OperationLogs
                .AsNoTracking()
                .Where(log => log.LogTime >= startTime && log.LogTime <= endTime);
            if (deviceId.HasValue)
                query = query.Where(log => log.DeviceId == deviceId.Value);

            var ordered = query
                .OrderByDescending(log => log.LogTime)
                .ThenByDescending(log => log.Id);
            var skip = checked(pageIndex * pageSize);
            return await ordered.Skip(skip).Take(pageSize)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<List<TemperatureLog>> GetTemperatureLogsPagedAsync(
            int? deviceId,
            DateTime startTime,
            DateTime endTime,
            int pageIndex,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            ValidateRange(startTime, endTime);
            ValidateDeviceId(deviceId);
            ValidatePage(pageIndex, pageSize);
            using var context = CreateContext();
            var query = context.TemperatureLogs
                .AsNoTracking()
                .Where(log => log.RecordTime >= startTime && log.RecordTime <= endTime);
            if (deviceId.HasValue)
                query = query.Where(log => log.DeviceId == deviceId.Value);

            var ordered = query
                .OrderByDescending(log => log.RecordTime)
                .ThenByDescending(log => log.Id);
            var skip = checked(pageIndex * pageSize);
            var page = await ordered.Skip(skip).Take(pageSize)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            page.Reverse();
            return page;
        }

        public async Task<int> GetOperationLogCountAsync(
            int? deviceId,
            DateTime startTime,
            DateTime endTime,
            CancellationToken cancellationToken = default)
        {
            ValidateRange(startTime, endTime);
            ValidateDeviceId(deviceId);
            using var context = CreateContext();
            var query = context.OperationLogs
                .AsNoTracking()
                .Where(log => log.LogTime >= startTime && log.LogTime <= endTime);
            if (deviceId.HasValue)
                query = query.Where(log => log.DeviceId == deviceId.Value);
            return await query.CountAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<int> GetTemperatureLogCountAsync(
            int? deviceId,
            DateTime startTime,
            DateTime endTime,
            CancellationToken cancellationToken = default)
        {
            ValidateRange(startTime, endTime);
            ValidateDeviceId(deviceId);
            using var context = CreateContext();
            var query = context.TemperatureLogs
                .AsNoTracking()
                .Where(log => log.RecordTime >= startTime && log.RecordTime <= endTime);
            if (deviceId.HasValue)
                query = query.Where(log => log.DeviceId == deviceId.Value);
            return await query.CountAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<TemperatureStatistics> GetTemperatureStatisticsAsync(
            int? deviceId,
            DateTime startTime,
            DateTime endTime,
            CancellationToken cancellationToken = default)
        {
            ValidateRange(startTime, endTime);
            ValidateDeviceId(deviceId);
            using var context = CreateContext();
            var query = context.TemperatureLogs
                .AsNoTracking()
                .Where(log => log.RecordTime >= startTime && log.RecordTime <= endTime);
            if (deviceId.HasValue)
                query = query.Where(log => log.DeviceId == deviceId.Value);

            return await ProjectStatistics(query)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false)
                ?? new TemperatureStatistics();
        }

        private static IQueryable<TemperatureStatistics> ProjectStatistics(
            IQueryable<TemperatureLog> query)
            => query.GroupBy(_ => 1).Select(group => new TemperatureStatistics
            {
                Count = group.Count(),
                AbnormalCount = group.Count(log => log.IsAbnormal),
                Minimum = group.Min(log => log.Temperature),
                Maximum = group.Max(log => log.Temperature),
                Average = (float)group.Average(log => log.Temperature)
            });

        private MonitorDbContext CreateContext() => new(_databasePath);

        private static void ValidateRange(DateTime startTime, DateTime endTime)
        {
            if (endTime < startTime)
                throw new ArgumentException("结束时间不能早于开始时间");
        }

        private static void ValidateDeviceId(int? deviceId)
        {
            if (deviceId.HasValue && deviceId.Value <= 0)
                throw new ArgumentOutOfRangeException(nameof(deviceId));
        }

        private static void ValidatePage(int pageIndex, int pageSize)
        {
            if (pageIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(pageIndex));
            if (pageSize <= 0 || pageSize > 10000)
                throw new ArgumentOutOfRangeException(nameof(pageSize), "分页大小必须在 1～10000 之间");
        }

        public void Dispose()
        {
            // 短生命周期上下文模式，无字段资源需要释放。
        }
    }
}
