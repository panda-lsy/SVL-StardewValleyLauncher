using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SVL.Core.Logging;

namespace SVL.Core.Stardew.Mod;

public sealed class SvlSourceMetadata
{
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = string.Empty;

    [JsonPropertyName("projectId")]
    public string ProjectId { get; set; } = string.Empty;

    [JsonPropertyName("fileId")]
    public string FileId { get; set; } = string.Empty;

    [JsonPropertyName("modId")]
    public string ModId { get; set; } = string.Empty;

    [JsonPropertyName("modName")]
    public string ModName { get; set; } = string.Empty;

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("installedAtUtc")]
    public string InstalledAtUtc { get; set; } = string.Empty;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 3;

    [JsonPropertyName("isParentMod")]
    public bool IsParentMod { get; set; }

    [JsonPropertyName("parentMod")]
    public SvlParentModReference? ParentMod { get; set; }

    [JsonPropertyName("childMods")]
    public List<SvlChildModReference> ChildMods { get; set; } = new();

    [JsonPropertyName("localization")]
    public SvlSourceLocalization? Localization { get; set; }
}

public sealed class SvlParentModReference
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("relativePath")]
    public string RelativePath { get; set; } = string.Empty;
}

public sealed class SvlChildModReference
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("relativePath")]
    public string RelativePath { get; set; } = string.Empty;

    [JsonPropertyName("uniqueId")]
    public string UniqueId { get; set; } = string.Empty;
}

public sealed class SvlSourceLocalization
{
    [JsonPropertyName("entityType")]
    public string EntityType { get; set; } = string.Empty;

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("nameZhCn")]
    public string NameZhCn { get; set; } = string.Empty;

    [JsonPropertyName("nameSource")]
    public string NameSource { get; set; } = string.Empty;

    [JsonPropertyName("descriptionZhCn")]
    public string DescriptionZhCn { get; set; } = string.Empty;

    [JsonPropertyName("descriptionSource")]
    public string DescriptionSource { get; set; } = string.Empty;

    [JsonPropertyName("sourceUrl")]
    public string SourceUrl { get; set; } = string.Empty;

    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; set; } = string.Empty;

    [JsonPropertyName("contributor")]
    public string Contributor { get; set; } = string.Empty;
}

public static class SvlSourceMetadataStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string GetFilePath(string modDir)
    {
        return Path.Combine(modDir, "svl-source.json");
    }

    public static SvlSourceMetadata? TryReadFromDirectory(string modDir)
    {
        try
        {
            var filePath = GetFilePath(modDir);
            if (!File.Exists(filePath))
                return null;

            return JsonSerializer.Deserialize<SvlSourceMetadata>(File.ReadAllText(filePath), s_jsonOptions);
        }
        catch (Exception ex)
        {
            Log.Warn($"[SvlSourceMetadataStore] 读取来源元数据失败: {modDir}", ex);
            return null;
        }
    }

    public static bool WriteToDirectory(string modDir, SvlSourceMetadata metadata)
    {
        try
        {
            if (!Directory.Exists(modDir))
                return false;

            var filePath = GetFilePath(modDir);
            File.WriteAllText(filePath, JsonSerializer.Serialize(metadata, s_jsonOptions));
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"[SvlSourceMetadataStore] 写入来源元数据失败: {modDir}", ex);
            return false;
        }
    }
}