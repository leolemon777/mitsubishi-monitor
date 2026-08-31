using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using MitsubishiMonitor.Demo.Models;

namespace MitsubishiMonitor.Demo
{
    /// <summary>
    /// 应用配置的唯一入口。生产配置必须完整通过语义校验；保存采用同目录临时文件加原子替换。
    /// 数据库路径只在下次启动生效，避免运行中的 DbContext 与静态配置状态分裂。
    /// </summary>
    public static class AppConfig
    {
        private const int DeviceCount = 4;
        private const float MinimumThreshold = 0f;
        private const float MaximumThreshold = 500f;

        private static readonly object ConfigSync = new();
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        static AppConfig()
        {
            JsonOptions.Converters.Add(new JsonStringEnumConverter());
            Load();
        }

        private static readonly string ConfigFilePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "config.json");
        private static readonly string BackupConfigFilePath = ConfigFilePath + ".last-known-good";
        private static readonly string DefaultDbPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Data", "Monitor.db");

        private static readonly string[] DefaultDeviceIps =
        {
            "192.168.1.5",
            "192.168.1.10",
            "192.168.1.15",
            "192.168.1.20"
        };

        private static readonly float[] DefaultThresholds = { 90f, 90f, 90f, 90f };
        private static readonly DeviceMonitoringMode[] DefaultMonitoringModes =
        {
            DeviceMonitoringMode.AutoStandby,
            DeviceMonitoringMode.AutoStandby,
            DeviceMonitoringMode.AutoStandby,
            DeviceMonitoringMode.AutoStandby
        };

        public static string SavedDatabasePath { get; private set; } = "";

        /// <summary>当前进程实际使用的数据库路径；设置页保存新路径时不会热切换此值。</summary>
        public static string DatabasePath { get; private set; } = DefaultDbPath;

        public static string AutoExportPath { get; private set; } = "";
        public static string[] DeviceIPs { get; private set; } = (string[])DefaultDeviceIps.Clone();
        public static float[] DeviceThresholds { get; private set; } = (float[])DefaultThresholds.Clone();
        public static DeviceMonitoringMode[] DeviceMonitoringModes { get; private set; } =
            (DeviceMonitoringMode[])DefaultMonitoringModes.Clone();

        public static bool IsConfigurationValid { get; private set; }
        public static string ConfigurationError { get; private set; } = "";
        public static string ConfigurationWarning { get; private set; } = "";
        public static bool LoadedFromBackup { get; private set; }
        public static bool RequiresRestartForDatabasePathChange { get; private set; }
        public static bool IsDemoIsolationActive { get; private set; }

        /// <summary>
        /// 将演示运行时切换到每进程独立的临时数据库，并强制关闭自动导出。
        /// 不修改或覆盖生产 config.json。
        /// </summary>
        public static void EnableDemoIsolation()
        {
            lock (ConfigSync)
            {
                var demoRoot = Path.Combine(
                    Path.GetTempPath(),
                    "MitsubishiMonitor",
                    "Demo",
                    Environment.ProcessId.ToString());
                Directory.CreateDirectory(demoRoot);

                DatabasePath = Path.Combine(demoRoot, "Monitor.demo.db");
                AutoExportPath = "";
                IsDemoIsolationActive = true;
                IsConfigurationValid = true;
                ConfigurationError = "";
                ConfigurationWarning = "演示模式使用独立临时数据库，未连接现场 PLC";
            }
        }

        private static void Load()
        {
            lock (ConfigSync)
            {
                ResetToSafeDefaults();

                if (!File.Exists(ConfigFilePath))
                {
                    try
                    {
                        WriteDocumentAtomic(CreateDefaultDocument());
                    }
                    catch (Exception ex)
                    {
                        ConfigurationError = $"配置文件不存在，且生成模板失败：{ex.Message}";
                        return;
                    }

                    ConfigurationError = "配置文件原先不存在，已生成默认模板；请确认现场 IP 和阈值并保存后再连接 PLC";
                    return;
                }

                if (TryReadValidatedDocument(ConfigFilePath, out var document, out var primaryError))
                {
                    ApplyDocument(document);
                    IsConfigurationValid = true;
                    return;
                }

                if (TryReadValidatedDocument(BackupConfigFilePath, out document, out var backupError))
                {
                    ApplyDocument(document);
                    IsConfigurationValid = true;
                    LoadedFromBackup = true;
                    ConfigurationWarning = $"主配置无效，已使用最近有效备份：{primaryError}";
                    return;
                }

                ConfigurationError =
                    $"配置无效，已禁止 PLC 连接。主配置：{primaryError}；备份：{backupError}";
            }
        }

