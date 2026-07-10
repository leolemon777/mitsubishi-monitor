using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using MitsubishiMonitor.Demo.Models;

namespace MitsubishiMonitor.Demo.Services
{
    /// <summary>
    /// 自动导出服务：把新数据攒批后以追加方式写入指定文件夹的当日 HTML 文件。
    /// 操作日志和温度记录分别对应两个 HTML 文件，浏览器直接打开即可查看。
    /// Append* 只入队不做磁盘 IO（调用方在 PLC 事件线程上，目标可能是慢盘/网络盘），
    /// 由 3 秒定时器在后台线程批量落盘，模式与 LogBufferService 一致。
    /// </summary>
    public class AutoExportService : IDisposable
    {
        private readonly ConcurrentQueue<OperationLog> _opQueue = new();
        private readonly ConcurrentQueue<TemperatureLog> _tempQueue = new();
        private readonly System.Timers.Timer _flushTimer;
        private int _isFlushing;
        private bool _isDisposed;

        /// <summary>
        /// 内存队列上限，超出后丢弃最早的条目。
        /// 自动导出只是辅助查看手段（数据库才是权威存储），慢盘时宁可丢 HTML 行也不能撑爆内存。
        /// </summary>
        public int MaxQueueSize { get; set; } = 20000;

        /// <summary>
        /// 当前导出目录，空字符串表示未启用
        /// </summary>
        public string ExportPath { get; private set; } = "";

        public AutoExportService()
        {
            ExportPath = AppConfig.AutoExportPath ?? "";

            _flushTimer = new System.Timers.Timer(3000);
            _flushTimer.Elapsed += (s, e) => Flush();
            _flushTimer.AutoReset = true;
            _flushTimer.Start();
        }

        /// <summary>
        /// 更新导出目录（设置保存后调用）
        /// </summary>
        public void UpdateExportPath(string path)
        {
            ExportPath = path ?? "";
        }

        /// <summary>
        /// 入队一条操作日志（线程安全，不阻塞、不做磁盘 IO）
        /// </summary>
        public void AppendOperationLog(OperationLog log)
        {
            if (string.IsNullOrWhiteSpace(ExportPath)) return;

            _opQueue.Enqueue(log);
            while (_opQueue.Count > MaxQueueSize && _opQueue.TryDequeue(out _)) { }
        }

        /// <summary>
        /// 入队一条温度日志（线程安全，不阻塞、不做磁盘 IO）
        /// </summary>
        public void AppendTemperatureLog(TemperatureLog log)
        {
            if (string.IsNullOrWhiteSpace(ExportPath)) return;

            _tempQueue.Enqueue(log);
            while (_tempQueue.Count > MaxQueueSize && _tempQueue.TryDequeue(out _)) { }
        }

        /// <summary>
        /// 把队列中积压的日志批量落盘。定时器与 Dispose 共用，Interlocked 防重入。
        /// </summary>
        private void Flush()
        {
            if (Interlocked.Exchange(ref _isFlushing, 1) == 1)
                return;

            try
            {
                FlushOperationLogs();
                FlushTemperatureLogs();
            }
            finally
            {
                Interlocked.Exchange(ref _isFlushing, 0);
            }
        }

