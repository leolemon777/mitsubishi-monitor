using System;
using Microsoft.EntityFrameworkCore;
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
        private readonly string _dbPath = AppConfig.DatabasePath;

        public MonitorDbContext()
        {
            // 确保目录存在
            var directory = System.IO.Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseSqlite($"Data Source={_dbPath};Journal Mode=WAL;");
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
                entity.HasIndex(e => e.RecordTime);
                entity.HasIndex(e => e.DeviceId);
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
            });
        }

        public DbSet<TemperatureLog> TemperatureLogs { get; set; } = null!;
        public DbSet<OperationLog> OperationLogs { get; set; } = null!;

        /// <summary>
        /// 兼容老库：在已有数据库上为缺失列执行 ALTER TABLE ADD COLUMN。
        /// 因为项目用 EnsureCreated（不是 Migrations），新增字段不会自动落库；
        /// 这个方法在程序启动时由 DataService.InitializeAsync 调用一次即可。
        /// 调用顺序必须在 EnsureCreatedAsync 之后，新表已建好的前提下补漏。
        /// </summary>
        public void EnsureSchemaUpgraded()
        {
            // SQLite 没有 "ADD COLUMN IF NOT EXISTS"，需要先用 PRAGMA table_info 判定列是否存在
            TryAddColumn("OperationLog", "DeviceName", "TEXT");
            TryAddColumn("OperationLog", "PointLabel", "TEXT");
            TryAddColumn("TemperatureLog", "DeviceName", "TEXT");
        }

        private void TryAddColumn(string table, string column, string sqlType)
        {
            try
            {
                using var conn = Database.GetDbConnection();
                if (conn.State != System.Data.ConnectionState.Open)
                    conn.Open();

                bool exists = false;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"PRAGMA table_info({table});";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        // PRAGMA table_info 返回列：cid, name, type, notnull, dflt_value, pk
                        var name = reader.GetString(1);
                        if (string.Equals(name, column, StringComparison.OrdinalIgnoreCase))
                        {
                            exists = true;
                            break;
                        }
                    }
                }

                if (!exists)
                {
                    using var alter = conn.CreateCommand();
                    alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {sqlType};";
                    alter.ExecuteNonQuery();
                    System.Diagnostics.Debug.WriteLine($"[DB升级] {table} 添加列 {column} ({sqlType})");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DB升级] {table}.{column} 检查/添加失败: {ex.Message}");
            }
        }
    }
}
