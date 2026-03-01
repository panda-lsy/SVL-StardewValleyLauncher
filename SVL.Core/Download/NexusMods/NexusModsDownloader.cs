using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SVL.Core.Logging;

namespace SVL.Core.Download.NexusMods;

/// <summary>
/// Nexus Mods下载进度报告
/// </summary>
public class NexusDownloadProgress
{
    public long BytesReceived { get; set; }
    public long TotalBytes { get; set; }
    public int Percentage { get; set; }
    public double Speed { get; set; } // bytes per second
    public string FileName { get; set; }
    public string Status { get; set; }
    public int CurrentUrlIndex { get; set; } // 当前使用的 CDN URL 索引
    public int TotalUrls { get; set; } // 总 CDN URL 数量
    public bool IsResuming { get; set; } // 是否为续传
}

/// <summary>
/// Nexus Mods下载结果
/// </summary>
public class NexusDownloadResult
{
    public bool Success { get; set; }
    public bool RequiresPremiumManualDownload { get; set; }
    public string FilePath { get; set; }
    public string FileName { get; set; }
    public long FileSize { get; set; }
    public string Error { get; set; }
    public TimeSpan DownloadTime { get; set; }
    public int UsedUrlIndex { get; set; } // 成功使用的 CDN URL 索引
    public bool WasResumed { get; set; } // 是否使用了断点续传
}

