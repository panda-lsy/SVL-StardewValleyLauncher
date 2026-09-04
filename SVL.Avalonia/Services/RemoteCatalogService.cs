using SVL.Avalonia.Models;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SVL.Avalonia.Services;

public sealed class RemoteCatalogService
{
    // Follow legacy desktop implementation: Stardew Valley gameId on curse.tools is 669.
    private const int StardewCurseforgeGameId = 669;
    private const string NexusGameDomain = "stardewvalley";
    private readonly AppUserSettingsStore _settingsStore;
    private CommunityLocalizationService? _localizationService;
    private static readonly object HttpClientLock = new();
    private static HttpClient? _httpClient;
    private static HttpClient? _directHttpClient;
    private static string _httpClientProxySignature = string.Empty;
    private static readonly TimeSpan NexusAuthNotifyCooldown = TimeSpan.FromSeconds(30);
    private DateTimeOffset _lastNexusAuthNotifiedAt = DateTimeOffset.MinValue;

    public Action<string>? DebugLogger { get; set; }

    public event Action<string>? NexusAuthExpired;

    public RemoteCatalogService(AppUserSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    /// <summary>注入社区汉化服务（启用缓存）。若不注入则回退到无缓存直连。</summary>
    public void SetLocalizationService(CommunityLocalizationService service)
    {
        _localizationService = service;
    }

    private HttpClient GetHttpClient(AppUserSettings? settings = null)
    {
        settings ??= _settingsStore.Load();
        var signature = BuildProxySignature(settings);

        lock (HttpClientLock)
        {
            if (_httpClient != null && string.Equals(signature, _httpClientProxySignature, StringComparison.Ordinal))
            {
                return _httpClient;
            }

            _httpClient?.Dispose();
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            if (settings.EnableDownloadProxy &&
                TryResolveProxyUri(settings.DownloadProxyUrl, out var proxyUri))
            {
                var proxy = new WebProxy(proxyUri);
                if (!string.IsNullOrWhiteSpace(settings.DownloadProxyUserName))
                {
                    proxy.Credentials = new NetworkCredential(
                        settings.DownloadProxyUserName.Trim(),
                        settings.DownloadProxyPassword ?? string.Empty);
                }

                handler.UseProxy = true;
                handler.Proxy = proxy;
            }

            _httpClient = new HttpClient(handler, disposeHandler: true);
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SVL-Avalonia-Migration");
            _httpClientProxySignature = signature;
            return _httpClient;
        }
    }

    private HttpClient GetDirectHttpClient()
    {
        lock (HttpClientLock)
        {
            if (_directHttpClient != null)
            {
                return _directHttpClient;
            }

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseProxy = false,
                Proxy = null
            };

            _directHttpClient = new HttpClient(handler, disposeHandler: true);
            _directHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SVL-Avalonia-Migration");
            return _directHttpClient;
        }
    }

    private static string BuildProxySignature(AppUserSettings settings)
    {
        if (!settings.EnableDownloadProxy)
        {
            return "disabled";
        }

        return string.Join('|',
            "enabled",
            settings.DownloadProxyUrl?.Trim() ?? string.Empty,
            settings.DownloadProxyUserName?.Trim() ?? string.Empty,
            string.IsNullOrWhiteSpace(settings.DownloadProxyUserName)
                ? "anonymous"
                : (string.IsNullOrEmpty(settings.DownloadProxyPassword) ? "user-np" : "user-p"));
    }

