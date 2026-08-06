using HslCommunication;
using HslCommunication.Profinet.Melsec;
using MitsubishiMonitor.Demo.Models;

namespace MitsubishiMonitor.Demo.Services
{
    /// <summary>
    /// HslCommunication 的最小适配层。
    /// 生产环境使用真实 MelsecA1ENet，测试环境可以注入会卡住或失败的传输实现，
    /// 从而在没有 PLC 的情况下验证超时、废弃旧会话和重连逻辑。
    /// </summary>
    internal interface IMitsubishiPlcTransport
    {
        int ReceiveTimeOut { get; set; }
        int ConnectTimeOut { get; set; }

        OperateResult ConnectServer();
        OperateResult ConnectClose();
        void Abort();
        OperateResult<bool[]> ReadBool(string address, ushort length);
        OperateResult<short[]> ReadInt16(string address, ushort length);
        OperateResult<int[]> ReadInt32(string address, ushort length);
    }

    internal interface IMitsubishiPlcTransportFactory
    {
        IMitsubishiPlcTransport Create(PlcConfig config);
    }

    internal sealed class HslMitsubishiPlcTransportFactory : IMitsubishiPlcTransportFactory
    {
        public static HslMitsubishiPlcTransportFactory Instance { get; } = new();

        private HslMitsubishiPlcTransportFactory()
        {
        }

        public IMitsubishiPlcTransport Create(PlcConfig config)
            => new HslMitsubishiPlcTransport(config.IpAddress, config.Port);
    }

    internal sealed class HslMitsubishiPlcTransport : IMitsubishiPlcTransport
    {
        private readonly MelsecA1ENet _client;

        public HslMitsubishiPlcTransport(string ipAddress, int port)
        {
            _client = new MelsecA1ENet(ipAddress, port);
        }

        public int ReceiveTimeOut
        {
            get => _client.ReceiveTimeOut;
            set => _client.ReceiveTimeOut = value;
        }

        public int ConnectTimeOut
        {
            get => _client.ConnectTimeOut;
            set => _client.ConnectTimeOut = value;
        }

        public OperateResult ConnectServer() => _client.ConnectServer();
        public OperateResult ConnectClose() => _client.ConnectClose();

        public void Abort()
        {
            // HslCommunication 12.3 的 PipeTcpNet.CloseCommunication 直接关闭
            // 底层 Socket，不等待通信锁，可从监控线程打断阻塞读。
            _client.CommunicationPipe?.CloseCommunication();
        }

        public OperateResult<bool[]> ReadBool(string address, ushort length) => _client.ReadBool(address, length);
        public OperateResult<short[]> ReadInt16(string address, ushort length) => _client.ReadInt16(address, length);
        public OperateResult<int[]> ReadInt32(string address, ushort length) => _client.ReadInt32(address, length);
    }
}
