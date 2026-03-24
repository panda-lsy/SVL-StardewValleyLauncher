using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SVL.Core.Config;
using SVL.Core.Logging;

namespace SVL.Core.Stardew.ResourceProject.NexusMods;

public class NexusModsClient
{
    private static readonly HttpClient _httpClient = new();
    private static readonly object _tokenExpiredLogLock = new();
    private static DateTime _lastTokenExpiredLogUtc = DateTime.MinValue;

    public const string BaseUrl = "https://api.nexusmods.com/v1";
    public const string GameDomain = "stardewvalley";
    public const int GameId = 1303;

    // Nexus API 必需的应用信息（完全模拟 Mod Organizer 2）
    private const string ApplicationName = "Mod Organizer";
    private const string ApplicationVersion = "2.5.2";  // MO2 当前版本
    private const string ProtocolVersion = "1.0.0";

    /// <summary>
    /// API 速率限制信息
    /// </summary>
    public static NexusRateLimit RateLimit { get; } = new NexusRateLimit();

    /// <summary>
    /// 添加 OAuth 认证头
    /// </summary>
    private static void AddApiKeyHeader(HttpRequestMessage request)
    {
        var settings = AppConfig.GetSettings();
        var accessToken = settings.NexusModsOAuthToken;

        // OAuth 优先
        if (!string.IsNullOrEmpty(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
        else
        {
            // 兼容：旧 API Key（尽管已弃用，但可作为 REST v1 的 fallback）
            var apiKey = settings.NexusModsApiKey;
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.TryAddWithoutValidation("apikey", apiKey);
            }
        }

        // NexusMods 官方/第三方客户端通常需要声明应用信息（此处保持最小必要头）
        request.Headers.TryAddWithoutValidation("User-Agent", "SVL-Launcher/1.0");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Application-Name", ApplicationName);
        request.Headers.TryAddWithoutValidation("Application-Version", ApplicationVersion);
        request.Headers.TryAddWithoutValidation("Protocol-Version", ProtocolVersion);
    }

    /// <summary>
    /// 获取当前 Access Token
    /// </summary>
    public static string? GetAccessToken()
    {
        var settings = AppConfig.GetSettings();
        return settings.NexusModsOAuthToken;
    }

    private static async Task<string> GetAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}{endpoint}");
        AddApiKeyHeader(request);

        var response = await _httpClient.SendAsync(request, cancellationToken);

