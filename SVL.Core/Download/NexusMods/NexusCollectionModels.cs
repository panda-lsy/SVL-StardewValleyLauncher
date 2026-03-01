using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SVL.Core.Download.NexusMods;

/// <summary>
/// NexusMods 集合信息
/// </summary>
public class NexusCollectionInfo
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("slug")]
    public string Slug { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("summary")]
    public string Summary { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; }

    [JsonPropertyName("picture_url")]
    public string PictureUrl { get; set; }

    [JsonPropertyName("user")]
    public NexusCollectionUser User { get; set; }

    [JsonPropertyName("game_id")]
    public long GameId { get; set; }

    [JsonPropertyName("total_downloads")]
    public long TotalDownloads { get; set; }

    [JsonPropertyName("endorsement_count")]
    public int EndorsementCount { get; set; }

    [JsonPropertyName("created_time")]
    public long CreatedTime { get; set; }

    [JsonPropertyName("updated_time")]
    public long UpdatedTime { get; set; }

    [JsonPropertyName("revision")]
    public int Revision { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("mod_count")]
    public int ModCount { get; set; }
}

/// <summary>
/// 集合用户信息
/// </summary>
public class NexusCollectionUser
{
    [JsonPropertyName("user_id")]
    public long UserId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("avatar_url")]
    public string AvatarUrl { get; set; }
}

/// <summary>
/// 集合中的 Mod 信息
/// </summary>
public class NexusCollectionMod
{
    [JsonPropertyName("mod")]
    public NexusCollectionModDetails Mod { get; set; }

    [JsonPropertyName("file")]
    public NexusCollectionFile File { get; set; }

    [JsonPropertyName("optional")]
    public bool Optional { get; set; }
}

/// <summary>
/// 集合 Mod 详情
/// </summary>
public class NexusCollectionModDetails
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("game_id")]
    public long GameId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("summary")]
    public string Summary { get; set; }

    [JsonPropertyName("picture_url")]
    public string PictureUrl { get; set; }
}

/// <summary>
/// 集合文件信息
/// </summary>
public class NexusCollectionFile
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("file_id")]
    public long FileId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("category")]
    public NexusCollectionFileCategory Category { get; set; }
}

/// <summary>
/// 集合文件分类
/// </summary>
public class NexusCollectionFileCategory
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }
}

/// <summary>
/// 集合下载链接响应
/// </summary>
public class NexusCollectionDownloadResponse
{
    [JsonPropertyName("collection")]
    public NexusCollectionInfo Collection { get; set; }

    [JsonPropertyName("mods")]
    public List<NexusCollectionMod> Mods { get; set; }

    [JsonPropertyName("download_links")]
    public Dictionary<string, string> DownloadLinks { get; set; }
}

/// <summary>
/// Nexus Collection JSON 根对象（从 7z 文件中解析）
/// </summary>
public class NexusCollectionJson
{
    [JsonPropertyName("info")]
    public NexusCollectionJsonInfo? Info { get; set; }

    [JsonPropertyName("mods")]
    public NexusCollectionJsonMod[]? Mods { get; set; }

    [JsonPropertyName("modRules")]
    public NexusCollectionJsonModRule[]? ModRules { get; set; }
}

/// <summary>
/// Collection JSON 基本信息
/// </summary>
public class NexusCollectionJsonInfo
{
    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("authorUrl")]
    public string? AuthorUrl { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("installInstructions")]
    public string? InstallInstructions { get; set; }

    [JsonPropertyName("domainName")]
    public string? DomainName { get; set; }

    [JsonPropertyName("gameVersions")]
    public string[]? GameVersions { get; set; }
}

/// <summary>
/// Collection JSON Mod
/// </summary>
public class NexusCollectionJsonMod
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("optional")]
    public bool Optional { get; set; }

    [JsonPropertyName("domainName")]
    public string? DomainName { get; set; }

    [JsonPropertyName("phase")]
    public int Phase { get; set; } = 1;

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("details")]
    public NexusCollectionJsonModDetails? Details { get; set; }

    [JsonPropertyName("source")]
    public NexusCollectionJsonModSource? Source { get; set; }

    [JsonPropertyName("patches")]
    public Dictionary<string, string>? Patches { get; set; }
}

/// <summary>
/// Collection JSON Mod 详细信息
/// </summary>
public class NexusCollectionJsonModDetails
{
    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

/// <summary>
/// Collection JSON Mod 源信息
/// </summary>
public class NexusCollectionJsonModSource
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }  // nexus | manual | browse | direct | bundle

    [JsonPropertyName("modId")]
    public long ModId { get; set; }

    [JsonPropertyName("fileId")]
    public long FileId { get; set; }

    [JsonPropertyName("md5")]
    public string? Md5 { get; set; }

    [JsonPropertyName("fileSize")]
    public long FileSize { get; set; }

    [JsonPropertyName("logicalFilename")]
    public string? LogicalFilename { get; set; }

    [JsonPropertyName("updatePolicy")]
    public string? UpdatePolicy { get; set; }  // exact | latest | prefer

    [JsonPropertyName("tag")]
    public string? Tag { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }  // 直链下载 URL（用于 browse/direct/manual 类型）
}

/// <summary>
/// Collection JSON Mod 加载规则
/// </summary>
public class NexusCollectionJsonModRule
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("reference")]
    public NexusCollectionJsonModReference? Reference { get; set; }
}

/// <summary>
/// Collection JSON Mod 引用
/// </summary>
public class NexusCollectionJsonModReference
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }  // mod | collection | game

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("fileId")]
    public long? FileId { get; set; }

    [JsonPropertyName("link")]
    public string? Link { get; set; }
}
