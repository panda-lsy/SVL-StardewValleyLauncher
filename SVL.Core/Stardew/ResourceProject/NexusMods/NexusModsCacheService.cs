using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SVL.Core.Logging;

namespace SVL.Core.Stardew.ResourceProject.NexusMods;

/// <summary>
/// NexusMods 下载缓存服务
/// 用于缓存从 NexusMods 下载的模组和 SMAPI 文件
/// </summary>
public static class NexusModsCacheService
{
    private static readonly string _cachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SVL",
        "cache",
        "nexusmods",
        "downloads"
    );

    /// <summary>
    /// 获取缓存文件路径
    /// </summary>
    public static string GetCachePath(long modId, long fileId)
    {
        // 确保缓存目录存在
        if (!Directory.Exists(_cachePath))
        {
            Directory.CreateDirectory(_cachePath);
        }

        return Path.Combine(_cachePath, $"mod_{modId}_{fileId}.zip");
    }

    /// <summary>
    /// 检查缓存是否存在
    /// </summary>
    public static bool Exists(long modId, long fileId)
    {
        var cachePath = GetCachePath(modId, fileId);
        return File.Exists(cachePath);
    }

    /// <summary>
    /// 从缓存获取文件
    /// </summary>
    /// <param name="modId">Mod ID</param>
    /// <param name="fileId">文件 ID</param>
    /// <returns>缓存文件路径，如果不存在则返回 null</returns>
    public static string? Get(long modId, long fileId)
    {
        var cachePath = GetCachePath(modId, fileId);
        if (File.Exists(cachePath))
        {
            Log.Info($"[NexusModsCache] 从缓存获取文件: modId={modId}, fileId={fileId}");
            return cachePath;
        }
        return null;
    }

    /// <summary>
    /// 保存文件到缓存
    /// </summary>
    /// <param name="sourcePath">源文件路径</param>
    /// <param name="modId">Mod ID</param>
    /// <param name="fileId">文件 ID</param>
    public static async Task SaveAsync(string sourcePath, long modId, long fileId)
    {
        try
        {
            var cachePath = GetCachePath(modId, fileId);

            // 如果源文件和缓存文件是同一个文件，不需要复制
            if (Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(cachePath), StringComparison.OrdinalIgnoreCase))
            {
                Log.Info($"[NexusModsCache] 源文件已经是缓存文件，跳过复制");
                return;
            }

            // 删除旧的缓存文件（如果存在）
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }

            // 复制文件到缓存目录
            await Task.Run(() => File.Copy(sourcePath, cachePath));

            var fileSize = new FileInfo(cachePath).Length / 1024.0 / 1024.0;
            Log.Info($"[NexusModsCache] 已保存到缓存: modId={modId}, fileId={fileId}, 大小={fileSize:F2}MB, 路径={cachePath}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[NexusModsCache] 保存缓存失败: modId={modId}, fileId={fileId}, 错误={ex.Message}");
        }
    }

    /// <summary>
    /// 清除所有缓存
    /// </summary>
    public static async Task ClearAsync()
    {
        try
        {
            if (Directory.Exists(_cachePath))
            {
                var files = Directory.GetFiles(_cachePath, "*.zip");
                foreach (var file in files)
                {
                    await Task.Run(() => File.Delete(file));
                }

                Log.Info($"[NexusModsCache] 已清除 {files.Length} 个缓存文件");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NexusModsCache] 清除缓存失败");
        }
    }

    /// <summary>
    /// 获取缓存总大小（MB）
    /// </summary>
    public static double GetCacheSize()
    {
        try
        {
            if (!Directory.Exists(_cachePath))
            {
                return 0;
            }

            var files = Directory.GetFiles(_cachePath, "*.zip");
            long totalBytes = files.Sum(f => new FileInfo(f).Length);
            return totalBytes / 1024.0 / 1024.0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NexusModsCache] 获取缓存大小失败");
            return 0;
        }
    }

    /// <summary>
    /// 获取缓存文件数量
    /// </summary>
    public static int GetCacheCount()
    {
        try
        {
            if (!Directory.Exists(_cachePath))
            {
                return 0;
            }

            return Directory.GetFiles(_cachePath, "*.zip").Length;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NexusModsCache] 获取缓存数量失败");
            return 0;
        }
    }

    /// <summary>
    /// 清理过期下载缓存（按最后写入时间）
    /// </summary>
    public static async Task<int> CleanupExpiredAsync(TimeSpan retention)
    {
        try
        {
            if (retention <= TimeSpan.Zero)
                return 0;

            if (!Directory.Exists(_cachePath))
                return 0;

            var nowUtc = DateTime.UtcNow;
            var removed = 0;
            var files = Directory.GetFiles(_cachePath, "*.zip");
            foreach (var file in files)
            {
                try
                {
                    var lastWriteUtc = File.GetLastWriteTimeUtc(file);
                    if ((nowUtc - lastWriteUtc) > retention)
                    {
                        await Task.Run(() => File.Delete(file));
                        removed++;
                    }
                }
                catch
                {
                    // ignore
                }
            }

            if (removed > 0)
                Log.Info($"[NexusModsCache] 已清理过期下载缓存 {removed} 个文件 (retention={retention.TotalMinutes}min)");
            return removed;
        }
        catch (Exception ex)
        {
            Log.Warn("[NexusModsCache] 清理过期下载缓存失败", ex);
            return 0;
        }
    }
}
