using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SVL.Avalonia.Models;

namespace SVL.Avalonia.Services;

/// <summary>
/// HTTP 下载服务：支持多线程分片下载、断点续传（.part + 元数据）、URL 缓存与代理。
/// - 多线程：按 DownloadSegmentThreads 分片并发，进度快照携带各分片进度（用于进度条分块显示）。
/// - 断点续传：下载写入 targetPath + ".part"，元数据记录各分片已下载字节数；失败/取消后重试自动续传。
/// - 缓存：EnableDownloadCache 开启后按 URL 哈希缓存，重复下载直接复制本地文件。
/// 代理配置从 AppUserSettingsStore 实时读取并按签名缓存 HttpClient。
/// </summary>
public sealed class HttpDownloadService
{
    private const int TransientDownloadRetryAttempts = 3;

    private readonly AppUserSettingsStore _settingsStore;
    private readonly object _httpClientLock = new();
    private HttpClient? _httpClient;
    private string _httpClientProxySignature = string.Empty;

    public HttpDownloadService(AppUserSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    /// <summary>下载文件（线程数由设置项 DownloadSegmentThreads 决定）。</summary>
    public Task DownloadAsync(
        string url,
        string targetPath,
        Action<DownloadProgressSnapshot>? onProgress,
        CancellationToken cancellationToken = default,
        Action<string>? log = null,
        Func<string, bool>? cacheValidator = null)
    {
        return DownloadAsync(url, targetPath, 0, onProgress, cancellationToken, log, cacheValidator);
    }

    /// <summary>下载文件（显式指定线程数；threadCount &lt;= 0 时读取设置项）。</summary>
    public async Task DownloadAsync(
        string url,
        string targetPath,
        int threadCount,
        Action<DownloadProgressSnapshot>? onProgress,
        CancellationToken cancellationToken = default,
        Action<string>? log = null,
        Func<string, bool>? cacheValidator = null)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("下载地址不能为空", nameof(url));
        }

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new ArgumentException("目标文件路径不能为空", nameof(targetPath));
        }

        // edge.forgecdn.net 通常还会 302 到 mediafilez.forgecdn.net。部分网络环境
        // 在这个跳转链上会丢失 Range 或提前断流；直接使用最终 CDN 主机仍保持同一
        // 文件路径和缓存语义，但可以避免一次不稳定的重定向。
        url = NormalizeDownloadUrl(url);

        // 即使命中本地缓存，也必须尊重已经发出的取消请求；否则取消按钮在
        // 缓存复制路径上不会生效，任务会被错误地标记为完成。
        cancellationToken.ThrowIfCancellationRequested();

        var settings = _settingsStore.Load();
        if (threadCount <= 0)
        {
            threadCount = Math.Clamp(settings.DownloadSegmentThreads, 1, 16);
        }
        else
        {
            threadCount = Math.Max(1, Math.Min(16, threadCount));
        }

        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 缓存命中：直接复制本地缓存文件，免重复下载
        if (settings.EnableDownloadCache)
        {
            var cachePath = DownloadFileCache.GetCachePath(url);
            if (DownloadFileCache.TryHit(cachePath, out var cachedFile, out var cachedSize) &&
                IsCacheArtifactValid(cachedFile, cacheValidator))
            {
                log?.Invoke($"命中下载缓存，直接从缓存复制（{cachedSize} 字节）");
                File.Copy(cachedFile, targetPath, true);
                onProgress?.Invoke(new DownloadProgressSnapshot(
                    100,
                    cachedSize,
                    cachedSize,
                    0,
                    SegmentPercents: null,
                    IsComplete: true));
                return;
            }

            if (File.Exists(cachePath) && cacheValidator != null)
            {
                // 通用 URL 缓存可能由旧版本写入过 HTML/错误页；发现调用方
                // 提供的归档校验不通过时立即淘汰，避免每次重试都命中同一坏缓存。
                TryDeleteFile(cachePath);
                log?.Invoke("下载缓存校验失败，已清理后重新下载");
            }

            await DownloadCoreWithRetryAsync(
                url,
                targetPath,
                threadCount,
                onProgress,
                cancellationToken,
                log);

            if (!IsCacheArtifactValid(targetPath, cacheValidator))
            {
                TryDeleteFile(targetPath);
                throw new InvalidDataException("下载结果未通过文件校验");
            }

            await DownloadFileCache.SaveAsync(cachePath, targetPath);
            log?.Invoke("下载完成，已写入下载缓存");
            return;
        }

        await DownloadCoreWithRetryAsync(
            url,
            targetPath,
            threadCount,
            onProgress,
            cancellationToken,
            log);
    }

    /// <summary>
    /// 对响应提前结束、连接重置和有限的 HTTP 服务端错误做短暂重试。
    /// CDN 偶发会返回完整 Content-Length 后中途断流；这类错误不应立刻
    /// 触发上层更换来源，也不应把半成品当作最终失败。断点元数据由底层
    /// DownloadSingle/DownloadMultiPart 保留，下一次尝试会继续或安全重启。
    /// </summary>
    private async Task DownloadCoreWithRetryAsync(
        string url,
        string targetPath,
        int threadCount,
        Action<DownloadProgressSnapshot>? onProgress,
        CancellationToken cancellationToken,
        Action<string>? log)
    {
        for (var attempt = 1; attempt <= TransientDownloadRetryAttempts; attempt++)
        {
            try
            {
                await DownloadCoreAsync(
                    url,
                    targetPath,
                    threadCount,
                    onProgress,
                    cancellationToken);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (
                attempt < TransientDownloadRetryAttempts &&
                IsTransientDownloadFailure(ex))
            {
                log?.Invoke(
                    $"下载响应中断，自动重试 {attempt}/{TransientDownloadRetryAttempts - 1}: {ex.Message}");
                await Task.Delay(
                    TimeSpan.FromMilliseconds(250 * attempt),
                    cancellationToken);
            }
        }

        // The final attempt either returned successfully or re-threw its original
        // exception, so this line is unreachable but keeps the method total for
        // the compiler and future changes to the loop.
        throw new InvalidOperationException("下载重试流程异常结束");
    }

    private static bool IsTransientDownloadFailure(Exception exception)
    {
        if (exception is EndOfStreamException or IOException)
        {
            return true;
        }

        if (exception is not HttpRequestException httpException)
        {
            return false;
        }

        // StatusCode == null means transport failure (connection reset, EOF, TLS
        // interruption, etc.). Retry only transient server responses when a status
        // code is available; deterministic authorization/not-found/range errors
        // should immediately return to the caller for source-specific handling.
        return httpException.StatusCode is null or
            HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;
    }

    private static bool IsCacheArtifactValid(string path, Func<string, bool>? cacheValidator)
    {
        if (cacheValidator == null)
        {
            return true;
        }

        try
        {
            return cacheValidator(path);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeDownloadUrl(string value)
    {
        var trimmed = value.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !uri.Host.Equals("edge.forgecdn.net", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        try
        {
            var builder = new UriBuilder(uri)
            {
                Host = "mediafilez.forgecdn.net"
            };
            return builder.Uri.AbsoluteUri;
        }
        catch
        {
            return uri.AbsoluteUri;
        }
    }

    private async Task DownloadCoreAsync(
        string url,
        string targetPath,
        int threadCount,
        Action<DownloadProgressSnapshot>? onProgress,
        CancellationToken cancellationToken)
    {
        var probe = await ProbeRangeSupportAsync(url, cancellationToken);

        if (!probe.SupportsRange || probe.TotalBytes <= 0 || threadCount == 1)
        {
            await DownloadSingleAsync(
                url,
                targetPath,
                probe.SupportsRange && probe.TotalBytes > 0,
                probe.TotalBytes,
                onProgress,
                cancellationToken);
            return;
        }

        await DownloadMultiPartAsync(url, targetPath, probe.TotalBytes, threadCount, onProgress, cancellationToken);
    }

    private async Task DownloadSingleAsync(
        string url,
        string targetPath,
        bool canResume,
        long totalBytes,
        Action<DownloadProgressSnapshot>? onProgress,
        CancellationToken cancellationToken)
    {
        var partPath = targetPath + ".part";
        var metaPath = partPath + ".json";

        // 断点续传：读取上次进度（校验 URL/总大小/分片布局/.part 文件）
        var startOffset = 0L;
        if (canResume)
        {
            var resumeMeta = TryLoadResumeMeta(metaPath, url, totalBytes, 1);
            if (resumeMeta?.SegmentDownloaded is { Length: 1 } &&
                File.Exists(partPath) &&
                new FileInfo(partPath).Length >= resumeMeta.SegmentDownloaded[0])
            {
                startOffset = Math.Clamp(resumeMeta.SegmentDownloaded[0], 0, totalBytes);
            }
        }

        // 上次已写完但尚未完成最终改名时，直接完成改名，避免请求 Range: total- 导致 416。
        if (startOffset >= totalBytes && totalBytes > 0 &&
            File.Exists(partPath) && new FileInfo(partPath).Length >= totalBytes)
        {
            TryDeleteFile(metaPath);
            File.Move(partPath, targetPath, true);
            onProgress?.Invoke(CreateSnapshot(
                totalBytes,
                totalBytes,
                0,
                totalBytes,
                completed: true));
            return;
        }

        var http = GetHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (startOffset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(startOffset, null);
        }

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var resumed = startOffset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        var downloaded = resumed ? startOffset : 0L;
        if (!resumed)
        {
            startOffset = 0;
        }

        var contentLength = response.Content.Headers.ContentLength ?? 0;
        if (resumed && contentLength > 0)
        {
            var expectedBytes = totalBytes - startOffset;
            var contentRange = response.Content.Headers.ContentRange;
            if (contentRange != null &&
                (contentRange.From != startOffset ||
                 (contentRange.Length.HasValue && contentRange.Length.Value != totalBytes) ||
                 (contentRange.To.HasValue && contentRange.To.Value != totalBytes - 1)))
            {
                throw new HttpRequestException("服务器返回的断点范围与请求不一致");
            }

            if (contentLength != expectedBytes)
            {
                throw new HttpRequestException("服务器返回的断点大小与请求不一致");
            }

            totalBytes = startOffset + contentLength;
        }
        else if (contentLength > 0)
        {
            totalBytes = contentLength;
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);

        var sessionStartBytes = downloaded;
        var sw = Stopwatch.StartNew();
        var lastReportMs = 0L;
        var lastMetaFlushMs = 0L;
        var completed = false;
        var buffer = new byte[1024 * 64];

        {
            await using var fileStream = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 1024 * 64, true);
            if (!resumed)
            {
                fileStream.SetLength(0);
            }
            fileStream.Position = downloaded;

            try
            {
                while (true)
                {
                    var readLength = buffer.Length;
                    if (totalBytes > 0)
                    {
                        var remaining = totalBytes - downloaded;
                        if (remaining <= 0)
                        {
                            break;
                        }

                        readLength = (int)Math.Min(readLength, remaining);
                    }

                    var read = await source.ReadAsync(
                        buffer.AsMemory(0, readLength),
                        cancellationToken);
                    if (read <= 0)
                    {
                        break;
                    }

                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    downloaded += read;

                    if (totalBytes > 0 && sw.ElapsedMilliseconds - lastReportMs >= 200)
                    {
                        lastReportMs = sw.ElapsedMilliseconds;
                        onProgress?.Invoke(CreateSnapshot(
                            downloaded,
                            totalBytes,
                            sw.Elapsed.TotalSeconds,
                            sessionStartBytes,
                            completed: false));
                    }

                    // 周期性持久化断点（约 1 秒一次）
                    if (canResume && totalBytes > 0 && sw.ElapsedMilliseconds - lastMetaFlushMs >= 1000)
                    {
                        lastMetaFlushMs = sw.ElapsedMilliseconds;
                        FlushResumeMeta(metaPath, url, totalBytes, 1, [downloaded]);
                    }
                }

                if (totalBytes > 0 && downloaded != totalBytes)
                {
                    throw new EndOfStreamException("服务器提前结束下载响应");
                }

                completed = true;
            }
            finally
            {
                if (completed)
                {
                    TryDeleteFile(metaPath);
                }
                else if (canResume && totalBytes > 0)
                {
                    FlushResumeMeta(metaPath, url, totalBytes, 1, [downloaded]);
                }
            }
        }

        File.Move(partPath, targetPath, true);
        var reportTotal = totalBytes > 0 ? totalBytes : downloaded;
        onProgress?.Invoke(CreateSnapshot(
            downloaded,
            reportTotal,
            sw.Elapsed.TotalSeconds,
            sessionStartBytes,
            completed: true));
    }

    private async Task DownloadMultiPartAsync(
        string url,
        string targetPath,
        long totalBytes,
        int threadCount,
        Action<DownloadProgressSnapshot>? onProgress,
        CancellationToken cancellationToken)
    {
        var partPath = targetPath + ".part";
        var metaPath = partPath + ".json";

        var segmentSize = totalBytes / threadCount;
        if (segmentSize <= 0)
        {
            await DownloadSingleAsync(url, targetPath, true, totalBytes, onProgress, cancellationToken);
            return;
        }

        // 计算分片区间
        var segmentRanges = new (long Start, long End)[threadCount];
        for (var i = 0; i < threadCount; i++)
        {
            var start = i * segmentSize;
            segmentRanges[i] = (start, i == threadCount - 1 ? totalBytes - 1 : start + segmentSize - 1);
        }

        // 断点续传：读取各分片已下载字节数（校验 URL/总大小/分片布局/.part 文件）
        var segDownloaded = new long[threadCount];
        var resumeMeta = TryLoadResumeMeta(metaPath, url, totalBytes, threadCount);
        if (resumeMeta != null &&
            File.Exists(partPath) &&
            new FileInfo(partPath).Length == totalBytes)
        {
            for (var i = 0; i < threadCount; i++)
            {
                var segLength = segmentRanges[i].End - segmentRanges[i].Start + 1;
                segDownloaded[i] = Math.Clamp(resumeMeta.SegmentDownloaded[i], 0, segLength);
            }
        }

        // 预分配文件长度（保留已下载内容）
        using (var fs = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Write, 1, true))
        {
            fs.SetLength(totalBytes);
        }

        var sessionStartBytes = segDownloaded.Sum();
        long totalDownloaded = sessionStartBytes;
        var sw = Stopwatch.StartNew();
        var lastReportMs = 0L;
        var lastMetaFlushMs = 0L;
        var resumeMetaLock = new object();
        var completed = false;

        try
        {
            var tasks = new List<Task>(threadCount);
            for (var i = 0; i < threadCount; i++)
            {
                var index = i;
                var range = segmentRanges[index];

                tasks.Add(Task.Run(async () =>
                {
                    var cursor = range.Start + segDownloaded[index];
                    if (cursor > range.End)
                    {
                        return; // 该分片已下载完成（断点续传跳过）
                    }

                    var http = GetHttpClient();
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Range = new RangeHeaderValue(cursor, range.End);

                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (response.StatusCode != HttpStatusCode.PartialContent)
                    {
                        throw new HttpRequestException($"服务器未返回分片响应: {(int)response.StatusCode} {response.StatusCode}");
                    }

                    var expectedBytes = range.End - cursor + 1;
                    var contentRange = response.Content.Headers.ContentRange;
                    if (contentRange != null &&
                        (contentRange.From != cursor || contentRange.To != range.End ||
                         (contentRange.Length.HasValue && contentRange.Length.Value != totalBytes)))
                    {
                        throw new HttpRequestException("服务器返回的分片范围与请求不一致");
                    }

                    if (response.Content.Headers.ContentLength is long contentLength &&
                        contentLength != expectedBytes)
                    {
                        throw new HttpRequestException("服务器返回的分片大小与请求不一致");
                    }

                    await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                    await using var destination = new FileStream(partPath, FileMode.Open, FileAccess.Write, FileShare.Write, 1024 * 32, true);
                    destination.Position = cursor;

                    var buffer = new byte[1024 * 32];
                    var receivedBytes = 0L;
                    while (receivedBytes < expectedBytes)
                    {
                        var remaining = expectedBytes - receivedBytes;
                        var read = await source.ReadAsync(
                            buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                            cancellationToken);
                        if (read <= 0)
                        {
                            throw new EndOfStreamException("服务器提前结束分片响应");
                        }

                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        receivedBytes += read;
                        segDownloaded[index] += read;
                        var current = Interlocked.Add(ref totalDownloaded, read);

                        // 报告限流：多线程共享一个 sw 和 lastReportMs
                        var elapsedMs = sw.ElapsedMilliseconds;
                        if (elapsedMs - Interlocked.Read(ref lastReportMs) >= 200)
                        {
                            Interlocked.Exchange(ref lastReportMs, elapsedMs);
                            onProgress?.Invoke(CreateSnapshot(
                                current,
                                totalBytes,
                                sw.Elapsed.TotalSeconds,
                                sessionStartBytes,
                                segDownloaded,
                                segmentRanges,
                                completed: false));
                        }

                        // 多线程下载不能等到 WhenAll 失败/取消后才写断点：进程被
                        // 直接终止时 finally 不一定有机会执行。按秒保存一次，且
                        // 只允许一个分片写入共享元数据，避免 JSON 互相覆盖。
                        var metaElapsedMs = sw.ElapsedMilliseconds;
                        var lastMetaMs = Interlocked.Read(ref lastMetaFlushMs);
                        if (metaElapsedMs - lastMetaMs >= 1000 &&
                            Interlocked.CompareExchange(
                                ref lastMetaFlushMs,
                                metaElapsedMs,
                                lastMetaMs) == lastMetaMs)
                        {
                            lock (resumeMetaLock)
                            {
                                FlushResumeMeta(
                                    metaPath,
                                    url,
                                    totalBytes,
                                    threadCount,
                                    segDownloaded);
                            }
                        }
                    }
                }, cancellationToken));
            }

            await Task.WhenAll(tasks);
            completed = true;
        }
        finally
        {
            // 成功清理断点元数据；失败/取消时持久化断点供重试续传
            if (completed)
            {
                TryDeleteFile(metaPath);
            }
            else
            {
                FlushResumeMeta(metaPath, url, totalBytes, threadCount, segDownloaded);
            }
        }

        File.Move(partPath, targetPath, true);
        onProgress?.Invoke(CreateSnapshot(
            totalBytes,
            totalBytes,
            sw.Elapsed.TotalSeconds,
            sessionStartBytes,
            segmentDownloaded: segmentRanges.Select(range => range.End - range.Start + 1).ToArray(),
            segmentRanges,
            completed: true));
    }

    private async Task<(bool SupportsRange, long TotalBytes)> ProbeRangeSupportAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var http = GetHttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 0);

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var contentRange = response.Content.Headers.ContentRange;
                if (contentRange?.HasLength == true && contentRange.Length.HasValue)
                {
                    return (true, contentRange.Length.Value);
                }

                var contentLength = response.Content.Headers.ContentLength;
                return (true, contentLength ?? 0);
            }

            var length = response.Content.Headers.ContentLength ?? 0;
            return (false, length);
        }
        catch (OperationCanceledException)
        {
            // Range 探测只是下载前置步骤，用户取消时不能静默降级为普通下载，
            // 否则会再发起一次已知会失败的请求并延迟任务进入取消态。
            throw;
        }
        catch
        {
            return (false, 0);
        }
    }

    private static DownloadProgressSnapshot CreateSnapshot(
        long downloadedBytes,
        long totalBytes,
        double elapsedSeconds,
        long sessionStartBytes,
        long[]? segmentDownloaded = null,
        (long Start, long End)[]? segmentRanges = null,
        bool completed = false)
    {
        // 速度按本次会话增量计算（断点续传时避免速度虚高）
        var sessionBytes = Math.Max(0, downloadedBytes - sessionStartBytes);
        var speed = elapsedSeconds <= 0 ? 0 : sessionBytes / elapsedSeconds;
        var percent = totalBytes > 0
            ? Math.Min(100, downloadedBytes * 100d / totalBytes)
            : 0;
        if (!completed && totalBytes > 0 && downloadedBytes >= totalBytes)
        {
            // 最后一个分片可能已经写满预分配文件，但其它分片任务尚未全部
            // 完成释放响应流；在 DownloadMultiPartAsync 返回前不能显示满格。
            percent = 99;
        }

        double[]? segmentPercents = null;
        if (segmentDownloaded != null && segmentRanges != null && segmentRanges.Length > 1)
        {
            segmentPercents = new double[segmentRanges.Length];
            for (var i = 0; i < segmentRanges.Length; i++)
            {
                // 每个分片在 UI 中占据一格，因此显示该分片自身的完成度；
                // 若按总文件大小计算，多个分片会重复缩小视觉进度。
                var segmentLength = segmentRanges[i].End - segmentRanges[i].Start + 1;
                var rawSegmentPercent = segmentLength > 0
                    ? Math.Min(100, segmentDownloaded[i] * 100d / segmentLength)
                    : 0;
                // 分块条使用整数百分比，与任务列表/详情中的总进度保持同一
                // 显示口径。只有 DownloadAsync 正常返回后的最终快照才允许 100。
                segmentPercents[i] = completed
                    ? rawSegmentPercent
                    : Math.Min(99, Math.Floor(rawSegmentPercent));
            }
        }

        return new DownloadProgressSnapshot(
            percent,
            downloadedBytes,
            totalBytes,
            speed,
            segmentPercents,
            completed);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore
        }
    }

    private sealed class DownloadMetaDto
    {
        public string Url { get; set; } = string.Empty;

        public long TotalBytes { get; set; }

        public int ThreadCount { get; set; }

        public long[] SegmentDownloaded { get; set; } = [];
    }

    /// <summary>加载断点续传元数据（URL/总大小/线程数需与当前下载一致才可续传）。</summary>
    private static DownloadMetaDto? TryLoadResumeMeta(string metaPath, string url, long totalBytes, int threadCount)
    {
        try
        {
            if (!File.Exists(metaPath))
            {
                return null;
            }

            var dto = JsonSerializer.Deserialize<DownloadMetaDto>(File.ReadAllText(metaPath));
            if (dto == null)
            {
                return null;
            }

            if (!string.Equals(dto.Url, url, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (dto.TotalBytes != totalBytes || dto.ThreadCount != threadCount)
            {
                return null;
            }

            if (dto.SegmentDownloaded == null || dto.SegmentDownloaded.Length != threadCount)
            {
                return null;
            }

            return dto;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>持久化断点续传元数据（失败/取消时调用）。</summary>
    private static void FlushResumeMeta(string metaPath, string url, long totalBytes, int threadCount, long[] segmentDownloaded)
    {
        try
        {
            var dto = new DownloadMetaDto
            {
                Url = url,
                TotalBytes = totalBytes,
                ThreadCount = threadCount,
                SegmentDownloaded = (long[])segmentDownloaded.Clone()
            };
            // 断点元数据会在多个分片下载线程运行期间周期性更新；原地覆盖时若进程
            // 被终止，下一次启动可能读到半截 JSON，进而丢失整个文件的可恢复进度。
            AtomicFileWriter.WriteUtf8(metaPath, JsonSerializer.Serialize(dto));
        }
        catch
        {
            // ignore
        }
    }

    private HttpClient GetHttpClient()
    {
        var settings = _settingsStore.Load();
        var signature = BuildProxySignature(settings);

        lock (_httpClientLock)
        {
            if (_httpClient != null && string.Equals(signature, _httpClientProxySignature, StringComparison.Ordinal))
            {
                return _httpClient;
            }

            _httpClient?.Dispose();
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            if (settings.EnableDownloadProxy &&
                TryResolveProxyUri(settings.DownloadProxyUrl, out var proxyUri))
            {
                var proxy = new WebProxy(proxyUri);
                if (!string.IsNullOrWhiteSpace(settings.DownloadProxyUserName))
                {
                    proxy.Credentials = new NetworkCredential(
                        settings.DownloadProxyUserName.Trim(),
                        settings.DownloadProxyPassword ?? string.Empty);
                }

                handler.UseProxy = true;
                handler.Proxy = proxy;
            }

            _httpClient = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromMinutes(60)
            };
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SVL-Avalonia/1.0");
            _httpClientProxySignature = signature;
            return _httpClient;
        }
    }

    private static string BuildProxySignature(AppUserSettings settings)
    {
        if (!settings.EnableDownloadProxy)
        {
            return "disabled";
        }

        return string.Join('|',
            "enabled",
            settings.DownloadProxyUrl?.Trim() ?? string.Empty,
            settings.DownloadProxyUserName?.Trim() ?? string.Empty,
            string.IsNullOrWhiteSpace(settings.DownloadProxyUserName)
                ? "anonymous"
                : "cred");
    }

    private static bool TryResolveProxyUri(string? rawUrl, out Uri uri)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            uri = null!;
            return false;
        }

        return Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out uri!) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}

