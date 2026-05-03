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

        static AppConfig()
        {
            Load();
        }

        private static void Load()
        {
            SavedDatabasePath = "";

            // 默认 IP（万一 config.json 里没有 DeviceIPs 字段）
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

                    if (doc.RootElement.TryGetProperty("DeviceIPs", out var ipsElement)
                        && ipsElement.ValueKind == JsonValueKind.Array)
                    {
                        var ips = new List<string>();
                        foreach (var item in ipsElement.EnumerateArray())
                            ips.Add(item.GetString() ?? "");

                        // 不足 4 个时用默认补齐
                        for (int i = ips.Count; i < 4; i++)
                            ips.Add(defaultIPs[i]);

                        DeviceIPs = ips.ToArray();
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
        }

        public static void SaveDatabasePath(string path)
        {
            try
            {
                // 重新写 config.json，保留 DeviceIPs
                var config = new
                {
                    DatabasePath = path ?? "",
                    DeviceIPs
                };
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigFilePath, json);
                SavedDatabasePath = path ?? "";
                System.Diagnostics.Debug.WriteLine($"[配置] 已保存数据库路径: {(string.IsNullOrEmpty(path) ? "(默认)" : path)}");
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
                        DeviceIPs = new[] { "192.168.1.5", "192.168.1.10", "192.168.1.15", "192.168.1.20" }
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
