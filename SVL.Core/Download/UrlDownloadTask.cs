using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SVL.Core.Logging;

namespace SVL.Core.Download;

/// <summary>
/// 通用 URL 接管下载任务（支持多线程分片下载）。
/// </summary>
public class UrlDownloadTask : DownloadTask
{
    private readonly string _url;
    private readonly string _targetFilePath;
    private readonly int _threadCount;
    private readonly CancellationTokenSource _cts = new();

    public UrlDownloadTask(string url, string targetFilePath, int threadCount)
    {
        _url = url;
        _targetFilePath = targetFilePath;
        _threadCount = Math.Max(1, Math.Min(16, threadCount));

        var fileName = Path.GetFileName(targetFilePath);
        Type = DownloadTaskType.Mod;
        Name = $"接管下载: {fileName}";
        Status = DownloadTaskStatus.Pending;
        StatusMessage = "等待下载...";
    }

    public override async Task ExecuteAsync()
    {
        try
        {
            Status = DownloadTaskStatus.Downloading;
            StatusMessage = "正在准备下载...";
            Progress = 0;

            Log.Info($"[UrlDownloadTask] 开始下载: {_url}, 目标: {_targetFilePath}, 线程: {_threadCount}");

            await HttpMultiThreadDownloader.DownloadAsync(
                _url,
                _targetFilePath,
                _threadCount,
                (progress, downloaded, total, speed) =>
                {
                    Progress = progress;
                    FileDownloadProgress = progress;
                    FileDownloadBytes = downloaded;
                    FileDownloadTotalBytes = total;

                    var downloadedMb = downloaded / 1024d / 1024d;
                    var totalMb = total / 1024d / 1024d;
                    var speedMb = speed / 1024d / 1024d;

                    StatusMessage = total > 0
                        ? $"下载中... {progress:F1}% ({downloadedMb:F1}/{totalMb:F1} MB, {speedMb:F1} MB/s)"
                        : $"下载中... {downloadedMb:F1} MB ({speedMb:F1} MB/s)";
                },
                _cts.Token);

            Status = DownloadTaskStatus.Completed;
            StatusMessage = $"✓ 下载完成: {Path.GetFileName(_targetFilePath)}";
            Progress = 100;
            FileDownloadProgress = 100;
            CompletedTime = DateTime.Now;

            Log.Info($"[UrlDownloadTask] 下载完成: {_targetFilePath}");
        }
        catch (OperationCanceledException)
        {
            Status = DownloadTaskStatus.Cancelled;
            StatusMessage = "已取消";
            CompletedTime = DateTime.Now;
            Log.Info($"[UrlDownloadTask] 下载取消: {_targetFilePath}");
        }
        catch (Exception ex)
        {
            Status = DownloadTaskStatus.Failed;
            StatusMessage = $"下载失败: {ex.Message}";
            CompletedTime = DateTime.Now;
            Log.Error(ex, $"[UrlDownloadTask] 下载失败: {_url}");
            throw;
        }
    }

    public override void Cancel()
    {
        _cts.Cancel();
    }
}
