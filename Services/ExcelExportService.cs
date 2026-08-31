using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using MitsubishiMonitor.Demo.Models;

namespace MitsubishiMonitor.Demo.Services
{
    /// <summary>
    /// Excel / CSV / HTML 导出服务。所有最终文件均先写入同目录临时文件，成功后再原子替换；
    /// 导出行数受统一上限约束，避免 UI 进程因超大工作簿耗尽内存。
    /// </summary>
    public class ExcelExportService
    {
        private const int CancellationCheckInterval = 512;

        public Task<string> ExportTemperatureLogsAsync(
            List<TemperatureLog> logs,
            string filePath = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(logs);
            if (logs.Count == 0)
                throw new InvalidOperationException("没有数据可导出");
            EnsureSafeRowCount(logs.Count, 0);

            filePath ??= GetDefaultFilePath("温度日志");
            return CreateWorkbookAsync(filePath, cancellationToken, (workbook, token) =>
            {
                var sheet = workbook.Worksheets.Add("温度日志");
                WriteHeader(sheet, new[] { "序号", "设备ID", "温度(°C)", "是否异常", "记录时间" }, "#00BCD4");
                for (var i = 0; i < logs.Count; i++)
                {
                    CheckCancellation(i, token);
                    var log = logs[i];
                    var row = i + 2;
                    sheet.Cell(row, 1).Value = i + 1;
                    sheet.Cell(row, 2).Value = log.DeviceId;
                    sheet.Cell(row, 3).Value = log.Temperature;
                    SetSafeText(sheet.Cell(row, 4), log.IsAbnormal ? "是" : "否");
                    sheet.Cell(row, 5).Value = log.RecordTime;
                    sheet.Cell(row, 5).Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";
                    if (log.IsAbnormal)
                        sheet.Cell(row, 3).Style.Font.FontColor = XLColor.Red;
                }
                FinishDataSheet(sheet, new[] { 9d, 12d, 14d, 12d, 22d });
            });
        }

        public Task<string> ExportOperationLogsAsync(
            List<OperationLog> logs,
            string filePath = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(logs);
            if (logs.Count == 0)
                throw new InvalidOperationException("没有数据可导出");
            EnsureSafeRowCount(0, logs.Count);

            filePath ??= GetDefaultFilePath("操作日志");
            return CreateWorkbookAsync(filePath, cancellationToken, (workbook, token) =>
            {
                var sheet = workbook.Worksheets.Add("操作日志");
                WriteHeader(sheet, new[] { "序号", "时间", "类型", "地址", "动作", "描述", "操作员" }, "#00BCD4");
                for (var i = 0; i < logs.Count; i++)
                {
                    CheckCancellation(i, token);
                    var log = logs[i];
                    var row = i + 2;
                    sheet.Cell(row, 1).Value = i + 1;
                    sheet.Cell(row, 2).Value = log.LogTime;
                    sheet.Cell(row, 2).Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";
                    SetSafeText(sheet.Cell(row, 3), log.LogType);
                    SetSafeText(sheet.Cell(row, 4), log.PointAddress);
                    SetSafeText(sheet.Cell(row, 5), log.Action);
                    SetSafeText(sheet.Cell(row, 6), log.Description);
                    SetSafeText(sheet.Cell(row, 7), log.Operator);
                }
                FinishDataSheet(sheet, new[] { 9d, 22d, 10d, 12d, 12d, 48d, 16d });
            });
        }

        public Task<string> ExportAllAsync(
            List<TemperatureLog> tempLogs,
            List<OperationLog> opLogs,
            string filePath = null,
            CancellationToken cancellationToken = default)
        {
            ValidateLists(tempLogs, opLogs);
            filePath ??= GetDefaultFilePath("监控数据");
            return CreateWorkbookAsync(filePath, cancellationToken, (workbook, token) =>
            {
                WriteTemperatureSheet(workbook, tempLogs, null, token);
                WriteOperationSheet(workbook, opLogs, null, token);
            });
        }