        public static void SaveDeviceThreshold(int deviceIndex, float threshold)
        {
            if (deviceIndex < 0 || deviceIndex >= DeviceCount)
                throw new ArgumentOutOfRangeException(nameof(deviceIndex));
            if (!float.IsFinite(threshold) || threshold < MinimumThreshold || threshold > MaximumThreshold)
                throw new ArgumentOutOfRangeException(nameof(threshold), "报警阈值必须是 0～500 之间的有限数值");

            lock (ConfigSync)
            {
                EnsureProductionPersistenceAllowed();
                var document = CreateCurrentDocument();
                var thresholds = (float[])document.DeviceThresholds.Clone();
                thresholds[deviceIndex] = threshold;
                document.DeviceThresholds = thresholds;
                SaveValidatedDocument(document);
                DeviceThresholds = thresholds;
            }
        }

        /// <summary>保存下次启动使用的数据库路径；当前进程的 DatabasePath 保持不变。</summary>
        public static void SaveDatabasePath(string path)
        {
            lock (ConfigSync)
            {
                EnsureProductionPersistenceAllowed();
                var normalized = NormalizeOptionalPath(path);
                var document = CreateCurrentDocument();
                document.DatabasePath = normalized;
                SaveValidatedDocument(document);

                SavedDatabasePath = normalized;
                var nextRuntimePath = ResolveDatabasePath(normalized);
                RequiresRestartForDatabasePathChange = !string.Equals(
                    DatabasePath,
                    nextRuntimePath,
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        public static void SaveAutoExportPath(string path)
        {
            lock (ConfigSync)
            {
                EnsureProductionPersistenceAllowed();
                var normalized = NormalizeOptionalPath(path);
                var document = CreateCurrentDocument();
                document.AutoExportPath = normalized;
                SaveValidatedDocument(document);
                AutoExportPath = normalized;
            }
        }

        /// <summary>
        /// 一次性保存数据库路径与自动导出路径，避免其中一项成功、另一项失败造成部分提交。
        /// 数据库路径仍只在下次启动生效；自动导出路径由调用方在落盘成功后切换运行实例。
        /// </summary>
        public static void SaveStorageSettings(
            string databasePath,
            string autoExportPath,
            DeviceMonitoringMode[] monitoringModes = null)
        {
            lock (ConfigSync)
            {
                EnsureProductionPersistenceAllowed();
                var normalizedDatabasePath = NormalizeOptionalPath(databasePath);
                var normalizedAutoExportPath = NormalizeOptionalPath(autoExportPath);
                var document = CreateCurrentDocument();
                document.DatabasePath = normalizedDatabasePath;
                document.AutoExportPath = normalizedAutoExportPath;
                if (monitoringModes != null)
                    document.DeviceMonitoringModes = (DeviceMonitoringMode[])monitoringModes.Clone();
                SaveValidatedDocument(document);

                SavedDatabasePath = normalizedDatabasePath;
                AutoExportPath = normalizedAutoExportPath;
                DeviceMonitoringModes = (DeviceMonitoringMode[])document.DeviceMonitoringModes.Clone();
                var nextRuntimePath = ResolveDatabasePath(normalizedDatabasePath);
                RequiresRestartForDatabasePathChange = !string.Equals(
                    DatabasePath,
                    nextRuntimePath,
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        public static void SaveDeviceMonitoringMode(int deviceIndex, DeviceMonitoringMode mode)
        {
            if (deviceIndex < 0 || deviceIndex >= DeviceCount)
                throw new ArgumentOutOfRangeException(nameof(deviceIndex));
            if (!Enum.IsDefined(mode))
                throw new ArgumentOutOfRangeException(nameof(mode));

            lock (ConfigSync)
            {
                EnsureProductionPersistenceAllowed();
                var document = CreateCurrentDocument();
                var modes = (DeviceMonitoringMode[])document.DeviceMonitoringModes.Clone();
                modes[deviceIndex] = mode;
                document.DeviceMonitoringModes = modes;
                SaveValidatedDocument(document);
                DeviceMonitoringModes = modes;
            }
        }

        public static void SaveDeviceIPs(string[] deviceIps)
        {
            lock (ConfigSync)
            {
                EnsureProductionPersistenceAllowed();
                var document = CreateCurrentDocument();
                document.DeviceIPs = deviceIps?.Select(ip => ip?.Trim() ?? "").ToArray();
                SaveValidatedDocument(document);
                DeviceIPs = (string[])document.DeviceIPs.Clone();
            }
        }

        private static void SaveValidatedDocument(ConfigurationDocument document)
        {
            if (!TryValidateDocument(document, out var error))
                throw new InvalidOperationException(error);

            WriteDocumentAtomic(document);
            IsConfigurationValid = true;
            ConfigurationError = "";
            ConfigurationWarning = "";
            LoadedFromBackup = false;
        }

        private static void EnsureProductionPersistenceAllowed()
        {
            if (IsDemoIsolationActive)
                throw new InvalidOperationException("演示模式不允许修改生产配置");
        }

        private static void ResetToSafeDefaults()
        {
            SavedDatabasePath = "";
            DatabasePath = DefaultDbPath;
            AutoExportPath = "";
            DeviceIPs = (string[])DefaultDeviceIps.Clone();
            DeviceThresholds = (float[])DefaultThresholds.Clone();
            DeviceMonitoringModes = (DeviceMonitoringMode[])DefaultMonitoringModes.Clone();
            IsConfigurationValid = false;
            ConfigurationError = "";
            ConfigurationWarning = "";
            LoadedFromBackup = false;
            RequiresRestartForDatabasePathChange = false;
            IsDemoIsolationActive = false;
        }

        private static ConfigurationDocument CreateDefaultDocument()
            => new()
            {
                DatabasePath = "",
                AutoExportPath = "",
                DeviceIPs = (string[])DefaultDeviceIps.Clone(),
                DeviceThresholds = (float[])DefaultThresholds.Clone(),
                DeviceMonitoringModes = (DeviceMonitoringMode[])DefaultMonitoringModes.Clone()
            };

        private static ConfigurationDocument CreateCurrentDocument()
            => new()
            {
                DatabasePath = SavedDatabasePath ?? "",
                AutoExportPath = AutoExportPath ?? "",
                DeviceIPs = (string[])DeviceIPs.Clone(),
                DeviceThresholds = (float[])DeviceThresholds.Clone(),
                DeviceMonitoringModes = (DeviceMonitoringMode[])DeviceMonitoringModes.Clone()
            };

        private static void ApplyDocument(ConfigurationDocument document)
        {
            SavedDatabasePath = document.DatabasePath ?? "";
            DatabasePath = ResolveDatabasePath(SavedDatabasePath);
            AutoExportPath = document.AutoExportPath ?? "";
            DeviceIPs = (string[])document.DeviceIPs.Clone();
            DeviceThresholds = (float[])document.DeviceThresholds.Clone();
            DeviceMonitoringModes = (DeviceMonitoringMode[])document.DeviceMonitoringModes.Clone();
            RequiresRestartForDatabasePathChange = false;
        }

        private static string ResolveDatabasePath(string savedPath)
            => string.IsNullOrWhiteSpace(savedPath) ? DefaultDbPath : Path.GetFullPath(savedPath);

        private static string NormalizeOptionalPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "";
            return Path.GetFullPath(path.Trim());
        }

        private static bool TryReadValidatedDocument(
            string path,
            out ConfigurationDocument document,
            out string error)
        {
            document = null;
            error = "文件不存在";
            if (!File.Exists(path))
                return false;

            try
            {
                var json = File.ReadAllText(path);
                document = JsonSerializer.Deserialize<ConfigurationDocument>(json, JsonOptions);
                return TryValidateDocument(document, out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal static bool TryValidateDocument(ConfigurationDocument document, out string error)
        {
            if (document == null)
            {
                error = "配置内容为空";
                return false;
            }

            if (document.DeviceIPs == null || document.DeviceIPs.Length != DeviceCount)
            {
                error = $"DeviceIPs 必须恰好包含 {DeviceCount} 个地址";
                return false;
            }

            var normalizedIps = document.DeviceIPs.Select(ip => ip?.Trim() ?? "").ToArray();
            for (var index = 0; index < normalizedIps.Length; index++)
            {
                if (!IPAddress.TryParse(normalizedIps[index], out var address) ||
                    address.AddressFamily != AddressFamily.InterNetwork ||
                    IPAddress.Any.Equals(address) ||
                    IPAddress.None.Equals(address))
                {
                    error = $"第 {index + 1} 台设备 IP 无效：{normalizedIps[index]}";
                    return false;
                }
            }

            normalizedIps = normalizedIps
                .Select(ip => IPAddress.Parse(ip).ToString())
                .ToArray();
            if (normalizedIps.Distinct(StringComparer.OrdinalIgnoreCase).Count() != DeviceCount)
            {
                error = "四台设备 IP 不能重复";
                return false;
            }
            document.DeviceIPs = normalizedIps;

            if (document.DeviceThresholds == null || document.DeviceThresholds.Length != DeviceCount)
            {
                error = $"DeviceThresholds 必须恰好包含 {DeviceCount} 个阈值";
                return false;
            }

            for (var index = 0; index < document.DeviceThresholds.Length; index++)
            {
                var threshold = document.DeviceThresholds[index];
                if (!float.IsFinite(threshold) || threshold < MinimumThreshold || threshold > MaximumThreshold)
                {
                    error = $"第 {index + 1} 台设备报警阈值必须是 0～500 之间的有限数值";
                    return false;
                }
            }

            // 兼容 1.2.0 及更早版本：缺少该字段时安全迁移为四台“自动待机”。
            if (document.DeviceMonitoringModes == null || document.DeviceMonitoringModes.Length == 0)
            {
                document.DeviceMonitoringModes =
                    (DeviceMonitoringMode[])DefaultMonitoringModes.Clone();
            }
            else if (document.DeviceMonitoringModes.Length != DeviceCount)
            {
                error = $"DeviceMonitoringModes 必须恰好包含 {DeviceCount} 个模式";
                return false;
            }

            for (var index = 0; index < document.DeviceMonitoringModes.Length; index++)
            {
                if (!Enum.IsDefined(document.DeviceMonitoringModes[index]))
                {
                    error = $"第 {index + 1} 台设备运行模式无效";
                    return false;
                }
            }

            if (!TryValidateOptionalPath(document.DatabasePath, expectDatabaseFile: true, out error) ||
                !TryValidateOptionalPath(document.AutoExportPath, expectDatabaseFile: false, out error))
                return false;

            document.DatabasePath = NormalizeOptionalPath(document.DatabasePath);
            document.AutoExportPath = NormalizeOptionalPath(document.AutoExportPath);
            error = "";
            return true;
        }

        private static bool TryValidateOptionalPath(
            string path,
            bool expectDatabaseFile,
            out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(path))
                return true;

            try
            {
                var trimmedPath = path.Trim();
                if (!Path.IsPathFullyQualified(trimmedPath))
                {
                    error = $"路径必须是绝对路径：{path}";
                    return false;
                }

                var fullPath = Path.GetFullPath(trimmedPath);

                if (expectDatabaseFile &&
                    !fullPath.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                {
                    error = "数据库路径必须以 .db 结尾";
                    return false;
                }

                if (expectDatabaseFile && fullPath.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    error = "SQLite 数据库不能放在 UNC 网络共享路径；请使用本机磁盘，网络目录仅用于辅助导出";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = $"路径无效：{ex.Message}";
                return false;
            }
        }

        private static void WriteDocumentAtomic(ConfigurationDocument document)
        {
            var directory = Path.GetDirectoryName(ConfigFilePath)
                ?? throw new InvalidOperationException("无法确定配置目录");
            Directory.CreateDirectory(directory);

            var temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(ConfigFilePath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                var json = JsonSerializer.Serialize(document, JsonOptions);
                File.WriteAllText(temporaryPath, json);

                if (!TryReadValidatedDocument(temporaryPath, out _, out var validationError))
                    throw new InvalidOperationException($"配置落盘校验失败：{validationError}");

                if (File.Exists(ConfigFilePath))
                {
                    var replacementBackupPath = BackupConfigFilePath +
                        ".pending-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        // File.Replace 先把旧主配置保存到唯一候选备份。主配置替换成功后再覆盖
                        // 固定备份名；若第二步失败，原 last-known-good 仍然保留。
                        File.Replace(temporaryPath, ConfigFilePath, replacementBackupPath, true);
                        File.Move(replacementBackupPath, BackupConfigFilePath, overwrite: true);
                    }
                    finally
                    {
                        if (File.Exists(replacementBackupPath))
                            File.Delete(replacementBackupPath);
                    }
                }
                else
                {
                    File.Move(temporaryPath, ConfigFilePath);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }

        internal sealed class ConfigurationDocument
        {
            public string DatabasePath { get; set; } = "";
            public string AutoExportPath { get; set; } = "";
            public string[] DeviceIPs { get; set; } = Array.Empty<string>();
            public float[] DeviceThresholds { get; set; } = Array.Empty<float>();
            public DeviceMonitoringMode[] DeviceMonitoringModes { get; set; } =
                Array.Empty<DeviceMonitoringMode>();
        }
    }
}
