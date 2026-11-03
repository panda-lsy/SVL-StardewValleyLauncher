namespace SVL.Avalonia.Models;

/// <summary>发布时写入程序集的运行环境类型；未知类型不得自动替换为其他类型。</summary>
public enum LauncherRuntimeMode
{
    Unknown,
    SelfContained,
    FrameworkDependent
}

public sealed class LauncherReleaseAsset
{
    public string Name { get; set; } = string.Empty;

    public string BrowserDownloadUrl { get; set; } = string.Empty;

    public long Size { get; set; }

    /// <summary>发布源提供时用于校验完整性的 SHA-256；为空表示来源未提供。</summary>
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class LauncherReleaseInfo
{
    public string TagName { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public string HtmlUrl { get; set; } = string.Empty;

    public DateTime PublishedAt { get; set; }

    public bool IsPrerelease { get; set; }

    public string UpdateLog { get; set; } = string.Empty;

    public List<LauncherReleaseAsset> Assets { get; } = [];
}

public sealed class LauncherUpdateCheckResult
{
    public bool Success { get; set; }

    public string Source { get; set; } = "-";

    public string ErrorMessage { get; set; } = string.Empty;

    public bool HasUpdate { get; set; }

    public Version CurrentVersion { get; set; } = new(0, 0, 0, 0);

    public Version LatestVersion { get; set; } = new(0, 0, 0, 0);

    public LauncherReleaseInfo? ReleaseInfo { get; set; }
}

public enum UpdateDialogAction
{
    Later,
    SkipVersion,
    OpenRelease,
    /// <summary>用户选择应用内下载并安装更新（新架构补齐）。</summary>
    DownloadAndInstall
}
