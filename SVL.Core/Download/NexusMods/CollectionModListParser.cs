using System;
using System.Collections.Generic;
using System.Linq;
using SVL.Core.Logging;
using SVL.Core.Stardew.ResourceProject.NexusMods;

namespace SVL.Core.Download.NexusMods;

/// <summary>
/// Collection Mod 下载项信息
/// </summary>
public class CollectionModDownloadItem
{
    /// <summary>
    /// Mod 名称
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Mod 版本
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// 是否为可选 Mod
    /// </summary>
    public bool IsOptional { get; set; }

    /// <summary>
    /// 安装阶段（用于排序）
    /// </summary>
    public int Phase { get; set; }

    /// <summary>
    /// Nexus Mod ID
    /// </summary>
    public long ModId { get; set; }

    /// <summary>
    /// Nexus File ID
    /// </summary>
    public long FileId { get; set; }

    /// <summary>
    /// 游戏域名（如 stardewvalley）
    /// </summary>
    public string GameDomain { get; set; } = "stardewvalley";

    /// <summary>
    /// 源类型（nexus, bundle, manual, browse, direct）
    /// </summary>
    public string SourceType { get; set; } = string.Empty;

    /// <summary>
    /// 文件大小（字节）
    /// </summary>
    public long FileSize { get; set; }

    /// <summary>
    /// 逻辑文件名
    /// </summary>
    public string? LogicalFilename { get; set; }

    /// <summary>
    /// Mod 作者
    /// </summary>
    public string? Author { get; set; }

    /// <summary>
    /// 直链下载 URL（用于 browse/direct/manual 类型）
    /// </summary>
    public string? DirectDownloadUrl { get; set; }

    /// <summary>
    /// 补丁列表（用于 patches 应用）
    /// Key: 文件路径，Value: CRC32 哈希
    /// </summary>
    public Dictionary<string, string> Patches { get; set; } = new();

    /// <summary>
    /// NexusMods 文件页面 URL
    /// </summary>
    public string FilesPageUrl => $"https://www.nexusmods.com/{GameDomain}/mods/{ModId}?tab=files&file_id={FileId}&nmm=1";

    /// <summary>
    /// 是否支持直链下载（有 DirectDownloadUrl 的 browse/direct/manual 类型）
    /// </summary>
    public bool SupportsDirectDownload => !string.IsNullOrEmpty(DirectDownloadUrl) &&
        (SourceType == "browse" || SourceType == "direct" || SourceType == "manual");

    /// <summary>
    /// 是否需要浏览器下载（非 nexus 类型且没有直链）
    /// </summary>
    public bool RequiresBrowserDownload => SourceType != "nexus" && SourceType != "bundle" && !SupportsDirectDownload;

    /// <summary>
    /// 下载状态
    /// </summary>
    public CollectionModDownloadStatus Status { get; set; } = CollectionModDownloadStatus.Pending;
}

/// <summary>
/// Collection Mod 下载状态
/// </summary>
public enum CollectionModDownloadStatus
{
    /// <summary>
    /// 等待下载
    /// </summary>
    Pending,

    /// <summary>
    /// 浏览器已打开
    /// </summary>
    BrowserOpened,

    /// <summary>
    /// 正在下载
    /// </summary>
    Downloading,

    /// <summary>
    /// 下载完成
    /// </summary>
    Completed,

    /// <summary>
    /// 下载失败
    /// </summary>
    Failed,

    /// <summary>
    /// 已跳过（可选 Mod）
    /// </summary>
    Skipped
}

/// <summary>
/// Collection Mod 下载列表解析结果
/// </summary>
public class CollectionModListResult
{
    /// <summary>
    /// Collection 名称
    /// </summary>
    public string CollectionName { get; set; } = string.Empty;

    /// <summary>
    /// Collection 作者
    /// </summary>
    public string CollectionAuthor { get; set; } = string.Empty;

    /// <summary>
    /// Collection 图片 URL（用于设置实例图标）
    /// </summary>
    public string PictureUrl { get; set; } = string.Empty;

    /// <summary>
    /// SMAPI Mod（需要优先安装）
    /// </summary>
    public CollectionModDownloadItem? SmapiMod { get; set; }

    /// <summary>
    /// 需要从 Nexus 下载的 Mods（需要浏览器或 API）
    /// </summary>
    public List<CollectionModDownloadItem> NexusMods { get; set; } = new();

    /// <summary>
    /// Bundled Mods（已包含在 Collection 中）
    /// </summary>
    public List<CollectionModDownloadItem> BundledMods { get; set; } = new();

    /// <summary>
    /// 需要手动下载的 Mods
    /// </summary>
    public List<CollectionModDownloadItem> ManualMods { get; set; } = new();

