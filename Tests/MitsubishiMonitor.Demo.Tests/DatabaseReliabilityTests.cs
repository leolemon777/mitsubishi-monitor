using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MitsubishiMonitor.Demo.Data;
using MitsubishiMonitor.Demo.Models;
using MitsubishiMonitor.Demo.Services;
using Xunit;

namespace MitsubishiMonitor.Demo.Tests
{
    public class DatabaseReliabilityTests
    {
        [Fact]
        public void LegacySchema_IsUpgradedTransactionally_AndUpgradeIsIdempotent()
        {
            using var workspace = new TemporaryWorkspace();
            var databasePath = Path.Combine(workspace.Path, "legacy.db");
            CreateLegacyDatabase(databasePath);

            using (var context = new MonitorDbContext(databasePath))
            {
                context.EnsureSchemaUpgraded();
                context.EnsureSchemaUpgraded();

                context.TemperatureLogs.Add(new TemperatureLog
                {
                    DeviceId = 2,
                    DeviceName = "设备2",
                    Temperature = 63.5f,
                    RecordTime = DateTime.Now,
                    Threshold = 90f,
                    AlarmThreshold = 88f,
                    TargetTemperature = 72f,
                    AuxiliarySampleTime = DateTime.Now.AddSeconds(-1),
                    HasFreshAuxiliaryData = true
                });
                context.SaveChanges();
            }

            using var connection = new SqliteConnection($"Data Source={databasePath}");
            connection.Open();
            var columns = ReadColumns(connection, "TemperatureLog");
            Assert.Contains("AlarmThreshold", columns);
            Assert.Contains("TargetTemperature", columns);
            Assert.Contains("AuxiliarySampleTime", columns);
            Assert.Contains("HasFreshAuxiliaryData", columns);
            Assert.Equal(2L, ExecuteScalar<long>(connection,
                "SELECT Value FROM SchemaInfo WHERE Key='SchemaVersion';"));
            Assert.Equal(1L, ExecuteScalar<long>(connection,
                "SELECT COUNT(*) FROM TemperatureLog WHERE DeviceId=2 AND AlarmThreshold=88;"));
        }

        [Fact]
        public void DurableSpool_PeekDoesNotDeleteUntilAcknowledged_AndSurvivesRestart()
        {
            using var workspace = new TemporaryWorkspace();
            var databasePath = Path.Combine(workspace.Path, "monitor.db");
            var spool = new DurableLogSpool(databasePath);
            Assert.True(spool.TryAppend(new OperationLog { DeviceId = 1, LogType = "X", PointAddress = "X0" }));
            Assert.True(spool.TryAppend(new OperationLog { DeviceId = 1, LogType = "Y", PointAddress = "Y0" }));

            var firstPeek = spool.PeekOperations(1);
            Assert.Single(firstPeek);
            Assert.Equal(2, spool.PendingCount);

            var restartedBeforeAck = new DurableLogSpool(databasePath);
            Assert.Equal(2, restartedBeforeAck.PendingCount);
            Assert.Single(restartedBeforeAck.PeekOperations(1));

            restartedBeforeAck.AcknowledgeOperations(1);
            var restartedAfterAck = new DurableLogSpool(databasePath);
            Assert.Equal(1, restartedAfterAck.PendingCount);
            Assert.Single(restartedAfterAck.PeekOperations(10));
        }

        [Fact]
        public void SafeEventDispatcher_ContinuesAfterOneSubscriberThrows()
        {
            var successfulSubscriberCalled = false;
            EventHandler<int> handlers = (_, _) => throw new InvalidOperationException("subscriber fault");
            handlers += (_, value) => successfulSubscriberCalled = value == 42;

            SafeEventDispatcher.Invoke(this, handlers, 42);

            Assert.True(successfulSubscriberCalled);
        }

