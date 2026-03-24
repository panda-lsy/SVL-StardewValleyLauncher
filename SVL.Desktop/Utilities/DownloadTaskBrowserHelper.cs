using System;
using SVL.Core.Download;
using SVL.Core.Download.NexusMods;

namespace SVL.Desktop.Utilities;

/// <summary>
/// 提供下载任务的浏览器地址解析能力，避免各页面重复实现。
/// </summary>
public static class DownloadTaskBrowserHelper
{
    public const string ReopenBrowserStatusMessage = "已重新打开浏览器，请在浏览器中继续下载。";
    public const string WaitingBrowserDownloadStatusMessage = "等待浏览器下载（请点击下载按钮）...";

    public static string BuildNexusModFilePageUrl(long modId, long fileId)
    {
        return $"https://www.nexusmods.com/stardewvalley/mods/{modId}?tab=files&file_id={fileId}&nmm=1";
    }

    public static string BuildNexusModPageUrl(long modId)
    {
        return BuildNexusModPageUrl(modId.ToString());
    }

    public static string BuildNexusModPageUrl(string modId)
    {
        return $"https://www.nexusmods.com/stardewvalley/mods/{modId}";
    }

    public static string BuildNexusCollectionRevisionPageUrl(string collectionSlug, int revisionNumber)
    {
        return $"https://www.nexusmods.com/games/stardewvalley/collections/{collectionSlug}/revisions/{revisionNumber}";
    }

    public static bool HasBrowserOpenUrl(DownloadTask? task)
    {
        return TryGetBrowserOpenUrl(task, out _);
    }

    public static bool TryGetBrowserOpenUrl(DownloadTask? task, out string url)
    {
        url = string.Empty;

        if (task == null)
            return false;

        switch (task)
        {
            case PlaceholderDownloadTask placeholderTask when !string.IsNullOrWhiteSpace(placeholderTask.BrowserOpenUrl):
                url = placeholderTask.BrowserOpenUrl;
                return true;

            case SmapiDownloadTask smapiTask when !string.IsNullOrWhiteSpace(smapiTask.BrowserOpenUrl):
                url = smapiTask.BrowserOpenUrl;
                return true;

            case NexusModsBrowserDownloadTask browserTask when !string.IsNullOrWhiteSpace(browserTask.BrowserOpenUrl):
                url = browserTask.BrowserOpenUrl;
                return true;

            case NexusModsBrowserDownloadTask browserTask when browserTask.PendingModId > 0:
                url = BuildNexusModFilePageUrl(browserTask.PendingModId, browserTask.PendingFileId);
                return true;

            case SvlModpackInstallTask svlTask when svlTask.PendingNexusModId > 0:
                url = BuildNexusModFilePageUrl(svlTask.PendingNexusModId, svlTask.PendingNexusFileId);
                return true;

            default:
                return false;
        }
    }
}