/// <summary>
/// 下载文件缓存（按 URL 哈希键）。重复下载同一 URL 时直接复制本地缓存，免重复下载。
/// 对齐旧架构 SVL.Core.IO.DownloadCacheService 的能力，运行在 Avalonia 层。
/// </summary>
public static class DownloadFileCache
{
    public static string CacheDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SVL", "Avalonia", "cache", "downloads");

    public static string GetCachePath(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
        return Path.Combine(CacheDirectory, hash);
    }

    /// <summary>检查缓存命中（文件存在且非空）。</summary>
    public static bool TryHit(string cachePath, out string cachedFile, out long cachedSize)
    {
        cachedFile = cachePath;
        cachedSize = 0;
        try
        {
            if (File.Exists(cachePath))
            {
                var length = new FileInfo(cachePath).Length;
                if (length > 0)
                {
                    cachedSize = length;
                    return true;
                }
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    /// <summary>保存文件到缓存（写入失败静默忽略，不影响下载结果）。</summary>
    public static async Task SaveAsync(string cachePath, string sourceFilePath)
    {
        var tempPath = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
            {
                return;
            }

            Directory.CreateDirectory(CacheDirectory);
            tempPath = cachePath + ".tmp-" + Guid.NewGuid().ToString("N");
            await using (var source = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 64, true))
            await using (var destination = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 64, true))
            {
                await source.CopyToAsync(destination);
            }

            File.Move(tempPath, cachePath, true);
        }
        catch
        {
            // 缓存写入失败不影响下载结果
        }
        finally
        {
            try
            {
                // 只清理本次写入创建的临时文件，避免并发缓存写入互相删除。
                if (!string.IsNullOrWhiteSpace(tempPath) && File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // ignore orphaned temporary cache files
            }
        }
    }
}

public readonly record struct DownloadProgressSnapshot(
    double Percent,
    long DownloadedBytes,
    long TotalBytes,
    double BytesPerSecond,
    double[]? SegmentPercents = null,
    bool IsComplete = false);

/// <summary>
/// 将下载快照转换为进度条显示值。
/// 未达到总字节数时最高只显示 99%，避免浮点数/四舍五入让未完成任务提前填满。
/// </summary>
public static class DownloadProgressCalculator
{
    /// <summary>
    /// 根据下载快照计算显示进度。即使字节数已经达到总大小，只要底层下载调用
    /// 尚未正常返回，也保持 99%，避免最后一个分片回调提前填满进度条。
    /// </summary>
    public static int ToDisplayPercent(DownloadProgressSnapshot snapshot)
    {
        if (!snapshot.IsComplete &&
            snapshot.TotalBytes > 0 &&
            snapshot.DownloadedBytes >= snapshot.TotalBytes)
        {
            return 99;
        }

        return ToDisplayPercent(snapshot.Percent, snapshot.DownloadedBytes, snapshot.TotalBytes);
    }

    public static int ToDisplayPercent(double percent, long downloadedBytes, long totalBytes)
    {
        if (totalBytes <= 0)
        {
            return 0;
        }

        if (downloadedBytes >= totalBytes)
        {
            return 100;
        }

        if (!double.IsFinite(percent))
        {
            return 0;
        }

        // 进度条的权威数据是已写入字节数，而不是回调中可能被截断/滞后的
        // Percent 字段。这样文本、普通进度条和多线程分片条不会各算一遍。
        var actualPercent = totalBytes > 0
            ? downloadedBytes * 100d / totalBytes
            : percent;
        return Math.Clamp((int)Math.Floor(Math.Clamp(actualPercent, 0, 100)), 0, 99);
    }
}
