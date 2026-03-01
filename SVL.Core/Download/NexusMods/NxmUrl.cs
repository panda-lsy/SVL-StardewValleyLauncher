using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SVL.Core.Download.NexusMods;

/// <summary>
/// NXM URL类型
/// </summary>
public enum NxmUrlType
{
    Mod,
    Collection,
    OAuth,
    Premium
}

/// <summary>
/// NXM协议URL解析器
/// 解析格式：nxm://GAME_DOMAIN/mods/MOD_ID/files/FILE_ID?key=API_KEY&expires=TIMESTAMP&user_id=USER_ID&view=true
/// </summary>
public class NxmUrl
{
    private static readonly Regex ModUrlExpression = new Regex(@"/mods/(\d+)/files/(\d+)", RegexOptions.IgnoreCase);
    private static readonly Regex CollectionUrlExpression = new Regex(@"/collections/(\w+)/revisions/(\d+|latest)", RegexOptions.IgnoreCase);

    public string GameId { get; private set; }
    public long ModId { get; private set; }
    public long FileId { get; private set; }
    public long? CollectionId { get; private set; }
    public string CollectionSlug { get; private set; }
    public int? RevisionId { get; private set; }
    public int? RevisionNumber { get; private set; }
    public string OAuthCode { get; private set; }
    public string OAuthState { get; private set; }
    public string Key { get; private set; }
    public long? Expires { get; private set; }
    public long? UserId { get; private set; }
    public bool View { get; private set; }
    public Dictionary<string, string> ExtraParams { get; private set; } = new Dictionary<string, string>();
    public bool IsPremium { get; private set; }

    private NxmUrl()
    {
    }

    /// <summary>
    /// 创建 Mod 下载的 NXM URL
    /// </summary>
    public static NxmUrl CreateModUrl(string gameId, long modId, long fileId)
    {
        return new NxmUrl
        {
            GameId = gameId,
            ModId = modId,
            FileId = fileId
        };
    }

    /// <summary>
    /// 创建 Collection 下载的 NXM URL（使用 slug）
    /// </summary>
    public static NxmUrl CreateCollectionUrl(string gameId, string collectionSlug, int revisionNumber = -1)
    {
        return new NxmUrl
        {
            GameId = gameId,
            CollectionSlug = collectionSlug,
            RevisionNumber = revisionNumber
        };
    }

    /// <summary>
    /// 创建 Collection 下载的 NXM URL（使用 ID）
    /// </summary>
    public static NxmUrl CreateCollectionUrlById(string gameId, long collectionId, int revisionId)
    {
        return new NxmUrl
        {
            GameId = gameId,
            CollectionId = collectionId,
            RevisionId = revisionId
        };
    }

    /// <summary>
    /// 解析NXM URL字符串
    /// </summary>
    /// <param name="nxmUrlString">NXM URL字符串</param>
    /// <returns>解析后的NXM URL对象</returns>
    public static NxmUrl Parse(string nxmUrlString)
    {
        if (string.IsNullOrWhiteSpace(nxmUrlString))
            throw new ArgumentException("NXM URL不能为空", nameof(nxmUrlString));

        if (!nxmUrlString.StartsWith("nxm://"))
            throw new ArgumentException($"无效的NXM URL: {nxmUrlString}", nameof(nxmUrlString));

        try
        {
            var uri = new Uri(nxmUrlString);
            var result = new NxmUrl
            {
                GameId = uri.Host
            };

            // 解析路径
            var pathMatch = ModUrlExpression.Match(uri.AbsolutePath);
            var collectionPathMatch = CollectionUrlExpression.Match(uri.AbsolutePath);

            if (pathMatch.Success)
            {
                // MOD URL: nxm://stardewvalley/mods/1234/files/5678
                if (pathMatch.Groups.Count != 3)
                    throw new ArgumentException($"无效的NXM MOD URL格式: {nxmUrlString}");

                result.ModId = long.Parse(pathMatch.Groups[1].Value);
                result.FileId = long.Parse(pathMatch.Groups[2].Value);
            }
            else if (collectionPathMatch.Success)
            {
                // Collection URL: nxm://stardewvalley/collections/slug/revisions/123
                if (collectionPathMatch.Groups.Count != 3)
                    throw new ArgumentException($"无效的NXM Collection URL格式: {nxmUrlString}");

                var collectionIdOrSlug = collectionPathMatch.Groups[1].Value;
                var revision = collectionPathMatch.Groups[2].Value;

                // 尝试解析为数字ID（旧格式）
                if (long.TryParse(collectionIdOrSlug, out var collectionId) && collectionIdOrSlug.Length < 6)
                {
                    result.CollectionId = collectionId;
                    result.RevisionId = int.Parse(revision);
                }
                else
                {
                    // 新格式使用slug
                    result.CollectionSlug = collectionIdOrSlug;
                    result.RevisionNumber = revision == "latest" ? -1 : int.Parse(revision);
                }
            }
            else if (uri.Host == "oauth" && uri.AbsolutePath == "/callback")
            {
                // OAuth回调
                result.OAuthCode = GetQueryParam(uri.Query, "code");
                result.OAuthState = GetQueryParam(uri.Query, "state");
            }
            else if (uri.Host == "premium")
            {
                result.IsPremium = true;
            }
            else
            {
                throw new ArgumentException($"未识别的NXM URL类型: {nxmUrlString}");
            }

            // 解析查询参数
            result.Key = GetQueryParam(uri.Query, "key");

            var expiresStr = GetQueryParam(uri.Query, "expires");
            if (!string.IsNullOrEmpty(expiresStr) && long.TryParse(expiresStr, out var expires))
                result.Expires = expires;

            var userIdStr = GetQueryParam(uri.Query, "user_id");
            if (!string.IsNullOrEmpty(userIdStr) && long.TryParse(userIdStr, out var userId))
                result.UserId = userId;

            var viewStr = GetQueryParam(uri.Query, "view");
            if (!string.IsNullOrEmpty(viewStr))
            {
                result.View = viewStr.ToLower() == "true" || (int.TryParse(viewStr, out var viewInt) && viewInt > 0);
            }

            // 提取所有额外参数
            var queryParameters = uri.Query.TrimStart('?').Split('&');
            foreach (var param in queryParameters)
            {
                var parts = param.Split('=');
                if (parts.Length == 2)
                {
                    var key = parts[0];
                    var value = Uri.UnescapeDataString(parts[1]);
                    result.ExtraParams[key] = value;
                }
            }

            return result;
        }
        catch (Exception ex) when (!(ex is ArgumentException))
        {
            throw new ArgumentException($"解析NXM URL失败: {nxmUrlString}", ex);
        }
    }