        public Task<string> ExportDeviceDataAsync(
            Device device,
            List<TemperatureLog> tempLogs,
            List<OperationLog> opLogs,
            string filePath = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(device);
            ValidateLists(tempLogs, opLogs);
            filePath ??= GetDefaultFilePath($"{device.Name}_{DateTime.Now:yyyyMMdd_HHmmss}");

            return CreateWorkbookAsync(filePath, cancellationToken, (workbook, token) =>
            {
                var infoSheet = workbook.Worksheets.Add("设备信息");
                SetSafeText(infoSheet.Cell(1, 1), "设备名称");
                SetSafeText(infoSheet.Cell(1, 2), device.Name);
                SetSafeText(infoSheet.Cell(2, 1), "设备位置");
                SetSafeText(infoSheet.Cell(2, 2), device.Location);
                SetSafeText(infoSheet.Cell(3, 1), "IP地址");
                SetSafeText(infoSheet.Cell(3, 2), device.IpAddress);
                SetSafeText(infoSheet.Cell(4, 1), "导出时间");
                infoSheet.Cell(4, 2).Value = DateTime.Now;
                infoSheet.Cell(4, 2).Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";
                infoSheet.Column(1).Width = 14;
                infoSheet.Column(2).Width = 36;

                WriteTemperatureSheet(workbook, tempLogs, device.Name, token);
                WriteOperationSheet(workbook, opLogs, device.Name, token);
            });
        }

        public async Task<string> ExportDeviceReadablePackageAsync(
            Device device,
            List<TemperatureLog> tempLogs,
            List<OperationLog> opLogs,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(device);
            ValidateLists(tempLogs, opLogs);
            var finalDirectory = GetUniquePackageDirectory($"{device.Name}_{DateTime.Now:yyyyMMdd_HHmmss}");
            var stagingDirectory = finalDirectory + ".partial-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(stagingDirectory);

            try
            {
                await ExportDeviceDataAsync(
                    device,
                    tempLogs,
                    opLogs,
                    Path.Combine(stagingDirectory, "Excel备份.xlsx"),
                    cancellationToken).ConfigureAwait(false);

                var tempCsvTask = WriteUtf8BomAtomicallyAsync(
                    Path.Combine(stagingDirectory, "温度记录.csv"),
                    BuildTemperatureCsv(tempLogs, device.Name, cancellationToken),
                    cancellationToken);
                var operationCsvTask = WriteUtf8BomAtomicallyAsync(
                    Path.Combine(stagingDirectory, "操作日志.csv"),
                    BuildOperationCsv(opLogs, device.Name, cancellationToken),
                    cancellationToken);
                var htmlTask = WriteUtf8BomAtomicallyAsync(
                    Path.Combine(stagingDirectory, "日志查看.html"),
                    BuildReadableHtml(
                        device.Name,
                        DateTime.MinValue,
                        DateTime.MinValue,
                        tempLogs,
                        opLogs,
                        cancellationToken,
                        device),
                    cancellationToken);
                await Task.WhenAll(tempCsvTask, operationCsvTask, htmlTask).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                Directory.Move(stagingDirectory, finalDirectory);
                return Path.Combine(finalDirectory, "日志查看.html");
            }
            catch
            {
                TryDeleteDirectory(stagingDirectory);
                throw;
            }
        }

