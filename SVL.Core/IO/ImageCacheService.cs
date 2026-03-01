using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using SVL.Core.Logging;

namespace SVL.Core.IO;

/// <summary>
/// 图片缓存服务
/// </summary>
public static class ImageCacheService
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SVL",
        "cache",
        "images"
    );

    private static readonly HttpClient HttpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    static ImageCacheService()
    {
        try
        {
            // 确保缓存目录存在
            if (!Directory.Exists(CacheDir))
            {
                Directory.CreateDirectory(CacheDir);
                Log.Info($"[ImageCache] 创建缓存目录: {CacheDir}");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[ImageCache] 初始化缓存目录失败");
        }
    }

    /// <summary>
    /// 获取缓存图片路径
    /// </summary>
    /// <param name="imageUrl">图片URL</param>
    /// <returns>本地缓存文件路径，如果缓存不存在则返回null</returns>
    public static string? GetCachedImagePath(string imageUrl)
    {
        if (string.IsNullOrEmpty(imageUrl))
            return null;

        try
        {
            var fileName = GetCacheFileName(imageUrl);
            var cachedPath = Path.Combine(CacheDir, fileName);

            if (File.Exists(cachedPath))
            {
                // 缓存命中，不输出日志以减少日志量
                return cachedPath;
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Warn("[ImageCache] 检查缓存失败", ex);
            return null;
        }
    }

    /// <summary>
    /// 下载并缓存图片
    /// </summary>
    /// <param name="imageUrl">图片URL</param>
    /// <returns>本地缓存文件路径，下载失败返回null</returns>
    public static async Task<string?> DownloadAndCacheImageAsync(string imageUrl)
    {
        if (string.IsNullOrEmpty(imageUrl))
            return null;

        try
        {
            var fileName = GetCacheFileName(imageUrl);
            var cachedPath = Path.Combine(CacheDir, fileName);

            // 如果已经存在，直接返回
            if (File.Exists(cachedPath))
            {
                return cachedPath;
            }

            Log.Info($"[ImageCache] 下载图片: {imageUrl}");

            // 下载图片
            var response = await HttpClient.GetAsync(imageUrl);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsByteArrayAsync();

            // 保存到缓存
            File.WriteAllBytes(cachedPath, content);

            Log.Info($"[ImageCache] 图片已缓存: {cachedPath}");
            return cachedPath;
        }
        catch (Exception ex)
        {
            Log.Warn($"[ImageCache] 下载图片失败: {imageUrl}", ex);
            return null;
        }
    }

    /// <summary>
    /// 清除所有图片缓存
    /// </summary>
    /// <returns>清除的文件数量</returns>
    public static int ClearCache()
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
                    Log.Warn($"[ImageCache] 删除缓存文件失败: {file}", ex);
                }
            }

            Log.Info($"[ImageCache] 已清除 {count} 个缓存文件");
            return count;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[ImageCache] 清除缓存失败");
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
                Log.Info($"[ImageCache] 已清理过期缓存 {removed} 个文件 (retention={retention.TotalMinutes}min)");
            return removed;
        }
        catch (Exception ex)
        {
            Log.Warn("[ImageCache] 清理过期缓存失败", ex);
            return 0;
        }
    }

    /// <summary>
    /// 获取缓存大小（字节）
    /// </summary>
    /// <returns>缓存大小（字节）</returns>
    public static long GetCacheSize()
    {
        try
        {
            if (!Directory.Exists(CacheDir))
                return 0;

            var files = Directory.GetFiles(CacheDir, "*.*", SearchOption.AllDirectories);
            long totalSize = 0;

            foreach (var file in files)
            {
                try
                {
                    var fileInfo = new FileInfo(file);
                    totalSize += fileInfo.Length;
                }
                catch (Exception ex)
                {
                    Log.Warn($"[ImageCache] 获取文件大小失败: {file}", ex);
                }
            }

            return totalSize;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[ImageCache] 获取缓存大小失败");
            return 0;
        }
    }

    /// <summary>
    /// 获取缓存大小（格式化的字符串）
    /// </summary>
    /// <returns>格式化的缓存大小（如 "10.5 MB"）</returns>
    public static string GetFormattedCacheSize()
    {
        var bytes = GetCacheSize();
        return FormatBytes(bytes);
    }

    /// <summary>
    /// 格式化字节数
    /// </summary>
    /// <param name="bytes">字节数</param>
    /// <returns>格式化的字符串</returns>
    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;

        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }

        return $"{len:0.##} {sizes[order]}";
    }

    /// <summary>
    /// 根据URL生成缓存文件名
    /// </summary>
    /// <param name="url">图片URL</param>
    /// <returns>缓存文件名</returns>
    private static string GetCacheFileName(string url)
    {
        // 使用URL的哈希值作为文件名，避免文件名过长或包含非法字符
        using var hash = System.Security.Cryptography.SHA256.Create();
        var hashBytes = hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(url));
        var hashString = BitConverter.ToString(hashBytes).Replace("-", "").ToLower();

        // 保留原始扩展名
        var uri = new Uri(url);
        var extension = Path.GetExtension(uri.AbsolutePath);
        if (string.IsNullOrEmpty(extension))
            extension = ".jpg";

        return $"{hashString}{extension}";
    }
}