    /// <summary>
    /// 总 Mod 数量
    /// </summary>
    public int TotalModCount { get; set; }

    /// <summary>
    /// 是否包含 SMAPI
    /// </summary>
    public bool HasSMAPI => SmapiMod != null;

    /// <summary>
    /// 需要 Nexus 下载的 Mod 数量（包括 nexus 和 browse 类型）
    /// </summary>
    public int NexusModCount => NexusMods.Count;

    /// <summary>
    /// Bundled Mod 数量
    /// </summary>
    public int BundledModCount => BundledMods.Count;

    /// <summary>
    /// 需要手动下载的 Mod 数量
    /// </summary>
    public int ManualModCount => ManualMods.Count;

    /// <summary>
    /// 总下载大小（字节，仅计算需要下载的）
    /// </summary>
    public long TotalDownloadSize => NexusMods.Sum(m => m.FileSize);

    /// <summary>
    /// 总下载大小（格式化字符串）
    /// </summary>
    public string TotalDownloadSizeFormatted => FormatFileSize(TotalDownloadSize);

    /// <summary>
    /// 按 Phase 分组的 Nexus Mods
    /// </summary>
    public Dictionary<int, List<CollectionModDownloadItem>> NexusModsByPhase =>
        NexusMods.GroupBy(m => m.Phase > 0 ? m.Phase : 1)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.ToList());

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}

