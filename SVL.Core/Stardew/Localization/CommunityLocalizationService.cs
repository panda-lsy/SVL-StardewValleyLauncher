using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using SVL.Core.Config;
using SVL.Core.Logging;

namespace SVL.Core.Stardew.Localization;

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
    public CommunityLocalizationDependency[] Dependencies { get; set; } = Array.Empty<CommunityLocalizationDependency>();

    [JsonPropertyName("hardConflicts")]
    public CommunityLocalizationRelation[] HardConflicts { get; set; } = Array.Empty<CommunityLocalizationRelation>();

    [JsonPropertyName("functionalOverlaps")]
    public CommunityLocalizationRelation[] FunctionalOverlaps { get; set; } = Array.Empty<CommunityLocalizationRelation>();

    [JsonPropertyName("localizedMods")]
    public CommunityLocalizationRelation[] LocalizedMods { get; set; } = Array.Empty<CommunityLocalizationRelation>();

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

public static class CommunityLocalizationService
{
    private static readonly HttpClient s_httpClient = CreateHttpClient();
    private static readonly JsonSerializerOptions s_jsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<CommunityLocalizationEntry?> GetAsync(string entityType, string platform, string id, bool forceRefresh = false)
    {
        var normalizedEntityType = NormalizeEntityType(entityType);
        var normalizedPlatform = NormalizePlatform(platform);
        var normalizedId = NormalizeId(id);

        if (string.IsNullOrWhiteSpace(normalizedEntityType) || string.IsNullOrWhiteSpace(normalizedId))
            return null;

        var relativePath = BuildRelativePath(normalizedEntityType, normalizedPlatform, normalizedId);
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        var provider = GetSelectedProvider();

        var cached = TryReadCache(provider, relativePath, forceRefresh);
        if (cached != null)
            return cached;

        var downloaded = await TryDownloadAsync(provider, relativePath).ConfigureAwait(false);
        if (downloaded != null)
            return downloaded;

        return null;
    }

    public static async Task<CommunityLocalizationEntry?> GetByUniqueIdAsync(string uniqueId, bool forceRefresh = false)
    {
        var normalizedId = NormalizeId(uniqueId);
        if (string.IsNullOrWhiteSpace(normalizedId))
            return null;

        var relativePath = $"Mods/UniqueID/{normalizedId}.json";
        var provider = GetSelectedProvider();

        var cached = TryReadCache(provider, relativePath, forceRefresh);
        if (cached != null)
            return cached;

        var downloaded = await TryDownloadAsync(provider, relativePath).ConfigureAwait(false);
        return downloaded;
    }

    public static string NormalizeEntityType(string? entityType)
    {
        if (string.IsNullOrWhiteSpace(entityType))
            return string.Empty;

        if (string.Equals(entityType, "collection", StringComparison.OrdinalIgnoreCase))
            return "collection";

        if (string.Equals(entityType, "modpack", StringComparison.OrdinalIgnoreCase))
            return "modpack";

        if (string.Equals(entityType, "mod", StringComparison.OrdinalIgnoreCase))
            return "mod";

        return string.Empty;
    }

