using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SVL.Core.Logging;

namespace SVL.Core.App;

/// <summary>
/// 启动器更新服务
/// </summary>
public static class LauncherUpdateService
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    // 更新源配置
    private const string GitHubRepo = "panda-lsy/SVL-StardewValleyLauncher";
    private const string GiteeRepo = "mc_shengxia/SVL-StardewValleyLauncher";

    private static readonly string GitHubApiUrl = $"https://api.github.com/repos/{GitHubRepo}/releases?per_page=10";
    private static readonly string GiteeApiUrl = $"https://gitee.com/api/v5/repos/{GiteeRepo}/releases?per_page=100&sort=created&direction=desc";

    // 缓存最新版本信息（按源分开缓存）
    private static ReleaseInfo? _cachedGitHubRelease;
    private static ReleaseInfo? _cachedGiteeRelease;
    private static DateTime _lastGitHubCheckTime;
    private static DateTime _lastGiteeCheckTime;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 当前版本
    /// </summary>
    public static Version CurrentVersion
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version ?? new Version(1, 1, 1, 0);
        }
    }

    /// <summary>
    /// 当前是否为 Debug 版本
    /// </summary>
    public static bool IsDebugBuild
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

    /// <summary>
    /// 清除指定源的缓存
    /// </summary>
    /// <param name="preferGitee">true 清除 Gitee 缓存，false 清除 GitHub 缓存</param>
    public static void ClearCache(bool preferGitee)
    {
        if (preferGitee)
        {
            _cachedGiteeRelease = null;
            _lastGiteeCheckTime = DateTime.MinValue;
            Log.Info("[LauncherUpdateService] 已清除 Gitee 缓存");
        }
        else
        {
            _cachedGitHubRelease = null;
            _lastGitHubCheckTime = DateTime.MinValue;
            Log.Info("[LauncherUpdateService] 已清除 GitHub 缓存");
        }
    }

    /// <summary>
    /// 清除所有缓存
    /// </summary>
    public static void ClearAllCache()
    {
        _cachedGitHubRelease = null;
        _cachedGiteeRelease = null;
        _lastGitHubCheckTime = DateTime.MinValue;
        _lastGiteeCheckTime = DateTime.MinValue;
        Log.Info("[LauncherUpdateService] 已清除所有更新缓存");
    }

    /// <summary>
    /// 检查更新
    /// </summary>
    /// <param name="preferGitee">是否优先使用 Gitee 源（默认 true）</param>
    /// <param name="includePrerelease">是否包含预发布版本（默认 false）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>更新检查结果</returns>
    public static async Task<UpdateCheckResult> CheckForUpdateAsync(bool preferGitee = true, bool includePrerelease = false, CancellationToken cancellationToken = default)
    {
        try
        {
            // 根据当前选择的源检查对应的缓存
            var (cachedRelease, lastCheckTime) = preferGitee
                ? (_cachedGiteeRelease, _lastGiteeCheckTime)
                : (_cachedGitHubRelease, _lastGitHubCheckTime);

            if (cachedRelease != null && DateTime.Now - lastCheckTime < CacheDuration)
            {
                Log.Info($"[LauncherUpdateService] 使用 {(preferGitee ? "Gitee" : "GitHub")} 缓存的版本信息: {cachedRelease.TagName}");
                var cachedResult = CreateResult(cachedRelease);
                cachedResult.Source = preferGitee ? "Gitee" : "GitHub";
                return cachedResult;
            }

            ReleaseInfo? release = null;
            string? usedSource = null;

            // 根据偏好选择源
            var sources = preferGitee
                ? new[] { (Url: GiteeApiUrl, Name: "Gitee") }
                : new[] { (Url: GitHubApiUrl, Name: "GitHub") };

            Log.Info($"[LauncherUpdateService] 开始检查更新，首选源: {(preferGitee ? "Gitee" : "GitHub")}");

            // 尝试首选源
            foreach (var source in sources)
            {
                try
                {
                    Log.Info($"[LauncherUpdateService] 正在从 {source.Name} 获取更新...");
                    release = await FetchLatestReleaseAsync(source.Url, source.Name, includePrerelease, cancellationToken);
                    if (release != null)
                    {
                        usedSource = source.Name;
                        Log.Info($"[LauncherUpdateService] 从 {source.Name} 成功获取版本: {release.TagName}");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"[LauncherUpdateService] 从 {source.Name} 获取更新失败: {ex.Message}");
                    Log.Debug($"[LauncherUpdateService] 详细错误: {ex}");
                }
            }

            // 如果首选源失败，尝试备用源
            if (release == null)
            {
                var fallbackSource = preferGitee
                    ? (Url: GitHubApiUrl, Name: "GitHub")
                    : (Url: GiteeApiUrl, Name: "Gitee");

                try
                {
                    Log.Info($"[LauncherUpdateService] 首选源失败，尝试备用源 {fallbackSource.Name}...");
                    release = await FetchLatestReleaseAsync(fallbackSource.Url, fallbackSource.Name, includePrerelease, cancellationToken);
                    if (release != null)
                    {
                        usedSource = fallbackSource.Name;
                        Log.Info($"[LauncherUpdateService] 从备用源 {fallbackSource.Name} 成功获取版本: {release.TagName}");
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"[LauncherUpdateService] 从备用源 {fallbackSource.Name} 获取更新失败: {ex.Message}");
                    Log.Debug($"[LauncherUpdateService] 详细错误: {ex}");
                }
            }

            if (release == null)
            {
                Log.Warn("[LauncherUpdateService] 无法从任何更新源获取版本信息");
                return new UpdateCheckResult
                {
                    Success = false,
                    ErrorMessage = "无法从任何更新源获取版本信息"
                };
            }

            // 根据使用的源缓存结果
            if (usedSource == "GitHub")
            {
                _cachedGitHubRelease = release;
                _lastGitHubCheckTime = DateTime.Now;
            }
            else if (usedSource == "Gitee")
            {
                _cachedGiteeRelease = release;
                _lastGiteeCheckTime = DateTime.Now;
            }

            var result = CreateResult(release);
            result.Source = usedSource ?? "Unknown";
            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[LauncherUpdateService] 检查更新失败");
            return new UpdateCheckResult
            {
                Success = false,
                ErrorMessage = $"检查更新失败: {ex.Message}"
            };
        }
    }

    private static UpdateCheckResult CreateResult(ReleaseInfo release)
    {
        var latestVersion = ParseVersion(release.TagName);
        var hasUpdate = latestVersion > CurrentVersion;

        return new UpdateCheckResult
        {
            Success = true,
            LatestVersion = latestVersion,
            CurrentVersion = CurrentVersion,
            HasUpdate = hasUpdate,
            ReleaseInfo = release
        };
    }

    private static async Task<ReleaseInfo?> FetchLatestReleaseAsync(string url, string source, bool includePrerelease, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);

        // GitHub 需要 User-Agent
        if (source == "GitHub")
        {
            request.Headers.Add("User-Agent", "SVL-Launcher/1.0");
        }

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();

        ReleaseInfo? info;
        string? updateTxtUrl;

        if (source == "GitHub")
        {
            (info, updateTxtUrl) = ParseGitHubReleases(json, includePrerelease);
        }
        else
        {
            (info, updateTxtUrl) = ParseGiteeReleases(json, includePrerelease);
        }

        // 异步获取 Update.txt 内容
        if (info != null && !string.IsNullOrEmpty(updateTxtUrl))
        {
            try
            {
                Log.Debug($"[LauncherUpdateService] 正在获取 Update.txt: {updateTxtUrl}");
                info.UpdateLog = await FetchUpdateLogAsync(updateTxtUrl, cancellationToken);
                Log.Debug($"[LauncherUpdateService] 从 Update.txt 获取更新日志，长度: {info.UpdateLog?.Length ?? 0}");
            }
            catch (OperationCanceledException)
            {
                // 取消操作时不记录警告，直接返回
                Log.Debug("[LauncherUpdateService] 获取 Update.txt 已被取消");
                throw;
            }
            catch (HttpRequestException ex)
            {
                // 网络请求错误（如 GitHub 资源无法访问）
                Log.Warn($"[LauncherUpdateService] 获取 Update.txt 失败 (网络错误): {ex.Message}");
                Log.Debug($"[LauncherUpdateService] Update.txt URL: {updateTxtUrl}");
                info.UpdateLog = "无法获取更新日志（网络连接失败）";
            }
            catch (Exception ex)
            {
                Log.Warn($"[LauncherUpdateService] 获取 Update.txt 失败: {ex.Message}");
                info.UpdateLog = "无法获取更新日志";
            }
        }
        else if (info != null && string.IsNullOrEmpty(updateTxtUrl))
        {
            info.UpdateLog = "该版本未提供更新日志";
            Log.Debug($"[LauncherUpdateService] Release {info.TagName} 没有 Update.txt 文件");
        }

        return info;
    }

    private static (ReleaseInfo? info, string? updateTxtUrl) ParseGitHubReleases(string json, bool includePrerelease)
    {
        try
        {
            Log.Debug($"[LauncherUpdateService] GitHub API 响应长度: {json.Length}");
            using var doc = JsonDocument.Parse(json);
            var releases = doc.RootElement.EnumerateArray();
            var releaseCount = 0;

            // Debug 和 Release 发布到同一个 Release 中，通过文件名区分
            // 文件名包含 "Debug" 的是 Debug 版本，包含 "Release" 的是 Release 版本
            foreach (var release in releases)
            {
                releaseCount++;
                var tagName = release.GetProperty("tag_name").GetString() ?? "";
                var isPrerelease = release.TryGetProperty("prerelease", out var prereleaseProp) && prereleaseProp.GetBoolean();

                // 如果不包含预发布版本，且该版本是预发布，则跳过
                if (!includePrerelease && isPrerelease)
                {
                    Log.Debug($"[LauncherUpdateService] GitHub Release #{releaseCount}: {tagName} 是预发布版本，已跳过");
                    continue;
                }

                Log.Debug($"[LauncherUpdateService] GitHub Release #{releaseCount}: {tagName}, IsDebugBuild={IsDebugBuild}, IsPrerelease={isPrerelease}");

                var info = new ReleaseInfo
                {
                    TagName = tagName,
                    Name = release.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? tagName : tagName,
                    Body = "", // 不再使用 Body
                    HtmlUrl = release.GetProperty("html_url").GetString() ?? "",
                    PublishedAt = release.TryGetProperty("published_at", out var dateProp) ? dateProp.GetDateTime() : DateTime.MinValue,
                    IsPrerelease = isPrerelease
                };

                // 解析资源，根据当前构建类型筛选
                var buildType = IsDebugBuild ? "Debug" : "Release";
                string? updateTxtUrl = null;
                
                if (release.TryGetProperty("assets", out var assetsProp))
                {
                    foreach (var asset in assetsProp.EnumerateArray())
                    {
                        var assetName = asset.GetProperty("name").GetString() ?? "";
                        var downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                        
                        // 检查是否是 Update.txt 文件
                        if (assetName.Equals("Update.txt", StringComparison.OrdinalIgnoreCase))
                        {
                            updateTxtUrl = downloadUrl;
                            continue;
                        }
                        
                        // 检查文件是否匹配当前构建类型（使用 IndexOf 兼容 .NET Framework）
                        if ((assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                             assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) &&
                            assetName.IndexOf(buildType, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            info.Assets.Add(new ReleaseAsset
                            {
                                Name = assetName,
                                BrowserDownloadUrl = downloadUrl,
                                Size = asset.GetProperty("size").GetInt64()
                            });
                        }
                    }
                }

                // 只有找到匹配的资源才返回
                if (info.Assets.Count > 0)
                {
                    Log.Info($"[LauncherUpdateService] 选择 GitHub Release: {tagName}, 匹配 '{buildType}' 的资源数: {info.Assets.Count}");
                    return (info, updateTxtUrl);
                }
                else
                {
                    Log.Debug($"[LauncherUpdateService] GitHub Release {tagName} 没有找到匹配 '{buildType}' 的资源");
                }
            }

            Log.Warn($"[LauncherUpdateService] GitHub: 检查了 {releaseCount} 个版本，未找到匹配 '{(IsDebugBuild ? "Debug" : "Release")}' 的资源");
            return (null, null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[LauncherUpdateService] 解析 GitHub releases 失败");
            return (null, null);
        }
    }

    private static (ReleaseInfo? info, string? updateTxtUrl) ParseGiteeReleases(string json, bool includePrerelease)
    {
        try
        {
            Log.Debug($"[LauncherUpdateService] Gitee API 响应长度: {json.Length}");
            using var doc = JsonDocument.Parse(json);
            var releases = doc.RootElement.EnumerateArray();
            var releaseCount = 0;

            // Debug 和 Release 发布到同一个 Release 中，通过文件名区分
            // 文件名包含 "Debug" 的是 Debug 版本，包含 "Release" 的是 Release 版本
            foreach (var release in releases)
            {
                releaseCount++;
                var tagName = release.GetProperty("tag_name").GetString() ?? "";
                var isPrerelease = release.TryGetProperty("prerelease", out var prereleaseProp) && prereleaseProp.GetBoolean();

                // 如果不包含预发布版本，且该版本是预发布，则跳过
                if (!includePrerelease && isPrerelease)
                {
                    Log.Debug($"[LauncherUpdateService] Gitee Release #{releaseCount}: {tagName} 是预发布版本，已跳过");
                    continue;
                }

                Log.Debug($"[LauncherUpdateService] Gitee Release #{releaseCount}: {tagName}, IsDebugBuild={IsDebugBuild}, IsPrerelease={isPrerelease}");

                var info = new ReleaseInfo
                {
                    TagName = tagName,
                    Name = release.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? tagName : tagName,
                    Body = "", // 不再使用 Body，只使用 Update.txt
                    HtmlUrl = release.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() ?? "" : "",
                    PublishedAt = release.TryGetProperty("published_at", out var dateProp)
                        ? DateTime.TryParse(dateProp.GetString(), out var date) ? date : DateTime.MinValue
                        : DateTime.MinValue,
                    IsPrerelease = isPrerelease
                };

                // 解析资源，根据当前构建类型筛选
                var buildType = IsDebugBuild ? "Debug" : "Release";
                string? updateTxtUrl = null;
                
                if (release.TryGetProperty("assets", out var assetsProp))
                {
                    foreach (var asset in assetsProp.EnumerateArray())
                    {
                        var assetName = asset.GetProperty("name").GetString() ?? "";
                        var downloadUrl = asset.TryGetProperty("browser_download_url", out var dlProp)
                            ? dlProp.GetString() ?? ""
                            : "";
                        
                        // 检查是否是 Update.txt 文件
                        if (assetName.Equals("Update.txt", StringComparison.OrdinalIgnoreCase))
                        {
                            updateTxtUrl = downloadUrl;
                            continue;
                        }
                        
                        // 检查文件是否匹配当前构建类型（使用 IndexOf 兼容 .NET Framework）
                        if ((assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                             assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) &&
                            assetName.IndexOf(buildType, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            info.Assets.Add(new ReleaseAsset
                            {
                                Name = assetName,
                                BrowserDownloadUrl = downloadUrl,
                                Size = asset.TryGetProperty("size", out var sizeProp) ? sizeProp.GetInt64() : 0
                            });
                        }
                    }
                }

                // 只有找到匹配的资源才返回
                if (info.Assets.Count > 0)
                {
                    // 不再在此处同步获取 Update.txt，由调用方异步处理
                    Log.Info($"[LauncherUpdateService] 选择 Gitee Release: {tagName}, 匹配 '{buildType}' 的资源数: {info.Assets.Count}");
                    return (info, updateTxtUrl);
                }
                else
                {
                    Log.Debug($"[LauncherUpdateService] Gitee Release {tagName} 没有找到匹配 '{buildType}' 的资源");
                }
            }

            Log.Warn($"[LauncherUpdateService] Gitee: 检查了 {releaseCount} 个版本，未找到匹配 '{(IsDebugBuild ? "Debug" : "Release")}' 的资源");
            return (null, null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[LauncherUpdateService] 解析 Gitee releases 失败");
            return (null, null);
        }
    }

    /// <summary>
    /// 从 Update.txt URL 获取更新日志内容
    /// </summary>
    private static async Task<string> FetchUpdateLogAsync(string url, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", "SVL-Launcher/1.0");
        
        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        
        return await response.Content.ReadAsStringAsync();
    }

    private static Version ParseVersion(string tagName)
    {
        // 移除 'v' 前缀
        var versionStr = tagName.TrimStart('v');

        // 尝试解析版本号
        if (Version.TryParse(versionStr, out var version))
        {
            return version;
        }

        // 尝试从字符串中提取版本号
        var match = Regex.Match(versionStr, @"(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?");
        if (match.Success)
        {
            var major = int.Parse(match.Groups[1].Value);
            var minor = int.Parse(match.Groups[2].Value);
            var build = int.Parse(match.Groups[3].Value);
            var revision = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 0;
            return new Version(major, minor, build, revision);
        }

        return new Version(0, 0, 0, 0);
    }
}

/// <summary>
/// 更新检查结果
/// </summary>
public class UpdateCheckResult
{
    /// <summary>
    /// 是否成功获取更新信息
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 错误消息
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 最新版本号
    /// </summary>
    public Version LatestVersion { get; set; } = new Version(0, 0, 0, 0);

    /// <summary>
    /// 当前版本号
    /// </summary>
    public Version CurrentVersion { get; set; } = new Version(0, 0, 0, 0);

    /// <summary>
    /// 是否有可用更新
    /// </summary>
    public bool HasUpdate { get; set; }

    /// <summary>
    /// 使用的更新源
    /// </summary>
    public string Source { get; set; } = "";

    /// <summary>
    /// 发布信息
    /// </summary>
    public ReleaseInfo? ReleaseInfo { get; set; }
}

/// <summary>
/// 发布信息
/// </summary>
public class ReleaseInfo
{
    /// <summary>
    /// 标签名称 (如 v1.1.0)
    /// </summary>
    public string TagName { get; set; } = "";

    /// <summary>
    /// 发布名称
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// 发布说明
    /// </summary>
    public string Body { get; set; } = "";

    /// <summary>
    /// 更新日志（从 Update.txt 获取）
    /// </summary>
    public string UpdateLog { get; set; } = "";

    /// <summary>
    /// 网页 URL
    /// </summary>
    public string HtmlUrl { get; set; } = "";

    /// <summary>
    /// 发布时间
    /// </summary>
    public DateTime PublishedAt { get; set; }

    /// <summary>
    /// 是否为预发布版本（Debug 版本视为预发布）
    /// </summary>
    public bool IsPrerelease { get; set; }

    /// <summary>
    /// 资产列表
    /// </summary>
    public List<ReleaseAsset> Assets { get; set; } = new();
}

/// <summary>
/// 发布资产
/// </summary>
public class ReleaseAsset
{
    /// <summary>
    /// 资产名称
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// 下载 URL
    /// </summary>
    public string BrowserDownloadUrl { get; set; } = "";

    /// <summary>
    /// 文件大小（字节）
    /// </summary>
    public long Size { get; set; }
}
