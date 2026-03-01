using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SVL.Core.Logging;
using SVL.Core.Stardew.ResourceProject.NexusMods;

namespace SVL.Core.Download.NexusMods;

/// <summary>
/// NexusMods 浏览器下载任务
/// 用于非 Premium 用户：打开浏览器 → 等待 NXM 回调 → 下载 → 安装
/// </summary>
public class NexusModsBrowserDownloadTask : DownloadTask
{
    private readonly string _gameId;
    private readonly long _modId;
    private readonly long _fileId;
    private readonly string _modName;
    private readonly string _fileName;
    private readonly string _downloadPageUrl;
    private readonly string _targetModsPath;
    private readonly string _gameBasePath;
    private readonly CancellationTokenSource _cts = new();

    // NXM 回调协调
    private TaskCompletionSource<NxmUrl>? _nxmCompletionSource;

    /// <summary>
    /// 创建 NexusMods 浏览器下载任务
    /// </summary>
    public NexusModsBrowserDownloadTask(
        string gameId,
        long modId,
        long fileId,
        string modName,
        string fileName,
        string downloadPageUrl,
        string targetModsPath,
        string gameBasePath)
    {
        _gameId = gameId;
        _modId = modId;
        _fileId = fileId;
        _modName = modName;
        _fileName = fileName;
        _downloadPageUrl = downloadPageUrl;
        _targetModsPath = targetModsPath;
        _gameBasePath = gameBasePath;

        Type = DownloadTaskType.Mod;
        Name = $"{_modName} (浏览器下载)";
        Status = DownloadTaskStatus.WaitingConfirmation;
        StatusMessage = "等待打开浏览器下载...";
        Progress = 0;
    }

    /// <summary>
    /// 待匹配的 ModId
    /// </summary>
    public long PendingModId => _modId;

    /// <summary>
    /// 待匹配的 FileId
    /// </summary>
    public long PendingFileId => _fileId;

    /// <summary>
    /// 处理 NXM URL 回调
    /// </summary>
    public bool HandleNxmUrl(NxmUrl nxmUrl)
    {
        if (nxmUrl.ModId != _modId || nxmUrl.FileId != _fileId)
        {
            Log.Debug($"[NexusBrowserDownload] NXM URL 不匹配: 期望 ModId={_modId}, FileId={_fileId}, 实际 ModId={nxmUrl.ModId}, FileId={nxmUrl.FileId}");
            return false;
        }

        if (_nxmCompletionSource == null)
        {
            Log.Warn("[NexusBrowserDownload] 接收到 NXM URL 但没有在等待下载");
            return false;
        }

        Log.Info($"[NexusBrowserDownload] 接收到匹配的 NXM URL: ModId={nxmUrl.ModId}, FileId={nxmUrl.FileId}");
        _nxmCompletionSource.TrySetResult(nxmUrl);
        return true;
    }

