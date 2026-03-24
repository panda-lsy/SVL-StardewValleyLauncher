using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SVL.Core.Stardew.ResourceProject.NexusMods;

public class NexusMod
{
    [JsonPropertyName("id")]
    public string GraphQlId { get; set; }

    [JsonPropertyName("uid")]
    public long Uid { get; set; }

    [JsonPropertyName("mod_id")]
    public int ModId { get; set; }

    // GraphQL 返回字段名是 modId（camelCase），与 REST 的 mod_id 不同。
    // 为避免 GraphQL 搜索结果 ModId 反序列化为 0，这里单独映射并在调用处归一化。
    [JsonPropertyName("modId")]
    public int ModIdGraphQl { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("summary")]
    public string Summary { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; }

    [JsonPropertyName("pictureUrl")]
    public string PictureUrl { get; set; }

    [JsonPropertyName("picture_url")]
    public string PictureUrlLegacy { get; set; }  // 保留旧字段名以兼容 REST API

    [JsonPropertyName("category_id")]
    public long CategoryId { get; set; }

    [JsonPropertyName("author")]
    public string Author { get; set; }

    [JsonPropertyName("uploaded_time")]
    public long UploadedTime { get; set; }  // 保留：REST API 兼容

    [JsonPropertyName("updated_time")]
    public object? UpdatedTime { get; set; }  // 保留：REST API 兼容

    // GraphQL 时间字段（ISO 8601 格式）
    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    [JsonPropertyName("downloads")]
    public long Downloads { get; set; }

    [JsonPropertyName("mod_downloads")]
    public long ModDownloadsLegacy { get; set; }  // 保留：REST API 兼容

    [JsonPropertyName("endorsements")]
    public int Endorsements { get; set; }

    [JsonPropertyName("endorsement_count")]
    public int EndorsementCountLegacy { get; set; }  // 保留旧字段名以兼容 REST API

    [JsonPropertyName("category")]
    public string Category { get; set; }  // GraphQL 返回 String，不是对象

    [JsonPropertyName("status")]
    public string Status { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; }

    [JsonPropertyName("adultContent")]
    public bool AdultContent { get; set; }

    [JsonPropertyName("directDownloadEnabled")]
    public bool DirectDownloadEnabled { get; set; }

    [JsonPropertyName("fileSize")]
    public int? FileSize { get; set; }

    [JsonPropertyName("isBlockedFromEarningDp")]
    public bool? IsBlockedFromEarningDp { get; set; }

    [JsonPropertyName("thumbnailBlurredUrl")]
    public string ThumbnailBlurredUrl { get; set; }

    [JsonPropertyName("thumbnailLargeBlurredUrl")]
    public string ThumbnailLargeBlurredUrl { get; set; }

    [JsonPropertyName("thumbnailLargeUrl")]
    public string ThumbnailLargeUrl { get; set; }

    [JsonPropertyName("thumbnailUrl")]
    public string ThumbnailUrl { get; set; }

    [JsonPropertyName("viewerBlocked")]
    public bool ViewerBlocked { get; set; }

    [JsonPropertyName("viewerDownloaded")]
    public DateTime? ViewerDownloaded { get; set; }

    [JsonPropertyName("viewerEndorsed")]
    public bool? ViewerEndorsed { get; set; }

    [JsonPropertyName("viewerIsBlocked")]
    public bool? ViewerIsBlocked { get; set; }

    [JsonPropertyName("viewerTracked")]
    public bool ViewerTracked { get; set; }

    [JsonPropertyName("viewerUpdateAvailable")]
    public bool? ViewerUpdateAvailable { get; set; }

    // GraphQL 可选字段：translations，结构可能随 API 演进变化，保留原始 JSON 便于兼容解析。
    [JsonPropertyName("translations")]
    public JsonElement TranslationsRaw { get; set; }

    // GraphQL 额外字段
    [JsonPropertyName("game")]
    public NexusGame Game { get; set; }

    [JsonPropertyName("modCategory")]
    public NexusModCategory ModCategory { get; set; }

    [JsonPropertyName("uploader")]
    public NexusUser Uploader { get; set; }

    [JsonPropertyName("tags")]
    public List<NexusLegacyTag> Tags { get; set; } = [];

    [JsonPropertyName("mirrors")]
    public List<NexusModMirror> Mirrors { get; set; } = [];