    public static string NormalizePlatform(string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
            return string.Empty;

        if (string.Equals(platform, "Curseforge", StringComparison.OrdinalIgnoreCase))
            return "Curseforge";

        if (string.Equals(platform, "NexusMods", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(platform, "Nexus", StringComparison.OrdinalIgnoreCase))
            return "NexusMods";

        if (string.Equals(platform, "GitHub", StringComparison.OrdinalIgnoreCase))
            return "GitHub";

        if (string.Equals(platform, "UniqueID", StringComparison.OrdinalIgnoreCase)
            || string.Equals(platform, "LocalUniqueID", StringComparison.OrdinalIgnoreCase))
            return "UniqueID";

        return platform.Trim();
    }

    public static string NormalizeId(string? id)
    {
        return string.IsNullOrWhiteSpace(id) ? string.Empty : id.Trim();
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SVL-CommunityLocalization/1.0");
        return client;
    }

    private static string BuildRelativePath(string entityType, string platform, string id)
    {
        if (string.Equals(entityType, "mod", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(platform, "UniqueID", StringComparison.OrdinalIgnoreCase))
                return $"Mods/UniqueID/{id}.json";

            if (string.Equals(platform, "Curseforge", StringComparison.OrdinalIgnoreCase))
                return $"Mods/Curseforge/{id}.json";

            if (string.Equals(platform, "NexusMods", StringComparison.OrdinalIgnoreCase))
                return $"Mods/NexusMods/{id}.json";

            return string.Empty;
        }

        if (string.Equals(entityType, "modpack", StringComparison.OrdinalIgnoreCase))
            return $"Modpacks/{id}.json";

        if (string.Equals(entityType, "collection", StringComparison.OrdinalIgnoreCase))
            return $"Collections/{id}.json";

        return string.Empty;
    }

    private static string GetSelectedProvider()
    {
        var preferred = AppConfig.GetSettings().LocalizationPreferredSource;
        if (string.Equals(preferred, "GitHub", StringComparison.OrdinalIgnoreCase))
            return "GitHub";

        return "Gitee";
    }

    private static CommunityLocalizationEntry? TryReadCache(string provider, string relativePath, bool forceRefresh)
    {
        var cacheFilePath = GetCacheFilePath(provider, relativePath);
        if (!File.Exists(cacheFilePath))
            return null;

        try
        {
            if (!forceRefresh)
            {
                var ttlMinutes = Math.Max(15, AppConfig.GetSettings().CacheRetentionMinutes);
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFilePath);
                if (age <= TimeSpan.FromMinutes(ttlMinutes))
                    return Deserialize(File.ReadAllText(cacheFilePath));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[CommunityLocalization] 读取缓存失败: {cacheFilePath}", ex);
        }

        try
        {
            return Deserialize(File.ReadAllText(cacheFilePath));
        }
        catch (Exception ex)
        {
            Log.Warn($"[CommunityLocalization] 读取过期缓存失败: {cacheFilePath}", ex);
            return null;
        }
    }

    private static async Task<CommunityLocalizationEntry?> TryDownloadAsync(string provider, string relativePath)
    {
        var url = BuildRawUrl(provider, relativePath);
        if (string.IsNullOrWhiteSpace(url))
            return null;

        try
        {
            using var response = await s_httpClient.GetAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Info($"[CommunityLocalization] {provider} 未命中文件: {relativePath}, status={(int)response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var entry = Deserialize(json);
            if (entry == null)
                return null;

            WriteCache(provider, relativePath, json);
            return entry;
        }
        catch (Exception ex)
        {
            Log.Warn($"[CommunityLocalization] 从 {provider} 拉取失败: {relativePath}", ex);
            return null;
        }
    }

    private static CommunityLocalizationEntry? Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        return JsonSerializer.Deserialize<CommunityLocalizationEntry>(json, s_jsonOptions);
    }

    private static void WriteCache(string provider, string relativePath, string json)
    {
        try
        {
            var cacheFilePath = GetCacheFilePath(provider, relativePath);
            var cacheDirectory = Path.GetDirectoryName(cacheFilePath);
            if (!string.IsNullOrWhiteSpace(cacheDirectory) && !Directory.Exists(cacheDirectory))
                Directory.CreateDirectory(cacheDirectory);

            File.WriteAllText(cacheFilePath, json);
        }
        catch (Exception ex)
        {
            Log.Warn($"[CommunityLocalization] 写入缓存失败: {relativePath}", ex);
        }
    }

    private static string BuildRawUrl(string provider, string relativePath)
    {
        var normalizedRelativePath = relativePath.Replace('\\', '/');
        if (string.Equals(provider, "GitHub", StringComparison.OrdinalIgnoreCase))
            return $"https://raw.githubusercontent.com/panda-lsy/StardewValley-Community-Localization/main/{normalizedRelativePath}";

        if (string.Equals(provider, "Gitee", StringComparison.OrdinalIgnoreCase))
            return $"https://gitee.com/mc_shengxia/StardewValley-Community-Localization/raw/main/{normalizedRelativePath}";

        return string.Empty;
    }

    private static string GetCacheFilePath(string provider, string relativePath)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SVL",
            "cache",
            "community-localization",
            provider);

        var normalizedRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        return Path.Combine(root, normalizedRelativePath);
    }
}