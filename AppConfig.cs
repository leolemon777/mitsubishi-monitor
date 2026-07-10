using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MitsubishiMonitor.Demo
{
    public static class AppConfig
    {
        private static readonly string ConfigFilePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "config.json");

        private static readonly string DefaultDbPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Data", "Monitor.db");

        public static string SavedDatabasePath { get; private set; }
        public static string DatabasePath { get; private set; }

        /// <summary>
        /// 自动导出 HTML 的目标文件夹路径，空表示不启用
        /// </summary>
        public static string AutoExportPath { get; private set; } = "";

        /// <summary>
        /// 设备 IP 地址列表（索引 0=1号机，1=2号机，2=3号机，3=4号机）
        /// 从 config.json 的 DeviceIPs 读取，缺失时使用默认值
        /// </summary>
        public static string[] DeviceIPs { get; private set; } = new[]
        {
            "192.168.1.5",
            "192.168.1.10",
            "192.168.1.15",
            "192.168.1.20"
        };

        /// <summary>
        /// 各设备温度报警阈值（索引 0=1号机，默认 90°C）
        /// 对应设备详情页"温度报警阈值"输入框，持久化到 config.json
        /// </summary>
        public static float[] DeviceThresholds { get; private set; } = new float[] { 90f, 90f, 90f, 90f };

        static AppConfig()
        {
            Load();
        }

        private static void Load()
        {
            SavedDatabasePath = "";
            var defaultIPs = new[] { "192.168.1.5", "192.168.1.10", "192.168.1.15", "192.168.1.20" };

            try
            {
                if (!File.Exists(ConfigFilePath))
                {
                    System.Diagnostics.Debug.WriteLine($"[配置] config.json 不存在，使用默认配置");
                    CreateDefaultConfig();
                }
                else
                {
                    var json = File.ReadAllText(ConfigFilePath);
                    using var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("DatabasePath", out var pathElement))
                        SavedDatabasePath = pathElement.GetString() ?? "";

                    if (doc.RootElement.TryGetProperty("AutoExportPath", out var exportPathElement))
                        AutoExportPath = exportPathElement.GetString() ?? "";

                    if (doc.RootElement.TryGetProperty("DeviceIPs", out var ipsElement)
                        && ipsElement.ValueKind == JsonValueKind.Array)
                    {
                        var ips = new List<string>();
                        foreach (var item in ipsElement.EnumerateArray())
                            ips.Add(item.GetString() ?? "");
                        for (int i = ips.Count; i < 4; i++)
                            ips.Add(defaultIPs[i]);
                        DeviceIPs = ips.ToArray();
                    }

                    // 读取各设备报警阈值
                    if (doc.RootElement.TryGetProperty("DeviceThresholds", out var threshEl)
                        && threshEl.ValueKind == JsonValueKind.Array)
                    {
                        var thresholds = new List<float>();
                        foreach (var item in threshEl.EnumerateArray())
                            thresholds.Add(item.GetSingle());
                        for (int i = thresholds.Count; i < 4; i++)
                            thresholds.Add(90f);
                        DeviceThresholds = thresholds.ToArray();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[配置] 读取 config.json 失败: {ex.Message}");
            }

            DatabasePath = string.IsNullOrWhiteSpace(SavedDatabasePath)
                ? DefaultDbPath
                : SavedDatabasePath;

            System.Diagnostics.Debug.WriteLine($"[配置] 数据库路径: {DatabasePath}");
            System.Diagnostics.Debug.WriteLine($"[配置] 设备IP: {string.Join(", ", DeviceIPs)}");
            System.Diagnostics.Debug.WriteLine($"[配置] 报警阈值: {string.Join(", ", DeviceThresholds)}");
        }

        /// <summary>
        /// 保存单个设备的报警阈值并持久化（deviceIndex 从 0 开始，对应设备 Id-1）
        /// </summary>
        public static void SaveDeviceThreshold(int deviceIndex, float threshold)
        {
            if (deviceIndex < 0 || deviceIndex >= 4) return;
            DeviceThresholds[deviceIndex] = threshold;
            WriteConfig();
            System.Diagnostics.Debug.WriteLine($"[配置] 设备{deviceIndex + 1}报警阈值已保存: {threshold}°C");
        }

        public static void SaveDatabasePath(string path)
        {
            SavedDatabasePath = path ?? "";
            DatabasePath = string.IsNullOrWhiteSpace(SavedDatabasePath) ? DefaultDbPath : SavedDatabasePath;
            WriteConfig();
            System.Diagnostics.Debug.WriteLine($"[配置] 已保存数据库路径: {(string.IsNullOrEmpty(path) ? "(默认)" : path)}");
        }

        /// <summary>
        /// 保存自动导出路径到 config.json
        /// </summary>
        public static void SaveAutoExportPath(string path)
        {
            AutoExportPath = path ?? "";
            WriteConfig();
            System.Diagnostics.Debug.WriteLine($"[配置] 已保存自动导出路径: {(string.IsNullOrEmpty(path) ? "(未启用)" : path)}");
        }

        /// <summary>
        /// 统一写入 config.json（所有字段一次性写入，避免字段丢失）
        /// </summary>
        private static void WriteConfig()
        {
            try
            {
                var config = new
                {
                    DatabasePath = SavedDatabasePath ?? "",
                    AutoExportPath,
                    DeviceIPs,
                    DeviceThresholds
                };
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[配置] 保存 config.json 失败: {ex.Message}");
                throw;
            }
        }

        private static void CreateDefaultConfig()
        {
            try
            {
                var defaultContent = JsonSerializer.Serialize(
                    new
                    {
                        DatabasePath = "",
                        AutoExportPath = "",
                        DeviceIPs = new[] { "192.168.1.5", "192.168.1.10", "192.168.1.15", "192.168.1.20" },
                        DeviceThresholds = new float[] { 90f, 90f, 90f, 90f }
                    },
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigFilePath, defaultContent);
                System.Diagnostics.Debug.WriteLine($"[配置] 已生成默认 config.json: {ConfigFilePath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[配置] 生成默认 config.json 失败: {ex.Message}");
            }
        }
    }
}
