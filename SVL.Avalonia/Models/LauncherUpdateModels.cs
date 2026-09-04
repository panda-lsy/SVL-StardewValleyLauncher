namespace SVL.Avalonia.Models;

public sealed class LauncherReleaseAsset
{
    public string Name { get; set; } = string.Empty;

    public string BrowserDownloadUrl { get; set; } = string.Empty;

    public long Size { get; set; }
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