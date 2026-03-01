using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SVL.Core.Config;
using SVL.Core.Download.NexusMods;
using SVL.Core.Logging;
using SVL.Core.Stardew.ResourceProject.NexusMods;

namespace SVL.Core.Download.NexusMods;

public class NexusPremiumRequiredException : Exception
{
    public string GameId { get; }
    public long ModId { get; }
    public long FileId { get; }
    public string DownloadPageUrl => $"https://www.nexusmods.com/{GameId}/mods/{ModId}?tab=files&file_id={FileId}";

    public NexusPremiumRequiredException(string gameId, long modId, long fileId, string message)
        : base(message)
    {
        GameId = gameId;
        ModId = modId;
        FileId = fileId;
    }
}

/// <summary>
/// NexusMods 下载工作流（SMAPI 与普通 MOD 共用）。
/// </summary>
public static class NexusDownloadWorkflow
{
    public static bool IsPremiumRequiredError(string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
            return false;

        return errorMessage.IndexOf("NEXUS_PREMIUM_REQUIRED", StringComparison.OrdinalIgnoreCase) >= 0
               || (errorMessage.IndexOf("403", StringComparison.OrdinalIgnoreCase) >= 0
                   && (errorMessage.IndexOf("premium users only", StringComparison.OrdinalIgnoreCase) >= 0
                       || errorMessage.IndexOf("forbidden", StringComparison.OrdinalIgnoreCase) >= 0));
    }

    public static async Task<string> DownloadZipAsync(
        string gameId,
        long modId,
        long fileId,
        string workingDirectory,
        Action<NexusDownloadProgress> progressCallback = null,
        CancellationToken cancellationToken = default,
        bool useCache = true)
    {
        if (modId <= 0)
            throw new ArgumentException("modId 必须大于 0", nameof(modId));

        if (fileId <= 0)
            throw new ArgumentException("fileId 必须大于 0", nameof(fileId));

        if (string.IsNullOrWhiteSpace(gameId))
            throw new ArgumentException("gameId 不能为空", nameof(gameId));

        if (string.IsNullOrWhiteSpace(workingDirectory))
            throw new ArgumentException("workingDirectory 不能为空", nameof(workingDirectory));

        Directory.CreateDirectory(workingDirectory);

        // 检查是否启用下载文件缓存
        var settings = AppConfig.GetSettings();
        useCache = useCache && settings.EnableDownloadCache;

        if (useCache)
        {
            var cached = NexusModsCacheService.Get(modId, fileId);
            if (!string.IsNullOrWhiteSpace(cached) && File.Exists(cached))
            {
                Log.Info($"[NexusWorkflow] 命中下载缓存: modId={modId}, fileId={fileId}, path={cached}");
                progressCallback?.Invoke(new NexusDownloadProgress
                {
                    Percentage = 100,
                    BytesReceived = new FileInfo(cached).Length,
                    TotalBytes = new FileInfo(cached).Length,
                    Speed = 0,
                    CurrentUrlIndex = 1,
                    TotalUrls = 1
                });
                return cached;
            }
        }

        var accessToken = AppConfig.GetSettings().NexusModsOAuthToken;
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new Exception("未找到 NexusMods OAuth Token，请先在设置中登录 NexusMods");

        var nxmUrl = NxmUrl.CreateModUrl(gameId, modId, fileId);

        using var downloader = new NexusModsDownloader(workingDirectory);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() =>
            {
                try
                {
                    downloader.CancelDownload();
                }
                catch
                {
                    // ignore
                }
            });
        }

        var progress = new Progress<NexusDownloadProgress>(p => progressCallback?.Invoke(p));
        var result = await downloader.DownloadModAsync(nxmUrl, accessToken, progress);

        if (result.RequiresPremiumManualDownload || IsPremiumRequiredError(result.Error))
            throw new NexusPremiumRequiredException(gameId, modId, fileId, "非 Premium 用户需通过浏览器点击下载后由启动器接管 NXM。\n请在弹出的页面点击 Manual Download。 ");

        if (!result.Success || string.IsNullOrWhiteSpace(result.FilePath) || !File.Exists(result.FilePath))
            throw new Exception($"NexusMods 下载失败: {result.Error ?? "未知错误"}");

        if (useCache)
            await NexusModsCacheService.SaveAsync(result.FilePath, modId, fileId);

        return result.FilePath;
    }
}
