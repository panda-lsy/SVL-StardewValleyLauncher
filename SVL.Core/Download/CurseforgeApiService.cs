using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using SVL.Core.Logging;
using SVL.Core.Stardew.Mod.SMAPI;
using SVL.Core.Utils;

namespace SVL.Core.Download;

/// <summary>
/// Curseforge API 服务
/// </summary>
public static class CurseforgeApiService
{
    private static readonly HttpClient _httpClient = new();

    static CurseforgeApiService()
    {
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "SVL-StardewValleyLauncher/1.0");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    // 兼容旧调用链：已不再使用 API Key，保留空实现避免大范围改签名。
    private static void EnsureApiKeyLoaded()
    {
    }

    /// <summary>
    /// 获取 Curseforge 上的 SMAPI 文件列表
    /// </summary>
    /// <param name="gameId">游戏ID（星露谷物语是 1157）</param>
    /// <param name="projectId">SMAPI 项目ID（需要查询）</param>
    /// <param name="index">文件索引，默认0</param>
    /// <param name="pageSize">每页大小，默认50</param>
    public static async Task<List<CurseforgeFile>> GetSmapifiFilesAsync(int index = 0, int pageSize = 50)
    {
        try
        {
            // SMAPI 的 Curseforge 项目 ID（星露谷 Modding API）
            const int projectId = 898372;

            var url = $"https://api.curse.tools/v1/cf/mods/{projectId}/files?index={index}&pageSize={pageSize}";
            Log.Info($"[Curseforge] 获取 SMAPI 文件列表: {url}");

            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<CurseforgeFileListResponse>(json);

            if (result?.Data == null)
            {
                Log.Warn("[Curseforge] 返回的数据为空");
                return new List<CurseforgeFile>();
            }

            Log.Info($"[Curseforge] 获取到 {result.Data.Count} 个 SMAPI 文件");
            return result.Data;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Curseforge] 获取文件列表失败");
            return new List<CurseforgeFile>();
        }
    }

    /// <summary>
    /// 获取文件的直接下载 URL
    /// </summary>
    /// <param name="fileId">文件ID</param>
    public static string GetFileDownloadUrl(int fileId)
    {
        return $"https://www.curseforge.com/api/v1/mods/file/{fileId}/download";
    }

    /// <summary>
    /// 通过 Curseforge API 获取文件的真实下载 URL
    /// 如果普通 API 返回 403，自动尝试使用 CFWidget API
    /// </summary>
    public static async Task<string?> GetFileDownloadUrlAsync(int modId, int fileId)
    {
        try
        {
            var url = $"https://api.curse.tools/v1/cf/mods/{modId}/files/{fileId}/download-url";
            Log.Info($"[Curseforge] 获取文件下载地址: modId={modId}, fileId={fileId}");

            Log.Debug($"[Curseforge] 请求头: User-Agent={_httpClient.DefaultRequestHeaders.UserAgent}, Accept={_httpClient.DefaultRequestHeaders.Accept}");

            var response = await _httpClient.GetAsync(url);

            Log.Debug($"[Curseforge] 响应状态码: {response.StatusCode}, ReasonPhrase: {response.ReasonPhrase}");

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                // 尝试读取响应内容以获取更多错误信息
                var errorContent = await response.Content.ReadAsStringAsync();
                Log.Warn($"[Curseforge] API 返回 403 (Forbidden), ResponseContent: {errorContent.Substring(0, Math.Min(500, errorContent.Length))}");
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Log.Warn($"[Curseforge] API 返回错误: {response.StatusCode}, ResponseContent: {errorContent.Substring(0, Math.Min(500, errorContent.Length))}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            Log.Debug($"[Curseforge] 响应内容: {json.Substring(0, Math.Min(500, json.Length))}...");

            var result = JsonSerializer.Deserialize<CurseforgeDownloadUrlResponse>(json);

            if (string.IsNullOrWhiteSpace(result?.Data))
            {
                Log.Warn($"[Curseforge] 下载地址为空: modId={modId}, fileId={fileId}, ResponseData: {json?.Substring(0, Math.Min(200, json?.Length ?? 0))}");
                return null;
            }

            Log.Info($"[Curseforge] 成功获取下载地址: modId={modId}, fileId={fileId}, Url={result.Data.Substring(0, Math.Min(100, result.Data.Length))}...");
            return result.Data;
        }
        catch (Exception ex)
        {
            Log.Warn($"[Curseforge] 获取文件下载地址失败: modId={modId}, fileId={fileId}, Error: {ex.Message}", ex);
            return null;
        }
    }

    /// <summary>
    /// 构建 Curseforge CDN 直接下载链接（硬解析 fallback）
    /// 规律: https://edge.forgecdn.net/files/{high4}/{low3}/{url_encoded_filename}
    /// 例如: FileId=7448774, FileName="Content Patcher 2.9.0.zip"
    ///      -> https://edge.forgecdn.net/files/7448/774/Content%20Patcher%202.9.0.zip
    /// </summary>
    /// <param name="fileId">文件 ID</param>
    /// <param name="fileName">文件名</param>
    /// <returns>CDN 直接下载链接</returns>
    public static string BuildCdnUrl(int fileId, string fileName)
    {
        var fileIdStr = fileId.ToString();

        // FileId 分割：前4位为高段，剩余为低段
        // 例如 7448774 -> high4=7448, low3=774
        string high4, low3;
        if (fileIdStr.Length >= 4)
        {
            high4 = fileIdStr.Substring(0, 4);
            low3 = fileIdStr.Substring(4);
        }
        else
        {
            // FileId 不足4位时补零
            high4 = fileIdStr.PadLeft(4, '0').Substring(0, 4);
            low3 = "0";
        }

        // URL 编码文件名
        var encodedFileName = System.Uri.EscapeDataString(fileName);

        var cdnUrl = $"https://edge.forgecdn.net/files/{high4}/{low3}/{encodedFileName}";
        Log.Info($"[Curseforge] 构建 CDN URL: fileId={fileId}, fileName={fileName} -> {cdnUrl}");
        return cdnUrl;
    }

    /// <summary>
    /// 解析 Curseforge 文件下载 URL（带完整 Fallback 链）
    /// Fallback 顺序: API -> fallbackUrl -> CDN 硬解析
    /// </summary>
    /// <param name="modId">Mod ID</param>
    /// <param name="fileId">File ID</param>
    /// <param name="fileName">文件名（用于 CDN 硬解析）</param>
    /// <param name="fallbackUrl">备选下载 URL（API 失败时使用）</param>
    /// <returns>下载 URL，如果所有方法都失败则返回 null</returns>
    public static async Task<string?> ResolveFileDownloadUrlAsync(int modId, int fileId, string fileName, string? fallbackUrl = null)
    {
        Log.Info($"[Curseforge] 解析下载 URL: modId={modId}, fileId={fileId}, fileName={fileName}");

        // 1. 尝试通过 API 获取下载链接
        var downloadUrl = await GetFileDownloadUrlAsync(modId, fileId);
        if (!string.IsNullOrWhiteSpace(downloadUrl))
        {
            Log.Info($"[Curseforge] API 成功获取下载链接");
            return downloadUrl;
        }

        // 2. API 失败，尝试使用 fallback URL
        if (!string.IsNullOrWhiteSpace(fallbackUrl))
        {
            Log.Warn($"[Curseforge] API 返回空，尝试使用 fallback URL");
            return fallbackUrl;
        }

        // 3. CDN 硬解析作为最后的 fallback
        Log.Warn($"[Curseforge] API 和 fallback URL 均失败，尝试使用 CDN 硬解析");
        try
        {
            var cdnUrl = BuildCdnUrl(fileId, fileName);
            Log.Warn($"[Curseforge] 使用 CDN 硬解析: {cdnUrl}");
            return cdnUrl;
        }
        catch (Exception ex)
        {
            Log.Warn($"[Curseforge] CDN 硬解析失败: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// 获取 Curseforge Mod 详情（包含 logo）
    /// </summary>
    /// <param name="modId">Mod ID</param>
    public static async Task<CurseforgeModInfo?> GetModInfoAsync(int modId)
    {
        try
        {
            EnsureApiKeyLoaded();

            var url = $"https://api.curse.tools/v1/cf/mods/{modId}";
            Log.Info($"[Curseforge] 获取 Mod 详情: {url}");

            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<CurseforgeModResponse>(json);

            var modInfo = result?.Data;
            if (modInfo != null)
            {
                Log.Info($"[Curseforge] 获取到 Mod 详情: {modInfo.Name}, Logo: {modInfo.Logo?.Url ?? modInfo.Logo?.ThumbnailUrl ?? "(none)"}");
            }
            return modInfo;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[Curseforge] 获取 Mod 详情失败: {modId}");
            return null;
        }
    }

    /// <summary>
    /// 获取 MOD 的文件列表
    /// </summary>
    public static async Task<List<CurseforgeFile>?> GetModFilesAsync(int modId, int index = 0, int pageSize = 100)
    {
        try
        {
            EnsureApiKeyLoaded();

            var url = $"https://api.curse.tools/v1/cf/mods/{modId}/files?index={index}&pageSize={pageSize}";
            Log.Info($"[Curseforge] 获取 Mod 文件列表: {url}");

            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<CurseforgeFilesResponse>(json);

            var files = result?.Data;
            if (files != null)
            {
                Log.Info($"[Curseforge] 获取到 {files.Count} 个文件");

                var dependencies = files
                    .Where(file => file.Dependencies != null && file.Dependencies.Count > 0)
                    .SelectMany(file => file.Dependencies)
                    .ToList();

                if (dependencies.Count > 0)
                {
                    var relationSummary = string.Join(", ",
                        dependencies
                            .GroupBy(dependency => dependency.RelationType)
                            .OrderBy(group => group.Key)
                            .Select(group => $"{group.Key}:{group.Count()}"));

                    Log.Info($"[Curseforge] Mod {modId} 依赖统计: total={dependencies.Count}, relationTypes={relationSummary}");

                    var unknownRelationTypes = dependencies
                        .Select(dependency => dependency.RelationType)
                        .Where(relationType => !Enum.IsDefined(typeof(CurseforgeFileRelationType), relationType))
                        .Distinct()
                        .OrderBy(value => value)
                        .ToList();

                    if (unknownRelationTypes.Count > 0)
                    {
                        Log.Warn($"[Curseforge] Mod {modId} 存在未识别 relationType: {string.Join(", ", unknownRelationTypes)}");
                    }
                }
                else
                {
                    Log.Info($"[Curseforge] Mod {modId} 文件列表中未发现依赖项");
                }
            }
            return files;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[Curseforge] 获取 Mod 文件列表失败: {modId}");
            return null;
        }
    }

    /// <summary>
    /// Curseforge Mod 详情响应
    /// </summary>
    private class CurseforgeModResponse
    {
        [JsonPropertyName("data")]
        public CurseforgeModInfo? Data { get; set; }
    }

    /// <summary>
    /// Curseforge Mod 详情信息
    /// </summary>
    public class CurseforgeModInfo
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("slug")]
        public string Slug { get; set; }

        [JsonPropertyName("links")]
        public CurseforgeModLinks Links { get; set; }

        [JsonPropertyName("logo")]
        public CurseforgeModLogo Logo { get; set; }

        [JsonPropertyName("summary")]
        public string? Summary { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("downloadCount")]
        public long DownloadCount { get; set; }

        [JsonPropertyName("dateModified")]
        public string? DateModified { get; set; }

        [JsonPropertyName("dateCreated")]
        public string? DateCreated { get; set; }

        [JsonPropertyName("dateReleased")]
        public string? DateReleased { get; set; }

        [JsonPropertyName("categories")]
        public List<CurseforgeCategory>? Categories { get; set; }

        [JsonPropertyName("latestFile")]
        public CurseforgeLatestFile? LatestFile { get; set; }

        [JsonPropertyName("mainFileId")]
        public int MainFileId { get; set; }

        [JsonPropertyName("gameId")]
        public int GameId { get; set; }

        [JsonPropertyName("gameVersionLatestFiles")]
        public List<object>? GameVersionLatestFiles { get; set; }

        [JsonPropertyName("gamePopularityRank")]
        public int GamePopularityRank { get; set; }

        [JsonPropertyName("isAvailable")]
        public bool IsAvailable { get; set; }

        [JsonPropertyName("thumbsUpCount")]
        public int ThumbsUpCount { get; set; }

        [JsonPropertyName("authors")]
        public List<CurseforgeAuthor>? Authors { get; set; }

        [JsonPropertyName("relations")]
        public List<CurseforgeModRelation>? Relations { get; set; }
    }

    public class CurseforgeModRelation
    {
        [JsonPropertyName("modId")]
        public int ModId { get; set; }

        [JsonPropertyName("relationType")]
        public int RelationType { get; set; }
    }

    /// <summary>
    /// Curseforge 作者信息
    /// </summary>
    public class CurseforgeAuthor
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }

    /// <summary>
    /// Curseforge Mod 链接
    /// </summary>
    public class CurseforgeModLinks
    {
        [JsonPropertyName("websiteUrl")]
        public string WebsiteUrl { get; set; }

        [JsonPropertyName("wikiUrl")]
        public string WikiUrl { get; set; }

        [JsonPropertyName("issuesUrl")]
        public string IssuesUrl { get; set; }

        [JsonPropertyName("sourceUrl")]
        public string SourceUrl { get; set; }
    }

    /// <summary>
    /// Curseforge Mod Logo
    /// </summary>
    public class CurseforgeModLogo
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("modId")]
        public int ModId { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("thumbnailUrl")]
        public string ThumbnailUrl { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; }
    }


    /// <summary>
    /// Curseforge 文件列表响应
    /// </summary>
    private class CurseforgeFileListResponse
    {
        [JsonPropertyName("data")]
        public List<CurseforgeFile> Data { get; set; }

        [JsonPropertyName("pagination")]
        public CurseforgePagination Pagination { get; set; }
    }

    /// <summary>
    /// Curseforge 分页信息
    /// </summary>
    public class CurseforgePagination
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("pageSize")]
        public int PageSize { get; set; }

        [JsonPropertyName("totalCount")]
        public int TotalCount { get; set; }

        [JsonPropertyName("resultCount")]
        public int ResultCount { get; set; }
    }

    /// <summary>
    /// Curseforge MOD 搜索结果
    /// </summary>
    public class CurseforgeModSearchResult
    {
        [JsonPropertyName("data")]
        public List<CurseforgeModSearchItem> Data { get; set; }

        [JsonPropertyName("pagination")]
        public CurseforgePagination Pagination { get; set; }
    }

    /// <summary>
    /// Curseforge MOD 搜索项
    /// </summary>
    public class CurseforgeModSearchItem
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("slug")]
        public string Slug { get; set; }

        [JsonPropertyName("summary")]
        public string Summary { get; set; }

        [JsonPropertyName("downloadCount")]
        public long DownloadCount { get; set; }

        [JsonPropertyName("categories")]
        public List<CurseforgeCategory> Categories { get; set; }

        [JsonPropertyName("logo")]
        public CurseforgeModLogo Logo { get; set; }

        [JsonPropertyName("links")]
        public CurseforgeModLinks Links { get; set; }

        [JsonPropertyName("latestFile")]
        public CurseforgeLatestFile LatestFile { get; set; }

        [JsonPropertyName("dateModified")]
        public string DateModified { get; set; }
    }

    /// <summary>
    /// Curseforge 分类
    /// </summary>
    public class CurseforgeCategory
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("gameId")]
        public int GameId { get; set; }

        [JsonPropertyName("classId")]
        public int ClassId { get; set; }

        [JsonPropertyName("isClass")]
        public bool IsClassId { get; set; }

        [JsonPropertyName("parentCategoryId")]
        public int ParentCategoryId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("slug")]
        public string Slug { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; }
    }

    public class CurseforgeCategoriesResult
    {
        [JsonPropertyName("data")]
        public List<CurseforgeCategory> Data { get; set; }
    }

    /// <summary>
    /// Curseforge 最新文件信息
    /// </summary>
    public class CurseforgeLatestFile
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; }

        [JsonPropertyName("fileName")]
        public string FileName { get; set; }

        [JsonPropertyName("fileDate")]
        public DateTime FileDate { get; set; }

        [JsonPropertyName("fileLength")]
        public long FileLength { get; set; }

        [JsonPropertyName("releaseType")]
        public int ReleaseType { get; set; }

        [JsonPropertyName("gameVersion")]
        public List<string> GameVersion { get; set; }
    }

    /// <summary>
    /// 获取特色模组（热门、最近更新等）
    /// </summary>
    public static async Task<CurseforgeFeaturedModsResponse?> GetFeaturedModsAsync(
        int gameId = 669,  // Stardew Valley 在 Curseforge 上的游戏 ID
        int pageSize = 50)
    {
        try
        {
            EnsureApiKeyLoaded();

            var cacheKey = $"featured|gameId={gameId}|ps={pageSize}";
            if (SVL.Core.IO.SearchCacheService.TryGet<CurseforgeFeaturedModsResponse>("curseforge", cacheKey, out var cached))
            {
                return cached;
            }

            var url = "https://api.curse.tools/v1/cf/mods/featured";
            Log.Info($"[Curseforge] 获取特色模组 URL: {url}");

            // 构建请求体 - 参考官方 API 文档
            var requestBody = new
            {
                gameId = gameId,
                excludedModIds = new int[0],  // 不排除任何模组
                pageSize = pageSize
                // 注意：不包含 gameVersionTypeId，这可能导致问题
            };

            var jsonContent = JsonSerializer.Serialize(requestBody);
            Log.Info($"[Curseforge] 请求体: {jsonContent}");

            var content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(url, content);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Log.Warn($"[Curseforge] 获取特色模组失败: {response.StatusCode}, Content: {errorContent}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            Log.Info($"[Curseforge] 特色模组 API 响应长度: {json.Length}, 响应内容: {json}");

            var result = JsonSerializer.Deserialize<CurseforgeFeaturedModsResponse>(json);

            var totalMods = (result?.Data?.Featured?.Count ?? 0) +
                           (result?.Data?.Popular?.Count ?? 0) +
                           (result?.Data?.RecentlyUpdated?.Count ?? 0);
            Log.Info($"[Curseforge] 获取到特色模组 - Featured: {result?.Data?.Featured?.Count ?? 0}, Popular: {result?.Data?.Popular?.Count ?? 0}, RecentlyUpdated: {result?.Data?.RecentlyUpdated?.Count ?? 0}");

            if (result != null)
            {
                await SVL.Core.IO.SearchCacheService.SetAsync("curseforge", cacheKey, result);
            }

            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Curseforge] 获取特色模组失败");
            return null;
        }
    }

    /// <summary>
    /// 搜索 MOD（从 Curseforge）
    /// </summary>
    public static async Task<List<CurseforgeModSearchItem>> SearchModsAsync(
        string searchQuery,
        int gameId = 669,  // Stardew Valley 在 Curseforge 上的游戏 ID
        int pageSize = 50,
        int index = 0,
        string? gameVersion = null)
    {
        try
        {
            EnsureApiKeyLoaded();

            searchQuery ??= string.Empty;
            gameVersion = string.IsNullOrWhiteSpace(gameVersion) || string.Equals(gameVersion, "全部", StringComparison.OrdinalIgnoreCase)
                ? null
                : gameVersion.Trim();
            pageSize = Math.Max(1, Math.Min(50, pageSize));
            var cacheKey = $"search|q={searchQuery.Trim()}|gameId={gameId}|ps={pageSize}|idx={index}|gv={gameVersion ?? string.Empty}";
            if (SVL.Core.IO.SearchCacheService.TryGet<List<CurseforgeModSearchItem>>("curseforge", cacheKey, out var cached))
            {
                return cached ?? new List<CurseforgeModSearchItem>();
            }

            // 构建搜索 URL
            // 注意：Curseforge API 的空搜索需要特殊处理
            var url = $"https://api.curse.tools/v1/cf/mods/search?gameId={gameId}&pageSize={pageSize}&index={index}&sortField=2&sortOrder=desc";

            // 只有在 searchQuery 不为空时才添加 searchFilter 参数
            if (!string.IsNullOrWhiteSpace(searchQuery))
            {
                var encodedQuery = Uri.EscapeDataString(searchQuery);
                url += $"&searchFilter={encodedQuery}";
            }

            if (!string.IsNullOrWhiteSpace(gameVersion))
            {
                var encodedGameVersion = Uri.EscapeDataString(gameVersion);
                url += $"&gameVersion={encodedGameVersion}";
            }

            Log.Info($"[Curseforge] 搜索 URL: {url}");

            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Log.Warn($"[Curseforge] 搜索 MOD 失败: {response.StatusCode}, Content: {errorContent}");
                return new List<CurseforgeModSearchItem>();
            }

            var json = await response.Content.ReadAsStringAsync();
            Log.Info($"[Curseforge] API 响应长度: {json.Length}, 前200字符: {json.Substring(0, Math.Min(200, json.Length))}...");

            var result = JsonSerializer.Deserialize<CurseforgeModSearchResult>(json);

            var mods = result?.Data ?? new List<CurseforgeModSearchItem>();
            Log.Info($"[Curseforge] 搜索到 {mods.Count} 个 MOD");

            await SVL.Core.IO.SearchCacheService.SetAsync("curseforge", cacheKey, mods);

            return mods;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Curseforge] 搜索 MOD 失败");
            return new List<CurseforgeModSearchItem>();
        }
    }

    public static async Task<List<string>> GetGameVersionsAsync(int gameId = 669)
    {
        try
        {
            EnsureApiKeyLoaded();

            var cacheKey = $"game-versions|gameId={gameId}";
            if (SVL.Core.IO.SearchCacheService.TryGet<List<string>>("curseforge", cacheKey, out var cached))
            {
                return cached ?? new List<string>();
            }

            var versions = await TryGetGameVersionsV2Async(gameId);
            if (versions.Count == 0)
                versions = await TryGetGameVersionsV1Async(gameId);

            versions = versions
                .Where(version => !string.IsNullOrWhiteSpace(version))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(version => version, SemanticVersionComparer.Instance)
                .ToList();

            if (versions.Count > 0)
            {
                await SVL.Core.IO.SearchCacheService.SetAsync("curseforge", cacheKey, versions);
            }

            return versions;
        }
        catch (Exception ex)
        {
            Log.Warn("[Curseforge] 获取游戏版本列表失败", ex);
            return new List<string>();
        }
    }

    private static async Task<List<string>> TryGetGameVersionsV2Async(int gameId)
    {
        var url = $"https://api.curseforge.com/v2/games/{gameId}/versions";
        var response = await _httpClient.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Log.Warn($"[Curseforge] 获取游戏版本 V2 失败: {response.StatusCode}, Content: {body}");
            return new List<string>();
        }

        var result = JsonSerializer.Deserialize<CurseforgeGameVersionsV2Response>(body);
        return result?.Data?
            .SelectMany(item => item.Versions ?? new List<CurseforgeGameVersionItem>())
            .Select(item => string.IsNullOrWhiteSpace(item.Name) ? item.Slug : item.Name)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList() ?? new List<string>();
    }

    private static async Task<List<string>> TryGetGameVersionsV1Async(int gameId)
    {
        var url = $"https://api.curseforge.com/v1/games/{gameId}/versions";
        var response = await _httpClient.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Log.Warn($"[Curseforge] 获取游戏版本 V1 失败: {response.StatusCode}, Content: {body}");
            return new List<string>();
        }

        var result = JsonSerializer.Deserialize<CurseforgeGameVersionsV1Response>(body);
        return result?.Data?
            .SelectMany(item => item.Versions ?? new List<string>())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList() ?? new List<string>();
    }

    public static async Task<string?> GetGameDependenciesRawAsync(int gameId = 669)
    {
        try
        {
            EnsureApiKeyLoaded();
            var url = $"https://api.curseforge.com/v1/games/{gameId}/dependencies";
            Log.Info($"[Curseforge] 获取游戏依赖定义: {url}");

            var response = await _httpClient.GetAsync(url);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"[Curseforge] 获取游戏依赖定义失败: {response.StatusCode}, Content: {body}");
                return null;
            }

            Log.Info($"[Curseforge] 获取游戏依赖定义成功，长度={body.Length}");
            return body;
        }
        catch (Exception ex)
        {
            Log.Warn($"[Curseforge] 获取游戏依赖定义失败: {ex.Message}");
            return null;
        }
    }

    public static async Task<List<CurseforgeModSearchItem>> SearchModpacksAsync(
        string searchQuery,
        int gameId = 669,
        int pageSize = 50,
        int index = 0)
    {
        try
        {
            EnsureApiKeyLoaded();

            searchQuery ??= string.Empty;
            pageSize = Math.Max(1, Math.Min(50, pageSize));
            var modpackClassId = await TryGetModpackClassIdAsync(gameId);

            var cacheKey = $"search-modpack|q={searchQuery.Trim()}|gameId={gameId}|ps={pageSize}|idx={index}|class={modpackClassId}";
            if (SVL.Core.IO.SearchCacheService.TryGet<List<CurseforgeModSearchItem>>("curseforge", cacheKey, out var cached))
            {
                return cached ?? new List<CurseforgeModSearchItem>();
            }

            var url = $"https://api.curse.tools/v1/cf/mods/search?gameId={gameId}&pageSize={pageSize}&index={index}&sortField=2&sortOrder=desc";

            if (!string.IsNullOrWhiteSpace(searchQuery))
            {
                var encodedQuery = Uri.EscapeDataString(searchQuery);
                url += $"&searchFilter={encodedQuery}";
            }

            if (modpackClassId > 0)
            {
                url += $"&classId={modpackClassId}";
            }

            Log.Info($"[Curseforge] 搜索 Modpacks URL: {url}");

            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Log.Warn($"[Curseforge] 搜索 Modpacks 失败: {response.StatusCode}, Content: {errorContent}");
                return new List<CurseforgeModSearchItem>();
            }

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<CurseforgeModSearchResult>(json);
            var modpacks = result?.Data ?? new List<CurseforgeModSearchItem>();

            if (modpackClassId <= 0)
            {
                modpacks = modpacks.Where(IsLikelyModpack).ToList();
            }

            await SVL.Core.IO.SearchCacheService.SetAsync("curseforge", cacheKey, modpacks);

            Log.Info($"[Curseforge] 搜索到 {modpacks.Count} 个 Modpacks");
            return modpacks;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Curseforge] 搜索 Modpacks 失败");
            return new List<CurseforgeModSearchItem>();
        }
    }

    private static async Task<int> TryGetModpackClassIdAsync(int gameId)
    {
        try
        {
            var cacheKey = $"modpack-class-id|gameId={gameId}";
            if (SVL.Core.IO.SearchCacheService.TryGet<int>("curseforge", cacheKey, out var cachedId) && cachedId > 0)
            {
                return cachedId;
            }

            var url = $"https://api.curse.tools/v1/cf/categories?gameId={gameId}";
            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                return 0;
            }

            var json = await response.Content.ReadAsStringAsync();
            var categoryResult = JsonSerializer.Deserialize<CurseforgeCategoriesResult>(json);
            var categories = categoryResult?.Data ?? new List<CurseforgeCategory>();

            var modpackClass = categories.FirstOrDefault(c =>
                (c.IsClassId || c.ParentCategoryId == 0) &&
                (string.Equals(c.Slug, "modpacks", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(c.Name, "Modpacks", StringComparison.OrdinalIgnoreCase)));

            if (modpackClass?.Id > 0)
            {
                await SVL.Core.IO.SearchCacheService.SetAsync("curseforge", cacheKey, modpackClass.Id);
                return modpackClass.Id;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("[Curseforge] 解析 Modpack classId 失败，将使用兼容筛选", ex);
        }

        return 0;
    }

    private static bool IsLikelyModpack(CurseforgeModSearchItem mod)
    {
        if (mod == null)
            return false;

        if ((mod.Name?.IndexOf("modpack", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
            return true;

        if ((mod.Summary?.IndexOf("modpack", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
            return true;

        return mod.Categories?.Any(c =>
            (c?.Name?.IndexOf("modpack", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
            (c?.Slug?.IndexOf("modpack", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
            (c?.Name?.IndexOf("collection", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
            (c?.Slug?.IndexOf("collection", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
        ) == true;
    }

    /// <summary>
    /// 特色模组响应
    /// </summary>
    public class CurseforgeFeaturedModsResponse
    {
        [JsonPropertyName("data")]
        public CurseforgeFeaturedModsData? Data { get; set; }
    }

    /// <summary>
    /// 特色模组数据
    /// </summary>
    public class CurseforgeFeaturedModsData
    {
        [JsonPropertyName("featured")]
        public List<CurseforgeModSearchItem>? Featured { get; set; }

        [JsonPropertyName("popular")]
        public List<CurseforgeModSearchItem>? Popular { get; set; }

        [JsonPropertyName("recentlyUpdated")]
        public List<CurseforgeModSearchItem>? RecentlyUpdated { get; set; }
    }
}

/// <summary>
/// Curseforge 文件信息
/// </summary>
public class CurseforgeFile
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("gameId")]
    public int GameId { get; set; }

    [JsonPropertyName("modId")]
    public int ModId { get; set; }

    [JsonPropertyName("isAvailable")]
    public bool IsAvailable { get; set; }

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; }

    [JsonPropertyName("fileName")]
    public string FileName { get; set; }

    [JsonPropertyName("fileDate")]
    public DateTime FileDate { get; set; }

    [JsonPropertyName("fileLength")]
    public long FileLength { get; set; }

    [JsonPropertyName("releaseType")]
    public int ReleaseType { get; set; }

    [JsonPropertyName("fileStatus")]
    public int FileStatus { get; set; }

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; }

    [JsonPropertyName("isAlternate")]
    public bool IsAlternate { get; set; }

    [JsonPropertyName("alternateFileId")]
    public int AlternateFileId { get; set; }

    [JsonPropertyName("dependencies")]
    public List<CurseforgeFileDependency> Dependencies { get; set; }

    [JsonPropertyName("isEarlyAccessContent")]
    public bool IsEarlyAccessContent { get; set; }

    [JsonPropertyName("earlyAccessEndDate")]
    public DateTime EarlyAccessEndDate { get; set; }

    [JsonPropertyName("gameVersions")]
    public List<string> GameVersions { get; set; }

    [JsonPropertyName("modules")]
    public List<CurseforgeModule> Modules { get; set; }

    [JsonPropertyName("downloadCount")]
    public long DownloadCount { get; set; }
}

public class CurseforgeFileDependency
{
    [JsonPropertyName("modId")]
    public int ModId { get; set; }

    [JsonPropertyName("addonId")]
    public int AddonId { get; set; }

    [JsonPropertyName("relationType")]
    public int RelationType { get; set; }

    [JsonPropertyName("required")]
    public bool? Required { get; set; }
}

public enum CurseforgeFileRelationType
{
    EmbeddedLibrary = 1,
    OptionalDependency = 2,
    RequiredDependency = 3,
    Tool = 4,
    Incompatible = 5,
    Include = 6
}

/// <summary>
/// Curseforge 文件列表响应
/// </summary>
public class CurseforgeFilesResponse
{
    [JsonPropertyName("data")]
    public List<CurseforgeFile> Data { get; set; }
}

public class CurseforgeGameVersionsV1Response
{
    [JsonPropertyName("data")]
    public List<CurseforgeGameVersionsByTypeV1>? Data { get; set; }
}

public class CurseforgeGameVersionsByTypeV1
{
    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("versions")]
    public List<string>? Versions { get; set; }
}

public class CurseforgeGameVersionsV2Response
{
    [JsonPropertyName("data")]
    public List<CurseforgeGameVersionsByTypeV2>? Data { get; set; }
}

public class CurseforgeGameVersionsByTypeV2
{
    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("versions")]
    public List<CurseforgeGameVersionItem>? Versions { get; set; }
}

public class CurseforgeGameVersionItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public class CurseforgeDownloadUrlResponse
{
    [JsonPropertyName("data")]
    public string? Data { get; set; }
}

/// <summary>
/// Curseforge 模块信息
/// </summary>
public class CurseforgeModule
{
    [JsonPropertyName("foldername")]
    public string FolderName { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("fingerprint")]
    public long Fingerprint { get; set; }

    [JsonPropertyName("type")]
    public int Type { get; set; }
}
