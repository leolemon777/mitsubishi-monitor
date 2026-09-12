using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text;
using System.Text.Json.Serialization;
using MitsubishiMonitor.Demo.Models;

namespace MitsubishiMonitor.Demo.Services
{
    /// <summary>
    /// 日志批次在提交 SQLite 前持久化，成功后确认删除；退出时也保存待写队列。
    /// </summary>
    internal sealed class DurableLogSpool
    {
        private readonly object _sync = new();
        private readonly string _operationPath;
        private readonly string _temperaturePath;
        private readonly string _deadLetterPath;
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            // 无效数值仍必须能持久化，随后由 LogBuffer 的语义校验隔离到 dead-letter。
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
        private long _pendingCount;

        public DurableLogSpool(string databasePath)
        {
            var dbDirectory = Path.GetDirectoryName(databasePath) ?? AppContext.BaseDirectory;
            var spoolDirectory = Path.Combine(dbDirectory, "Spool");
            Directory.CreateDirectory(spoolDirectory);
            _operationPath = Path.Combine(spoolDirectory, "operation.pending.jsonl");
            _temperaturePath = Path.Combine(spoolDirectory, "temperature.pending.jsonl");
            _deadLetterPath = Path.Combine(spoolDirectory, "dead-letter.jsonl");
            _pendingCount = CountLines(_operationPath) + CountLines(_temperaturePath);
        }

        public long PendingCount
        {
            get { lock (_sync) return _pendingCount; }
        }

        public bool TryAppend(OperationLog log) => TryAppend(_operationPath, log);
        public bool TryAppend(TemperatureLog log) => TryAppend(_temperaturePath, log);
        public bool TryAppendBatch(IReadOnlyList<OperationLog> logs) => TryAppendBatch(_operationPath, logs);
        public bool TryAppendBatch(IReadOnlyList<TemperatureLog> logs) => TryAppendBatch(_temperaturePath, logs);

        public List<OperationLog> PeekOperations(int maxCount)
            => Peek<OperationLog>(_operationPath, maxCount);

        public List<TemperatureLog> PeekTemperatures(int maxCount)
            => Peek<TemperatureLog>(_temperaturePath, maxCount);

        public void AcknowledgeOperations(int count)
            => Acknowledge<OperationLog>(_operationPath, count);

        public void AcknowledgeTemperatures(int count)
            => Acknowledge<TemperatureLog>(_temperaturePath, count);

        public void AppendDeadLetter(string kind, object value, Exception error)
        {
            lock (_sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_deadLetterPath));
                var envelope = JsonSerializer.Serialize(new
                {
                    Time = DateTime.Now,
                    Kind = kind,
                    Error = error?.ToString() ?? "未知错误",
                    Value = value
                }, _jsonOptions);
                File.AppendAllText(_deadLetterPath, envelope + Environment.NewLine);
            }
        }

        private bool TryAppend<T>(string path, T value)
            => TryAppendBatch(path, new[] { value });

        private bool TryAppendBatch<T>(string path, IReadOnlyList<T> values)
        {
            try
            {
                lock (_sync)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                    var originalLength = stream.Length;
                    stream.Position = originalLength;
                    try
                    {
                        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true))
                        {
                            foreach (var value in values)
                                writer.WriteLine(JsonSerializer.Serialize(value, _jsonOptions));
                            writer.Flush();
                            stream.Flush(flushToDisk: true);
                        }
                        _pendingCount += values.Count;
                    }
                    catch
                    {
                        stream.SetLength(originalLength);
                        throw;
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private List<T> Peek<T>(string path, int maxCount)
        {
            if (maxCount <= 0)
                return new List<T>();

            lock (_sync)
            {
                if (!File.Exists(path))
                    return new List<T>();

                var selected = new List<T>();
                var remaining = new List<string>();
                var corruptCount = 0;
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    try
                    {
                        var value = JsonSerializer.Deserialize<T>(line, _jsonOptions)
                            ?? throw new JsonException("spool 记录为空");
                        if (selected.Count < maxCount)
                            selected.Add(value);
                        remaining.Add(line);
                    }
                    catch (Exception ex)
                    {
                        AppendDeadLetter("spool-corrupt", line, ex);
                        corruptCount++;
                    }
                }

                if (corruptCount > 0)
                {
                    Rewrite(path, remaining);
                    _pendingCount = Math.Max(0, _pendingCount - corruptCount);
                }
                return selected;
            }
        }

        private void Acknowledge<T>(string path, int count)
        {
            if (count <= 0)
                return;

            lock (_sync)
            {
                if (!File.Exists(path))
                    return;

                var remaining = new List<string>();
                var acknowledgedValidCount = 0;
                var removedTotalCount = 0;
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    try
                    {
                        _ = JsonSerializer.Deserialize<T>(line, _jsonOptions)
                            ?? throw new JsonException("spool 记录为空");
                        if (acknowledgedValidCount < count)
                        {
                            acknowledgedValidCount++;
                            removedTotalCount++;
                        }
                        else
                            remaining.Add(line);
                    }
                    catch (Exception ex)
                    {
                        AppendDeadLetter("spool-corrupt", line, ex);
                        removedTotalCount++;
                    }
                }

                Rewrite(path, remaining);
                _pendingCount = Math.Max(0, _pendingCount - removedTotalCount);
            }
        }

        private static void Rewrite(string path, List<string> remaining)
        {
            var tempPath = path + ".rewrite.tmp";
            try
            {
                if (remaining.Count == 0)
                {
                    File.WriteAllText(tempPath, "");
                }
                else
                {
                    File.WriteAllLines(tempPath, remaining);
                }
                File.Move(tempPath, path, true);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }

        private static long CountLines(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadLines(path).LongCount(line => !string.IsNullOrWhiteSpace(line)) : 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
