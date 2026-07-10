using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using MitsubishiMonitor.Demo.Models;

namespace MitsubishiMonitor.Demo.Services
{
    /// <summary>
    /// 内存缓存服务 - 减少数据库频繁查询
    /// </summary>
    public class CacheService
    {
        private static readonly Lazy<CacheService> _instance = new(() => new CacheService());
        public static CacheService Instance => _instance.Value;

        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
        private readonly TimeSpan _defaultExpiration = TimeSpan.FromMinutes(5);

        /// <summary>
        /// 缓存最大条目数，防止无上限增长（超出后不再写入新缓存）
        /// </summary>
        private const int MaxEntries = 200;

        /// <summary>
        /// 定时清理：每 10 分钟清除过期条目，防止长时间运行内存泄漏
        /// </summary>
        private readonly Timer _cleanupTimer;

        private CacheService()
        {
            _cleanupTimer = new Timer(TimeSpan.FromMinutes(10).TotalMilliseconds);
            _cleanupTimer.Elapsed += (_, _) => CleanupExpired();
            _cleanupTimer.AutoReset = true;
            _cleanupTimer.Start();
        }

        /// <summary>
        /// 获取缓存
        /// </summary>
        public T Get<T>(string key) where T : class
        {
            if (_cache.TryGetValue(key, out var entry))
            {
                if (entry.IsExpired)
                {
                    _cache.TryRemove(key, out _);
                    return null;
                }
                return entry.Value as T;
            }
            return null;
        }

        /// <summary>
        /// 设置缓存
        /// </summary>
        public void Set<T>(string key, T value, TimeSpan? expiration = null) where T : class
        {
            // 超过最大条目数时先清理过期条目，若仍超限则跳过写入
            if (_cache.Count >= MaxEntries)
            {
                CleanupExpired();
                if (_cache.Count >= MaxEntries)
                {
                    System.Diagnostics.Debug.WriteLine($"[Cache] 条目已满({MaxEntries})，跳过写入 key={key}");
                    return;
                }
            }

            var entry = new CacheEntry
            {
                Value = value,
                ExpirationTime = DateTime.Now.Add(expiration ?? _defaultExpiration)
            };
            _cache[key] = entry;
        }

        /// <summary>
        /// 移除缓存
        /// </summary>
        public void Remove(string key)
        {
            _cache.TryRemove(key, out _);
        }

        /// <summary>
        /// 清空所有缓存
        /// </summary>
        public void Clear()
        {
            _cache.Clear();
        }

        /// <summary>
        /// 清理过期缓存
        /// </summary>
        public void CleanupExpired()
        {
            var expiredKeys = _cache.Where(kvp => kvp.Value.IsExpired).Select(kvp => kvp.Key).ToList();
            foreach (var key in expiredKeys)
            {
                _cache.TryRemove(key, out _);
            }
        }

        /// <summary>
        /// 生成操作日志缓存Key
        /// </summary>
        public static string GetOperationLogsKey(int deviceId, DateTime start, DateTime end)
            => $"operationlogs_{deviceId}_{start:yyyyMMddHHmmss}_{end:yyyyMMddHHmmss}";

        /// <summary>
        /// 生成温度日志缓存Key
        /// </summary>
        public static string GetTemperatureLogsKey(int deviceId, DateTime start, DateTime end)
            => $"templogs_{deviceId}_{start:yyyyMMddHHmmss}_{end:yyyyMMddHHmmss}";

        /// <summary>
        /// 缓存项
        /// </summary>
        private class CacheEntry
        {
            public object Value { get; set; }
            public DateTime ExpirationTime { get; set; }
            public bool IsExpired => DateTime.Now > ExpirationTime;
        }
    }

    /// <summary>
    /// 缓存扩展方法
    /// </summary>
    public static class CacheExtensions
    {
        public static T GetOrLoad<T>(
            this CacheService cache,
            string key,
            Func<T> loader,
            TimeSpan? expiration = null) where T : class
        {
            var cached = cache.Get<T>(key);
            if (cached != null)
                return cached;

            cached = loader();
            if (cached != null)
                cache.Set(key, cached, expiration);

            return cached;
        }

        public static async Task<T> GetOrLoadAsync<T>(
            this CacheService cache,
            string key,
            Func<Task<T>> loader,
            TimeSpan? expiration = null) where T : class
        {
            var cached = cache.Get<T>(key);
            if (cached != null)
                return cached;

            cached = await loader();
            if (cached != null)
                cache.Set(key, cached, expiration);

            return cached;
        }
    }
}
