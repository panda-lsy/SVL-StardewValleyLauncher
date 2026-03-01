using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using SVL.Core.Logging;

namespace SVL.Core.IO;

/// <summary>
/// 头像缓存服务
/// </summary>
public static class AvatarCacheService
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SVL",
        "cache",
        "avatars"
    );

    private static readonly HttpClient _httpClient = new();

    static AvatarCacheService()
    {
        // 确保缓存目录存在
        if (!Directory.Exists(CacheDir))
        {
            Directory.CreateDirectory(CacheDir);
        }
    }

    /// <summary>
    /// 获取头像缓存路径
    /// </summary>
    public static string GetAvatarCachePath(string userName)
    {
        // 使用用户名作为文件名（避免特殊字符）
        var safeFileName = string.Join("_", userName.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(CacheDir, $"{safeFileName}.png");
    }

    /// <summary>
    /// 下载并缓存头像
    /// </summary>
    public static async Task<string?> DownloadAndCacheAvatarAsync(string avatarUrl, string userName)
    {
        if (string.IsNullOrEmpty(avatarUrl))
            return null;

        try
        {
            var cachePath = GetAvatarCachePath(userName);

            // 检查缓存是否已存在且有效（7天内）
            if (File.Exists(cachePath))
            {
                var fileInfo = new FileInfo(cachePath);
                var age = DateTime.Now - fileInfo.LastWriteTime;

                // 缓存7天有效
                if (age.TotalDays < 7)
                {
                    Log.Info($"[AvatarCache] 使用缓存的头像: {userName}");
                    return cachePath;
                }
                else
                {
                    // 缓存过期，删除旧文件
                    File.Delete(cachePath);
                    Log.Info($"[AvatarCache] 删除过期的头像缓存: {userName}");
                }
            }

            // 下载头像
            Log.Info($"[AvatarCache] 下载头像: {userName} - {avatarUrl}");

            using (var response = await _httpClient.GetAsync(avatarUrl))
            {
                if (!response.IsSuccessStatusCode)
                {
                    Log.Warn($"[AvatarCache] 下载头像失败: {response.StatusCode}");
                    return null;
                }

                var imageData = await response.Content.ReadAsByteArrayAsync();

                // 保存到缓存
                File.WriteAllBytes(cachePath, imageData);

                Log.Info($"[AvatarCache] 头像已缓存: {cachePath}");
                return cachePath;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[AvatarCache] 下载头像失败: {userName}", ex);
            return null;
        }
    }

    /// <summary>
    /// 获取缓存的头像路径
    /// </summary>
    public static string? GetCachedAvatar(string userName)
    {
        var cachePath = GetAvatarCachePath(userName);

        if (File.Exists(cachePath))
        {
            var fileInfo = new FileInfo(cachePath);
            var age = DateTime.Now - fileInfo.LastWriteTime;

            // 缓存7天有效
            if (age.TotalDays < 7)
            {
                return cachePath;
            }
            else
            {
                // 缓存过期，删除旧文件
                try
                {
                    File.Delete(cachePath);
                    Log.Info($"[AvatarCache] 删除过期的头像缓存: {userName}");
                }
                catch (Exception ex)
                {
                    Log.Warn("[AvatarCache] 删除过期缓存失败", ex);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 清除所有头像缓存
    /// </summary>
    public static void ClearAllCache()
    {
        try
        {
            if (Directory.Exists(CacheDir))
            {
                var files = Directory.GetFiles(CacheDir);
                foreach (var file in files)
                {
                    File.Delete(file);
                }
                Log.Info($"[AvatarCache] 已清除 {files.Length} 个头像缓存");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[AvatarCache] 清除缓存失败");
        }
    }
}