        private void FlushOperationLogs()
        {
            if (_opQueue.IsEmpty) return;

            var exportPath = ExportPath;
            if (string.IsNullOrWhiteSpace(exportPath))
            {
                // 运行中关闭了导出：清掉积压，避免之后重新开启时把旧数据一股脑写出去
                while (_opQueue.TryDequeue(out _)) { }
                return;
            }

            var logs = new List<OperationLog>();
            while (_opQueue.TryDequeue(out var log)) logs.Add(log);

            try
            {
                // 按日期分组写入各自的当日文件（跨零点的批次会拆成两个文件）
                foreach (var group in logs.GroupBy(l => l.LogTime.Date))
                {
                    var filePath = GetDailyFilePath(exportPath, "操作日志", group.Key);
                    // 表头只看文件是否存在：同一天重启程序不会重复写表头
                    bool isNew = !File.Exists(filePath);

                    using var sw = new StreamWriter(filePath, append: true, encoding: new UTF8Encoding(true));

                    if (isNew)
                    {
                        sw.WriteLine("<!doctype html>");
                        sw.WriteLine("<html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
                        sw.WriteLine("<title>操作日志 " + group.Key.ToString("yyyy-MM-dd") + "</title>");
                        sw.WriteLine(GetHtmlStyle());
                        sw.WriteLine("</head><body>");
                        sw.WriteLine("<h1>操作日志 — " + group.Key.ToString("yyyy-MM-dd") + "</h1>");
                        sw.WriteLine("<p class=\"tip\">此文件每次有 IO 点位变化时自动追加，用浏览器打开即可查看。</p>");
                        sw.WriteLine("<table><thead><tr>");
                        sw.WriteLine("<th>时间</th><th>设备名</th><th>类型</th><th>地址</th><th>中文点位</th><th>动作</th><th>描述</th>");
                        sw.WriteLine("</tr></thead>");
                    }

                    foreach (var log in group)
                    {
                        sw.WriteLine("<tr>" +
                            $"<td>{H(log.LogTime.ToString("HH:mm:ss"))}</td>" +
                            $"<td>{H(log.DeviceName)}</td>" +
                            $"<td>{H(log.LogType)}</td>" +
                            $"<td>{H(log.PointAddress)}</td>" +
                            $"<td>{H(log.PointLabel)}</td>" +
                            $"<td class=\"{(log.Action == "ON" ? "on" : "off")}\">{H(log.Action)}</td>" +
                            $"<td>{H(log.Description)}</td>" +
                            "</tr>");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AutoExport] 操作日志写入失败: {ex.Message}");
            }
        }

        private void FlushTemperatureLogs()
        {
            if (_tempQueue.IsEmpty) return;

            var exportPath = ExportPath;
            if (string.IsNullOrWhiteSpace(exportPath))
            {
                while (_tempQueue.TryDequeue(out _)) { }
                return;
            }

            var logs = new List<TemperatureLog>();
            while (_tempQueue.TryDequeue(out var log)) logs.Add(log);

            try
            {
                foreach (var group in logs.GroupBy(l => l.RecordTime.Date))
                {
                    var filePath = GetDailyFilePath(exportPath, "温度记录", group.Key);
                    bool isNew = !File.Exists(filePath);

                    using var sw = new StreamWriter(filePath, append: true, encoding: new UTF8Encoding(true));

                    if (isNew)
                    {
                        sw.WriteLine("<!doctype html>");
                        sw.WriteLine("<html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
                        sw.WriteLine("<title>温度记录 " + group.Key.ToString("yyyy-MM-dd") + "</title>");
                        sw.WriteLine(GetHtmlStyle());
                        sw.WriteLine("</head><body>");
                        sw.WriteLine("<h1>温度记录 — " + group.Key.ToString("yyyy-MM-dd") + "</h1>");
                        sw.WriteLine("<p class=\"tip\">此文件每 10 秒自动追加一次，用浏览器打开即可查看。</p>");
                        sw.WriteLine("<table><thead><tr>");
                        sw.WriteLine("<th>时间</th><th>设备名</th><th>温度(℃)</th><th>热电偶A(V)</th><th>热电偶B(V)</th><th>热电偶C(V)</th><th>是否异常</th>");
                        sw.WriteLine("</tr></thead>");
                    }

                    foreach (var log in group)
                    {
                        var abnClass = log.IsAbnormal ? " class=\"bad\"" : "";
                        sw.WriteLine("<tr>" +
                            $"<td>{H(log.RecordTime.ToString("HH:mm:ss"))}</td>" +
                            $"<td>{H(log.DeviceName)}</td>" +
                            $"<td{abnClass}>{log.Temperature:F1}</td>" +
                            $"<td>{log.ThermocoupleA:F3}</td>" +
                            $"<td>{log.ThermocoupleB:F3}</td>" +
                            $"<td>{log.ThermocoupleC:F3}</td>" +
                            $"<td>{(log.IsAbnormal ? "⚠ 异常" : "正常")}</td>" +
                            "</tr>");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AutoExport] 温度日志写入失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 程序退出时停止定时器并把剩余日志落盘。多次调用安全。
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try { _flushTimer?.Stop(); _flushTimer?.Dispose(); } catch { }

            // 定时器回调可能还在写文件：最多等 3 秒拿到门，拿不到就放弃（导出文件允许丢尾巴）
            var deadline = Environment.TickCount64 + 3000;
            while (Interlocked.Exchange(ref _isFlushing, 1) == 1)
            {
                if (Environment.TickCount64 > deadline) return;
                Thread.Sleep(20);
            }

            try
            {
                FlushOperationLogs();
                FlushTemperatureLogs();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AutoExport] 退出时落盘失败: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _isFlushing, 0);
            }
        }

        /// <summary>
        /// 获取当日文件路径，目录不存在时自动创建
        /// </summary>
        private static string GetDailyFilePath(string dir, string type, DateTime date)
        {
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            return Path.Combine(dir, $"{date:yyyy-MM-dd}_{type}.html");
        }

        /// <summary>
        /// HTML 转义，防止设备名/标签中的特殊字符破坏页面结构
        /// </summary>
        private static string H(string value) => WebUtility.HtmlEncode(value ?? "");

        /// <summary>
        /// 公共 CSS 样式（写入 head 中）
        /// </summary>
        private static string GetHtmlStyle() => @"<style>
body{font-family:'Microsoft YaHei UI','Microsoft YaHei',Arial,sans-serif;margin:24px;background:#f3f5f7;color:#1f2933}
h1{font-size:22px;margin:0 0 6px}
.tip{font-size:12px;color:#64748b;margin:0 0 16px}
table{border-collapse:collapse;width:100%;background:#fff;font-size:13px}
th,td{border:1px solid #d7dde5;padding:6px 10px;text-align:left;vertical-align:top;white-space:nowrap}
th{background:#e9eef5;color:#111827;position:sticky;top:0}
tr:nth-child(even){background:#f8fafc}
.on{color:#16a34a;font-weight:700}
.off{color:#dc2626;font-weight:700}
.bad{color:#b91c1c;font-weight:700}
</style>";
    }
}