/// <summary>
/// Collection Mod 下载列表解析服务
/// 负责解析 Collection JSON 并提取需要下载的 Mod 列表
/// </summary>
public static class CollectionModListParser
{
    /// <summary>
    /// 检测 Mod 是否为 SMAPI 安装器
    /// </summary>
    private static bool IsSMAPI(NexusCollectionJsonMod mod)
    {
        if (string.IsNullOrEmpty(mod.Name) || mod.Source == null)
            return false;

        // 方法1: 检查 Mod ID（SMAPI 在 NexusMods 上的官方 Mod ID 是 2400）
        if (mod.Source.ModId == 2400)
            return true;

        // 方法2: 检查名称（必须以 "SMAPI" 开头且不包含 "Component"、"Dependency" 等关键词）
        var name = mod.Name.Trim();
        if (!name.StartsWith("SMAPI", StringComparison.OrdinalIgnoreCase))
            return false;

        // 排除 SMAPI 组件和依赖项
        var excludeKeywords = new[] { "Component", "Dependency", "Extension", "Addon", "Patch" };
        return !excludeKeywords.Any(keyword => name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>
    /// 解析 Collection JSON 并提取需要下载的 Mod 列表
    /// </summary>
    /// <param name="collection">Collection JSON 数据</param>
    /// <returns>解析后的 Mod 下载列表</returns>
    public static CollectionModListResult ParseModList(NexusCollectionJson collection)
    {
        if (collection?.Info == null)
        {
            throw new ArgumentException("Collection 数据无效", nameof(collection));
        }

        Log.Info($"[CollectionModListParser] 解析 Collection: {collection.Info.Name}");

        var result = new CollectionModListResult
        {
            CollectionName = collection.Info.Name ?? "Unknown Collection",
            CollectionAuthor = collection.Info.Author ?? "Unknown Author",
            PictureUrl = string.Empty,  // collection.json 中没有图片 URL，需要从 NexusMods API 获取
            TotalModCount = collection.Mods?.Length ?? 0
        };

        if (collection.Mods == null || collection.Mods.Length == 0)
        {
            Log.Warn("[CollectionModListParser] Collection 中没有 Mod");
            return result;
        }

        foreach (var mod in collection.Mods)
        {
            if (mod.Source == null)
            {
                Log.Debug($"[CollectionModListParser] Mod {mod.Name} 没有源信息，跳过");
                continue;
            }

            var downloadItem = CreateDownloadItem(mod);
            if (downloadItem == null)
                continue;

            // 检测是否为 SMAPI
            if (IsSMAPI(mod))
            {
                result.SmapiMod = downloadItem;
                // 确保 SMAPI 的 Phase 为 0，使其在排序时排在最前面
                downloadItem.Phase = 0;
                // 将 SMAPI 也添加到 NexusMods 列表中，以便在向导中显示
                result.NexusMods.Add(downloadItem);
                Log.Info($"[CollectionModListParser] 检测到 SMAPI: {downloadItem.Name} (ModId={downloadItem.ModId}, FileId={downloadItem.FileId})");
                continue;
            }

            // 根据源类型分类
            switch (mod.Source.Type?.ToLower())
            {
                case "nexus":
                case "browse":  // browse 类型也需要浏览器下载
                    result.NexusMods.Add(downloadItem);
                    break;

                case "bundle":
                    result.BundledMods.Add(downloadItem);
                    break;

                case "manual":
                case "direct":
                    result.ManualMods.Add(downloadItem);
                    break;

                default:
                    Log.Debug($"[CollectionModListParser] Mod {mod.Name} 源类型未知: {mod.Source.Type}");
                    result.ManualMods.Add(downloadItem);
                    break;
            }
        }

        if (result.HasSMAPI)
        {
            Log.Info($"[CollectionModListParser] Collection 包含 SMAPI，将在向导显示前优先安装");
        }

        // 检查缓存中的 Mods
        CheckCachedMods(result);

        Log.Info($"[CollectionModListParser] 解析完成: " +
                 $"SMAPI={result.HasSMAPI}, Nexus={result.NexusModCount}, Bundled={result.BundledModCount}, Manual={result.ManualModCount}, " +
                 $"总大小={result.TotalDownloadSizeFormatted}");

        return result;
    }

    /// <summary>
    /// 从 Collection JSON Mod 创建下载项
    /// </summary>
    private static CollectionModDownloadItem? CreateDownloadItem(NexusCollectionJsonMod mod)
    {
        if (mod.Source == null)
            return null;

        return new CollectionModDownloadItem
        {
            Name = mod.Name ?? $"Mod_{mod.Source.ModId}",
            Version = mod.Version,
            IsOptional = mod.Optional,
            Phase = mod.Phase > 0 ? mod.Phase : 1,
            ModId = mod.Source.ModId,
            FileId = mod.Source.FileId,
            GameDomain = mod.DomainName ?? "stardewvalley",
            SourceType = mod.Source.Type ?? "unknown",
            FileSize = mod.Source.FileSize,
            LogicalFilename = mod.Source.LogicalFilename,
            Author = mod.Author,
            DirectDownloadUrl = mod.Source.Url,  // 解析直链下载 URL
            Patches = mod.Patches ?? new Dictionary<string, string>()  // 解析补丁信息
        };
    }

    /// <summary>
    /// 获取下一个需要下载的 Mod（按 Phase 和顺序）
    /// </summary>
    /// <param name="result">解析结果</param>
    /// <returns>下一个需要下载的 Mod，如果没有则返回 null</returns>
    public static CollectionModDownloadItem? GetNextPendingMod(CollectionModListResult result)
    {
        // 按 Phase 分组，然后按顺序获取
        foreach (var phaseGroup in result.NexusModsByPhase.OrderBy(g => g.Key))
        {
            var nextMod = phaseGroup.Value.FirstOrDefault(m => m.Status == CollectionModDownloadStatus.Pending);
            if (nextMod != null)
                return nextMod;
        }

        return null;
    }

    /// <summary>
    /// 检查是否所有需要下载的 Mod 都已完成
    /// </summary>
    /// <param name="result">解析结果</param>
    /// <returns>是否全部完成</returns>
    public static bool AreAllModsCompleted(CollectionModListResult result)
    {
        return result.NexusMods.All(m =>
            m.Status == CollectionModDownloadStatus.Completed ||
            m.Status == CollectionModDownloadStatus.Skipped);
    }

    /// <summary>
    /// 获取下载进度统计
    /// </summary>
    /// <param name="result">解析结果</param>
    /// <returns>进度统计</returns>
    public static (int completed, int total, int failed, int skipped) GetProgressStats(CollectionModListResult result)
    {
        var total = result.NexusMods.Count;
        var completed = result.NexusMods.Count(m => m.Status == CollectionModDownloadStatus.Completed);
        var failed = result.NexusMods.Count(m => m.Status == CollectionModDownloadStatus.Failed);
        var skipped = result.NexusMods.Count(m => m.Status == CollectionModDownloadStatus.Skipped);

        return (completed, total, failed, skipped);
    }

    /// <summary>
    /// 检查缓存中的 Mods 并标记为已完成
    /// </summary>
    /// <param name="result">解析结果</param>
    private static void CheckCachedMods(CollectionModListResult result)
    {
        int cachedCount = 0;

        foreach (var mod in result.NexusMods)
        {
            // 检查缓存
            var cachedPath = NexusModsCacheService.Get(mod.ModId, mod.FileId);
            if (cachedPath != null)
            {
                mod.Status = CollectionModDownloadStatus.Completed;
                cachedCount++;
                Log.Info($"[CollectionModListParser] 发现缓存: {mod.Name} (ModId={mod.ModId}, FileId={mod.FileId})");
            }
        }

        if (cachedCount > 0)
        {
            Log.Info($"[CollectionModListParser] 已找到 {cachedCount} 个缓存的 Mod");
        }
    }
}
