using System;
using MitsubishiMonitor.Demo.Models;
using MitsubishiMonitor.Demo.Services;
using Xunit;

namespace MitsubishiMonitor.Demo.Tests
{
    public class PolicyAndConfigurationTests
    {
        [Theory]
        [InlineData(0, 0, false, false, false, TowerLightDecision.Off)]
        [InlineData(4, 4, false, false, false, TowerLightDecision.Green)]
        [InlineData(4, 3, false, false, false, TowerLightDecision.Yellow)]
        [InlineData(4, 4, false, true, false, TowerLightDecision.Yellow)]
        [InlineData(4, 0, true, false, false, TowerLightDecision.RedBuzzerOn)]
        [InlineData(4, 0, true, true, true, TowerLightDecision.RedBuzzerOff)]
        public void TowerLightPolicy_PrioritizesAlarm_AndRequiresAllMonitoredDataFresh(
            int monitored,
            int fresh,
            bool alarm,
            bool systemFault,
            bool muted,
            TowerLightDecision expected)
        {
            Assert.Equal(expected,
                TowerLightPolicy.Decide(monitored, fresh, alarm, systemFault, muted));
        }

        [Fact]
        public void TowerLightPolicy_MixedPower_AutoStandbyDevicesDoNotCreateFalseFaults()
        {
            var states = new[]
            {
                new TowerLightPolicy.DeviceState(DeviceMonitoringMode.AutoStandby, true, true, false),
                new TowerLightPolicy.DeviceState(DeviceMonitoringMode.AutoStandby, false, false, false),
                new TowerLightPolicy.DeviceState(DeviceMonitoringMode.AutoStandby, false, false, false),
                new TowerLightPolicy.DeviceState(DeviceMonitoringMode.AutoStandby, false, false, false)
            };

            Assert.Equal(TowerLightDecision.Green,
                TowerLightPolicy.Decide(states, hasSystemFault: false, isBuzzerMuted: false));

            var allOff = new[]
            {
                new TowerLightPolicy.DeviceState(DeviceMonitoringMode.AutoStandby, false, false, false),
                new TowerLightPolicy.DeviceState(DeviceMonitoringMode.AutoStandby, false, false, false),
                new TowerLightPolicy.DeviceState(DeviceMonitoringMode.Disabled, false, false, false),
                new TowerLightPolicy.DeviceState(DeviceMonitoringMode.Disabled, false, false, false)
            };
            Assert.Equal(TowerLightDecision.Off,
                TowerLightPolicy.Decide(allOff, hasSystemFault: false, isBuzzerMuted: false));
        }

        [Fact]
        public void TowerLightPolicy_RequiredOffline_OnlineStale_AndFreshAlarmRemainActionable()
        {
            var requiredOffline = new[]
            {
                new TowerLightPolicy.DeviceState(DeviceMonitoringMode.RequiredOnline, false, false, false),
                new TowerLightPolicy.DeviceState(DeviceMonitoringMode.AutoStandby, false, false, false)
            };
            Assert.Equal(TowerLightDecision.Yellow,
                TowerLightPolicy.Decide(requiredOffline, false, false));

            var onlineStale = new[]
            {
                new TowerLightPolicy.DeviceState(DeviceMonitoringMode.AutoStandby, true, false, false)
            };
            Assert.Equal(TowerLightDecision.Yellow,
                TowerLightPolicy.Decide(onlineStale, false, false));

            var freshAlarm = new[]
            {
                new TowerLightPolicy.DeviceState(DeviceMonitoringMode.AutoStandby, true, true, true)
            };
            Assert.Equal(TowerLightDecision.RedBuzzerOn,
                TowerLightPolicy.Decide(freshAlarm, false, false));
        }

