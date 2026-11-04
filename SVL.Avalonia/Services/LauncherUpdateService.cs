using SVL.Avalonia.Models;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SVL.Avalonia.Services;

public sealed class LauncherUpdateService
{
    private const string GitHubRepo = "panda-lsy/SVL-StardewValleyLauncher";
    private const string GiteeRepo = "mc_shengxia/SVL-StardewValleyLauncher";
    private static readonly string GitHubApiUrl = $"https://api.github.com/repos/{GitHubRepo}/releases?per_page=10";
    private static readonly string GiteeApiUrl = $"https://gitee.com/api/v5/repos/{GiteeRepo}/releases?per_page=100&sort=created&direction=desc";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DownloadLocks = new(StringComparer.Ordinal);
    private readonly HttpClient _httpClient;
    private readonly string _downloadDirectory;
    private readonly LauncherRuntimeMode _runtimeMode;

    public LauncherUpdateService(HttpClient? httpClient = null, string? downloadDirectory = null)
        : this(httpClient, downloadDirectory, DetectCurrentRuntimeMode())
    {
    }

    internal LauncherUpdateService(HttpClient? httpClient, string? downloadDirectory, LauncherRuntimeMode runtimeMode)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        _downloadDirectory = downloadDirectory ?? Path.Combine(Path.GetTempPath(), "SVL", "launcher-update");
        _runtimeMode = runtimeMode;
    }

    private LauncherReleaseInfo? _cachedRelease;
    private DateTime _lastCheckTime;
    private bool _cachedIncludePrerelease;
    private bool _cachedPreferGitee;
    private string _cachedSource = "-";

    public Version CurrentVersion => ResolveCurrentVersion();
    public LauncherRuntimeMode RuntimeMode => _runtimeMode;

    public static bool IncludesPrerelease(string? channel) =>
        !string.IsNullOrWhiteSpace(channel) &&
        (channel.Contains("预览", StringComparison.OrdinalIgnoreCase) ||
         channel.Contains("preview", StringComparison.OrdinalIgnoreCase) ||
         channel.Contains("prerelease", StringComparison.OrdinalIgnoreCase));

    public static bool PrefersGitee(string? source) =>
        !string.IsNullOrWhiteSpace(source) && source.Contains("Gitee", StringComparison.OrdinalIgnoreCase);

    internal static LauncherRuntimeMode DetectCurrentRuntimeMode() =>
        ParseRuntimeMode(typeof(LauncherUpdateService).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "SVL.RuntimeMode")?.Value);

    internal static LauncherRuntimeMode ParseRuntimeMode(string? mode) => mode switch
    {
        "self-contained" => LauncherRuntimeMode.SelfContained,
        "framework-dependent" => LauncherRuntimeMode.FrameworkDependent,
        _ => LauncherRuntimeMode.Unknown
    };

    public static string DescribeRuntimeMode(LauncherRuntimeMode mode) => mode switch
    {
        LauncherRuntimeMode.SelfContained => "自带运行环境",
        LauncherRuntimeMode.FrameworkDependent => "轻量版（需要 .NET 10）",
        _ => "未识别（请手动选择下载包）"
    };

    public LauncherReleaseAsset? SelectPreferredAsset(LauncherReleaseInfo release) =>
        SelectPreferredAsset(release, _runtimeMode, OperatingSystem.IsWindows());

    internal static LauncherReleaseAsset? SelectPreferredAsset(LauncherReleaseInfo release, LauncherRuntimeMode mode, bool isWindows)
    {
        if (mode == LauncherRuntimeMode.Unknown) return null;

        return release.Assets
            .Where(asset => !IsLegacyDebugAsset(asset.Name))
            .Where(asset => asset.Name.Contains("requires_dotnet", StringComparison.OrdinalIgnoreCase) ==
                            (mode == LauncherRuntimeMode.FrameworkDependent))
            .OrderBy(asset => isWindows && asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault();
    }

    /// <summary>
    /// 下载指定资产到本地临时文件，报告进度（0-100）与已下载字节数。
    /// 支持取消。下载完成后返回本地文件路径。
    /// </summary>
    public async Task<string> DownloadAssetAsync(
        LauncherReleaseAsset asset,
        IProgress<(int Percent, long DownloadedBytes, long TotalBytes)>? progress,
        CancellationToken cancellationToken)
    {
        if (asset == null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
        {
            throw new ArgumentException("资产下载地址无效", nameof(asset));
        }

        if (string.IsNullOrWhiteSpace(asset.Name) || Path.GetFileName(asset.Name) != asset.Name ||
            !Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("更新资源名称或地址无效", nameof(asset));

        Directory.CreateDirectory(_downloadDirectory);
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(asset.BrowserDownloadUrl)))[..16];
        var localPath = Path.Combine(_downloadDirectory, $"{id}-{asset.Name}");
        var gate = DownloadLocks.GetOrAdd(localPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (asset.Size > 0 && File.Exists(localPath) && new FileInfo(localPath).Length == asset.Size &&
                await MatchesSha256Async(localPath, asset.Sha256, cancellationToken))
            {
                progress?.Report((100, asset.Size, asset.Size));
                return localPath;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, asset.BrowserDownloadUrl);
            if (asset.BrowserDownloadUrl.Contains("github", StringComparison.OrdinalIgnoreCase))
                request.Headers.Add("User-Agent", "SVL-Avalonia/1.0");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var totalBytes = asset.Size > 0 ? asset.Size : (response.Content.Headers.ContentLength ?? 0);
            var temporaryPath = localPath + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                long downloadedBytes = 0;
                await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var fileStream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    var lastReportedPercent = -1;
                    int read;
                    while ((read = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        downloadedBytes += read;
                        if (totalBytes > 0 && downloadedBytes > totalBytes)
                            throw new InvalidDataException("更新文件超过声明的长度");
                        if (progress != null && totalBytes > 0)
                        {
                            var percent = (int)Math.Min(99, downloadedBytes * 100 / totalBytes);
                            if (percent != lastReportedPercent)
                            {
                                lastReportedPercent = percent;
                                progress.Report((percent, downloadedBytes, totalBytes));
                            }
                        }
                    }
                    await fileStream.FlushAsync(cancellationToken);
                }
                if (downloadedBytes <= 0 || (totalBytes > 0 && downloadedBytes != totalBytes))
                    throw new InvalidDataException($"更新文件不完整（已下载 {downloadedBytes}，预期 {totalBytes} 字节）");
                if (!await MatchesSha256Async(temporaryPath, asset.Sha256, cancellationToken))
                    throw new InvalidDataException("更新文件的 SHA-256 校验失败");
                File.Move(temporaryPath, localPath, overwrite: true);
                progress?.Report((100, downloadedBytes, totalBytes));
                return localPath;
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
        finally { gate.Release(); }
    }

    private static async Task<bool> MatchesSha256Async(string path, string? expected, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expected)) return true;
        var normalized = expected.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? expected[7..] : expected;
        if (normalized.Length != 64 || normalized.Any(ch => !Uri.IsHexDigit(ch))) return false;
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        return string.Equals(actual, normalized, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>准备 Windows 单文件更新；调用方在成功启动辅助进程后应关闭当前程序。</summary>
    public void StartUpdateInstaller(string localFilePath)
    {
        LauncherUpdateApplier.Start(localFilePath, _downloadDirectory);
    }

    public async Task<LauncherUpdateCheckResult> CheckForUpdateAsync(bool includePrerelease, bool preferGitee, CancellationToken cancellationToken = default)
    {
        if (_cachedRelease != null &&
            DateTime.Now - _lastCheckTime < TimeSpan.FromMinutes(20) &&
            _cachedIncludePrerelease == includePrerelease &&
            _cachedPreferGitee == preferGitee)
        {
            return CreateResult(_cachedRelease, _cachedSource);
        }

        try
        {
            var primaryApi = preferGitee ? GiteeApiUrl : GitHubApiUrl;
            var primarySource = preferGitee ? "Gitee" : "GitHub";
            var fallbackApi = preferGitee ? GitHubApiUrl : GiteeApiUrl;
            var fallbackSource = preferGitee ? "GitHub" : "Gitee";

            LauncherReleaseInfo? release;
            try
            {
                release = await FetchLatestReleaseAsync(primaryApi, primarySource, includePrerelease, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // 首选源离线或限流时仍应尝试另一个源。
                release = null;
            }
            var source = primarySource;

            if (release == null)
            {
                release = await FetchLatestReleaseAsync(fallbackApi, fallbackSource, includePrerelease, cancellationToken);
                source = fallbackSource;
            }

            if (release == null)
            {
                return new LauncherUpdateCheckResult
                {
                    Success = false,
                    Source = "-",
                    ErrorMessage = "无法从更新源获取版本信息"
                };
            }

            _cachedRelease = release;
            _lastCheckTime = DateTime.Now;
            _cachedIncludePrerelease = includePrerelease;
            _cachedPreferGitee = preferGitee;
            _cachedSource = source;

            return CreateResult(release, source);
        }
        catch (Exception ex)
        {
            return new LauncherUpdateCheckResult
            {
                Success = false,
                Source = "-",
                ErrorMessage = $"检查更新失败: {ex.Message}"
            };
        }
    }

    private LauncherUpdateCheckResult CreateResult(LauncherReleaseInfo releaseInfo, string source)
    {
        var currentVersion = CurrentVersion;
        var latestVersion = ParseVersion(releaseInfo.TagName);

        return new LauncherUpdateCheckResult
        {
            Success = true,
            Source = source,
            CurrentVersion = currentVersion,
            LatestVersion = latestVersion,
            HasUpdate = latestVersion > currentVersion,
            ReleaseInfo = releaseInfo
        };
    }

    private async Task<LauncherReleaseInfo?> FetchLatestReleaseAsync(string apiUrl, string source, bool includePrerelease, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        if (string.Equals(source, "GitHub", StringComparison.Ordinal))
        {
            request.Headers.Add("User-Agent", "SVL-Avalonia/1.0");
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var releaseElement in document.RootElement.EnumerateArray())
        {
            var tagName = ReadString(releaseElement, "tag_name", "tagName");
            if (string.IsNullOrWhiteSpace(tagName))
            {
                continue;
            }

            var isPrerelease = ReadBoolean(releaseElement, "prerelease", "prerelease_release", "pre_release");
            if (!includePrerelease && isPrerelease)
            {
                continue;
            }

            var info = new LauncherReleaseInfo
            {
                TagName = tagName,
                Name = ReadString(releaseElement, "name") ?? tagName,
                Body = ReadString(releaseElement, "body") ?? string.Empty,
                HtmlUrl = ReadString(releaseElement, "html_url") ?? string.Empty,
                PublishedAt = ReadDateTime(releaseElement, "published_at", "created_at"),
                IsPrerelease = isPrerelease
            };

            if (string.IsNullOrWhiteSpace(info.HtmlUrl))
            {
                var repository = string.Equals(source, "Gitee", StringComparison.Ordinal) ? GiteeRepo : GitHubRepo;
                var host = string.Equals(source, "Gitee", StringComparison.Ordinal) ? "gitee.com" : "github.com";
                info.HtmlUrl = $"https://{host}/{repository}/releases/tag/{Uri.EscapeDataString(tagName)}";
            }

            string? updateTxtUrl = null;
            if (TryGetProperty(releaseElement, out var assetsElement, "assets") && assetsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var assetElement in assetsElement.EnumerateArray())
                {
                    var assetName = ReadString(assetElement, "name") ?? string.Empty;
                    var downloadUrl = ReadString(assetElement, "browser_download_url", "download_url", "url") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(assetName) || string.IsNullOrWhiteSpace(downloadUrl))
                    {
                        continue;
                    }

                    if (assetName.Equals("Update.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        updateTxtUrl = downloadUrl;
                        continue;
                    }

                    if (!IsCompatibleAsset(assetName))
                    {
                        continue;
                    }

                    info.Assets.Add(new LauncherReleaseAsset
                    {
                        Name = assetName,
                        BrowserDownloadUrl = downloadUrl,
                        Size = ReadInt64(assetElement, "size"),
                        Sha256 = ReadString(assetElement, "digest", "sha256") ?? string.Empty
                    });
                }
            }

            if (info.Assets.Count == 0)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(updateTxtUrl))
            {
                try
                {
                    info.UpdateLog = await FetchUpdateLogAsync(updateTxtUrl, source, cancellationToken);
                }
                catch
                {
                    info.UpdateLog = string.Empty;
                }
            }

            if (string.IsNullOrWhiteSpace(info.UpdateLog))
            {
                info.UpdateLog = info.Body;
            }

            return info;
        }

        return null;
    }

    private async Task<string> FetchUpdateLogAsync(string updateTxtUrl, string source, CancellationToken cancellationToken)
    {
        var updateRequest = new HttpRequestMessage(HttpMethod.Get, updateTxtUrl);
        if (string.Equals(source, "GitHub", StringComparison.Ordinal))
        {
            updateRequest.Headers.Add("User-Agent", "SVL-Avalonia/1.0");
        }

        using var updateResponse = await _httpClient.SendAsync(updateRequest, cancellationToken);
        updateResponse.EnsureSuccessStatusCode();
        return await updateResponse.Content.ReadAsStringAsync(cancellationToken);
    }

    private static Version ResolveCurrentVersion()
    {
        var entry = Assembly.GetEntryAssembly()?.GetName().Version;
        if (entry != null)
        {
            return entry;
        }

        var executing = Assembly.GetExecutingAssembly().GetName().Version;
        if (executing != null)
        {
            return executing;
        }

        return new Version(1, 0, 0, 0);
    }

    private static Version ParseVersion(string rawTag)
    {
        if (string.IsNullOrWhiteSpace(rawTag))
        {
            return new Version(0, 0, 0, 0);
        }

        var tag = rawTag.Trim().TrimStart('v', 'V');
        var segments = tag.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var numbers = new List<int>(4);

        foreach (var segment in segments)
        {
            var numericPart = new string(segment.TakeWhile(char.IsDigit).ToArray());
            if (string.IsNullOrWhiteSpace(numericPart))
            {
                break;
            }

            if (int.TryParse(numericPart, out var value))
            {
                numbers.Add(value);
            }

            if (numbers.Count >= 4)
            {
                break;
            }
        }

        while (numbers.Count < 4)
        {
            numbers.Add(0);
        }

        return new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    private static bool IsCompatibleAsset(string assetName) => IsCompatibleAsset(
        assetName,
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
        RuntimeInformation.ProcessArchitecture.ToString());

    private static bool IsLegacyDebugAsset(string name) =>
        name.Contains("debug", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("diagnostic", StringComparison.OrdinalIgnoreCase);

    internal static bool IsCompatibleAsset(string assetName, string platform, string arch)
    {
        if (string.IsNullOrWhiteSpace(assetName) || IsLegacyDebugAsset(assetName)) return false;

        var hasReleaseMarker = assetName.Contains("release", StringComparison.OrdinalIgnoreCase);
        var isWindowsLiteExe = platform.Equals("windows", StringComparison.OrdinalIgnoreCase) &&
            assetName.StartsWith("SVL_v", StringComparison.OrdinalIgnoreCase) &&
            assetName.Contains("Windows_", StringComparison.OrdinalIgnoreCase) &&
            assetName.Contains("requires_dotnet", StringComparison.OrdinalIgnoreCase) &&
            assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        var isLinuxDeb = platform.Equals("linux", StringComparison.OrdinalIgnoreCase) &&
            assetName.StartsWith("svl-launcher_", StringComparison.OrdinalIgnoreCase) &&
            assetName.EndsWith(".deb", StringComparison.OrdinalIgnoreCase);
        if (!hasReleaseMarker && !isWindowsLiteExe && !isLinuxDeb) return false;

        foreach (var knownArch in new[] { "x64", "x86", "arm64", "arm32" })
        {
            if (assetName.Contains(knownArch, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(arch, knownArch, StringComparison.OrdinalIgnoreCase)) return false;
        }

        if (assetName.Contains("amd64", StringComparison.OrdinalIgnoreCase) &&
            !arch.Equals("x64", StringComparison.OrdinalIgnoreCase)) return false;
        if (assetName.Contains("aarch64", StringComparison.OrdinalIgnoreCase) &&
            !arch.Equals("arm64", StringComparison.OrdinalIgnoreCase)) return false;

        if (platform.Equals("windows", StringComparison.OrdinalIgnoreCase))
        {
            // 旧 WPF 升级 EXE 未在文件名标记架构，实际只提供 x64。
            if (assetName.StartsWith("SVL.Desktop_", StringComparison.OrdinalIgnoreCase) &&
                !arch.Equals("x64", StringComparison.OrdinalIgnoreCase)) return false;
            return (assetName.Contains("Windows_", StringComparison.OrdinalIgnoreCase) &&
                    assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) ||
                   assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        }

        if (platform.Equals("macos", StringComparison.OrdinalIgnoreCase))
        {
            return (assetName.Contains("osx_", StringComparison.OrdinalIgnoreCase) ||
                    assetName.Contains("macOS_", StringComparison.OrdinalIgnoreCase)) &&
                   (assetName.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase) ||
                    assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        }

        return isLinuxDeb || ((assetName.Contains("linux_", StringComparison.OrdinalIgnoreCase) ||
                assetName.Contains("linux-", StringComparison.OrdinalIgnoreCase)) &&
               (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                assetName.EndsWith(".deb", StringComparison.OrdinalIgnoreCase) ||
                assetName.EndsWith($"_Linux_{arch}", StringComparison.OrdinalIgnoreCase) ||
                assetName.EndsWith($"_Linux_{arch}_requires_dotnet10", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool TryGetProperty(JsonElement element, out JsonElement value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out value))
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? ReadString(JsonElement element, params string[] keys)
    {
        if (!TryGetProperty(element, out var value, keys))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static bool ReadBoolean(JsonElement element, params string[] keys)
    {
        if (!TryGetProperty(element, out var value, keys))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.GetInt32() != 0,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
            _ => false
        };
    }

    private static DateTime ReadDateTime(JsonElement element, params string[] keys)
    {
        if (!TryGetProperty(element, out var value, keys))
        {
            return DateTime.MinValue;
        }

        if (value.ValueKind == JsonValueKind.String && DateTime.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return DateTime.MinValue;
    }

    private static long ReadInt64(JsonElement element, params string[] keys)
    {
        if (!TryGetProperty(element, out var value, keys))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetInt64(),
            JsonValueKind.String => long.TryParse(value.GetString(), out var parsed) ? parsed : 0,
            _ => 0
        };
    }
}
