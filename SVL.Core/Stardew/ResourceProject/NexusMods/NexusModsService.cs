using System;
using SVL.Core.IO;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SVL.Core.Logging;
using SVL.Core.Stardew.ResourceProject.NexusMods;

namespace SVL.Core.Stardew.ResourceProject.NexusMods;

public class NexusModsService
{
    public sealed class NexusModFileMetadata
    {
        public long DownloadCount { get; set; }

        public List<NexusModFileRequirement> Requirements { get; set; } = new();
    }

    private static readonly string _cachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SVL",
        "cache",
        "nexusmods"
    );

    private static readonly Dictionary<string, NexusMod> _modCache = new();

    // 搜索结果缓存（按 query 维度）。用于页面返回/重复搜索时减少 GraphQL 调用。
    private static readonly Dictionary<string, List<NexusMod>> _searchCache = new(StringComparer.OrdinalIgnoreCase);

    // 文件元数据缓存：modId -> (fileId -> metadata)
    private static readonly Dictionary<long, Dictionary<long, NexusModFileMetadata>> _fileMetadataCache = new();

    /// <summary>
    /// 搜索 NexusMods 模组
    /// </summary>
    /// <param name="query">搜索关键词</param>
    /// <param name="page">页码（从 1 开始）</param>
    /// <param name="pageSize">每页数量</param>
    /// <param name="useCache">是否使用缓存</param>
    public static async Task<List<NexusMod>> SearchModsAsync(string query, int page = 1, int pageSize = 20, bool useCache = true)
    {
        // 分页搜索不使用缓存
        if (page > 1 || pageSize != 20)
        {
            useCache = false;
        }

        query = query ?? string.Empty;
        var cacheKey = query.Trim();
        var cacheKeyWithPaging = $"q={cacheKey}|p={page}|ps={pageSize}";

        if (useCache)
        {
            // 先读通用缓存（可统计/可过期）
            if (SVL.Core.IO.SearchCacheService.TryGet<List<NexusMod>>("nexus", "search|" + cacheKeyWithPaging, out var cached))
            {
                return cached ?? new List<NexusMod>();
            }

            // 再读进程内缓存
            if (_searchCache.TryGetValue(cacheKeyWithPaging, out var cachedResults) && cachedResults != null && cachedResults.Count > 0)
            {
                Log.Info($"[NexusModsService] Using in-memory cached search results for: '{cacheKeyWithPaging}'");
                return cachedResults;
            }
        }

        var mods = await NexusModsClient.SearchModsAsync(query, page, pageSize);

        foreach (var mod in mods)
        {
            // 防御：避免把 GraphQL 反序列化失败导致的 modId=0 写进缓存
            if (mod.ModId > 0)
            {
                _modCache[mod.ModId.ToString()] = mod;
            }
        }

        // 只有成功获取到数据时才缓存（空列表可能是请求失败导致的）
        if (useCache && mods.Count > 0)
        {
            _searchCache[cacheKeyWithPaging] = mods;
            await SVL.Core.IO.SearchCacheService.SetAsync("nexus", "search|" + cacheKeyWithPaging, mods);
            await SaveCacheAsync();
        }

        return mods;
    }

    public static async Task<List<NexusCollection>> SearchCollectionsAsync(string query, int page = 1, int pageSize = 20, bool useCache = true)
    {
        var normalized = query ?? string.Empty;
        var cacheKey = $"collections|q={normalized.Trim()}|p={page}|ps={pageSize}";

        if (useCache && SearchCacheService.TryGet<List<NexusCollection>>("nexus", cacheKey, out var cached))
        {
            return cached ?? new List<NexusCollection>();
        }

        var collections = await NexusModsClient.SearchCollectionsAsync(normalized, page, pageSize);

        // 只有成功获取到数据时才缓存（空列表可能是请求失败导致的）
        if (useCache && collections.Count > 0)
        {
            await SearchCacheService.SetAsync("nexus", cacheKey, collections);
        }

        return collections;
    }

    /// <summary>
    /// 获取 Collection 的所有 Revisions（版本列表）
    /// </summary>
    /// <param name="collectionSlug">Collection 的 Slug（如 g3i395）</param>
    /// <param name="useCache">是否使用缓存</param>
    public static async Task<List<NexusCollectionRevision>> GetAllCollectionRevisionsAsync(string collectionSlug, bool useCache = true)
    {
        var cacheKey = $"collection_revisions|slug={collectionSlug}|all=true";

        if (useCache && SearchCacheService.TryGet<List<NexusCollectionRevision>>("nexus", cacheKey, out var cached))
        {
            return cached ?? new List<NexusCollectionRevision>();
        }

        var revisions = await NexusModsClient.GetAllCollectionRevisionsAsync(collectionSlug);

        // 只有成功获取到数据时才缓存（空列表可能是请求失败导致的）
        if (useCache && revisions.Count > 0)
        {
            await SearchCacheService.SetAsync("nexus", cacheKey, revisions);
        }

        return revisions;
    }

    /// <summary>
    /// 获取 Collection 的 Revisions（分页）
    /// </summary>
    /// <param name="collectionSlug">Collection 的 Slug（如 g3i395）</param>
    /// <param name="page">页码（从 1 开始）</param>
    /// <param name="pageSize">每页数量</param>
    /// <param name="useCache">是否使用缓存</param>
    public static async Task<List<NexusCollectionRevision>> GetCollectionRevisionsAsync(string collectionSlug, int page = 1, int pageSize = 20, bool useCache = true)
    {
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Max(1, pageSize);
        var cacheKey = $"collection_revisions|slug={collectionSlug}|p={safePage}|ps={safePageSize}";

        if (useCache && SearchCacheService.TryGet<List<NexusCollectionRevision>>("nexus", cacheKey, out var cached))
        {
            return cached ?? new List<NexusCollectionRevision>();
        }

        var revisions = await NexusModsClient.GetCollectionRevisionsAsync(collectionSlug, page: safePage, pageSize: safePageSize);

        // 只有成功获取到数据时才缓存（空列表可能是请求失败导致的）
        if (useCache && revisions.Count > 0)
        {
            await SearchCacheService.SetAsync("nexus", cacheKey, revisions);
        }

        return revisions;
    }

    /// <summary>
    /// 获取 Collection 的 Revisions（Relay 风格游标分页）
    /// </summary>
    /// <param name="collectionSlug">Collection 的 Slug（如 g3i395）</param>
    /// <param name="first">返回的元素数量（默认 5）</param>
    /// <param name="after">游标字符串（用于获取下一页）</param>
    /// <param name="gameDomain">游戏域名（默认为 stardewvalley）</param>
    public static async Task<CollectionRevisionsPagedResult> GetCollectionRevisionsPagedAsync(
        string collectionSlug,
        int first = 5,
        string? after = null,
        string? gameDomain = null)
    {
        // 游标分页不使用缓存，因为数据是动态的
        var result = await NexusModsClient.GetCollectionRevisionsPagedAsync(
            collectionSlug,
            first,
            after,
            gameDomain ?? NexusModsClient.GameDomain
        );

        Log.Info($"[NexusModsService] 获取 Collection Revisions 分页: slug={collectionSlug}, first={first}, after={after}, count={result.Revisions.Count}, hasNext={result.HasNextPage}");

        return result;
    }

    /// <summary>
    /// 获取 Collection Revision 详情（支持缓存）
    /// </summary>
    /// <param name="collectionSlug">Collection 的 Slug（如 g3i395）</param>
    /// <param name="revisionNumber">Revision 号</param>
    /// <param name="gameDomain">游戏域名（默认为 stardewvalley）</param>
    /// <param name="useCache">是否使用缓存（从设置读取）</param>
    public static async Task<NexusCollectionRevisionDetail?> GetCollectionRevisionDetailAsync(
        string collectionSlug,
        int revisionNumber,
        string? gameDomain = null,
        bool useCache = true)
    {
        try
        {
            var cacheKey = $"collection_revision_detail|slug={collectionSlug}|rev={revisionNumber}";

            if (useCache && SearchCacheService.TryGet<NexusCollectionRevisionDetail>("nexus", cacheKey, out var cached))
            {
                Log.Info($"[NexusModsService] Collection Revision Detail 缓存命中: {collectionSlug} r{revisionNumber}");
                return cached;
            }

            var detail = await NexusModsClient.GetCollectionRevisionDetailAsync(collectionSlug, revisionNumber, gameDomain);

            if (detail != null && useCache)
            {
                await SearchCacheService.SetAsync("nexus", cacheKey, detail);
                Log.Info($"[NexusModsService] Collection Revision Detail 已缓存: {collectionSlug} r{revisionNumber}");
            }

            return detail;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[NexusModsService] 获取 Collection Revision Detail 失败: {collectionSlug} r{revisionNumber}");
            return null;
        }
    }

    public static async Task<NexusMod?> GetModDetailsAsync(long modId, bool useCache = true)
    {
        if (useCache)
        {
            var cached = _modCache.Values.FirstOrDefault(m => m.ModId == modId);
            if (cached != null)
            {
                Log.Info($"Using cached details for mod {modId}");
                return cached;
            }
        }

        var mod = await NexusModsClient.GetModDetailsAsync(modId);

        return mod;
    }

    /// <summary>
    /// 获取 Mod 详情（优先复用 GraphQL 搜索链路，失败后再回退 REST）
    /// 用于已登录但 REST 详情偶发失败的场景。
    /// </summary>
    public static async Task<NexusMod?> GetModDetailsWithSearchFallbackAsync(long modId, string? nameHint = null)
    {
        try
        {
            // 1) 先用名称提示（GraphQL）定位（SMAPI 等固定名称场景更快，且避免空查询拉一堆热门）
            if (!string.IsNullOrWhiteSpace(nameHint))
            {
                var byName = await SearchModsAsync(nameHint, page: 1, pageSize: 50, useCache: false);
                var exact = byName?.FirstOrDefault(m => m.ModId == modId)
                    ?? byName?.FirstOrDefault(m => string.Equals(m.Name, nameHint, StringComparison.OrdinalIgnoreCase));
                if (exact != null)
                    return exact;
            }

            // 2) 再用热门列表（GraphQL）按 modId 定位（兜底）
            var popular = await SearchModsAsync(string.Empty, page: 1, pageSize: 100, useCache: false);
            var byPopular = popular?.FirstOrDefault(m => m.ModId == modId);
            if (byPopular != null)
                return byPopular;
        }
        catch (Exception ex)
        {
            Log.Warn("[NexusModsService] GraphQL 搜索回退详情失败，将尝试 REST", ex);
        }

        // 3) 最后回退 REST
        return await GetModDetailsAsync(modId, useCache: false);
    }

    public static async Task<List<NexusModFile>> GetModFilesAsync(long modId)
    {
        var cacheKey = $"files|modId={modId}";
        if (SVL.Core.IO.SearchCacheService.TryGet<List<NexusModFile>>("nexus", cacheKey, out var cached))
        {
            return cached ?? new List<NexusModFile>();
        }

        var files = await NexusModsClient.GetModFilesAsync(modId);
        // 只有成功获取到数据时才缓存（空列表可能是请求失败导致的）
        if (files.Count > 0)
        {
            await SVL.Core.IO.SearchCacheService.SetAsync("nexus", cacheKey, files);
        }
        return files;
    }

    public static async Task<NexusModFileMetadata> GetModFileMetadataAsync(long modId, long fileId)
    {
        if (_fileMetadataCache.TryGetValue(modId, out var memoryMap) && memoryMap != null)
        {
            if (memoryMap.TryGetValue(fileId, out var memoryMetadata))
                return memoryMetadata;

            return new NexusModFileMetadata();
        }

        var mapCacheKey = $"filemeta_map|modId={modId}";
        if (SVL.Core.IO.SearchCacheService.TryGet<Dictionary<long, NexusModFileMetadata>>("nexus", mapCacheKey, out var cachedMap) && cachedMap != null)
        {
            _fileMetadataCache[modId] = cachedMap;
            if (cachedMap.TryGetValue(fileId, out var cachedMetadata))
                return cachedMetadata;

            return new NexusModFileMetadata();
        }

        var jsonDoc = await NexusModsClient.GetModFilesMetadataGraphQlJsonAsync(modId);
        Dictionary<long, NexusModFileMetadata> map;
        if (jsonDoc == null)
        {
            // 请求失败，不缓存空结果
            return new NexusModFileMetadata();
        }

        map = ParseModFilesMetadataMap(jsonDoc.RootElement, modId);
        jsonDoc.Dispose();
        _fileMetadataCache[modId] = map;
        // 只有成功获取到数据时才缓存
        if (map.Count > 0)
        {
            await SVL.Core.IO.SearchCacheService.SetAsync("nexus", mapCacheKey, map);
        }

        if (map.TryGetValue(fileId, out var metadata))
            return metadata;

        return new NexusModFileMetadata();
    }

    private static Dictionary<long, NexusModFileMetadata> ParseModFilesMetadataMap(JsonElement root, long currentModId)
    {
        var result = new Dictionary<long, NexusModFileMetadata>();
        var fileNodes = new List<JsonElement>();
        CollectFileNodesRecursive(root, fileNodes);

        foreach (var node in fileNodes)
        {
            var fileId = TryGetLong(node, "fileId", "file_id");
            if (fileId <= 0)
                continue;

            var metadata = ParseModFileMetadata(node, currentModId);

            // 若当前节点无下载量，尝试从周边容器补取一次（例如外层 data 包裹）
            if (metadata.DownloadCount <= 0)
                metadata.DownloadCount = ExtractDownloadCount(root);

            result[fileId] = metadata;
        }

        return result;
    }

    private static void CollectFileNodesRecursive(JsonElement element, List<JsonElement> fileNodes)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var hasFileId = TryGetLong(element, "fileId", "file_id") > 0;
            var hasDownloadLike = TryGetPropertyIgnoreCase(element, "downloadCount", out _) ||
                                  TryGetPropertyIgnoreCase(element, "download_count", out _) ||
                                  TryGetPropertyIgnoreCase(element, "downloads", out _) ||
                                  TryGetPropertyIgnoreCase(element, "downloads_count", out _) ||
                                  TryGetPropertyIgnoreCase(element, "downloadsCount", out _);

            if (hasFileId && hasDownloadLike)
            {
                fileNodes.Add(element);
            }

            foreach (var property in element.EnumerateObject())
            {
                CollectFileNodesRecursive(property.Value, fileNodes);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectFileNodesRecursive(item, fileNodes);
            }
        }
    }

    private static NexusModFileMetadata ParseModFileMetadata(JsonElement root, long currentModId)
    {
        var metadata = new NexusModFileMetadata
        {
            DownloadCount = ExtractDownloadCount(root)
        };

        var requirements = ExtractRequirementsFromKnownFields(root, currentModId);
        if (requirements.Count == 0)
        {
            ExtractRequirementsRecursive(root, requirements, currentModId, depth: 0);
        }

        metadata.Requirements = requirements
            .GroupBy(r => $"{r.ModId}|{r.Name}|{r.Version}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        return metadata;
    }

    private static List<NexusModFileRequirement> ExtractRequirementsFromKnownFields(JsonElement root, long currentModId)
    {
        var result = new List<NexusModFileRequirement>();

        foreach (var container in EnumerateContainers(root))
        {
            if (!TryGetPropertyIgnoreCase(container, "requirements", out var requirementsElement))
                continue;

            CollectRequirementsFromContainer(requirementsElement, result, currentModId, depth: 0);
        }

        return result;
    }

    private static long ExtractDownloadCount(JsonElement root)
    {
        foreach (var container in EnumerateContainers(root))
        {
            var value = TryGetLong(container, "downloadCount", "download_count", "downloads", "downloads_count", "downloadsCount");
            if (value > 0)
                return value;
        }

        return 0;
    }

    private static IEnumerable<JsonElement> EnumerateContainers(JsonElement root)
    {
        yield return root;

        if (TryGetPropertyIgnoreCase(root, "file_details", out var fileDetails))
            yield return fileDetails;

        if (TryGetPropertyIgnoreCase(root, "file", out var file))
            yield return file;

        if (TryGetPropertyIgnoreCase(root, "data", out var data))
            yield return data;
    }

    private static void ExtractRequirementsRecursive(JsonElement element, List<NexusModFileRequirement> result, long currentModId, int depth)
    {
        if (depth > 6)
            return;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                var name = prop.Name;
                var value = prop.Value;

                var looksLikeRequirementsContainer =
                    name.IndexOf("require", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("depend", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("prereq", StringComparison.OrdinalIgnoreCase) >= 0;

                if (looksLikeRequirementsContainer)
                {
                    CollectRequirementsFromContainer(value, result, currentModId, depth + 1);
                }

                ExtractRequirementsRecursive(value, result, currentModId, depth + 1);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ExtractRequirementsRecursive(item, result, currentModId, depth + 1);
            }
        }
    }

    private static void CollectRequirementsFromContainer(JsonElement container, List<NexusModFileRequirement> result, long currentModId, int depth)
    {
        if (depth > 8)
            return;

        if (container.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in container.EnumerateArray())
            {
                var req = TryParseRequirement(item, currentModId);
                if (req != null)
                    result.Add(req);

                CollectRequirementsFromContainer(item, result, currentModId, depth + 1);
            }

            return;
        }

        if (container.ValueKind == JsonValueKind.Object)
        {
            var req = TryParseRequirement(container, currentModId);
            if (req != null)
                result.Add(req);

            foreach (var prop in container.EnumerateObject())
            {
                CollectRequirementsFromContainer(prop.Value, result, currentModId, depth + 1);
            }
        }
    }

    private static NexusModFileRequirement? TryParseRequirement(JsonElement element, long currentModId)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        var modId = TryGetLong(element, "mod_id", "modId", "id");
        var name = TryGetString(element, "mod_name", "name", "title");
        var version = TryGetString(element, "version", "min_version", "minimum_version", "required_version", "version_range", "constraint");
        var url = TryGetString(element, "url", "external_url", "mod_url", "link");
        var isRequired = true;

        if (TryGetPropertyIgnoreCase(element, "isRequired", out var isRequiredElement) &&
            (isRequiredElement.ValueKind == JsonValueKind.True || isRequiredElement.ValueKind == JsonValueKind.False))
        {
            isRequired = isRequiredElement.GetBoolean();
        }
        else if (TryGetPropertyIgnoreCase(element, "required", out var requiredElement) &&
                 (requiredElement.ValueKind == JsonValueKind.True || requiredElement.ValueKind == JsonValueKind.False))
        {
            isRequired = requiredElement.GetBoolean();
        }
        else if (TryGetPropertyIgnoreCase(element, "optional", out var optionalElement) &&
                 (optionalElement.ValueKind == JsonValueKind.True || optionalElement.ValueKind == JsonValueKind.False))
        {
            isRequired = !optionalElement.GetBoolean();
        }

        if (modId <= 0 && string.IsNullOrWhiteSpace(name))
            return null;

        if (modId > 0 && modId == currentModId)
            return null;

        if (modId > 0 && string.IsNullOrWhiteSpace(url))
            url = $"https://www.nexusmods.com/{NexusModsClient.GameDomain}/mods/{modId}";

        return new NexusModFileRequirement
        {
            ModId = modId,
            Name = string.IsNullOrWhiteSpace(name) ? $"MOD {modId}" : name,
            Version = version ?? string.Empty,
            IsRequired = isRequired,
            Url = url ?? string.Empty
        };
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? TryGetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetPropertyIgnoreCase(element, name, out var value))
            {
                if (value.ValueKind == JsonValueKind.String)
                    return value.GetString();

                if (value.ValueKind == JsonValueKind.Number)
                    return value.GetRawText();
            }
        }

        return null;
    }

    private static long TryGetLong(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetPropertyIgnoreCase(element, name, out var value))
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
                    return number;

                if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var fromString))
                    return fromString;
            }
        }

        return 0;
    }

    /// <summary>
    /// 下载进度回调
    /// </summary>
    /// <param name="progress">进度百分比 (0-100)</param>
    /// <param name="statusMessage">状态消息</param>
    /// <param name="bytesRead">已下载字节数（可选）</param>
    /// <param name="totalBytes">总字节数（可选）</param>
    public delegate void DownloadProgressCallback(double progress, string statusMessage, long bytesRead = 0, long totalBytes = 0);

    /// <summary>
    /// 下载 Mod（使用 NXM key 和 expiry）
    /// </summary>
    /// <param name="modId">Mod ID</param>
    /// <param name="fileId">File ID</param>
    /// <param name="destinationPath">目标路径</param>
    /// <param name="key">NXM key</param>
    /// <param name="expiry">NXM expiry</param>
    /// <param name="progressCallback">进度回调（可选）</param>
    /// <param name="cancellationToken">取消令牌（可选）</param>
    public static async Task<bool> DownloadModAsync(long modId, long fileId, string destinationPath, string key, string expiry, DownloadProgressCallback? progressCallback = null, CancellationToken cancellationToken = default)
    {
        try
        {
            Log.Info($"[NexusMods] 开始下载: modId={modId}, fileId={fileId}");

            progressCallback?.Invoke(0, "获取 Mod 信息...");

            var mod = await NexusModsClient.GetModDetailsAsync(modId, cancellationToken);
            var files = await NexusModsClient.GetModFilesAsync(modId, cancellationToken);

            var file = files.FirstOrDefault(f => f.GetFileIdLong() == fileId);
            if (file == null)
            {
                Log.Error($"[NexusMods] File {fileId} not found for mod {modId}");
                return false;
            }

            var modPath = Path.Combine(destinationPath, mod?.Name ?? $"mod_{modId}");
            var fileName = file.Name ?? file.FileName;

            // 使用 Mod 的真实名称作为 ZIP 文件名（清理非法字符）
            var safeModName = SVL.Core.IO.FileNameValidator.SanitizeFolderName(mod?.Name ?? $"mod_{modId}");
            var zipFileName = $"{safeModName}.zip";
            var zipFilePath = Path.Combine(destinationPath, zipFileName);

            // 验证并创建目标目录
            try
            {
                if (!string.IsNullOrEmpty(destinationPath) && !Directory.Exists(destinationPath))
                {
                    Directory.CreateDirectory(destinationPath);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[NexusMods] 创建目标目录失败: destinationPath='{destinationPath}', 错误={ex.Message}");
                return false;
            }

            // 检查缓存
            var cachedFilePath = NexusModsCacheService.Get(modId, fileId);

            // 如果缓存存在，直接使用
            if (cachedFilePath != null)
            {
                Log.Info($"[NexusMods] ✓ 使用缓存文件: {cachedFilePath}");
                progressCallback?.Invoke(95, "从缓存加载...");

                try
                {
                    File.Copy(cachedFilePath, zipFilePath, true);
                    progressCallback?.Invoke(100, "下载完成（来自缓存）");
                    Log.Info($"[NexusMods] ✓ 从缓存加载成功: {zipFilePath}");
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Warn($"[NexusMods] 从缓存复制失败，将重新下载: {ex.Message}");
                    // 继续下载流程
                }
            }

            progressCallback?.Invoke(5, "获取下载链接...");

            // 使用 NXM key 获取下载链接列表
            var keyPreview = string.IsNullOrEmpty(key) ? "(空)" : key.Substring(0, Math.Min(8, key.Length));
            Log.Info($"[NexusMods] 使用 NXM key 获取下载链接: key={keyPreview}..., expiry={expiry}");

            var downloadUrls = await NexusModsClient.GetDownloadUrlWithKeyAsync(modId, fileId, key, expiry, cancellationToken);

            if (downloadUrls == null || downloadUrls.Count == 0)
            {
                Log.Error($"[NexusMods] 使用 NXM key 获取下载链接失败");
                return false;
            }

            // 使用第一个可用的 URL
            var downloadUrl = downloadUrls[0];
            Log.Info($"[NexusMods] 获取到 {downloadUrls.Count} 个 CDN URL，使用第一个");

            progressCallback?.Invoke(10, "开始下载文件...");

            Log.Info($"[NexusMods] 开始下载文件: {zipFileName}");
            var success = await DownloadFromUrlAsync(downloadUrl, zipFilePath, fileName, progressCallback, cancellationToken);

            if (success)
            {
                Log.Info($"[NexusMods] ✓ 文件下载成功: {zipFilePath}");
                progressCallback?.Invoke(95, "保存到缓存...");

                // 保存到缓存
                try
                {
                    await NexusModsCacheService.SaveAsync(zipFilePath, modId, fileId);
                    Log.Info($"[NexusMods] ✓ 已保存到缓存: modId={modId}, fileId={fileId}");
                }
                catch (Exception ex)
                {
                    Log.Warn($"[NexusMods] 保存到缓存失败（不影响使用）: {ex.Message}");
                }

                progressCallback?.Invoke(100, "下载完成");
            }
            else
            {
                Log.Error($"[NexusMods] ✗ 文件下载失败: {zipFilePath}");
            }

            return success;
        }
        catch (OperationCanceledException)
        {
            Log.Info($"[NexusMods] 下载已取消: modId={modId}, fileId={fileId}");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[NexusMods] 下载 Mod 失败: modId={modId}, fileId={fileId}");
            return false;
        }
    }

    public static async Task<bool> DownloadModAsync(long modId, long fileId, string destinationPath, string nxmLink = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var mod = await NexusModsClient.GetModDetailsAsync(modId, cancellationToken);
            var files = await NexusModsClient.GetModFilesAsync(modId, cancellationToken);

            var file = files.FirstOrDefault(f => f.GetFileIdLong() == fileId);
            if (file == null)
            {
                Log.Error($"[NexusMods] File {fileId} not found for mod {modId}");
                return false;
            }

            var modPath = Path.Combine(destinationPath, mod?.Name ?? $"mod_{modId}");

            if (!Directory.Exists(destinationPath))
            {
                Directory.CreateDirectory(destinationPath);
            }

            string downloadUrl;
            List<string> downloadUrls;

            // 优先尝试使用 .nxm 链接（如果提供）
            if (!string.IsNullOrEmpty(nxmLink))
            {
                var parsed = NexusModsClient.ParseNxmLink(nxmLink);
                if (parsed.HasValue && !string.IsNullOrEmpty(parsed.Value.key) && !string.IsNullOrEmpty(parsed.Value.expiry))
                {
                    Log.Info($"[NexusMods] ✓ 使用 .nxm 链接参数获取下载链接（非 Premium 用户方式）");
                    downloadUrls = await NexusModsClient.GetDownloadUrlWithKeyAsync(modId, fileId, parsed.Value.key, parsed.Value.expiry, cancellationToken);

                    if (downloadUrls != null && downloadUrls.Count > 0)
                    {
                        downloadUrl = downloadUrls[0];
                        Log.Info($"[NexusMods] 获取到 {downloadUrls.Count} 个 CDN URL，使用第一个");
                    }
                    else
                    {
                        downloadUrl = string.Empty;
                    }
                }
                else
                {
                    Log.Warn($"[NexusMods] .nxm 链接无效或缺少参数，尝试使用 API");
                    downloadUrl = await NexusModsClient.GetDownloadUrlAsync(modId, fileId, cancellationToken);
                }
            }
            else
            {
                // 尝试使用 API 获取下载链接（Premium 用户）
                try
                {
                    downloadUrl = await NexusModsClient.GetDownloadUrlAsync(modId, fileId, cancellationToken);
                }
                catch (NexusModsPremiumRequiredException)
                {
                    // 403 错误：非 Premium 用户，重新抛出让上层处理
                    throw;
                }
            }

            if (string.IsNullOrEmpty(downloadUrl))
            {
                // API 获取失败，提供非 Premium 用户解决方案
                Log.Warn($"[NexusMods] ===============================================");
                Log.Warn($"[NexusMods] 无法通过 API 获取下载链接");
                Log.Warn($"[NexusMods]");
                Log.Warn($"[NexusMods] 【非 Premium 用户下载步骤】");
                Log.Warn($"[NexusMods] 1. 点击下方链接打开浏览器：");
                Log.Warn($"[NexusMods]    https://www.nexusmods.com/{NexusModsClient.GameDomain}/mods/{modId}?tab=files");
                Log.Warn($"[NexusMods]");
                Log.Warn($"[NexusMods] 2. 找到文件 ID 为 {fileId} 的文件（{file.Name}）");
                Log.Warn($"[NexusMods]");
                Log.Warn($"[NexusMods] 3. 点击「下载」按钮");
                Log.Warn($"[NexusMods]");
                Log.Warn($"[NexusMods] 4. 浏览器会自动复制 .nxm 链接到剪贴板");
                Log.Warn($"[NexusMods]");
                Log.Warn($"[NexusMods] 5. 返回 SVL，点击「使用剪贴板 .nxm 链接」按钮");
                Log.Warn($"[NexusMods] ===============================================");

                return false;
            }
            else
            {
                // 使用下载链接下载文件
                return await DownloadFromUrlAsync(downloadUrl, modPath, file.Name ?? file.FileName, null, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Info($"[NexusMods] 下载已取消: modId={modId}, fileId={fileId}");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[NexusMods] Failed to download mod: {modId}");
            return false;
        }
    }

    /// <summary>
    /// 从 URL 下载文件
    /// </summary>
    private static async Task<bool> DownloadFromUrlAsync(string url, string destinationPath, string fileName, DownloadProgressCallback? progressCallback = null, CancellationToken cancellationToken = default)
    {
        try
        {
            Log.Info($"[NexusMods] 开始从 CDN 下载: {fileName}");
            Log.Debug($"[NexusMods] 下载 URL: {url}");

            progressCallback?.Invoke(15, "连接 CDN...");

            using var client = new System.Net.Http.HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "SVL Launcher");

            Log.Debug($"[NexusMods] 发送 HTTP GET 请求...");

            progressCallback?.Invoke(20, "模组下载中");

            // 传递 cancellationToken 以支持取消
            var response = await client.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            Log.Debug($"[NexusMods] HTTP 响应状态: {response.StatusCode}");
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            var totalMB = totalBytes / 1024.0 / 1024.0;
            Log.Info($"[NexusMods] 文件大小: {totalMB:F2} MB");

            progressCallback?.Invoke(25, "模组下载中");

            Log.Debug($"[NexusMods] 开始读取响应内容...");
            using var stream = await response.Content.ReadAsStreamAsync();
            using var fileStream = System.IO.File.Create(destinationPath);

            var buffer = new byte[81920]; // 80KB buffer
            long bytesRead = 0;
            int lastLoggedProgress = 0;  // 上次记录日志的进度（每25%记录一次）

            var lastUpdateTime = DateTime.UtcNow;

            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, read, cancellationToken);
                bytesRead += read;

                var currentTime = DateTime.UtcNow;
                var elapsedMs = (currentTime - lastUpdateTime).TotalMilliseconds;

                // 每 1000ms (1秒) 更新一次进度
                if (elapsedMs >= 1000)
                {
                    lastUpdateTime = currentTime;

                    if (totalBytes > 0)
                    {
                        var progress = 25 + (bytesRead * 70.0 / totalBytes); // 25-95%
                        var currentMB = bytesRead / 1024.0 / 1024.0;

                        // 格式：模组下载中 + 换行 + 百分比和大小
                        // 同时传递字节数信息
                        progressCallback?.Invoke(progress, $"模组下载中\n{progress:F2}% {currentMB:F2}MB/{totalMB:F2}MB", bytesRead, totalBytes);

                        // 每 25% 记录一次日志
                        var progressInt = (int)progress;
                        if (progressInt >= lastLoggedProgress + 25)
                        {
                            lastLoggedProgress = progressInt;
                            Log.Info($"[NexusMods] 下载进度: {progress:F2}% ({currentMB:F2}MB / {totalMB:F2}MB)");
                        }
                    }
                }
            }

            var finalMB = bytesRead / 1024.0 / 1024.0;
            Log.Info($"[NexusMods] 下载完成: {finalMB:F2}MB");
            Log.Debug($"[NexusMods] 写入文件: {destinationPath}");

            progressCallback?.Invoke(95, "保存文件...");

            await fileStream.FlushAsync();

            progressCallback?.Invoke(98, "完成");

            Log.Info($"[NexusMods] ✓ 下载成功: {fileName} ({bytesRead} bytes)");
            return true;
        }
        catch (System.Net.Http.HttpRequestException httpEx)
        {
            Log.Error(httpEx, $"[NexusMods] HTTP 请求失败: {url}");

            // 尝试获取 HTTP 状态码
            var statusCode = "未知";
            if (httpEx.InnerException is System.Net.WebException webEx && webEx.Response is System.Net.HttpWebResponse httpResponse)
            {
                statusCode = ((int)httpResponse.StatusCode).ToString();
            }

            Log.Error($"[NexusMods] HTTP 状态: {statusCode}");
            Log.Error($"[NexusMods] HTTP 消息: {httpEx.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[NexusMods] 从 URL 下载失败: {url}");
            return false;
        }
    }

    /// <summary>
    /// 备用下载方法（用于非 Premium 用户）
    /// </summary>
    private static async Task<bool> DownloadModFileAsyncWithFallback(long modId, long fileId, NexusModFile file, string destinationPath)
    {
        try
        {
            // 构造 NexusMods 网站的下载页面 URL
            // 注意：这个方法需要用户手动登录浏览器，或者需要处理 cookie
            var downloadPageUrl = $"https://www.nexusmods.com/{NexusModsClient.GameDomain}/mods/{modId}?tab=files&file_id={fileId}";

            Log.Warn($"[NexusMods] 请访问以下链接手动下载文件：");
            Log.Warn($"[NexusMods] {downloadPageUrl}");
            Log.Warn($"[NexusMods] 文件名: {file.Name ?? file.FileName}");

            // TODO: 可以添加浏览器自动化或解析页面的功能
            // 当前暂时返回失败，提示用户手动下载
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[NexusMods] 备用下载方法失败");
            return false;
        }
    }

    public static async Task ClearCacheAsync()
    {
        try
        {
            _modCache.Clear();
            _searchCache.Clear();
            _fileMetadataCache.Clear();

            await SVL.Core.IO.SearchCacheService.ClearSourceAsync("nexus");

            if (Directory.Exists(_cachePath))
            {
                var files = Directory.GetFiles(_cachePath, "*.json");
                foreach (var file in files)
                {
                    await Task.Run(() => File.Delete(file));
                }

                Log.Info("Cleared NexusMods cache");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to clear cache");
        }
    }


    private static async Task SaveCacheAsync()
    {
        try
        {
            if (!Directory.Exists(_cachePath))
            {
                Directory.CreateDirectory(_cachePath);
            }

            foreach (var kvp in _modCache)
            {
                var cacheFile = Path.Combine(_cachePath, $"{kvp.Key}.json");
                var json = System.Text.Json.JsonSerializer.Serialize(kvp.Value);
                await FileEx.WriteAllTextAsync(cacheFile, json);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save cache");
        }
    }
}
