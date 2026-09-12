using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using MitsubishiMonitor.Demo.Models;
using MitsubishiMonitor.Demo.Services;
using MitsubishiMonitor.Demo.ViewModels;
using Xunit;

namespace MitsubishiMonitor.Demo.Tests
{
    public class TelemetryPresentationRegressionTests
    {
        private static readonly DateTime Start = new(2026, 9, 12, 9, 0, 0);

        [Fact]
        public void ChartHistoryAndRollingSamples_KeepAllSeriesAndLabelsAligned()
        {
            var chart = new TemperatureChartWindow();
            chart.ReplaceHistory(Enumerable.Range(0, 50).Select(Sample), Start.AddSeconds(490));
            for (var i = 50; i < 62; i++) chart.AppendLive(Sample(i));
            Assert.Equal(60, chart.Temperatures.Count);
            Assert.Equal(60, chart.PhaseA.Count);
            Assert.Equal(60, chart.PhaseB.Count);
            Assert.Equal(60, chart.PhaseC.Count);
            Assert.Equal(60, chart.Labels.Length);
            for (var i = 0; i < 60; i++)
            {
                var expected = Sample(i + 2);
                Assert.Equal(expected.RecordTime.ToString("MM-dd HH:mm:ss"), chart.Labels[i]);
                Assert.Equal((double)expected.Temperature, chart.Temperatures[i]);
                Assert.Equal(expected.HasUsableAuxiliaryData ? (double?)expected.ThermocoupleA : null, chart.PhaseA[i]);
                Assert.Equal(expected.HasUsableAuxiliaryData ? (double?)expected.ThermocoupleB : null, chart.PhaseB[i]);
                Assert.Equal(expected.HasUsableAuxiliaryData ? (double?)expected.ThermocoupleC : null, chart.PhaseC[i]);
            }
        }

        [Fact]
        public void LiveHistoryQuery_PreservesSamplesReceivedWhileQueryWasRunning()
        {
            var chart = new TemperatureChartWindow();
            chart.AppendLive(Sample(50));
            chart.ReplaceHistory(Enumerable.Range(0, 50).Select(Sample), Sample(49).RecordTime);
            Assert.Equal(51, chart.Temperatures.Count);
            Assert.Equal((double)Sample(50).Temperature, chart.Temperatures.Last());
            Assert.Equal(Sample(50).RecordTime.ToString("MM-dd HH:mm:ss"), chart.Labels.Last());
        }

        [Fact]
        public void HistoricalRange_DoesNotMixInLiveSamplesOrPreviousVoltages()
        {
            var chart = new TemperatureChartWindow();
            chart.AppendLive(Sample(50));
            chart.FollowLive = false;
            chart.ReplaceHistory(new[] { Sample(2), Sample(3) }, Sample(3).RecordTime);
            chart.AppendLive(Sample(51));
            Assert.Equal(new double?[] { 42, 43 }, chart.Temperatures);
            Assert.Equal(new double?[] { 2, null }, chart.PhaseA);
            Assert.Equal(2, chart.Labels.Length);
        }

        [Theory]
        [InlineData(true, 1, true)]
        [InlineData(true, 30, false)]
        [InlineData(false, 1, false)]
        [InlineData(true, -1, false)]
        [InlineData(true, 0, true)]
        public void LiveAuxiliaryFreshness_RejectsOldOfflineAndFutureSamples(bool online, int ageSeconds, bool expected)
        {
            var status = new PlcStatus
            {
                IsConnected = online, LastAuxiliarySampleTime = Start.AddSeconds(-ageSeconds),
                TargetTemperature = 0, ThermocoupleA = 0, ThermocoupleB = 0, ThermocoupleC = 0
            };
            Assert.Equal(expected, AuxiliaryTelemetry.IsFresh(status, 10000, Start));
        }

        [Fact]
        public void MissingAuxiliarySample_DoesNotExposeDefaultZeroAsMeasurement()
        {
            var record = new TemperatureLog { HasFreshAuxiliaryData = true };
            Assert.False(record.HasUsableAuxiliaryData);
            Assert.Equal("未采集", record.AuxiliaryQualityDisplay);
            Assert.Equal("—", record.ThermocoupleADisplay);
            Assert.Equal("—", record.TargetTemperatureDisplay);
        }

