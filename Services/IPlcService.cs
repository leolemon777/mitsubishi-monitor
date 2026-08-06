using System;
using System.Threading.Tasks;
using MitsubishiMonitor.Demo.Models;

namespace MitsubishiMonitor.Demo.Services
{
    /// <summary>
    /// PLC服务接口
    /// </summary>
    public interface IPlcService
    {
        /// <summary>
        /// 当前PLC状态
        /// </summary>
        PlcStatus CurrentStatus { get; }

        /// <summary>
        /// PLC配置
        /// </summary>
        PlcConfig Config { get; }

        /// <summary>
        /// 连接状态变化事件
        /// </summary>
        event EventHandler<bool> ConnectionStateChanged;

        /// <summary>
        /// X/Y点状态变化事件
        /// </summary>
        event EventHandler<StateChangeEvent> StateChanged;

        /// <summary>
        /// 连接到PLC
        /// </summary>
        Task<bool> ConnectAsync();

        /// <summary>
        /// 断开连接
        /// </summary>
        void Disconnect();

        /// <summary>
        /// 读取X点状态
        /// </summary>
        Task<bool[]> ReadXPointsAsync();

        /// <summary>
        /// 读取Y点状态
        /// </summary>
        Task<bool[]> ReadYPointsAsync();

        /// <summary>
        /// 读取实际温度。读取失败时返回 <see cref="float.NaN"/>；
        /// 0°C 和负温是合法采样，调用方不得将其当作失败值。
        /// </summary>
        Task<float> ReadTemperatureAsync();

        /// <summary>
        /// 一次性读取所有数据
        /// </summary>
        Task<PlcStatus> ReadAllAsync();

        /// <summary>
        /// 开始自动采集
        /// </summary>
        void StartAcquisition();

        /// <summary>
        /// 停止自动采集
        /// </summary>
        void StopAcquisition();

        /// <summary>
        /// 是否正在采集
        /// </summary>
        bool IsAcquiring { get; }
    }
}
