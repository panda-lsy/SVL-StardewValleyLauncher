using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using SVL.Core.Logging;

namespace SVL.Core.Download.NexusMods;

/// <summary>
/// Nexus Mods REST API客户端
/// </summary>
public class NexusApiClient : IDisposable
{
    private const string BaseUrl = "https://api.nexusmods.com/v1";
    private readonly string _accessToken;
    private readonly HttpClient _httpClient;

    public NexusApiClient(string accessToken)
    {
        _accessToken = accessToken ?? throw new ArgumentNullException(nameof(accessToken));
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_accessToken}");
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "SVL-StardewLauncher/1.0 (+https://github.com/yourusername/SVL)");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// 验证API Token（通过UserInfo端点）
    /// </summary>
    public async Task<NexusUserInfo> ValidateTokenAsync()
    {
        Log.Info("[NexusApi] 验证Token（UserInfo端点）");

        try
        {
            // 使用 OAuth UserInfo 端点
            var response = await _httpClient.GetAsync("https://users.nexusmods.com/oauth/userinfo");

            Log.Info($"[NexusApi] UserInfo 响应状态: {response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Log.Error($"[NexusApi] UserInfo 验证失败: {response.StatusCode} - {errorContent}");
                throw new HttpRequestException($"Token验证失败: {response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync();
            Log.Debug($"[NexusApi] UserInfo 响应内容: {json}");

            // UserInfo 端点返回用户信息
            var userInfo = JsonSerializer.Deserialize<NexusUserInfo>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            Log.Info($"[NexusApi] Token验证成功，用户: {userInfo?.Name}, 头像: {userInfo?.Avatar}");
            return userInfo;
        }
        catch (HttpRequestException ex)
        {
            Log.Error(ex, "[NexusApi] Token验证HTTP请求失败");
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NexusApi] Token验证发生未知错误");
            throw;
        }
    }

    /// <summary>
    /// 获取Mod信息
    /// </summary>
    public async Task<NexusModInfo> GetModInfoAsync(string gameId, long modId)
    {
        Log.Info($"[NexusApi] 获取Mod信息: game={gameId}, modId={modId}");

        var response = await _httpClient.GetAsync($"{BaseUrl}/games/{gameId}/mods/{modId}.json");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var modInfo = JsonSerializer.Deserialize<NexusModInfo>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Log.Info($"[NexusApi] 获取Mod信息成功: {modInfo?.Name}");
        return modInfo;
    }

    /// <summary>
    /// 获取Mod文件列表
    /// </summary>
    public async Task<List<NexusFileInfo>> GetModFilesAsync(string gameId, long modId)
    {
        Log.Info($"[NexusApi] 获取Mod文件列表: game={gameId}, modId={modId}");

        var response = await _httpClient.GetAsync($"{BaseUrl}/games/{gameId}/mods/{modId}/files.json");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var filesResponse = JsonSerializer.Deserialize<NexusFilesResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        var files = filesResponse?.Files ?? new List<NexusFileInfo>();
        Log.Info($"[NexusApi] 获取到 {files.Count} 个文件");
        return files;
    }

    /// <summary>
    /// 获取文件详情
    /// </summary>
    public async Task<NexusFileInfo> GetFileInfoAsync(string gameId, long modId, long fileId)
    {
        Log.Info($"[NexusApi] 获取文件详情: game={gameId}, modId={modId}, fileId={fileId}");

        var response = await _httpClient.GetAsync($"{BaseUrl}/games/{gameId}/mods/{modId}/files/{fileId}.json");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        NexusFileInfo? fileInfo = null;
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        try
        {
            fileInfo = JsonSerializer.Deserialize<NexusFileInfo>(json, options);
        }
        catch (JsonException ex)
        {
            Log.Warn($"[NexusApi] 文件详情反序列化失败，尝试兼容解析: modId={modId}, fileId={fileId}", ex);

            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            fileInfo = new NexusFileInfo
            {
                Id = root.TryGetProperty("id", out var idEl) ? idEl : default,
                ModId = root.TryGetProperty("mod_id", out var modEl) && modEl.TryGetInt64(out var modIdParsed) ? modIdParsed : modId,
                GameId = root.TryGetProperty("game_id", out var gameEl) && gameEl.TryGetInt64(out var gameIdParsed) ? gameIdParsed : 0,
                Name = root.TryGetProperty("name", out var nameEl) ? (nameEl.GetString() ?? string.Empty) : string.Empty,
                Version = root.TryGetProperty("version", out var versionEl) ? (versionEl.GetString() ?? string.Empty) : string.Empty,
                FileName = root.TryGetProperty("file_name", out var fileNameEl) ? (fileNameEl.GetString() ?? string.Empty) : string.Empty,
                Description = root.TryGetProperty("description", out var descEl) ? (descEl.GetString() ?? string.Empty) : string.Empty,
                Dependencies = new List<NexusDependency>()
            };
        }

        Log.Info($"[NexusApi] 获取文件详情成功: {fileInfo?.Name}");
        return fileInfo ?? new NexusFileInfo();
    }

    /// <summary>
    /// 获取下载链接（Premium 用户直接下载）
    /// </summary>
    public async Task<string> GetDownloadLinkAsync(string gameId, long modId, long fileId)
    {
        Log.Info($"[NexusApi] 获取下载链接: game={gameId}, modId={modId}, fileId={fileId}");

        var response = await _httpClient.GetAsync($"{BaseUrl}/games/{gameId}/mods/{modId}/files/{fileId}/download_link.json");

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            Log.Error($"[NexusApi] 获取下载链接失败: {response.StatusCode} - {errorContent}");

            if ((int)response.StatusCode == 403)
            {
                var message = string.IsNullOrWhiteSpace(errorContent)
                    ? "You don't have permission to get download links from the API without visiting nexusmods.com (premium users only)."
                    : errorContent;

                throw new NexusPremiumRequiredException(
                    gameId,
                    modId,
                    fileId,
                    $"Nexus API 403（需浏览器手动下载）: {message}");
            }

            response.EnsureSuccessStatusCode();
        }

        var json = await response.Content.ReadAsStringAsync();
        var links = JsonSerializer.Deserialize<List<NexusDownloadLinkResponse>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        var downloadUrl = links?[0]?.Uri;
        if (string.IsNullOrEmpty(downloadUrl))
            throw new InvalidOperationException("未获取到下载链接");

        Log.Info($"[NexusApi] 获取下载链接成功");
        return downloadUrl;
    }

    /// <summary>
    /// 使用 NXM key 获取下载链接（支持非 Premium 用户）
    /// </summary>
    /// <param name="gameId">游戏域名（如 stardewvalley）</param>
    /// <param name="modId">Mod ID</param>
    /// <param name="fileId">文件 ID</param>
    /// <param name="nxmKey">从 NXM URL 提取的 key</param>
    /// <param name="expires">从 NXM URL 提取的过期时间（可选）</param>
    /// <returns>下载链接列表（支持备用 CDN）</returns>
    public async Task<List<string>> GetDownloadLinkWithKeyAsync(string gameId, long modId, long fileId, string nxmKey, string? expires = null)
    {
        // 验证 NXM key 格式
        if (string.IsNullOrEmpty(nxmKey) || nxmKey.Length < 20)
        {
            Log.Error($"[NexusApi] NXM key 格式无效: {nxmKey} (长度: {nxmKey?.Length ?? 0})");
            throw new ArgumentException("NXM key 格式无效", nameof(nxmKey));
        }

        // 检查 key 是否过期
        if (!string.IsNullOrEmpty(expires) && long.TryParse(expires, out var expiryTimestamp))
        {
            var expiryDate = DateTimeOffset.FromUnixTimeSeconds(expiryTimestamp);
            if (DateTimeOffset.UtcNow > expiryDate)
            {
                Log.Warn($"[NexusApi] NXM key 已过期: {expiryDate:yyyy-MM-dd HH:mm:ss} UTC");
                throw new InvalidOperationException("NXM key 已过期，请重新从浏览器获取下载链接");
            }
            Log.Info($"[NexusApi] NXM key 有效期至: {expiryDate:yyyy-MM-dd HH:mm:ss} UTC");
        }

        Log.Info($"[NexusApi] 使用 NXM key 获取下载链接: game={gameId}, modId={modId}, fileId={fileId}");

        // 构造 API 端点，NXM key 作为查询参数
        var endpoint = $"{BaseUrl}/games/{gameId}/mods/{modId}/files/{fileId}/download_link.json?key={nxmKey}";

        var response = await _httpClient.GetAsync(endpoint);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            Log.Error($"[NexusApi] 使用 NXM key 获取下载链接失败: {response.StatusCode} - {errorContent}");

            // 检查是否为 403 错误（key 被拒绝）
            if ((int)response.StatusCode == 403)
            {
                throw new InvalidOperationException("NXM key 被拒绝（403），请重新从浏览器获取下载链接");
            }

            response.EnsureSuccessStatusCode();
        }

        var json = await response.Content.ReadAsStringAsync();
        var links = JsonSerializer.Deserialize<List<NexusDownloadLinkResponse>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (links == null || links.Count == 0)
            throw new InvalidOperationException("未获取到下载链接");

        // 提取所有 CDN URL
        var downloadUrls = new List<string>();
        foreach (var link in links)
        {
            if (!string.IsNullOrEmpty(link.Uri))
            {
                downloadUrls.Add(link.Uri);
            }
        }

        Log.Info($"[NexusApi] 使用 NXM key 获取到 {downloadUrls.Count} 个下载链接");
        return downloadUrls;
    }

    /// <summary>
    /// 搜索Mods
    /// </summary>
    public async Task<List<NexusModInfo>> SearchModsAsync(string gameId, string query, int limit = 20)
    {
        Log.Info($"[NexusApi] 搜索Mods: game={gameId}, query={query}, limit={limit}");

        // Nexus Mods API不直接支持搜索，这里返回空列表
        // 实际搜索需要通过网站或其他方式
        Log.Warn("[NexusApi] Nexus Mods REST API不支持直接搜索");
        return new List<NexusModInfo>();
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}