    [JsonPropertyName("modRequirements")]
    public NexusModRequirements ModRequirements { get; set; }

    public bool HasTranslationSignals()
    {
        if (TranslationsRaw.ValueKind == JsonValueKind.Undefined ||
            TranslationsRaw.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        var flattened = FlattenTranslationText(TranslationsRaw).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(flattened))
            return false;

        if (flattened.Contains("中文") || flattened.Contains("汉化") || flattened.Contains("漢化"))
            return true;

        return Regex.IsMatch(flattened, @"\b(chinese|mandarin|zh[-_ ]?cn|cn|translation|translations)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string FlattenTranslationText(JsonElement element)
    {
        var builder = new StringBuilder();
        AppendJsonText(element, builder);
        return builder.ToString();
    }

    private static void AppendJsonText(JsonElement element, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    builder.Append(value);
                    builder.Append(' ');
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AppendJsonText(item, builder);
                }
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (!string.IsNullOrWhiteSpace(property.Name))
                    {
                        builder.Append(property.Name);
                        builder.Append(' ');
                    }
                    AppendJsonText(property.Value, builder);
                }
                break;
            default:
                break;
        }
    }
}

/// <summary>
/// Nexus 游戏信息（GraphQL）
/// </summary>
public class NexusGame
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("domainName")]
    public string DomainName { get; set; }
}

public class NexusModCategory
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("categoryId")]
    public int CategoryId { get; set; }

    [JsonPropertyName("gameId")]
    public int GameId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }
}

public class NexusUser
{
    [JsonPropertyName("memberId")]
    public long MemberId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("avatar")]
    public string Avatar { get; set; }

    [JsonPropertyName("isBlocked")]
    public bool IsBlocked { get; set; }

    [JsonPropertyName("isTracked")]
    public bool IsTracked { get; set; }
}

public class NexusLegacyTag
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("global")]
    public bool Global { get; set; }

    [JsonPropertyName("parentId")]
    public string ParentId { get; set; }

    [JsonPropertyName("blockable")]
    public bool Blockable { get; set; }

    [JsonPropertyName("searchable")]
    public bool Searchable { get; set; }
}

public class NexusModMirror
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("gameId")]
    public int GameId { get; set; }

    [JsonPropertyName("modId")]
    public int ModId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("uri")]
    public string Uri { get; set; }

    [JsonPropertyName("count")]
    public int? Count { get; set; }

    [JsonPropertyName("totalDownloads")]
    public int? TotalDownloads { get; set; }
}

public class NexusModRequirements
{
    [JsonPropertyName("dlcRequirements")]
    public List<NexusModRequirementsDlc> DlcRequirements { get; set; } = [];

    [JsonPropertyName("nexusRequirements")]
    public NexusModRequirementPage NexusRequirements { get; set; }

    [JsonPropertyName("modsRequiringThisMod")]
    public NexusModRequiringPage ModsRequiringThisMod { get; set; }
}

public class NexusModRequirementsDlc
{
    [JsonPropertyName("gameExpansion")]
    public NexusGameExpansion GameExpansion { get; set; }

    [JsonPropertyName("notes")]
    public string Notes { get; set; }
}

public class NexusGameExpansion
{
    [JsonPropertyName("gameId")]
    public string GameId { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }
}

public class NexusModRequirementPage
{
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("nodesCount")]
    public int NodesCount { get; set; }

    [JsonPropertyName("nodes")]
    public List<NexusModRequirementNode> Nodes { get; set; } = [];
}

public class NexusModRequiringPage
{
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("nodesCount")]
    public int NodesCount { get; set; }

    [JsonPropertyName("nodes")]
    public List<NexusModRequiringNode> Nodes { get; set; } = [];
}

public class NexusModRequirementNode
{
    [JsonPropertyName("externalRequirement")]
    public bool ExternalRequirement { get; set; }

    [JsonPropertyName("gameId")]
    public string GameId { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("modId")]
    public string ModId { get; set; }

    [JsonPropertyName("modName")]
    public string ModName { get; set; }

    [JsonPropertyName("notes")]
    public string Notes { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; }
}

public class NexusModRequiringNode
{
    [JsonPropertyName("externalRequirement")]
    public bool ExternalRequirement { get; set; }

    [JsonPropertyName("gameId")]
    public string GameId { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("modId")]
    public string ModId { get; set; }

    [JsonPropertyName("modName")]
    public string ModName { get; set; }

