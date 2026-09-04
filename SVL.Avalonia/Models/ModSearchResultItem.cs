using System;
using System.Collections.Generic;

namespace SVL.Avalonia.Models;

/// <summary>资源来源（搜索结果结构化模型用）。</summary>
public enum CatalogSource
{
    Unknown,
    GitHub,
    NexusMods,
    Curseforge
}

/// <summary>资源身份标识。用于跨页面传递资源唯一身份（ResourceId + Source + IsModpack）。</summary>
public readonly record struct CatalogResourceIdentity(long ResourceId, string Name, CatalogSource Source, bool IsModpack, string CollectionSlug);

/// <summary>
/// 搜索结果结构化项。替代旧的反模式（用格式化字符串 + 副表反查身份）。
/// 由 RemoteCatalogService 的搜索方法返回，承载 UI 绑定所需的全部字段。
/// </summary>
public sealed class ModSearchResultItem
{
    /// <summary>资源身份（跨页面传递用，详情页据此加载）。</summary>
    public CatalogResourceIdentity Identity { get; init; }

    /// <summary>显示名称（已应用社区本地化）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>摘要（已应用社区本地化）。</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>统计信息（下载量/星标等，已格式化为字符串）。</summary>
    public string Stat { get; init; } = string.Empty;

    /// <summary>时间标签（更新时间等）。</summary>
    public string TimeTag { get; init; } = string.Empty;

    /// <summary>图标 URL（缩略图，可能为空）。</summary>
    public string IconUrl { get; init; } = string.Empty;

    /// <summary>完整图标 URL（高清，可能为空）。</summary>
    public string FullIconUrl { get; init; } = string.Empty;

    /// <summary>Mod 类型标签（如 SMAPI、Content 等，用于筛选显示）。</summary>
    public string ModType { get; init; } = string.Empty;

    /// <summary>游戏版本标签（兼容版本提示）。</summary>
    public string GameVersionTag { get; init; } = string.Empty;

    /// <summary>来源显示文本（如 "NexusMods"、"Curseforge"、"GitHub"）。</summary>
    public string SourceDisplay => Identity.Source switch
    {
        CatalogSource.NexusMods => "NexusMods",
        CatalogSource.Curseforge => "Curseforge",
        CatalogSource.GitHub => "GitHub",
        _ => string.Empty
    };

    /// <summary>是否为整合包（区分 Mod/Modpack 搜索）。</summary>
    public bool IsModpack => Identity.IsModpack;

    /// <summary>Nexus Collection 的 slug（仅 IsModpack 且来源为 NexusMods 时有值，用于详情页拉取 revisions）。</summary>
    public string CollectionSlug => Identity.CollectionSlug ?? string.Empty;

    /// <summary>来源前缀标签（UI 显示用，如 "NexusMod#123"）。</summary>
    public string SourceTag => $"{SourceDisplay}{(Identity.IsModpack ? "Pack" : "")}#{Identity.ResourceId}";
}
