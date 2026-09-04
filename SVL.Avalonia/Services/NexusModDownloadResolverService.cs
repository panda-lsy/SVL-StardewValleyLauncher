using SVL.Core.Platform.Abstractions;
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

        if (string.IsNullOrWhiteSpace(apiKey) && string.IsNullOrWhiteSpace(accessToken))
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
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
        }

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            return NexusResolveResult.Failed($"请求 Nexus 下载地址失败: {ex.Message}");
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return NexusResolveResult.Failed($"Nexus 下载地址解析失败: HTTP {(int)response.StatusCode} {body}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var links = await JsonSerializer.DeserializeAsync<List<NexusDownloadLinkItem>>(stream, cancellationToken: cancellationToken);
        var picked = links?.FirstOrDefault(link => !string.IsNullOrWhiteSpace(link.Uri));

        if (picked == null)
        {
            return NexusResolveResult.Failed("Nexus 返回的下载地址为空");
        }

        var fileName = ResolveFileNameFromDownloadLink(picked, info);

        var allUrls = links?
            .Where(link => !string.IsNullOrWhiteSpace(link.Uri))
            .Select(link => link.Uri!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        return NexusResolveResult.Success(picked.Uri!, fileName, allUrls);
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

        if (string.IsNullOrWhiteSpace(apiKey) && string.IsNullOrWhiteSpace(accessToken))
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

                return NexusResolveResult.Success(resolved.DownloadUrl, preferredName, resolved.DownloadUrls);
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
        catch (Exception ex)
        {
            return NexusResolveResult.Failed($"请求 Nexus 下载地址失败: {ex.Message}");
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return NexusResolveResult.Failed($"Nexus 下载地址解析失败: HTTP {(int)response.StatusCode} {body}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var links = await JsonSerializer.DeserializeAsync<List<NexusDownloadLinkItem>>(stream, cancellationToken: cancellationToken);
        var picked = links?.FirstOrDefault(link => !string.IsNullOrWhiteSpace(link.Uri));

        if (picked == null)
        {
            return NexusResolveResult.Failed("Nexus 返回的下载地址为空");
        }

        var fileName = ExtractFileNameFromUri(picked.Uri);

        var allUrls = links?
            .Where(link => !string.IsNullOrWhiteSpace(link.Uri))
            .Select(link => link.Uri!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        return NexusResolveResult.Success(picked.Uri!, fileName, allUrls);
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

    public static NexusResolveResult Success(string downloadUrl, string fileName, IReadOnlyList<string>? downloadUrls = null)
    {
        return new NexusResolveResult
        {
            IsSuccess = true,
            Message = "解析成功",
            DownloadUrl = downloadUrl,
            FileName = fileName,
            DownloadUrls = downloadUrls ?? [downloadUrl]
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
