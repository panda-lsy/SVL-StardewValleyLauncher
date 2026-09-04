using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using AnimatedImage.Avalonia;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SVL.Avalonia.Converters;

public sealed class AssetImageConverter : IValueConverter
{
    // In-memory bitmap cache: avoids re-decoding the same image every time the view is
    // recreated (e.g. switching pages). WeakReference allows GC to reclaim bitmaps under
    // memory pressure while keeping hot icons alive across page switches.
    private static readonly ConcurrentDictionary<string, WeakReference<Bitmap?>> BitmapCache = new();

    // Shared on-disk icon cache directory. The ViewModel downloads remote icons here and
    // updates IconSource with the local path; the converter also reads from this directory
    // so cached icons render instantly even before the ViewModel's async resolver runs.
    public static readonly string IconCacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SVL",
        "Avalonia",
        "smapi-icon-cache");

    /// <summary>
    /// Computes the local cache file path for a remote icon URL. The hash is computed over
    /// the Uri-normalized form of the URL so the converter and the ViewModel always agree
    /// on the cache location for the same remote icon.
    /// </summary>
    public static string GetIconCachePath(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl) ||
            !Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        var normalizedUrl = uri.ToString();
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(normalizedUrl));
        var hash = System.Convert.ToHexString(hashBytes).ToLowerInvariant();

        var extension = Path.GetExtension(uri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 8)
        {
            extension = ".img";
        }

        return Path.Combine(IconCacheDirectory, hash + extension);
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Bitmap bitmap)
        {
            return bitmap;
        }

        if (value is not string path || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (path.StartsWith("//", StringComparison.Ordinal))
        {
            path = "https:" + path;
        }

        var normalizedPath = StripQueryAndFragment(path);

        if (normalizedPath.StartsWith("//", StringComparison.Ordinal))
        {
            normalizedPath = "https:" + normalizedPath;
        }

        // 当 ConverterParameter=animate 时，仅对 GIF 文件返回 AnimatedImageSourceUri，
        // 让 ImageBehavior.AnimatedSource 接管动画播放。非 GIF 返回 null，不影响 Source 显示。
        var allowAnimated = parameter is string paramStr &&
                            paramStr.Equals("animate", StringComparison.OrdinalIgnoreCase);
        if (allowAnimated)
        {
            if (!IsGifPath(normalizedPath))
            {
                return null;
            }

            try
            {
                if (File.Exists(normalizedPath))
                {
                    return new AnimatedImageSourceUri(new Uri(normalizedPath));
                }

                if (Uri.TryCreate(path, UriKind.Absolute, out var animatedUri))
                {
                    return new AnimatedImageSourceUri(animatedUri);
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        try
        {
            if (File.Exists(normalizedPath))
            {
                // 本地文件缓存 key 包含 LastWriteTime，确保文件被覆写后缓存自动失效
                // 否则 ChangeIcon/TryWriteDefaultSmapiIcon 覆写 .svl-instance-icon.png 后
                // 仍命中旧 Bitmap，导致图标不刷新（?v=ticks 被 StripQueryAndFragment 去掉）
                var cacheKey = BuildLocalFileCacheKey(normalizedPath);
                return LoadCached(cacheKey, () => new Bitmap(normalizedPath));
            }

            if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
            {
                if (uri.IsFile)
                {
                    var localPath = Uri.UnescapeDataString(uri.LocalPath);
                    if (File.Exists(localPath))
                    {
                        return LoadCached(BuildLocalFileCacheKey(localPath), () => new Bitmap(localPath));
                    }

                    return null;
                }

                if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                {
                    // Do NOT block the UI thread on remote HTTP downloads. Instead, serve the
                    // icon from the shared on-disk cache if the ViewModel has already fetched
                    // it. If not cached yet, return null; the ViewModel's async icon resolver
                    // (ResolveRemoteIconToLocalAsync) will download the icon, update IconSource
                    // to the local cache path, and re-trigger this converter with that path.
                    var cachePath = GetIconCachePath(path);
                    if (!string.IsNullOrEmpty(cachePath) && File.Exists(cachePath))
                    {
                        return LoadCached(BuildLocalFileCacheKey(cachePath), () => new Bitmap(cachePath));
                    }

                    return null;
                }

                return LoadCached(uri.ToString(), () =>
                {
                    using var stream = AssetLoader.Open(uri);
                    return new Bitmap(stream);
                });
            }

            if (Uri.TryCreate(normalizedPath, UriKind.Absolute, out var normalizedUri))
            {
                if (normalizedUri.IsFile)
                {
                    var localPath = Uri.UnescapeDataString(normalizedUri.LocalPath);
                    if (File.Exists(localPath))
                    {
                        return LoadCached(BuildLocalFileCacheKey(localPath), () => new Bitmap(localPath));
                    }

                    return null;
                }

                if (normalizedUri.Scheme == Uri.UriSchemeHttp || normalizedUri.Scheme == Uri.UriSchemeHttps)
                {
                    // Same disk-cache fast path as above. Use the original (non-stripped) URL
                    // so the computed cache path matches what the ViewModel hashed.
                    var cachePath = GetIconCachePath(path);
                    if (!string.IsNullOrEmpty(cachePath) && File.Exists(cachePath))
                    {
                        return LoadCached(cachePath, () => new Bitmap(cachePath));
                    }

                    return null;
                }

                return LoadCached(normalizedUri.ToString(), () =>
                {
                    using var stream = AssetLoader.Open(normalizedUri);
                    return new Bitmap(stream);
                });
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns a cached bitmap for the given key, or loads and caches a new one.
    /// This prevents re-decoding the same image every time the view is recreated
    /// (e.g. when switching pages), which is the main cause of multi-second lag.
    /// </summary>
    private static Bitmap? LoadCached(string key, Func<Bitmap> load)
    {
        if (BitmapCache.TryGetValue(key, out var weakRef) &&
            weakRef.TryGetTarget(out var cached) && cached != null)
        {
            return cached;
        }

        Bitmap? bitmap;
        try
        {
            bitmap = load();
        }
        catch
        {
            return null;
        }

        BitmapCache[key] = new WeakReference<Bitmap?>(bitmap);
        return bitmap;
    }

    private static string StripQueryAndFragment(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var index = path.IndexOfAny(['?', '#']);
        return index < 0 ? path : path[..index];
    }

    /// <summary>
    /// 为本地文件构建包含 LastWriteTime 的缓存 key。
    /// 文件被覆写后 LastWriteTime 变化，缓存自动失效，确保图标及时刷新。
    /// </summary>
    private static string BuildLocalFileCacheKey(string localPath)
    {
        try
        {
            var lastWrite = File.GetLastWriteTimeUtc(localPath).Ticks;
            return $"{localPath}|{lastWrite}";
        }
        catch
        {
            return localPath;
        }
    }

    /// <summary>检查路径是否为 GIF 文件（通过扩展名判断）。</summary>
    private static bool IsGifPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return null;
    }
}
