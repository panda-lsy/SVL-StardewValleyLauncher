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
    /// WPF 旧版图片缓存目录。只读兼容该目录，新的下载仍写入 Avalonia 目录；
    /// 这样升级后首次显示旧搜索结果时不会重新下载全部图片。
    /// </summary>
    public static readonly string LegacyIconCacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SVL",
        "cache",
        "images");

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

    /// <summary>
    /// 按 WPF 旧版规则计算图片缓存路径。旧版使用原始 URL（不是 Uri 规范化后的文本）
    /// 计算 SHA-256，因此必须保留独立方法，不能直接复用新的路径算法。
    /// </summary>
    public static string GetLegacyIconCachePath(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl) ||
            !Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        using var sha256 = SHA256.Create();
        var hash = System.Convert.ToHexString(
                sha256.ComputeHash(Encoding.UTF8.GetBytes(remoteUrl)))
            .ToLowerInvariant();
        var extension = Path.GetExtension(uri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".jpg";
        }

        return Path.Combine(LegacyIconCacheDirectory, hash + extension);
    }

    private static string FindExistingRemoteIconCachePath(string remoteUrl)
    {
        var currentPath = GetIconCachePath(remoteUrl);
        if (!string.IsNullOrWhiteSpace(currentPath) && File.Exists(currentPath))
        {
            return currentPath;
        }

        var legacyPath = GetLegacyIconCachePath(remoteUrl);
        return !string.IsNullOrWhiteSpace(legacyPath) && File.Exists(legacyPath)
            ? legacyPath
            : string.Empty;
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

        // 当 ConverterParameter=animate 时，仅对 GIF 和实际包含 acTL 块的 APNG 返回
        // AnimatedImageSourceUri。普通 PNG 继续由 Source 的内存 Bitmap 渲染，避免
        // 动画控件长期占用实例目录中的静态图标文件。
        var allowAnimated = parameter is string paramStr &&
                            paramStr.Equals("animate", StringComparison.OrdinalIgnoreCase);
        if (allowAnimated)
        {
            if (!IsAnimatedImagePath(normalizedPath))
            {
                return null;
            }

            try
            {
                if (File.Exists(normalizedPath))
                {
                    // AnimatedImageSourceUri keeps a URI-backed decoder alive. Point it at an
                    // application cache copy instead of the instance file so a custom APNG/GIF
                    // can never prevent its game version directory from being deleted.
                    var cachedAnimatedUri = CreateAnimatedCacheUri(normalizedPath);
                    return cachedAnimatedUri is null ? null : new AnimatedImageSourceUri(cachedAnimatedUri);
                }

                if (Uri.TryCreate(path, UriKind.Absolute, out var animatedUri))
                {
                    if (animatedUri.IsFile)
                    {
                        var localPath = Uri.UnescapeDataString(animatedUri.LocalPath);
                        var cachedAnimatedUri = CreateAnimatedCacheUri(localPath);
                        return cachedAnimatedUri is null ? null : new AnimatedImageSourceUri(cachedAnimatedUri);
                    }

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
                return LoadCached(cacheKey, () => LoadLocalBitmap(normalizedPath));
            }

            if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
            {
                if (uri.IsFile)
                {
                    var localPath = Uri.UnescapeDataString(uri.LocalPath);
                    if (File.Exists(localPath))
                    {
                        return LoadCached(BuildLocalFileCacheKey(localPath), () => LoadLocalBitmap(localPath));
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
                    var cachePath = FindExistingRemoteIconCachePath(path);
                    if (!string.IsNullOrEmpty(cachePath) && File.Exists(cachePath))
                    {
                        return LoadCached(BuildLocalFileCacheKey(cachePath), () => LoadLocalBitmap(cachePath));
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
                        return LoadCached(BuildLocalFileCacheKey(localPath), () => LoadLocalBitmap(localPath));
                    }

                    return null;
                }

                if (normalizedUri.Scheme == Uri.UriSchemeHttp || normalizedUri.Scheme == Uri.UriSchemeHttps)
                {
                    // Same disk-cache fast path as above. Use the original (non-stripped) URL
                    // so the computed cache path matches what the ViewModel hashed.
                    var cachePath = FindExistingRemoteIconCachePath(path);
                    if (!string.IsNullOrEmpty(cachePath) && File.Exists(cachePath))
                    {
                        return LoadCached(BuildLocalFileCacheKey(cachePath), () => LoadLocalBitmap(cachePath));
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

    /// <summary>
    /// 移除指定实例目录下的本地图像缓存索引。Bitmap 本身由当前 Image 控件继续持有，
    /// 因此不在这里 Dispose，避免控件在重绘期间访问已释放的对象。
    /// </summary>
    public static void RemoveCachedLocalFilesUnderDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        string normalizedDirectory;
        try
        {
            normalizedDirectory = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }
        catch
        {
            return;
        }

        foreach (var cacheKey in BitmapCache.Keys)
        {
            var separatorIndex = cacheKey.LastIndexOf('|');
            var localPath = separatorIndex >= 0 ? cacheKey[..separatorIndex] : cacheKey;
            try
            {
                var normalizedPath = Path.GetFullPath(localPath);
                if (normalizedPath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    BitmapCache.TryRemove(cacheKey, out _);
                }
            }
            catch
            {
                // URI resource keys and malformed cache keys are not local files.
            }
        }
    }

    /// <summary>从共享读取流中解码本地文件，避免 Avalonia Bitmap 长时间持有源文件句柄。</summary>
    private static Bitmap LoadLocalBitmap(string localPath)
    {
        using var source = new FileStream(
            localPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        source.CopyTo(memory);
        memory.Position = 0;
        return new Bitmap(memory);
    }

    /// <summary>检查路径是否实际包含动画帧；普通 PNG 必须走内存 Bitmap，不能交给动画控件。</summary>
    private static bool IsAnimatedImagePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
               File.Exists(path) &&
               IsAnimatedPng(path);
    }

    private static bool IsAnimatedPng(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

            var signature = reader.ReadBytes(8);
            if (signature.Length != 8 ||
                signature[0] != 137 || signature[1] != 80 || signature[2] != 78 || signature[3] != 71)
            {
                return false;
            }

            while (stream.Position + 8 <= stream.Length)
            {
                var lengthBytes = reader.ReadBytes(4);
                var typeBytes = reader.ReadBytes(4);
                if (lengthBytes.Length != 4 || typeBytes.Length != 4)
                {
                    return false;
                }

                var length = ((long)lengthBytes[0] << 24) |
                             ((long)lengthBytes[1] << 16) |
                             ((long)lengthBytes[2] << 8) |
                             lengthBytes[3];
                var type = Encoding.ASCII.GetString(typeBytes);
                if (string.Equals(type, "acTL", StringComparison.Ordinal))
                {
                    return true;
                }

                // Skip payload and CRC. A malformed chunk is treated as a non-animated PNG.
                if (length < 0 || stream.Position + length + 4 > stream.Length)
                {
                    return false;
                }

                stream.Seek(length + 4, SeekOrigin.Current);
            }
        }
        catch
        {
            // A normal static bitmap is still rendered by the regular Source binding.
        }

        return false;
    }

    /// <summary>
    /// Copies a local animated asset to the shared cache before handing it to the animation
    /// component. This keeps the decoder away from per-instance files that users may delete.
    /// </summary>
    private static Uri? CreateAnimatedCacheUri(string localPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(localPath);
            var lastWrite = File.GetLastWriteTimeUtc(fullPath).Ticks;
            var length = new FileInfo(fullPath).Length;
            var identity = $"{fullPath}|{lastWrite}|{length}";
            using var sha256 = SHA256.Create();
            var hash = System.Convert.ToHexString(sha256.ComputeHash(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
            var extension = Path.GetExtension(fullPath);
            var cacheDirectory = Path.Combine(IconCacheDirectory, "animated");
            var cachePath = Path.Combine(cacheDirectory, hash + (string.IsNullOrWhiteSpace(extension) ? ".img" : extension));

            if (!File.Exists(cachePath))
            {
                Directory.CreateDirectory(cacheDirectory);
                using var source = new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var destination = new FileStream(cachePath, FileMode.Create, FileAccess.Write, FileShare.Read);
                source.CopyTo(destination);
            }

            return new Uri(cachePath);
        }
        catch
        {
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return null;
    }
}
