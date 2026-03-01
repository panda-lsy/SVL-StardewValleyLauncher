using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SVL.Core.Logging;

namespace SVL.Core.IO;

/// <summary>
/// 通用搜索缓存（支持 TTL）。
/// - 用于 NexusMods / Curseforge / GitHub 等搜索/列表类 API 的结果缓存。
/// - 以“source + key”为维度落盘，便于统计条数与按来源清理。
/// </summary>
public static class SearchCacheService
{
    private static readonly string CacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SVL",
        "cache",
        "search"
    );

    private sealed class CacheEnvelope
    {
        public DateTime CreatedUtc { get; set; }
        public string PayloadJson { get; set; } = string.Empty;
    }

    private static readonly ConcurrentDictionary<string, CacheEnvelope> Memory = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsEnabled { get; set; } = true;

    public static TimeSpan DefaultTtl { get; set; } = TimeSpan.FromHours(1);

    public static bool TryGet<T>(string source, string key, out T? value, TimeSpan? ttl = null)
    {
        value = default;
        if (!IsEnabled)
            return false;

        ttl ??= DefaultTtl;
        if (ttl.Value <= TimeSpan.Zero)
            return false;

        var cacheId = BuildCacheId(source, key);

        // 1) 内存
        if (Memory.TryGetValue(cacheId, out var env))
        {
            if (IsExpired(env.CreatedUtc, ttl.Value))
            {
                Memory.TryRemove(cacheId, out _);
            }
            else
            {
                try
                {
                    value = JsonSerializer.Deserialize<T>(env.PayloadJson);
                    if (value != null)
                    {
                        Log.Debug($"[SearchCache] 命中内存缓存: source={source}, key={key}");
                    }
                    return value != null;
                }
                catch
                {
                    Memory.TryRemove(cacheId, out _);
                }
            }
        }

        // 2) 磁盘
        try
        {
            var path = GetCacheFilePath(source, key);
            if (!File.Exists(path))
                return false;

            var json = File.ReadAllText(path);
            var diskEnv = JsonSerializer.Deserialize<CacheEnvelope>(json);
            if (diskEnv == null)
                return false;

            if (IsExpired(diskEnv.CreatedUtc, ttl.Value))
            {
                TryDeleteFile(path);
                return false;
            }

            value = JsonSerializer.Deserialize<T>(diskEnv.PayloadJson);
            if (value == null)
                return false;

            Memory[cacheId] = diskEnv;
            Log.Debug($"[SearchCache] 命中磁盘缓存: source={source}, key={key}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("[SearchCache] 读取缓存失败", ex);
            return false;
        }
    }

    public static async Task SetAsync<T>(string source, string key, T value)
    {
        if (!IsEnabled)
            return;

        try
        {
            EnsureDir(source);
            var payloadJson = JsonSerializer.Serialize(value);
            var env = new CacheEnvelope
            {
                CreatedUtc = DateTime.UtcNow,
                PayloadJson = payloadJson
            };

            var cacheId = BuildCacheId(source, key);
            Memory[cacheId] = env;

            var path = GetCacheFilePath(source, key);
            var envJson = JsonSerializer.Serialize(env);
            await FileEx.WriteAllTextAsync(path, envJson);
        }
        catch (Exception ex)
        {
            Log.Warn("[SearchCache] 写入缓存失败", ex);
        }
    }

    public static int GetEntryCount(string source, TimeSpan? ttl = null)
    {
        ttl ??= DefaultTtl;
        try
        {
            var dir = GetSourceDir(source);
            if (!Directory.Exists(dir))
            {
                Log.Debug($"[SearchCache] GetEntryCount: 目录不存在: {dir}");
                return 0;
            }

            CleanupExpired(source, ttl.Value);
            var count = Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly).Length;
            Log.Debug($"[SearchCache] GetEntryCount: source={source}, count={count}, dir={dir}");
            return count;
        }
        catch (Exception ex)
        {
            Log.Warn($"[SearchCache] GetEntryCount 失败: source={source}", ex);
            return 0;
        }
    }

    public static void CleanupExpired(string source, TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero)
            return;

        try
        {
            var dir = GetSourceDir(source);
            if (!Directory.Exists(dir))
                return;

            foreach (var file in Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var env = JsonSerializer.Deserialize<CacheEnvelope>(json);
                    if (env == null || IsExpired(env.CreatedUtc, ttl))
                        TryDeleteFile(file);
                }
                catch
                {
                    TryDeleteFile(file);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("[SearchCache] 清理过期缓存失败", ex);
        }
    }

    public static async Task ClearSourceAsync(string source)
    {
        try
        {
            var dir = GetSourceDir(source);
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
                {
                    await Task.Run(() => TryDeleteFile(file));
                }
            }

            // 内存清理：按前缀匹配
            var prefix = source.Trim().ToLowerInvariant() + "|";
            foreach (var kv in Memory)
            {
                if (kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    Memory.TryRemove(kv.Key, out _);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("[SearchCache] 清理来源缓存失败", ex);
        }
    }

    private static bool IsExpired(DateTime createdUtc, TimeSpan ttl)
        => createdUtc == default || (DateTime.UtcNow - createdUtc) > ttl;

    private static void EnsureDir(string source)
    {
        Directory.CreateDirectory(CacheRoot);
        Directory.CreateDirectory(GetSourceDir(source));
    }

    private static string GetSourceDir(string source)
        => Path.Combine(CacheRoot, SafeDirName(source));

    private static string GetCacheFilePath(string source, string key)
    {
        var dir = GetSourceDir(source);
        var hash = Sha256Hex(key);
        return Path.Combine(dir, hash + ".json");
    }

    private static string BuildCacheId(string source, string key)
        => source.Trim().ToLowerInvariant() + "|" + key;

    private static string SafeDirName(string source)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            source = source.Replace(c, '_');
        return source.Trim();
    }

    private static string Sha256Hex(string input)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input ?? string.Empty));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }
}