    [JsonPropertyName("notes")]
    public string Notes { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; }
}

// 保留旧的 NexusCategory 类以兼容 REST API
public class NexusCategory
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }
}

public class NexusModFile
{
    [JsonPropertyName("id")]
    public object FileId { get; set; }  // NexusMods API 可能返回数字、字符串或数组（已废弃，请使用 FileIdValue）

    [JsonPropertyName("file_id")]
    public long? FileIdValue { get; set; }  // NexusMods API 返回的实际文件ID

    /// <summary>
    /// 获取文件 ID 的 long 表示（优先使用 file_id 字段）
    /// </summary>
    public long GetFileIdLong()
    {
        // 优先使用 file_id 字段（这是 API 返回的正确文件ID）
        if (FileIdValue.HasValue && FileIdValue.Value != 0)
            return FileIdValue.Value;

        // 兼容旧代码：如果 file_id 不存在，则从 id 字段解析
        if (FileId == null)
            return 0;

        // 直接的数字类型
        if (FileId is long l)
            return l;
        if (FileId is int i)
            return i;

        // 字符串类型
        if (FileId is string s && long.TryParse(s, out var sl))
            return sl;

        // JsonElement 类型（单个值或数组）
        if (FileId is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                // 数组格式：取第一个元素
                var first = element.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Number)
                    return first.GetInt64();
                if (first.ValueKind == JsonValueKind.String)
                {
                    var str = first.GetString();
                    if (long.TryParse(str, out var sl2))
                        return sl2;
                }
            }
            else if (element.ValueKind == JsonValueKind.Number)
            {
                return element.GetInt64();
            }
            else if (element.ValueKind == JsonValueKind.String)
            {
                var str = element.GetString();
                if (long.TryParse(str, out var sl2))
                    return sl2;
            }
        }

        // JsonElement[] 类型（数组被反序列化为对象数组）
        if (FileId is JsonElement[] elementArray && elementArray.Length > 0)
        {
            var first = elementArray[0];
            if (first.ValueKind == JsonValueKind.Number)
                return first.GetInt64();
            if (first.ValueKind == JsonValueKind.String)
            {
                var str = first.GetString();
                if (long.TryParse(str, out var sl2))
                    return sl2;
            }
        }

        // object[] 类型（数组被反序列化为对象数组）
        if (FileId is object[] objArray && objArray.Length > 0)
        {
            var first = objArray[0];
            if (first is long l2)
                return l2;
            if (first is int i2)
                return i2;
            if (first is JsonElement je && je.ValueKind == JsonValueKind.Number)
                return je.GetInt64();
        }

        return 0;
    }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; }

    [JsonPropertyName("file_name")]
    public string FileName { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    // 某些 NexusMods 端点会返回文件下载量（如果不存在则为 0）
    [JsonPropertyName("download_count")]
    public long DownloadCount { get; set; }

    [JsonPropertyName("downloads")]
    public long Downloads { get; set; }

    [JsonPropertyName("downloads_count")]
    public long DownloadsCount { get; set; }

    [JsonPropertyName("uploaded_time")]
    public string UploadedTime { get; set; }  // ISO 8601 格式字符串

    [JsonPropertyName("changelog_html")]
    public string ChangelogHtml { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; }

    [JsonPropertyName("categories")]
    public List<NexusModFileCategory> Categories { get; set; } = [];

    [JsonPropertyName("category_id")]
    public long CategoryId { get; set; }

    [JsonPropertyName("category_name")]
    public string CategoryName { get; set; }

    [JsonPropertyName("is_primary")]
    public bool IsPrimary { get; set; }

    public long GetEffectiveDownloadCount()
    {
        return Math.Max(DownloadCount, Math.Max(Downloads, DownloadsCount));
    }
}

public class NexusModFileCategory
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }
}

public class NexusModFileRequirement
{
    public long ModId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public bool IsRequired { get; set; } = true;

    public string Url { get; set; } = string.Empty;
}

public class NexusSearchResponse
{
    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("term")]
    public string Term { get; set; }

    [JsonPropertyName("data")]
    public List<NexusMod> Data { get; set; }
}

public class NexusCollection
{
    public long CollectionId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string Author { get; set; } = string.Empty;