        [Fact]
        public async Task AllManualExportFormats_PreserveAuxiliaryQualityAndMaskStaleValues()
        {
            using var workspace = new ReviewWorkspace();
            var records = ExportSamples();
            var path = Path.Combine(workspace.Path, "export.xlsx");
            await new ExcelExportService().ExportAllAsync(records, new List<OperationLog>(), path);
            using (var workbook = new XLWorkbook(path))
            {
                var sheet = workbook.Worksheet("温度记录");
                Assert.Equal("—", sheet.Cell(2, 4).GetString());
                Assert.Equal("过期或无效", sheet.Cell(2, 12).GetString());
                Assert.Equal(records[0].AuxiliarySampleTimeDisplay, sheet.Cell(2, 11).GetString());
                Assert.Equal(90d, sheet.Cell(2, 9).GetDouble());
                Assert.Equal(1.5d, sheet.Cell(3, 4).GetDouble());
                Assert.Equal("有效", sheet.Cell(3, 12).GetString());
            }
            var csv = ExcelExportService.BuildTemperatureCsv(records, "device", CancellationToken.None);
            var html = ExcelExportService.BuildReadableHtml("device", Start, Start.AddMinutes(1),
                records, new List<OperationLog>(), CancellationToken.None);
            foreach (var content in new[] { csv, System.Net.WebUtility.HtmlDecode(html) })
            {
                Assert.Contains("辅助采样时间", content);
                Assert.Contains("辅助质量", content);
                Assert.Contains("过期或无效", content);
                Assert.DoesNotContain("123.456", content);
                Assert.Contains("1.500", content);
            }
        }

        [Fact]
        public void AutomaticExport_UsesQualityFormatAndPreservesLegacyFile()
        {
            using var workspace = new ReviewWorkspace();
            var legacyPath = Path.Combine(workspace.Path, "2026-09-12_温度记录.html");
            File.WriteAllText(legacyPath, "legacy seven-column data");
            using (var exporter = new AutoExportService())
            {
                exporter.UpdateExportPath(workspace.Path);
                foreach (var record in ExportSamples()) exporter.AppendTemperatureLog(record);
            }
            var output = File.ReadAllText(Directory.GetFiles(workspace.Path, "*_温度记录_含采样质量.html").Single());
            Assert.Contains("辅助质量", output);
            Assert.Contains("过期或无效", output);
            Assert.DoesNotContain("123.456", output);
            Assert.Equal("legacy seven-column data", File.ReadAllText(legacyPath));
        }

        [Fact]
        public async Task DetailView_StopsDiagnosisWhenAuxiliaryDataExpires_AndRestartsWindow()
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    var plc = new ReviewPlcService();
                    var device = new Device { Id = 2 };
                    var manager = new DeviceManagerService(new[] { new DevicePlcWrapper(device, plc) });
                    try
                    {
                        using var vm = new DeviceDetailViewModel(device, manager, loadHistory: false, startTimer: false);
                        var status = plc.CurrentStatus;
                        status.IsConnected = true;
                        status.TemperatureQuality = TemperatureSampleQuality.Valid;
                        status.LastTemperatureConnectionGeneration = 1;
                        var now = DateTime.Now;
                        for (var i = 0; i < 6; i++)
                        {
                            status.LastTemperatureSampleTime = now.AddSeconds(i - 6);
                            status.LastAuxiliarySampleTime = status.LastTemperatureSampleTime.AddMilliseconds(-10);
                            status.Temperature = 60 + i;
                            vm.UpdatePhaseVoltages();
                        }
                        Assert.Contains("基本未加热", vm.HeatingDiagnosis);
                        status.LastAuxiliarySampleTime = now.AddMinutes(-1);
                        vm.UpdatePhaseVoltages();
                        Assert.Equal("--.- V", vm.PhaseAVoltage);
                        Assert.Equal("--.-°C", vm.TargetTemperatureDisplay);
                        Assert.Contains("数据缺失或过期", vm.HeatingDiagnosis);
                        status.LastTemperatureSampleTime = now;
                        status.LastAuxiliarySampleTime = now.AddMilliseconds(-10);
                        vm.UpdatePhaseVoltages();
                        Assert.Contains("数据采集中", vm.HeatingDiagnosis);
                    }
                    finally { manager.StopMonitoring(); }
                    completion.TrySetResult(true);
                }
                catch (Exception ex) { completion.TrySetException(ex); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static TemperatureLog Sample(int index) => new()
        {
            RecordTime = Start.AddSeconds(index * 10), Temperature = 40 + index,
            AuxiliarySampleTime = Start.AddSeconds(index * 10 - 1), HasFreshAuxiliaryData = index % 2 == 0,
            ThermocoupleA = index, ThermocoupleB = index + 1, ThermocoupleC = index + 2
        };

        private static List<TemperatureLog> ExportSamples() => new()
        {
            new TemperatureLog { RecordTime = Start, Temperature = 70, ThermocoupleA = 123.456f,
                AuxiliarySampleTime = Start.AddMinutes(-1), HasFreshAuxiliaryData = false, AlarmThreshold = 90 },
            new TemperatureLog { RecordTime = Start.AddSeconds(10), Temperature = 71, ThermocoupleA = 1.5f,
                AuxiliarySampleTime = Start.AddSeconds(9), HasFreshAuxiliaryData = true, TargetTemperature = 80, AlarmThreshold = 90 }
        };
    }
}
