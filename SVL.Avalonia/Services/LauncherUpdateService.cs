using SVL.Avalonia.Models;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SVL.Avalonia.Services;

public sealed class LauncherUpdateService
{
    private const string GitHubRepo = "panda-lsy/SVL-StardewValleyLauncher";
    private const string GiteeRepo = "mc_shengxia/SVL-StardewValleyLauncher";
    private static readonly string GitHubApiUrl = $"https://api.github.com/repos/{GitHubRepo}/releases?per_page=10";
    private static readonly string GiteeApiUrl = $"https://gitee.com/api/v5/repos/{GiteeRepo}/releases?per_page=100&sort=created&direction=desc";

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(25)
    };

    private LauncherReleaseInfo? _cachedRelease;
    private DateTime _lastCheckTime;
    private bool _cachedIncludePrerelease;
    private bool _cachedPreferGitee;
    private string _cachedSource = "-";

    public Version CurrentVersion => ResolveCurrentVersion();

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

        var tempDir = Path.Combine(Path.GetTempPath(), "SVL", "launcher-update");
        Directory.CreateDirectory(tempDir);
        var localPath = Path.Combine(tempDir, asset.Name);

        using var request = new HttpRequestMessage(HttpMethod.Get, asset.BrowserDownloadUrl);
        if (asset.BrowserDownloadUrl.Contains("github", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Add("User-Agent", "SVL-Avalonia/1.0");
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = asset.Size > 0 ? asset.Size : (response.Content.Headers.ContentLength ?? 0);
        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = File.Create(localPath);

        var buffer = new byte[81920];
        long downloadedBytes = 0;
        int lastReportedPercent = -1;
        int read;

        while ((read = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloadedBytes += read;

            if (progress != null && totalBytes > 0)
            {
                var percent = (int)(downloadedBytes * 100 / totalBytes);
                if (percent != lastReportedPercent)
                {
                    lastReportedPercent = percent;
                    progress.Report((percent, downloadedBytes, totalBytes));
                }
            }
        }

        progress?.Report((100, downloadedBytes, totalBytes));
        return localPath;
    }

    /// <summary>启动已下载的更新包（Windows .exe / macOS .dmg / Linux .zip）。</summary>
    public void StartUpdateInstaller(string localFilePath)
    {
        if (!File.Exists(localFilePath))
        {
            throw new FileNotFoundException("更新文件不存在", localFilePath);
        }

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = localFilePath,
            UseShellExecute = true
        };

        // Windows .exe 与 macOS .dmg 可直接用 ShellExecute 打开；Linux 依赖文件管理器
        System.Diagnostics.Process.Start(startInfo);
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

            var release = await FetchLatestReleaseAsync(primaryApi, primarySource, includePrerelease, cancellationToken);
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
                        Size = ReadInt64(assetElement, "size")
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

    private static bool IsCompatibleAsset(string assetName)
    {
        var buildToken = IsDebugBuild ? "Debug" : "Release";
        if (!assetName.Contains(buildToken, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return assetName.Contains("Windows_", StringComparison.OrdinalIgnoreCase) ||
                   assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        }

        if (OperatingSystem.IsMacOS())
        {
            return assetName.Contains("osx_", StringComparison.OrdinalIgnoreCase) ||
                   assetName.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase) ||
                   assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        }

        var arch = RuntimeInformation.ProcessArchitecture.ToString();
        return assetName.Contains("linux_", StringComparison.OrdinalIgnoreCase) ||
               assetName.Contains(arch, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDebugBuild
    {
        get
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
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