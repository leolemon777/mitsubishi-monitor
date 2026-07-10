using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MitsubishiMonitor.Demo.Data;
using MitsubishiMonitor.Demo.Models;

namespace MitsubishiMonitor.Demo.Services
{
    /// <summary>
    /// 数据库服务实现
    /// 每次方法调用都新建一个短生命周期 DbContext，并用 AsNoTracking 进行只读查询，
    /// 避免长生命周期上下文的实体跟踪膨胀（程序长期运行时内存暴涨）和并发访问异常。
    /// </summary>
    public class DataService : IDataService, IDisposable
    {
        /// <summary>
        /// DB 是否已初始化（静态，进程生命周期内只执行一次 EnsureCreated + PRAGMA + schema 升级）
        /// </summary>
        private static volatile bool _initialized = false;
        private static readonly object _initLock = new();
        private static Task _initializeTask;

        public DataService()
        {
        }

        /// <summary>
        /// 初始化数据库：EnsureCreated + PRAGMA + 表结构升级。
        /// 内部用静态锁保证全局只执行一次，外部可多次安全调用。
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_initialized) return;

            Task initTask;
            lock (_initLock)
            {
                if (_initialized) return;
                _initializeTask ??= InitializeCoreAsync();
                initTask = _initializeTask;
            }