        // 检查 401 Unauthorized - Token 过期
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            LogTokenExpiredWarningOnce();
            throw new NexusModsTokenExpiredException();
        }

        response.EnsureSuccessStatusCode();

        // 更新速率限制信息
        RateLimit.UpdateFromHeaders(response.Headers);

        return await response.Content.ReadAsStringAsync();
    }

    private static void LogTokenExpiredWarningOnce()
    {
        lock (_tokenExpiredLogLock)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastTokenExpiredLogUtc).TotalSeconds < 30)
                return;

            _lastTokenExpiredLogUtc = now;
            Log.Warn("[NexusMods] Access Token 已过期 (401 Unauthorized)");
        }
    }

    /// <summary>
    /// 发送 GET 请求，使用 NXM key 作为查询参数
    /// 注意：仍然需要 API Key 认证，NXM key 只是额外的参数
    /// </summary>
    private static async Task<string> GetAsyncWithNxmKey(string endpoint, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}{endpoint}");

        // 仍然需要 API Key 认证
        AddApiKeyHeader(request);

        Log.Info($"[NexusMods] 请求 API（使用 API Key + NXM key）: {endpoint}");

        var response = await _httpClient.SendAsync(request, cancellationToken);

        // 更新速率限制信息
        RateLimit.UpdateFromHeaders(response.Headers);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            Log.Error($"[NexusMods] API 请求失败: {response.StatusCode}, 响应: {errorContent}");
            throw new HttpRequestException($"API 请求失败: {response.StatusCode}\n{errorContent}");
        }

        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// 使用 GraphQL API 搜索 Mod（NexusMods REST API 已弃用）
    /// </summary>
    public static async Task<List<NexusMod>> SearchModsAsync(string query, int page = 1, int pageSize = 20)
    {
        try
        {
            var normalizedQuery = string.IsNullOrWhiteSpace(query) ? string.Empty : query.Trim();
            var skip = Math.Max(0, (page - 1) * pageSize);
            var requestOffset = skip;
            var requestCount = pageSize;

            // 稳定查询结构（Mod 类型当前不支持 translations 字段）。
            var graphQLQuery = @"
                query SearchModsByGame($filter: ModsFilter, $sort: [ModsSort!], $offset: Int, $count: Int) {
                    mods(filter: $filter, sort: $sort, offset: $offset, count: $count) {
                        nodes {
                            modId
                            name
                            summary
                            description
                            pictureUrl
                            author
                            downloads
                            endorsements
                            category
                            createdAt
                            updatedAt
                            game {
                                id
                                name
                                domainName
                            }
                        }
                        totalCount
                        nodesCount
                    }
                }";

            async Task<List<NexusMod>> ExecuteSearchAsync(object filter, string strategyName, bool logAsError, int? countOverride = null)
            {
                var requestBody = new
                {
                    query = graphQLQuery,
                    variables = new
                    {
                        filter,
                        sort = new[]
                        {
                            new
                            {
                                downloads = new
                                {
                                    direction = "DESC"
                                }
                            }
                        },
                        offset = requestOffset,
                        count = countOverride ?? requestCount
                    }
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.nexusmods.com/v2/graphql")
                {
                    Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
                };

                AddApiKeyHeader(request);

                Log.Info($"[NexusMods] GraphQL 搜索: '{query}'，策略={strategyName} (第{page}页, 每页{pageSize}个, 请求偏移{requestOffset}, 请求数量{countOverride ?? requestCount})");

                var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    if (logAsError)
                        Log.Error($"[NexusMods] GraphQL 错误: {response.StatusCode}, 策略={strategyName}, 响应: {error}");
                    else
                        Log.Debug($"[NexusMods] GraphQL 搜索策略失败: {strategyName}, Status={response.StatusCode}");

                    return new List<NexusMod>();
                }

                var jsonResponse = await response.Content.ReadAsStringAsync();
                Log.Debug($"[NexusMods] GraphQL 响应（前500字符）: {jsonResponse.Substring(0, Math.Min(500, jsonResponse.Length))}...");

                var result = System.Text.Json.JsonSerializer.Deserialize<GraphQLSearchResponse>(jsonResponse);
                var mods = result?.Data?.Mods?.Nodes ?? new List<NexusMod>();

                foreach (var mod in mods)
                {
                    if (mod.ModId == 0 && mod.ModIdGraphQl != 0)
                        mod.ModId = mod.ModIdGraphQl;
                }

                return mods;
            }

            if (string.IsNullOrWhiteSpace(normalizedQuery))
            {
                var filter = new
                {
                    gameDomainName = new[]
                    {
                        new
                        {
                            @op = "EQUALS",
                            value = "stardewvalley"
                        }
                    }
                };

                var mods = await ExecuteSearchAsync(filter, "popular", true);
                Log.Info($"[NexusMods] 找到 {mods.Count} 个模组（查询: {query}）");
                return mods;
            }

            var trimmedQuery = normalizedQuery;
            var wildcardQuery = trimmedQuery.Contains('*') ? trimmedQuery : $"*{trimmedQuery}*";
            var prefixWildcardQuery = trimmedQuery.Contains('*') ? trimmedQuery : $"{trimmedQuery}*";
            var gameOnlyFilter = new
            {
                gameDomainName = new[] { new { @op = "EQUALS", value = "stardewvalley" } }
            };

            var filterCandidates = new List<(object filter, string strategyName)>
            {
                (new
                {
                    gameDomainName = new[] { new { @op = "EQUALS", value = "stardewvalley" } },
                    name = new[] { new { @op = "WILDCARD", value = wildcardQuery } }
                }, "name-wildcard"),
                (new
                {
                    gameDomainName = new[] { new { @op = "EQUALS", value = "stardewvalley" } },
                    name = new[] { new { @op = "WILDCARD", value = prefixWildcardQuery } }
                }, "name-prefix-wildcard"),
                (new
                {
                    gameDomainName = new[] { new { @op = "EQUALS", value = "stardewvalley" } },
                    name = new[] { new { @op = "EQUALS", value = trimmedQuery } }
                }, "name-equals")
            };

            for (var i = 0; i < filterCandidates.Count; i++)
            {
                var (filter, strategyName) = filterCandidates[i];
                var mods = await ExecuteSearchAsync(filter, strategyName, i == filterCandidates.Count - 1);
                if (mods.Count > 0)
                {
                    Log.Info($"[NexusMods] 找到 {mods.Count} 个模组（查询: {query}，策略: {strategyName}）");
                    return mods;
                }
            }

            var fallbackCount = Math.Min(Math.Max(skip + pageSize + 120, pageSize * 8), 240);
            var fallbackMods = await ExecuteSearchAsync(gameOnlyFilter, "popular-fallback", false, fallbackCount);
            if (fallbackMods.Count > 0)
            {
                var localFiltered = fallbackMods
                    .Where(mod => !string.IsNullOrWhiteSpace(mod.Name) && mod.Name.IndexOf(trimmedQuery, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Take(pageSize)
                    .ToList();

                if (localFiltered.Count > 0)
                {
                    Log.Info($"[NexusMods] 本地回退筛选命中 {localFiltered.Count} 个模组（查询: {query}）");
                    return localFiltered;
                }
            }

            return new List<NexusMod>();
        }
        catch (NexusModsTokenExpiredException)
        {
            // Token 过期，重新抛出
            Log.Warn("[NexusMods] Token 已过期（在 GraphQL 搜索中）");
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Failed to search mods: {query}");
            return new List<NexusMod>();
        }
    }

    public static async Task<List<NexusCollection>> SearchCollectionsAsync(string query, int page = 1, int pageSize = 20)
    {
        try
        {
            int safePage = Math.Max(1, page);
            int safePageSize = Math.Max(1, pageSize);
            int offset = (safePage - 1) * safePageSize;

            var graphQLQuery = @"
                query GetGameCollections(
                  $filter: CollectionsSearchFilter,
                  $sort: [CollectionsSearchSort!],
                  $offset: Int,
                  $count: Int
                ) {
                  collectionsV2(
                    filter: $filter,
                    sort: $sort,
                    offset: $offset,
                    count: $count
                  ) {
                    nodes {
                      id
                      name
                      slug
                      summary
                      totalDownloads
                      updatedAt
                      user { name }
                      tileImage { url }
                      game { domainName }
                    }
                    totalCount
                    nodesCount
                  }
                }";

            var filterCandidates = new object[]
            {
                new
                {
                    gameDomain = new[] { new { op = "EQUALS", value = GameDomain } },
                    collectionStatus = new[] { new { op = "EQUALS", value = "published" } }
                },
                new
                {
                    gameDomainName = new[] { new { op = "EQUALS", value = GameDomain } },
                    collectionStatus = new[] { new { op = "EQUALS", value = "published" } }
                },
                new
                {
                    gameDomain = new[] { new { op = "EQUALS", value = GameDomain } }
                },
                new
                {
                    gameDomainName = new[] { new { op = "EQUALS", value = GameDomain } }
                }
            };

            List<NexusCollectionNode> nodes = new();

            foreach (var filter in filterCandidates)
            {
                var requestBody = new
                {
                    query = graphQLQuery,
                    variables = new
                    {
                        filter,
                        sort = new[]
                        {
                            new
                            {
                                endorsements = new { direction = "DESC" }
                            }
                        },
                        offset,
                        count = safePageSize
                    }
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.nexusmods.com/v2/graphql")
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
                };

                AddApiKeyHeader(request);

                var response = await _httpClient.SendAsync(request);
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    LogTokenExpiredWarningOnce();
                    throw new NexusModsTokenExpiredException();
                }

                response.EnsureSuccessStatusCode();

                var jsonResponse = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<GraphQLCollectionsResponse>(jsonResponse);
                nodes = result?.Data?.CollectionsV2?.Nodes ?? new List<NexusCollectionNode>();

                Log.Debug($"[NexusMods] collectionsV2 响应节点: {nodes.Count}, filter={JsonSerializer.Serialize(filter)}");
                if (nodes.Count > 0)
                {
                    break;
                }
            }

            var collections = nodes
                .Where(n => n != null && n.Id > 0)
                .Select(n =>
                {
                    var domain = string.IsNullOrWhiteSpace(n.Game?.DomainName) ? GameDomain : n.Game.DomainName;
                    var slugOrId = !string.IsNullOrWhiteSpace(n.Slug) ? n.Slug : n.Id.ToString();

                    return new NexusCollection
                    {
                        CollectionId = n.Id,
                        Name = n.Name ?? $"Collection {n.Id}",
                        Summary = n.Summary ?? string.Empty,
                        Author = string.IsNullOrWhiteSpace(n.User?.Name) ? "NexusMods" : n.User.Name,
                        Downloads = n.TotalDownloads,
                        UpdatedAt = n.UpdatedAt,
                        PictureUrl = n.TileImage?.Url ?? string.Empty,
                        Url = $"https://next.nexusmods.com/{domain}/collections/{slugOrId}"
                    };
                })
                .ToList();

            if (!string.IsNullOrWhiteSpace(query))
            {
                collections = collections
                    .Where(c => (c.Name?.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                                (c.Summary?.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
                    .ToList();
            }

            Log.Info($"[NexusMods] GraphQL collectionsV2 成功: query={query}, page={safePage}, count={collections.Count}");
            return collections;
        }
        catch (NexusModsTokenExpiredException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("[NexusMods] GraphQL collectionsV2 查询失败", ex);
            return new List<NexusCollection>();
        }
    }

    /// <summary>
    /// 获取 Collection 的 Revisions（分页）
    /// 注意：NexusMods GraphQL API 不支持 revisions 字段分页，这里返回所有版本
    /// </summary>
    /// <param name="collectionSlug">Collection 的 Slug（如 g3i395）</param>
    /// <param name="page">页码（参数保留但无效，API 不支持分页）</param>
    /// <param name="pageSize">每页数量（参数保留但无效，API 不支持分页）</param>
    /// <param name="gameDomain">游戏域名（默认 stardewvalley）</param>
    public static async Task<List<NexusCollectionRevision>> GetCollectionRevisionsAsync(string collectionSlug, int page = 1, int pageSize = 20, string? gameDomain = null)
    {
        try
        {
            var domain = gameDomain ?? GameDomain;

            // NexusMods GraphQL API 的 revisions 字段不支持分页参数
            // 直接调用 GetAllCollectionRevisionsAsync
            var revisions = await GetAllCollectionRevisionsAsync(collectionSlug, domain);

            // 记录请求参数（虽然 API 不支持分页）
            Log.Info($"[NexusMods] 获取 Collection {collectionSlug} 的 Revisions: page={page}, pageSize={pageSize}, 总数={revisions.Count}");

            return revisions;
        }
        catch (NexusModsTokenExpiredException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"[NexusMods] 获取 Collection {collectionSlug} 的 Revisions 失败", ex);
            return new List<NexusCollectionRevision>();
        }
    }

    /// <summary>
    /// 获取 Collection 的所有 Revisions（版本列表）- 仅用于特殊情况，建议使用分页版本
    /// </summary>
    /// <param name="collectionSlug">Collection 的 Slug（如 g3i395）</param>
    /// <param name="gameDomain">游戏域名（默认 stardewvalley）</param>
    public static async Task<List<NexusCollectionRevision>> GetAllCollectionRevisionsAsync(string collectionSlug, string? gameDomain = null)
    {
        try
        {
            var domain = gameDomain ?? GameDomain;
            var query = @"
                query GetCollectionRevisionsAll($slug: String!, $domainName: String!) {
                  collection(slug: $slug, domainName: $domainName) {
                    id
                    name
                    slug
                    revisions {
                      id
                      revisionNumber
                      revisionStatus
                      status
                      createdAt
                      updatedAt
                      fileSize
                      totalSize
                      modCount
                      totalDownloads
                      uniqueDownloads
                      downloadLink
                      latest
                      adultContent
                    }
                  }
                }";

            var revisions = await QueryCollectionRevisionsInternalAsync(
                collectionSlug,
                domain,
                query,
                new { slug = collectionSlug, domainName = domain });

            Log.Info($"[NexusMods] 获取 Collection {collectionSlug} 的全部 Revisions: count={revisions.Count}");
            return revisions;
        }
        catch (NexusModsTokenExpiredException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"[NexusMods] 获取 Collection {collectionSlug} 的全部 Revisions 失败", ex);
            return new List<NexusCollectionRevision>();
        }
    }

    /// <summary>
    /// 获取 Collection 的 Revisions（分页，使用 first/after）
    /// </summary>
    /// <param name="collectionSlug">Collection 的 Slug（如 g3i395）</param>
    /// <param name="first">返回数量</param>
    /// <param name="after">游标（用于获取下一页）</param>
    /// <param name="gameDomain">游戏域名</param>
    public static async Task<CollectionRevisionsPagedResult> GetCollectionRevisionsPagedAsync(
        string collectionSlug,
        int first = 5,
        string? after = null,
        string? gameDomain = null)
    {
        try
        {
            var domain = gameDomain ?? GameDomain;

            // Relay 风格分页查询
            var query = @"
                query GetCollectionRevisionsPaged($slug: String!, $domainName: String!, $first: Int!, $after: String) {
                  collection(slug: $slug, domainName: $domainName) {
                    id
                    name
                    slug
                    revisions(first: $first, after: $after) {
                      edges {
                        node {
                          id
                          revisionNumber
                          revisionStatus
                          status
                          createdAt
                          updatedAt
                          fileSize
                          totalSize
                          modCount
                          totalDownloads
                          uniqueDownloads
                          downloadLink
                          latest
                          adultContent
                        }
                        cursor
                      }
                      nodes {
                        id
                        revisionNumber
                        revisionStatus
                        status
                        createdAt
                        updatedAt
                        fileSize
                        totalSize
                        modCount
                        totalDownloads
                        uniqueDownloads
                        downloadLink
                        latest
                        adultContent
                      }
                      pageInfo {
                        hasNextPage
                        hasPreviousPage
                        startCursor
                        endCursor
                      }
                      totalCount
                    }
                  }
                }";

            var variables = new Dictionary<string, object?>
            {
                { "slug", collectionSlug },
                { "domainName", domain },
                { "first", first }
            };

            if (!string.IsNullOrEmpty(after))
            {
                variables["after"] = after;
            }

            var requestBody = new
            {
                query,
                variables
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.nexusmods.com/v2/graphql")
            {
                Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
            };

            AddApiKeyHeader(request);

            var response = await _httpClient.SendAsync(request);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                LogTokenExpiredWarningOnce();
                throw new NexusModsTokenExpiredException();
            }

            response.EnsureSuccessStatusCode();

            var jsonResponse = await response.Content.ReadAsStringAsync();
            Log.Debug($"[NexusMods] Collection Revisions 分页响应: {jsonResponse.Substring(0, Math.Min(500, jsonResponse.Length))}...");

            var result = JsonSerializer.Deserialize<GraphQLCollectionRevisionsPagedResponse>(jsonResponse);
            var connection = result?.Data?.Collection?.Revisions;

            if (connection == null)
            {
                Log.Warn($"[NexusMods] 分页查询 Collection {collectionSlug} Revisions 失败：响应为空");
                return new CollectionRevisionsPagedResult
                {
                    Revisions = new List<NexusCollectionRevision>(),
                    HasNextPage = false,
                    TotalCount = 0
                };
            }

            // 转换为 NexusCollectionRevision 列表
            var revisions = connection.Nodes?
                .Where(r => r != null)
                .Select(r => new NexusCollectionRevision
                {
                    RevisionId = r.Id > 0 ? r.Id : r.RevisionNumber,
                    RevisionNumber = r.RevisionNumber,
                    Name = $"Revision {r.RevisionNumber}",
                    Description = r.RevisionStatus ?? r.Status ?? string.Empty,
                    IsLatest = r.Latest,
                    CreatedAt = r.CreatedAt,
                    UpdatedAt = r.UpdatedAt,
                    FileSize = r.FileSize,
                    ModCount = r.ModCount,
                    TotalDownloads = r.TotalDownloads,
                    DownloadLink = r.DownloadLink ?? string.Empty,
                    CollectionSlug = collectionSlug
                })
                .OrderByDescending(r => r.RevisionNumber)
                .ToList() ?? new List<NexusCollectionRevision>();

            Log.Info($"[NexusMods] 分页获取 Collection {collectionSlug} Revisions: first={first}, after={(after?.Substring(0, 20) ?? "null")}, 返回={revisions.Count}, 还有更多页={connection.PageInfo?.HasNextPage ?? false}");

            return new CollectionRevisionsPagedResult
            {
                Revisions = revisions,
                HasNextPage = connection.PageInfo?.HasNextPage ?? false,
                EndCursor = connection.PageInfo?.EndCursor,
                TotalCount = connection.TotalCount
            };
        }
        catch (NexusModsTokenExpiredException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"[NexusMods] 分页获取 Collection {collectionSlug} Revisions 失败", ex);
            return new CollectionRevisionsPagedResult
            {
                Revisions = new List<NexusCollectionRevision>(),
                HasNextPage = false,
                TotalCount = 0
            };
        }
    }

    private static async Task<List<NexusCollectionRevision>> QueryCollectionRevisionsInternalAsync(
        string collectionSlug,
        string domain,
        string graphQLQuery,
        object variables)
    {
        var requestBody = new
        {
            query = graphQLQuery,
            variables
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.nexusmods.com/v2/graphql")
        {
            Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
        };

        AddApiKeyHeader(request);

        var response = await _httpClient.SendAsync(request);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            Log.Warn("[NexusMods] Access Token 已过期 (401 Unauthorized)");
            throw new NexusModsTokenExpiredException();
        }

        response.EnsureSuccessStatusCode();

        var jsonResponse = await response.Content.ReadAsStringAsync();
        Log.Debug($"[NexusMods] Collection Revisions 原始响应: {jsonResponse.Substring(0, Math.Min(500, jsonResponse.Length))}...");

        var result = JsonSerializer.Deserialize<GraphQLCollectionRevisionsResponse>(jsonResponse);
        var revisions = result?.Data?.Collection?.Revisions ?? new List<NexusCollectionRevisionNode>();

        return revisions
            .Where(r => r != null)
            .Select(r => new NexusCollectionRevision
            {
                RevisionId = r.Id > 0 ? r.Id : r.RevisionNumber,
                RevisionNumber = r.RevisionNumber,
                Name = $"Revision {r.RevisionNumber}",
                Description = r.RevisionStatus ?? r.Status ?? string.Empty,
                IsLatest = r.Latest,
                CreatedAt = r.CreatedAt,
                UpdatedAt = r.UpdatedAt,
                TotalDownloads = r.TotalDownloads,
                FileSize = r.TotalSize > 0 ? r.TotalSize : r.FileSize,
                ModCount = r.ModCount,
                DownloadLink = r.DownloadLink ?? string.Empty,
                CollectionSlug = collectionSlug
            })
            .OrderByDescending(r => r.RevisionNumber)
            .ToList();
    }

    /// <summary>
    /// 获取 Collection 单个 Revision 的详细信息（包含 modFiles 列表）
    /// </summary>
    public static async Task<NexusCollectionRevisionDetail?> GetCollectionRevisionDetailAsync(
        string collectionSlug,
        int revisionNumber,
        string? gameDomain = null,
        bool allowIdFallback = true)
    {
        try
        {
            var domain = gameDomain ?? GameDomain;

            var graphQLQuery = @"
                query GetCollectionRevision($slug: String!, $revision: Int!, $domainName: String!) {
                  collectionRevision(slug: $slug, revision: $revision, domainName: $domainName) {
                    id
                    revisionNumber
                    status
                    revisionStatus
                    fileSize
                    totalSize
                    modCount
                    totalDownloads
                    uniqueDownloads
                    downloadLink
                    latest
                    adultContent
                    createdAt
                    updatedAt
                    collection {
                      id
                      name
                      slug
                      game {
                        id
                        name
                        domainName
                      }
                      user {
                                                memberId
                        name
                                                avatar
                      }
                    }
                  }
                }";

            var requestBody = new
            {
                query = graphQLQuery,
                variables = new
                {
                    slug = collectionSlug,
                    revision = revisionNumber,
                    domainName = domain
                }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.nexusmods.com/v2/graphql")
            {
                Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
            };

            AddApiKeyHeader(request);

            var response = await _httpClient.SendAsync(request);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                Log.Warn("[NexusMods] Access Token 已过期 (401 Unauthorized)");
                throw new NexusModsTokenExpiredException();
            }

            response.EnsureSuccessStatusCode();

            var jsonResponse = await response.Content.ReadAsStringAsync();
            Log.Debug($"[NexusMods] Collection Revision Detail 原始响应: {jsonResponse.Substring(0, Math.Min(500, jsonResponse.Length))}...");

            var result = JsonSerializer.Deserialize<GraphQLCollectionRevisionDetailResponse>(jsonResponse);
            var revisionData = result?.Data?.CollectionRevision;

            if (revisionData == null)
            {
                if (allowIdFallback && revisionNumber > 0)
                {
                    var mappedRevisionNumber = await TryMapRevisionIdToRevisionNumberAsync(collectionSlug, revisionNumber, domain);
                    if (mappedRevisionNumber.HasValue && mappedRevisionNumber.Value != revisionNumber)
                    {
                        Log.Warn($"[NexusMods] Collection {collectionSlug} Revision 参数可能为内部ID，回退为 revisionNumber={mappedRevisionNumber.Value} 后重试");
                        return await GetCollectionRevisionDetailAsync(collectionSlug, mappedRevisionNumber.Value, domain, allowIdFallback: false);
                    }
                }

                Log.Warn($"[NexusMods] Collection {collectionSlug} Revision {revisionNumber} 不存在");
                return null;
            }

            var detail = new NexusCollectionRevisionDetail
            {
                Id = revisionData.Id,
                RevisionNumber = revisionData.RevisionNumber,
                Status = revisionData.Status ?? revisionData.RevisionStatus ?? string.Empty,
                ModCount = revisionData.ModCount,
                TotalDownloads = revisionData.TotalDownloads,
                UniqueDownloads = revisionData.UniqueDownloads,
                DownloadLink = revisionData.DownloadLink ?? string.Empty,
                IsLatest = revisionData.Latest,
                AdultContent = revisionData.AdultContent,
                CreatedAt = revisionData.CreatedAt,
                UpdatedAt = revisionData.UpdatedAt,
                CollectionSlug = collectionSlug,
                CollectionName = revisionData.Collection?.Name ?? string.Empty,
                Author = revisionData.Collection?.User?.Name ?? string.Empty,
                GameDomain = revisionData.Collection?.Game?.DomainName ?? domain,
                ModFiles = await GetCollectionRevisionModsAsync(collectionSlug, revisionData.RevisionNumber, domain)
            };

            // 解析 BigInt 字符串
            if (long.TryParse(revisionData.FileSizeStr, out var fileSize))
                detail.FileSize = fileSize;
            if (long.TryParse(revisionData.TotalSizeStr, out var totalSize))
                detail.TotalSize = totalSize;

            Log.Info($"[NexusMods] 获取 Collection Revision 详情: {collectionSlug} r{revisionNumber}, ModFiles: {detail.ModFiles.Count}");
            return detail;
        }
        catch (NexusModsTokenExpiredException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"[NexusMods] 获取 Collection Revision 详情失败: {collectionSlug} r{revisionNumber}", ex);
            return null;
        }
    }

    private static async Task<List<NexusCollectionModFile>> GetCollectionRevisionModsAsync(string collectionSlug, int revisionNumber, string domain)
    {
        try
        {
            // 注意：根据 Vortex 文档，Collection 的 Mod 列表需要通过 download_link 下载 JSON 文件获取
            // 而不是直接通过 GraphQL 查询
            // 这个方法目前返回空列表，实际的 Mod 列表会在下载 Collection JSON 文件后解析
            Log.Info($"[NexusMods] Collection Mod 列表需要通过 download_link 下载 JSON 文件获取: {collectionSlug} r{revisionNumber}");
            return new List<NexusCollectionModFile>();
        }
        catch (NexusModsTokenExpiredException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"[NexusMods] 获取 Collection Revision Mods 失败: {collectionSlug} r{revisionNumber}", ex);
            return new List<NexusCollectionModFile>();
        }
    }

    private static async Task<int?> TryMapRevisionIdToRevisionNumberAsync(string collectionSlug, int revisionId, string domain)
    {
        try
        {
            var revisions = await GetCollectionRevisionsAsync(collectionSlug, page: 1, pageSize: 300, gameDomain: domain);
            var matched = revisions.FirstOrDefault(r => r.RevisionId == revisionId);
            if (matched == null || matched.RevisionNumber <= 0)
                return null;

            return matched.RevisionNumber;
        }
        catch (Exception ex)
        {
            Log.Debug($"[NexusMods] 回退映射 revisionId -> revisionNumber 失败: slug={collectionSlug}, revisionId={revisionId}, error={ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 验证 Access Token 是否有效（通过 Validate 端点）
    /// </summary>
    /// <param name="accessToken">可选的 Access Token，如果不提供则从配置读取</param>
    public static async Task<bool> ValidateAccessTokenAsync(string? accessToken = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                accessToken = GetAccessToken();
            }

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                Log.Warn("[NexusMods] Access Token 为空");
                return false;
            }

            // 使用验证端点
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/users/validate");

            request.Headers.Add("Authorization", $"Bearer {accessToken}");
            request.Headers.Add("User-Agent", "SVL-Launcher/1.0");
            request.Headers.Add("Accept", "application/json");

            var response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                Log.Info("[NexusMods] Access Token 验证成功");
                return true;
            }
            else
            {
                var content = await response.Content.ReadAsStringAsync();
                Log.Warn($"[NexusMods] Access Token 验证失败: {response.StatusCode}, Content: {content}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NexusMods] Access Token 验证失败");
            return false;
        }
    }

    public static async Task<NexusMod> GetModDetailsAsync(long modId, CancellationToken cancellationToken = default)
    {
        try
        {
            var endpoint = $"/games/{GameDomain}/mods/{modId}";
            var json = await GetAsync(endpoint, cancellationToken);
            var mod = System.Text.Json.JsonSerializer.Deserialize<NexusMod>(json);

            Log.Info($"Retrieved details for mod {modId}");
            return mod;
        }
        catch (NexusModsTokenExpiredException)
        {
            // Token 过期，重新抛出
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Failed to get mod details: {modId}");
            return null;
        }
    }

    /// <summary>
    /// 通过 GraphQL 查询 ModRequirements（REST 详情接口不包含该字段）
    /// </summary>
    public static async Task<NexusModRequirements?> GetModRequirementsGraphQlAsync(long modId, CancellationToken cancellationToken = default)
    {
        try
        {
            var graphQLQuery = @"
                query GetModRequirements($gameId: ID!, $modId: ID!, $offset: Int!, $count: Int!) {
                    mod(gameId: $gameId, modId: $modId) {
                        modRequirements {
                            dlcRequirements {
                                gameExpansion {
                                    gameId
                                    id
                                    name
                                }
                                notes
                            }
                            nexusRequirements(offset: $offset, count: $count) {
                                totalCount
                                nodesCount
                                nodes {
                                    externalRequirement
                                    gameId
                                    id
                                    modId
                                    modName
                                    notes
                                    url
                                }
                            }
                            modsRequiringThisMod(offset: $offset, count: $count) {
                                totalCount
                                nodesCount
                                nodes {
                                    externalRequirement
                                    gameId
                                    id
                                    modId
                                    modName
                                    notes
                                    url
                                }
                            }
                        }
                    }
                }";

            var requestBody = new
            {
                query = graphQLQuery,
                variables = new
                {
                    gameId = GameId,
                    modId,
                    offset = 0,
                    count = 100
                }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.nexusmods.com/v2/graphql")
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
            };

            AddApiKeyHeader(request);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                LogTokenExpiredWarningOnce();
                throw new NexusModsTokenExpiredException();
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                Log.Debug($"[NexusMods] GraphQL modRequirements 查询失败: modId={modId}, status={response.StatusCode}, body={errorBody}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(json))
                return null;

            using var document = JsonDocument.Parse(json);

            if (document.RootElement.TryGetProperty("errors", out var errorsElement) &&
                errorsElement.ValueKind == JsonValueKind.Array &&
                errorsElement.GetArrayLength() > 0)
            {
                Log.Warn($"[NexusMods] GraphQL modRequirements 存在 errors: modId={modId}, errors={errorsElement.GetRawText()}");
            }

            if (!document.RootElement.TryGetProperty("data", out var dataElement) ||
                !dataElement.TryGetProperty("mod", out var modElement) ||
                modElement.ValueKind == JsonValueKind.Null)
            {
                Log.Warn($"[NexusMods] GraphQL modRequirements 返回空 mod: modId={modId}, gameId={GameId}");
                return null;
            }

            if (!modElement.TryGetProperty("modRequirements", out var requirementsElement) ||
                requirementsElement.ValueKind == JsonValueKind.Null)
            {
                Log.Warn($"[NexusMods] GraphQL modRequirements 字段为空: modId={modId}, gameId={GameId}");
                return null;
            }

            return System.Text.Json.JsonSerializer.Deserialize<NexusModRequirements>(requirementsElement.GetRawText());
        }
        catch (NexusModsTokenExpiredException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug($"[NexusMods] GraphQL modRequirements 查询异常: modId={modId}, error={ex.Message}");
            return null;
        }
    }

    public static async Task<List<NexusModFile>> GetModFilesAsync(long modId, CancellationToken cancellationToken = default)
    {
        try
        {
            var endpoint = $"/games/{GameDomain}/mods/{modId}/files";
            var json = await GetAsync(endpoint, cancellationToken);

            var response = System.Text.Json.JsonSerializer.Deserialize<NexusFilesResponse>(json);
            return response?.Files ?? new List<NexusModFile>();
        }
        catch (NexusModsTokenExpiredException)
        {
            // Token 过期，重新抛出
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Failed to get mod files: {modId}");
            return new List<NexusModFile>();
        }
    }

    /// <summary>
    /// 获取 Mod 的多个文件元数据（使用 GraphQL modFile 查询）
    /// 通过 REST API 获取文件列表，然后使用 GraphQL 批量查询文件详情
    /// </summary>
    public static async Task<JsonDocument?> GetModFilesMetadataGraphQlJsonAsync(long modId, CancellationToken cancellationToken = default)
    {
        try
        {
            // 首先通过 REST API 获取文件列表
            var files = await GetModFilesAsync(modId, cancellationToken);
            if (files == null || files.Count == 0)
            {
                Log.Debug($"[NexusMods] 未找到 mod 文件列表: modId={modId}");
                return null;
            }

            // 限制批量查询数量（避免 GraphQL 查询过大）
            const int maxBatchSize = 50;
            var fileIds = files.Take(maxBatchSize).Select(f => f.GetFileIdLong()).Where(id => id > 0).ToList();

            if (fileIds.Count == 0)
            {
                Log.Debug($"[NexusMods] 没有有效的文件 ID: modId={modId}");
                return null;
            }

            // 使用 GraphQL 别名批量查询多个 modFile
            var query = BuildBatchModFileQuery(fileIds);

            var jsonRequest = System.Text.Json.JsonSerializer.Serialize(new { query });
            using var content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.nexusmods.com/v2/graphql")
            {
                Content = content
            };

            AddApiKeyHeader(request);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                Log.Debug($"[NexusMods] GraphQL modFile 批量查询失败: modId={modId}, status={response.StatusCode}, body={errorBody}");
                return null;
            }

            var responseJson = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(responseJson))
                return null;

            return JsonDocument.Parse(responseJson);
        }
        catch (NexusModsTokenExpiredException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug($"[NexusMods] GraphQL 文件元数据获取失败: modId={modId}, error={ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 构建批量 modFile GraphQL 查询（使用别名）
    /// </summary>
    private static string BuildBatchModFileQuery(List<long> fileIds)
    {
        // 为每个文件 ID 创建一个 modFile 查询，使用别名
        // 例如: file1: modFile(fileId: "123") { ... }, file2: modFile(fileId: "456") { ... }
        var selectionFields = @"
            fileId
            name
            description
            version
            date
            sizeInBytes
            downloadCount
            downloads
            downloadsCount
            uploadedTime
            requirements {
                modId
                name
                version
                url
                optional
                isOptional
                required
                isRequired
            }
        """.Trim();

        var queries = new List<string>();
        for (int i = 0; i < fileIds.Count; i++)
        {
            var alias = $"file{i}";
            var fileId = fileIds[i];
            queries.Add($"{alias}: modFile(fileId: \"{fileId}\") {{ {selectionFields} }}");
        }

        return $"{{ {{ {string.Join("\n  ", queries)} }} }}";
    }

    public static async Task<byte[]> DownloadModFileAsync(long fileId, string fileName)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/games/{GameDomain}/mods/files/{fileId}");
            AddApiKeyHeader(request);

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var bytes = await response.Content.ReadAsByteArrayAsync();
            Log.Info($"Downloaded {fileName} ({bytes.Length} bytes)");

            return bytes;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Failed to download mod file: {fileName}");
            return Array.Empty<byte>();
        }
    }

    /// <summary>
    /// 获取文件的下载链接
    /// </summary>
    /// <param name="modId">Mod ID</param>
    /// <param name="fileId">文件 ID</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static async Task<string> GetDownloadUrlAsync(long modId, long fileId, CancellationToken cancellationToken = default)
    {
        try
        {
            // 正确的 API 端点：/v1/games/{game_domain}/mods/{mod_id}/files/{id}/download_link.json
            var endpoint = $"/games/{GameDomain}/mods/{modId}/files/{fileId}/download_link.json";

            Log.Info($"[NexusMods] 请求下载链接: modId={modId}, fileId={fileId}");
            var json = await GetAsync(endpoint, cancellationToken);

            Log.Debug($"[NexusMods] API 响应前200字符: {json.Substring(0, Math.Min(200, json.Length))}...");

            var response = System.Text.Json.JsonSerializer.Deserialize<NexusDownloadUrlResponse>(json);

            if (response == null)
            {
                Log.Error($"[NexusMods] 反序列化失败，响应内容: {json}");
                return string.Empty;
            }

            var downloadUrl = response.GetFirstUrl();

            if (string.IsNullOrEmpty(downloadUrl))
            {
                Log.Error($"[NexusMods] 无法从响应中提取下载 URL，响应: {json}");
                return string.Empty;
            }

            Log.Info($"[NexusMods] 获取下载链接成功: {downloadUrl}");
            return downloadUrl;
        }
        catch (OperationCanceledException)
        {
            Log.Info($"[NexusMods] 获取下载链接已取消: modId={modId}, fileId={fileId}");
            return string.Empty;
        }
        catch (System.Text.Json.JsonException jsonEx)
        {
            Log.Error(jsonEx, $"[NexusMods] JSON 解析失败: modId={modId}, fileId={fileId}");
            return string.Empty;
        }
        catch (System.Net.Http.HttpRequestException httpEx) when (httpEx.Message.Contains("403") || httpEx.Message.Contains("Forbidden"))
        {
            // 403 错误：非 Premium 用户无法直接下载
            Log.Warn($"[NexusMods] 需要 Premium 权限: modId={modId}, fileId={fileId}");
            throw new NexusModsPremiumRequiredException(modId, fileId, "该资源需要 NexusMods Premium 权限", httpEx);
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[NexusMods] 获取下载链接失败: modId={modId}, fileId={fileId}");
            return string.Empty;
        }
    }

    /// <summary>
    /// 使用 .nxm 链接参数获取下载链接（支持非 Premium 用户）
    /// 参考 MO2: nexusinterface.cpp:934-942
    /// </summary>
    /// <param name="modId">Mod ID</param>
    /// <param name="fileId">文件 ID</param>
    /// <param name="nxmKey">从 .nxm 链接提取的 key</param>
    /// <param name="expires">从 .nxm 链接提取的 expiry</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static async Task<List<string>> GetDownloadUrlWithKeyAsync(long modId, long fileId, string nxmKey, string? expires = null, CancellationToken cancellationToken = default)
    {
        // 检查 key 是否有效
        if (string.IsNullOrEmpty(nxmKey) || nxmKey.Length < 20)
        {
            Log.Warn($"[NexusMods] NXM key 格式无效: {nxmKey} (长度: {nxmKey?.Length ?? 0})");
            return new List<string>();
        }

        // 检查 key 是否过期
        if (!string.IsNullOrEmpty(expires) && long.TryParse(expires, out var expiryTimestamp))
        {
            var expiryDate = DateTimeOffset.FromUnixTimeSeconds(expiryTimestamp);
            if (DateTimeOffset.UtcNow > expiryDate)
            {
                Log.Warn($"[NexusMods] NXM key 已过期: {expiryDate:yyyy-MM-dd HH:mm:ss} UTC");
                return new List<string>();
            }
            Log.Info($"[NexusMods] NXM key 有效期至: {expiryDate:yyyy-MM-dd HH:mm:ss} UTC");
        }

        Log.Info($"[NexusMods] 使用 NXM key 获取下载链接: modId={modId}, fileId={fileId}");

        // MO2 代码: nexusinterface.cpp:936-942
        // url = ".../download_link?key={key}&expires={expires}"
        var endpoint = $"/games/{GameDomain}/mods/{modId}/files/{fileId}/download_link.json?key={nxmKey}";

        if (!string.IsNullOrEmpty(expires))
        {
            endpoint += $"&expires={expires}";
        }

        // 使用 API Key 认证 + NXM key 参数
        var json = await GetAsyncWithNxmKey(endpoint, cancellationToken);

        Log.Debug($"[NexusMods] API 响应前200字符: {json.Substring(0, Math.Min(200, json.Length))}...");

        List<string> downloadUrls;

        // 尝试解析为数组（新格式）
        try
        {
            var arrayResponse = System.Text.Json.JsonSerializer.Deserialize<List<NexusCdnServer>>(json);
            if (arrayResponse != null && arrayResponse.Count > 0)
            {
                downloadUrls = arrayResponse.Select(c => c.URI).ToList();
                Log.Info($"[NexusMods] 使用数组格式解析成功，CDN: {arrayResponse[0].Name}，共 {downloadUrls.Count} 个 URL");
                return downloadUrls;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            Log.Debug("[NexusMods] 不是数组格式，尝试对象格式");
        }

        // 尝试解析为对象（旧格式）
        var response = System.Text.Json.JsonSerializer.Deserialize<NexusDownloadUrlResponse>(json);

        if (response == null)
        {
            Log.Error($"[NexusMods] 反序列化失败，响应内容: {json}");
            return new List<string>();
        }

        var singleUrl = response.GetFirstUrl();

        if (string.IsNullOrEmpty(singleUrl))
        {
            Log.Error($"[NexusMods] 无法从响应中提取下载 URL，响应: {json}");
            return new List<string>();
        }

        downloadUrls = new List<string> { singleUrl };
        Log.Info($"[NexusMods] 使用 NXM key 获取下载链接成功，共 {downloadUrls.Count} 个 URL");
        return downloadUrls;
    }

    /// <summary>
    /// 解析 .nxm 链接，提取下载参数
    /// </summary>
    /// <param name="nxmLink">.nxm 链接，格式：nxm://{game_domain}/mods/{mod_id}/files/{file_id}?key={key}&expires={expires}&user_id={user_id}</param>
    public static (long modId, long fileId, string key, string expiry)? ParseNxmLink(string nxmLink)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(nxmLink) || !nxmLink.StartsWith("nxm://"))
            {
                Log.Warn($"[NexusMods] 无效的 .nxm 链接格式");
                return null;
            }

            // 移除 nxm:// 前缀
            var urlPart = nxmLink.Substring(6);
            var parts = urlPart.Split('/');

            if (parts.Length < 5)
            {
                Log.Warn($"[NexusMods] .nxm 链接格式不正确，期望格式：nxm://stardewvalley/mods/{{mod_id}}/files/{{file_id}}，实际长度：{parts.Length}");
                return null;
            }

            // 解析路径: {game_domain}/mods/{mod_id}/files/{file_id}
            var gameDomain = parts[0]; // "stardewvalley"
            var modIdStr = parts[2];   // "2400" (mod ID)
            var fileIdStr = parts[4];  // "154869?key=..." (file ID)

            if (!long.TryParse(modIdStr, out var modId))
            {
                Log.Warn($"[NexusMods] 无法解析 mod_id: {modIdStr}");
                return null;
            }

            // 从 fileIdStr 中分离出文件 ID 和查询参数
            var fileIdPart = fileIdStr.Split('?')[0];
            if (!long.TryParse(fileIdPart, out var fileId))
            {
                Log.Warn($"[NexusMods] 无法解析 file_id: {fileIdPart}");
                return null;
            }

            // 解析查询参数: ?key={key}&expires={expires}&user_id={user_id}
            string key = string.Empty;
            string expiry = string.Empty;

            var queryPart = parts.FirstOrDefault(p => p.Contains("?"));
            if (!string.IsNullOrEmpty(queryPart))
            {
                Log.Info($"[NexusMods] 找到查询参数部分: {queryPart}");
                var queryString = queryPart.Substring(queryPart.IndexOf('?') + 1);
                Log.Info($"[NexusMods] 查询字符串: {queryString}");
                var queryParams = queryString.Split('&');
                Log.Info($"[NexusMods] 查询参数数量: {queryParams.Length}");

                foreach (var param in queryParams)
                {
                    var keyValue = param.Split('=');
                    Log.Info($"[NexusMods] 解析参数: {param} -> 键: {keyValue[0]}, 值: {(keyValue.Length > 1 ? keyValue[1] : "(空)")}");

                    if (keyValue.Length >= 2)
                    {
                        if (keyValue[0] == "key")
                            key = keyValue[1];
                        else if (keyValue[0] == "expires" || keyValue[0] == "expiry")
                            expiry = keyValue[1];
                    }
                }
            }
            else
            {
                Log.Warn("[NexusMods] 未找到查询参数");
            }

            Log.Info($"[NexusMods] 解析 .nxm 链接成功: modId={modId}, fileId={fileId}, key={(!string.IsNullOrEmpty(key) ? key : "(无)")}, expiry={(!string.IsNullOrEmpty(expiry) ? expiry : "(无)")}");
            return (modId, fileId, key, expiry);
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[NexusMods] 解析 .nxm 链接失败: {nxmLink}");
            return null;
        }
    }
}
