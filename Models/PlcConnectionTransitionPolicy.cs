namespace MitsubishiMonitor.Demo.Models
{
    /// <summary>
    /// PLC 连接生命周期的唯一合法转移表。
    /// 同一连接代次只能按协议握手、首样本和新鲜度顺序推进；
    /// 新代次只能从“新建会话”或“废弃旧会话”的边界状态进入。
    /// </summary>
    internal static class PlcConnectionTransitionPolicy
    {
        public static bool CanTransition(
            long currentGeneration,
            PlcConnectionPhase currentPhase,
            long nextGeneration,
            PlcConnectionPhase nextPhase)
        {
            if (currentGeneration < 0 || nextGeneration < currentGeneration)
                return false;

            if (nextGeneration == currentGeneration)
            {
                if (currentPhase == nextPhase)
                    return true;

                return CanTransitionWithinGeneration(currentPhase, nextPhase);
            }

            // 每次建立新会话或废弃旧会话只允许推进一个代次，禁止跳代和旧代倒灌。
            if (nextGeneration != currentGeneration + 1 ||
                currentPhase == PlcConnectionPhase.Disposed)
                return false;

            return nextPhase is PlcConnectionPhase.TcpConnecting
                or PlcConnectionPhase.Disconnected
                or PlcConnectionPhase.CommunicationFault
                or PlcConnectionPhase.Disposed;
        }

        private static bool CanTransitionWithinGeneration(
            PlcConnectionPhase currentPhase,
            PlcConnectionPhase nextPhase)
        {
            return currentPhase switch
            {
                PlcConnectionPhase.TcpConnecting =>
                    nextPhase == PlcConnectionPhase.ProtocolVerifying,
                PlcConnectionPhase.ProtocolVerifying =>
                    nextPhase == PlcConnectionPhase.AwaitingFirstSample,
                PlcConnectionPhase.AwaitingFirstSample =>
                    nextPhase == PlcConnectionPhase.OnlineFresh,
                _ => false
            };
        }
    }
}
