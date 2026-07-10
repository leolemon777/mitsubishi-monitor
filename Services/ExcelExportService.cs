using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using OfficeOpenXml;
using MitsubishiMonitor.Demo.Models;

namespace MitsubishiMonitor.Demo.Services
{
    /// <summary>
    /// Excel导出服务
    /// </summary>
    public class ExcelExportService
    {
        static ExcelExportService()
        {
            // 设置EPPlus的许可证上下文 (个人/非商业用途)
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        }

        /// <summary>
        /// 导出温度日志到Excel
        /// </summary>
        public async Task<string> ExportTemperatureLogsAsync(List<TemperatureLog> logs, string filePath = null)
        {
            if (!logs.Any())
                throw new InvalidOperationException("没有数据可导出");

            filePath ??= GetDefaultFilePath("温度日志", DateTime.Now.ToString("yyyyMMdd_HHmmss"));

            using var package = new ExcelPackage();
            var worksheet = package.Workbook.Worksheets.Add("温度日志");

            // 设置标题行
            worksheet.Cells[1, 1].Value = "序号";
            worksheet.Cells[1, 2].Value = "设备ID";
            worksheet.Cells[1, 3].Value = "温度(°C)";
            worksheet.Cells[1, 4].Value = "是否异常";
            worksheet.Cells[1, 5].Value = "记录时间";

            // 设置标题样式
            using (var range = worksheet.Cells[1, 1, 1, 5])
            {
                range.Style.Font.Bold = true;
                range.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                range.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(0, 188, 212));
                range.Style.Font.Color.SetColor(System.Drawing.Color.White);
            }

            // 填充数据
            for (int i = 0; i < logs.Count; i++)
            {
                var log = logs[i];
                int row = i + 2;

                worksheet.Cells[row, 1].Value = i + 1;
                worksheet.Cells[row, 2].Value = log.DeviceId;
                worksheet.Cells[row, 3].Value = log.Temperature;
                worksheet.Cells[row, 4].Value = log.IsAbnormal ? "是" : "否";
                worksheet.Cells[row, 5].Value = log.RecordTime.ToString("yyyy-MM-dd HH:mm:ss");

                // 异常数据标红
                if (log.IsAbnormal)
                {
                    worksheet.Cells[row, 3].Style.Font.Color.SetColor(System.Drawing.Color.Red);
                }
            }

            // 自动调整列宽
            worksheet.Cells.AutoFitColumns();

            // 保存文件
            var fileInfo = new FileInfo(filePath);
            await package.SaveAsAsync(fileInfo);

            return filePath;
        }

        /// <summary>
        /// 导出操作日志到Excel
        /// </summary>
        public async Task<string> ExportOperationLogsAsync(List<OperationLog> logs, string filePath = null)
        {
            if (!logs.Any())
                throw new InvalidOperationException("没有数据可导出");

            filePath ??= GetDefaultFilePath("操作日志", DateTime.Now.ToString("yyyyMMdd_HHmmss"));

            using var package = new ExcelPackage();
            var worksheet = package.Workbook.Worksheets.Add("操作日志");

            // 设置标题行
            worksheet.Cells[1, 1].Value = "序号";
            worksheet.Cells[1, 2].Value = "时间";
            worksheet.Cells[1, 3].Value = "类型";
            worksheet.Cells[1, 4].Value = "地址";
            worksheet.Cells[1, 5].Value = "动作";
            worksheet.Cells[1, 6].Value = "描述";
            worksheet.Cells[1, 7].Value = "操作员";

            // 设置标题样式
            using (var range = worksheet.Cells[1, 1, 1, 7])
            {
                range.Style.Font.Bold = true;
                range.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                range.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(0, 188, 212));
                range.Style.Font.Color.SetColor(System.Drawing.Color.White);
            }

            // 填充数据
            for (int i = 0; i < logs.Count; i++)
            {
                var log = logs[i];
                int row = i + 2;

                worksheet.Cells[row, 1].Value = i + 1;
                worksheet.Cells[row, 2].Value = log.LogTime.ToString("yyyy-MM-dd HH:mm:ss");
                worksheet.Cells[row, 3].Value = log.LogType;
                worksheet.Cells[row, 4].Value = log.PointAddress;
                worksheet.Cells[row, 5].Value = log.Action;
                worksheet.Cells[row, 6].Value = log.Description;
                worksheet.Cells[row, 7].Value = log.Operator;
            }

