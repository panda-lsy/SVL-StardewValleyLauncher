using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SVL.Core.Download.NexusMods;

/// <summary>
/// Nexus Mod信息
/// </summary>
public class NexusModInfo
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("game_id")]
    public long GameId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("summary")]
    public string Summary { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; }

    [JsonPropertyName("picture_url")]
    public string PictureUrl { get; set; }

    [JsonPropertyName("uid")]
    public string Uid { get; set; }

    [JsonPropertyName("mod_uploader")]
    public NexusUploader Uploader { get; set; }

    [JsonPropertyName("author")]
    public string Author { get; set; }

    [JsonPropertyName("category_id")]
    public long CategoryId { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; }

    [JsonPropertyName("endorsement_count")]
    public int EndorsementCount { get; set; }

    [JsonPropertyName("total_downloads")]
    public long TotalDownloads { get; set; }

    [JsonPropertyName("date_added")]
    public long DateAdded { get; set; }

    [JsonPropertyName("date_updated")]
    public long DateUpdated { get; set; }
}

/// <summary>
/// Nexus上传者信息
/// </summary>
public class NexusUploader
{
    [JsonPropertyName("member_id")]
    public long MemberId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("avatar_url")]
    public string AvatarUrl { get; set; }
}

/// <summary>
/// Nexus文件列表响应
/// </summary>
public class NexusFilesResponse
{
    [JsonPropertyName("files")]
    public List<NexusFileInfo> Files { get; set; }
}

/// <summary>
/// Nexus Mod文件信息
/// </summary>
public class NexusFileInfo
{
    [JsonPropertyName("id")]
    public object? Id { get; set; }

    [JsonPropertyName("mod_id")]
    public long ModId { get; set; }

    [JsonPropertyName("game_id")]
    public long GameId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; }

    [JsonPropertyName("category_id")]
    public long CategoryId { get; set; }

    [JsonPropertyName("is_primary")]
    public bool IsPrimary { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("file_name")]
    public string FileName { get; set; }

    [JsonPropertyName("uploaded_date")]
    public long UploadedDate { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; }

    [JsonPropertyName("content_preview_link")]
    public string ContentPreviewLink { get; set; }

    [JsonPropertyName("cdn_uri")]
    public string CdnUri { get; set; }

    [JsonPropertyName("mod_version")]
    public string ModVersion { get; set; }

    [JsonPropertyName("external_virus_scan_url")]
    public string ExternalVirusScanUrl { get; set; }

    [JsonPropertyName("dependencies")]
    public List<NexusDependency> Dependencies { get; set; }

    public long GetIdLong()
    {
        if (Id == null)
            return 0;

        if (Id is long l)
            return l;
        if (Id is int i)
            return i;
        if (Id is string s && long.TryParse(s, out var sl))
            return sl;

        if (Id is System.Text.Json.JsonElement element)
        {
            if (element.ValueKind == System.Text.Json.JsonValueKind.Number && element.TryGetInt64(out var n))
                return n;

            if (element.ValueKind == System.Text.Json.JsonValueKind.String && long.TryParse(element.GetString(), out var sn))
                return sn;

            if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == System.Text.Json.JsonValueKind.Number && item.TryGetInt64(out var an))
                        return an;
                    if (item.ValueKind == System.Text.Json.JsonValueKind.String && long.TryParse(item.GetString(), out var asn))
                        return asn;
                }
            }
        }

        return 0;
    }
}

/// <summary>
/// Nexus Mod依赖
/// </summary>
public class NexusDependency
{
    [JsonPropertyName("mod_id")]
    public long? ModId { get; set; }

    [JsonPropertyName("file_id")]
    public long? FileId { get; set; }

    [JsonPropertyName("relationship_type")]
    public int RelationshipType { get; set; }
}

/// <summary>
/// Nexus下载链接响应
/// </summary>
public class NexusDownloadLinkResponse
{
    [JsonPropertyName("URI")]
    public string Uri { get; set; }

    [JsonPropertyName("expires")]
    public long Expires { get; set; }
}

/// <summary>
/// Nexus用户信息
/// </summary>
public class NexusUserInfo
{
    [JsonPropertyName("user_id")]
    public long UserId { get; set; }

    [JsonPropertyName("key")]
    public string Key { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("email")]
    public string Email { get; set; }

    [JsonPropertyName("is_premium")]
    public bool IsPremium { get; set; }

    [JsonPropertyName("is_supporter")]
    public bool IsSupporter { get; set; }

    [JsonPropertyName("profile_url")]
    public string ProfileUrl { get; set; }

    [JsonPropertyName("avatar")]
    public string Avatar { get; set; }
}

/// <summary>
/// OAuth Token响应
/// </summary>
public class NexusTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; }

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; }

    [JsonPropertyName("scope")]
    public string Scope { get; set; }

    [JsonPropertyName("id_token")]
    public string IdToken { get; set; }
}

/// <summary>
/// JWT 用户信息（从 id_token 解析）
/// </summary>
public class NexusJwtUserInfo
{
    [JsonPropertyName("sub")]
    public string Sub { get; set; }  // 用户 ID

    [JsonPropertyName("name")]
    public string Name { get; set; }  // 用户名

    [JsonPropertyName("email")]
    public string Email { get; set; }  // 邮箱

    [JsonPropertyName("avatar")]
    public string Avatar { get; set; }  // 头像 URL

    [JsonPropertyName("membership_role")]
    public string MembershipRole { get; set; }  // 会员类型

    [JsonPropertyName("exp")]
    public long Exp { get; set; }  // 过期时间
}
