using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using MitsubishiMonitor.Demo.Models;
using MitsubishiMonitor.Demo.Services;
using Xunit;

namespace MitsubishiMonitor.Demo.Tests
{
    public class PlcConnectionTransitionPolicyTests
    {
        [Fact]
        public void SameGeneration_MatrixContainsNoUndeclaredTransition()
        {
            var forwardSteps = new HashSet<(PlcConnectionPhase Current, PlcConnectionPhase Next)>
            {
                (PlcConnectionPhase.TcpConnecting, PlcConnectionPhase.ProtocolVerifying),
                (PlcConnectionPhase.ProtocolVerifying, PlcConnectionPhase.AwaitingFirstSample),
                (PlcConnectionPhase.AwaitingFirstSample, PlcConnectionPhase.OnlineFresh)
            };

            foreach (var current in Enum.GetValues<PlcConnectionPhase>())
            foreach (var next in Enum.GetValues<PlcConnectionPhase>())
            {
                var expected = current == next || forwardSteps.Contains((current, next));
                Assert.Equal(
                    expected,
                    PlcConnectionTransitionPolicy.CanTransition(7, current, 7, next));
            }
        }

        [Fact]
        public void NextGeneration_MatrixContainsOnlySessionBoundaryEntries()
        {
            var boundaryEntries = new HashSet<PlcConnectionPhase>
            {
                PlcConnectionPhase.TcpConnecting,
                PlcConnectionPhase.Disconnected,
                PlcConnectionPhase.CommunicationFault,
                PlcConnectionPhase.Disposed
            };

            foreach (var current in Enum.GetValues<PlcConnectionPhase>())
            foreach (var next in Enum.GetValues<PlcConnectionPhase>())
            {
                var expected = current != PlcConnectionPhase.Disposed &&
                               boundaryEntries.Contains(next);
                Assert.Equal(
                    expected,
                    PlcConnectionTransitionPolicy.CanTransition(7, current, 8, next));
            }
        }

        [Theory]
        [InlineData(PlcConnectionPhase.TcpConnecting, PlcConnectionPhase.ProtocolVerifying)]
        [InlineData(PlcConnectionPhase.ProtocolVerifying, PlcConnectionPhase.AwaitingFirstSample)]
        [InlineData(PlcConnectionPhase.AwaitingFirstSample, PlcConnectionPhase.OnlineFresh)]
        public void SameGeneration_AllowsOnlyDeclaredLifecycleSteps(
            PlcConnectionPhase current,
            PlcConnectionPhase next)
        {
            Assert.True(PlcConnectionTransitionPolicy.CanTransition(7, current, 7, next));
        }

        [Theory]
        [InlineData(PlcConnectionPhase.Disconnected, PlcConnectionPhase.TcpConnecting)]
        [InlineData(PlcConnectionPhase.Disconnected, PlcConnectionPhase.OnlineFresh)]
        [InlineData(PlcConnectionPhase.TcpConnecting, PlcConnectionPhase.OnlineFresh)]
        [InlineData(PlcConnectionPhase.ProtocolVerifying, PlcConnectionPhase.OnlineFresh)]
        [InlineData(PlcConnectionPhase.OnlineFresh, PlcConnectionPhase.ProtocolVerifying)]
        [InlineData(PlcConnectionPhase.OnlineFresh, PlcConnectionPhase.CommunicationFault)]
        [InlineData(PlcConnectionPhase.CommunicationFault, PlcConnectionPhase.TcpConnecting)]
        [InlineData(PlcConnectionPhase.Disposed, PlcConnectionPhase.Disconnected)]
        public void SameGeneration_RejectsSkippedOrBackwardSteps(
            PlcConnectionPhase current,
            PlcConnectionPhase next)
        {
            Assert.False(PlcConnectionTransitionPolicy.CanTransition(7, current, 7, next));
        }

        [Theory]
        [InlineData(PlcConnectionPhase.TcpConnecting)]
        [InlineData(PlcConnectionPhase.Disconnected)]
        [InlineData(PlcConnectionPhase.CommunicationFault)]
        [InlineData(PlcConnectionPhase.Disposed)]
        public void NextGeneration_AllowsOnlySessionBoundaryEntries(PlcConnectionPhase next)
        {
            Assert.True(PlcConnectionTransitionPolicy.CanTransition(
                7,
                PlcConnectionPhase.OnlineFresh,
                8,
                next));
        }

        [Fact]
        public void Generation_RejectsOldResultsSkippedGenerationsAndDisposedRestart()
        {
            Assert.False(PlcConnectionTransitionPolicy.CanTransition(
                7,
                PlcConnectionPhase.OnlineFresh,
                6,
                PlcConnectionPhase.OnlineFresh));
            Assert.False(PlcConnectionTransitionPolicy.CanTransition(
                7,
                PlcConnectionPhase.OnlineFresh,
                9,
                PlcConnectionPhase.TcpConnecting));
            Assert.False(PlcConnectionTransitionPolicy.CanTransition(
                7,
                PlcConnectionPhase.Disposed,
                8,
                PlcConnectionPhase.TcpConnecting));
        }

        [Fact]
        public async Task DemoService_UsesTheSameOrderedLifecycleAndAdvancesGenerationOnDisconnect()
        {
            var service = new DemoPlcService(
                new PlcConfig { Name = "演示状态机测试" },
                deviceId: 1);
            var phases = new ConcurrentQueue<PlcConnectionPhase>();
            service.ConnectionStateChangedDetailed += (_, args) => phases.Enqueue(args.Snapshot.Phase);

            Assert.True(await service.ConnectAsync());
            var connectedGeneration = service.ConnectionSnapshot.Generation;

            Assert.Equal(
                new[]
                {
                    PlcConnectionPhase.TcpConnecting,
                    PlcConnectionPhase.ProtocolVerifying,
                    PlcConnectionPhase.AwaitingFirstSample,
                    PlcConnectionPhase.OnlineFresh
                },
                phases.ToArray());
            Assert.True(service.ConnectionSnapshot.IsDataFresh);
            Assert.True(service.CurrentStatus.IsConnected);

            service.Disconnect();

            Assert.Equal(connectedGeneration + 1, service.ConnectionSnapshot.Generation);
            Assert.Equal(PlcConnectionPhase.Disconnected, service.ConnectionSnapshot.Phase);
            Assert.False(service.CurrentStatus.IsConnected);
        }
    }
}
