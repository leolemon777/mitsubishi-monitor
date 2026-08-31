using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MitsubishiMonitor.Demo.Models;

namespace MitsubishiMonitor.Demo.Services
{
    /// <summary>
    /// 数据服务接口
    /// </summary>
    public interface IDataService
    {
        /// <summary>
        /// 初始化数据库
        /// </summary>
        Task InitializeAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 清理旧数据（超过 15 天）
        /// </summary>
        Task CleanOldDataAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取单台或全部设备的操作日志分页；deviceId 为 null 表示全部设备。
        /// </summary>
        Task<List<OperationLog>> GetOperationLogsPagedAsync(
            int? deviceId,
            DateTime startTime,
            DateTime endTime,
            int pageIndex,
            int pageSize,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取单台或全部设备的温度日志分页；返回页内按时间正序排列。
        /// </summary>
        Task<List<TemperatureLog>> GetTemperatureLogsPagedAsync(
            int? deviceId,
            DateTime startTime,
            DateTime endTime,
            int pageIndex,
            int pageSize,
            CancellationToken cancellationToken = default);

        Task<int> GetOperationLogCountAsync(
            int? deviceId,
            DateTime startTime,
            DateTime endTime,
            CancellationToken cancellationToken = default);

        Task<int> GetTemperatureLogCountAsync(
            int? deviceId,
            DateTime startTime,
            DateTime endTime,
            CancellationToken cancellationToken = default);

        Task<TemperatureStatistics> GetTemperatureStatisticsAsync(
            int? deviceId,
            DateTime startTime,
            DateTime endTime,
            CancellationToken cancellationToken = default);
    }
}
