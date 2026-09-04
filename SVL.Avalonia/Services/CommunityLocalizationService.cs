using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SVL.Avalonia.Services;

/// <summary>社区汉化条目（完整模型，对齐旧架构 CommunityLocalizationEntry）。</summary>
public sealed class CommunityLocalizationEntry
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("entityType")]
    public string EntityType { get; set; } = string.Empty;

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public CommunityLocalizedText Name { get; set; } = new();

    [JsonPropertyName("description")]
    public CommunityLocalizedText Description { get; set; } = new();

    [JsonPropertyName("dependencies")]
    public CommunityLocalizationDependency[] Dependencies { get; set; } = [];

    [JsonPropertyName("hardConflicts")]
    public CommunityLocalizationRelation[] HardConflicts { get; set; } = [];

    [JsonPropertyName("functionalOverlaps")]
    public CommunityLocalizationRelation[] FunctionalOverlaps { get; set; } = [];

    [JsonPropertyName("localizedMods")]
    public CommunityLocalizationRelation[] LocalizedMods { get; set; } = [];

    [JsonPropertyName("meta")]
    public CommunityLocalizationMeta Meta { get; set; } = new();
}

public sealed class CommunityLocalizedText
{
    [JsonPropertyName("zh-CN")]
    public string ZhCn { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;
}

public sealed class CommunityLocalizationMeta
{
    [JsonPropertyName("contributor")]
    public string Contributor { get; set; } = string.Empty;

    [JsonPropertyName("sourceUrl")]
    public string SourceUrl { get; set; } = string.Empty;

    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; set; } = string.Empty;
}

public sealed class CommunityLocalizationDependency
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("optional")]
    public bool Optional { get; set; }

    [JsonPropertyName("note")]
    public string Note { get; set; } = string.Empty;
}

public sealed class CommunityLocalizationRelation
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// 社区汉化服务。承载双源（GitHub/Gitee）选择 + 磁盘缓存 + 降级读取。
/// 对齐旧架构 SVL.Core.Stardew.Localization.CommunityLocalizationService。
/// 缓存位置：%LOCALAPPDATA%/SVL/Avalonia/cache/community-localization/{provider}/...
/// </summary>
public sealed class CommunityLocalizationService
{
    /// <summary>GitHub 源基础 URL。</summary>
    public const string GitHubBaseUrl = "https://raw.githubusercontent.com/panda-lsy/StardewValley-Community-Localization/main/";

    /// <summary>Gitee 源基础 URL。</summary>
    public const string GiteeBaseUrl = "https://gitee.com/mc_shengxia/StardewValley-Community-Localization/raw/main/";

    private static readonly HttpClient s_httpClient = CreateHttpClient();
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly AppUserSettingsStore _settingsStore;

    public CommunityLocalizationService(AppUserSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    /// <summary>按 entityType+platform+id 获取汉化条目（带缓存）。</summary>
    public async Task<CommunityLocalizationEntry?> GetAsync(string entityType, string platform, string id, bool forceRefresh = false)
    {
        var normalizedEntityType = NormalizeEntityType(entityType);
        var normalizedPlatform = NormalizePlatform(platform);
        var normalizedId = NormalizeId(id);

        if (string.IsNullOrWhiteSpace(normalizedEntityType) || string.IsNullOrWhiteSpace(normalizedId))
        {
            return null;
        }

        var relativePath = BuildRelativePath(normalizedEntityType, normalizedPlatform, normalizedId);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        return await GetByRelativePathAsync(relativePath, forceRefresh);
    }

    /// <summary>按 UniqueID 获取汉化条目（带缓存）。</summary>
    public async Task<CommunityLocalizationEntry?> GetByUniqueIdAsync(string uniqueId, bool forceRefresh = false)
    {
        var normalizedId = NormalizeId(uniqueId);
        if (string.IsNullOrWhiteSpace(normalizedId))
        {
            return null;
        }

        return await GetByRelativePathAsync($"Mods/UniqueID/{normalizedId}.json", forceRefresh);
    }

    /// <summary>按相对路径获取汉化条目（带缓存）。公开供外部直接传路径调用。</summary>
    public async Task<CommunityLocalizationEntry?> GetByRelativePathAsync(string relativePath, bool forceRefresh = false)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var provider = GetSelectedProvider();

        // 1. 尝试缓存命中（未过期）
        var cached = TryReadCache(provider, relativePath, forceRefresh);
        if (cached != null)
        {
            return cached;
        }

        // 2. 网络下载
        var downloaded = await TryDownloadAsync(provider, relativePath);
        return downloaded;
    }