        public Task<string> ExportLogsAsync(
            string deviceLabel,
            DateTime startTime,
            DateTime endTime,
            List<TemperatureLog> tempLogs,
            List<OperationLog> opLogs,
            string filePath = null,
            CancellationToken cancellationToken = default)
        {
            ValidateLists(tempLogs, opLogs);
            var safeDeviceLabel = SanitizeFileName(deviceLabel);
            filePath ??= GetDefaultFilePath($"日志_{safeDeviceLabel}_{DateTime.Now:yyyyMMdd_HHmmss}");

            return CreateWorkbookAsync(filePath, cancellationToken, (workbook, token) =>
            {
                var infoSheet = workbook.Worksheets.Add("查询条件");
                WriteInfoRow(infoSheet, 1, "设备范围", deviceLabel);
                WriteInfoRow(infoSheet, 2, "时间范围（起）", startTime.ToString("yyyy-MM-dd HH:mm:ss"));
                WriteInfoRow(infoSheet, 3, "时间范围（止）", endTime.ToString("yyyy-MM-dd HH:mm:ss"));
                WriteInfoRow(infoSheet, 4, "温度记录条数", tempLogs.Count.ToString("N0"));
                WriteInfoRow(infoSheet, 5, "操作日志条数", opLogs.Count.ToString("N0"));
                WriteInfoRow(infoSheet, 6, "导出时间", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                infoSheet.Column(1).Width = 18;
                infoSheet.Column(2).Width = 40;

                WriteTemperatureSheet(workbook, tempLogs, "", token);
                WriteOperationSheet(workbook, opLogs, "", token);
            });
        }

        public async Task<string> ExportLogsReadablePackageAsync(
            string deviceLabel,
            DateTime startTime,
            DateTime endTime,
            List<TemperatureLog> tempLogs,
            List<OperationLog> opLogs,
            CancellationToken cancellationToken = default)
        {
            ValidateLists(tempLogs, opLogs);
            var safeDeviceLabel = SanitizeFileName(deviceLabel);
            var finalDirectory = GetUniquePackageDirectory(
                $"日志_{safeDeviceLabel}_{DateTime.Now:yyyyMMdd_HHmmss}");
            var stagingDirectory = finalDirectory + ".partial-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(stagingDirectory);

            try
            {
                await ExportLogsAsync(
                    deviceLabel,
                    startTime,
                    endTime,
                    tempLogs,
                    opLogs,
                    Path.Combine(stagingDirectory, "Excel备份.xlsx"),
                    cancellationToken).ConfigureAwait(false);

                var tempCsvTask = WriteUtf8BomAtomicallyAsync(
                    Path.Combine(stagingDirectory, "温度记录.csv"),
                    BuildTemperatureCsv(tempLogs, "", cancellationToken),
                    cancellationToken);
                var operationCsvTask = WriteUtf8BomAtomicallyAsync(
                    Path.Combine(stagingDirectory, "操作日志.csv"),
                    BuildOperationCsv(opLogs, "", cancellationToken),
                    cancellationToken);
                var htmlTask = WriteUtf8BomAtomicallyAsync(
                    Path.Combine(stagingDirectory, "日志查看.html"),
                    BuildReadableHtml(
                        deviceLabel,
                        startTime,
                        endTime,
                        tempLogs,
                        opLogs,
                        cancellationToken),
                    cancellationToken);
                await Task.WhenAll(tempCsvTask, operationCsvTask, htmlTask).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                Directory.Move(stagingDirectory, finalDirectory);
                return Path.Combine(finalDirectory, "日志查看.html");
            }
            catch
            {
                TryDeleteDirectory(stagingDirectory);
                throw;
            }
        }

        private static async Task<string> CreateWorkbookAsync(
            string filePath,
            CancellationToken cancellationToken,
            Action<XLWorkbook, CancellationToken> populate)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            var absolutePath = Path.GetFullPath(filePath);
            var directory = Path.GetDirectoryName(absolutePath)
                ?? throw new InvalidOperationException("无法确定导出目录");
            Directory.CreateDirectory(directory);

            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var workbook = new XLWorkbook();
                populate(workbook, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                SaveWorkbookAtomically(workbook, absolutePath, cancellationToken);
            }, cancellationToken).ConfigureAwait(false);
            return absolutePath;
        }

