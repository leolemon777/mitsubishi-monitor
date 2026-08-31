using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MitsubishiMonitor.Demo.Models;

namespace MitsubishiMonitor.Demo.Services
{
    /// <summary>
    /// 导出查询统一入口。先统计再分页读取，禁止把无界历史一次性装入内存。
    /// </summary>
    public static class BoundedLogExportLoader
    {
        public const int MaximumCombinedRows = 100000;
        private const int PageSize = 5000;

        public static async Task<LogExportDataSet> LoadAsync(
            IDataService dataService,
            int? deviceId,
            DateTime startTime,
            DateTime endTime,
            bool includeTemperature,
            bool includeOperation,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(dataService);
            if (!includeTemperature && !includeOperation)
                throw new ArgumentException("至少需要选择一种导出数据", nameof(includeTemperature));

            var temperatureCountTask = includeTemperature
                ? dataService.GetTemperatureLogCountAsync(deviceId, startTime, endTime, cancellationToken)
                : Task.FromResult(0);
            var operationCountTask = includeOperation
                ? dataService.GetOperationLogCountAsync(deviceId, startTime, endTime, cancellationToken)
                : Task.FromResult(0);

            await Task.WhenAll(temperatureCountTask, operationCountTask).ConfigureAwait(false);
            var temperatureCount = await temperatureCountTask.ConfigureAwait(false);
            var operationCount = await operationCountTask.ConfigureAwait(false);
            var combinedCount = checked(temperatureCount + operationCount);

            if (combinedCount > MaximumCombinedRows)
            {
                throw new InvalidOperationException(
                    $"当前范围共 {combinedCount:N0} 条（温度 {temperatureCount:N0}、操作 {operationCount:N0}），" +
                    $"超过单次导出安全上限 {MaximumCombinedRows:N0} 条。请缩小时间范围后再导出，系统不会静默截断数据。");
            }

            var temperaturesTask = includeTemperature
                ? LoadTemperaturesAsync(
                    dataService, deviceId, startTime, endTime, temperatureCount, cancellationToken)
                : Task.FromResult(new List<TemperatureLog>());
            var operationsTask = includeOperation
                ? LoadOperationsAsync(
                    dataService, deviceId, startTime, endTime, operationCount, cancellationToken)
                : Task.FromResult(new List<OperationLog>());

            await Task.WhenAll(temperaturesTask, operationsTask).ConfigureAwait(false);
            return new LogExportDataSet(
                await temperaturesTask.ConfigureAwait(false),
                await operationsTask.ConfigureAwait(false));
        }

        private static async Task<List<TemperatureLog>> LoadTemperaturesAsync(
            IDataService dataService,
            int? deviceId,
            DateTime startTime,
            DateTime endTime,
            int expectedCount,
            CancellationToken cancellationToken)
        {
            var result = new List<TemperatureLog>(expectedCount);
            for (var pageIndex = 0; result.Count < expectedCount; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await dataService.GetTemperatureLogsPagedAsync(
                    deviceId, startTime, endTime, pageIndex, PageSize, cancellationToken)
                    .ConfigureAwait(false);
                if (page.Count == 0)
                    break;
                result.AddRange(page);
            }

            // 数据库分页按“最新页优先”，导出温度曲线统一恢复为全局时间正序。
            return result
                .OrderBy(log => log.RecordTime)
                .ThenBy(log => log.Id)
                .ToList();
        }

        private static async Task<List<OperationLog>> LoadOperationsAsync(
            IDataService dataService,
            int? deviceId,
            DateTime startTime,
            DateTime endTime,
            int expectedCount,
            CancellationToken cancellationToken)
        {
            var result = new List<OperationLog>(expectedCount);
            for (var pageIndex = 0; result.Count < expectedCount; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await dataService.GetOperationLogsPagedAsync(
                    deviceId, startTime, endTime, pageIndex, PageSize, cancellationToken)
                    .ConfigureAwait(false);
                if (page.Count == 0)
                    break;
                result.AddRange(page);
            }

            return result
                .OrderByDescending(log => log.LogTime)
                .ThenByDescending(log => log.Id)
                .ToList();
        }
    }

    public sealed class LogExportDataSet
    {
        public LogExportDataSet(
            List<TemperatureLog> temperatureLogs,
            List<OperationLog> operationLogs)
        {
            TemperatureLogs = temperatureLogs ?? throw new ArgumentNullException(nameof(temperatureLogs));
            OperationLogs = operationLogs ?? throw new ArgumentNullException(nameof(operationLogs));
        }

        public List<TemperatureLog> TemperatureLogs { get; }
        public List<OperationLog> OperationLogs { get; }
    }
}
