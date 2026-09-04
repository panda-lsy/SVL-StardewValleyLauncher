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
        Action<string>? log = null)
    {
        return DownloadAsync(url, targetPath, 0, onProgress, cancellationToken, log);
    }

    /// <summary>下载文件（显式指定线程数；threadCount &lt;= 0 时读取设置项）。</summary>
    public async Task DownloadAsync(
        string url,
        string targetPath,
        int threadCount,
        Action<DownloadProgressSnapshot>? onProgress,
        CancellationToken cancellationToken = default,
        Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("下载地址不能为空", nameof(url));
        }

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new ArgumentException("目标文件路径不能为空", nameof(targetPath));
        }

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
            if (DownloadFileCache.TryHit(cachePath, out var cachedFile, out var cachedSize))
            {
                log?.Invoke($"命中下载缓存，直接从缓存复制（{cachedSize} 字节）");
                File.Copy(cachedFile, targetPath, true);
                onProgress?.Invoke(new DownloadProgressSnapshot(100, cachedSize, cachedSize, 0));
                return;
            }

            await DownloadCoreAsync(url, targetPath, threadCount, onProgress, cancellationToken);
            await DownloadFileCache.SaveAsync(cachePath, targetPath);
            log?.Invoke("下载完成，已写入下载缓存");
            return;
        }

        await DownloadCoreAsync(url, targetPath, threadCount, onProgress, cancellationToken);
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
            onProgress?.Invoke(CreateSnapshot(totalBytes, totalBytes, 0, totalBytes));
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
                        onProgress?.Invoke(CreateSnapshot(downloaded, totalBytes, sw.Elapsed.TotalSeconds, sessionStartBytes));
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
        onProgress?.Invoke(CreateSnapshot(downloaded, reportTotal, sw.Elapsed.TotalSeconds, sessionStartBytes));
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
                                current, totalBytes, sw.Elapsed.TotalSeconds, sessionStartBytes, segDownloaded, segmentRanges));
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
        onProgress?.Invoke(CreateSnapshot(totalBytes, totalBytes, sw.Elapsed.TotalSeconds, sessionStartBytes));
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
        (long Start, long End)[]? segmentRanges = null)
    {
        // 速度按本次会话增量计算（断点续传时避免速度虚高）
        var sessionBytes = Math.Max(0, downloadedBytes - sessionStartBytes);
        var speed = elapsedSeconds <= 0 ? 0 : sessionBytes / elapsedSeconds;
        var percent = totalBytes > 0
            ? Math.Min(100, downloadedBytes * 100d / totalBytes)
            : 0;

        double[]? segmentPercents = null;
        if (segmentDownloaded != null && segmentRanges != null && segmentRanges.Length > 1)
        {
            segmentPercents = new double[segmentRanges.Length];
            for (var i = 0; i < segmentRanges.Length; i++)
            {
                // 每个分片在 UI 中占据一格，因此显示该分片自身的完成度；
                // 若按总文件大小计算，多个分片会重复缩小视觉进度。
                var segmentLength = segmentRanges[i].End - segmentRanges[i].Start + 1;
                segmentPercents[i] = segmentLength > 0
                    ? Math.Min(100, segmentDownloaded[i] * 100d / segmentLength)
                    : 0;
            }
        }

        return new DownloadProgressSnapshot(percent, downloadedBytes, totalBytes, speed, segmentPercents);
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
            File.WriteAllText(metaPath, JsonSerializer.Serialize(dto));
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
    double[]? SegmentPercents = null);