    // ================================================================
    // 公开静态工具方法（便于测试和外部调用）
    // ================================================================

    public static string NormalizeEntityType(string? entityType)
    {
        if (string.IsNullOrWhiteSpace(entityType))
        {
            return string.Empty;
        }

        if (string.Equals(entityType, "collection", StringComparison.OrdinalIgnoreCase))
        {
            return "collection";
        }

        if (string.Equals(entityType, "modpack", StringComparison.OrdinalIgnoreCase))
        {
            return "modpack";
        }

        if (string.Equals(entityType, "mod", StringComparison.OrdinalIgnoreCase))
        {
            return "mod";
        }

        return string.Empty;
    }

    public static string NormalizePlatform(string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
        {
            return string.Empty;
        }

        if (string.Equals(platform, "Curseforge", StringComparison.OrdinalIgnoreCase))
        {
            return "Curseforge";
        }

        if (string.Equals(platform, "NexusMods", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(platform, "Nexus", StringComparison.OrdinalIgnoreCase))
        {
            return "NexusMods";
        }

        if (string.Equals(platform, "GitHub", StringComparison.OrdinalIgnoreCase))
        {
            return "GitHub";
        }

        if (string.Equals(platform, "UniqueID", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(platform, "LocalUniqueID", StringComparison.OrdinalIgnoreCase))
        {
            return "UniqueID";
        }

        return platform.Trim();
    }

    public static string NormalizeId(string? id)
    {
        return string.IsNullOrWhiteSpace(id) ? string.Empty : id.Trim();
    }

    /// <summary>构造仓库内的相对路径（如 "Mods/NexusMods/12345.json"）。</summary>
    public static string BuildRelativePath(string entityType, string platform, string id)
    {
        if (string.Equals(entityType, "mod", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(platform, "UniqueID", StringComparison.OrdinalIgnoreCase))
            {
                return $"Mods/UniqueID/{id}.json";
            }

            if (string.Equals(platform, "Curseforge", StringComparison.OrdinalIgnoreCase))
            {
                return $"Mods/Curseforge/{id}.json";
            }

            if (string.Equals(platform, "NexusMods", StringComparison.OrdinalIgnoreCase))
            {
                return $"Mods/NexusMods/{id}.json";
            }

            return string.Empty;
        }

        if (string.Equals(entityType, "modpack", StringComparison.OrdinalIgnoreCase))
        {
            return $"Modpacks/{id}.json";
        }

        if (string.Equals(entityType, "collection", StringComparison.OrdinalIgnoreCase))
        {
            return $"Collections/{id}.json";
        }

        return string.Empty;
    }

    /// <summary>构造指定源的完整 URL。</summary>
    public static string BuildRawUrl(string provider, string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        if (string.Equals(provider, "GitHub", StringComparison.OrdinalIgnoreCase))
        {
            return GitHubBaseUrl + normalized;
        }

        if (string.Equals(provider, "Gitee", StringComparison.OrdinalIgnoreCase))
        {
            return GiteeBaseUrl + normalized;
        }

        return string.Empty;
    }

    /// <summary>构造缓存文件路径。公开便于测试和清理。</summary>
    public static string GetCacheFilePath(string provider, string relativePath)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SVL",
            "Avalonia",
            "cache",
            "community-localization",
            provider);

        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        return Path.Combine(root, normalized);
    }

