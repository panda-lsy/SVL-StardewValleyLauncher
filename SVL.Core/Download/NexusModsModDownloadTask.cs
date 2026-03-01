using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SVL.Core.Config;
using SVL.Core.Download.NexusMods;
using SVL.Core.Logging;

namespace SVL.Core.Download;

/// <summary>
/// NexusMods Mod 下载任务
/// 支持：
/// - NXM key 认证（非 Premium 用户）
/// - 多 CDN URL 自动切换
/// - 取消下载
/// </summary>
public class NexusModsModDownloadTask : DownloadTask
{
    private readonly string _gameId;
    private readonly long _modId;
    private readonly long _fileId;
    private readonly string _modName;
    private readonly string _downloadDirectory;
    private readonly string? _oauthToken;  // OAuth Token（优先使用）
    private NexusModsDownloader? _downloader;  // 保持引用以便取消

    /// <summary>
    /// 创建 NexusMods Mod 下载任务
    /// </summary>
    public NexusModsModDownloadTask(
        string gameId,
        long modId,
        long fileId,
        string modName,
        string downloadDirectory,
        string? oauthToken = null)
    {
        _gameId = gameId;
        _modId = modId;
        _fileId = fileId;
        _modName = modName;
        _downloadDirectory = downloadDirectory;
        _oauthToken = oauthToken;

        Type = DownloadTaskType.Mod;
        Name = $"{_modName} (NexusMods)";
        StatusMessage = "准备下载...";
    }

    public override async Task ExecuteAsync()
    {
        string? accessToken = _oauthToken;

        // 如果没有提供 OAuth Token，从配置加载
        if (string.IsNullOrEmpty(accessToken))
        {
            var settings = AppConfig.GetSettings();
            accessToken = settings.NexusModsOAuthToken;

            if (string.IsNullOrEmpty(accessToken))
            {
                throw new Exception("未找到 NexusMods OAuth Token，请先登录");
            }
        }

        Log.Info($"[NexusDownload] 开始下载: modId={_modId}, fileId={_fileId}");

        try
        {
            Status = DownloadTaskStatus.Downloading;
            StatusMessage = "正在获取下载链接...";
            Progress = 0;

            // 创建 NXM URL
            var nxmUrl = NxmUrl.CreateModUrl(_gameId, _modId, _fileId);

            // 创建下载器并保存引用
            _downloader = new NexusModsDownloader(_downloadDirectory);

            // 创建进度报告
            var progress = new Progress<NexusDownloadProgress>(p =>
            {
                Progress = p.Percentage;

                // 显示当前使用的 CDN
                if (p.TotalUrls > 1)
                {
                    StatusMessage = $"下载中... {p.Percentage}% (CDN {p.CurrentUrlIndex}/{p.TotalUrls})";
                }
                else
                {
                    StatusMessage = $"下载中... {p.Percentage}%";
                }

                Log.Debug($"[NexusDownload] 进度: {p.Percentage}% - {p.Speed / 1024 / 1024:F2} MB/s");
            });

            // 执行下载
            var result = await _downloader.DownloadModAsync(nxmUrl, accessToken, progress);

            if (result.Success)
            {
                Status = DownloadTaskStatus.Completed;
                StatusMessage = $"✓ 下载完成: {result.FileName}";
                Progress = 100;
                CompletedTime = DateTime.Now;

                Log.Info($"[NexusDownload] ✓ 下载完成: {result.FileName}");
                Log.Info($"[NexusDownload]   保存路径: {result.FilePath}");
                Log.Info($"[NexusDownload]   文件大小: {result.FileSize} bytes");
                Log.Info($"[NexusDownload]   下载耗时: {result.DownloadTime.TotalSeconds:F2} 秒");

                if (result.UsedUrlIndex >= 0)
                {
                    Log.Info($"[NexusDownload]   使用 CDN: [{result.UsedUrlIndex + 1}]");
                }
            }
            else if (result.RequiresPremiumManualDownload)
            {
                // 非 Premium 用户需要使用浏览器下载
                Log.Warn($"[NexusDownload] 检测到非 Premium 用户，切换到浏览器下载模式");

                // 构造下载页面 URL（添加 nmm=1 参数启用 NXM 协议下载）
                var downloadPageUrl = $"https://www.nexusmods.com/{_gameId}/mods/{_modId}?tab=files&file_id={_fileId}&nmm=1";

                Status = DownloadTaskStatus.WaitingConfirmation;
                StatusMessage = "需要打开浏览器下载（非 Premium 用户）";
                Progress = 0;

                Log.Info($"[NexusDownload] 打开浏览器下载页面: {downloadPageUrl}");

                try
                {
                    // 打开浏览器
                    IO.ProcessEx.OpenUrl(downloadPageUrl);

                    Status = DownloadTaskStatus.WaitingConfirmation;
                    StatusMessage = "请在浏览器中下载文件，然后点击任务管理页面中的「文件已下载」按钮";

                    Log.Info($"[NexusDownload] ✓ 浏览器已打开，等待用户下载文件");
                    Log.Warn($"[NexusDownload] 用户需要在浏览器中手动下载文件，下载完成后点击「文件已下载」继续");

                    // 等待用户操作（通过设置状态来处理）
                    // 实际上这里会返回 WaitingConfirmation 状态
                    // 调用方需要处理这个状态
                }
                catch (Exception ex)
                {
                    Status = DownloadTaskStatus.Failed;
                    StatusMessage = $"打开浏览器失败: {ex.Message}";
                    Log.Error(ex, "[NexusDownload] 打开浏览器失败");
                    throw;
                }
            }
            else
            {
                Status = DownloadTaskStatus.Failed;
                StatusMessage = $"下载失败: {result.Error}";
                CompletedTime = DateTime.Now;

                Log.Error($"[NexusDownload] 下载失败: {result.Error}");
                throw new Exception(result.Error);
            }
        }
        catch (OperationCanceledException)
        {
            Status = DownloadTaskStatus.Cancelled;
            StatusMessage = "已取消";
            Log.Info($"[NexusDownload] 下载已取消: {_modName}");
        }
        catch (Exception ex)
        {
            Status = DownloadTaskStatus.Failed;
            StatusMessage = $"错误: {ex.Message}";
            CompletedTime = DateTime.Now;
            Log.Error(ex, $"[NexusDownload] 下载失败: {_modName}");
            throw;
        }
        finally
        {
            // 清理下载器
            try
            {
                _downloader?.Dispose();
                _downloader = null;
            }
            catch { }
        }
    }

    public override void Cancel()
    {
        try
        {
            // 调用下载器的取消方法
            _downloader?.CancelDownload();

            Status = DownloadTaskStatus.Cancelled;
            StatusMessage = "正在取消...";
            Log.Info($"[NexusDownload] 取消任务: {_modName}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NexusDownload] 取消任务失败");
        }
    }
}
