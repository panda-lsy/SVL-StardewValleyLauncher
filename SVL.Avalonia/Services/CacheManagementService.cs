using System.IO;
using SVL.Avalonia.Converters;

namespace SVL.Avalonia.Services;

/// <summary>缓存类别。</summary>
public enum CacheCategory
{
    /// <summary>社区汉化缓存。</summary>
    CommunityLocalization,
    /// <summary>SMAPI zip 缓存。</summary>
    SmapiDownloads,
    /// <summary>下载安装临时文件。</summary>
    DownloadInstall,
    /// <summary>远程图片与图标缓存（Mod、Modpack、SMAPI 等共用）。</summary>
    Images,
    /// <summary>下载文件缓存（按 URL 哈希，重复下载免下载）。</summary>
    DownloadsCache,
    /// <summary>游戏本体下载缓存（SteamCMD depot 下载产物）。</summary>
    Game,
    /// <summary>Nexus 资源下载缓存（按 ModID_FileID，免重复浏览器下载指引）。</summary>
    Nexus
}

/// <summary>缓存统计信息。</summary>
public sealed class CacheStatistics
{
    public long SizeBytes { get; set; }
    public int FileCount { get; set; }
    public string DisplaySize => FormatSize(SizeBytes);

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}

/// <summary>
/// 统一缓存管理服务。聚合各服务的缓存清理与统计能力。
/// 对齐旧架构 SettingsViewModel 中的缓存管理面板（ClearImageCache/ClearDownloadCache/ClearSearchCache）。
/// </summary>
public static class CacheManagementService
{
    /// <summary>获取指定类别的缓存统计。</summary>
    public static CacheStatistics GetStatistics(CacheCategory category)
    {
        if (category == CacheCategory.Nexus)
        {
            return Add(
                CalculateDirectoryStats(NexusDownloadCache.Root),
                CalculateDirectoryStats(NexusDownloadCache.LegacyRoot));
        }

        if (category == CacheCategory.DownloadsCache)
        {
            return Add(
                CalculateDirectoryStats(GetCachePath(category)),
                CalculateDirectoryStats(CurseforgeDownloadCache.Root));
        }

        if (category == CacheCategory.Images)
        {
            return Add(
                CalculateDirectoryStats(GetCachePath(category)),
                CalculateDirectoryStats(AssetImageConverter.LegacyIconCacheDirectory));
        }

        var path = GetCachePath(category);
        return CalculateDirectoryStats(path);
    }

    /// <summary>获取所有缓存类别的总统计。</summary>
    public static CacheStatistics GetTotalStatistics()
    {
        long totalSize = 0;
        var totalCount = 0;

        foreach (CacheCategory category in Enum.GetValues<CacheCategory>())
        {
            var stats = GetStatistics(category);
            totalSize += stats.SizeBytes;
            totalCount += stats.FileCount;
        }

        return new CacheStatistics { SizeBytes = totalSize, FileCount = totalCount };
    }

    /// <summary>清理指定类别的缓存。</summary>
    public static void Clear(CacheCategory category)
    {
        if (category == CacheCategory.Nexus)
        {
            NexusDownloadCache.Clear();
            return;
        }

        if (category == CacheCategory.DownloadsCache)
        {
            DeleteDirectory(GetCachePath(category));
            CurseforgeDownloadCache.Clear();
            return;
        }

        if (category == CacheCategory.Images)
        {
            DeleteDirectory(GetCachePath(category));
            DeleteDirectory(AssetImageConverter.LegacyIconCacheDirectory);
            return;
        }

        var path = GetCachePath(category);
        DeleteDirectory(path);
    }

    /// <summary>清理所有缓存。</summary>
    public static void ClearAll()
    {
        foreach (CacheCategory category in Enum.GetValues<CacheCategory>())
        {
            Clear(category);
        }
    }