    /// <summary>清空指定源的缓存。</summary>
    public static void ClearCache(string? provider = null)
    {
        var providers = string.IsNullOrWhiteSpace(provider)
            ? new[] { "GitHub", "Gitee" }
            : new[] { provider };

        foreach (var p in providers)
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SVL",
                "Avalonia",
                "cache",
                "community-localization",
                p);

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    /// <summary>获取缓存目录大小（字节）。</summary>
    public static long GetCacheSize()
    {
        long size = 0;
        var providers = new[] { "GitHub", "Gitee" };
        foreach (var p in providers)
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SVL",
                "Avalonia",
                "cache",
                "community-localization",
                p);

            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                try { size += new FileInfo(file).Length; } catch { }
            }
        }
        return size;
    }

    // ================================================================
    // 内部方法
    // ================================================================

    private string GetSelectedProvider()
    {
        var preferred = _settingsStore.Load().LocalizationPreferredSource;
        if (string.Equals(preferred, "GitHub", StringComparison.OrdinalIgnoreCase))
        {
            return "GitHub";
        }

        return "Gitee";
    }

    private CommunityLocalizationEntry? TryReadCache(string provider, string relativePath, bool forceRefresh)
    {
        var cacheFilePath = GetCacheFilePath(provider, relativePath);
        if (!File.Exists(cacheFilePath))
        {
            return null;
        }

        var settings = _settingsStore.Load();
        var ttlMinutes = Math.Max(15, settings.CacheRetentionMinutes);

        try
        {
            if (!forceRefresh)
            {
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFilePath);
                if (age <= TimeSpan.FromMinutes(ttlMinutes))
                {
                    return Deserialize(File.ReadAllText(cacheFilePath));
                }
            }
        }
        catch
        {
            // 缓存读取异常，继续尝试降级读取
        }

        // 缓存已过期或 forceRefresh：尝试网络下载，失败时降级读取过期缓存
        return null;
    }

    /// <summary>降级读取过期缓存（网络下载失败时调用）。</summary>
    private static CommunityLocalizationEntry? TryReadStaleCache(string provider, string relativePath)
    {
        var cacheFilePath = GetCacheFilePath(provider, relativePath);
        if (!File.Exists(cacheFilePath))
        {
            return null;
        }

        try
        {
            return Deserialize(File.ReadAllText(cacheFilePath));
        }
        catch
        {
            return null;
        }
    }

    private async Task<CommunityLocalizationEntry?> TryDownloadAsync(string provider, string relativePath)
    {
        var url = BuildRawUrl(provider, relativePath);
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        try
        {
            using var response = await s_httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                // 首选源失败：尝试备用源
                var fallbackProvider = string.Equals(provider, "GitHub", StringComparison.OrdinalIgnoreCase)
                    ? "Gitee" : "GitHub";
                var fallbackUrl = BuildRawUrl(fallbackProvider, relativePath);
                if (!string.IsNullOrWhiteSpace(fallbackUrl))
                {
                    using var fallbackResponse = await s_httpClient.GetAsync(fallbackUrl);
                    if (fallbackResponse.IsSuccessStatusCode)
                    {
                        var fallbackJson = await fallbackResponse.Content.ReadAsStringAsync();
                        var entry = Deserialize(fallbackJson);
                        if (entry != null)
                        {
                            WriteCache(fallbackProvider, relativePath, fallbackJson);
                            return entry;
                        }
                    }
                }

                // 双源均失败：降级读取过期缓存
                return TryReadStaleCache(provider, relativePath);
            }

            var json = await response.Content.ReadAsStringAsync();
            var downloaded = Deserialize(json);
            if (downloaded == null)
            {
                return TryReadStaleCache(provider, relativePath);
            }

            WriteCache(provider, relativePath, json);
            return downloaded;
        }
        catch
        {
            // 网络异常：降级读取过期缓存
            return TryReadStaleCache(provider, relativePath);
        }
    }

    private static CommunityLocalizationEntry? Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize<CommunityLocalizationEntry>(json, s_jsonOptions);
    }

    private static void WriteCache(string provider, string relativePath, string json)
    {
        try
        {
            var cacheFilePath = GetCacheFilePath(provider, relativePath);
            var cacheDirectory = Path.GetDirectoryName(cacheFilePath);
            if (!string.IsNullOrWhiteSpace(cacheDirectory) && !Directory.Exists(cacheDirectory))
            {
                Directory.CreateDirectory(cacheDirectory);
            }

            File.WriteAllText(cacheFilePath, json);
        }
        catch
        {
            // 缓存写入失败不影响主流程
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SVL-CommunityLocalization/1.0");
        return client;
    }
}