/// <summary>
/// Nexus Mods下载器
/// 参考 Mod Organizer 实现，支持：
/// - NXM key 认证（非 Premium 用户）
/// - 多 CDN URL 自动切换
/// - HTTP Range 断点续传
/// - .meta 文件存储（支持应用重启后恢复）
/// - 流式下载（80KB-1MB 缓冲区）
/// - 取消支持
/// </summary>
public class NexusModsDownloader : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _downloadDirectory;
    private CancellationTokenSource _cts = new();
    private bool _disposed = false;

    // 缓冲区大小：80KB（Mod Organizer 使用 80KB-1MB）
    private const int BufferSize = 80 * 1024;

    public NexusModsDownloader(string downloadDirectory)
    {
        _downloadDirectory = downloadDirectory ?? throw new ArgumentNullException(nameof(downloadDirectory));

        // 确保下载目录存在
        if (!Directory.Exists(_downloadDirectory))
            Directory.CreateDirectory(_downloadDirectory);

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(30) // 30分钟超时
        };
    }

    /// <summary>
    /// 下载Mod文件（支持断点续传和 NXM key）
    /// </summary>
    public async Task<NexusDownloadResult> DownloadModAsync(
        NxmUrl nxmUrl,
        string accessToken,
        IProgress<NexusDownloadProgress> progress = null)
    {
        if (nxmUrl == null)
            throw new ArgumentNullException(nameof(nxmUrl));
        if (string.IsNullOrEmpty(accessToken))
            throw new ArgumentNullException(nameof(accessToken));

        Log.Info($"[NexusDownloader] 开始下载: game={nxmUrl.GameId}, mod={nxmUrl.ModId}, file={nxmUrl.FileId}");

        var startTime = DateTime.Now;
        bool wasResumed = false;

        try
        {
            // 创建新的 CancellationTokenSource 用于此次下载
            using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);

            // 1. 创建API客户端
            using (var apiClient = new NexusApiClient(accessToken))
            {
                // 获取文件信息
                var fileInfo = await apiClient.GetFileInfoAsync(nxmUrl.GameId, nxmUrl.ModId, nxmUrl.FileId);
                var fileName = fileInfo.FileName;
                var totalBytes = fileInfo.Size;

                Log.Info($"[NexusDownloader] 文件信息: {fileName}, 大小: {totalBytes} bytes");

                // 2. 准备本地文件路径
                var filePath = Path.Combine(_downloadDirectory, fileName);
                long downloadedBytes = 0;

                // 3. 检查是否有续传元数据
                var existingMeta = NexusDownloadMetaManager.LoadMeta(filePath);

                // 4. 获取下载链接列表（优先使用 NXM key）
                List<string> downloadUrls;

                // 如果有元数据且 URL 列表有效，使用缓存的 URL
                if (existingMeta != null && existingMeta.DownloadUrls.Count > 0 && !existingMeta.IsNxMKeyExpired)
                {
                    downloadUrls = existingMeta.DownloadUrls;
                    downloadedBytes = existingMeta.DownloadedBytes;
                    wasResumed = downloadedBytes > 0 && downloadedBytes < totalBytes;

                    if (wasResumed)
                    {
                        Log.Info($"[NexusDownloader] 检测到续传元数据: 已下载 {downloadedBytes}/{totalBytes} bytes");
                    }
                    else
                    {
                        Log.Info($"[NexusDownloader] 检测到元数据但未开始下载或已完成，重新下载");
                        downloadedBytes = 0; // 重新开始
                    }
                }
                else
                {
                    // 获取新的下载链接
                    string? nxmKey = nxmUrl.Key;
                    string? expires = nxmUrl.Expires?.ToString();

                    if (!string.IsNullOrEmpty(nxmKey))
                    {
                        try
                        {
                            downloadUrls = await apiClient.GetDownloadLinkWithKeyAsync(
                                nxmUrl.GameId,
                                nxmUrl.ModId,
                                nxmUrl.FileId,
                                nxmKey,
                                expires
                            );
                            Log.Info($"[NexusDownloader] 使用 NXM key 获取到 {downloadUrls.Count} 个 CDN URL");
                        }
                        catch (InvalidOperationException ex)
                        {
                            Log.Warn($"[NexusDownloader] NXM key 获取失败: {ex.Message}，尝试直接获取");
                            var directUrl = await apiClient.GetDownloadLinkAsync(nxmUrl.GameId, nxmUrl.ModId, nxmUrl.FileId);
                            downloadUrls = new List<string> { directUrl };
                        }
                    }
                    else
                    {
                        var directUrl = await apiClient.GetDownloadLinkAsync(nxmUrl.GameId, nxmUrl.ModId, nxmUrl.FileId);
                        downloadUrls = new List<string> { directUrl };
                        Log.Info($"[NexusDownloader] 直接获取下载链接（Premium）");
                    }

                    // 创建并保存元数据
                    var newMeta = NexusDownloadMetaManager.CreateMeta(
                        nxmUrl.GameId,
                        nxmUrl.ModId,
                        nxmUrl.FileId,
                        fileInfo.Name ?? nxmUrl.ModId.ToString(),
                        fileName,
                        totalBytes,
                        downloadUrls,
                        nxmUrl.Key,
                        nxmUrl.Expires,
                        nxmUrl.UserId
                    );
                    NexusDownloadMetaManager.SaveMeta(filePath, newMeta);
                }

                // 5. 如果文件已存在且不是续传，删除它
                if (File.Exists(filePath) && !wasResumed)
                {
                    var backupPath = Path.Combine(_downloadDirectory, $"{Path.GetFileNameWithoutExtension(fileName)}_old{Path.GetExtension(fileName)}");
                    if (File.Exists(backupPath))
                        File.Delete(backupPath);
                    File.Move(filePath, backupPath);
                    Log.Info($"[NexusDownloader] 已备份旧文件: {backupPath}");
                }

                Log.Info($"[NexusDownloader] 开始下载到: {filePath}");
                Log.Info($"[NexusDownloader] 共有 {downloadUrls.Count} 个 CDN URL 可用");

                // 6. 尝试使用多个 CDN URL 进行下载
                for (int urlIndex = 0; urlIndex < downloadUrls.Count; urlIndex++)
                {
                    if (downloadCts.Token.IsCancellationRequested)
                    {
                        return new NexusDownloadResult
                        {
                            Success = false,
                            Error = "下载已取消"
                        };
                    }

                    var downloadUrl = downloadUrls[urlIndex];
                    Log.Info($"[NexusDownloader] 尝试使用 CDN [{urlIndex + 1}/{downloadUrls.Count}]: {downloadUrl}");

                    try
                    {
                        var result = await DownloadFromUrlAsync(
                            downloadUrl,
                            filePath,
                            totalBytes,
                            fileName,
                            startTime,
                            urlIndex,
                            downloadUrls.Count,
                            downloadedBytes,
                            wasResumed,
                            progress,
                            downloadCts.Token
                        );

                        if (result.Success)
                        {
                            result.UsedUrlIndex = urlIndex;
                            result.WasResumed = wasResumed;

                            // 下载完成，删除元数据
                            NexusDownloadMetaManager.DeleteMeta(filePath);

                            Log.Info($"[NexusDownloader] ✓ 下载成功，使用 CDN [{urlIndex + 1}]");
                            return result;
                        }
                        else
                        {
                            Log.Warn($"[NexusDownloader] CDN [{urlIndex + 1}] 下载失败: {result.Error}");
                            // 继续尝试下一个 URL
                        }
                    }
                    catch (HttpRequestException ex)
                    {
                        Log.Warn($"[NexusDownloader] CDN [{urlIndex + 1}] 网络错误: {ex.Message}");
                        // 继续尝试下一个 URL
                    }
                    catch (TaskCanceledException)
                    {
                        Log.Info($"[NexusDownloader] 下载已取消");
                        return new NexusDownloadResult
                        {
                            Success = false,
                            Error = "下载已取消"
                        };
                    }
                }

                // 所有 CDN URL 都失败了
                return new NexusDownloadResult
                {
                    Success = false,
                    Error = $"所有 {downloadUrls.Count} 个 CDN URL 都失败"
                };
            }
        }
        catch (NexusPremiumRequiredException ex)
        {
            Log.Warn($"[NexusDownloader] 需要浏览器手动下载（非 Premium API 限制）: {ex.Message}");
            return new NexusDownloadResult
            {
                Success = false,
                RequiresPremiumManualDownload = true,
                Error = $"NEXUS_PREMIUM_REQUIRED|{ex.Message}"
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[NexusDownloader] 下载失败");
            return new NexusDownloadResult
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// 从单个 URL 下载文件（支持断点续传）
    /// </summary>
    private async Task<NexusDownloadResult> DownloadFromUrlAsync(
        string downloadUrl,
        string filePath,
        long totalBytes,
        string fileName,
        DateTime startTime,
        int urlIndex,
        int totalUrls,
        long startByteOffset,
        bool isResuming,
        IProgress<NexusDownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        Log.Info($"[NexusDownloader] 开始从 CDN 下载: {downloadUrl}");

        try
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl))
            {
                // 如果是续传，使用 Range 请求
                if (isResuming && startByteOffset > 0 && startByteOffset < totalBytes)
                {
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(startByteOffset, null);
                    Log.Info($"[NexusDownloader] 断点续传: Range: bytes={startByteOffset}-");
                }

                using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    response.EnsureSuccessStatusCode();

                    // 检查服务器是否支持 Range 请求
                    var isPartialContent = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
                    if (isResuming && !isPartialContent && startByteOffset > 0)
                    {
                        Log.Warn("[NexusDownloader] 服务器不支持断点续传，从头开始下载");
                        startByteOffset = 0;
                        isResuming = false;
                    }
                    else if (isPartialContent)
                    {
                        Log.Info("[NexusDownloader] 服务器支持断点续传 (206 Partial Content)");
                    }

                    var contentLength = response.Content.Headers.ContentLength ?? (totalBytes - startByteOffset);
                    var actualTotal = startByteOffset + contentLength;
                    var buffer = new byte[BufferSize];
                    long totalRead = startByteOffset;
                    int read;

                    using (var fileStream = new FileStream(
                        filePath,
                        startByteOffset > 0 ? FileMode.Append : FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        BufferSize,
                        true
                    ))
                    using (var stream = await response.Content.ReadAsStreamAsync())
                    {
                        var lastProgressUpdate = DateTime.Now;
                        var lastMetaUpdate = totalRead;

                        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, read, cancellationToken);
                            totalRead += read;

                            // 检查取消
                            if (cancellationToken.IsCancellationRequested)
                            {
                                // 保存当前进度到元数据
                                NexusDownloadMetaManager.UpdateProgress(filePath, totalRead);
                                throw new TaskCanceledException();
                            }

                            // 每 1MB 或每 5 秒更新一次元数据
                            if (totalRead - lastMetaUpdate > 1024 * 1024 || (DateTime.Now - lastProgressUpdate).TotalSeconds >= 5)
                            {
                                NexusDownloadMetaManager.UpdateProgress(filePath, totalRead);
                                lastMetaUpdate = totalRead;
                            }

                            // 每 200ms 更新一次进度
                            var now = DateTime.Now;
                            if ((now - lastProgressUpdate).TotalMilliseconds >= 200 || totalRead == actualTotal)
                            {
                                var percentage = actualTotal > 0
                                    ? (int)(totalRead * 100 / actualTotal)
                                    : 0;

                                // 计算速度
                                var timeElapsed = (now - startTime).TotalSeconds;
                                var speed = timeElapsed > 0 ? totalRead / timeElapsed : 0;

                                progress?.Report(new NexusDownloadProgress
                                {
                                    BytesReceived = totalRead,
                                    TotalBytes = actualTotal,
                                    Percentage = percentage,
                                    Speed = speed,
                                    FileName = fileName,
                                    Status = isResuming ? $"续传中... ({urlIndex + 1}/{totalUrls})" : $"下载中... ({urlIndex + 1}/{totalUrls})",
                                    CurrentUrlIndex = urlIndex + 1,
                                    TotalUrls = totalUrls,
                                    IsResuming = isResuming
                                });

                                lastProgressUpdate = now;

                                Log.Debug($"[NexusDownloader] 下载进度: {percentage}% ({totalRead}/{actualTotal} bytes), 速度: {speed / 1024 / 1024:F2} MB/s");
                            }
                        }
                    }

                    Log.Info($"[NexusDownloader] ✓ 下载完成: {filePath}, 大小: {totalRead} bytes");
                    var downloadTime = DateTime.Now - startTime;

                    return new NexusDownloadResult
                    {
                        Success = true,
                        FilePath = filePath,
                        FileName = Path.GetFileName(filePath),
                        FileSize = new FileInfo(filePath).Length,
                        DownloadTime = downloadTime
                    };
                }
            }
        }
        catch (TaskCanceledException)
        {
            // 清理部分下载的文件（如果不是续传）
            if (!isResuming && File.Exists(filePath))
            {
                try
                {
                    File.Delete(filePath);
                    Log.Info($"[NexusDownloader] 已清理部分下载的文件: {filePath}");
                }
                catch (Exception ex)
                {
                    Log.Warn($"[NexusDownloader] 清理文件失败: {ex.Message}");
                }
            }
            throw;
        }
    }

    /// <summary>
    /// 取消当前下载
    /// </summary>
    public void CancelDownload()
    {
        if (!_disposed && _cts != null && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
            Log.Info("[NexusDownloader] 已请求取消下载");
        }
    }

    /// <summary>
    /// 重置下载器（用于开始新的下载）
    /// </summary>
    public void Reset()
    {
        if (_cts != null)
        {
            _cts.Cancel();
            _cts.Dispose();
        }
        _cts = new CancellationTokenSource();
        Log.Info("[NexusDownloader] 下载器已重置");
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _cts?.Dispose();
            _httpClient?.Dispose();
            _disposed = true;
        }
    }
}
