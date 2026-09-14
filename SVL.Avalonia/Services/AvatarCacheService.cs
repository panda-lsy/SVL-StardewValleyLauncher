using System.Net.Http;

namespace SVL.Avalonia.Services;

/// <summary>
/// Nexus OAuth 头像缓存。
///
/// 缓存目录和文件命名沿用旧 WPF 实现，确保迁移后可以直接显示已有头像，
/// 同时避免在 Avalonia 的 Image 转换器中同步访问网络。
/// </summary>
public static class AvatarCacheService
{
    private static readonly HttpClient HttpClient = new();
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SVL",
        "cache",
        "avatars");

    /// <summary>按旧 WPF 规则计算指定用户的缓存路径。</summary>
    public static string GetCachePath(string? userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return string.Empty;
        }

        var safeFileName = string.Join("_", userName.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(Root, $"{safeFileName}.png");
    }

    /// <summary>返回仍在有效期内的头像缓存，不触发网络请求。</summary>
    public static string? GetCachedAvatar(string? userName)
    {
        var path = GetCachePath(userName);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            if (age >= TimeSpan.Zero && age >= CacheLifetime)
            {
                File.Delete(path);
                return null;
            }

            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 下载并原子写入头像缓存。仅接受 HTTP(S) 地址，避免把 OAuth claim 当作本地路径处理。
    /// </summary>
    public static async Task<string?> DownloadAndCacheAvatarAsync(
        string? avatarUrl,
        string? userName,
        CancellationToken cancellationToken = default)
    {
        var cachePath = GetCachePath(userName);
        if (string.IsNullOrWhiteSpace(cachePath) ||
            !Uri.TryCreate(avatarUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var cached = GetCachedAvatar(userName);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        try
        {
            using var response = await HttpClient.GetAsync(uri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length == 0 || bytes.Length > 10 * 1024 * 1024)
            {
                return null;
            }

            Directory.CreateDirectory(Root);
            var temporaryPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
                File.Move(temporaryPath, cachePath, overwrite: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
                catch
                {
                    // 缓存清理失败不应影响已经完成的登录流程。
                }
            }

            return cachePath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