    private static string GetQueryParam(string query, string paramName)
    {
        if (string.IsNullOrEmpty(query))
            return null;

        var param = $"{paramName}=";
        var index = query.IndexOf(param, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return null;

        var startIndex = index + param.Length;
        var endIndex = query.IndexOf('&', startIndex);
        if (endIndex < 0)
            endIndex = query.Length;

        return Uri.UnescapeDataString(query.Substring(startIndex, endIndex - startIndex));
    }

    /// <summary>
    /// 获取URL类型
    /// </summary>
    public NxmUrlType Type
    {
        get
        {
            if (!string.IsNullOrEmpty(OAuthCode))
                return NxmUrlType.OAuth;
            if (IsPremium)
                return NxmUrlType.Premium;
            if (CollectionId.HasValue || !string.IsNullOrEmpty(CollectionSlug))
                return NxmUrlType.Collection;
            return NxmUrlType.Mod;
        }
    }

    /// <summary>
    /// 是否为MOD类型URL
    /// </summary>
    public bool IsMod => Type == NxmUrlType.Mod;

    /// <summary>
    /// 是否为Collection类型URL
    /// </summary>
    public bool IsCollection => Type == NxmUrlType.Collection;

    /// <summary>
    /// 是否为OAuth回调URL
    /// </summary>
    public bool IsOAuth => Type == NxmUrlType.OAuth;

    /// <summary>
    /// 转换为字符串表示（包含完整的查询参数）
    /// </summary>
    public override string ToString()
    {
        if (IsMod)
        {
            var url = $"nxm://{GameId}/mods/{ModId}/files/{FileId}";
            var queryParams = new List<string>();

            if (!string.IsNullOrEmpty(Key))
                queryParams.Add($"key={Key}");

            if (Expires.HasValue)
                queryParams.Add($"expires={Expires.Value}");

            if (UserId.HasValue)
                queryParams.Add($"user_id={UserId.Value}");

            if (View)
                queryParams.Add("view=true");

            if (queryParams.Count > 0)
                url += "?" + string.Join("&", queryParams);

            return url;
        }
        else if (IsCollection)
        {
            var id = CollectionSlug ?? CollectionId.ToString();
            var rev = RevisionNumber.HasValue && RevisionNumber.Value == -1 ? "latest" : (RevisionId?.ToString() ?? "unknown");
            return $"nxm://{GameId}/collections/{id}/revisions/{rev}";
        }
        else if (IsOAuth)
        {
            return $"nxm://oauth/callback?code={OAuthCode}&state={OAuthState}";
        }
        return "nxm://unknown";
    }
}

/// <summary>
/// NXM URL 事件参数
/// </summary>
public class NxmUrlReceivedEventArgs : EventArgs
{
    /// <summary>
    /// 解析后的 NXM URL
    /// </summary>
    public NxmUrl Url { get; }

    /// <summary>
    /// 是否需要置顶窗口（来自其他实例或浏览器的信号）
    /// </summary>
    public bool ShouldBringToFront { get; }

    /// <summary>
    /// NXM URL 原始字符串
    /// </summary>
    public string OriginalUrl { get; }

    public NxmUrlReceivedEventArgs(NxmUrl url, bool shouldBringToFront, string originalUrl)
    {
        Url = url;
        ShouldBringToFront = shouldBringToFront;
        OriginalUrl = originalUrl;
    }
}