            // 自动调整列宽
            worksheet.Cells.AutoFitColumns();

            // 保存文件
            var fileInfo = new FileInfo(filePath);
            await package.SaveAsAsync(fileInfo);

            return filePath;
        }

        /// <summary>
        /// 导出所有数据到一个Excel文件
        /// </summary>
        public async Task<string> ExportAllAsync(List<TemperatureLog> tempLogs, List<OperationLog> opLogs, string filePath = null)
        {
            // 支持空数据导出模板
            filePath ??= GetDefaultFilePath("监控数据", DateTime.Now.ToString("yyyyMMdd_HHmmss"));

            // 确保目录存在
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var package = new ExcelPackage();

            // 添加温度日志sheet (始终添加，即使为空)
            var tempSheet = package.Workbook.Worksheets.Add("温度日志");

            tempSheet.Cells[1, 1].Value = "序号";
            tempSheet.Cells[1, 2].Value = "温度(°C)";
            tempSheet.Cells[1, 3].Value = "记录时间";

            using (var range = tempSheet.Cells[1, 1, 1, 3])
            {
                range.Style.Font.Bold = true;
                range.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                range.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(0, 188, 212));
                range.Style.Font.Color.SetColor(System.Drawing.Color.White);
            }

            for (int i = 0; i < tempLogs.Count; i++)
            {
                tempSheet.Cells[i + 2, 1].Value = i + 1;
                tempSheet.Cells[i + 2, 2].Value = tempLogs[i].Temperature;
                tempSheet.Cells[i + 2, 3].Value = tempLogs[i].RecordTime.ToString("yyyy-MM-dd HH:mm:ss");
            }

            tempSheet.Cells.AutoFitColumns();

            // 添加操作日志sheet (始终添加，即使为空)
            var opSheet = package.Workbook.Worksheets.Add("操作日志");

            opSheet.Cells[1, 1].Value = "序号";
            opSheet.Cells[1, 2].Value = "时间";
            opSheet.Cells[1, 3].Value = "类型";
            opSheet.Cells[1, 4].Value = "地址";
            opSheet.Cells[1, 5].Value = "描述";

            using (var range = opSheet.Cells[1, 1, 1, 5])
            {
                range.Style.Font.Bold = true;
                range.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                range.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(0, 188, 212));
                range.Style.Font.Color.SetColor(System.Drawing.Color.White);
            }

            for (int i = 0; i < opLogs.Count; i++)
            {
                opSheet.Cells[i + 2, 1].Value = i + 1;
                opSheet.Cells[i + 2, 2].Value = opLogs[i].LogTime.ToString("yyyy-MM-dd HH:mm:ss");
                opSheet.Cells[i + 2, 3].Value = opLogs[i].LogType;
                opSheet.Cells[i + 2, 4].Value = opLogs[i].PointAddress;
                opSheet.Cells[i + 2, 5].Value = opLogs[i].Description;
            }

            opSheet.Cells.AutoFitColumns();

            var fileInfo = new FileInfo(filePath);
            await package.SaveAsAsync(fileInfo);

            return filePath;
        }

        /// <summary>
        /// 导出设备数据（温度+操作日志）
        /// </summary>
        public async Task<string> ExportDeviceDataAsync(Device device, List<TemperatureLog> tempLogs, List<OperationLog> opLogs, string filePath = null)
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            filePath ??= GetDefaultFilePath($"{device.Name}_{timestamp}", timestamp);

            // 确保目录存在
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var package = new ExcelPackage();

            // 添加设备信息sheet
            var infoSheet = package.Workbook.Worksheets.Add("设备信息");
            infoSheet.Cells[1, 1].Value = "设备名称";
            infoSheet.Cells[1, 2].Value = device.Name;
            infoSheet.Cells[2, 1].Value = "设备位置";
            infoSheet.Cells[2, 2].Value = device.Location;
            infoSheet.Cells[3, 1].Value = "IP地址";
            infoSheet.Cells[3, 2].Value = device.IpAddress;
            infoSheet.Cells[4, 1].Value = "导出时间";
            infoSheet.Cells[4, 2].Value = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            // 添加温度日志sheet（增加 设备名 列，使行级数据可独立解读）
            var tempSheet = package.Workbook.Worksheets.Add("温度记录");
            tempSheet.Cells[1, 1].Value = "序号";
            tempSheet.Cells[1, 2].Value = "设备名";
            tempSheet.Cells[1, 3].Value = "温度(°C)";
            tempSheet.Cells[1, 4].Value = "热电偶A(V)";
            tempSheet.Cells[1, 5].Value = "热电偶B(V)";
            tempSheet.Cells[1, 6].Value = "热电偶C(V)";
            tempSheet.Cells[1, 7].Value = "是否异常";
            tempSheet.Cells[1, 8].Value = "记录时间";

            using (var range = tempSheet.Cells[1, 1, 1, 8])
            {
                range.Style.Font.Bold = true;
                range.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                range.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(240, 136, 62));
                range.Style.Font.Color.SetColor(System.Drawing.Color.White);
            }

            for (int i = 0; i < tempLogs.Count; i++)
            {
                var log = tempLogs[i];
                // 老数据可能没存 DeviceName，回退用本次导出的设备名
                var rowDevName = string.IsNullOrEmpty(log.DeviceName) ? device.Name : log.DeviceName;
                tempSheet.Cells[i + 2, 1].Value = i + 1;
                tempSheet.Cells[i + 2, 2].Value = rowDevName;
                tempSheet.Cells[i + 2, 3].Value = log.Temperature;
                tempSheet.Cells[i + 2, 4].Value = log.ThermocoupleA;
                tempSheet.Cells[i + 2, 5].Value = log.ThermocoupleB;
                tempSheet.Cells[i + 2, 6].Value = log.ThermocoupleC;
                tempSheet.Cells[i + 2, 7].Value = log.IsAbnormal ? "是" : "否";
                tempSheet.Cells[i + 2, 8].Value = log.RecordTime.ToString("yyyy-MM-dd HH:mm:ss");

                // 异常数据标红
                if (log.IsAbnormal)
                {
                    tempSheet.Cells[i + 2, 3].Style.Font.Color.SetColor(System.Drawing.Color.Red);
                }
            }

            tempSheet.Cells.AutoFitColumns();

            // 添加操作日志sheet（增加 设备名 / 中文点位 两列）
            var opSheet = package.Workbook.Worksheets.Add("操作日志");
            opSheet.Cells[1, 1].Value = "序号";
            opSheet.Cells[1, 2].Value = "时间";
            opSheet.Cells[1, 3].Value = "设备名";
            opSheet.Cells[1, 4].Value = "类型";
            opSheet.Cells[1, 5].Value = "地址";
            opSheet.Cells[1, 6].Value = "中文点位";
            opSheet.Cells[1, 7].Value = "动作";
            opSheet.Cells[1, 8].Value = "描述";
            opSheet.Cells[1, 9].Value = "操作员";

            using (var range = opSheet.Cells[1, 1, 1, 9])
            {
                range.Style.Font.Bold = true;
                range.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                range.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(76, 175, 80));
                range.Style.Font.Color.SetColor(System.Drawing.Color.White);
            }

            for (int i = 0; i < opLogs.Count; i++)
            {
                var log = opLogs[i];
                var rowDevName = string.IsNullOrEmpty(log.DeviceName) ? device.Name : log.DeviceName;
                opSheet.Cells[i + 2, 1].Value = i + 1;
                opSheet.Cells[i + 2, 2].Value = log.LogTime.ToString("yyyy-MM-dd HH:mm:ss");
                opSheet.Cells[i + 2, 3].Value = rowDevName;
                opSheet.Cells[i + 2, 4].Value = log.LogType;
                opSheet.Cells[i + 2, 5].Value = log.PointAddress;
                opSheet.Cells[i + 2, 6].Value = log.PointLabel;
                opSheet.Cells[i + 2, 7].Value = log.Action;
                opSheet.Cells[i + 2, 8].Value = log.Description;
                opSheet.Cells[i + 2, 9].Value = log.Operator;
            }

            opSheet.Cells.AutoFitColumns();

            var fileInfo = new FileInfo(filePath);
            await package.SaveAsAsync(fileInfo);

            return filePath;
        }

        /// <summary>
        /// 导出工控机可直接查看的数据包：HTML 查看页 + CSV 明细 + Excel 备份。
        /// HTML 可用系统自带浏览器打开，CSV 可用记事本打开。
        /// </summary>
        public async Task<string> ExportDeviceReadablePackageAsync(Device device, List<TemperatureLog> tempLogs, List<OperationLog> opLogs)
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var directory = GetExportPackageDirectory($"{device.Name}_{timestamp}");

            await ExportDeviceDataAsync(device, tempLogs, opLogs, Path.Combine(directory, "Excel备份.xlsx"));

            var tempCsvPath = Path.Combine(directory, "温度记录.csv");
            var opCsvPath = Path.Combine(directory, "操作日志.csv");
            var htmlPath = Path.Combine(directory, "日志查看.html");

            await WriteUtf8BomAsync(tempCsvPath, BuildTemperatureCsv(tempLogs, device.Name));
            await WriteUtf8BomAsync(opCsvPath, BuildOperationCsv(opLogs, device.Name));
            await WriteUtf8BomAsync(htmlPath, BuildReadableHtml(
                device.Name,
                DateTime.MinValue,
                DateTime.MinValue,
                tempLogs,
                opLogs,
                device));

            return htmlPath;
        }

        private string GetDefaultFilePath(string dataType, string timestamp)
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            return Path.Combine(desktop, "监控数据导出", $"{dataType}.xlsx");
        }

        /// <summary>
        /// 日志查询页通用导出：温度+操作两个 sheet，每行带"设备名"列，支持多设备混合数据。
        /// </summary>
        public async Task<string> ExportLogsAsync(
            string deviceLabel,
            DateTime startTime,
            DateTime endTime,
            List<TemperatureLog> tempLogs,
            List<OperationLog> opLogs,
            string filePath = null)
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var safeDeviceLabel = SanitizeFileName(deviceLabel);
            filePath ??= Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "监控数据导出",
                $"日志_{safeDeviceLabel}_{timestamp}.xlsx");

            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var package = new ExcelPackage();

            // 概要 sheet
            var infoSheet = package.Workbook.Worksheets.Add("查询条件");
            infoSheet.Cells[1, 1].Value = "设备范围";
            infoSheet.Cells[1, 2].Value = deviceLabel;
            infoSheet.Cells[2, 1].Value = "时间范围（起）";
            infoSheet.Cells[2, 2].Value = startTime.ToString("yyyy-MM-dd HH:mm:ss");
            infoSheet.Cells[3, 1].Value = "时间范围（止）";
            infoSheet.Cells[3, 2].Value = endTime.ToString("yyyy-MM-dd HH:mm:ss");
            infoSheet.Cells[4, 1].Value = "温度记录条数";
            infoSheet.Cells[4, 2].Value = tempLogs.Count;
            infoSheet.Cells[5, 1].Value = "操作日志条数";
            infoSheet.Cells[5, 2].Value = opLogs.Count;
            infoSheet.Cells[6, 1].Value = "导出时间";
            infoSheet.Cells[6, 2].Value = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            infoSheet.Cells.AutoFitColumns();

            // 温度记录 sheet
            var tempSheet = package.Workbook.Worksheets.Add("温度记录");
            tempSheet.Cells[1, 1].Value = "序号";
            tempSheet.Cells[1, 2].Value = "设备名";
            tempSheet.Cells[1, 3].Value = "温度(°C)";
            tempSheet.Cells[1, 4].Value = "热电偶A(V)";
            tempSheet.Cells[1, 5].Value = "热电偶B(V)";
            tempSheet.Cells[1, 6].Value = "热电偶C(V)";
            tempSheet.Cells[1, 7].Value = "是否异常";
            tempSheet.Cells[1, 8].Value = "记录时间";
            using (var range = tempSheet.Cells[1, 1, 1, 8])
            {
                range.Style.Font.Bold = true;
                range.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                range.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(240, 136, 62));
                range.Style.Font.Color.SetColor(System.Drawing.Color.White);
            }
            for (int i = 0; i < tempLogs.Count; i++)
            {
                var log = tempLogs[i];
                tempSheet.Cells[i + 2, 1].Value = i + 1;
                tempSheet.Cells[i + 2, 2].Value = log.DeviceName ?? "";
                tempSheet.Cells[i + 2, 3].Value = log.Temperature;
                tempSheet.Cells[i + 2, 4].Value = log.ThermocoupleA;
                tempSheet.Cells[i + 2, 5].Value = log.ThermocoupleB;
                tempSheet.Cells[i + 2, 6].Value = log.ThermocoupleC;
                tempSheet.Cells[i + 2, 7].Value = log.IsAbnormal ? "是" : "否";
                tempSheet.Cells[i + 2, 8].Value = log.RecordTime.ToString("yyyy-MM-dd HH:mm:ss");
                if (log.IsAbnormal)
                {
                    tempSheet.Cells[i + 2, 3].Style.Font.Color.SetColor(System.Drawing.Color.Red);
                }
            }
            tempSheet.Cells.AutoFitColumns();

            // 操作日志 sheet
            var opSheet = package.Workbook.Worksheets.Add("操作日志");
            opSheet.Cells[1, 1].Value = "序号";
            opSheet.Cells[1, 2].Value = "时间";
            opSheet.Cells[1, 3].Value = "设备名";
            opSheet.Cells[1, 4].Value = "类型";
            opSheet.Cells[1, 5].Value = "地址";
            opSheet.Cells[1, 6].Value = "中文点位";
            opSheet.Cells[1, 7].Value = "动作";
            opSheet.Cells[1, 8].Value = "描述";
            opSheet.Cells[1, 9].Value = "操作员";
            using (var range = opSheet.Cells[1, 1, 1, 9])
            {
                range.Style.Font.Bold = true;
                range.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                range.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(76, 175, 80));
                range.Style.Font.Color.SetColor(System.Drawing.Color.White);
            }
            for (int i = 0; i < opLogs.Count; i++)
            {
                var log = opLogs[i];
                opSheet.Cells[i + 2, 1].Value = i + 1;
                opSheet.Cells[i + 2, 2].Value = log.LogTime.ToString("yyyy-MM-dd HH:mm:ss");
                opSheet.Cells[i + 2, 3].Value = log.DeviceName ?? "";
                opSheet.Cells[i + 2, 4].Value = log.LogType;
                opSheet.Cells[i + 2, 5].Value = log.PointAddress;
                opSheet.Cells[i + 2, 6].Value = log.PointLabel ?? "";
                opSheet.Cells[i + 2, 7].Value = log.Action;
                opSheet.Cells[i + 2, 8].Value = log.Description;
                opSheet.Cells[i + 2, 9].Value = log.Operator;
            }
            opSheet.Cells.AutoFitColumns();

            var fileInfo = new FileInfo(filePath);
            await package.SaveAsAsync(fileInfo);
            return filePath;
        }

        /// <summary>
        /// 日志查询页导出工控机可读包：HTML 查看页 + CSV 明细 + Excel 备份。
        /// </summary>
        public async Task<string> ExportLogsReadablePackageAsync(
            string deviceLabel,
            DateTime startTime,
            DateTime endTime,
            List<TemperatureLog> tempLogs,
            List<OperationLog> opLogs)
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var safeDeviceLabel = SanitizeFileName(deviceLabel);
            var directory = GetExportPackageDirectory($"日志_{safeDeviceLabel}_{timestamp}");

            await ExportLogsAsync(
                deviceLabel,
                startTime,
                endTime,
                tempLogs,
                opLogs,
                Path.Combine(directory, "Excel备份.xlsx"));

            var tempCsvPath = Path.Combine(directory, "温度记录.csv");
            var opCsvPath = Path.Combine(directory, "操作日志.csv");
            var htmlPath = Path.Combine(directory, "日志查看.html");

            await WriteUtf8BomAsync(tempCsvPath, BuildTemperatureCsv(tempLogs, ""));
            await WriteUtf8BomAsync(opCsvPath, BuildOperationCsv(opLogs, ""));
            await WriteUtf8BomAsync(htmlPath, BuildReadableHtml(
                deviceLabel,
                startTime,
                endTime,
                tempLogs,
                opLogs));

            return htmlPath;
        }

        private static string SanitizeFileName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "未命名";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(raw.Length);
            foreach (var c in raw)
                sb.Append(invalid.Contains(c) ? '_' : c);
            return sb.ToString();
        }

        private string GetExportPackageDirectory(string name)
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var directory = Path.Combine(desktop, "监控数据导出", SanitizeFileName(name));
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static async Task WriteUtf8BomAsync(string path, string content)
        {
            await File.WriteAllTextAsync(path, content, new UTF8Encoding(true));
        }

        private static string BuildTemperatureCsv(List<TemperatureLog> logs, string fallbackDeviceName)
        {
            var sb = new StringBuilder();
            sb.AppendLine("序号,设备名,温度(℃),热电偶A(V),热电偶B(V),热电偶C(V),是否异常,记录时间");
            for (int i = 0; i < logs.Count; i++)
            {
                var log = logs[i];
                var deviceName = string.IsNullOrEmpty(log.DeviceName) ? fallbackDeviceName : log.DeviceName;
                sb.AppendLine(string.Join(",",
                    Csv(i + 1),
                    Csv(deviceName),
                    Csv(log.Temperature.ToString("F1", CultureInfo.InvariantCulture)),
                    Csv(log.ThermocoupleA.ToString("F3", CultureInfo.InvariantCulture)),
                    Csv(log.ThermocoupleB.ToString("F3", CultureInfo.InvariantCulture)),
                    Csv(log.ThermocoupleC.ToString("F3", CultureInfo.InvariantCulture)),
                    Csv(log.IsAbnormal ? "是" : "否"),
                    Csv(log.RecordTime.ToString("yyyy-MM-dd HH:mm:ss"))));
            }
            return sb.ToString();
        }

        private static string BuildOperationCsv(List<OperationLog> logs, string fallbackDeviceName)
        {
            var sb = new StringBuilder();
            sb.AppendLine("序号,时间,设备名,类型,地址,中文点位,动作,描述,操作员");
            for (int i = 0; i < logs.Count; i++)
            {
                var log = logs[i];
                var deviceName = string.IsNullOrEmpty(log.DeviceName) ? fallbackDeviceName : log.DeviceName;
                sb.AppendLine(string.Join(",",
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
            return sb.ToString();
        }

        private static string BuildReadableHtml(
            string title,
            DateTime startTime,
            DateTime endTime,
            List<TemperatureLog> tempLogs,
            List<OperationLog> opLogs,
            Device device = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!doctype html>");
            sb.AppendLine("<html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
            sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            sb.AppendLine($"<title>{Html(title)} 日志查看</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("body{font-family:'Microsoft YaHei UI','Microsoft YaHei',Arial,sans-serif;margin:24px;background:#f3f5f7;color:#1f2933}");
            sb.AppendLine("h1{font-size:24px;margin:0 0 8px} h2{font-size:18px;margin:28px 0 10px}");
            sb.AppendLine(".meta{background:#fff;border:1px solid #d7dde5;padding:14px 16px;margin:16px 0 18px}");
            sb.AppendLine(".meta div{line-height:1.8}.file{color:#475569;font-size:13px}");
            sb.AppendLine("table{border-collapse:collapse;width:100%;background:#fff;margin-bottom:22px;font-size:13px}");
            sb.AppendLine("th,td{border:1px solid #d7dde5;padding:7px 8px;text-align:left;vertical-align:top}");
            sb.AppendLine("th{background:#e9eef5;color:#111827;position:sticky;top:0}.bad{color:#b91c1c;font-weight:700}");
            sb.AppendLine(".empty{padding:18px;background:#fff;border:1px solid #d7dde5;color:#64748b}");
            sb.AppendLine("</style></head><body>");
            sb.AppendLine($"<h1>{Html(title)} 日志查看</h1>");
            sb.AppendLine("<div class=\"file\">此文件可在未安装 Excel/WPS/数据库工具的工控机上直接查看。</div>");
            sb.AppendLine("<div class=\"meta\">");
            if (device != null)
            {
                sb.AppendLine($"<div><b>设备名称：</b>{Html(device.Name)}</div>");
                sb.AppendLine($"<div><b>设备位置：</b>{Html(device.Location)}</div>");
                sb.AppendLine($"<div><b>IP 地址：</b>{Html(device.IpAddress)}</div>");
            }
            else
            {
                sb.AppendLine($"<div><b>设备范围：</b>{Html(title)}</div>");
                sb.AppendLine($"<div><b>查询时间：</b>{Html(startTime.ToString("yyyy-MM-dd HH:mm:ss"))} ~ {Html(endTime.ToString("yyyy-MM-dd HH:mm:ss"))}</div>");
            }
            sb.AppendLine($"<div><b>导出时间：</b>{Html(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))}</div>");
            sb.AppendLine($"<div><b>温度记录：</b>{tempLogs.Count} 条</div>");
            sb.AppendLine($"<div><b>操作日志：</b>{opLogs.Count} 条</div>");
            sb.AppendLine("</div>");

            sb.AppendLine("<h2>操作日志</h2>");
            if (opLogs.Count == 0)
            {
                sb.AppendLine("<div class=\"empty\">暂无操作日志。</div>");
            }
            else
            {
                sb.AppendLine("<table><thead><tr><th>序号</th><th>时间</th><th>设备名</th><th>类型</th><th>地址</th><th>中文点位</th><th>动作</th><th>描述</th><th>操作员</th></tr></thead><tbody>");
                for (int i = 0; i < opLogs.Count; i++)
                {
                    var log = opLogs[i];
                    var deviceName = string.IsNullOrEmpty(log.DeviceName) ? device?.Name ?? "" : log.DeviceName;
                    sb.AppendLine("<tr>" +
                        $"<td>{i + 1}</td><td>{Html(log.LogTime.ToString("yyyy-MM-dd HH:mm:ss"))}</td>" +
                        $"<td>{Html(deviceName)}</td><td>{Html(log.LogType)}</td><td>{Html(log.PointAddress)}</td>" +
                        $"<td>{Html(log.PointLabel)}</td><td>{Html(log.Action)}</td><td>{Html(log.Description)}</td><td>{Html(log.Operator)}</td>" +
                        "</tr>");
                }
                sb.AppendLine("</tbody></table>");
            }

            sb.AppendLine("<h2>温度记录</h2>");
            if (tempLogs.Count == 0)
            {
                sb.AppendLine("<div class=\"empty\">暂无温度记录。</div>");
            }
            else
            {
                sb.AppendLine("<table><thead><tr><th>序号</th><th>设备名</th><th>温度(℃)</th><th>热电偶A(V)</th><th>热电偶B(V)</th><th>热电偶C(V)</th><th>是否异常</th><th>记录时间</th></tr></thead><tbody>");
                for (int i = 0; i < tempLogs.Count; i++)
                {
                    var log = tempLogs[i];
                    var deviceName = string.IsNullOrEmpty(log.DeviceName) ? device?.Name ?? "" : log.DeviceName;
                    var abnormalClass = log.IsAbnormal ? " class=\"bad\"" : "";
                    sb.AppendLine("<tr>" +
                        $"<td>{i + 1}</td><td>{Html(deviceName)}</td><td{abnormalClass}>{Html(log.Temperature.ToString("F1", CultureInfo.InvariantCulture))}</td>" +
                        $"<td>{Html(log.ThermocoupleA.ToString("F3", CultureInfo.InvariantCulture))}</td><td>{Html(log.ThermocoupleB.ToString("F3", CultureInfo.InvariantCulture))}</td>" +
                        $"<td>{Html(log.ThermocoupleC.ToString("F3", CultureInfo.InvariantCulture))}</td><td>{Html(log.IsAbnormal ? "是" : "否")}</td>" +
                        $"<td>{Html(log.RecordTime.ToString("yyyy-MM-dd HH:mm:ss"))}</td>" +
                        "</tr>");
                }
                sb.AppendLine("</tbody></table>");
            }

            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        private static string Csv(object value)
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        private static string Html(string value)
        {
            return WebUtility.HtmlEncode(value ?? "");
        }
    }
}
