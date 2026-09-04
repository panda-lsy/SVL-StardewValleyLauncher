using System.Text.RegularExpressions;

namespace SVL.Core.Platform.Abstractions;

public sealed class NxmLinkParser : INxmLinkParser
{
    private static readonly Regex ModPathPattern = new(@"^/mods/(?<modId>\d+)/files/(?<fileId>\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CollectionPathPattern = new(@"^/collections/(?<slug>[\w-]+)/revisions/(?<revision>latest|\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public bool TryParse(string? link, out NxmLinkInfo info, out string errorMessage)
    {
        info = new NxmLinkInfo();
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(link))
        {
            errorMessage = "NXM 链接为空";
            return false;
        }

        if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri))
        {
            errorMessage = "链接格式无效";
            return false;
        }

        if (!string.Equals(uri.Scheme, "nxm", StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = "仅支持 nxm:// 协议链接";
            return false;
        }

        var query = ParseQuery(uri.Query);
        var modPathMatch = ModPathPattern.Match(uri.AbsolutePath);
        if (modPathMatch.Success)
        {
            if (!long.TryParse(modPathMatch.Groups["modId"].Value, out var modId) ||
                !long.TryParse(modPathMatch.Groups["fileId"].Value, out var fileId))
            {
                errorMessage = "modId/fileId 解析失败";
                return false;
            }

            info = new NxmLinkInfo
            {
                ResourceType = NxmResourceType.ModFile,
                GameDomain = uri.Host,
                ModId = modId,
                FileId = fileId,
                Key = TryGetValue(query, "key"),
                Expires = TryParseLong(TryGetValue(query, "expires")),
                UserId = TryParseLong(TryGetValue(query, "user_id"))
            };

            return true;
        }

        var collectionPathMatch = CollectionPathPattern.Match(uri.AbsolutePath);
        if (collectionPathMatch.Success)
        {
            var slug = collectionPathMatch.Groups["slug"].Value;
            var revisionRaw = collectionPathMatch.Groups["revision"].Value;
            var revision = revisionRaw.Equals("latest", StringComparison.OrdinalIgnoreCase)
                ? -1
                : int.TryParse(revisionRaw, out var parsedRevision) ? parsedRevision : -1;

            info = new NxmLinkInfo
            {
                ResourceType = NxmResourceType.Collection,
                GameDomain = uri.Host,
                CollectionSlug = slug,
                RevisionNumber = revision,
                Key = TryGetValue(query, "key"),
                Expires = TryParseLong(TryGetValue(query, "expires")),
                UserId = TryParseLong(TryGetValue(query, "user_id"))
            };

            return true;
        }

        errorMessage = "仅支持两类链接：nxm://<game>/mods/<modId>/files/<fileId> 或 nxm://<game>/collections/<slug>/revisions/<latest|id>";
        return false;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
        {
            return map;
        }

        var parts = query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var index = part.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            var key = part[..index];
            var value = Uri.UnescapeDataString(part[(index + 1)..]);
            map[key] = value;
        }

        return map;
    }

    private static string? TryGetValue(Dictionary<string, string> query, string key)
    {
        return query.TryGetValue(key, out var value) ? value : null;
    }

    private static long? TryParseLong(string? raw)
    {
        return long.TryParse(raw, out var value) ? value : null;
    }
}