using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SVL.Core.Modpack;

/// <summary>
/// Curseforge 整合包 manifest.json 模型
/// </summary>
public class CurseforgeModpackManifest
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

/// <summary>
/// Curseforge 整合包中的单个文件
/// </summary>
public class CurseforgeModpackFile
{
    [JsonPropertyName("projectID")]
    public long ProjectId { get; set; }

    [JsonPropertyName("fileID")]
    public long FileId { get; set; }

    [JsonPropertyName("required")]
    public bool Required { get; set; } = true;
}

/// <summary>
/// Curseforge 整合包解析器
/// </summary>
public static class CurseforgeModpackParser
{
    /// <summary>
    /// 从 .cfmodpack 文件解析 manifest.json
    /// </summary>
    /// <param name="modpackFilePath">.cfmodpack 文件路径</param>
    /// <returns>解析后的 manifest 对象</returns>
    public static CurseforgeModpackManifest Parse(string modpackFilePath)
    {
        // .cfmodpack 实际上是 .zip 文件
        using var zipFile = new ICSharpCode.SharpZipLib.Zip.ZipFile(modpackFilePath);

        // 查找 manifest.json
        var manifestEntry = zipFile.Cast<ICSharpCode.SharpZipLib.Zip.ZipEntry>()
            .FirstOrDefault(e => e.Name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase));

        if (manifestEntry == null)
        {
            throw new FileNotFoundException("整合包中未找到 manifest.json 文件");
        }

        // 读取 manifest.json 内容
        using var stream = zipFile.GetInputStream(manifestEntry);
        using var reader = new StreamReader(stream);
        var jsonContent = reader.ReadToEnd();

        // 解析 JSON
        var manifest = JsonSerializer.Deserialize<CurseforgeModpackManifest>(jsonContent)
            ?? throw new InvalidOperationException("无法解析 manifest.json");

        return manifest;
    }

    /// <summary>
    /// 从已解压的 manifest.json 文件解析
    /// </summary>
    /// <param name="manifestJsonPath">manifest.json 文件路径</param>
    /// <returns>解析后的 manifest 对象</returns>
    public static CurseforgeModpackManifest ParseFromJsonFile(string manifestJsonPath)
    {
        if (!File.Exists(manifestJsonPath))
        {
            throw new FileNotFoundException($"manifest.json 文件不存在: {manifestJsonPath}");
        }

        var jsonContent = File.ReadAllText(manifestJsonPath);
        var manifest = JsonSerializer.Deserialize<CurseforgeModpackManifest>(jsonContent)
            ?? throw new InvalidOperationException("无法解析 manifest.json");

        return manifest;
    }

    /// <summary>
    /// 提取整合包内容到临时目录
    /// </summary>
    /// <param name="modpackFilePath">.cfmodpack 文件路径</param>
    /// <returns>临时目录路径</returns>
    public static string ExtractToTemp(string modpackFilePath)
    {
        var tempDir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "SVL",
            "modpack",
            Guid.NewGuid().ToString());

        System.IO.Directory.CreateDirectory(tempDir);

        // 解压 .cfmodpack（实际是 .zip）
        using var zipFile = new ICSharpCode.SharpZipLib.Zip.ZipFile(modpackFilePath);

        foreach (ICSharpCode.SharpZipLib.Zip.ZipEntry entry in zipFile)
        {
            if (entry.IsDirectory)
                continue;

            var destinationPath = System.IO.Path.Combine(tempDir, entry.Name.Replace('/', System.IO.Path.DirectorySeparatorChar));
            var destinationDir = System.IO.Path.GetDirectoryName(destinationPath);

            if (!string.IsNullOrEmpty(destinationDir) && !System.IO.Directory.Exists(destinationDir))
            {
                System.IO.Directory.CreateDirectory(destinationDir);
            }

            using var stream = zipFile.GetInputStream(entry);
            using var fileStream = System.IO.File.Create(destinationPath);
            stream.CopyTo(fileStream);
        }

        return tempDir;
    }

    /// <summary>
    /// 检查文件是否是有效的 .cfmodpack 文件
    /// </summary>
    public static bool IsValidModpackFile(string filePath)
    {
        if (!System.IO.File.Exists(filePath))
            return false;

        // 检查文件扩展名
        if (!filePath.EndsWith(".cfmodpack", StringComparison.OrdinalIgnoreCase) &&
            !filePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 检查是否包含 manifest.json
        try
        {
            using var zipFile = new ICSharpCode.SharpZipLib.Zip.ZipFile(filePath);
            return zipFile.Cast<ICSharpCode.SharpZipLib.Zip.ZipEntry>()
                .Any(e => e.Name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 获取默认的实例名称
    /// </summary>
    public static string GetDefaultInstanceName(CurseforgeModpackManifest manifest)
    {
        return $"{manifest.Name} {manifest.Version}";
    }

    /// <summary>
    /// 查找 SMAPI 在文件列表中的索引
    /// </summary>
    /// <param name="manifest">整合包 manifest</param>
    /// <returns>SMAPI 的文件索引，如果不存在返回 -1</returns>
    public static int FindSMAPIIndex(CurseforgeModpackManifest manifest)
    {
        // SMAPI 的 ProjectID 是 898372
        for (int i = 0; i < manifest.Files.Count; i++)
        {
            if (manifest.Files[i].ProjectId == 898372)
            {
                return i;
            }
        }
        return -1;
    }
}
