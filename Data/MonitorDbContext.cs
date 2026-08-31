using System;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MitsubishiMonitor.Demo.Models;

namespace MitsubishiMonitor.Demo.Data
{
    /// <summary>
    /// 数据库上下文
    /// </summary>
    public class MonitorDbContext : DbContext
    {
        /// <summary>
        /// 数据库文件路径，优先使用 config.json 中的配置，回退到程序目录 Data\Monitor.db
        /// </summary>
        private readonly string _dbPath;

        public MonitorDbContext() : this(AppConfig.DatabasePath)
        {
        }

        internal MonitorDbContext(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("数据库路径不能为空", nameof(databasePath));
            _dbPath = System.IO.Path.GetFullPath(databasePath);

            // 确保目录存在
            var directory = System.IO.Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                DefaultTimeout = 5,
            }.ToString();
            optionsBuilder.UseSqlite(connectionString);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // TemperatureLog配置
            modelBuilder.Entity<TemperatureLog>(entity =>
            {
                entity.ToTable("TemperatureLog");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Temperature).IsRequired();
                entity.Property(e => e.RecordTime).IsRequired();
                entity.Property(e => e.DeviceName).HasMaxLength(50);
                entity.Property(e => e.AlarmThreshold).IsRequired();
                entity.Property(e => e.TargetTemperature).IsRequired();
                entity.HasIndex(e => e.RecordTime);
                entity.HasIndex(e => e.DeviceId);
                entity.HasIndex(e => new { e.DeviceId, e.RecordTime });
            });

            // OperationLog配置
            modelBuilder.Entity<OperationLog>(entity =>
            {
                entity.ToTable("OperationLog");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.LogType).HasMaxLength(10).IsRequired();
                entity.Property(e => e.PointAddress).HasMaxLength(10);
                entity.Property(e => e.PointLabel).HasMaxLength(100);
                entity.Property(e => e.DeviceName).HasMaxLength(50);
                entity.Property(e => e.Action).HasMaxLength(50);
                entity.Property(e => e.Description).HasMaxLength(200);
                entity.Property(e => e.LogTime).IsRequired();
                entity.HasIndex(e => e.LogTime);
                entity.HasIndex(e => e.DeviceId);
                entity.HasIndex(e => new { e.DeviceId, e.LogTime });
            });
        }

        public DbSet<TemperatureLog> TemperatureLogs { get; set; } = null!;
        public DbSet<OperationLog> OperationLogs { get; set; } = null!;

        /// <summary>
        /// 版本化升级旧库。所有升级在事务中执行，任何必要列/索引失败都会抛出，
        /// 调用方不得再把数据库标记为已就绪。
        /// </summary>
        public void EnsureSchemaUpgraded()
        {
            Database.OpenConnection();
            using var transaction = Database.BeginTransaction();

            ExecuteNonQuery("CREATE TABLE IF NOT EXISTS SchemaInfo (Key TEXT PRIMARY KEY, Value INTEGER NOT NULL);");
            ExecuteNonQuery("INSERT OR IGNORE INTO SchemaInfo (Key, Value) VALUES ('SchemaVersion', 0);");
            var version = ReadSchemaVersion();

            if (version < 1)
            {
                AddColumnIfMissing("OperationLog", "DeviceName", "TEXT");
                AddColumnIfMissing("OperationLog", "PointLabel", "TEXT");
                AddColumnIfMissing("TemperatureLog", "DeviceName", "TEXT");
                CreateIndex("IX_OperationLog_DeviceId_LogTime", "OperationLog", "DeviceId, LogTime");
                CreateIndex("IX_TemperatureLog_DeviceId_RecordTime", "TemperatureLog", "DeviceId, RecordTime");
                version = 1;
                WriteSchemaVersion(version);
            }

            if (version < 2)
            {
                AddColumnIfMissing("TemperatureLog", "AlarmThreshold", "REAL NOT NULL DEFAULT 90");
                AddColumnIfMissing("TemperatureLog", "TargetTemperature", "REAL NOT NULL DEFAULT 0");
                AddColumnIfMissing("TemperatureLog", "AuxiliarySampleTime", "TEXT NULL");
                AddColumnIfMissing("TemperatureLog", "HasFreshAuxiliaryData", "INTEGER NOT NULL DEFAULT 0");
                version = 2;
                WriteSchemaVersion(version);
            }

            ValidateRequiredSchema();
            transaction.Commit();
        }

        private void AddColumnIfMissing(string table, string column, string sqlType)
        {
            if (!ColumnExists(table, column))
            {
                ExecuteNonQuery($"ALTER TABLE {table} ADD COLUMN {column} {sqlType};");
                System.Diagnostics.Debug.WriteLine($"[DB升级] {table} 添加列 {column} ({sqlType})");
            }
        }

        private void CreateIndex(string indexName, string table, string columns)
        {
            ExecuteNonQuery($"CREATE INDEX IF NOT EXISTS {indexName} ON {table} ({columns});");
        }

        private bool ColumnExists(string table, string column)
        {
            using var command = CreateCommand($"PRAGMA table_info({table});");
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private int ReadSchemaVersion()
        {
            using var command = CreateCommand("SELECT Value FROM SchemaInfo WHERE Key='SchemaVersion';");
            return Convert.ToInt32(command.ExecuteScalar() ?? 0);
        }

        private void WriteSchemaVersion(int version)
        {
            using var command = CreateCommand("UPDATE SchemaInfo SET Value=$version WHERE Key='SchemaVersion';");
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$version";
            parameter.Value = version;
            command.Parameters.Add(parameter);
            command.ExecuteNonQuery();
        }

        private void ValidateRequiredSchema()
        {
            var requirements = new[]
            {
                ("OperationLog", "DeviceName"),
                ("OperationLog", "PointLabel"),
                ("TemperatureLog", "DeviceName"),
                ("TemperatureLog", "AlarmThreshold"),
                ("TemperatureLog", "TargetTemperature"),
                ("TemperatureLog", "AuxiliarySampleTime"),
                ("TemperatureLog", "HasFreshAuxiliaryData")
            };

            foreach (var requirement in requirements)
            {
                if (!ColumnExists(requirement.Item1, requirement.Item2))
                    throw new InvalidOperationException($"数据库升级后仍缺少 {requirement.Item1}.{requirement.Item2}");
            }
        }

        private void ExecuteNonQuery(string sql)
        {
            using var command = CreateCommand(sql);
            command.ExecuteNonQuery();
        }

        private System.Data.Common.DbCommand CreateCommand(string sql)
        {
            var command = Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            var currentTransaction = Database.CurrentTransaction;
            if (currentTransaction != null)
                command.Transaction = currentTransaction.GetDbTransaction();
            return command;
        }
    }
}
