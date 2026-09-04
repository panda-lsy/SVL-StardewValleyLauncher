using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SVL.Core.Platform.Modpack;

/// <summary>Curseforge 整合包 manifest.json 模型（从 SVL.Core.Modpack 下沉）。</summary>
public sealed class CurseforgeModpackManifest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("minecraftVersion")]
    public string MinecraftVersion { get; set; } = string.Empty;

    [JsonPropertyName("manifestVersion")]
    public int ManifestVersion { get; set; }

    [JsonPropertyName("files")]
    public List<CurseforgeModpackFile> Files { get; set; } = new();

    [JsonPropertyName("overrides")]
    public string? Overrides { get; set; }
}

/// <summary>Curseforge 整合包中的单个文件条目。</summary>
public sealed class CurseforgeModpackFile
{
    [JsonPropertyName("projectID")]
    public long ProjectId { get; set; }

    [JsonPropertyName("fileID")]
    public long FileId { get; set; }

    [JsonPropertyName("required")]
    public bool Required { get; set; } = true;
}

/// <summary>
/// Curseforge 整合包解析器（精简版）。从 SVL.Core.Modpack.CurseforgeModpackParser 下沉，
/// 用 System.Text.Json + System.IO.Compression 替代 SharpZipLib。
/// </summary>
public static class CurseforgeModpackParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>从已解压的 manifest.json 文件解析。</summary>
    public static CurseforgeModpackManifest ParseFromJsonFile(string manifestJsonPath)
    {
        if (!File.Exists(manifestJsonPath))
        {
            throw new FileNotFoundException($"manifest.json 文件不存在: {manifestJsonPath}");
        }

        var jsonContent = File.ReadAllText(manifestJsonPath);
        return JsonSerializer.Deserialize<CurseforgeModpackManifest>(jsonContent, JsonOptions)
            ?? throw new InvalidOperationException("无法解析 manifest.json");
    }

    /// <summary>直接从 zip 文件中读取 manifest.json（用于不经临时解压的快速校验场景）。</summary>
    public static CurseforgeModpackManifest? TryParseFromZip(string zipFilePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipFilePath);
            var manifestEntry = archive.Entries.FirstOrDefault(e =>
                e.Name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase));
            if (manifestEntry == null)
            {
                return null;
            }

            using var stream = manifestEntry.Open();
            using var reader = new StreamReader(stream);
            var jsonContent = reader.ReadToEnd();
            return JsonSerializer.Deserialize<CurseforgeModpackManifest>(jsonContent, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>获取默认的实例名称。</summary>
    public static string GetDefaultInstanceName(CurseforgeModpackManifest manifest)
    {
        return $"{manifest.Name} {manifest.Version}".Trim();
    }
}
