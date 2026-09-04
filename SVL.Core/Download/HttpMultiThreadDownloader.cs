using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using SVL.Core.Logging;

namespace SVL.Core.Download;

/// <summary>
/// 通用 HTTP 多线程分片下载器。
/// </summary>
public static class HttpMultiThreadDownloader
{
    private static readonly HttpClient HttpClient = CreateClient();

    public static async Task DownloadAsync(
        string url,
        string targetPath,
        int threadCount,
        Action<double, long, long, double>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("下载地址不能为空", nameof(url));

        if (string.IsNullOrWhiteSpace(targetPath))
            throw new ArgumentException("目标路径不能为空", nameof(targetPath));

        threadCount = Math.Max(1, Math.Min(16, threadCount));

        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var probe = await ProbeRangeSupportAsync(url, cancellationToken);

        if (!probe.SupportsRange || probe.TotalBytes <= 0 || threadCount == 1)
        {
            Log.Info($"[HttpMultiThreadDownloader] 使用单线程下载: SupportsRange={probe.SupportsRange}, Size={probe.TotalBytes}, Threads={threadCount}");
            await DownloadSingleAsync(url, targetPath, probe.TotalBytes, progressCallback, cancellationToken);
            return;
        }

        Log.Info($"[HttpMultiThreadDownloader] 使用多线程下载: Size={probe.TotalBytes}, Threads={threadCount}");
        await DownloadMultiPartAsync(url, targetPath, probe.TotalBytes, threadCount, progressCallback, cancellationToken);
    }

    private static async Task DownloadSingleAsync(
        string url,
        string targetPath,
        long totalBytes,
        Action<double, long, long, double>? progressCallback,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength ?? totalBytes;

        using var source = await response.Content.ReadAsStreamAsync();
        using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 64, true);

        var buffer = new byte[1024 * 64];
        long downloaded = 0;
        var sw = Stopwatch.StartNew();
        var lastReportTicks = 0L;

        while (true)
        {
            var read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
            if (read <= 0)
                break;

            await fileStream.WriteAsync(buffer, 0, read, cancellationToken);
            downloaded += read;

            if (contentLength > 0)
            {
                var nowTicks = sw.ElapsedMilliseconds;
                if (nowTicks - lastReportTicks >= 200)
                {
                    var speed = sw.Elapsed.TotalSeconds <= 0 ? 0 : downloaded / sw.Elapsed.TotalSeconds;
                    progressCallback?.Invoke(downloaded * 100.0 / contentLength, downloaded, contentLength, speed);
                    lastReportTicks = nowTicks;
                }
            }
        }

        var finalSpeed = sw.Elapsed.TotalSeconds <= 0 ? 0 : downloaded / sw.Elapsed.TotalSeconds;
        if (contentLength > 0)
        {
            if (downloaded != contentLength)
            {
                throw new EndOfStreamException($"服务器提前结束下载响应: {downloaded}/{contentLength} 字节");
            }

            progressCallback?.Invoke(100, downloaded, contentLength, finalSpeed);
        }
        else
        {
            progressCallback?.Invoke(100, downloaded, downloaded, finalSpeed);
        }
    }

    private static async Task DownloadMultiPartAsync(
        string url,
        string targetPath,
        long totalBytes,
        int threadCount,
        Action<double, long, long, double>? progressCallback,
        CancellationToken cancellationToken)
    {
        using (var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.Write, 1, true))
        {
            fs.SetLength(totalBytes);
        }

        var segmentSize = totalBytes / threadCount;
        if (segmentSize <= 0)
        {
            await DownloadSingleAsync(url, targetPath, totalBytes, progressCallback, cancellationToken);
            return;
        }

        var tasks = new List<Task>(threadCount);
        long downloaded = 0;
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < threadCount; i++)
        {
            var start = i * segmentSize;
            var end = (i == threadCount - 1) ? totalBytes - 1 : start + segmentSize - 1;

            tasks.Add(Task.Run(async () =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(start, end);

                using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.StatusCode != HttpStatusCode.PartialContent)
                {
                    throw new HttpRequestException($"服务器未返回分片响应: {(int)response.StatusCode} {response.StatusCode}");
                }

                var expectedBytes = end - start + 1;
                var contentRange = response.Content.Headers.ContentRange;
                if (contentRange != null &&
                    (contentRange.From != start || contentRange.To != end ||
                     (contentRange.Length.HasValue && contentRange.Length.Value != totalBytes)))
                {
                    throw new HttpRequestException("服务器返回的分片范围与请求不一致");
                }

                if (response.Content.Headers.ContentLength is long contentLength &&
                    contentLength != expectedBytes)
                {
                    throw new HttpRequestException("服务器返回的分片大小与请求不一致");
                }

                using var source = await response.Content.ReadAsStreamAsync();
                using var destination = new FileStream(targetPath, FileMode.Open, FileAccess.Write, FileShare.Write, 1024 * 32, true);
                destination.Position = start;

                var buffer = new byte[1024 * 32];
                var receivedBytes = 0L;
                while (receivedBytes < expectedBytes)
                {
                    var remaining = expectedBytes - receivedBytes;
                    var read = await source.ReadAsync(
                        buffer,
                        0,
                        (int)Math.Min(buffer.Length, remaining),
                        cancellationToken);
                    if (read <= 0)
                    {
                        throw new EndOfStreamException("服务器提前结束分片响应");
                    }

                    await destination.WriteAsync(buffer, 0, read, cancellationToken);
                    receivedBytes += read;
                    var current = Interlocked.Add(ref downloaded, read);

                    var speed = sw.Elapsed.TotalSeconds <= 0 ? 0 : current / sw.Elapsed.TotalSeconds;
                    progressCallback?.Invoke(current * 100.0 / totalBytes, current, totalBytes, speed);
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks);

        var finalSpeed = sw.Elapsed.TotalSeconds <= 0 ? 0 : downloaded / sw.Elapsed.TotalSeconds;
        progressCallback?.Invoke(100, totalBytes, totalBytes, finalSpeed);
    }

    private static async Task<(bool SupportsRange, long TotalBytes)> ProbeRangeSupportAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 0);

            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
        catch (Exception ex)
        {
            Log.Warn($"[HttpMultiThreadDownloader] 探测分片下载能力失败: {ex.Message}");
            return (false, 0);
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(60)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("SVL-StardewValleyLauncher/1.0");
        return client;
    }
}
