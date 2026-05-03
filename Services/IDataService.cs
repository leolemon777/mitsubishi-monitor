using System;
using System.Collections.Generic;
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
        Task InitializeAsync();

        /// <summary>
        /// 添加温度日志
        /// </summary>
        Task AddTemperatureLogAsync(TemperatureLog log);

        /// <summary>
        /// 添加操作日志
        /// </summary>
        Task AddOperationLogAsync(OperationLog log);

        /// <summary>
        /// 获取指定时间范围的温度日志
        /// </summary>
        Task<List<TemperatureLog>> GetTemperatureLogsAsync(DateTime startTime, DateTime endTime);

        /// <summary>
        /// 获取最近的温度日志 (用于曲线图)
        /// </summary>
        Task<List<TemperatureLog>> GetRecentTemperatureLogsAsync(int count = 100);

        /// <summary>
        /// 获取最近的操作日志
        /// </summary>
        Task<List<OperationLog>> GetRecentOperationLogsAsync(int count = 50);

        /// <summary>
        /// 获取所有操作日志
        /// </summary>
        Task<List<OperationLog>> GetAllOperationLogsAsync();

        /// <summary>
        /// 清理旧数据 (超过15天)
        /// </summary>
        Task CleanOldDataAsync();

        /// <summary>
        /// 获取温度统计信息
        /// </summary>
        Task<(float min, float max, float avg)> GetTemperatureStatsAsync(DateTime? startTime = null);

        /// <summary>
        /// 获取指定设备的温度日志
        /// </summary>
        Task<List<TemperatureLog>> GetTemperatureLogsByDeviceAsync(int deviceId, DateTime startTime, DateTime endTime);

        /// <summary>
        /// 获取指定设备的操作日志
        /// </summary>
        Task<List<OperationLog>> GetOperationLogsByDeviceAsync(int deviceId, DateTime startTime, DateTime endTime);

        /// <summary>
        /// 获取指定设备最近的操作日志数量
        /// </summary>
        Task<int> GetOperationLogCountByDeviceAsync(int deviceId, DateTime startTime, DateTime endTime);

        /// <summary>
        /// 获取指定设备的操作日志（分页）
        /// </summary>
        Task<List<OperationLog>> GetOperationLogsByDevicePagedAsync(int deviceId, DateTime startTime, DateTime endTime, int pageIndex, int pageSize);

        /// <summary>
        /// 获取指定设备的温度日志（分页）
        /// </summary>
        Task<List<TemperatureLog>> GetTemperatureLogsByDevicePagedAsync(int deviceId, DateTime startTime, DateTime endTime, int pageIndex, int pageSize);
    }
}
