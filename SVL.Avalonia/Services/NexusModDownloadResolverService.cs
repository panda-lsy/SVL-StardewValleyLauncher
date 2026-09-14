using SVL.Core.Platform.Abstractions;
using SVL.Avalonia.Models;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SVL.Avalonia.Services;

public sealed class NexusModDownloadResolverService
{
    private static readonly HttpClient Http = CreateClient();

    public async Task<NexusResolveResult> ResolveDownloadUrlAsync(
        NxmLinkInfo info,
        string apiKey,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (info.ResourceType != NxmResourceType.ModFile)
        {
            return NexusResolveResult.Failed("仅支持 NXM Mod 文件链接解析");
        }

        if (string.IsNullOrWhiteSpace(info.GameDomain) || info.ModId <= 0 || info.FileId <= 0)
        {
            return NexusResolveResult.Failed("NXM 链接缺少必要参数（game/mod/file）");
        }

        // 浏览器回调返回的 NXM 链接自带 key/expires/user_id，即使本地没有保存
        // Nexus 登录凭据，也可以直接用这些一次性凭据解析下载地址。
        if (string.IsNullOrWhiteSpace(apiKey) &&
            string.IsNullOrWhiteSpace(accessToken) &&
            string.IsNullOrWhiteSpace(info.Key))
        {
            return NexusResolveResult.Failed("Nexus 未登录，无法解析真实下载地址");
        }

        var uriBuilder = new UriBuilder($"https://api.nexusmods.com/v1/games/{info.GameDomain}/mods/{info.ModId}/files/{info.FileId}/download_link.json");
        var queryItems = new List<string>();

        if (!string.IsNullOrWhiteSpace(info.Key))
        {
            queryItems.Add($"key={Uri.EscapeDataString(info.Key)}");
        }

        if (info.Expires.HasValue)
        {
            queryItems.Add($"expires={info.Expires.Value}");
        }

        if (info.UserId.HasValue)
        {
            queryItems.Add($"user_id={info.UserId.Value}");
        }

        if (queryItems.Count > 0)
        {
            uriBuilder.Query = string.Join("&", queryItems);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uriBuilder.Uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("application-name", "SVL.Avalonia");

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Add("apikey", apiKey.Trim());
        }
        else if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
        }

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 用户取消必须穿透解析层，不能被当成“没有解析到地址”而继续
            // 打开浏览器或落入其它下载回退分支。
            throw;
        }
        catch (Exception ex)
        {
            return NexusResolveResult.Failed($"请求 Nexus 下载地址失败: {ex.Message}");
        }

        using (response)
        {
            NexusApiRateLimitService.Record(response.Headers);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                return NexusResolveResult.Failed($"Nexus 下载地址解析失败: HTTP {(int)response.StatusCode} {body}");
            }

            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            var links = ParseDownloadLinks(document.RootElement);
            var picked = links.FirstOrDefault();

            if (picked == null)
            {
                return NexusResolveResult.Failed("Nexus 返回的下载地址为空");
            }

            var fileName = ResolveFileNameFromDownloadLink(picked, info);

            var allUrls = links
                .Select(link => link.Uri!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];

            return NexusResolveResult.Success(picked.Uri!, fileName, allUrls, info.ModId, info.FileId);
        }
    }

    /// <summary>
    /// 当来源只记录了 Nexus Mod ID 时，查询文件列表并选择主文件/最新文件。
    /// 这是旧版整合包清单常见的格式，不能因为缺少 fileId 就直接判定 Mod 安装失败。
    /// </summary>
    public async Task<NexusResolveResult> ResolveLatestDownloadUrlAsync(
        long modId,
        string gameDomain,
        AppUserSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (modId <= 0 || string.IsNullOrWhiteSpace(gameDomain))
        {
            return NexusResolveResult.Failed("Nexus Mod 来源缺少有效 ID");
        }

        if (string.IsNullOrWhiteSpace(settings.NexusApiKey) &&
            string.IsNullOrWhiteSpace(settings.NexusOAuthAccessToken))
        {
            return NexusResolveResult.Failed("Nexus 未登录，无法查询 Mod 文件列表");
        }

        var requestUri = $"https://api.nexusmods.com/v1/games/{gameDomain}/mods/{modId}/files.json";
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("application-name", "SVL.Avalonia");
        if (!string.IsNullOrWhiteSpace(settings.NexusApiKey))
        {
            request.Headers.Add("apikey", settings.NexusApiKey.Trim());
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", settings.NexusOAuthAccessToken.Trim());
        }

        try
        {
            using var response = await Http.SendAsync(request, cancellationToken);
            NexusApiRateLimitService.Record(response.Headers);
            if (!response.IsSuccessStatusCode)
            {
                return NexusResolveResult.Failed($"Nexus 文件列表解析失败: HTTP {(int)response.StatusCode}");
            }

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));
            var files = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToList()
                : document.RootElement.TryGetProperty("files", out var fileArray) &&
                  fileArray.ValueKind == JsonValueKind.Array
                    ? fileArray.EnumerateArray().ToList()
                    : [];

            var selected = files
                .Where(file => GetJsonLong(file, "file_id", "id") > 0)
                .OrderByDescending(file => IsMainFile(file))
                .ThenByDescending(file => GetJsonDate(file, "uploaded_time", "date"))
                .ThenByDescending(file => GetJsonLong(file, "file_id", "id"))
                .FirstOrDefault();
            if (selected.ValueKind == JsonValueKind.Undefined)
            {
                return NexusResolveResult.Failed("Nexus 未返回可用 Mod 文件");
            }

            var fileId = GetJsonLong(selected, "file_id", "id");
            var info = new NxmLinkInfo
            {
                ResourceType = NxmResourceType.ModFile,
                GameDomain = gameDomain,
                ModId = modId,
                FileId = fileId
            };
            return await ResolveDownloadUrlAsync(
                info, settings.NexusApiKey, settings.NexusOAuthAccessToken, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return NexusResolveResult.Failed($"请求 Nexus 文件列表失败: {ex.Message}");
        }
    }

    private static bool IsMainFile(JsonElement file)
    {
        var category = GetJsonString(file, "category_name", "category");
        var categoryId = GetJsonLong(file, "category_id");
        var isPrimary = file.TryGetProperty("is_primary", out var primary) &&
                        primary.ValueKind == JsonValueKind.True;
        return isPrimary || categoryId == 1 || string.Equals(category, "MAIN", StringComparison.OrdinalIgnoreCase);
    }

    private static long GetJsonLong(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number)) return number;
        }

        return 0;
    }

    private static string GetJsonString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static DateTime GetJsonDate(JsonElement element, params string[] names)
    {
        var text = GetJsonString(element, names);
        return DateTime.TryParse(text, out var date) ? date : DateTime.MinValue;
    }

    public async Task<NexusResolveResult> ResolveCollectionDownloadUrlAsync(
        NxmLinkInfo info,
        string apiKey,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (info.ResourceType != NxmResourceType.Collection)
        {
            return NexusResolveResult.Failed("仅支持 NXM Collection 链接解析");
        }

        if (string.IsNullOrWhiteSpace(info.GameDomain) || string.IsNullOrWhiteSpace(info.CollectionSlug))
        {
            return NexusResolveResult.Failed("NXM Collection 链接缺少必要参数（game/slug）");
        }

        // 浏览器回调返回的 Collection NXM 链接同样携带一次性 key。
        // 没有本地账号凭据时，仍可使用该 key 请求 Nexus API；否则浏览器回退
        // 在无账号登录状态下会被这里提前拦截，表现为“已回调但仍无法下载”。
        if (string.IsNullOrWhiteSpace(apiKey) &&
            string.IsNullOrWhiteSpace(accessToken) &&
            string.IsNullOrWhiteSpace(info.Key))
        {
            return NexusResolveResult.Failed("Nexus 未登录，无法解析 Collection 下载地址");
        }

        var revision = info.RevisionNumber < 0 ? "latest" : info.RevisionNumber.ToString();
        var candidateUrls = new List<string>
        {
            $"https://api.nexusmods.com/v1/games/{info.GameDomain}/collections/{info.CollectionSlug}/revisions/{revision}/download_link.json",
            $"https://api.nexusmods.com/v1/games/{info.GameDomain}/collections/{info.CollectionSlug}/revisions/{revision}/download-links.json"
        };

        var queryItems = new List<string>();
        if (!string.IsNullOrWhiteSpace(info.Key))
        {
            queryItems.Add($"key={Uri.EscapeDataString(info.Key)}");
        }

        if (info.Expires.HasValue)
        {
            queryItems.Add($"expires={info.Expires.Value}");
        }

        if (info.UserId.HasValue)
        {
            queryItems.Add($"user_id={info.UserId.Value}");
        }

        var query = queryItems.Count > 0 ? "?" + string.Join("&", queryItems) : string.Empty;

        foreach (var baseUrl in candidateUrls)
        {
            var resolved = await TryResolveByRequestAsync(baseUrl + query, apiKey, accessToken, cancellationToken);
            if (resolved.IsSuccess)
            {
                var preferredName = string.IsNullOrWhiteSpace(resolved.FileName)
                    ? $"collection-{info.CollectionSlug}-rev-{revision}.zip"
                    : resolved.FileName;

                // Collection API 返回的多个 URI 是同一个 Collection 包的镜像，
                // 不能作为多个待安装文件交给任务队列，否则会把镜像重复下载，
                // 后续还可能把 CDN 响应误当成多个 Mod 资源。
                return NexusResolveResult.Success(resolved.DownloadUrl, preferredName, [resolved.DownloadUrl]);
            }
        }

        return NexusResolveResult.Failed("Nexus Collection 下载地址解析失败：未找到可用下载链接");
    }

    private static async Task<NexusResolveResult> TryResolveByRequestAsync(
        string requestUrl,
        string apiKey,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("application-name", "SVL.Avalonia");

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Add("apikey", apiKey.Trim());
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
        }

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return NexusResolveResult.Failed($"请求 Nexus 下载地址失败: {ex.Message}");
        }

        using (response)
        {
            NexusApiRateLimitService.Record(response.Headers);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                return NexusResolveResult.Failed($"Nexus 下载地址解析失败: HTTP {(int)response.StatusCode} {body}");
            }

            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            var links = ParseDownloadLinks(document.RootElement);
            var picked = links.FirstOrDefault();

            if (picked == null)
            {
                return NexusResolveResult.Failed("Nexus 返回的下载地址为空");
            }

            var fileName = ExtractFileNameFromUri(picked.Uri);

            var allUrls = links
                .Select(link => link.Uri!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];

            return NexusResolveResult.Success(picked.Uri!, fileName, allUrls);
        }
    }

    /// <summary>从下载 URL 路径中提取文件名。</summary>
    private static string ExtractFileNameFromUri(string? uri)
    {
        if (!string.IsNullOrWhiteSpace(uri) &&
            Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            var name = Path.GetFileName(parsed.LocalPath);
            if (!string.IsNullOrWhiteSpace(name) && name.Contains('.'))
            {
                return name;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 兼容 Nexus 下载接口及历史导出中的多种响应形态：
    /// 顶层数组、{download_links:[...]}/{download_links:{...}}、字符串 URL、
    /// 键值映射，以及 data/result/links 包装；字段名不区分大小写。
    /// </summary>
    private static List<NexusDownloadLinkItem> ParseDownloadLinks(JsonElement root)
    {
        var links = new List<NexusDownloadLinkItem>();
        AppendDownloadLinks(root, links, depth: 0);
        return links
            .Where(link => !string.IsNullOrWhiteSpace(link.Uri))
            .GroupBy(link => link.Uri!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static void AppendDownloadLinks(
        JsonElement value,
        ICollection<NexusDownloadLinkItem> destination,
        int depth,
        string? mapName = null)
    {
        if (depth > 4)
        {
            return;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var uri = value.GetString()?.Trim();
            if (IsHttpDownloadUrl(uri))
            {
                destination.Add(new NexusDownloadLinkItem
                {
                    Uri = uri,
                    Name = mapName
                });
            }

            return;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                AppendDownloadLinks(item, destination, depth + 1);
            }

            return;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (TryGetStringPropertyIgnoreCase(
                value,
                out var directUri,
                "uri",
                "url",
                "downloadUrl",
                "download_url"))
        {
            var item = new NexusDownloadLinkItem
            {
                Uri = directUri,
                Name = GetStringPropertyIgnoreCase(value, "name"),
                ShortName = GetStringPropertyIgnoreCase(value, "short_name", "shortName")
            };
            if (IsHttpDownloadUrl(item.Uri))
            {
                destination.Add(item);
            }

            return;
        }

        var wrapped = false;
        foreach (var propertyName in new[]
                 {
                     "download_links",
                     "download-links",
                     "links",
                     "data",
                     "result"
                 })
        {
            if (TryGetPropertyIgnoreCase(value, propertyName, out var child))
            {
                wrapped = true;
                AppendDownloadLinks(child, destination, depth + 1);
            }
        }

        if (wrapped)
        {
            return;
        }

        // 某些旧响应把 CDN 名称作为 key、URL 作为 value。
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                AppendDownloadLinks(property.Value, destination, depth + 1, property.Name);
            }
        }
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement objectElement,
        string propertyName,
        out JsonElement value)
    {
        foreach (var property in objectElement.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetStringPropertyIgnoreCase(
        JsonElement objectElement,
        out string value,
        params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (TryGetPropertyIgnoreCase(objectElement, propertyName, out var property) &&
                property.ValueKind == JsonValueKind.String)
            {
                value = property.GetString() ?? string.Empty;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static string GetStringPropertyIgnoreCase(
        JsonElement objectElement,
        params string[] propertyNames)
    {
        return TryGetStringPropertyIgnoreCase(objectElement, out var value, propertyNames)
            ? value
            : string.Empty;
    }

    private static bool IsHttpDownloadUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("SVL-Avalonia/1.0");
        return client;
    }

    /// <summary>
    /// 从 Nexus download_link.json 返回的下载链接项中解析文件名。
    /// download_link.json 的 name/short_name 字段是 CDN 名称（如 "Nexus CDN"），不是文件名。
    /// 优先从下载 URL 的路径中提取真实文件名，回退到 short_name/name。
    /// </summary>
    private static string ResolveFileNameFromDownloadLink(NexusDownloadLinkItem link, NxmLinkInfo info)
    {
        // 优先从 URL 路径中提取文件名（URL 中的文件名是真实文件名）
        if (!string.IsNullOrWhiteSpace(link.Uri) &&
            Uri.TryCreate(link.Uri, UriKind.Absolute, out var uri))
        {
            var urlFileName = Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(urlFileName) && urlFileName.Contains('.'))
            {
                return urlFileName;
            }
        }

        // 回退到 short_name（可能是短文件名）
        if (!string.IsNullOrWhiteSpace(link.ShortName) && link.ShortName.Contains('.'))
        {
            return link.ShortName;
        }

        // 回退到 name（可能是 CDN 名称，但总比没有好）
        if (!string.IsNullOrWhiteSpace(link.Name) && link.Name.Contains('.'))
        {
            return link.Name;
        }

        // 最终回退
        return $"nexus-{info.ModId}-{info.FileId}.zip";
    }
}

public sealed class NexusResolveResult
{
    public bool IsSuccess { get; init; }

    public string Message { get; init; } = string.Empty;

    public string DownloadUrl { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;

    public IReadOnlyList<string> DownloadUrls { get; init; } = [];

    public long ModId { get; init; }

    public long FileId { get; init; }

    public static NexusResolveResult Success(
        string downloadUrl,
        string fileName,
        IReadOnlyList<string>? downloadUrls = null,
        long modId = 0,
        long fileId = 0)
    {
        return new NexusResolveResult
        {
            IsSuccess = true,
            Message = "解析成功",
            DownloadUrl = downloadUrl,
            FileName = fileName,
            DownloadUrls = downloadUrls ?? [downloadUrl],
            ModId = modId,
            FileId = fileId
        };
    }

    public static NexusResolveResult Failed(string message)
    {
        return new NexusResolveResult
        {
            IsSuccess = false,
            Message = message
        };
    }
}

internal sealed class NexusDownloadLinkItem
{
    [JsonPropertyName("URI")]
    public string? Uri { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("short_name")]
    public string? ShortName { get; set; }
}