    public override async Task ExecuteAsync()
    {
        try
        {
            // 1. 打开浏览器
            var urlWithNmm = _downloadPageUrl + (_downloadPageUrl.Contains("?") ? "&" : "?") + "nmm=1";
            Log.Info($"[NexusBrowserDownload] 打开浏览器: {urlWithNmm}");

            try
            {
                IO.ProcessEx.OpenUrl(urlWithNmm);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[NexusBrowserDownload] 打开浏览器失败");
                Status = DownloadTaskStatus.Failed;
                StatusMessage = $"打开浏览器失败: {ex.Message}";
                CompletedTime = DateTime.Now;
                return;
            }

            // 2. 等待 NXM 回调
            Status = DownloadTaskStatus.WaitingConfirmation;
            StatusMessage = $"请在浏览器中点击「Manual Download」({_modName})";

            _nxmCompletionSource = new TaskCompletionSource<NxmUrl>();

            using var registration = _cts.Token.Register(() =>
            {
                _nxmCompletionSource?.TrySetCanceled();
            });

            NxmUrl nxmUrl;
            var completedTask = await Task.WhenAny(
                _nxmCompletionSource.Task,
                Task.Delay(TimeSpan.FromMinutes(30), _cts.Token));

            if (completedTask == _nxmCompletionSource.Task)
            {
                nxmUrl = await _nxmCompletionSource.Task;
            }
            else
            {
                Status = DownloadTaskStatus.Failed;
                StatusMessage = "等待浏览器下载超时（30分钟）";
                CompletedTime = DateTime.Now;
                return;
            }

            _nxmCompletionSource = null;

            // 3. 通过 NXM key 下载文件
            Status = DownloadTaskStatus.Downloading;
            StatusMessage = $"正在下载: {_modName}...";

            var tempDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SVL", "temp", "nxm_browser", $"{_modId}_{_fileId}");
            Directory.CreateDirectory(tempDir);

            var progressCallback = new NexusModsService.DownloadProgressCallback(
                (progress, statusMessage, bytesRead, totalBytes) =>
                {
                    if (totalBytes > 0)
                    {
                        FileDownloadProgress = bytesRead * 100.0 / totalBytes;
                        FileDownloadBytes = bytesRead;
                        FileDownloadTotalBytes = totalBytes;
                    }
                    Progress = (int)(progress * 0.6); // 0-60%
                    StatusMessage = $"正在下载: {_modName}... {progress:F0}%";
                });

            var success = await NexusModsService.DownloadModAsync(
                _modId,
                _fileId,
                tempDir,
                nxmUrl.Key ?? string.Empty,
                nxmUrl.Expires?.ToString() ?? string.Empty,
                progressCallback,
                _cts.Token);

            if (!success)
            {
                Status = DownloadTaskStatus.Failed;
                StatusMessage = $"下载失败: {_modName}";
                CompletedTime = DateTime.Now;
                return;
            }

            // 4. 查找下载的文件
            var downloadedFile = Directory.GetFiles(tempDir, "*.zip", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => File.GetCreationTime(f))
                .FirstOrDefault();

            if (downloadedFile == null)
            {
                // 也查找非 zip
                downloadedFile = Directory.GetFiles(tempDir)
                    .OrderByDescending(f => File.GetCreationTime(f))
                    .FirstOrDefault();
            }

            if (string.IsNullOrEmpty(downloadedFile) || !File.Exists(downloadedFile))
            {
                // 尝试缓存
                var cached = NexusModsCacheService.Get(_modId, _fileId);
                if (!string.IsNullOrEmpty(cached) && File.Exists(cached))
                {
                    downloadedFile = cached;
                }
                else
                {
                    Status = DownloadTaskStatus.Failed;
                    StatusMessage = "下载成功但找不到文件";
                    CompletedTime = DateTime.Now;
                    return;
                }
            }

            // 保存缓存
            await NexusModsCacheService.SaveAsync(downloadedFile, _modId, _fileId);

            // 5. 安装: 创建 ModDownloadTask 执行安装
            Status = DownloadTaskStatus.Installing;
            StatusMessage = $"正在安装: {_modName}...";
            Progress = 65;

            var installTask = new ModDownloadTask(
                modId: _modId.ToString(),
                modName: _modName,
                fileName: Path.GetFileName(downloadedFile),
                localZipPath: downloadedFile,
                isLocalFile: true,
                gameBasePath: _gameBasePath,
                targetModsPath: _targetModsPath,
                saveOnly: false,
                sourcePlatform: "NexusMods",
                sourceProjectId: _modId.ToString(),
                sourceFileId: _fileId.ToString());

            await installTask.ExecuteAsync();

            if (installTask.Status == DownloadTaskStatus.Completed)
            {
                Status = DownloadTaskStatus.Completed;
                StatusMessage = $"已完成: {_modName}";
                Progress = 100;
                CompletedTime = DateTime.Now;
                Log.Info($"[NexusBrowserDownload] ✓ 安装完成: {_modName}");
            }
            else
            {
                Status = DownloadTaskStatus.Failed;
                StatusMessage = $"安装失败: {installTask.StatusMessage}";
                CompletedTime = DateTime.Now;
            }
        }
        catch (OperationCanceledException)
        {
            Status = DownloadTaskStatus.Cancelled;
            StatusMessage = "已取消";
        }
        catch (Exception ex)
        {
            Status = DownloadTaskStatus.Failed;
            StatusMessage = $"失败: {ex.Message}";
            CompletedTime = DateTime.Now;
            Log.Error(ex, $"[NexusBrowserDownload] 任务失败: {_modName}");
        }
    }

    public override void Cancel()
    {
        _cts.Cancel();
        _nxmCompletionSource?.TrySetCanceled();
        Status = DownloadTaskStatus.Cancelled;
        StatusMessage = "已取消";
        Log.Info($"[NexusBrowserDownload] 任务已取消: {_modName}");
    }
}