    public long Downloads { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string PictureUrl { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Collection 的 Slug（用于构建 NXM 链接）
    /// </summary>
    public string Slug { get; set; } = string.Empty;
}

/// <summary>
/// NexusMods Collection Revision 信息
/// </summary>
public class NexusCollectionRevision
{
    /// <summary>
    /// Revision ID
    /// </summary>
    public long RevisionId { get; set; }

    /// <summary>
    /// Revision 号
    /// </summary>
    public int RevisionNumber { get; set; }

    /// <summary>
    /// 版本名称
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 版本描述
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 是否为最新版本
    /// </summary>
    public bool IsLatest { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 更新时间
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// 下载次数
    /// </summary>
    public long TotalDownloads { get; set; }

    /// <summary>
    /// 文件大小（字节）
    /// </summary>
    public long FileSize { get; set; }

    /// <summary>
    /// 包含的 Mod 数量
    /// </summary>
    public int ModCount { get; set; }

    /// <summary>
    /// 下载链接
    /// </summary>
    public string DownloadLink { get; set; } = string.Empty;

    /// <summary>
    /// Collection Slug（用于构建 NXM 链接）
    /// </summary>
    public string CollectionSlug { get; set; } = string.Empty;
}

public class NexusFilesResponse
{
    [JsonPropertyName("files")]
    public List<NexusModFile> Files { get; set; }
}

public class NexusDownloadUrlResponse
{
    [JsonPropertyName("uri")]
    public object Uri { get; set; }  // 可能是字符串（单个 URL）或数组（Premium 用户的多个 CDN 链接）

    /// <summary>
    /// 获取第一个可用的下载 URL
    /// </summary>
    public string GetFirstUrl()
    {
        if (Uri == null)
            return string.Empty;

        // 如果是字符串，直接返回
        if (Uri is string uriString)
            return uriString;

        // 如果是 JsonElement
        if (Uri is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                return element.GetString() ?? string.Empty;
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                // Premium 用户：返回第一个 CDN 链接
                var first = element.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.String)
                {
                    return first.GetString() ?? string.Empty;
                }
            }
        }

        return string.Empty;
    }
}

/// <summary>
/// Nexus CDN 服务器信息（用于数组格式的下载链接响应）
/// </summary>
public class NexusCdnServer
{
    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("short_name")]
    public string ShortName { get; set; }

    [JsonPropertyName("URI")]
    public string URI { get; set; }
}

#region GraphQL API 响应模型

/// <summary>
/// GraphQL 搜索响应根对象
/// </summary>
public class GraphQLSearchResponse
{
    [JsonPropertyName("data")]
    public GraphQLData Data { get; set; }
}

/// <summary>
/// GraphQL 数据容器
/// </summary>
public class GraphQLData
{
    [JsonPropertyName("mods")]
    public ModsContainer Mods { get; set; }

    [JsonPropertyName("collectionsV2")]
    public NexusCollectionsContainer? CollectionsV2 { get; set; }
}

/// <summary>
/// Mods 容器（直接在根级别，不是嵌套在 game 下）
/// </summary>
public class ModsContainer
{
    [JsonPropertyName("nodes")]
    public List<NexusMod> Nodes { get; set; } = new();

    [JsonPropertyName("totalCount")]
    public int? TotalCount { get; set; }

    [JsonPropertyName("nodesCount")]
    public int? NodesCount { get; set; }
}

public class GraphQLCollectionsResponse
{
    [JsonPropertyName("data")]
    public GraphQLData? Data { get; set; }
}

public class NexusCollectionsContainer
{
    [JsonPropertyName("nodes")]
    public List<NexusCollectionNode> Nodes { get; set; } = new();

    [JsonPropertyName("totalCount")]
    public int? TotalCount { get; set; }

    [JsonPropertyName("nodesCount")]
    public int? NodesCount { get; set; }
}

public class NexusCollectionNode
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("totalDownloads")]
    public long TotalDownloads { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    [JsonPropertyName("user")]
    public NexusCollectionUser? User { get; set; }

    [JsonPropertyName("tileImage")]
    public NexusCollectionTileImage? TileImage { get; set; }

    [JsonPropertyName("game")]
    public NexusGame? Game { get; set; }
}

public class NexusCollectionUser
{
    [JsonPropertyName("memberId")]
    public long MemberId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("avatar")]
    public string? Avatar { get; set; }
}

public class NexusCollectionTileImage
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