        [Fact]
        public void DeviceMonitoringPolicy_UsesSlowDiscoveryForStandby_AndDisablesProbing()
        {
            Assert.True(DeviceMonitoringPolicy.IsConnectionAuthorized(DeviceMonitoringMode.AutoStandby));
            Assert.True(DeviceMonitoringPolicy.IsConnectionAuthorized(DeviceMonitoringMode.RequiredOnline));
            Assert.False(DeviceMonitoringPolicy.IsConnectionAuthorized(DeviceMonitoringMode.Disabled));
            Assert.False(DeviceMonitoringPolicy.IsExpectedOnline(DeviceMonitoringMode.AutoStandby));
            Assert.True(DeviceMonitoringPolicy.IsExpectedOnline(DeviceMonitoringMode.RequiredOnline));
            Assert.True(
                DeviceMonitoringPolicy.GetReconnectDelay(DeviceMonitoringMode.AutoStandby, 1) >
                DeviceMonitoringPolicy.GetReconnectDelay(DeviceMonitoringMode.RequiredOnline, 1));
            Assert.Equal(
                System.Threading.Timeout.InfiniteTimeSpan,
                DeviceMonitoringPolicy.GetReconnectDelay(DeviceMonitoringMode.Disabled, 1));
        }

        [Fact]
        public void ReconnectOutcome_OpensGateImmediately_SoBackoffIsAppliedOnlyOnce()
        {
            var state = new DeviceManagerService.ReconnectState { Attempt = 3 };

            var attempt = DeviceManagerService.ApplyReconnectOutcome(
                state,
                restored: false,
                failureReason: "连接超时");

            // 失败后闸门必须立即可调度：退避只由下一次调度任务内的延迟承担。
            // 若这里也推迟一个退避时长，同一延迟会被闸门和任务内等待各消耗一次，
            // 实际重连间隔变成策略值的约两倍（稳态约 120 秒而非 30–60 秒）。
            Assert.Equal(3, attempt);
            Assert.True(
                state.NextRetryTimestamp <= System.Diagnostics.Stopwatch.GetTimestamp(),
                "失败后调度闸门必须立即开放");
            Assert.Null(state.NextRetryAt);

            var restoredAttempt = DeviceManagerService.ApplyReconnectOutcome(
                state,
                restored: true,
                failureReason: "");
            Assert.Equal(3, restoredAttempt);
            Assert.Equal(0, state.NextRetryTimestamp);
            Assert.Null(state.NextRetryAt);
        }

        [Fact]
        public void ConfigurationValidator_AcceptsFourDistinctIpv4Devices()
        {
            var document = ValidDocument();

            Assert.True(AppConfig.TryValidateDocument(document, out var error), error);
            Assert.Equal("192.168.1.5", document.DeviceIPs[0]);
            Assert.Equal(4, document.DeviceMonitoringModes.Length);
            Assert.All(document.DeviceMonitoringModes,
                mode => Assert.Equal(DeviceMonitoringMode.AutoStandby, mode));
        }

        [Fact]
        public void ConfigurationValidator_MigratesLegacyMissingThresholdsAndModes()
        {
            var legacy = ValidDocument();
            legacy.DeviceThresholds = Array.Empty<float>();
            legacy.DeviceMonitoringModes = Array.Empty<DeviceMonitoringMode>();
            Assert.True(AppConfig.TryValidateDocument(legacy, out var legacyError), legacyError);
            Assert.Equal(new[] { 90f, 90f, 90f, 90f }, legacy.DeviceThresholds);
            Assert.Equal(4, legacy.DeviceMonitoringModes.Length);
            Assert.All(legacy.DeviceMonitoringModes,
                mode => Assert.Equal(DeviceMonitoringMode.AutoStandby, mode));

            var wrongThresholdLength = ValidDocument();
            wrongThresholdLength.DeviceThresholds = new[] { 90f };
            Assert.False(AppConfig.TryValidateDocument(wrongThresholdLength, out var thresholdLengthError));
            Assert.Contains("DeviceThresholds", thresholdLengthError);
        }