        private static void SaveWorkbookAtomically(
            XLWorkbook workbook,
            string finalPath,
            CancellationToken cancellationToken)
        {
            var directory = Path.GetDirectoryName(finalPath)
                ?? throw new InvalidOperationException("无法确定导出目录");
            var extension = Path.GetExtension(finalPath);
            var temporaryPath = Path.Combine(
                directory,
                Path.GetFileNameWithoutExtension(finalPath) +
                ".tmp-" + Guid.NewGuid().ToString("N") + extension);
            try
            {
                workbook.SaveAs(temporaryPath);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, finalPath, overwrite: true);
            }
            finally
            {
                TryDeleteFile(temporaryPath);
            }
        }

        private static void WriteTemperatureSheet(
            XLWorkbook workbook,
            List<TemperatureLog> logs,
            string fallbackDeviceName,
            CancellationToken cancellationToken)
        {
            var sheet = workbook.Worksheets.Add("温度记录");
            WriteHeader(
                sheet,
                new[] { "序号", "设备名", "温度(°C)", "热电偶A(V)", "热电偶B(V)", "热电偶C(V)", "是否异常", "记录时间" },
                "#F0883E");

            for (var i = 0; i < logs.Count; i++)
            {
                CheckCancellation(i, cancellationToken);
                var log = logs[i];
                var row = i + 2;
                var deviceName = string.IsNullOrEmpty(log.DeviceName) ? fallbackDeviceName : log.DeviceName;
                sheet.Cell(row, 1).Value = i + 1;
                SetSafeText(sheet.Cell(row, 2), deviceName);
                sheet.Cell(row, 3).Value = log.Temperature;
                sheet.Cell(row, 4).Value = log.ThermocoupleA;
                sheet.Cell(row, 5).Value = log.ThermocoupleB;
                sheet.Cell(row, 6).Value = log.ThermocoupleC;
                SetSafeText(sheet.Cell(row, 7), log.IsAbnormal ? "是" : "否");
                sheet.Cell(row, 8).Value = log.RecordTime;
                sheet.Cell(row, 8).Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";
                if (log.IsAbnormal)
                    sheet.Cell(row, 3).Style.Font.FontColor = XLColor.Red;
            }

            FinishDataSheet(sheet, new[] { 9d, 22d, 14d, 14d, 14d, 14d, 12d, 22d });
        }

        private static void WriteOperationSheet(
            XLWorkbook workbook,
            List<OperationLog> logs,
            string fallbackDeviceName,
            CancellationToken cancellationToken)
        {
            var sheet = workbook.Worksheets.Add("操作日志");
            WriteHeader(
                sheet,
                new[] { "序号", "时间", "设备名", "类型", "地址", "中文点位", "动作", "描述", "操作员" },
                "#4CAF50");

            for (var i = 0; i < logs.Count; i++)
            {
                CheckCancellation(i, cancellationToken);
                var log = logs[i];
                var row = i + 2;
                var deviceName = string.IsNullOrEmpty(log.DeviceName) ? fallbackDeviceName : log.DeviceName;
                sheet.Cell(row, 1).Value = i + 1;
                sheet.Cell(row, 2).Value = log.LogTime;
                sheet.Cell(row, 2).Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";
                SetSafeText(sheet.Cell(row, 3), deviceName);
                SetSafeText(sheet.Cell(row, 4), log.LogType);
                SetSafeText(sheet.Cell(row, 5), log.PointAddress);
                SetSafeText(sheet.Cell(row, 6), log.PointLabel);
                SetSafeText(sheet.Cell(row, 7), log.Action);
                SetSafeText(sheet.Cell(row, 8), log.Description);
                SetSafeText(sheet.Cell(row, 9), log.Operator);
            }

            FinishDataSheet(sheet, new[] { 9d, 22d, 22d, 10d, 12d, 28d, 12d, 48d, 16d });
        }

        private static void WriteHeader(
            IXLWorksheet sheet,
            IReadOnlyList<string> headers,
            string backgroundColor)
        {
            for (var column = 0; column < headers.Count; column++)
                SetSafeText(sheet.Cell(1, column + 1), headers[column]);

            var range = sheet.Range(1, 1, 1, headers.Count);
            range.Style.Font.Bold = true;
            range.Style.Fill.BackgroundColor = XLColor.FromHtml(backgroundColor);
            range.Style.Font.FontColor = XLColor.White;
            range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }

        private static void FinishDataSheet(IXLWorksheet sheet, IReadOnlyList<double> widths)
        {
            for (var column = 0; column < widths.Count; column++)
                sheet.Column(column + 1).Width = widths[column];
            sheet.SheetView.FreezeRows(1);
            var usedRange = sheet.RangeUsed();
            if (usedRange != null && usedRange.RowCount() > 1)
                usedRange.SetAutoFilter();
        }

        private static void WriteInfoRow(IXLWorksheet sheet, int row, string label, string value)
        {
            SetSafeText(sheet.Cell(row, 1), label);
            SetSafeText(sheet.Cell(row, 2), value);
            sheet.Cell(row, 1).Style.Font.Bold = true;
        }

        private static void SetSafeText(IXLCell cell, string value)
            => cell.Value = ProtectSpreadsheetText(value ?? "");

        private static string ProtectSpreadsheetText(string value)
        {
            if (value.Length == 0)
                return value;

            var first = value[0];
            var suspicious = first is '=' or '+' or '@' or '\t' or '\r' or '\n';
            if (first == '-' && !decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                suspicious = true;
            return suspicious ? "'" + value : value;
        }

        private static string GetDefaultFilePath(string dataType)
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            return Path.Combine(desktop, "监控数据导出", SanitizeFileName(dataType) + ".xlsx");
        }

        private static string GetUniquePackageDirectory(string name)
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var root = Path.Combine(desktop, "监控数据导出");
            Directory.CreateDirectory(root);
            var basePath = Path.Combine(root, SanitizeFileName(name));
            var candidate = basePath;
            for (var suffix = 2; Directory.Exists(candidate) || File.Exists(candidate); suffix++)
                candidate = basePath + "_" + suffix;
            return candidate;
        }

        private static string SanitizeFileName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "未命名";
            var invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(raw.Length);
            foreach (var character in raw.Trim())
                builder.Append(invalid.Contains(character) ? '_' : character);
            var sanitized = builder.ToString().TrimEnd('.', ' ');
            return string.IsNullOrEmpty(sanitized) ? "未命名" : sanitized;
        }

        private static async Task WriteUtf8BomAtomicallyAsync(
            string finalPath,
            string content,
            CancellationToken cancellationToken)
        {
            var temporaryPath = finalPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllTextAsync(
                    temporaryPath,
                    content,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, finalPath, overwrite: true);
            }
            finally
            {
                TryDeleteFile(temporaryPath);
            }
        }

        private static string BuildTemperatureCsv(
            List<TemperatureLog> logs,
            string fallbackDeviceName,
            CancellationToken cancellationToken)
        {
            var builder = new StringBuilder();
            builder.AppendLine("序号,设备名,温度(℃),热电偶A(V),热电偶B(V),热电偶C(V),是否异常,记录时间");
            for (var i = 0; i < logs.Count; i++)
            {
                CheckCancellation(i, cancellationToken);
                var log = logs[i];
                var deviceName = string.IsNullOrEmpty(log.DeviceName) ? fallbackDeviceName : log.DeviceName;
                builder.AppendLine(string.Join(",",
                    Csv(i + 1),
                    Csv(deviceName),
                    Csv(log.Temperature.ToString("F1", CultureInfo.InvariantCulture)),
                    Csv(log.ThermocoupleA.ToString("F3", CultureInfo.InvariantCulture)),
                    Csv(log.ThermocoupleB.ToString("F3", CultureInfo.InvariantCulture)),
                    Csv(log.ThermocoupleC.ToString("F3", CultureInfo.InvariantCulture)),
                    Csv(log.IsAbnormal ? "是" : "否"),
                    Csv(log.RecordTime.ToString("yyyy-MM-dd HH:mm:ss"))));
            }
            return builder.ToString();
        }

        private static string BuildOperationCsv(
            List<OperationLog> logs,
            string fallbackDeviceName,
            CancellationToken cancellationToken)
        {
            var builder = new StringBuilder();
            builder.AppendLine("序号,时间,设备名,类型,地址,中文点位,动作,描述,操作员");
            for (var i = 0; i < logs.Count; i++)
            {
                CheckCancellation(i, cancellationToken);
                var log = logs[i];
                var deviceName = string.IsNullOrEmpty(log.DeviceName) ? fallbackDeviceName : log.DeviceName;
                builder.AppendLine(string.Join(",",
                    Csv(i + 1),
                    Csv(log.LogTime.ToString("yyyy-MM-dd HH:mm:ss")),
                    Csv(deviceName),
                    Csv(log.LogType),
                    Csv(log.PointAddress),
                    Csv(log.PointLabel),
                    Csv(log.Action),
                    Csv(log.Description),
                    Csv(log.Operator)));
            }
            return builder.ToString();
        }

        private static string BuildReadableHtml(
            string title,
            DateTime startTime,
            DateTime endTime,
            List<TemperatureLog> tempLogs,
            List<OperationLog> opLogs,
            CancellationToken cancellationToken,
            Device device = null)
        {
            var builder = new StringBuilder();
            builder.AppendLine("<!doctype html>");
            builder.AppendLine("<html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
            builder.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            builder.AppendLine($"<title>{Html(title)} 日志查看</title>");
            builder.AppendLine("<style>");
            builder.AppendLine("body{font-family:'Microsoft YaHei UI','Microsoft YaHei',Arial,sans-serif;margin:24px;background:#f3f5f7;color:#1f2933}");
            builder.AppendLine("h1{font-size:24px;margin:0 0 8px} h2{font-size:18px;margin:28px 0 10px}");
            builder.AppendLine(".meta{background:#fff;border:1px solid #d7dde5;padding:14px 16px;margin:16px 0 18px}.meta div{line-height:1.8}");
            builder.AppendLine(".file{color:#475569;font-size:13px}table{border-collapse:collapse;width:100%;background:#fff;margin-bottom:22px;font-size:13px}");
            builder.AppendLine("th,td{border:1px solid #d7dde5;padding:7px 8px;text-align:left;vertical-align:top}th{background:#e9eef5;color:#111827;position:sticky;top:0}");
            builder.AppendLine(".bad{color:#b91c1c;font-weight:700}.empty{padding:18px;background:#fff;border:1px solid #d7dde5;color:#64748b}");
            builder.AppendLine("</style></head><body>");
            builder.AppendLine($"<h1>{Html(title)} 日志查看</h1>");
            builder.AppendLine("<div class=\"file\">此文件可在未安装 Excel/WPS/数据库工具的工控机上直接查看。</div>");
            builder.AppendLine("<div class=\"meta\">");
            if (device != null)
            {
                builder.AppendLine($"<div><b>设备名称：</b>{Html(device.Name)}</div>");
                builder.AppendLine($"<div><b>设备位置：</b>{Html(device.Location)}</div>");
                builder.AppendLine($"<div><b>IP 地址：</b>{Html(device.IpAddress)}</div>");
            }
            else
            {
                builder.AppendLine($"<div><b>设备范围：</b>{Html(title)}</div>");
                builder.AppendLine($"<div><b>查询时间：</b>{Html(startTime.ToString("yyyy-MM-dd HH:mm:ss"))} ~ {Html(endTime.ToString("yyyy-MM-dd HH:mm:ss"))}</div>");
            }
            builder.AppendLine($"<div><b>导出时间：</b>{Html(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))}</div>");
            builder.AppendLine($"<div><b>温度记录：</b>{tempLogs.Count:N0} 条</div>");
            builder.AppendLine($"<div><b>操作日志：</b>{opLogs.Count:N0} 条</div>");
            builder.AppendLine("</div>");

            builder.AppendLine("<h2>操作日志</h2>");
            if (opLogs.Count == 0)
            {
                builder.AppendLine("<div class=\"empty\">暂无操作日志。</div>");
            }
            else
            {
                builder.AppendLine("<table><thead><tr><th>序号</th><th>时间</th><th>设备名</th><th>类型</th><th>地址</th><th>中文点位</th><th>动作</th><th>描述</th><th>操作员</th></tr></thead><tbody>");
                for (var i = 0; i < opLogs.Count; i++)
                {
                    CheckCancellation(i, cancellationToken);
                    var log = opLogs[i];
                    var deviceName = string.IsNullOrEmpty(log.DeviceName) ? device?.Name ?? "" : log.DeviceName;
                    builder.AppendLine("<tr>" +
                        $"<td>{i + 1}</td><td>{Html(log.LogTime.ToString("yyyy-MM-dd HH:mm:ss"))}</td>" +
                        $"<td>{Html(deviceName)}</td><td>{Html(log.LogType)}</td><td>{Html(log.PointAddress)}</td>" +
                        $"<td>{Html(log.PointLabel)}</td><td>{Html(log.Action)}</td><td>{Html(log.Description)}</td><td>{Html(log.Operator)}</td>" +
                        "</tr>");
                }
                builder.AppendLine("</tbody></table>");
            }

            builder.AppendLine("<h2>温度记录</h2>");
            if (tempLogs.Count == 0)
            {
                builder.AppendLine("<div class=\"empty\">暂无温度记录。</div>");
            }
            else
            {
                builder.AppendLine("<table><thead><tr><th>序号</th><th>设备名</th><th>温度(℃)</th><th>热电偶A(V)</th><th>热电偶B(V)</th><th>热电偶C(V)</th><th>是否异常</th><th>记录时间</th></tr></thead><tbody>");
                for (var i = 0; i < tempLogs.Count; i++)
                {
                    CheckCancellation(i, cancellationToken);
                    var log = tempLogs[i];
                    var deviceName = string.IsNullOrEmpty(log.DeviceName) ? device?.Name ?? "" : log.DeviceName;
                    var abnormalClass = log.IsAbnormal ? " class=\"bad\"" : "";
                    builder.AppendLine("<tr>" +
                        $"<td>{i + 1}</td><td>{Html(deviceName)}</td><td{abnormalClass}>{Html(log.Temperature.ToString("F1", CultureInfo.InvariantCulture))}</td>" +
                        $"<td>{Html(log.ThermocoupleA.ToString("F3", CultureInfo.InvariantCulture))}</td><td>{Html(log.ThermocoupleB.ToString("F3", CultureInfo.InvariantCulture))}</td>" +
                        $"<td>{Html(log.ThermocoupleC.ToString("F3", CultureInfo.InvariantCulture))}</td><td>{Html(log.IsAbnormal ? "是" : "否")}</td>" +
                        $"<td>{Html(log.RecordTime.ToString("yyyy-MM-dd HH:mm:ss"))}</td></tr>");
                }
                builder.AppendLine("</tbody></table>");
            }

            builder.AppendLine("</body></html>");
            return builder.ToString();
        }

        private static string Csv(object value)
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            text = ProtectSpreadsheetText(text);
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        private static string Html(string value) => WebUtility.HtmlEncode(value ?? "");

        private static void ValidateLists(
            List<TemperatureLog> temperatureLogs,
            List<OperationLog> operationLogs)
        {
            ArgumentNullException.ThrowIfNull(temperatureLogs);
            ArgumentNullException.ThrowIfNull(operationLogs);
            EnsureSafeRowCount(temperatureLogs.Count, operationLogs.Count);
        }

        private static void EnsureSafeRowCount(int temperatureCount, int operationCount)
        {
            var combined = checked(temperatureCount + operationCount);
            if (combined > BoundedLogExportLoader.MaximumCombinedRows)
            {
                throw new InvalidOperationException(
                    $"待导出数据共 {combined:N0} 条，超过单次导出安全上限 " +
                    $"{BoundedLogExportLoader.MaximumCombinedRows:N0} 条。请缩小时间范围。");
            }
        }

        private static void CheckCancellation(int index, CancellationToken cancellationToken)
        {
            if (index % CancellationCheckInterval == 0)
                cancellationToken.ThrowIfCancellationRequested();
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // 临时文件清理失败不能覆盖真正的导出异常。
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch
            {
                // 仅清理由本次调用创建、带随机后缀的暂存目录。
            }
        }
    }
}