        [Fact]
        public async Task GlobalQueries_ReturnTrueLatestRows_StableOrder_AndSqlStatistics()
        {
            using var workspace = new TemporaryWorkspace();
            var databasePath = Path.Combine(workspace.Path, "queries.db");
            using var dataService = new DataService(databasePath);
            await dataService.InitializeAsync();
            var start = new DateTime(2026, 8, 1, 8, 0, 0, DateTimeKind.Local);

            using (var context = new MonitorDbContext(databasePath))
            {
                for (var i = 0; i < 6; i++)
                {
                    context.OperationLogs.Add(new OperationLog
                    {
                        DeviceId = i % 2 + 1,
                        DeviceName = $"设备{i % 2 + 1}",
                        PointAddress = $"X{i}",
                        LogTime = start.AddMinutes(i)
                    });
                    context.TemperatureLogs.Add(new TemperatureLog
                    {
                        DeviceId = i % 2 + 1,
                        DeviceName = $"设备{i % 2 + 1}",
                        Temperature = 10 + i,
                        IsAbnormal = i >= 4,
                        RecordTime = start.AddMinutes(i)
                    });
                }
                await context.SaveChangesAsync();
            }

            var operations = await dataService.GetOperationLogsPagedAsync(
                null, start, start.AddHours(1), 0, 3);
            Assert.Equal(new[] { "X5", "X4", "X3" }, operations.Select(log => log.PointAddress));

            var temperatures = await dataService.GetTemperatureLogsPagedAsync(
                null, start, start.AddHours(1), 0, 3);
            Assert.Equal(new[] { 13f, 14f, 15f }, temperatures.Select(log => log.Temperature));

            var statistics = await dataService.GetTemperatureStatisticsAsync(
                null, start, start.AddHours(1));
            Assert.Equal(6, statistics.Count);
            Assert.Equal(2, statistics.AbnormalCount);
            Assert.Equal(10f, statistics.Minimum);
            Assert.Equal(15f, statistics.Maximum);
            Assert.Equal(12.5f, statistics.Average);

            var exportData = await BoundedLogExportLoader.LoadAsync(
                dataService,
                null,
                start,
                start.AddHours(1),
                includeTemperature: true,
                includeOperation: true);
            Assert.Equal(6, exportData.TemperatureLogs.Count);
            Assert.Equal(6, exportData.OperationLogs.Count);
            Assert.Equal(10f, exportData.TemperatureLogs.First().Temperature);
            Assert.Equal("X5", exportData.OperationLogs.First().PointAddress);
        }

        [Fact]
        public async Task DatabaseQuery_HonorsCancellationToken()
        {
            using var workspace = new TemporaryWorkspace();
            var databasePath = Path.Combine(workspace.Path, "cancel.db");
            using var dataService = new DataService(databasePath);
            await dataService.InitializeAsync();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                dataService.GetOperationLogsPagedAsync(
                    null,
                    DateTime.Today.AddDays(-1),
                    DateTime.Now,
                    0,
                    100,
                    cancellation.Token));
        }

        [Fact]
        public async Task ExcelExport_EscapesFormulaText_AndLeavesNoPartialFile()
        {
            using var workspace = new TemporaryWorkspace();
            var outputPath = Path.Combine(workspace.Path, "safe.xlsx");
            var service = new ExcelExportService();
            await service.ExportOperationLogsAsync(
                new System.Collections.Generic.List<OperationLog>
                {
                    new()
                    {
                        DeviceId = 1,
                        Description = "=HYPERLINK(\"https://invalid.example\",\"点击\")",
                        LogTime = new DateTime(2026, 8, 1, 8, 0, 0)
                    }
                },
                outputPath);

            using var workbook = new XLWorkbook(outputPath);
            var descriptionCell = workbook.Worksheet("操作日志").Cell(2, 6);
            Assert.Equal("=HYPERLINK(\"https://invalid.example\",\"点击\")",
                descriptionCell.GetString());
            Assert.False(descriptionCell.HasFormula);
            Assert.True(descriptionCell.Style.IncludeQuotePrefix);
            Assert.Empty(Directory.GetFiles(workspace.Path, "*.tmp-*"));
        }

        private static void CreateLegacyDatabase(string databasePath)
        {
            using var connection = new SqliteConnection($"Data Source={databasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
CREATE TABLE TemperatureLog (
    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    DeviceId INTEGER NOT NULL,
    Temperature REAL NOT NULL,
    ThermocoupleA REAL NOT NULL DEFAULT 0,
    ThermocoupleB REAL NOT NULL DEFAULT 0,
    ThermocoupleC REAL NOT NULL DEFAULT 0,
    RecordTime TEXT NOT NULL,
    IsAbnormal INTEGER NOT NULL,
    Threshold REAL NOT NULL DEFAULT 90
);
CREATE TABLE OperationLog (
    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    DeviceId INTEGER NOT NULL,
    LogType TEXT NOT NULL,
    PointAddress TEXT,
    Action TEXT,
    Description TEXT,
    LogTime TEXT NOT NULL
);";
            command.ExecuteNonQuery();
        }

        private static string[] ReadColumns(SqliteConnection connection, string table)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({table});";
            using var reader = command.ExecuteReader();
            var columns = new System.Collections.Generic.List<string>();
            while (reader.Read())
                columns.Add(reader.GetString(1));
            return columns.ToArray();
        }

        private static T ExecuteScalar<T>(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (T)Convert.ChangeType(command.ExecuteScalar(), typeof(T));
        }

        private sealed class TemporaryWorkspace : IDisposable
        {
            public TemporaryWorkspace()
            {
                Path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "MitsubishiMonitor.Tests",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            public void Dispose()
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
        }
    }
}