        [Fact]
        public void ConfigurationValidator_RejectsInvalidMonitoringModes()
        {
            var wrongLength = ValidDocument();
            wrongLength.DeviceMonitoringModes = new[] { DeviceMonitoringMode.AutoStandby };
            Assert.False(AppConfig.TryValidateDocument(wrongLength, out var lengthError));
            Assert.Contains("恰好包含 4", lengthError);

            var undefined = ValidDocument();
            undefined.DeviceMonitoringModes = new[]
            {
                DeviceMonitoringMode.AutoStandby,
                DeviceMonitoringMode.RequiredOnline,
                DeviceMonitoringMode.Disabled,
                (DeviceMonitoringMode)99
            };
            Assert.False(AppConfig.TryValidateDocument(undefined, out var modeError));
            Assert.Contains("运行模式无效", modeError);
        }

        [Fact]
        public void ConfigurationValidator_RejectsDuplicateIpv4_NonFiniteThreshold_AndRelativePath()
        {
            var duplicate = ValidDocument();
            duplicate.DeviceIPs[3] = duplicate.DeviceIPs[0];
            Assert.False(AppConfig.TryValidateDocument(duplicate, out var duplicateError));
            Assert.Contains("不能重复", duplicateError);

            var nonFinite = ValidDocument();
            nonFinite.DeviceThresholds[1] = float.NaN;
            Assert.False(AppConfig.TryValidateDocument(nonFinite, out var thresholdError));
            Assert.Contains("有限数值", thresholdError);

            var relativePath = ValidDocument();
            relativePath.DatabasePath = "Data\\Monitor.db";
            Assert.False(AppConfig.TryValidateDocument(relativePath, out var pathError));
            Assert.Contains("绝对路径", pathError);
        }

        [Fact]
        public void ConfigurationValidator_RejectsIpv6_AndUncSqlitePath()
        {
            var ipv6 = ValidDocument();
            ipv6.DeviceIPs[0] = "::1";
            Assert.False(AppConfig.TryValidateDocument(ipv6, out var ipError));
            Assert.Contains("IP 无效", ipError);

            var networkDatabase = ValidDocument();
            networkDatabase.DatabasePath = @"\\server\share\Monitor.db";
            Assert.False(AppConfig.TryValidateDocument(networkDatabase, out var databaseError));
            Assert.Contains("UNC", databaseError);
        }

        [Theory]
        [InlineData(PlcRegisterDataType.Int16, -125, -12.5f)]
        [InlineData(PlcRegisterDataType.UInt16, 845, 84.5f)]
        [InlineData(PlcRegisterDataType.Int32, 502, 50.2f)]
        public void TemperatureRegisterDefinition_ConvertsTypedRawValue(
            PlcRegisterDataType dataType,
            long rawValue,
            float expected)
        {
            var definition = new TemperatureRegisterDefinition
            {
                Address = "D10",
                DataType = dataType,
                Divisor = 10f,
                MinimumValid = -100f,
                MaximumValid = 500f
            };

            Assert.True(definition.TryConvert(rawValue, out var value, out var reason), reason);
            Assert.Equal(expected, value);
        }

        [Fact]
        public void TemperatureRegisterDefinition_RejectsInvalidScaleAndRange()
        {
            var zeroDivisor = new TemperatureRegisterDefinition { Address = "D10", Divisor = 0f };
            Assert.False(zeroDivisor.TryConvert(100, out _, out var divisorReason));
            Assert.Contains("除数", divisorReason);

            var invertedRange = new TemperatureRegisterDefinition
            {
                Address = "D10",
                MinimumValid = 100f,
                MaximumValid = 50f
            };
            Assert.False(invertedRange.TryConvert(100, out _, out var rangeReason));
            Assert.Contains("范围", rangeReason);
        }

        private static AppConfig.ConfigurationDocument ValidDocument()
            => new()
            {
                DatabasePath = "",
                AutoExportPath = "",
                DeviceIPs = new[]
                {
                    "192.168.1.5",
                    "192.168.1.10",
                    "192.168.1.15",
                    "192.168.1.20"
                },
                DeviceThresholds = new[] { 90f, 90f, 90f, 90f },
                DeviceMonitoringModes = new[]
                {
                    DeviceMonitoringMode.AutoStandby,
                    DeviceMonitoringMode.AutoStandby,
                    DeviceMonitoringMode.AutoStandby,
                    DeviceMonitoringMode.AutoStandby
                }
            };
    }
}
