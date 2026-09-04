using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace SVL.Core.Platform.Modpack;

/// <summary>整合包类型（从 SVL.Core.Modpack 下沉，供新架构 Avalonia 层使用）。</summary>
public enum ModpackType
{
    Curseforge,
    NexusCollection,
    SVL,
    Unknown
}

/// <summary>整合包类型检测结果。</summary>
public sealed class ModpackDetectionResult
{
    public ModpackType Type { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string TempExtractPath { get; set; } = string.Empty;
    public string? ModpackName { get; set; }
    public string? ModpackVersion { get; set; }
    public string? ModpackAuthor { get; set; }
    public string? ModpackDescription { get; set; }
    public int ModCount { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ModpackIconPath { get; set; }

    /// <summary>Curseforge manifest（如果是 Curseforge 类型）。</summary>
    public CurseforgeModpackManifest? CurseforgeManifest { get; set; }
}

/// <summary>
/// 整合包类型检测器。从 SVL.Core.Modpack.ModpackTypeDetector 下沉，
/// 用 System.IO.Compression.ZipArchive（.NET 内置）替代 SharpZipLib，保持 Core.Platform 零外部依赖。
/// 注意：不支持 .7z（旧架构用 SevenZipService，新架构暂不引入该依赖）。
/// </summary>
public static class ModpackTypeDetector
{
    /// <summary>检测文件是否是支持的整合包文件（.zip / .cfmodpack）。</summary>
    public static bool IsSupportedFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext == ".zip" || ext == ".cfmodpack";
    }

    /// <summary>检测整合包类型并解析元数据。</summary>
    public static ModpackDetectionResult Detect(string filePath)
    {
        var result = new ModpackDetectionResult { FilePath = filePath };
        result.ModpackIconPath = FindSidecarIconPath(filePath);

        try
        {
            if (!File.Exists(filePath))
            {
                result.Type = ModpackType.Unknown;
                result.ErrorMessage = "文件不存在";
                return result;
            }

            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext != ".zip" && ext != ".cfmodpack")
            {
                result.Type = ModpackType.Unknown;
                result.ErrorMessage = $"不支持的文件格式: {ext}（新架构暂不支持 .7z）";
                return result;
            }

            var tempDir = ExtractZipToTemp(filePath);
            if (string.IsNullOrEmpty(tempDir))
            {
                result.Type = ModpackType.Unknown;
                result.ErrorMessage = "无法解压 ZIP 文件";
                return result;
            }

            result.TempExtractPath = tempDir;

            // Nexus Collection（collection.json）
            var collectionJsonPath = FindFileInDirectory(tempDir, "collection.json");
            if (!string.IsNullOrEmpty(collectionJsonPath))
            {
                result.Type = ModpackType.NexusCollection;
                result.ModpackIconPath ??= FindModpackIconInDirectory(tempDir);
                ParseCollectionJson(collectionJsonPath, result);
                return result;
            }

            // SVL 整合包（modpack.json 或嵌套 modpack.zip）— 优先于 Curseforge
            var modpackJsonPath = FindFileInDirectory(tempDir, "modpack.json");
            var nestedModpackZip = FindFileInDirectory(tempDir, "modpack.zip");
            if (!string.IsNullOrEmpty(modpackJsonPath) || !string.IsNullOrEmpty(nestedModpackZip))
            {
                result.Type = ModpackType.SVL;
                result.ModpackIconPath ??= FindModpackIconInDirectory(tempDir);

                // 嵌套结构（modpack.zip）：先解压内层获取 modpack.json
                if (string.IsNullOrEmpty(modpackJsonPath) && !string.IsNullOrEmpty(nestedModpackZip))
                {
                    var innerTempDir = ExtractZipToTemp(nestedModpackZip);
                    if (!string.IsNullOrEmpty(innerTempDir))
                    {
                        modpackJsonPath = FindFileInDirectory(innerTempDir, "modpack.json");
                        result.ModpackIconPath ??= FindModpackIconInDirectory(innerTempDir);
                    }
                }

                if (!string.IsNullOrEmpty(modpackJsonPath))
                {
                    ParseModpackJson(modpackJsonPath, result);
                }
                else
                {
                    result.ModpackName ??= Path.GetFileNameWithoutExtension(filePath);
                }

                return result;
            }

            // Curseforge（manifest.json）
            var manifestJsonPath = FindFileInDirectory(tempDir, "manifest.json");
            if (!string.IsNullOrEmpty(manifestJsonPath) && LooksLikeCurseforgeManifest(manifestJsonPath))
            {
                result.Type = ModpackType.Curseforge;
                result.ModpackIconPath ??= FindModpackIconInDirectory(tempDir);
                try
                {
                    result.CurseforgeManifest = CurseforgeModpackParser.ParseFromJsonFile(manifestJsonPath);
                    result.ModpackName = result.CurseforgeManifest.Name;
                    result.ModpackVersion = result.CurseforgeManifest.Version;
                    result.ModpackAuthor = result.CurseforgeManifest.Author;
                    result.ModpackDescription = result.CurseforgeManifest.Description;
                    result.ModCount = result.CurseforgeManifest.Files.Count;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ModpackTypeDetector] 解析 manifest.json 失败: {ex.Message}");
                }

                return result;
            }

            result.Type = ModpackType.Unknown;
            result.ErrorMessage = "未找到 collection.json、manifest.json 或 modpack.json，无法识别整合包类型";
            CleanupTempDirectory(tempDir);
            return result;
        }
        catch (Exception ex)
        {
            result.Type = ModpackType.Unknown;
            result.ErrorMessage = $"检测失败: {ex.Message}";
            Debug.WriteLine($"[ModpackTypeDetector] 检测整合包类型失败: {filePath} - {ex}");
            return result;
        }
    }

    private static void ParseCollectionJson(string collectionJsonPath, ModpackDetectionResult result)
    {
        try
        {
            var jsonContent = File.ReadAllText(collectionJsonPath);
            using var doc = JsonDocument.Parse(jsonContent);
            var root = doc.RootElement;

            if (root.TryGetProperty("info", out var info))
            {
                if (info.TryGetProperty("name", out var name))
                {
                    result.ModpackName = name.GetString();
                }

                if (info.TryGetProperty("author", out var author))
                {
                    result.ModpackAuthor = author.GetString();
                }

                if (info.TryGetProperty("description", out var desc))
                {
                    result.ModpackDescription = desc.GetString();
                }

                if (info.TryGetProperty("gameVersions", out var versions) && versions.GetArrayLength() > 0)
                {
                    result.ModpackVersion = versions[0].GetString();
                }
            }

            if (root.TryGetProperty("mods", out var mods) && mods.ValueKind == JsonValueKind.Array)
            {
                result.ModCount = mods.GetArrayLength();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ModpackTypeDetector] 解析 collection.json 失败: {ex.Message}");
        }
    }

    private static void ParseModpackJson(string modpackJsonPath, ModpackDetectionResult result)
    {
        try
        {
            var jsonContent = File.ReadAllText(modpackJsonPath);
            using var doc = JsonDocument.Parse(jsonContent);
            var root = doc.RootElement;

            if (root.TryGetProperty("name", out var name))
            {
                result.ModpackName = name.GetString();
            }

            if (root.TryGetProperty("version", out var ver))
            {
                result.ModpackVersion = ver.GetString();
            }

            if (root.TryGetProperty("author", out var author))
            {
                result.ModpackAuthor = author.GetString();
            }

            if (root.TryGetProperty("description", out var desc))
            {
                result.ModpackDescription = desc.GetString();
            }

            if (root.TryGetProperty("mods", out var modsList) && modsList.ValueKind == JsonValueKind.Array)
            {
                result.ModCount = modsList.GetArrayLength();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ModpackTypeDetector] 解析 modpack.json 失败: {ex.Message}");
            result.ModpackName ??= Path.GetFileNameWithoutExtension(result.FilePath);
        }
    }

    private static string CreateTempDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SVL", "modpack_detect", Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    /// <summary>用 ZipArchive 解压 ZIP 到临时目录（替代旧架构的 SharpZipLib）。</summary>
    private static string? ExtractZipToTemp(string zipPath)
    {
        try
        {
            var tempDir = CreateTempDirectory();
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    // 目录项
                    continue;
                }

                var destinationPath = Path.Combine(tempDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                var destinationDir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(destinationDir) && !Directory.Exists(destinationDir))
                {
                    Directory.CreateDirectory(destinationDir);
                }

                using var stream = entry.Open();
                using var fileStream = File.Create(destinationPath);
                stream.CopyTo(fileStream);
            }

            return tempDir;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ModpackTypeDetector] 解压 ZIP 失败: {zipPath} - {ex}");
            return null;
        }
    }

    private static string? FindFileInDirectory(string directory, string fileName)
    {
        try
        {
            var rootFile = Path.Combine(directory, fileName);
            if (File.Exists(rootFile))
            {
                return rootFile;
            }

            var subDirs = Directory.GetDirectories(directory);
            if (subDirs.Length == 1)
            {
                var subFile = Path.Combine(subDirs[0], fileName);
                if (File.Exists(subFile))
                {
                    return subFile;
                }
            }

            return Directory.GetFiles(directory, fileName, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeCurseforgeManifest(string manifestJsonPath)
    {
        try
        {
            using var stream = File.OpenRead(manifestJsonPath);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            return root.TryGetProperty("manifestVersion", out var manifestVersion)
                   && manifestVersion.ValueKind == JsonValueKind.Number
                   && root.TryGetProperty("files", out var files)
                   && files.ValueKind == JsonValueKind.Array;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>清理临时解压目录。</summary>
    public static void CleanupTempDirectory(string tempDir)
    {
        try
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ModpackTypeDetector] 清理临时目录失败: {tempDir} - {ex.Message}");
        }
    }

    private static readonly string[] IconCandidates =
    {
        "modpack-icon.png", "modpack-icon.jpg", "modpack-icon.jpeg", "modpack-icon.webp", "modpack-icon.gif",
        "pack-icon.png", "pack-icon.jpg", "pack-icon.jpeg", "pack-icon.webp", "pack-icon.gif",
        "icon.png", "icon.jpg", "icon.jpeg", "icon.webp", "icon.gif",
        "logo.png", "logo.jpg", "logo.jpeg", "logo.webp", "logo.gif",
        "thumbnail.png", "thumbnail.jpg", "thumbnail.jpeg", "thumbnail.webp", "thumbnail.gif",
        "cover.png", "cover.jpg", "cover.jpeg", "cover.webp", "cover.gif"
    };

    private static string? FindModpackIconInDirectory(string directory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return null;
            }

            foreach (var name in IconCandidates)
            {
                var root = Path.Combine(directory, name);
                if (File.Exists(root))
                {
                    return root;
                }

                var any = Directory.GetFiles(directory, name, SearchOption.AllDirectories).FirstOrDefault();
                if (!string.IsNullOrEmpty(any))
                {
                    return any;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ModpackTypeDetector] 查找整合包图标失败: {ex.Message}");
            return null;
        }
    }

    private static string? FindSidecarIconPath(string modpackFilePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(modpackFilePath) || !File.Exists(modpackFilePath))
            {
                return null;
            }

            var dir = Path.GetDirectoryName(modpackFilePath);
            var baseName = Path.GetFileNameWithoutExtension(modpackFilePath);
            if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(baseName))
            {
                return null;
            }

            var candidates = new[]
            {
                Path.Combine(dir, $"{baseName}.png"),
                Path.Combine(dir, $"{baseName}.jpg"),
                Path.Combine(dir, $"{baseName}.jpeg"),
                Path.Combine(dir, $"{baseName}.webp"),
                Path.Combine(dir, $"{baseName}.icon.png"),
                Path.Combine(dir, $"{baseName}.icon.jpg"),
                Path.Combine(dir, $"{baseName}.icon.jpeg"),
                Path.Combine(dir, $"{baseName}.icon.webp")
            };

            return candidates.FirstOrDefault(File.Exists);
        }
        catch
        {
            return null;
        }
    }
}
