using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        private static string SanitizeFileName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "未命名";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(raw.Length);
            foreach (var c in raw)
                sb.Append(invalid.Contains(c) ? '_' : c);
            return sb.ToString();
        }
    }
}