    private static bool TryResolveProxyUri(string? rawProxyUrl, out Uri proxyUri)
    {
        proxyUri = default!;
        if (string.IsNullOrWhiteSpace(rawProxyUrl))
        {
            return false;
        }

        var trimmed = rawProxyUrl.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var parsedProxyUri) && parsedProxyUri != null)
        {
            proxyUri = parsedProxyUri;
            return true;
        }

        if (!trimmed.Contains("://", StringComparison.Ordinal) &&
            Uri.TryCreate($"http://{trimmed}", UriKind.Absolute, out parsedProxyUri) &&
            parsedProxyUri != null)
        {
            proxyUri = parsedProxyUri;
            return true;
        }

        return false;
    }

    public async Task<List<ModSearchResultItem>> SearchModsAsync(string keyword, string source = "全部")
    {
        var settings = _settingsStore.Load();
        var includeNexus = string.Equals(source, "全部", StringComparison.Ordinal) ||
                           string.Equals(source, "NexusMods", StringComparison.Ordinal);
        var includeCurseforge = string.Equals(source, "全部", StringComparison.Ordinal) ||
                                string.Equals(source, "Curseforge", StringComparison.Ordinal);

        var results = new List<ModSearchResultItem>();

        if (includeNexus)
        {
            var nexusResults = await SearchNexusModsAsync(keyword, settings);
            results.AddRange(nexusResults.Select(item => ToSearchResultItem(item, CatalogSource.NexusMods, isModpack: false)));
        }

        if (includeCurseforge)
        {
            var curseforgeResults = await SearchCurseforgeModsAsync(keyword);
            results.AddRange(curseforgeResults.Select(item => ToSearchResultItem(item, CatalogSource.Curseforge, isModpack: false)));
        }

        return Deduplicate(results);
    }

    public async Task<List<ModSearchResultItem>> SearchModsAdvancedAsync(
        string keyword,
        string source = "全部",
        string gameVersion = "全部",
        string modType = "全部",
        bool useCommunityLocalization = true,
        bool hotOnly = false,
        int page = 1)
    {
        var settings = _settingsStore.Load();
        var includeNexus = string.Equals(source, "全部", StringComparison.Ordinal) ||
                           string.Equals(source, "NexusMods", StringComparison.Ordinal);
        var includeCurseforge = string.Equals(source, "全部", StringComparison.Ordinal) ||
                                string.Equals(source, "Curseforge", StringComparison.Ordinal);

        var normalizedKeyword = hotOnly ? string.Empty : keyword?.Trim() ?? string.Empty;
        var normalizedVersionFilter = NormalizeFilterToken(gameVersion);
        var normalizedModTypeFilter = NormalizeFilterToken(modType);
        var pageSize = hotOnly ? 20 : 20;
        var offset = Math.Max(0, (page - 1) * pageSize);

        LogDebug($"SearchModsAdvanced/start source={source}, hotOnly={hotOnly}, page={page}, offset={offset}, keyword='{normalizedKeyword}'");

        var results = new List<ModSearchResultItem>();

        if (includeNexus)
        {
            var nexusItems = await SearchNexusModsAsync(normalizedKeyword, settings, pageSize, offset);
            LogDebug($"SearchModsAdvanced/nexus raw={nexusItems.Count}");
            if (!string.IsNullOrWhiteSpace(normalizedModTypeFilter))
            {
                nexusItems = nexusItems
                    .Where(item => MatchesModTypeFilter(item.ModType, normalizedModTypeFilter))
                    .ToList();
            }

            if (!string.IsNullOrWhiteSpace(normalizedVersionFilter))
            {
                nexusItems = nexusItems
                    .Where(item => MatchesGameVersionFilter(item.SupportedGameVersions, item.GameVersionTag, normalizedVersionFilter))
                    .ToList();
            }

            if (useCommunityLocalization)
            {
                await ApplyCommunityLocalizationAsync(nexusItems, "NexusMods");
            }

            LogDebug($"SearchModsAdvanced/nexus filtered={nexusItems.Count}");

            results.AddRange(nexusItems.Take(10).Select(item => ToSearchResultItem(item, CatalogSource.NexusMods, isModpack: false)));
        }

        if (includeCurseforge)
        {
            LogDebug($"SearchModsAdvanced/curse url={BuildCurseforgeSearchUrl(normalizedKeyword, pageSize)}");
            var curseforgeItems = await SearchCurseforgeModsAsync(normalizedKeyword, pageSize, offset);
            LogDebug($"SearchModsAdvanced/curse raw={curseforgeItems.Count}");
            if (!string.IsNullOrWhiteSpace(normalizedModTypeFilter))
            {
                curseforgeItems = curseforgeItems
                    .Where(item => MatchesModTypeFilter(item.ModType, normalizedModTypeFilter))
                    .ToList();
            }

            if (!string.IsNullOrWhiteSpace(normalizedVersionFilter))
            {
                curseforgeItems = curseforgeItems
                    .Where(item => MatchesGameVersionFilter(item.SupportedGameVersions, item.GameVersionTag, normalizedVersionFilter))
                    .ToList();
            }

            if (useCommunityLocalization)
            {
                await ApplyCommunityLocalizationAsync(curseforgeItems, "Curseforge");
            }

            LogDebug($"SearchModsAdvanced/curse filtered={curseforgeItems.Count}");

            results.AddRange(curseforgeItems.Take(10).Select(item => ToSearchResultItem(item, CatalogSource.Curseforge, isModpack: false)));
        }

        var deduplicated = Deduplicate(results);
        LogDebug($"SearchModsAdvanced/done total={deduplicated.Count}");
        return deduplicated;
    }

    public async Task<CatalogPagedResult> SearchModsAdvancedPagedAsync(
        string keyword,
        string source = "全部",
        string gameVersion = "全部",
        string modType = "全部",
        bool useCommunityLocalization = true,
        bool hotOnly = false,
        int page = 1,
        int pageSize = 10)
    {
        var settings = _settingsStore.Load();
        var includeNexus = string.Equals(source, "全部", StringComparison.Ordinal) ||
                           string.Equals(source, "NexusMods", StringComparison.Ordinal);
        var includeCurseforge = string.Equals(source, "全部", StringComparison.Ordinal) ||
                                string.Equals(source, "Curseforge", StringComparison.Ordinal);

        var normalizedKeyword = hotOnly ? string.Empty : keyword?.Trim() ?? string.Empty;
        var normalizedVersionFilter = NormalizeFilterToken(gameVersion);
        var normalizedModTypeFilter = NormalizeFilterToken(modType);
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 30);
        // 多请求 1 个用于判断是否有下一页
        var fetchCount = safePageSize + 1;
        var offset = (safePage - 1) * safePageSize;

        LogDebug($"SearchModsPaged/start source={source}, page={safePage}, offset={offset}, fetchCount={fetchCount}, keyword='{normalizedKeyword}'");

        var nexusItems = new List<RemoteSearchItem>();
        var curseItems = new List<RemoteSearchItem>();
        var nexusHasMore = false;
        var curseHasMore = false;

        // Kick off Nexus and Curseforge searches concurrently to reduce overall latency.
        var nexusSearchTask = includeNexus
            ? SearchNexusModsAsync(normalizedKeyword, settings, fetchCount, offset)
            : Task.FromResult(new List<RemoteSearchItem>());
        var curseSearchTask = includeCurseforge
            ? SearchCurseforgeModsAsync(normalizedKeyword, fetchCount, offset)
            : Task.FromResult(new List<RemoteSearchItem>());

        await Task.WhenAll(nexusSearchTask, curseSearchTask);

        if (includeNexus)
        {
            var nexusRaw = await nexusSearchTask;
            var nexusFiltered = nexusRaw
                .Where(item => MatchesModTypeFilter(item.ModType, normalizedModTypeFilter))
                .Where(item => MatchesGameVersionFilter(item.SupportedGameVersions, item.GameVersionTag, normalizedVersionFilter))
                .ToList();

            nexusHasMore = nexusFiltered.Count > safePageSize;
            nexusItems = nexusFiltered.Take(safePageSize).ToList();
            foreach (var item in nexusItems)
            {
                item.Source = CatalogSource.NexusMods;
                item.SourceTagHint = "NexusMod";
            }
        }

        if (includeCurseforge)
        {
            var curseRaw = await curseSearchTask;
            var curseFiltered = curseRaw
                .Where(item => MatchesModTypeFilter(item.ModType, normalizedModTypeFilter))
                .Where(item => MatchesGameVersionFilter(item.SupportedGameVersions, item.GameVersionTag, normalizedVersionFilter))
                .ToList();

            curseHasMore = curseFiltered.Count > safePageSize;
            curseItems = curseFiltered.Take(safePageSize).ToList();
            foreach (var item in curseItems)
            {
                item.Source = CatalogSource.Curseforge;
                item.SourceTagHint = "CurseforgeMod";
            }
        }

        var merged = MergeBySourceAlternating(nexusItems, curseItems)
            .Take(safePageSize)
            .ToList();

        // Apply community localization only to the items that will actually be displayed,
        // avoiding extra network calls for items dropped during merge/paging.
        if (useCommunityLocalization && merged.Count > 0)
        {
            var nexusMerged = merged.Where(item => item.Source == CatalogSource.NexusMods).ToList();
            var curseMerged = merged.Where(item => item.Source == CatalogSource.Curseforge).ToList();
            if (nexusMerged.Count > 0)
            {
                await ApplyCommunityLocalizationAsync(nexusMerged, "NexusMods");
            }
            if (curseMerged.Count > 0)
            {
                await ApplyCommunityLocalizationAsync(curseMerged, "Curseforge");
            }
        }

        var formatted = merged.Select(item => ToSearchResultItem(item, item.Source, isModpack: false)).ToList();

        return new CatalogPagedResult
        {
            Items = Deduplicate(formatted),
            HasMore = nexusHasMore || curseHasMore
        };
    }

    public async Task<List<string>> GetModGameVersionsAsync()
    {
        var versionSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var curseforgeVersions = await FetchCurseforgeGameVersionsAsync();
            foreach (var rawVersion in curseforgeVersions)
            {
                foreach (var token in ParsePossibleGameVersions(rawVersion))
                {
                    var normalized = NormalizeStardewGameVersionToken(token, keepPatch: true);
                    if (!string.IsNullOrWhiteSpace(normalized))
                    {
                        versionSet.Add(normalized);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogDebug($"GameVersions/load curseforge failed: {ex.Message}");
        }

        if (versionSet.Count == 0)
        {
            versionSet.UnionWith(["1.6", "1.5", "1.4"]);
        }

        var sorted = versionSet
            .OrderByDescending(ParseVersionSortValue)
            .ThenByDescending(version => version, StringComparer.OrdinalIgnoreCase)
            .ToList();

        LogDebug($"GameVersions/load total={sorted.Count}");
        return sorted;
    }

    private async Task<List<string>> FetchCurseforgeGameVersionsAsync()
    {
        var url = $"https://api.curse.tools/v1/games/{StardewCurseforgeGameId}/versions";
        LogDebug($"GameVersions/curse request url={url}");

        using var response = await GetWithRedirectAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadBodySnippetAsync(response, 240);
            LogDebug($"GameVersions/curse failed status={(int)response.StatusCode} {response.ReasonPhrase}, body={body}");
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var parsed = ParseCurseforgeGameVersions(doc.RootElement);
        LogDebug($"GameVersions/curse parsed={parsed.Count}");
        return parsed;
    }

    private static List<string> ParseCurseforgeGameVersions(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var versions = new List<string>();
        foreach (var group in data.EnumerateArray())
        {
            if (!group.TryGetProperty("versions", out var versionArray) || versionArray.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var versionElement in versionArray.EnumerateArray())
            {
                switch (versionElement.ValueKind)
                {
                    case JsonValueKind.String:
                    {
                        var value = versionElement.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            versions.Add(value);
                        }

                        break;
                    }
                    case JsonValueKind.Object:
                    {
                        var name = FirstNonEmpty(
                            TryGetString(versionElement, "name"),
                            TryGetString(versionElement, "slug"));
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            versions.Add(name);
                            break;
                        }

                        var id = TryGetLong(versionElement, "id");
                        if (id > 0)
                        {
                            versions.Add(id.ToString());
                        }

                        break;
                    }
                    case JsonValueKind.Number:
                        versions.Add(versionElement.GetRawText());
                        break;
                }
            }
        }

        return versions;
    }

    public async Task<CatalogPagedResult> SearchModpacksPagedAsync(
        string keyword,
        string source = "全部",
        int page = 1,
        int pageSize = 10)
    {
        var settings = _settingsStore.Load();
        var includeNexus = string.Equals(source, "全部", StringComparison.Ordinal) ||
                           string.Equals(source, "NexusMods", StringComparison.Ordinal);
        var includeCurseforge = string.Equals(source, "全部", StringComparison.Ordinal) ||
                                string.Equals(source, "Curseforge", StringComparison.Ordinal);

        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 30);
        var includeBothSources = includeNexus && includeCurseforge;
        var perSourceFetchCount = safePageSize;
        var mergedPageSize = includeBothSources ? safePageSize * 2 : safePageSize;

        var nexusItems = new List<RemoteSearchItem>();
        var curseItems = new List<RemoteSearchItem>();
        var nexusHasMore = false;
        var curseHasMore = false;

        if (includeNexus)
        {
            var nexusRaw = await SearchNexusCollectionsAsync(keyword, settings);
            nexusHasMore = nexusRaw.Count > safePage * perSourceFetchCount;
            nexusItems = nexusRaw
                .Skip((safePage - 1) * perSourceFetchCount)
                .Take(perSourceFetchCount)
                .ToList();
            foreach (var item in nexusItems)
            {
                item.Source = CatalogSource.NexusMods;
                item.SourceTagHint = "NexusPack";
            }
            nexusHasMore = nexusItems.Count >= perSourceFetchCount;
        }
        if (includeCurseforge)
        {
            var curseRaw = await SearchCurseforgeModpacksAsync(keyword);
            curseHasMore = curseRaw.Count > safePage * perSourceFetchCount;
            curseItems = curseRaw
                .Skip((safePage - 1) * perSourceFetchCount)
                .Take(perSourceFetchCount)
                .ToList();
            foreach (var item in curseItems)
            {
                item.Source = CatalogSource.Curseforge;
                item.SourceTagHint = "CurseforgePack";
            }
            await ApplyCommunityLocalizationAsync(curseItems, "Curseforge", "modpack");
        }

        await ApplyCommunityLocalizationAsync(nexusItems, "NexusMods", "collection");

        var merged = MergeBySourceAlternating(nexusItems, curseItems)
            .Take(safePageSize)
            .ToList();

        var formatted = merged.Select(item => ToSearchResultItem(item, item.Source, isModpack: true)).ToList();

        return new CatalogPagedResult
        {
            Items = Deduplicate(formatted),
            HasMore = nexusHasMore || curseHasMore
        };
    }

    private static List<RemoteSearchItem> MergeBySourceAlternating(
        List<RemoteSearchItem> first,
        List<RemoteSearchItem> second)
    {
        if (first.Count == 0)
        {
            return second;
        }

        if (second.Count == 0)
        {
            return first;
        }

        var merged = new List<RemoteSearchItem>(first.Count + second.Count);
        var max = Math.Max(first.Count, second.Count);
        for (var i = 0; i < max; i++)
        {
            if (i < first.Count)
            {
                merged.Add(first[i]);
            }

            if (i < second.Count)
            {
                merged.Add(second[i]);
            }
        }

        return merged;
    }

    private void LogDebug(string message)
    {
        DebugLogger?.Invoke(message);
        Debug.WriteLine($"[RemoteCatalogService] {message}");
    }

    public async Task<List<ModSearchResultItem>> SearchSmapiAsync(string keyword, string source = "全部")
    {
        var settings = _settingsStore.Load();
        var normalizedKeyword = NormalizeSmapiKeyword(keyword);
        var searchKeyword = string.IsNullOrWhiteSpace(normalizedKeyword)
            ? "SMAPI"
            : $"SMAPI {normalizedKeyword}";

        var includeGithub = string.Equals(source, "全部", StringComparison.Ordinal) ||
                            string.Equals(source, "GitHub", StringComparison.OrdinalIgnoreCase);
        var includeNexus = string.Equals(source, "全部", StringComparison.Ordinal) ||
                           string.Equals(source, "NexusMods", StringComparison.Ordinal);
        var includeCurseforge = string.Equals(source, "全部", StringComparison.Ordinal) ||
                                string.Equals(source, "Curseforge", StringComparison.Ordinal);

        LogDebug($"SearchSmapi/start source={source}, keyword='{normalizedKeyword}', query='{searchKeyword}'");
        var results = new List<ModSearchResultItem>();

        if (includeGithub)
        {
            var githubResults = await SearchGithubSmapiReleasesAsync(normalizedKeyword);
            LogDebug($"SearchSmapi/github raw={githubResults.Count}");
            results.AddRange(githubResults.Select(item => ToSearchResultItem(item, CatalogSource.GitHub, isModpack: false)));
        }

        if (includeNexus)
        {
            var nexusResults = await SearchNexusModsAsync(searchKeyword, settings);
            LogDebug($"SearchSmapi/nexus raw={nexusResults.Count}");
            results.AddRange(nexusResults.Select(item => ToSearchResultItem(item, CatalogSource.NexusMods, isModpack: false)));
        }

        if (includeCurseforge)
        {
            var curseforgeResults = await GetCurseforgeSmapiItemsAsync();
            LogDebug($"SearchSmapi/curse raw={curseforgeResults.Count}");
            results.AddRange(curseforgeResults.Select(item => ToSearchResultItem(item, CatalogSource.Curseforge, isModpack: false)));
        }

        var deduplicated = Deduplicate(results);
        LogDebug($"SearchSmapi/done total={deduplicated.Count}");
        return deduplicated;
    }

    private async Task<List<RemoteSearchItem>> GetCurseforgeSmapiItemsAsync()
    {
        const long smapiCurseforgeProjectId = 898372;
        var smapi = await GetCurseforgeModByIdAsync(smapiCurseforgeProjectId);
        if (smapi == null)
        {
            return [];
        }

        var normalizedName = smapi.Name.Contains("SMAPI", StringComparison.OrdinalIgnoreCase)
            ? smapi.Name
            : "SMAPI - Stardew Modding API";
        var normalizedSummary = string.IsNullOrWhiteSpace(smapi.Summary)
            ? "Stardew Valley 的模组加载 API（必须先安装）"
            : smapi.Summary;

        return
        [
            new RemoteSearchItem
            {
                ResourceId = smapi.ResourceId,
                Name = normalizedName,
                Summary = normalizedSummary,
                Stat = smapi.Stat,
                TimeTag = smapi.TimeTag,
                IconUrl = smapi.IconUrl,
                ModType = smapi.ModType,
                GameVersionTag = smapi.GameVersionTag,
                SupportedGameVersions = smapi.SupportedGameVersions,
                LocalizedName = smapi.LocalizedName,
                LocalizedSummary = smapi.LocalizedSummary
            }
        ];
    }

    public async Task<List<ModSearchResultItem>> SearchModpacksAsync(string keyword, string source = "全部")
    {
        var settings = _settingsStore.Load();
        var includeNexus = string.Equals(source, "全部", StringComparison.Ordinal) ||
                           string.Equals(source, "NexusMods", StringComparison.Ordinal);
        var includeCurseforge = string.Equals(source, "全部", StringComparison.Ordinal) ||
                                string.Equals(source, "Curseforge", StringComparison.Ordinal);

        var results = new List<ModSearchResultItem>();

        if (includeNexus)
        {
            var nexusCollections = await SearchNexusCollectionsAsync(keyword, settings);
            await ApplyCommunityLocalizationAsync(nexusCollections, "NexusMods", "collection");
            results.AddRange(nexusCollections.Select(item => ToSearchResultItem(item, CatalogSource.NexusMods, isModpack: true)));
        }

        if (includeCurseforge)
        {
            var curseforgeModpacks = await SearchCurseforgeModpacksAsync(keyword);
            await ApplyCommunityLocalizationAsync(curseforgeModpacks, "Curseforge", "modpack");
            results.AddRange(curseforgeModpacks.Select(item => ToSearchResultItem(item, CatalogSource.Curseforge, isModpack: true)));
        }

        return Deduplicate(results);
    }

    public async Task<CatalogResourceDetails> GetResourceDetailsAsync(CatalogResourceIdentity identity)
    {
        if (identity.ResourceId <= 0 && identity.Source == CatalogSource.Unknown)
        {
            return CatalogResourceDetails.Empty;
        }

        var settings = _settingsStore.Load();
        var details = identity.Source switch
        {
            CatalogSource.GitHub => await GetGithubSmapiDetailsAsync(identity),
            // NexusMods Collection（IsModpack 或有 CollectionSlug）走 Collection 详情流程
            CatalogSource.NexusMods when identity.IsModpack || !string.IsNullOrWhiteSpace(identity.CollectionSlug)
                => await GetNexusCollectionDetailsAsync(identity, settings),
            CatalogSource.NexusMods => await GetNexusResourceDetailsAsync(identity, settings),
            CatalogSource.Curseforge => await GetCurseforgeResourceDetailsAsync(identity),
            _ => CatalogResourceDetails.Empty
        };

        return await ApplyCommunityLocalizationToDetailsAsync(identity, details);
    }

    /// <summary>
    /// displayText 字符串重载（DownloadPage/VersionSettingsPage 未迁移到结构化模型，仍传 displayText）。
    /// 内部解析 header 的 [Source#Id] 构造 CatalogResourceIdentity 后委托给结构化重载。
    /// </summary>
    public async Task<CatalogResourceDetails> GetResourceDetailsAsync(string displayText)
    {
        if (string.IsNullOrWhiteSpace(displayText))
        {
            return CatalogResourceDetails.Empty;
        }

        var identity = ParseIdentityFromDisplayText(displayText);
        return await GetResourceDetailsAsync(identity);
    }

    private static CatalogResourceIdentity ParseIdentityFromDisplayText(string displayText)
    {
        var header = displayText;
        var pipeIndex = displayText.IndexOf('|');
        if (pipeIndex >= 0)
        {
            header = displayText[..pipeIndex].Trim();
        }

        if (!header.StartsWith("[", StringComparison.Ordinal))
        {
            return new CatalogResourceIdentity(0, header, CatalogSource.Unknown, false, string.Empty);
        }

        var closeIndex = header.IndexOf(']');
        if (closeIndex <= 1)
        {
            return new CatalogResourceIdentity(0, header, CatalogSource.Unknown, false, string.Empty);
        }

        var sourceSegment = header[1..closeIndex].Trim();
        var nameSegment = header[(closeIndex + 1)..].Trim();
        var sourceParts = sourceSegment.Split('#', 2, StringSplitOptions.TrimEntries);
        var sourceToken = sourceParts[0];
        var resourceIdText = sourceParts.Length > 1 ? sourceParts[1] : string.Empty;
        long.TryParse(resourceIdText, out var resourceId);

        var source = ResolveCatalogSource(sourceToken);
        var isModpack = sourceToken.Contains("Pack", StringComparison.OrdinalIgnoreCase) ||
                        sourceToken.Contains("Collection", StringComparison.OrdinalIgnoreCase);

        // 从 displayText 段中解析 slug=xxx（Collection 详情所需）。
        var slug = ExtractDisplaySegment(displayText, "slug=");

        return new CatalogResourceIdentity(resourceId, nameSegment, source, isModpack, slug);
    }

    private static string ExtractDisplaySegment(string displayText, string prefix)
    {
        if (string.IsNullOrWhiteSpace(displayText))
        {
            return string.Empty;
        }

        var parts = displayText.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in parts)
        {
            if (segment.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return segment[prefix.Length..].Trim();
            }
        }

        return string.Empty;
    }

    private static CatalogSource ResolveCatalogSource(string sourceToken)
    {
        if (sourceToken.Contains("github", StringComparison.OrdinalIgnoreCase))
        {
            return CatalogSource.GitHub;
        }

        if (sourceToken.Contains("nexus", StringComparison.OrdinalIgnoreCase))
        {
            return CatalogSource.NexusMods;
        }

        if (sourceToken.Contains("curse", StringComparison.OrdinalIgnoreCase))
        {
            return CatalogSource.Curseforge;
        }

        return CatalogSource.Unknown;
    }

    private async Task<CatalogResourceDetails> ApplyCommunityLocalizationToDetailsAsync(
        CatalogResourceIdentity identity,
        CatalogResourceDetails details)
    {
        // 整合包/Collection 也需要社区汉化
        var entityType = identity.IsModpack ? "modpack" : "mod";
        var platform = identity.Source switch
        {
            CatalogSource.NexusMods => "NexusMods",
            CatalogSource.Curseforge => "Curseforge",
            _ => string.Empty
        };

        if (identity.IsModpack && string.Equals(platform, "NexusMods", StringComparison.OrdinalIgnoreCase))
        {
            // Nexus Collection 详情用 collection 类型（但详情页暂无 slug，跳过）
            return details;
        }

        if (!identity.IsModpack && (identity.ResourceId <= 0 || string.IsNullOrWhiteSpace(platform)))
        {
            return details;
        }

        if (identity.IsModpack && identity.ResourceId <= 0)
        {
            return details;
        }

        try
        {
            var (nameZhCn, summaryZhCn, contributor) = await FetchCommunityLocalizationAsync(platform, identity.ResourceId.ToString(), entityType);
            var hasName = !string.IsNullOrWhiteSpace(nameZhCn);
            var hasSummary = !string.IsNullOrWhiteSpace(summaryZhCn);
            var hasContributor = !string.IsNullOrWhiteSpace(contributor);
            if (!hasName && !hasSummary && !hasContributor)
            {
                return details;
            }

            var effectiveContributor = hasContributor ? contributor : details.LocalizedContributor;
            if (!hasName && !hasSummary && string.Equals(effectiveContributor, details.LocalizedContributor, StringComparison.Ordinal))
            {
                return details;
            }

            return new CatalogResourceDetails
            {
                Name = details.Name,
                Source = details.Source,
                Summary = details.Summary,
                IconUrl = details.IconUrl,
                FullIconUrl = details.FullIconUrl,
                LocalizedContributor = effectiveContributor,
                LocalizedName = hasName ? nameZhCn : details.LocalizedName,
                LocalizedSummary = hasSummary ? summaryZhCn : details.LocalizedSummary,
                VersionOptions = details.VersionOptions,
                Dependencies = details.Dependencies,
                DownloadOptions = details.DownloadOptions
            };
        }
        catch
        {
            return details;
        }
    }

    private static string NormalizeSmapiKeyword(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return string.Empty;
        }

        var normalized = keyword.Trim();
        if (normalized.StartsWith("SMAPI", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[5..].Trim();
        }

        return normalized;
    }

    private async Task<List<RemoteSearchItem>> SearchGithubSmapiReleasesAsync(string keyword)
    {
        var (starCount, repositoryAvatar) = await GetGithubSmapiRepositoryStatsAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/Pathoschild/SMAPI/releases?per_page=20");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await GetHttpClient().SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            return
            [
                new RemoteSearchItem
                {
                    ResourceId = 0,
                    Name = "SMAPI - Stardew Modding API",
                    Summary = "官方发布页，包含稳定版与预发布版。",
                    Stat = starCount > 0 ? $"Star {starCount:N0}" : "GitHub",
                    TimeTag = string.Empty,
                    IconUrl = repositoryAvatar
                }
            ];
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var filter = NormalizeSmapiKeyword(keyword);
        var result = new List<RemoteSearchItem>();
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            var tag = TryGetString(release, "tag_name");
            var name = TryGetString(release, "name");
            var title = string.IsNullOrWhiteSpace(name)
                ? (string.IsNullOrWhiteSpace(tag) ? "SMAPI 发布" : $"SMAPI {tag}")
                : name;

            var summary = ExtractGithubReleaseSummary(release);
            if (!string.IsNullOrWhiteSpace(filter) &&
                title.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                tag.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                summary.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            var publishedAt = TryGetString(release, "published_at");
            var stat = starCount > 0 ? $"Star {starCount:N0}" : "GitHub";
            var timeTag = TryFormatTimeTag(publishedAt);
            var iconUrl = TryGetNestedString(release, "author", "avatar_url");
            if (string.IsNullOrWhiteSpace(iconUrl))
            {
                iconUrl = repositoryAvatar;
            }

            result.Add(new RemoteSearchItem
            {
                ResourceId = TryGetLong(release, "id"),
                Name = title,
                Summary = summary,
                Stat = stat,
                TimeTag = timeTag,
                IconUrl = iconUrl
            });
        }

        return result.Count > 0
            ? result
            :
            [
                new RemoteSearchItem
                {
                    ResourceId = 0,
                    Name = "SMAPI - Stardew Modding API",
                    Summary = "未匹配到发布版本，可打开详情查看官方发布地址。",
                    Stat = starCount > 0 ? $"Star {starCount:N0}" : "GitHub",
                    TimeTag = string.Empty,
                    IconUrl = repositoryAvatar
                }
            ];
    }

    private async Task<(long Stars, string AvatarUrl)> GetGithubSmapiRepositoryStatsAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/Pathoschild/SMAPI");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await GetHttpClient().SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            return (0, string.Empty);
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var stars = TryGetLong(doc.RootElement, "stargazers_count");
        var avatar = TryGetNestedString(doc.RootElement, "owner", "avatar_url");
        return (stars, avatar);
    }

    private static string ExtractGithubReleaseSummary(JsonElement release)
    {
        var body = TryGetString(release, "body");
        if (string.IsNullOrWhiteSpace(body))
        {
            return "SMAPI 官方发布版本";
        }

        var lines = body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var firstLine = lines.FirstOrDefault(line => !line.StartsWith("#", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return "SMAPI 官方发布版本";
        }

        return firstLine.Length > 140 ? firstLine[..140] + "..." : firstLine;
    }

    private async Task<List<RemoteSearchItem>> SearchCurseforgeModsAsync(string keyword, int pageSize = 20, int index = 0)
    {
        var url = BuildCurseforgeSearchUrl(keyword, pageSize, index);
        LogDebug($"Curseforge/search request url={url}");
        try
        {
            using var response = await GetWithRedirectAsync(url);
            LogDebug($"Curseforge/search response status={(int)response.StatusCode} {response.ReasonPhrase}, isSuccess={response.IsSuccessStatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await SafeReadBodySnippetAsync(response, 300);
                LogDebug($"Curseforge/search error body: {errorBody}");
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var items = ParseCurseforgeItems(doc.RootElement, onlyLikelyModpacks: false);
            await FillMissingCurseforgeIconsAsync(items);
            LogDebug($"Curseforge/search parsed={items.Count}");

            return items;
        }
        catch (Exception ex)
        {
            LogDebug($"Curseforge/search exception: {ex.Message}");
            return [];
        }
    }

    private async Task FillMissingCurseforgeIconsAsync(IEnumerable<RemoteSearchItem> items)
    {
        var list = items?.ToList();

        if (list == null || list.Count == 0)
        {
            return;
        }

        for (var index = 0; index < list.Count; index++)
        {
            var item = list[index];
            if (item == null || item.ResourceId <= 0 || !string.IsNullOrWhiteSpace(item.IconUrl))
            {
                continue;
            }

            var detail = await GetCurseforgeModByIdAsync(item.ResourceId);
            if (detail == null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(detail.IconUrl))
            {
                list[index] = new RemoteSearchItem
                {
                    ResourceId = item.ResourceId,
                    Name = item.Name,
                    Summary = item.Summary,
                    Stat = item.Stat,
                    TimeTag = item.TimeTag,
                    IconUrl = detail.IconUrl,
                    FullIconUrl = FirstNonEmpty(detail.FullIconUrl, detail.IconUrl),
                    ModType = item.ModType,
                    GameVersionTag = item.GameVersionTag,
                    SupportedGameVersions = item.SupportedGameVersions,
                    LocalizedName = item.LocalizedName,
                    LocalizedSummary = item.LocalizedSummary
                };
            }
        }

        if (items is List<RemoteSearchItem> writable)
        {
            writable.Clear();
            writable.AddRange(list);
        }
    }

    private async Task<RemoteSearchItem?> GetCurseforgeModByIdAsync(long modId)
    {
        if (modId <= 0)
        {
            return null;
        }

        var url = $"https://api.curse.tools/v1/mods/{modId}";
        LogDebug($"Curseforge/mod detail request url={url}");
        using var response = await GetWithRedirectAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            LogDebug($"Curseforge/mod detail failed status={(int)response.StatusCode} {response.ReasonPhrase}");
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        if (!doc.RootElement.TryGetProperty("data", out var itemElement) || itemElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ParseCurseforgeItem(itemElement, onlyLikelyModpacks: false);
    }

    private async Task<List<RemoteSearchItem>> SearchCurseforgeModpacksAsync(string keyword)
    {
        // 参考旧架构：通过 classId 过滤 Modpacks 分类，而非关键词拼接
        var modpackClassId = await TryGetModpackClassIdAsync();
        var url = BuildCurseforgeSearchUrl(keyword, 20, 0, modpackClassId);
        LogDebug($"Curseforge/modpacks request url={url} (classId={modpackClassId})");
        using var response = await GetWithRedirectAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            LogDebug($"Curseforge/modpacks failed status={(int)response.StatusCode} {response.ReasonPhrase}");
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        // 不使用 ParseCurseforgeItems（会排除 modpack），直接解析所有结果
        var items = ParseCurseforgeItemsRaw(doc.RootElement);

        // 参考旧架构 IsLikelyModpack：classId 获取失败时客户端兜底过滤
        if (modpackClassId <= 0)
        {
            items = items.Where(IsLikelyModpack).ToList();
        }

        await FillMissingCurseforgeIconsAsync(items);
        LogDebug($"Curseforge/modpacks parsed={items.Count}");
        return items;
    }

    /// <summary>
    /// 判断 Curseforge 项目是否可能是 Modpack（启发式规则）。
    /// 参考旧架构 CurseforgeApiService.IsLikelyModpack，当 classId 过滤不可用时作为兜底。
    /// </summary>
    private static bool IsLikelyModpack(RemoteSearchItem item)
    {
        if (item == null) return false;

        if ((item.Name?.IndexOf("modpack", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
            return true;

        if ((item.Summary?.IndexOf("modpack", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
            return true;

        return false;
    }

    /// <summary>
    /// 获取 Stardew Valley 在 CurseForge 的 Modpacks 顶层分类 ID。
    /// Stardew Valley (gameId=669) 的 Modpacks 分类 ID 为 6771（固定值，经 API 验证）。
    /// 旧架构通过 /v1/cf/categories 端点动态获取，但该端点在新架构中存在 302 HTTPS→HTTP 降级问题，
    /// 且 /v1/mods/categories 返回 404。直接使用已验证的固定值，避免网络请求和重定向问题。
    /// </summary>
    private static int? _cachedModpackClassId;

    /// <summary>Stardew Valley (gameId=669) 的 CurseForge Modpacks 顶层分类 ID（经 API 验证为固定值）。</summary>
    private const int StardewModpackClassId = 6771;

    /// <summary>
    /// 获取 Stardew Valley 在 CurseForge 的 Modpacks 顶层分类 ID。
    /// 直接使用固定值 6771，避免 /v1/cf/categories 端点的 302 重定向问题。
    /// </summary>
    private Task<int> TryGetModpackClassIdAsync()
    {
        if (!_cachedModpackClassId.HasValue)
        {
            _cachedModpackClassId = StardewModpackClassId;
            LogDebug($"Curseforge/modpacks using classId={StardewModpackClassId}");
        }
        return Task.FromResult(_cachedModpackClassId.Value);
    }
    /// <summary>
    /// 原始解析 CurseForge 搜索结果，不做 modpack 过滤
    /// </summary>
    private static List<RemoteSearchItem> ParseCurseforgeItemsRaw(JsonElement root)
    {
        var result = new List<RemoteSearchItem>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in data.EnumerateArray())
        {
            var name = TryGetString(item, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;

            var summary = TryGetString(item, "summary");
            var downloadCount = TryGetLong(item, "downloadCount");
            var timeTag = TryFormatTimeTag(TryGetString(item, "dateModified"));
            var fullIconUrl = TryGetNestedString(item, "logo", "url");
            var iconUrl = TryGetNestedString(item, "logo", "thumbnailUrl") ?? fullIconUrl;
            var resourceId = TryGetLong(item, "id");

            result.Add(new RemoteSearchItem
            {
                ResourceId = resourceId,
                Name = name,
                Summary = summary,
                Stat = downloadCount > 0 ? $"Downloads {downloadCount:N0}" : string.Empty,
                TimeTag = timeTag,
                IconUrl = iconUrl,
                FullIconUrl = fullIconUrl
            });
        }

        return result;
    }

    private static string BuildCurseforgeSearchUrl(string keyword, int pageSize = 20, int index = 0, int classId = 0)
    {
        var normalizedPageSize = Math.Clamp(pageSize, 1, 50);
        var baseUrl = $"https://api.curse.tools/v1/mods/search?gameId={StardewCurseforgeGameId}&pageSize={normalizedPageSize}&index={index}&sortField=2&sortOrder=desc";
        if (classId > 0)
        {
            baseUrl += $"&classId={classId}";
        }
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return baseUrl;
        }

        return baseUrl + $"&searchFilter={Uri.EscapeDataString(keyword.Trim())}";
    }

    private async Task<HttpResponseMessage> GetWithRedirectAsync(string url, int maxRedirects = 3)
    {
        // CurseForge 使用专用客户端，已处理重定向，无需 fallback
        if (IsCurseforgeApiUrl(url))
        {
            return await GetWithRedirectCoreAsync(url, maxRedirects, forceDirect: false, settings: null!);
        }

        var settings = _settingsStore.Load();
        var response = await GetWithRedirectCoreAsync(url, maxRedirects, forceDirect: false, settings);

        // CurseForge 使用独立客户端，无需 proxy fallback
        if (!settings.EnableDownloadProxy || IsCurseforgeApiUrl(url) || response.IsSuccessStatusCode)
        {
            return response;
        }

        var body = await SafeReadBodySnippetAsync(response, 280);
        if (!ShouldFallbackToDirectForCurseforge(response.StatusCode, body))
        {
            return response;
        }

        LogDebug($"Curseforge/proxy-fallback enabled status={(int)response.StatusCode}, body={body}");
        response.Dispose();
        return await GetWithRedirectCoreAsync(url, maxRedirects, forceDirect: true, settings);
    }

    /// <summary>
    /// 创建 CurseForge 专用 HttpClient
    /// 直接使用 /v1/mods/ 路径，避免 /v1/cf/ 302 重定向链中的 HTTPS→HTTP 降级问题
    /// </summary>
    private static HttpClient CreateCurseforgeHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "SVL-StardewValleyLauncher/1.0");
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        return client;
    }

    private async Task<HttpResponseMessage> GetWithRedirectCoreAsync(
        string url,
        int maxRedirects,
        bool forceDirect,
        AppUserSettings settings)
    {
        // CurseForge 使用专用客户端（对齐 WPF，自动跟随重定向）
        if (IsCurseforgeApiUrl(url))
        {
            using var cfClient = CreateCurseforgeHttpClient();
            return await cfClient.GetAsync(url);
        }

        var currentUrl = url;
        var visitedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { url };

        for (var index = 0; index <= maxRedirects; index++)
        {
            var response = await (forceDirect ? GetDirectHttpClient() : GetHttpClient(settings)).GetAsync(currentUrl);
            if (!IsRedirectStatusCode(response.StatusCode) || response.Headers.Location == null)
            {
                return response;
            }

            var currentUri = new Uri(currentUrl);
            var nextUri = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(currentUri, response.Headers.Location);

            nextUri = SanitizeCurseforgeRedirectUri(currentUri, nextUri);
            var nextUrl = nextUri.ToString();

            // 检测 Cloudflare 拦截（cdn-cgi 挑战页），必须在 loop 检测之前
            if (nextUrl.Contains("cdn-cgi", StringComparison.OrdinalIgnoreCase))
            {
                LogDebug($"Curseforge/Cloudflare challenge detected, API is blocked by bot protection");
                response.Dispose();
                return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    ReasonPhrase = "CurseForge API blocked by Cloudflare"
                };
            }

            // 检测重定向循环（同一 URL 再次出现）
            if (!visitedUrls.Add(nextUrl))
            {
                LogDebug($"Curseforge/redirect loop at {nextUrl}");
                response.Dispose();
                return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    ReasonPhrase = "CurseForge API redirect loop"
                };
            }

            LogDebug($"Curseforge/redirect {(int)response.StatusCode} -> {nextUrl}");
            response.Dispose();
            currentUrl = nextUrl;
        }

        return await (forceDirect ? GetDirectHttpClient() : GetHttpClient(settings)).GetAsync(currentUrl);
    }

    private static bool IsCurseforgeApiUrl(string rawUrl)
    {
        return Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) && IsCurseforgeApiHost(uri.Host);
    }

    private static bool IsCurseforgeApiHost(string host)
    {
        return host.Equals("api.curse.tools", StringComparison.OrdinalIgnoreCase);
    }

    private static Uri SanitizeCurseforgeRedirectUri(Uri currentUri, Uri redirectUri)
    {
        if (!IsCurseforgeApiHost(redirectUri.Host))
        {
            return redirectUri;
        }

        var builder = new UriBuilder(redirectUri);
        var changed = false;

        if (builder.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            builder.Scheme = Uri.UriSchemeHttps;
            builder.Port = -1;
            changed = true;
        }

        // 部分代理会把 /v1/cf/* 错误重写到 /v1/*，这里保持原始 cf 路径。
        if (currentUri.AbsolutePath.Contains("/v1/cf/", StringComparison.OrdinalIgnoreCase) &&
            !builder.Path.Contains("/v1/cf/", StringComparison.OrdinalIgnoreCase))
        {
            builder.Path = currentUri.AbsolutePath;
            builder.Query = currentUri.Query.TrimStart('?');
            changed = true;
        }

        return changed ? builder.Uri : redirectUri;
    }

    private static bool ShouldFallbackToDirectForCurseforge(HttpStatusCode statusCode, string bodySnippet)
    {
        // Cloudflare 拦截或服务不可用，直连也无法解决
        if (statusCode is HttpStatusCode.ServiceUnavailable)
        {
            return false;
        }

        if (statusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            return true;
        }

        // Cloudflare 挑战页面，直连也无法解决
        if (bodySnippet.Contains("cdn-cgi", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return bodySnippet.Contains("plain HTTP request was sent to HTTPS port", StringComparison.OrdinalIgnoreCase) ||
               bodySnippet.Contains("/v1/mods/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRedirectStatusCode(System.Net.HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code is 301 or 302 or 303 or 307 or 308;
    }

    private static List<RemoteSearchItem> ParseCurseforgeItems(JsonElement root, bool onlyLikelyModpacks)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<RemoteSearchItem>();
        foreach (var item in data.EnumerateArray())
        {
            var parsed = ParseCurseforgeItem(item, onlyLikelyModpacks);
            if (parsed != null)
            {
                result.Add(parsed);
            }
        }

        return result;
    }

    private static RemoteSearchItem? ParseCurseforgeItem(JsonElement item, bool onlyLikelyModpacks)
    {
        var name = TryGetString(item, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var summary = TryGetString(item, "summary");
        var downloadCount = TryGetLong(item, "downloadCount");
        var timeTag = TryFormatTimeTag(TryGetString(item, "dateModified"));
        var fullIconUrl = TryGetNestedString(item, "logo", "url");
        var iconUrl = TryGetNestedString(item, "logo", "thumbnailUrl");
        if (string.IsNullOrWhiteSpace(iconUrl))
        {
            iconUrl = fullIconUrl;
        }

        var isLikelyModpack = IsLikelyModpack(item);
        if (onlyLikelyModpacks && !isLikelyModpack)
        {
            return null;
        }

        if (!onlyLikelyModpacks && isLikelyModpack)
        {
            return null;
        }

        var modType = ResolveCurseforgeModType(item);
        var supportedVersions = ResolveCurseforgeGameVersions(item, summary, name);

        return new RemoteSearchItem
        {
            ResourceId = TryGetLong(item, "id"),
            Name = name,
            Summary = summary,
            Stat = downloadCount > 0 ? $"下载 {downloadCount:N0}" : string.Empty,
            TimeTag = timeTag,
            IconUrl = iconUrl,
            FullIconUrl = fullIconUrl,
            ModType = modType,
            GameVersionTag = supportedVersions.FirstOrDefault() ?? string.Empty,
            SupportedGameVersions = supportedVersions
        };
    }

    private static bool IsLikelyModpack(JsonElement item)
    {
        var name = TryGetString(item, "name");
        var summary = TryGetString(item, "summary");

        if (name.Contains("modpack", StringComparison.OrdinalIgnoreCase) ||
            summary.Contains("modpack", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!item.TryGetProperty("categories", out var categories) || categories.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var category in categories.EnumerateArray())
        {
            var categoryName = TryGetString(category, "name");
            var categorySlug = TryGetString(category, "slug");
            if (categoryName.Contains("modpack", StringComparison.OrdinalIgnoreCase) ||
                categorySlug.Contains("modpack", StringComparison.OrdinalIgnoreCase) ||
                categoryName.Contains("collection", StringComparison.OrdinalIgnoreCase) ||
                categorySlug.Contains("collection", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<List<RemoteSearchItem>> SearchNexusModsAsync(string keyword, AppUserSettings settings, int count = 20, int offset = 0)
    {
        if (!HasNexusCredential(settings))
        {
            HandleNexusAuthExpired("Nexus/search-mods-no-credential", settings, HttpStatusCode.Unauthorized, "No authentication method");
            return [];
        }

        var normalized = string.IsNullOrWhiteSpace(keyword) ? string.Empty : keyword.Trim();
        var safeCount = Math.Clamp(count, 1, 80);
        var graphQlQuery = @"
            query SearchModsByGame($filter: ModsFilter, $sort: [ModsSort!], $offset: Int, $count: Int) {
                mods(filter: $filter, sort: $sort, offset: $offset, count: $count) {
                    nodes {
                        modId
                        name
                        summary
                        description
                        pictureUrl
                        downloads
                        category
                        updatedAt
                    }
                }
            }";

        var filterCandidates = new List<object>();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            filterCandidates.Add(new
            {
                gameDomainName = new[] { new { op = "EQUALS", value = NexusGameDomain } }
            });
        }
        else
        {
            var wildcard = normalized.Contains('*') ? normalized : $"*{normalized}*";
            var prefixWildcard = normalized.Contains('*') ? normalized : $"{normalized}*";
            filterCandidates.Add(new
            {
                gameDomainName = new[] { new { op = "EQUALS", value = NexusGameDomain } },
                name = new[] { new { op = "WILDCARD", value = wildcard } }
            });
            filterCandidates.Add(new
            {
                gameDomainName = new[] { new { op = "EQUALS", value = NexusGameDomain } },
                name = new[] { new { op = "WILDCARD", value = prefixWildcard } }
            });
            filterCandidates.Add(new
            {
                gameDomainName = new[] { new { op = "EQUALS", value = NexusGameDomain } },
                name = new[] { new { op = "EQUALS", value = normalized } }
            });
        }

        foreach (var filter in filterCandidates)
        {
            var requestBody = new
            {
                query = graphQlQuery,
                variables = new
                {
                    filter,
                    sort = new[]
                    {
                        new
                        {
                            downloads = new { direction = "DESC" }
                        }
                    },
                    offset = offset,
                    count = safeCount
                }
            };

            using var doc = await ExecuteNexusGraphQlAsync(requestBody, settings);
            if (doc == null)
            {
                continue;
            }

            var parsed = ParseNexusModsFromGraphQl(doc.RootElement);
            if (parsed.Count > 0)
            {
                return parsed;
            }
        }

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return [];
        }

        var fallbackCount = Math.Min(Math.Max(safeCount * 8, 120), 240);
        var fallbackRequestBody = new
        {
            query = graphQlQuery,
            variables = new
            {
                filter = new
                {
                    gameDomainName = new[] { new { op = "EQUALS", value = NexusGameDomain } }
                },
                sort = new[]
                {
                    new
                    {
                        downloads = new { direction = "DESC" }
                    }
                },
                offset = 0,
                count = fallbackCount
            }
        };

        using var fallbackDoc = await ExecuteNexusGraphQlAsync(fallbackRequestBody, settings);
        if (fallbackDoc == null)
        {
            return [];
        }

        return ParseNexusModsFromGraphQl(fallbackDoc.RootElement)
            .Where(item => item.Name.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                           item.Summary.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            .Take(safeCount)
            .ToList();
    }

    private static List<RemoteSearchItem> ParseNexusModsFromGraphQl(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("mods", out var mods) ||
            !mods.TryGetProperty("nodes", out var nodes) ||
            nodes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<RemoteSearchItem>();
        foreach (var row in nodes.EnumerateArray())
        {
            var id = TryGetLong(row, "modId");
            if (id <= 0)
            {
                continue;
            }

            var name = TryGetString(row, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var summary = FirstNonEmpty(TryGetString(row, "summary"), TryGetString(row, "description"));
            var pictureUrl = FirstNonEmpty(
                TryGetString(row, "pictureUrl"),
                TryGetString(row, "picture_url"));
            var versions = ParsePossibleGameVersions(summary);
            results.Add(new RemoteSearchItem
            {
                ResourceId = id,
                Name = name,
                Summary = summary,
                Stat = BuildDownloadsMetric("downloads", TryGetLong(row, "downloads")),
                TimeTag = BuildTimeTag(TryGetString(row, "updatedAt")),
                IconUrl = pictureUrl,
                FullIconUrl = pictureUrl,
                ModType = NormalizeModTypeTag($"{TryGetString(row, "category")} {summary}"),
                SupportedGameVersions = versions,
                GameVersionTag = versions.FirstOrDefault() ?? string.Empty,
                Source = CatalogSource.NexusMods,
                SourceTagHint = "NexusMod"
            });
        }

        return results;
    }

    private async Task<List<RemoteSearchItem>> SearchNexusCollectionsAsync(string keyword, AppUserSettings settings)
    {
        if (!HasNexusCredential(settings))
        {
            HandleNexusAuthExpired("Nexus/search-collections-no-credential", settings, HttpStatusCode.Unauthorized, "No authentication method");
            return [];
        }

        var normalized = keyword?.Trim() ?? string.Empty;
        var graphQlQuery = @"
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
                  slug
                  name
                  summary
                  totalDownloads
                  updatedAt
                  tileImage { url }
                }
              }
            }";

        var filterCandidates = new object[]
        {
            new
            {
                gameDomain = new[] { new { op = "EQUALS", value = NexusGameDomain } },
                collectionStatus = new[] { new { op = "EQUALS", value = "published" } }
            },
            new
            {
                gameDomainName = new[] { new { op = "EQUALS", value = NexusGameDomain } },
                collectionStatus = new[] { new { op = "EQUALS", value = "published" } }
            },
            new
            {
                gameDomain = new[] { new { op = "EQUALS", value = NexusGameDomain } }
            },
            new
            {
                gameDomainName = new[] { new { op = "EQUALS", value = NexusGameDomain } }
            }
        };

        foreach (var filter in filterCandidates)
        {
            var requestBody = new
            {
                query = graphQlQuery,
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
                    offset = 0,
                    count = 30
                }
            };

            using var doc = await ExecuteNexusGraphQlAsync(requestBody, settings);
            if (doc == null)
            {
                continue;
            }

            var collections = ParseNexusCollectionsFromGraphQl(doc.RootElement);
            if (collections.Count == 0)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(normalized))
            {
                collections = collections
                    .Where(item => item.Name.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                                   item.Summary.Contains(normalized, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            return collections;
        }

        return [];
    }

    private static List<RemoteSearchItem> ParseNexusCollectionsFromGraphQl(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("collectionsV2", out var collections) ||
            !collections.TryGetProperty("nodes", out var nodes) ||
            nodes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<RemoteSearchItem>();
        foreach (var row in nodes.EnumerateArray())
        {
            var id = TryGetLong(row, "id");
            if (id <= 0)
            {
                continue;
            }

            var name = TryGetString(row, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var iconUrl = TryGetNestedString(row, "tileImage", "url");
            var slug = TryGetString(row, "slug");
            results.Add(new RemoteSearchItem
            {
                ResourceId = id,
                Name = name,
                Summary = TryGetString(row, "summary"),
                Stat = BuildDownloadsMetric("downloads", TryGetLong(row, "totalDownloads")),
                TimeTag = BuildTimeTag(TryGetString(row, "updatedAt")),
                IconUrl = iconUrl,
                FullIconUrl = iconUrl,
                Source = CatalogSource.NexusMods,
                SourceTagHint = "NexusPack",
                CollectionSlug = slug ?? string.Empty
            });
        }

        return results;
    }

    private async Task<JsonDocument?> ExecuteNexusGraphQlAsync(object requestBody, AppUserSettings settings)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.nexusmods.com/v2/graphql")
            {
                Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
            };
            ApplyNexusHeaders(request, settings);

            using var response = await GetHttpClient(settings).SendAsync(request);
            if (IsUnauthorizedStatusCode(response.StatusCode))
            {
                var body = await SafeReadBodySnippetAsync(response, 260);
                HandleNexusAuthExpired("Nexus/graphql-http", settings, response.StatusCode, body);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await SafeReadBodySnippetAsync(response, 260);
                LogDebug($"Nexus/graphql failed status={(int)response.StatusCode} {response.ReasonPhrase}, body={body}");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            var doc = await JsonDocument.ParseAsync(stream);
            if (TryExtractNexusGraphQlAuthError(doc.RootElement, out var authError))
            {
                HandleNexusAuthExpired("Nexus/graphql-body", settings, null, authError);
                doc.Dispose();
                return null;
            }

            return doc;
        }
        catch (Exception ex) when (IsNexusUnauthorizedMessage(ex.Message))
        {
            HandleNexusAuthExpired("Nexus/graphql-ex", settings, null, ex.Message);
            return null;
        }
    }

    private static bool HasNexusCredential(AppUserSettings settings)
    {
        return !string.IsNullOrWhiteSpace(settings.NexusOAuthAccessToken) ||
               !string.IsNullOrWhiteSpace(settings.NexusApiKey);
    }

    private static void ApplyNexusHeaders(HttpRequestMessage request, AppUserSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.NexusOAuthAccessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.NexusOAuthAccessToken);
        }
        else if (!string.IsNullOrWhiteSpace(settings.NexusApiKey))
        {
            request.Headers.TryAddWithoutValidation("apikey", settings.NexusApiKey);
        }

        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Application-Name", "Stardew Valley Launcher");
        request.Headers.TryAddWithoutValidation("Application-Version", "1.0.0");
        request.Headers.TryAddWithoutValidation("Protocol-Version", "1.0.0");
    }

    private void HandleNexusAuthExpired(string scene, AppUserSettings settings, HttpStatusCode? statusCode, string? detail)
    {
        var hadOAuthToken = !string.IsNullOrWhiteSpace(settings.NexusOAuthAccessToken) ||
                            !string.IsNullOrWhiteSpace(settings.NexusOAuthRefreshToken) ||
                            !string.IsNullOrWhiteSpace(settings.NexusOAuthIdToken);

        if (hadOAuthToken)
        {
            settings.NexusOAuthAccessToken = string.Empty;
            settings.NexusOAuthRefreshToken = string.Empty;
            settings.NexusOAuthIdToken = string.Empty;
            _settingsStore.Save(settings);
        }

        var compactDetail = CompactForLog(detail, 220);
        LogDebug($"Nexus/auth-expired scene={scene}, status={(statusCode.HasValue ? ((int)statusCode.Value).ToString() : "-")}, clearedOAuth={hadOAuthToken}, detail={compactDetail}");

        var now = DateTimeOffset.UtcNow;
        if (now - _lastNexusAuthNotifiedAt < NexusAuthNotifyCooldown)
        {
            return;
        }

        _lastNexusAuthNotifiedAt = now;
        NexusAuthExpired?.Invoke("Nexus OAuth 登录已失效，请在设置页重新登录。");
    }

    private static bool IsUnauthorizedStatusCode(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
    }

    private static bool IsNexusUnauthorizedMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("401", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("403", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("forbidden", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("token expired", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("token 已过期", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("invalid token", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("unauthenticated", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryExtractNexusGraphQlAuthError(JsonElement root, out string reason)
    {
        reason = string.Empty;

        if (!root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var error in errors.EnumerateArray())
        {
            var message = TryGetString(error, "message");
            if (IsNexusUnauthorizedMessage(message))
            {
                reason = message;
                return true;
            }

            if (error.TryGetProperty("extensions", out var extensions) && extensions.ValueKind == JsonValueKind.Object)
            {
                var code = TryGetString(extensions, "code");
                var classification = TryGetString(extensions, "classification");
                if (IsNexusUnauthorizedMessage(code) || IsNexusUnauthorizedMessage(classification))
                {
                    reason = FirstNonEmpty(message, code, classification);
                    return true;
                }
            }
        }

        return false;
    }

    private static async Task<string> SafeReadBodySnippetAsync(HttpResponseMessage response, int maxLength)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync();
            return CompactForLog(body, maxLength);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string CompactForLog(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var compact = text
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        return compact.Length <= maxLength
            ? compact
            : compact[..maxLength] + "...";
    }

    private static string NormalizeGameVersionForFilter(string rawVersion)
    {
        return NormalizeStardewGameVersionToken(rawVersion, keepPatch: false);
    }

    private static string NormalizeStardewGameVersionToken(string rawVersion, bool keepPatch)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return string.Empty;
        }

        var match = Regex.Match(rawVersion, @"(?i)(?:S[DV]V?\s*)?(?<major>\d+)\.(?<minor>\d+)(?:\.(?<patch>\d+))?\+?\b", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return string.Empty;
        }

        if (!int.TryParse(match.Groups["major"].Value, out var major) ||
            !int.TryParse(match.Groups["minor"].Value, out var minor) ||
            major != 1)
        {
            return string.Empty;
        }

        if (!keepPatch)
        {
            return $"{major}.{minor}";
        }

        if (match.Groups["patch"].Success && int.TryParse(match.Groups["patch"].Value, out var patch))
        {
            return $"{major}.{minor}.{patch}";
        }

        return $"{major}.{minor}";
    }

    private static Version ParseVersionSortValue(string version)
    {
        var normalized = NormalizeStardewGameVersionToken(version, keepPatch: true);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new Version(0, 0);
        }

        return Version.TryParse(normalized, out var parsed)
            ? parsed
            : new Version(0, 0);
    }

    private static List<string> ExtractCurseforgeFileGameVersions(JsonElement file)
    {
        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (file.TryGetProperty("gameVersions", out var gameVersions) && gameVersions.ValueKind == JsonValueKind.Array)
        {
            foreach (var gameVersionElement in gameVersions.EnumerateArray())
            {
                if (gameVersionElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var rawVersion = gameVersionElement.GetString() ?? string.Empty;
                foreach (var token in ParsePossibleGameVersions(rawVersion))
                {
                    var normalized = NormalizeStardewGameVersionToken(token, keepPatch: true);
                    if (!string.IsNullOrWhiteSpace(normalized))
                    {
                        versions.Add(normalized);
                    }
                }
            }
        }

        if (versions.Count == 0)
        {
            var fallbackText = FirstNonEmpty(TryGetString(file, "displayName"), TryGetString(file, "fileName"));
            foreach (var token in ParsePossibleGameVersions(fallbackText))
            {
                var normalized = NormalizeStardewGameVersionToken(token, keepPatch: true);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    versions.Add(normalized);
                }
            }
        }

        return versions
            .OrderByDescending(ParseVersionSortValue)
            .ThenByDescending(version => version, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> ParseCurseforgeRelationDependencies(JsonElement modData)
    {
        var dependencies = new List<string>();
        if (!modData.TryGetProperty("relations", out var relations) || relations.ValueKind != JsonValueKind.Array)
        {
            return dependencies;
        }

        foreach (var relation in relations.EnumerateArray())
        {
            var modId = TryGetLong(relation, "modId");
            var relationType = (int)TryGetLong(relation, "relationType");
            if (modId <= 0)
            {
                continue;
            }

            var text = BuildCurseforgeDependencyText(modId, relationType, null);
            if (!string.IsNullOrWhiteSpace(text))
            {
                dependencies.Add(text);
            }
        }

        return dependencies;
    }

    private static List<string> ParseCurseforgeFileDependencies(JsonElement file)
    {
        var dependencies = new List<string>();
        if (!file.TryGetProperty("dependencies", out var dependencyArray) || dependencyArray.ValueKind != JsonValueKind.Array)
        {
            return dependencies;
        }

        foreach (var dependency in dependencyArray.EnumerateArray())
        {
            if (!TryParseCurseforgeDependency(dependency, out var modId, out var relationType, out var required))
            {
                continue;
            }

            var text = BuildCurseforgeDependencyText(modId, relationType, required);
            if (!string.IsNullOrWhiteSpace(text))
            {
                dependencies.Add(text);
            }
        }

        return dependencies;
    }

    private static bool TryParseCurseforgeDependency(JsonElement dependency, out long modId, out int relationType, out bool? required)
    {
        modId = TryGetLong(dependency, "modId");
        if (modId <= 0)
        {
            modId = TryGetLong(dependency, "addonId");
        }

        relationType = (int)TryGetLong(dependency, "relationType");
        required = null;
        if (dependency.TryGetProperty("required", out var requiredElement) &&
            requiredElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            required = requiredElement.GetBoolean();
        }

        return modId > 0;
    }

    private static string BuildCurseforgeDependencyText(long modId, int relationType, bool? required)
    {
        if (modId <= 0)
        {
            return string.Empty;
        }

        var relationName = ResolveCurseforgeRelationTypeName(relationType);
        var isRequired = required ?? relationType is 1 or 3;

        return relationType switch
        {
            5 => $"冲突模组: ModId {modId} ({relationName})",
            6 => $"功能重叠: ModId {modId} ({relationName})",
            2 => $"相关依赖: ModId {modId} ({relationName})",
            _ => isRequired
                ? $"前置依赖: ModId {modId} ({relationName})"
                : $"相关依赖: ModId {modId} ({relationName})"
        };
    }

    private static string ResolveCurseforgeRelationTypeName(int relationType)
    {
        return relationType switch
        {
            1 => "EmbeddedLibrary",
            2 => "OptionalDependency",
            3 => "RequiredDependency",
            4 => "Tool",
            5 => "Incompatible",
            6 => "Include",
            _ => $"Unknown({relationType})"
        };
    }

    private static string ResolveCurseforgeReleaseTypeName(int releaseType)
    {
        return releaseType switch
        {
            1 => "Release",
            2 => "Beta",
            3 => "Alpha",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// 清理 SMAPI 文件 displayName 中的重复前缀（例如 "SMAPI SMAPI 4.5.1" → "SMAPI 4.5.1"）。
    /// 与 SVL.Core.Download.CurseforgeHelper.ParseSmapiDisplayName 行为保持一致。
    /// </summary>
    private static string ParseSmapiDisplayName(string displayName, string fileName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return "SMAPI";
        }

        var result = displayName.Replace(".zip", "").Replace(".ZIP", "").Trim();

        var versionMatch = Regex.Match(result, @"(\d+\.\d+(\.\d+)?)");
        string? version = null;

        if (versionMatch.Success)
        {
            version = versionMatch.Groups[1].Value;
        }
        else if (!string.IsNullOrWhiteSpace(fileName))
        {
            var fileVersionMatch = Regex.Match(fileName, @"(\d+\.\d+(\.\d+)?)");
            if (fileVersionMatch.Success)
            {
                version = fileVersionMatch.Groups[1].Value;
            }
        }

        if (!string.IsNullOrEmpty(version))
        {
            return $"SMAPI {version}";
        }

        if (result.StartsWith("SMAPI ", StringComparison.OrdinalIgnoreCase))
        {
            var afterPrefix = result.Substring(6).Trim();
            if (afterPrefix.StartsWith("SMAPI", StringComparison.OrdinalIgnoreCase))
            {
                return "SMAPI";
            }
        }

        return result.Trim();
    }

    private static string BuildCurseforgeDetailsSummary(string baseSummary, int fileCount, int gameVersionCount, int dependencyCount)
    {
        var metric = $"文件 {Math.Max(fileCount, 0)} 个，兼容版本 {Math.Max(gameVersionCount, 0)} 个，依赖 {Math.Max(dependencyCount, 0)} 项";
        if (string.IsNullOrWhiteSpace(baseSummary))
        {
            return $"已加载 Curseforge 资源详情（{metric}）";
        }

        return $"{baseSummary}\n{metric}";
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string BuildDownloadsMetric(string label, long downloads)
    {
        return downloads > 0 ? $"{label} {downloads:N0}" : string.Empty;
    }

    private static string BuildTimeTag(string? rawTime)
    {
        if (string.IsNullOrWhiteSpace(rawTime))
        {
            return string.Empty;
        }

        return TryFormatTimeTag(rawTime);
    }

    /// <summary>
    /// 解析 Nexus 文件的上传时间用于排序（降序，最新在前）。
    /// </summary>
    private static DateTime TryGetNexusFileSortTime(JsonElement file)
    {
        var timeStr = FirstNonEmpty(
            TryGetString(file, "uploaded_time"),
            TryGetString(file, "date"),
            TryGetString(file, "file_date"));

        if (string.IsNullOrWhiteSpace(timeStr))
        {
            return DateTime.MinValue;
        }

        return DateTime.TryParse(timeStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : DateTime.MinValue;
    }

    private static string ResolveNexusChannelName(int categoryId, string? categoryName, string? fileName = null)
    {
        // 优先检查文件名中的显式版本类型关键字（beta/alpha/rc）。
        // Nexus 的 category_id 有时不能准确反映文件的实际版本类型
        // （例如文件名含 "beta" 但 category_id=3 被映射为 Alpha）。
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            if (Regex.IsMatch(fileName, @"\bbeta\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return "Beta";
            }

            if (Regex.IsMatch(fileName, @"\balpha\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return "Alpha";
            }

            if (Regex.IsMatch(fileName, @"\brc\d", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return "Beta";
            }
        }

        // Nexus category_id: 1 = Main/Release, 2 = Optional/Beta, 3 = Old/Alpha (legacy convention).
        var byId = categoryId switch
        {
            1 => "Release",
            2 => "Beta",
            3 => "Alpha",
            _ => string.Empty
        };

        if (!string.IsNullOrWhiteSpace(byId))
        {
            return byId;
        }

        if (!string.IsNullOrWhiteSpace(categoryName))
        {
            var lower = categoryName.Trim();
            if (lower.Contains("beta", StringComparison.OrdinalIgnoreCase) || lower.Contains("测试", StringComparison.OrdinalIgnoreCase))
            {
                return "Beta";
            }

            if (lower.Contains("alpha", StringComparison.OrdinalIgnoreCase) || lower.Contains("旧版", StringComparison.OrdinalIgnoreCase) || lower.Contains("old", StringComparison.OrdinalIgnoreCase))
            {
                return "Alpha";
            }

            if (lower.Contains("main", StringComparison.OrdinalIgnoreCase) || lower.Contains("release", StringComparison.OrdinalIgnoreCase) || lower.Contains("主", StringComparison.OrdinalIgnoreCase))
            {
                return "Release";
            }
        }

        // 有文件名但无法判定频道时，默认为 Release。
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            return "Release";
        }

        return string.Empty;
    }

    /// <summary>
    /// 从 Nexus 文件信息中提取游戏版本。与 Curseforge 的 ExtractCurseforgeFileGameVersions 对齐，
    /// 支持多源 fallback：description → file_name → name(displayName)。
    /// NexusMods API 不提供文件级 gameVersion 字段，版本信息散落在文本中需正则提取。
    /// </summary>
    private static string ParseNexusFileGameVersion(JsonElement file)
    {
        var description = TryGetString(file, "description");
        var version = ExtractGameVersionFromContextText(description);
        if (!string.IsNullOrWhiteSpace(version))
        {
            return version;
        }

        // fallback 到 file_name（很多 Nexus mod 文件名含版本，如 "SMAPI 4.1.1 for SDV 1.6.zip"）
        var fileName = TryGetString(file, "file_name");
        version = ExtractGameVersionFromContextText(fileName);
        if (!string.IsNullOrWhiteSpace(version))
        {
            return version;
        }

        // fallback 到 name(displayName)
        var displayName = TryGetString(file, "name");
        version = ExtractGameVersionFromContextText(displayName);
        if (!string.IsNullOrWhiteSpace(version))
        {
            return version;
        }

        // 最终 fallback：用 ParsePossibleGameVersions 从所有文本中提取 1.x 格式版本号
        var combinedText = FirstNonEmpty(description, fileName, displayName);
        if (!string.IsNullOrWhiteSpace(combinedText))
        {
            var tokens = ParsePossibleGameVersions(combinedText);
            if (tokens.Count > 0)
            {
                return tokens.First();
            }
        }

        return string.Empty;
    }

    /// <summary>从文本中提取带上下文关键词的游戏版本（优先精确匹配，避免误提取 mod 版本号）。</summary>
    private static string ExtractGameVersionFromContextText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        // "for Stardew Valley 1.6" / "for Stardew 1.6.5" / "Stardew 1.6"
        var stardewMatch = Regex.Match(
            text,
            @"(?i)\bfor\s+stardew(?:\s+valley)?\s+(\d+(?:\.\d+){1,3})\b");
        if (stardewMatch.Success && TryNormalizeStardewVersionToken(stardewMatch.Groups[1].Value, out var stardewVersion))
        {
            return stardewVersion;
        }

        var genericMatch = Regex.Match(
            text,
            @"(?i)\bstardew(?:\s+valley)?\s+(\d+(?:\.\d+){1,3})\b");
        if (genericMatch.Success && TryNormalizeStardewVersionToken(genericMatch.Groups[1].Value, out var genericVersion))
        {
            return genericVersion;
        }

        // "SV1.6" / "SDV1.6" / "SV 1.6"
        var svMatch = Regex.Match(
            text,
            @"(?i)\bS[DV]V?\s*(\d+(?:\.\d+){1,3})\b");
        if (svMatch.Success && TryNormalizeStardewVersionToken(svMatch.Groups[1].Value, out var svVersion))
        {
            return svVersion;
        }

        // "兼容 1.6" / "for 1.6" / "SDV 1.6"
        var compatMatch = Regex.Match(
            text,
            @"(?i)(?:兼容|for|sdv|sv)\s*(\d+(?:\.\d+){1,3})\b");
        if (compatMatch.Success && TryNormalizeStardewVersionToken(compatMatch.Groups[1].Value, out var compatVersion))
        {
            return compatVersion;
        }

        return string.Empty;
    }

    private static bool TryNormalizeStardewVersionToken(string token, out string normalized)
    {
        normalized = NormalizeStardewGameVersionToken(token, keepPatch: true);
        return !string.IsNullOrWhiteSpace(normalized);
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes <= 0)
        {
            return string.Empty;
        }

        const long kb = 1024;
        const long mb = kb * 1024;
        const long gb = mb * 1024;

        if (bytes >= gb)
        {
            return $"{(double)bytes / gb:F2} GB";
        }

        if (bytes >= mb)
        {
            return $"{(double)bytes / mb:F2} MB";
        }

        if (bytes >= kb)
        {
            return $"{(double)bytes / kb:F1} KB";
        }

        return $"{bytes} B";
    }

    private static string BuildNexusOptionMetadata(string channel, string gameVersion, string sizeText, string downloadsText, string dateText, string version = "")
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(channel))
        {
            parts.Add($"channel={channel}");
        }

        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            parts.Add($"gamever={gameVersion}");
        }

        if (!string.IsNullOrWhiteSpace(version))
        {
            parts.Add($"version={version}");
        }

        if (!string.IsNullOrWhiteSpace(sizeText))
        {
            parts.Add($"size={sizeText}");
        }

        if (!string.IsNullOrWhiteSpace(downloadsText))
        {
            parts.Add($"downloads={downloadsText}");
        }

        if (!string.IsNullOrWhiteSpace(dateText))
        {
            parts.Add($"date={dateText}");
        }

        return parts.Count == 0 ? string.Empty : string.Join(';', parts);
    }

    private async Task<CatalogResourceDetails> GetNexusCollectionDetailsAsync(CatalogResourceIdentity identity, AppUserSettings settings)
    {
        var collectionName = identity.Name;

        // 没有 slug 无法拉取 revisions，回退到占位提示。
        if (string.IsNullOrWhiteSpace(identity.CollectionSlug))
        {
            return new CatalogResourceDetails
            {
                Name = collectionName,
                Source = "NexusMods",
                Summary = "未携带 Collection Slug，可通过 NXM Collection 链接导入下载。",
                VersionOptions = [$"Collection ID: {identity.ResourceId}"],
                Dependencies = [],
                DownloadOptions = ["通过 NXM Collection 链接导入下载"]
            };
        }

        if (!HasNexusCredential(settings))
        {
            HandleNexusAuthExpired("Nexus/collection-details-no-credential", settings, HttpStatusCode.Unauthorized, "No authentication method");
            return new CatalogResourceDetails
            {
                Name = collectionName,
                Source = "NexusMods",
                Summary = "Nexus OAuth 登录已失效，请在设置页重新登录后重试。",
                DownloadOptions = ["请前往设置页重新登录 Nexus 账户"]
            };
        }

        var slug = identity.CollectionSlug;
        LogDebug($"Nexus/collection-details start slug={slug}, id={identity.ResourceId}");

        // NexusMods Collection 没有可用的 REST 端点（/v1/games/{domain}/collections/{slug} 返回 404）。
        // 必须通过 GraphQL 获取 Collection 信息与 revisions 列表，参考旧架构 NexusModsClient.GetAllCollectionRevisionsAsync。
        var graphQlQuery = @"
            query GetCollectionDetails($slug: String!, $domainName: String!) {
              collection(slug: $slug, domainName: $domainName) {
                id
                name
                slug
                summary
                tileImage { url }
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

        var requestBody = new
        {
            query = graphQlQuery,
            variables = new
            {
                slug,
                domainName = NexusGameDomain
            }
        };

        // 一次 GraphQL 请求同时获取 Collection 基本信息（名称/简介/图标）与 revisions 列表。
        var collectionSummary = string.Empty;
        var collectionIconUrl = string.Empty;
        var collectionFullIconUrl = string.Empty;
        var versions = new List<string>();
        var downloadOptions = new List<string>();
        var revisionsLoaded = false;
        var revisionsError = string.Empty;

        try
        {
            using var doc = await ExecuteNexusGraphQlAsync(requestBody, settings);
            if (doc == null)
            {
                // 认证失败或请求失败已由 ExecuteNexusGraphQlAsync 处理。
                revisionsError = "无法获取 Collection 信息，可改用 NXM Collection 链接导入下载。";
            }
            else if (doc.RootElement.TryGetProperty("data", out var dataProp) &&
                     dataProp.TryGetProperty("collection", out var collection) &&
                     collection.ValueKind == JsonValueKind.Object)
            {
                collectionName = FirstNonEmpty(
                    TryGetStringMulti(collection, "name", "title"),
                    collectionName);
                collectionSummary = TryGetStringMulti(collection, "summary", "description");
                collectionIconUrl = TryGetNestedString(collection, "tileImage", "url");
                collectionFullIconUrl = collectionIconUrl;

                if (collection.TryGetProperty("revisions", out var revs) && revs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var rev in revs.EnumerateArray().Take(15))
                    {
                        var revisionNumber = TryGetLongMulti(rev, "revisionNumber", "revision_number");
                        var revName = FirstNonEmpty(
                            TryGetStringMulti(rev, "name", "revision_name", "revisionName"),
                            collectionName);
                        var isLatest = TryGetBoolMulti(rev, "latest", "is_latest", "isLatest");
                        var fileSize = TryGetLongMulti(rev, "totalSize", "fileSize", "total_size", "file_size");
                        var totalDownloads = TryGetLongMulti(rev, "totalDownloads", "total_downloads");
                        var modCount = TryGetLongMulti(rev, "modCount", "mod_count");
                        var updatedAt = FirstNonEmpty(
                            TryGetStringMulti(rev, "updatedAt", "updated_at"),
                            TryGetStringMulti(rev, "createdAt", "created_at"));

                        var sizeText = FormatFileSize(fileSize);
                        var downloadsText = totalDownloads > 0
                            ? totalDownloads.ToString("N0", CultureInfo.InvariantCulture)
                            : string.Empty;
                        var dateText = TryFormatTimeTag(updatedAt);
                        var latestTag = isLatest ? "最新" : string.Empty;

                        var versionLabel = revisionNumber > 0
                            ? $"Revision {revisionNumber}{(string.IsNullOrWhiteSpace(latestTag) ? string.Empty : $" ({latestTag})")}"
                            : "Revision";
                        versions.Add(versionLabel);

                        var optionTitle = revisionNumber > 0
                            ? $"Revision {revisionNumber}: {revName}"
                            : $"{revName}";
                        var meta = BuildNexusCollectionOptionMetadata(latestTag, sizeText, downloadsText, dateText, modCount);
                        var option = string.IsNullOrWhiteSpace(meta)
                            ? optionTitle
                            : $"{optionTitle} ~~ {meta}";
                        downloadOptions.Add(option);
                    }

                    revisionsLoaded = true;
                }
                else
                {
                    revisionsError = "未获取到 Collection 版本，可改用 NXM Collection 链接导入下载。";
                }
            }
            else
            {
                LogDebug($"Nexus/collection-details: collection not found for slug={slug}");
                revisionsError = "无法获取 Collection 信息，可改用 NXM Collection 链接导入下载。";
            }
        }
        catch (Exception ex)
        {
            LogDebug($"Nexus/collection-details exception: {ex.Message}");
            revisionsError = "无法获取 Collection 版本列表，可改用 NXM Collection 链接导入下载。";
        }

        LogDebug($"Nexus/collection-details done slug={slug}, revisions={downloadOptions.Count}, loaded={revisionsLoaded}");

        // 组装摘要：优先用 Collection 基本信息，revisions 失败时附加提示而非整体覆盖。
        // 当 Collection 信息与 revisions 均不可得时返回空 Summary，使 ViewModel 回退到搜索结果的上下文摘要。
        string summary;
        if (revisionsLoaded)
        {
            var revNote = downloadOptions.Count == 0
                ? "未获取到 Collection 版本，可改用 NXM Collection 链接导入下载。"
                : $"已获取 {downloadOptions.Count} 个 Collection 版本（Revision）";
            summary = string.IsNullOrWhiteSpace(collectionSummary)
                ? revNote
                : $"{collectionSummary}\n{revNote}";
        }
        else
        {
            // revisions 加载失败：如果拿到了 Collection 信息就附加上错误提示；否则返回空 Summary，
            // 使 ViewModel 回退到搜索结果的上下文摘要，避免用错误文本覆盖已有信息。
            summary = string.IsNullOrWhiteSpace(collectionSummary)
                ? string.Empty
                : $"{collectionSummary}\n{revisionsError}";
        }

        if (downloadOptions.Count == 0)
        {
            downloadOptions.Add("通过 NXM Collection 链接导入下载");
        }

        // 从 Collection summary 提取游戏版本信息（Collection 是多 mod 合集，GraphQL 不直接返回游戏版本字段）
        var collectionGameVersions = ParsePossibleGameVersions(collectionSummary);
        var collectionVersionHeader = collectionGameVersions
            .Select(version => $"兼容游戏版本：{version}")
            .ToList();
        var finalCollectionVersions = collectionVersionHeader
            .Concat(versions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new CatalogResourceDetails
        {
            Name = collectionName,
            Source = "NexusMods",
            Summary = summary,
            VersionOptions = finalCollectionVersions,
            Dependencies = [],
            DownloadOptions = downloadOptions,
            IconUrl = collectionIconUrl,
            FullIconUrl = collectionFullIconUrl
        };
    }

    /// <summary>枚举 Collection revisions 响应，兼容裸数组、{revisions:[...]}、{data:[...]}、{results:[...]} 四种形态。</summary>
    private static IEnumerable<JsonElement> EnumerateCollectionRevisions(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                yield return item;
            }

            yield break;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        if (root.TryGetProperty("revisions", out var revs) && revs.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in revs.EnumerateArray())
            {
                yield return item;
            }

            yield break;
        }

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                yield return item;
            }

            yield break;
        }

        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in results.EnumerateArray())
            {
                yield return item;
            }
        }
    }

    private static string BuildNexusCollectionOptionMetadata(string latestTag, string sizeText, string downloadsText, string dateText, long modCount)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(latestTag))
        {
            parts.Add($"channel={latestTag}");
        }

        if (!string.IsNullOrWhiteSpace(sizeText))
        {
            parts.Add($"size={sizeText}");
        }

        if (modCount > 0)
        {
            parts.Add($"mods={modCount.ToString(CultureInfo.InvariantCulture)}");
        }

        if (!string.IsNullOrWhiteSpace(downloadsText))
        {
            parts.Add($"downloads={downloadsText}");
        }

        if (!string.IsNullOrWhiteSpace(dateText))
        {
            parts.Add($"date={dateText}");
        }

        return parts.Count == 0 ? string.Empty : string.Join(';', parts);
    }

    private static string TryGetStringMulti(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var value = TryGetString(element, name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static long TryGetLongMulti(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var value = TryGetLong(element, name);
            if (value > 0)
            {
                return value;
            }
        }

        return 0;
    }

    private static bool TryGetBoolMulti(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.True)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<CatalogResourceDetails> GetNexusResourceDetailsAsync(CatalogResourceIdentity identity, AppUserSettings settings)
    {
        if (identity.IsModpack)
        {
            return await GetNexusCollectionDetailsAsync(identity, settings);
        }
        if (!HasNexusCredential(settings))
        {
            HandleNexusAuthExpired("Nexus/details-no-credential", settings, HttpStatusCode.Unauthorized, "No authentication method");
            return new CatalogResourceDetails
            {
                Name = identity.Name,
                Source = "NexusMods",
                Summary = "Nexus OAuth 登录已失效，请在设置页重新登录后重试。",
                DownloadOptions = ["请前往设置页重新登录 Nexus 账户"]
            };
        }

        var modName = identity.Name;
        var modSummary = string.Empty;
        var iconUrl = string.Empty;
        var fullIconUrl = string.Empty;

        using (var modRequest = new HttpRequestMessage(
                   HttpMethod.Get,
                   $"https://api.nexusmods.com/v1/games/{NexusGameDomain}/mods/{identity.ResourceId}.json"))
        {
            ApplyNexusHeaders(modRequest, settings);

            using var modResponse = await GetHttpClient(settings).SendAsync(modRequest);
            if (IsUnauthorizedStatusCode(modResponse.StatusCode))
            {
                var body = await SafeReadBodySnippetAsync(modResponse, 260);
                HandleNexusAuthExpired("Nexus/details-mod", settings, modResponse.StatusCode, body);
            }
            else if (modResponse.IsSuccessStatusCode)
            {
                await using var modStream = await modResponse.Content.ReadAsStreamAsync();
                using var modDoc = await JsonDocument.ParseAsync(modStream);
                var modRoot = modDoc.RootElement;

                modName = FirstNonEmpty(TryGetString(modRoot, "name"), modName);
                modSummary = FirstNonEmpty(TryGetString(modRoot, "summary"), TryGetString(modRoot, "description"));

                iconUrl = FirstNonEmpty(
                    TryGetString(modRoot, "pictureUrl"),
                    TryGetString(modRoot, "picture_url"),
                    TryGetNestedString(modRoot, "picture", "thumbnailUrl"),
                    TryGetNestedString(modRoot, "picture", "url"));

                fullIconUrl = FirstNonEmpty(
                    TryGetString(modRoot, "pictureFullUrl"),
                    TryGetString(modRoot, "picture_full_url"),
                    TryGetNestedString(modRoot, "picture", "url"),
                    iconUrl);
            }
            else
            {
                var modBody = await SafeReadBodySnippetAsync(modResponse, 260);
                LogDebug($"Nexus/details mod failed status={(int)modResponse.StatusCode} {modResponse.ReasonPhrase}, body={modBody}");
            }
        }

        LogDebug($"Nexus/details start id={identity.ResourceId}, hasCredential={HasNexusCredential(settings)}");

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.nexusmods.com/v1/games/{NexusGameDomain}/mods/{identity.ResourceId}/files.json");
        ApplyNexusHeaders(request, settings);

        using var response = await GetHttpClient(settings).SendAsync(request);
        if (IsUnauthorizedStatusCode(response.StatusCode))
        {
            var body = await SafeReadBodySnippetAsync(response, 260);
            HandleNexusAuthExpired("Nexus/details-files", settings, response.StatusCode, body);
            return new CatalogResourceDetails
            {
                Name = modName,
                Source = "NexusMods",
                Summary = "Nexus 登录已过期，请在设置页重新登录后重试。",
                DownloadOptions = ["请前往设置页重新登录 Nexus 账户"],
                IconUrl = iconUrl,
                FullIconUrl = fullIconUrl
            };
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadBodySnippetAsync(response, 260);
            LogDebug($"Nexus/details files failed status={(int)response.StatusCode} {response.ReasonPhrase}, body={body}");
            return new CatalogResourceDetails
            {
                Name = modName,
                Source = "NexusMods",
                Summary = "无法获取 Nexus 文件列表，请检查登录状态。",
                DownloadOptions = ["可先通过 NXM 链接导入下载"],
                IconUrl = iconUrl,
                FullIconUrl = fullIconUrl
            };
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        if (!doc.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            return new CatalogResourceDetails
            {
                Name = modName,
                Source = "NexusMods",
                Summary = "未获取到可用文件。",
                DownloadOptions = ["可先通过 NXM 链接导入下载"],
                IconUrl = iconUrl,
                FullIconUrl = fullIconUrl
            };
        }

        var versions = new List<string>();
        var downloadOptions = new List<string>();
        var supportedGameVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Nexus files.json 默认按时间升序返回（旧文件在前），取前 12 个会拿到旧版本。
        // 改为按 uploaded_time 降序排列后再取 12 个，确保展示最新文件。
        var nexusFileList = new List<JsonElement>();
        foreach (var f in files.EnumerateArray())
        {
            nexusFileList.Add(f);
        }

        foreach (var file in nexusFileList
            .OrderByDescending(TryGetNexusFileSortTime)
            .Take(12))
        {
            var version = TryGetString(file, "version");
            var fileName = TryGetString(file, "file_name");
            // Nexus files.json 同时包含 name（显示名）与 file_name（实际文件名）。
            // 标题优先使用显示名（与旧架构 file.Name ?? file.FileName 一致），回退到 file_name。
            var displayName = TryGetString(file, "name");
            var fileId = TryGetLong(file, "file_id");
            var categoryId = (int)TryGetLong(file, "category_id");
            var category = TryGetString(file, "category_name");

            var channel = ResolveNexusChannelName(categoryId, category, fileName);
            // 传入整个 file 对象，支持 description → file_name → name 多源 fallback
            var gameVersion = ParseNexusFileGameVersion(file);
            if (!string.IsNullOrWhiteSpace(gameVersion))
            {
                supportedGameVersions.Add(gameVersion);
            }

            var sizeText = FormatFileSize(TryGetLong(file, "size_in_bytes"));
            var downloadsCount = TryGetLong(file, "download_count");
            var downloadsText = downloadsCount > 0
                ? downloadsCount.ToString("N0", CultureInfo.InvariantCulture)
                : string.Empty;
            var dateText = TryFormatTimeTag(FirstNonEmpty(
                TryGetString(file, "uploaded_time"),
                TryGetString(file, "date"),
                TryGetString(file, "file_date")));

            var primaryGameVersion = !string.IsNullOrWhiteSpace(gameVersion) ? gameVersion : "未知";

            if (!string.IsNullOrWhiteSpace(version))
            {
                versions.Add(string.IsNullOrWhiteSpace(category) ? version : $"{version} ({category})");
            }

            if (fileId > 0)
            {
                // 标题只使用显示名（displayName），版本号通过 ~~ 元数据中的 VersionText 单独传递。
                var title = FirstNonEmpty(displayName, fileName, $"nexus-file-{fileId}");
                var option = $"File {fileId}: {title}";
                var meta = BuildNexusOptionMetadata(channel, primaryGameVersion, sizeText, downloadsText, dateText, version);
                if (!string.IsNullOrWhiteSpace(meta))
                {
                    option += $" ~~ {meta}";
                }
                downloadOptions.Add(option);
            }
        }

        // 构建版本过滤头部（与 Curseforge 详情一致），使 ViewModel 的 RebuildGameVersionFilters 能提取版本号
        var orderedGameVersions = supportedGameVersions
            .OrderByDescending(ParseVersionSortValue)
            .ThenByDescending(version => version, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var versionHeader = orderedGameVersions
            .Select(version => $"兼容游戏版本：{version}")
            .ToList();
        var finalVersionOptions = versionHeader
            .Concat(versions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        LogDebug($"Nexus/details done id={identity.ResourceId}, files={downloadOptions.Count}, versions={versions.Count}, gameVersions={supportedGameVersions.Count}");

        return new CatalogResourceDetails
        {
            Name = modName,
            Source = "NexusMods",
            Summary = string.IsNullOrWhiteSpace(modSummary)
                ? $"已获取 {downloadOptions.Count} 个可下载文件"
                : $"{modSummary}\n已获取 {downloadOptions.Count} 个可下载文件",
            VersionOptions = finalVersionOptions,
            Dependencies = [],
            DownloadOptions = downloadOptions,
            IconUrl = iconUrl,
            FullIconUrl = fullIconUrl
        };
    }

    private async Task<CatalogResourceDetails> GetCurseforgeResourceDetailsAsync(CatalogResourceIdentity identity)
    {
        if (identity.ResourceId <= 0)
        {
            return new CatalogResourceDetails
            {
                Name = identity.Name,
                Source = "Curseforge",
                Summary = "无效的 Curseforge 资源 ID。",
                DownloadOptions = ["可改用 URL/NXM 方式导入下载"]
            };
        }

        LogDebug($"Curseforge/details start id={identity.ResourceId}, name={identity.Name}");

        var modName = identity.Name;
        var modSummary = string.Empty;
        var iconUrl = string.Empty;
        var fullIconUrl = string.Empty;
        var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var modDetailUrl = $"https://api.curse.tools/v1/mods/{identity.ResourceId}";
        using (var modResponse = await GetWithRedirectAsync(modDetailUrl))
        {
            if (modResponse.IsSuccessStatusCode)
            {
                await using var modStream = await modResponse.Content.ReadAsStreamAsync();
                using var modDoc = await JsonDocument.ParseAsync(modStream);
                if (modDoc.RootElement.TryGetProperty("data", out var modData) && modData.ValueKind == JsonValueKind.Object)
                {
                    modName = FirstNonEmpty(TryGetString(modData, "name"), modName);
                    modSummary = FirstNonEmpty(TryGetString(modData, "summary"), TryGetString(modData, "description"));
                    fullIconUrl = FirstNonEmpty(TryGetNestedString(modData, "logo", "url"), fullIconUrl);
                    iconUrl = FirstNonEmpty(TryGetNestedString(modData, "logo", "thumbnailUrl"), iconUrl, fullIconUrl);
                    fullIconUrl = FirstNonEmpty(fullIconUrl, iconUrl);

                    var relationDependencies = ParseCurseforgeRelationDependencies(modData);
                    foreach (var relation in relationDependencies)
                    {
                        dependencies.Add(relation);
                    }

                    LogDebug($"Curseforge/details mod loaded id={identity.ResourceId}, relations={relationDependencies.Count}");
                }
            }
            else
            {
                var modBody = await SafeReadBodySnippetAsync(modResponse, 260);
                LogDebug($"Curseforge/details mod failed status={(int)modResponse.StatusCode} {modResponse.ReasonPhrase}, body={modBody}");
            }
        }

        var filesUrl = $"https://api.curse.tools/v1/mods/{identity.ResourceId}/files?index=0&pageSize=60";
        using var filesResponse = await GetWithRedirectAsync(filesUrl);
        if (!filesResponse.IsSuccessStatusCode)
        {
            var body = await SafeReadBodySnippetAsync(filesResponse, 260);
            LogDebug($"Curseforge/details files failed status={(int)filesResponse.StatusCode} {filesResponse.ReasonPhrase}, body={body}");

            return new CatalogResourceDetails
            {
                Name = modName,
                Source = "Curseforge",
                Summary = string.IsNullOrWhiteSpace(modSummary)
                    ? "无法获取 Curseforge 文件列表。"
                    : modSummary,
                Dependencies = dependencies.ToList(),
                DownloadOptions = ["可改用 URL/NXM 方式导入下载"],
                IconUrl = iconUrl,
                FullIconUrl = fullIconUrl
            };
        }

        await using var filesStream = await filesResponse.Content.ReadAsStreamAsync();
        using var filesDoc = await JsonDocument.ParseAsync(filesStream);
        if (!filesDoc.RootElement.TryGetProperty("data", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            return new CatalogResourceDetails
            {
                Name = modName,
                Source = "Curseforge",
                Summary = string.IsNullOrWhiteSpace(modSummary)
                    ? "未获取到可用文件。"
                    : modSummary,
                Dependencies = dependencies.ToList(),
                DownloadOptions = ["暂无可下载文件"],
                IconUrl = iconUrl,
                FullIconUrl = fullIconUrl
            };
        }

        var versionOptions = new List<string>();
        var downloadOptions = new List<string>();
        var supportedGameVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parsedFileDependencies = 0;

        // SMAPI 的 CurseForge 项目 ID 是 898372；其文件 displayName 常出现 "SMAPI SMAPI x.y.z" 重复前缀，需要清理。
        const long smapiCurseforgeProjectId = 898372;
        var isSmapiMod = identity.ResourceId == smapiCurseforgeProjectId
            || modName.Contains("SMAPI", StringComparison.OrdinalIgnoreCase);

        foreach (var file in files.EnumerateArray().Take(60))
        {
            var displayName = TryGetString(file, "displayName");
            var fileName = TryGetString(file, "fileName");
            var fileId = TryGetLong(file, "id");

            var releaseType = ResolveCurseforgeReleaseTypeName((int)TryGetLong(file, "releaseType"));
            var fileDate = TryFormatTimeTag(TryGetString(file, "fileDate"));

            // 读取文件大小（字节）与下载次数，供 ViewModel 通过 ~~ 元数据后缀解析为结构化字段。
            var fileLength = TryGetLong(file, "fileLength");
            var fileDownloadCount = TryGetLong(file, "downloadCount");
            var sizeText = FormatFileSize(fileLength);
            var downloadsText = fileDownloadCount > 0
                ? fileDownloadCount.ToString("N0", CultureInfo.InvariantCulture)
                : string.Empty;

            var gameVersions = ExtractCurseforgeFileGameVersions(file);
            foreach (var version in gameVersions)
            {
                supportedGameVersions.Add(version);
            }

            var primaryGameVersion = gameVersions.FirstOrDefault() ?? "未知";
            var displayLabel = FirstNonEmpty(displayName, fileName, fileId > 0 ? $"File {fileId}" : "未知文件");
            if (isSmapiMod)
            {
                displayLabel = ParseSmapiDisplayName(displayLabel, fileName);
            }

            // 复用与 Nexus 相同的 ~~ key=value; 元数据后缀，ViewModel 的 OptionMetadata 解析器可提取这些字段。
            // 将 primaryGameVersion 传入 meta，使 ViewModel 无需依赖标题中的 [version] 括号即可分组。
            // displayname 字段让 ViewModel 标题行优先显示 DisplayName（而非 VersionText）。
            var meta = BuildNexusOptionMetadata(releaseType, primaryGameVersion, sizeText, downloadsText, fileDate);
            if (!string.IsNullOrWhiteSpace(displayLabel))
            {
                var safeLabel = displayLabel.Replace(";", " ").Replace("=", " ").Trim();
                if (!string.IsNullOrWhiteSpace(safeLabel))
                {
                    meta = string.IsNullOrWhiteSpace(meta)
                        ? $"displayname={safeLabel}"
                        : $"{meta};displayname={safeLabel}";
                }
            }
            var versionLabel = $"[{primaryGameVersion}] {displayLabel} ({releaseType})";
            if (!string.IsNullOrWhiteSpace(meta))
            {
                versionLabel += $" ~~ {meta}";
            }
            else if (!string.IsNullOrWhiteSpace(fileDate))
            {
                versionLabel += $" · {fileDate}";
            }

            versionOptions.Add(versionLabel);

            if (fileId > 0)
            {
                // 标题只保留 displayLabel，gameVersion 和 releaseType 通过元数据与频道徽标单独展示。
                var optionPrefix = $"File {fileId}: {displayLabel}";
                var directUrl = TryGetString(file, "downloadUrl");
                var option = string.IsNullOrWhiteSpace(directUrl)
                    ? optionPrefix
                    : $"{optionPrefix} | {directUrl}";
                if (!string.IsNullOrWhiteSpace(meta))
                {
                    option += $" ~~ {meta}";
                }
                downloadOptions.Add(option);
            }
            else
            {
                var directUrl = TryGetString(file, "downloadUrl");
                if (!string.IsNullOrWhiteSpace(directUrl))
                {
                    var option = $"{displayLabel} | {directUrl}";
                    if (!string.IsNullOrWhiteSpace(meta))
                    {
                        option += $" ~~ {meta}";
                    }
                    downloadOptions.Add(option);
                }
            }

            var fileDependencies = ParseCurseforgeFileDependencies(file);
            parsedFileDependencies += fileDependencies.Count;
            foreach (var dependency in fileDependencies)
            {
                dependencies.Add(dependency);
            }
        }

        var orderedVersions = supportedGameVersions
            .OrderByDescending(ParseVersionSortValue)
            .ThenByDescending(version => version, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var versionHeader = orderedVersions
            .Select(version => $"兼容游戏版本：{version}")
            .ToList();
        var finalVersionOptions = versionHeader
            .Concat(versionOptions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var resolvedFileCount = downloadOptions.Count;
        var resolvedDependencyCount = dependencies.Count;

        if (downloadOptions.Count == 0)
        {
            downloadOptions.Add("暂无可下载文件");
        }

        var summary = BuildCurseforgeDetailsSummary(
            modSummary,
            resolvedFileCount,
            supportedGameVersions.Count,
            resolvedDependencyCount);

        LogDebug($"Curseforge/details done id={identity.ResourceId}, files={downloadOptions.Count}, versions={finalVersionOptions.Count}, deps={dependencies.Count}, fileDeps={parsedFileDependencies}");

        return new CatalogResourceDetails
        {
            Name = modName,
            Source = "Curseforge",
            Summary = summary,
            VersionOptions = finalVersionOptions,
            Dependencies = dependencies.ToList(),
            DownloadOptions = downloadOptions,
            IconUrl = iconUrl,
            FullIconUrl = fullIconUrl
        };
    }

    public async Task<string> ResolveCurseforgeFileDownloadUrlAsync(
        long modId,
        long fileId,
        string fallbackUrl = "",
        CancellationToken cancellationToken = default)
    {
        if (modId <= 0 || fileId <= 0)
        {
            return fallbackUrl ?? string.Empty;
        }

        var client = GetHttpClient();
        var candidateEndpoints = new[]
        {
            $"https://api.curse.tools/v1/mods/{modId}/files/{fileId}/download-url",
            $"https://api.curse.tools/v1/mods/{modId}/files/{fileId}",
            $"https://api.curse.tools/v1/mods/{modId}/files?index=0&pageSize=60"
        };

        foreach (var endpoint in candidateEndpoints)
        {
            try
            {
                using var response = await client.GetAsync(endpoint, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var resolved = await TryExtractCurseforgeDownloadUrlAsync(response.Content, fileId, cancellationToken);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    return resolved;
                }
            }
            catch
            {
                // Keep this resolver best-effort and continue fallback chain.
            }
        }

        return fallbackUrl ?? string.Empty;
    }

    private static async Task<string> TryExtractCurseforgeDownloadUrlAsync(
        HttpContent content,
        long fileId,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var direct = FindDownloadUrlInElement(doc.RootElement, fileId);
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        return string.Empty;
    }

    private static string FindDownloadUrlInElement(JsonElement element, long fileId)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                if (TryReadDownloadUrl(element, out var directUrl))
                {
                    return directUrl;
                }

                if (fileId > 0 &&
                    element.TryGetProperty("id", out var idElement) &&
                    idElement.ValueKind == JsonValueKind.Number &&
                    idElement.TryGetInt64(out var currentId) &&
                    currentId == fileId &&
                    TryReadDownloadUrl(element, out var matchedUrl))
                {
                    return matchedUrl;
                }

                foreach (var property in element.EnumerateObject())
                {
                    var nested = FindDownloadUrlInElement(property.Value, fileId);
                    if (!string.IsNullOrWhiteSpace(nested))
                    {
                        return nested;
                    }
                }

                return string.Empty;
            }
            case JsonValueKind.Array:
            {
                foreach (var item in element.EnumerateArray())
                {
                    var nested = FindDownloadUrlInElement(item, fileId);
                    if (!string.IsNullOrWhiteSpace(nested))
                    {
                        return nested;
                    }
                }

                return string.Empty;
            }
            default:
                return string.Empty;
        }
    }

    private static bool TryReadDownloadUrl(JsonElement element, out string downloadUrl)
    {
        var candidateKeys = new[] { "downloadUrl", "download_url", "fileUrl", "file_url", "url" };
        foreach (var key in candidateKeys)
        {
            if (!element.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var candidate = value.GetString();
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                downloadUrl = uri.ToString();
                return true;
            }
        }

        downloadUrl = string.Empty;
        return false;
    }

    private async Task<CatalogResourceDetails> GetGithubSmapiDetailsAsync(CatalogResourceIdentity identity)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/Pathoschild/SMAPI/releases?per_page=20");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await GetHttpClient().SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            return new CatalogResourceDetails
            {
                Name = string.IsNullOrWhiteSpace(identity.Name) ? "SMAPI - Stardew Modding API" : identity.Name,
                Source = "GitHub",
                Summary = "无法获取 GitHub 发布详情，可直接访问官方发布页。",
                IconUrl = "https://github.com/Pathoschild.png",
                FullIconUrl = "https://github.com/Pathoschild.png",
                VersionOptions = ["https://github.com/Pathoschild/SMAPI/releases"],
                Dependencies = BuildGithubSmapiDependencies(string.Empty),
                DownloadOptions = ["手动下载并导入 ZIP"]
            };
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return CatalogResourceDetails.Empty;
        }

        JsonElement? targetRelease = null;
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (identity.ResourceId > 0 && TryGetLong(release, "id") == identity.ResourceId)
            {
                targetRelease = release;
                break;
            }
        }

        if (targetRelease == null)
        {
            targetRelease = doc.RootElement.EnumerateArray().FirstOrDefault();
        }

        if (targetRelease == null || targetRelease.Value.ValueKind == JsonValueKind.Undefined)
        {
            return new CatalogResourceDetails
            {
                Name = string.IsNullOrWhiteSpace(identity.Name) ? "SMAPI - Stardew Modding API" : identity.Name,
                Source = "GitHub",
                Summary = "未获取到可用发布记录。",
                IconUrl = "https://github.com/Pathoschild.png",
                FullIconUrl = "https://github.com/Pathoschild.png",
                DownloadOptions = ["https://github.com/Pathoschild/SMAPI/releases"]
            };
        }

        var selectedRelease = targetRelease.Value;
        var tag = TryGetString(selectedRelease, "tag_name");
        var title = TryGetString(selectedRelease, "name");
        if (string.IsNullOrWhiteSpace(title))
        {
            title = string.IsNullOrWhiteSpace(tag)
                ? (string.IsNullOrWhiteSpace(identity.Name) ? "SMAPI - Stardew Modding API" : identity.Name)
                : $"SMAPI {tag}";
        }

        // 仓库作者头像作为详情页 Icon（GitHub API release.author.avatar_url）
        var repositoryAvatar = TryGetNestedString(selectedRelease, "author", "avatar_url");
        if (string.IsNullOrWhiteSpace(repositoryAvatar))
        {
            repositoryAvatar = "https://github.com/Pathoschild.png";
        }

        var releaseBody = TryGetString(selectedRelease, "body") ?? string.Empty;
        var publishedAt = TryGetString(selectedRelease, "published_at");
        var versionOptions = new List<string>();
        if (!string.IsNullOrWhiteSpace(tag))
        {
            versionOptions.Add($"Tag: {tag}");
        }

        if (DateTime.TryParse(publishedAt, out var published))
        {
            versionOptions.Add($"发布时间: {published:yyyy-MM-dd HH:mm}");
        }

        versionOptions.Add("官方发布页: https://github.com/Pathoschild/SMAPI/releases");

        var downloadOptions = new List<string>();
        if (selectedRelease.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray().Take(12))
            {
                var assetName = TryGetString(asset, "name");
                var assetUrl = TryGetString(asset, "browser_download_url");
                if (string.IsNullOrWhiteSpace(assetName) || string.IsNullOrWhiteSpace(assetUrl))
                {
                    continue;
                }

                downloadOptions.Add($"{assetName} | {assetUrl}");
            }
        }

        if (downloadOptions.Count == 0)
        {
            downloadOptions.Add("https://github.com/Pathoschild/SMAPI/releases");
        }

        // 详情摘要优先取仓库 README/About 内容（更稳定、更有信息量），
        // 失败时回退到发布说明片段。
        string summary;
        try
        {
            var readmeSummary = await FetchGithubSmapiReadmeAsync();
            summary = !string.IsNullOrWhiteSpace(readmeSummary)
                ? readmeSummary
                : BuildGithubSmapiSummary(releaseBody, tag);
        }
        catch
        {
            summary = BuildGithubSmapiSummary(releaseBody, tag);
        }

        return new CatalogResourceDetails
        {
            // 标题固定为 "SMAPI - Stardew Modding API"，而不是用发布版本号（如 "4.5.2"）当标题。
            Name = "SMAPI - Stardew Modding API",
            Source = "GitHub",
            Summary = summary,
            IconUrl = repositoryAvatar,
            FullIconUrl = repositoryAvatar,
            VersionOptions = versionOptions,
            // GitHub 源不展示前置/相关 Mod Card（避免与发布页/README 重复）。
            Dependencies = [],
            DownloadOptions = downloadOptions
        };
    }

    /// <summary>
    /// 拉取 SMAPI 仓库 README（About）内容并转成纯文本摘要，作为 GitHub 详情页的简介。
    /// 通过 GitHub readme API 获取（返回 base64），避免直接抓 HTML 页面被 include-fragment 占位符干扰。
    /// </summary>
    private async Task<string> FetchGithubSmapiReadmeAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/Pathoschild/SMAPI/readme");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("SVL-StardewValleyLauncher/1.0");

        using var response = await GetHttpClient().SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            return string.Empty;
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var encoding = TryGetString(doc.RootElement, "encoding");
        var content = TryGetString(doc.RootElement, "content");
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var markdown = string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase)
            ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(content))
            : content;

        return MarkdownToPlainSummary(markdown);
    }

    /// <summary>把 Markdown 转为纯文本摘要（去掉标题/链接/加粗/列表标记/徽章/HTML 标签），返回前几行有效内容。</summary>
    private static string MarkdownToPlainSummary(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var lines = new List<string>();
        foreach (var rawLine in markdown.Split(['\r', '\n']))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            // 跳过标题、分隔线、图片/徽章、HTML 注释
            if (line.StartsWith("#", StringComparison.Ordinal) ||
                line.StartsWith("![" , StringComparison.Ordinal) ||
                line.StartsWith("---", StringComparison.Ordinal) ||
                line.StartsWith("<!--", StringComparison.Ordinal))
            {
                continue;
            }

            // 去掉行内 markdown：链接 [text](url) -> text，加粗/斜体、行内代码、HTML 标签
            var cleaned = System.Text.RegularExpressions.Regex.Replace(line, @"\[([^\]]*)\]\([^)]*\)", "$1");
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"[*_`>#]+", " ");
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"<[^>]+>", " ");
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ").Trim();
            cleaned = cleaned.TrimStart('-', '*', ' ').Trim();

            if (cleaned.Length < 12)
            {
                continue;
            }

            lines.Add(cleaned);
            if (lines.Count >= 3)
            {
                break;
            }
        }

        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var summary = string.Join("；", lines);
        return summary.Length > 320 ? summary[..320] + "..." : summary;
    }

    /// <summary>从 SMAPI 发布说明中提取兼容的游戏版本号（如 1.6.15）。</summary>
    private static string? ExtractGithubCompatibleGameVersion(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        // 匹配 "Stardew Valley 1.6.15" / "Stardew Valley 1.5.6" 等
        var match = System.Text.RegularExpressions.Regex.Match(
            body,
            @"Stardew\s*Valley\s*(?:version\s*)?v?(\d+\.\d+(?:\.\d+)*)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>构建 SMAPI GitHub 详情摘要：截取发布说明前几行有效内容。</summary>
    private static string BuildGithubSmapiSummary(string body, string? tag)
    {
        var header = string.IsNullOrWhiteSpace(tag)
            ? "SMAPI 官方发布版本"
            : $"SMAPI {tag} 官方发布版本";

        if (string.IsNullOrWhiteSpace(body))
        {
            return header;
        }

        var lines = body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith("#", StringComparison.Ordinal) && line.Length > 3)
            .Take(3)
            .ToList();
        if (lines.Count == 0)
        {
            return header;
        }

        var summary = string.Join("；", lines.Select(line => line.TrimStart('-', '*', ' ')));
        return summary.Length > 300 ? summary[..300] + "..." : summary;
    }

    /// <summary>构建 SMAPI GitHub 详情的前置/相关 Mod 信息。</summary>
    private static List<string> BuildGithubSmapiDependencies(string body)
    {
        var dependencies = new List<string>();
        var gameVersion = ExtractGithubCompatibleGameVersion(body);
        dependencies.Add(string.IsNullOrWhiteSpace(gameVersion)
            ? "前置必需: Stardew Valley 游戏本体（1.5.6 或更高版本）"
            : $"前置必需: Stardew Valley 游戏本体（兼容 {gameVersion}）");

        dependencies.Add("相关推荐: .NET Desktop Runtime 6.0+（Windows 运行 SMAPI 所需）");
        dependencies.Add("相关推荐: 模组兼容性查询 https://smapi.io/mods");
        return dependencies;
    }

    private static string NormalizeFilterToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return string.Empty;
        }

        var normalized = token.Trim();
        return string.Equals(normalized, "全部", StringComparison.OrdinalIgnoreCase) ? string.Empty : normalized;
    }

    private async Task ApplyCommunityLocalizationAsync(IEnumerable<RemoteSearchItem> items, string platform, string entityType = "mod")
    {
        var tasks = items.Select(async item =>
        {
            // Collection 类型使用 slug 作为 ID，modpack/mod 类型使用数字 ResourceId
            var effectiveId = string.Equals(entityType, "collection", StringComparison.OrdinalIgnoreCase)
                ? item.CollectionSlug
                : item.ResourceId.ToString();

            if (string.IsNullOrWhiteSpace(effectiveId))
            {
                return;
            }

            try
            {
                var (nameZhCn, summaryZhCn, contributor) = await FetchCommunityLocalizationAsync(platform, effectiveId, entityType);
                if (string.IsNullOrWhiteSpace(nameZhCn) && string.IsNullOrWhiteSpace(summaryZhCn) && string.IsNullOrWhiteSpace(contributor))
                {
                    return;
                }

                item.LocalizedName = nameZhCn;
                item.LocalizedSummary = summaryZhCn;
                item.LocalizedContributor = contributor;
            }
            catch
            {
                // Ignore single-item localization failures.
            }
        });

        await Task.WhenAll(tasks);
    }

    private async Task<(string NameZhCn, string SummaryZhCn, string Contributor)> FetchCommunityLocalizationAsync(string platform, string id, string entityType = "mod")
    {
        var normalizedPlatform = NormalizeCommunityPlatform(platform);
        var normalizedEntityType = NormalizeCommunityEntityType(entityType);

        // modpack/collection 类型不需要 platform，直接用 id
        if (string.Equals(normalizedEntityType, "mod", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(normalizedPlatform) || string.IsNullOrWhiteSpace(id))
            {
                return (string.Empty, string.Empty, string.Empty);
            }
        }
        else if (string.IsNullOrWhiteSpace(id))
        {
            return (string.Empty, string.Empty, string.Empty);
        }

        // 优先走 CommunityLocalizationService（带缓存 + 源选择 + 降级）
        if (_localizationService != null)
        {
            var entry = await _localizationService.GetAsync(normalizedEntityType, normalizedPlatform, id);
            if (entry != null)
            {
                var contributor = FirstNonEmpty(
                    entry.Meta?.Contributor,
                    entry.Name?.Source);
                return (entry.Name?.ZhCn ?? string.Empty, entry.Description?.ZhCn ?? string.Empty, contributor ?? string.Empty);
            }

            return (string.Empty, string.Empty, string.Empty);
        }

        // 回退：无缓存直连（兼容未注入服务的场景）
        var relativePath = CommunityLocalizationService.BuildRelativePath(normalizedEntityType, normalizedPlatform, id);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return (string.Empty, string.Empty, string.Empty);
        }

        var urls = new[]
        {
            $"https://raw.githubusercontent.com/panda-lsy/StardewValley-Community-Localization/main/{relativePath}",
            $"https://gitee.com/mc_shengxia/StardewValley-Community-Localization/raw/main/{relativePath}"
        };

        foreach (var url in urls)
        {
            try
            {
                using var response = await GetHttpClient().GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                var root = doc.RootElement;
                var nameZhCn = TryGetNestedString(root, "name", "zh-CN");
                var summaryZhCn = TryGetNestedString(root, "description", "zh-CN");
                var contributor = TryGetNestedString(root, "contributor", "zh-CN");

                if (string.IsNullOrWhiteSpace(contributor) &&
                    root.TryGetProperty("meta", out var meta) &&
                    meta.ValueKind == JsonValueKind.Object)
                {
                    contributor = FirstNonEmpty(
                        TryGetNestedString(meta, "contributor", "zh-CN"),
                        TryGetString(meta, "contributor"),
                        TryGetNestedString(meta, "translator", "zh-CN"),
                        TryGetString(meta, "translator"));
                }

                if (string.IsNullOrWhiteSpace(contributor) && root.TryGetProperty("contributor", out var contributorRaw) && contributorRaw.ValueKind == JsonValueKind.String)
                {
                    contributor = contributorRaw.GetString() ?? string.Empty;
                }

                if (string.IsNullOrWhiteSpace(contributor))
                {
                    contributor = TryGetNestedString(root, "translator", "zh-CN");
                }

                if (string.IsNullOrWhiteSpace(contributor) && root.TryGetProperty("translator", out var translatorRaw) && translatorRaw.ValueKind == JsonValueKind.String)
                {
                    contributor = translatorRaw.GetString() ?? string.Empty;
                }

                if (!string.IsNullOrWhiteSpace(nameZhCn) || !string.IsNullOrWhiteSpace(summaryZhCn) || !string.IsNullOrWhiteSpace(contributor))
                {
                    return (nameZhCn, summaryZhCn, contributor);
                }
            }
            catch
            {
                // Try next provider.
            }
        }

        return (string.Empty, string.Empty, string.Empty);
    }

    private static string NormalizeCommunityPlatform(string platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
        {
            return string.Empty;
        }

        if (platform.Contains("nexus", StringComparison.OrdinalIgnoreCase))
        {
            return "NexusMods";
        }

        if (platform.Contains("curse", StringComparison.OrdinalIgnoreCase))
        {
            return "Curseforge";
        }

        return string.Empty;
    }

    /// <summary>归一化社区汉化实体类型（mod/modpack/collection）。</summary>
    private static string NormalizeCommunityEntityType(string entityType)
    {
        if (string.IsNullOrWhiteSpace(entityType))
        {
            return "mod";
        }

        if (entityType.Contains("collection", StringComparison.OrdinalIgnoreCase))
        {
            return "collection";
        }

        if (entityType.Contains("modpack", StringComparison.OrdinalIgnoreCase) ||
            entityType.Contains("pack", StringComparison.OrdinalIgnoreCase))
        {
            return "modpack";
        }

        return "mod";
    }

    private static bool MatchesModTypeFilter(string itemModType, string requestedType)
    {
        if (string.IsNullOrWhiteSpace(requestedType))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(itemModType))
        {
            return false;
        }

        return string.Equals(itemModType, requestedType, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesGameVersionFilter(IEnumerable<string> candidates, string fallbackTag, string requestedVersion)
    {
        if (string.IsNullOrWhiteSpace(requestedVersion))
        {
            return true;
        }

        var normalizedRequest = requestedVersion.Trim();
        var normalizedCandidates = (candidates ?? Enumerable.Empty<string>())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .ToList();

        if (!string.IsNullOrWhiteSpace(fallbackTag))
        {
            normalizedCandidates.Add(fallbackTag.Trim());
        }

        if (normalizedCandidates.Count == 0)
        {
            return false;
        }

        return normalizedCandidates.Any(candidate =>
            candidate.Equals(normalizedRequest, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(normalizedRequest + ".", StringComparison.OrdinalIgnoreCase) ||
            normalizedRequest.StartsWith(candidate + ".", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeModTypeTag(string? rawType)
    {
        if (string.IsNullOrWhiteSpace(rawType))
        {
            return string.Empty;
        }

        var text = rawType.Trim();
        if (text.Contains("ui", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("界面", StringComparison.OrdinalIgnoreCase))
        {
            return "界面美化";
        }

        if (text.Contains("content", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("expansion", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("内容", StringComparison.OrdinalIgnoreCase))
        {
            return "游戏内容";
        }

        if (text.Contains("tool", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("utility", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("工具", StringComparison.OrdinalIgnoreCase))
        {
            return "工具类";
        }

        if (text.Contains("texture", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("audio", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("sound", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("材质", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("音效", StringComparison.OrdinalIgnoreCase))
        {
            return "音效材质";
        }

        if (text.Contains("cheat", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("debug", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("作弊", StringComparison.OrdinalIgnoreCase))
        {
            return "作弊类";
        }

        if (text.Contains("framework", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("mechanic", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("功能", StringComparison.OrdinalIgnoreCase))
        {
            return "功能扩展";
        }

        return "功能扩展";
    }

    private static string ResolveCurseforgeModType(JsonElement item)
    {
        if (!item.TryGetProperty("categories", out var categories) || categories.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        foreach (var category in categories.EnumerateArray())
        {
            var name = TryGetString(category, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            return NormalizeModTypeTag(name);
        }

        return string.Empty;
    }

    private static List<string> ResolveCurseforgeGameVersions(JsonElement item, string summary, string name)
    {
        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (item.TryGetProperty("latestFilesIndexes", out var fileIndexes) && fileIndexes.ValueKind == JsonValueKind.Array)
        {
            foreach (var fileIndex in fileIndexes.EnumerateArray())
            {
                var version = TryGetString(fileIndex, "gameVersion");
                if (!string.IsNullOrWhiteSpace(version))
                {
                    versions.Add(version);
                }
            }
        }

        foreach (var version in ParsePossibleGameVersions(summary).Concat(ParsePossibleGameVersions(name)))
        {
            versions.Add(version);
        }

        return versions
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .OrderByDescending(item => item, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> ParsePossibleGameVersions(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        // Strip SV/SDV prefixes before version numbers so "SV1.6"/"SDV1.6" are recognized.
        var cleaned = Regex.Replace(text, @"(?i)S[DV]V?(?=\s*\d)", " ");
        var matches = Regex.Matches(cleaned, @"\b1\.\d+(?:\.\d+)?\+?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return matches
            .Select(match => NormalizeStardewGameVersionToken(match.Value, keepPatch: true))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<ModSearchResultItem> Deduplicate(IEnumerable<ModSearchResultItem> items)
    {
        // 按 ResourceId + Source + IsModpack 去重（替代旧的字符串去重）。
        return items
            .Where(item => item != null)
            .GroupBy(item => (item.Identity.ResourceId, item.Identity.Source, item.Identity.IsModpack))
            .Select(group => group.First())
            .ToList();
    }

    /// <summary>把内部 RemoteSearchItem 转为对外 ModSearchResultItem（应用本地化显示字段）。</summary>
    private static ModSearchResultItem ToSearchResultItem(RemoteSearchItem item, CatalogSource source, bool isModpack)
    {
        var displayName = !string.IsNullOrWhiteSpace(item.LocalizedName) ? item.LocalizedName : item.Name;
        var displaySummary = !string.IsNullOrWhiteSpace(item.LocalizedSummary) ? item.LocalizedSummary : item.Summary;
        return new ModSearchResultItem
        {
            Identity = new CatalogResourceIdentity(item.ResourceId, item.Name, source, isModpack, item.CollectionSlug ?? string.Empty),
            Name = displayName,
            Summary = displaySummary,
            Stat = item.Stat,
            TimeTag = item.TimeTag,
            IconUrl = item.IconUrl,
            FullIconUrl = item.FullIconUrl,
            ModType = item.ModType,
            GameVersionTag = item.GameVersionTag
        };
    }

    public async Task<List<SmapiVersionEntry>> GetSmapiVersionEntriesFromCurseForgeAsync(int page = 1, int perPage = 5)
    {
        var result = new List<SmapiVersionEntry>();

        try
        {
            // SMAPI 的 CurseForge 项目 ID 是 898372
            const long smapiCurseforgeProjectId = 898372;
            var filesUrl = $"https://api.curse.tools/v1/mods/{smapiCurseforgeProjectId}/files?index=0&pageSize=50";

            using var response = await GetWithRedirectAsync(filesUrl, maxRedirects: 2);
            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"[RemoteCatalogService] CurseForge SMAPI files request failed: {(int)response.StatusCode}");
                return result;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var file in data.EnumerateArray())
            {
                var displayName = TryGetString(file, "displayName");
                var fileName = TryGetString(file, "fileName");
                var fileId = TryGetLong(file, "id");
                var fileDate = TryGetString(file, "fileDate");

                DateTime.TryParse(fileDate, out var parsed);

                var version = !string.IsNullOrWhiteSpace(displayName) ? displayName : ExtractVersionFromName(fileName);
                var downloadUrl = $"https://edge.forgecdn.net/files/{fileId / 1000}/{fileId % 1000}";

                result.Add(new SmapiVersionEntry
                {
                    Version = version,
                    Description = fileName ?? string.Empty,
                    Source = "CurseForge",
                    DownloadUrl = downloadUrl,
                    PublishedDate = parsed
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RemoteCatalogService] GetSmapiVersionEntriesFromCurseForgeAsync failed: {ex.Message}");
        }

        return result
            .OrderByDescending(item => item.PublishedDate)
            .Skip(Math.Max(0, page - 1) * Math.Max(1, perPage))
            .Take(Math.Max(1, perPage))
            .ToList();
    }

    private static string ExtractVersionFromName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "unknown";
        var match = System.Text.RegularExpressions.Regex.Match(name, @"(\d+\.\d+\.?\d*)");
        return match.Success ? match.Value : name;
    }

    public async Task<List<SmapiVersionEntry>> GetSmapiVersionEntriesAsync(int page = 1, int perPage = 5)
    {
        var result = new List<SmapiVersionEntry>();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/Pathoschild/SMAPI/releases");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await GetHttpClient().SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return result;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var release in doc.RootElement.EnumerateArray())
            {
                var tag = TryGetString(release, "tag_name");
                var publishedText = TryGetString(release, "published_at");
                var published = DateTime.TryParse(publishedText, out var parsed) ? parsed : DateTime.MinValue;

                string downloadUrl = string.Empty;
                if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var assetName = TryGetString(asset, "name");
                        var assetUrl = TryGetString(asset, "browser_download_url");
                        if (!string.IsNullOrWhiteSpace(assetName) && !string.IsNullOrWhiteSpace(assetUrl) &&
                            assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = assetUrl;
                            break;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(downloadUrl))
                {
                    continue;
                }

                var version = string.IsNullOrWhiteSpace(tag) ? "unknown" : tag.TrimStart('v', 'V');
                var prerelease = release.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True;

                result.Add(new SmapiVersionEntry
                {
                    Version = version,
                    Description = prerelease ? $"{tag} (预发布)" : tag,
                    Source = "GitHub",
                    DownloadUrl = downloadUrl,
                    PublishedDate = published,
                    IsPrerelease = prerelease
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RemoteCatalogService] GetSmapiVersionEntriesAsync failed: {ex.Message}");
        }

        return result
            .OrderByDescending(item => item.PublishedDate)
            .Skip(Math.Max(0, page - 1) * Math.Max(1, perPage))
            .Take(Math.Max(1, perPage))
            .ToList();
    }

    private static string TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    }

    private static string TryGetNestedString(JsonElement element, string objectPropertyName, string valuePropertyName)
    {
        if (!element.TryGetProperty(objectPropertyName, out var nested) || nested.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        return TryGetString(nested, valuePropertyName);
    }

    private static string TryFormatTimeTag(string rawTime)
    {
        if (!DateTime.TryParse(rawTime, out var value))
        {
            return string.Empty;
        }

        return $"{value:yyyy-MM-dd}";
    }

    private static long TryGetLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        return 0;
    }

    private sealed class RemoteSearchItem
    {
        public long ResourceId { get; init; }

        public string Name { get; init; } = string.Empty;

        public string Summary { get; init; } = string.Empty;

        public string Stat { get; init; } = string.Empty;

        public string TimeTag { get; init; } = string.Empty;

        public string IconUrl { get; init; } = string.Empty;

        public string FullIconUrl { get; init; } = string.Empty;

        public string ModType { get; set; } = string.Empty;

        public string GameVersionTag { get; set; } = string.Empty;

        public List<string> SupportedGameVersions { get; set; } = [];

        public string LocalizedName { get; set; } = string.Empty;

        public string LocalizedSummary { get; set; } = string.Empty;

        public string LocalizedContributor { get; set; } = string.Empty;

        public CatalogSource Source { get; set; } = CatalogSource.Unknown;

        public string SourceTagHint { get; set; } = string.Empty;

        /// <summary>Nexus Collection 的 slug（用于社区汉化 Collections/{slug}.json 路径）。</summary>
        public string CollectionSlug { get; set; } = string.Empty;
    }
}

public sealed class CatalogResourceDetails
{
    public static CatalogResourceDetails Empty => new();

    public string Name { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string IconUrl { get; init; } = string.Empty;

    public string FullIconUrl { get; init; } = string.Empty;

    public string LocalizedContributor { get; init; } = string.Empty;

    public string LocalizedName { get; init; } = string.Empty;

    public string LocalizedSummary { get; init; } = string.Empty;

    public List<string> VersionOptions { get; init; } = [];

    public List<string> Dependencies { get; init; } = [];

    public List<string> DownloadOptions { get; init; } = [];
}

/// <summary>分页搜索结果。Items 已结构化为 ModSearchResultItem。</summary>
public sealed class CatalogPagedResult
{
    public List<ModSearchResultItem> Items { get; init; } = [];

    public bool HasMore { get; init; }
}