            try
            {
                await initTask;
            }
            catch
            {
                lock (_initLock)
                {
                    if (ReferenceEquals(_initializeTask, initTask))
                        _initializeTask = null;
                    _initialized = false;
                }
                throw;
            }
        }

        private static async Task InitializeCoreAsync()
        {
            using var ctx = new MonitorDbContext();
            await ctx.Database.EnsureCreatedAsync();

            // 启用 WAL 日志模式 + 适度同步级别，缓解 4 路 PLC 高频写入时的锁竞争
            try
            {
                await ctx.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
                await ctx.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DB] 设置 PRAGMA 失败: {ex.Message}");
            }

            // 老库无痛升级：补齐 DeviceName / PointLabel 列（已有列会被跳过）
            try
            {
                ctx.EnsureSchemaUpgraded();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DB] 表结构升级失败: {ex.Message}");
            }

            _initialized = true;
            System.Diagnostics.Debug.WriteLine("[DB] InitializeAsync 完成（全局仅此一次）");
        }

        public async Task AddTemperatureLogAsync(TemperatureLog log)
        {
            try
            {
                using var ctx = new MonitorDbContext();
                ctx.TemperatureLogs.Add(log);
                await ctx.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"添加温度日志失败: {ex.Message}");
            }
        }

        public async Task AddOperationLogAsync(OperationLog log)
        {
            try
            {
                using var ctx = new MonitorDbContext();
                ctx.OperationLogs.Add(log);
                await ctx.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"添加操作日志失败: {ex.Message}");
            }
        }

        public async Task<List<TemperatureLog>> GetTemperatureLogsAsync(DateTime startTime, DateTime endTime)
        {
            using var ctx = new MonitorDbContext();
            return await ctx.TemperatureLogs
                .AsNoTracking()
                .Where(l => l.RecordTime >= startTime && l.RecordTime <= endTime)
                .OrderBy(l => l.RecordTime)
                .ToListAsync();
        }

        public async Task<List<TemperatureLog>> GetRecentTemperatureLogsAsync(int count = 100)
        {
            using var ctx = new MonitorDbContext();
            return await ctx.TemperatureLogs
                .AsNoTracking()
                .OrderByDescending(l => l.RecordTime)
                .Take(count)
                .ToListAsync();
        }

        public async Task<List<OperationLog>> GetRecentOperationLogsAsync(int count = 50)
        {
            using var ctx = new MonitorDbContext();
            return await ctx.OperationLogs
                .AsNoTracking()
                .OrderByDescending(l => l.LogTime)
                .Take(count)
                .ToListAsync();
        }

        public async Task<List<OperationLog>> GetAllOperationLogsAsync()
        {
            using var ctx = new MonitorDbContext();
            return await ctx.OperationLogs
                .AsNoTracking()
                .OrderByDescending(l => l.LogTime)
                .ToListAsync();
        }

        public async Task CleanOldDataAsync()
        {
            try
            {
                // 保留最近 30 天的数据（约 1 个月）
                var cutoffDate = DateTime.Now.AddDays(-30);

                using var ctx = new MonitorDbContext();

                // 用 ExecuteSqlRaw 批量删，省去先 Load 再 Remove 的内存开销
                var tempDeleted = await ctx.Database.ExecuteSqlRawAsync(
                    "DELETE FROM TemperatureLog WHERE RecordTime < {0}", cutoffDate);
                var opDeleted = await ctx.Database.ExecuteSqlRawAsync(
                    "DELETE FROM OperationLog WHERE LogTime < {0}", cutoffDate);

                System.Diagnostics.Debug.WriteLine(
                    $"[数据清理] 删除温度日志 {tempDeleted} 条, 操作日志 {opDeleted} 条 (cutoff={cutoffDate:yyyy-MM-dd HH:mm:ss})");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"清理旧数据失败: {ex.Message}");
            }
        }

        public async Task<(float min, float max, float avg)> GetTemperatureStatsAsync(DateTime? startTime = null)
        {
            using var ctx = new MonitorDbContext();
            var query = ctx.TemperatureLogs.AsNoTracking().AsQueryable();

            if (startTime.HasValue)
            {
                query = query.Where(l => l.RecordTime >= startTime.Value);
            }

            // 改为聚合下推到 SQL 层，避免 ToListAsync 把全表拉到内存
            if (!await query.AnyAsync())
                return (0, 0, 0);

            var min = await query.MinAsync(l => l.Temperature);
            var max = await query.MaxAsync(l => l.Temperature);
            var avg = (float)await query.AverageAsync(l => l.Temperature);
            return (min, max, avg);
        }

        public async Task<List<TemperatureLog>> GetTemperatureLogsByDeviceAsync(int deviceId, DateTime startTime, DateTime endTime)
        {
            using var ctx = new MonitorDbContext();
            return await ctx.TemperatureLogs
                .AsNoTracking()
                .Where(l => l.DeviceId == deviceId && l.RecordTime >= startTime && l.RecordTime <= endTime)
                .OrderBy(l => l.RecordTime)
                .ToListAsync();
        }

        public async Task<List<OperationLog>> GetOperationLogsByDeviceAsync(int deviceId, DateTime startTime, DateTime endTime)
        {
            using var ctx = new MonitorDbContext();
            return await ctx.OperationLogs
                .AsNoTracking()
                .Where(l => l.DeviceId == deviceId && l.LogTime >= startTime && l.LogTime <= endTime)
                .OrderByDescending(l => l.LogTime)
                .ToListAsync();
        }

        public async Task<int> GetOperationLogCountByDeviceAsync(int deviceId, DateTime startTime, DateTime endTime)
        {
            using var ctx = new MonitorDbContext();
            return await ctx.OperationLogs
                .AsNoTracking()
                .Where(l => l.DeviceId == deviceId && l.LogTime >= startTime && l.LogTime <= endTime)
                .CountAsync();
        }

        public async Task<List<OperationLog>> GetOperationLogsByDevicePagedAsync(int deviceId, DateTime startTime, DateTime endTime, int pageIndex, int pageSize)
        {
            using var ctx = new MonitorDbContext();
            return await ctx.OperationLogs
                .AsNoTracking()
                .Where(l => l.DeviceId == deviceId && l.LogTime >= startTime && l.LogTime <= endTime)
                .OrderByDescending(l => l.LogTime)
                .Skip(pageIndex * pageSize)
                .Take(pageSize)
                .ToListAsync();
        }

        public async Task<List<TemperatureLog>> GetTemperatureLogsByDevicePagedAsync(int deviceId, DateTime startTime, DateTime endTime, int pageIndex, int pageSize)
        {
            using var ctx = new MonitorDbContext();
            return await ctx.TemperatureLogs
                .AsNoTracking()
                .Where(l => l.DeviceId == deviceId && l.RecordTime >= startTime && l.RecordTime <= endTime)
                .OrderBy(l => l.RecordTime)
                .Skip(pageIndex * pageSize)
                .Take(pageSize)
                .ToListAsync();
        }

        public void Dispose()
        {
            // 短生命周期上下文模式，无字段需要释放
        }
    }
}
