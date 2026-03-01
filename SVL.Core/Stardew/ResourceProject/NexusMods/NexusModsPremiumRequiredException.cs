using System;

namespace SVL.Core.Stardew.ResourceProject.NexusMods;

/// <summary>
/// NexusMods Premium 权限异常
/// 当遇到 403 错误时抛出，表示需要 Premium 或使用浏览器下载
/// </summary>
public class NexusModsPremiumRequiredException : Exception
{
    /// <summary>
    /// Mod ID
    /// </summary>
    public long ModId { get; }

    /// <summary>
    /// 文件 ID
    /// </summary>
    public long FileId { get; }

    /// <summary>
    /// Mod 名称
    /// </summary>
    public string? ModName { get; set; }

    /// <summary>
    /// 文件名称
    /// </summary>
    public string? FileName { get; set; }

    /// <summary>
    /// NexusMods 文件页面 URL
    /// </summary>
    public string FilesPageUrl => $"https://www.nexusmods.com/stardewvalley/mods/{ModId}?tab=files";

    public NexusModsPremiumRequiredException(long modId, long fileId)
        : base("需要 NexusMods Premium 权限或使用浏览器下载")
    {
        ModId = modId;
        FileId = fileId;
    }

    public NexusModsPremiumRequiredException(long modId, long fileId, string message)
        : base(message)
    {
        ModId = modId;
        FileId = fileId;
    }

    public NexusModsPremiumRequiredException(long modId, long fileId, string message, Exception innerException)
        : base(message, innerException)
    {
        ModId = modId;
        FileId = fileId;
    }
}