    /// <summary>
    /// 只清理超过保留时长的缓存文件，不删除仍在使用的目录或新近生成的文件。
    /// 返回实际删除的文件数量；单个文件被占用时跳过，避免影响下载/安装任务。
    /// </summary>
    public static int ClearExpired(TimeSpan retention)
    {
        if (retention <= TimeSpan.Zero)
        {
            retention = TimeSpan.FromMinutes(60);
        }

        var cutoff = DateTime.UtcNow - retention;
        var removed = 0;
        foreach (var path in GetManagedCachePaths())
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                continue;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                try
                {
                    // 断点续传和原子缓存写入的中间文件不属于可清理缓存。
                    // 即使当前没有持有文件锁，也不能让清理任务删掉它们，
                    // 否则重试会丢失下载进度或留下半成品。
                    if (IsTransientCacheFile(file))
                    {
                        continue;
                    }

                    if (File.GetLastWriteTimeUtc(file) >= cutoff)
                    {
                        continue;
                    }

                    File.Delete(file);
                    removed++;
                }
                catch
                {
                    // 活跃下载、权限不足或被其他进程占用的文件留给下次清理。
                }
            }
        }

        return removed;
    }

    private static bool IsTransientCacheFile(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".part.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains(".tmp-", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>获取缓存类别的显示名称。</summary>
    public static string GetCategoryDisplayName(CacheCategory category) => category switch
    {
        CacheCategory.CommunityLocalization => "社区汉化缓存",
        CacheCategory.SmapiDownloads => "SMAPI 下载缓存",
        CacheCategory.DownloadInstall => "下载安装临时文件",
        CacheCategory.Images => "图片/图标缓存",
        CacheCategory.DownloadsCache => "下载文件缓存",
        CacheCategory.Game => "游戏本体下载缓存",
        CacheCategory.Nexus => "Nexus 下载缓存",
        _ => category.ToString()
    };

    /// <summary>获取缓存路径。</summary>
    public static string GetCachePath(CacheCategory category)
    {
        var appDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SVL", "Avalonia");

        return category switch
        {
            CacheCategory.CommunityLocalization => Path.Combine(appDataRoot, "cache", "community-localization"),
            CacheCategory.SmapiDownloads => Path.Combine(Path.GetTempPath(), "SVL", "smapi"),
            CacheCategory.DownloadInstall => Path.Combine(appDataRoot, "InstalledMods"),
            // 历史目录名保持不变，避免升级后丢失已有的远程图片缓存。
            CacheCategory.Images => Path.Combine(appDataRoot, "smapi-icon-cache"),
            CacheCategory.DownloadsCache => DownloadFileCache.CacheDirectory,
            CacheCategory.Game => SteamCmdService.GameCacheRoot,
            CacheCategory.Nexus => NexusDownloadCache.Root,
            _ => string.Empty
        };
    }

    private static CacheStatistics CalculateDirectoryStats(string path)
    {
        var stats = new CacheStatistics();

        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return stats;
        }

        try
        {
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    stats.SizeBytes += new FileInfo(file).Length;
                    stats.FileCount++;
                }
                catch { }
            }
        }
        catch { }

        return stats;
    }

    private static CacheStatistics Add(CacheStatistics first, CacheStatistics second)
    {
        return new CacheStatistics
        {
            SizeBytes = first.SizeBytes + second.SizeBytes,
            FileCount = first.FileCount + second.FileCount
        };
    }

    private static IEnumerable<string> GetManagedCachePaths()
    {
        foreach (CacheCategory category in Enum.GetValues<CacheCategory>())
        {
            if (category == CacheCategory.Nexus)
            {
                yield return NexusDownloadCache.Root;
                yield return NexusDownloadCache.LegacyRoot;
            }
            else if (category == CacheCategory.DownloadsCache)
            {
                yield return GetCachePath(category);
                yield return CurseforgeDownloadCache.Root;
            }
            else if (category == CacheCategory.Images)
            {
                yield return GetCachePath(category);
                yield return AssetImageConverter.LegacyIconCacheDirectory;
            }
            else
            {
                yield return GetCachePath(category);
            }
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, true);
        }
        catch { }
    }
}