/// <summary>
/// GraphQL Collection Revisions 响应
/// </summary>
public class GraphQLCollectionRevisionsResponse
{
    [JsonPropertyName("data")]
    public GraphQLCollectionRevisionsData? Data { get; set; }
}

public class GraphQLCollectionRevisionsData
{
    [JsonPropertyName("collection")]
    public GraphQLCollectionDetail? Collection { get; set; }
}

public class GraphQLCollectionDetail
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("revisions")]
    public List<NexusCollectionRevisionNode> Revisions { get; set; } = new();
}

public class NexusCollectionRevisionNode
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("revisionNumber")]
    public int RevisionNumber { get; set; }

    [JsonPropertyName("revisionStatus")]
    public string? RevisionStatus { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    // BigInt 字段在 JSON 中是字符串格式
    [JsonPropertyName("fileSize")]
    public string? FileSizeStr { get; set; }

    [JsonPropertyName("totalSize")]
    public string? TotalSizeStr { get; set; }

    // 普通整数字段
    [JsonPropertyName("modCount")]
    public int ModCount { get; set; }

    [JsonPropertyName("totalDownloads")]
    public int TotalDownloads { get; set; }

    [JsonPropertyName("uniqueDownloads")]
    public int UniqueDownloads { get; set; }

    [JsonPropertyName("downloadLink")]
    public string? DownloadLink { get; set; }

    [JsonPropertyName("latest")]
    public bool Latest { get; set; }

    [JsonPropertyName("adultContent")]
    public bool AdultContent { get; set; }

    // 辅助属性：将字符串转换为 long
    public long FileSize => long.TryParse(FileSizeStr, out var v) ? v : 0;
    public long TotalSize => long.TryParse(TotalSizeStr, out var v) ? v : 0;
}

/// <summary>
/// Relay 分页信息
/// </summary>
public class PageInfo
{
    [JsonPropertyName("hasNextPage")]
    public bool HasNextPage { get; set; }

    [JsonPropertyName("hasPreviousPage")]
    public bool HasPreviousPage { get; set; }

    [JsonPropertyName("startCursor")]
    public string? StartCursor { get; set; }

    [JsonPropertyName("endCursor")]
    public string? EndCursor { get; set; }
}

/// <summary>
/// Collection Revision Edge（Relay 分页）
/// </summary>
public class CollectionRevisionEdge
{
    [JsonPropertyName("node")]
    public NexusCollectionRevisionNode Node { get; set; } = new();

    [JsonPropertyName("cursor")]
    public string? Cursor { get; set; }
}

/// <summary>
/// Collection Revisions 连接（Relay 分页）
/// </summary>
public class CollectionRevisionsConnection
{
    [JsonPropertyName("edges")]
    public List<CollectionRevisionEdge> Edges { get; set; } = new();

    [JsonPropertyName("nodes")]
    public List<NexusCollectionRevisionNode> Nodes { get; set; } = new();

    [JsonPropertyName("pageInfo")]
    public PageInfo? PageInfo { get; set; }

    [JsonPropertyName("totalCount")]
    public int? TotalCount { get; set; }
}

/// <summary>
/// Collection Revision 详细信息（包含 modFiles）
/// </summary>
public class NexusCollectionRevisionDetail
{
    public long Id { get; set; }
    public int RevisionNumber { get; set; }
    public string Status { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public long TotalSize { get; set; }
    public int ModCount { get; set; }
    public int TotalDownloads { get; set; }
    public int UniqueDownloads { get; set; }
    public string DownloadLink { get; set; } = string.Empty;
    public bool IsLatest { get; set; }
    public bool AdultContent { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Collection 信息
    public string CollectionSlug { get; set; } = string.Empty;
    public string CollectionName { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string GameDomain { get; set; } = string.Empty;

    // Mod 文件列表
    public List<NexusCollectionModFile> ModFiles { get; set; } = new();

    // 辅助属性
    public string FileSizeFormatted
    {
        get
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double size = FileSize;
            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }
            return $"{size:0.##} {sizes[order]}";
        }
    }
}

/// <summary>
/// Collection 中的 Mod 文件信息
/// </summary>
public class NexusCollectionModFile
{
    public long ModId { get; set; }
    public long FileId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public bool Optional { get; set; }
}

/// <summary>
/// GraphQL Collection Revision Detail 响应
/// </summary>
public class GraphQLCollectionRevisionDetailResponse
{
    [JsonPropertyName("data")]
    public GraphQLCollectionRevisionDetailData? Data { get; set; }
}

public class GraphQLCollectionRevisionDetailData
{
    [JsonPropertyName("collectionRevision")]
    public NexusCollectionRevisionDetailNode? CollectionRevision { get; set; }
}

public class NexusCollectionRevisionDetailNode
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("revisionNumber")]
    public int RevisionNumber { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("revisionStatus")]
    public string? RevisionStatus { get; set; }

    [JsonPropertyName("fileSize")]
    public string? FileSizeStr { get; set; }

    [JsonPropertyName("totalSize")]
    public string? TotalSizeStr { get; set; }

    [JsonPropertyName("modCount")]
    public int ModCount { get; set; }

    [JsonPropertyName("totalDownloads")]
    public int TotalDownloads { get; set; }

    [JsonPropertyName("uniqueDownloads")]
    public int UniqueDownloads { get; set; }

    [JsonPropertyName("downloadLink")]
    public string? DownloadLink { get; set; }

    [JsonPropertyName("latest")]
    public bool Latest { get; set; }

    [JsonPropertyName("adultContent")]
    public bool AdultContent { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    [JsonPropertyName("collection")]
    public NexusCollectionRevisionDetailCollection? Collection { get; set; }
}

