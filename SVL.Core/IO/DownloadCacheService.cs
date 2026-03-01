using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SVL.Core.Logging;
using SVL.Core.Download;

namespace SVL.Core.IO;

/// <summary>
/// 下载文件缓存服务（用于缓存 SMAPI、Mod 等 ZIP 文件）
/// </summary>
public static class DownloadCacheService
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SVL",
        "cache",
        "downloads"
    );

    static DownloadCacheService()
    {
        try
        {
            if (!Directory.Exists(CacheDir))
            {
                Directory.CreateDirectory(CacheDir);
                Log.Info($"[DownloadCache] 创建缓存目录: {CacheDir}");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DownloadCache] 初始化缓存目录失败");
        }
    }

    /// <summary>
    /// 获取缓存文件路径（如果缓存不存在则返回 null）
    /// </summary>
    /// <param name="key">缓存键（如："smapi-4.1.10", "mod-898372-12345"）</param>
    /// <param name="minFileSize">最小文件大小（字节），用于验证缓存完整性</param>
    /// <returns>缓存文件路径，如果缓存不存在或无效则返回 null</returns>
    public static string? GetCachedFile(string key, long minFileSize = 1024)
    {
        if (string.IsNullOrEmpty(key))
            return null;

        try
        {
            var fileName = GetCacheFileName(key);
            var cachedPath = Path.Combine(CacheDir, fileName);

            if (File.Exists(cachedPath))
            {
                var fileInfo = new FileInfo(cachedPath);

                // 验证文件大小
                if (fileInfo.Length >= minFileSize)
                {
                    Log.Debug($"[DownloadCache] 缓存命中: {key} ({fileInfo.Length} 字节)");
                    return cachedPath;
                }
                else
                {
                    Log.Warn($"[DownloadCache] 缓存文件不完整: {key} ({fileInfo.Length} 字节，要求至少 {minFileSize} 字节)");
                    // 删除不完整的缓存
                    try
                    {
                        File.Delete(cachedPath);
                    }
                    catch { }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"[DownloadCache] 检查缓存失败: {key}", ex);
            return null;
        }
    }

    /// <summary>
    /// 保存文件到缓存
    /// </summary>
    /// <param name="key">缓存键</param>
    /// <param name="sourceFilePath">源文件路径</param>
    /// <returns>缓存文件路径</returns>
    public static async Task<string> SaveToCacheAsync(string key, string sourceFilePath)
    {
        try
        {
            if (!File.Exists(sourceFilePath))
            {
                throw new FileNotFoundException($"源文件不存在: {sourceFilePath}");
            }

            var fileName = GetCacheFileName(key);
            var cachedPath = Path.Combine(CacheDir, fileName);

            // 如果目标文件已存在，先删除
            if (File.Exists(cachedPath))
            {
                File.Delete(cachedPath);
            }

            // 复制文件到缓存目录
            using (var sourceStream = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var destStream = new FileStream(cachedPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await sourceStream.CopyToAsync(destStream);
            }

            Log.Info($"[DownloadCache] 已缓存: {key} -> {cachedPath}");
            return cachedPath;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[DownloadCache] 保存缓存失败: {key}");
            throw;
        }
    }

    /// <summary>
    /// 从 URL 下载并缓存文件
    /// </summary>
    /// <param name="key">缓存键</param>
    /// <param name="url">下载 URL</param>
    /// <param name="progressCallback">进度回调（0.0 - 1.0）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>缓存文件路径</returns>
    public static async Task<string> DownloadAndCacheAsync(
        string key,
        string url,
        Action<double>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // 先检查缓存
            var cached = GetCachedFile(key);
            if (cached != null)
            {
                progressCallback?.Invoke(1.0);
                return cached;
            }

            var fileName = GetCacheFileName(key);
            var cachedPath = Path.Combine(CacheDir, fileName);

            Log.Info($"[DownloadCache] 开始下载: {key} from {url}");

            using var httpClient = new System.Net.Http.HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "SVL-StardewValleyLauncher/1.0");

            // 如果是 Curseforge URL，添加 API Key（与 ModDownloadTask 保持一致）
            if (url.Contains("curseforge.com"))
            {
                var apiKey = CurseforgeApiService.GetApiKey();
                if (!string.IsNullOrEmpty(apiKey))
                {
                    httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
                    Log.Info("[DownloadCache] 已添加 Curseforge API Key 到下载请求");
                }
                else
                {
                    Log.Warn("[DownloadCache] Curseforge API Key 未配置，下载可能失败");
                }
            }

            httpClient.Timeout = TimeSpan.FromMinutes(30);

            var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? 0;

            using var fs = new FileStream(cachedPath, FileMode.Create);
            using var stream = await response.Content.ReadAsStreamAsync();

            var buffer = new byte[8192];
            int bytesRead;
            long totalRead = 0;
            double lastProgress = 0;

            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await fs.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                totalRead += bytesRead;

                if (totalBytes > 0)
                {
                    var currentProgress = (double)totalRead / totalBytes;
                    // 每隔 5% 更新一次进度
                    if (currentProgress - lastProgress >= 0.05)
                    {
                        progressCallback?.Invoke(currentProgress);
                        lastProgress = currentProgress;
                    }
                }
            }

            progressCallback?.Invoke(1.0);

            Log.Info($"[DownloadCache] 下载完成: {key} ({totalRead} 字节)");
            return cachedPath;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[DownloadCache] 下载失败: {key}");

            // 清理部分下载的文件
            var fileName = GetCacheFileName(key);
            var cachedPath = Path.Combine(CacheDir, fileName);
            if (File.Exists(cachedPath))
            {
                try
                {
                    File.Delete(cachedPath);
                }
                catch { }
            }

            throw;
        }
    }

    /// <summary>
    /// 清除指定缓存
    /// </summary>
    public static bool ClearCache(string key)
    {
        try
        {
            var fileName = GetCacheFileName(key);
            var cachedPath = Path.Combine(CacheDir, fileName);

            if (File.Exists(cachedPath))
            {
                File.Delete(cachedPath);
                Log.Info($"[DownloadCache] 已清除缓存: {key}");
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"[DownloadCache] 清除缓存失败: {key}", ex);
            return false;
        }
    }

    /// <summary>
    /// 清除所有缓存
    /// </summary>
    /// <returns>清除的文件数量</returns>
    public static int ClearAllCache()
    {
        try
        {
            if (!Directory.Exists(CacheDir))
                return 0;

            var files = Directory.GetFiles(CacheDir);
            var count = 0;

            foreach (var file in files)
            {
                try
                {
                    File.Delete(file);
                    count++;
                }
                catch (Exception ex)
                {
                    Log.Warn($"[DownloadCache] 删除缓存文件失败: {file}", ex);
                }
            }

            Log.Info($"[DownloadCache] 已清除 {count} 个缓存文件");
            return count;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DownloadCache] 清除所有缓存失败");
            return 0;
        }
    }

    /// <summary>
    /// 清理过期缓存（按最后写入时间）
    /// </summary>
    public static int CleanupExpired(TimeSpan retention)
    {
        try
        {
            if (retention <= TimeSpan.Zero)
                return 0;
            if (!Directory.Exists(CacheDir))
                return 0;

            var nowUtc = DateTime.UtcNow;
            var removed = 0;

            foreach (var file in Directory.GetFiles(CacheDir, "*.*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var lastWriteUtc = File.GetLastWriteTimeUtc(file);
                    if ((nowUtc - lastWriteUtc) > retention)
                    {
                        File.Delete(file);
                        removed++;
                    }
                }
                catch
                {
                    // ignore
                }
            }

            if (removed > 0)
            {
                Log.Info($"[DownloadCache] 已清理过期缓存 {removed} 个文件 (retention={retention.TotalDays}天)");
            }

            return removed;
        }
        catch (Exception ex)
        {
            Log.Warn("[DownloadCache] 清理过期缓存失败", ex);
            return 0;
        }
    }

    /// <summary>
    /// 获取缓存大小（字节）
    /// </summary>
    public static long GetCacheSize()
    {
        try
        {
            if (!Directory.Exists(CacheDir))
                return 0;

            var files = Directory.GetFiles(CacheDir, "*.*", SearchOption.TopDirectoryOnly);
            long totalSize = 0;

            foreach (var file in files)
            {
                try
                {
                    totalSize += new FileInfo(file).Length;
                }
                catch { }
            }

            return totalSize;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 获取缓存文件数量
    /// </summary>
    public static int GetCacheCount()
    {
        try
        {
            if (!Directory.Exists(CacheDir))
                return 0;

            return Directory.GetFiles(CacheDir, "*.*", SearchOption.TopDirectoryOnly).Length;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// <summary>
    /// 获取缓存文件路径
    /// </summary>
    public static string GetCacheFilePath(string key)
    {
        var fileName = GetCacheFileName(key);
        return Path.Combine(CacheDir, fileName);
    }

    /// <summary>
    /// 生成缓存文件名
    /// </summary>
    private static string GetCacheFileName(string key)
    {
        // 使用键的哈希值作为文件名，避免文件名过长或包含非法字符
        using var hash = SHA256.Create();
        var hashBytes = hash.ComputeHash(Encoding.UTF8.GetBytes(key));
        var hashString = BitConverter.ToString(hashBytes).Replace("-", "").ToLower();

        // 保留 .zip 扩展名
        return $"{hashString}.zip";
    }

    /// <summary>
    /// 生成缓存键
    /// </summary>
    public static string GenerateCacheKey(string type, string identifier, string? version = null)
    {
        if (!string.IsNullOrEmpty(version))
        {
            return $"{type}-{identifier}-{version}";
        }
        return $"{type}-{identifier}";
    }
}