public class NexusCollectionRevisionDetailCollection
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("game")]
    public NexusGame? Game { get; set; }

    [JsonPropertyName("user")]
    public NexusCollectionUser? User { get; set; }
}

public class NexusCollectionModFileNode
{
    [JsonPropertyName("modId")]
    public long ModId { get; set; }

    [JsonPropertyName("fileId")]
    public long FileId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("optional")]
    public bool Optional { get; set; }
}

/// <summary>
/// 分页 Collection Revisions GraphQL 响应（Relay 风格）
/// </summary>
public class GraphQLCollectionRevisionsPagedResponse
{
    [JsonPropertyName("data")]
    public GraphQLCollectionRevisionsPagedData? Data { get; set; }
}

public class GraphQLCollectionRevisionsPagedData
{
    [JsonPropertyName("collection")]
    public GraphQLCollectionRevisionsPaged? Collection { get; set; }
}

public class GraphQLCollectionRevisionsPaged
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("revisions")]
    public CollectionRevisionsConnection? Revisions { get; set; }
}

/// <summary>
/// 分页结果（包含数据和分页信息）
/// </summary>
public class CollectionRevisionsPagedResult
{
    public List<NexusCollectionRevision> Revisions { get; set; } = new();
    public bool HasNextPage { get; set; }
    public string? EndCursor { get; set; }
    public int? TotalCount { get; set; }
}

#endregion

#region Collection Export Models

/// <summary>
/// Nexus Collection 导出数据结构
/// </summary>
public class NexusCollectionExport
{
    [JsonPropertyName("collectionName")]
    public string CollectionName { get; set; } = string.Empty;

    [JsonPropertyName("collectionSlug")]
    public string CollectionSlug { get; set; } = string.Empty;

    [JsonPropertyName("revisionNumber")]
    public int RevisionNumber { get; set; }

    [JsonPropertyName("revisionId")]
    public long RevisionId { get; set; }

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("compiledBy")]
    public long CompiledBy { get; set; }

    [JsonPropertyName("compiledByName")]
    public string CompiledByName { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    [JsonPropertyName("modCount")]
    public int ModCount { get; set; }

    [JsonPropertyName("fileSize")]
    public long FileSize { get; set; }

    [JsonPropertyName("fileSizeFormatted")]
    public string FileSizeFormatted { get; set; } = string.Empty;

    [JsonPropertyName("modFiles")]
    public List<NexusCollectionModFileExport> ModFiles { get; set; } = new();

    [JsonPropertyName("exportDate")]
    public DateTime ExportDate { get; set; } = DateTime.Now;

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";
}

/// <summary>
/// Nexus Collection 导出中的 Mod 文件信息
/// </summary>
public class NexusCollectionModFileExport
{
    [JsonPropertyName("modId")]
    public long ModId { get; set; }

    [JsonPropertyName("fileId")]
    public long FileId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("isOptional")]
    public bool IsOptional { get; set; }

    [JsonPropertyName("downloadUrl")]
    public string? DownloadUrl { get; set; }

    [JsonPropertyName("nxmLink")]
    public string? NxmLink { get; set; }
}

#endregion
