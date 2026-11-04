using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SVL.Core.Platform.IO;

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
/// zip 使用 System.IO.Compression，Nexus Collection 的 7z 使用平台层内置解压。
/// Nexus Collection 使用 .7z，平台层通过 SharpCompress 内置解压，不要求用户安装 7-Zip。
/// </summary>
public static class ModpackTypeDetector
{
    private static readonly JsonDocumentOptions CompatibleJsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>检测文件是否是支持的整合包文件（.zip / .cfmodpack / .7z）。</summary>
    public static bool IsSupportedFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext is ".zip" or ".cfmodpack" or ".7z" ||
               ArchiveExtractor.IsSevenZip(filePath) ||
               ArchiveExtractor.IsZip(filePath);
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
            var isSevenZip = ArchiveExtractor.IsSevenZip(filePath);
            var isZip = ArchiveExtractor.IsZip(filePath);
            if (ext is not (".zip" or ".cfmodpack" or ".7z") && !isSevenZip && !isZip)
            {
                result.Type = ModpackType.Unknown;
                result.ErrorMessage = $"不支持的文件格式: {ext}";
                return result;
            }

            var tempDir = ExtractArchiveToTemp(filePath);
            if (string.IsNullOrEmpty(tempDir))
            {
                result.Type = ModpackType.Unknown;
                result.ErrorMessage = isSevenZip || ext == ".7z" ? "无法解压 7z 文件" : "无法解压 ZIP 文件";
                return result;
            }

            result.TempExtractPath = tempDir;

            // Nexus Collection（collection.json）
            // 外层打包工具可能也带一个同名但不属于 Collection 的 JSON。
            // 不能只取第一个文件，否则内层有效 collection.json 会被遮蔽。
            var collectionJsonPath = FindValidCollectionManifest(tempDir);
            if (!string.IsNullOrEmpty(collectionJsonPath))
            {
                result.Type = ModpackType.NexusCollection;
                result.ModpackIconPath ??= FindModpackIconInDirectory(tempDir);
                ParseCollectionJson(collectionJsonPath, result);
                return result;
            }

            // SVL 整合包（modpack.json 或嵌套 modpack.zip）— 优先于 Curseforge
            var modpackJsonPath = FindValidSvlManifest(tempDir);
            var nestedModpackArchive = FindFileInDirectory(tempDir, "modpack.zip") ??
                                       FindFileInDirectory(tempDir, "modpack.7z");
            if (!string.IsNullOrEmpty(modpackJsonPath) || !string.IsNullOrEmpty(nestedModpackArchive))
            {
                result.Type = ModpackType.SVL;
                result.ModpackIconPath ??= FindModpackIconInDirectory(tempDir);

                // 嵌套结构（modpack.zip/modpack.7z）：解压到外层临时目录内部，
                // 使检测得到的 Icon 路径与 TempExtractPath 具有相同生命周期；
                // 用户确认导入后清理外层目录即可一并清理内层内容。
                if (string.IsNullOrEmpty(modpackJsonPath) && !string.IsNullOrEmpty(nestedModpackArchive))
                {
                    var innerTempDir = Path.Combine(tempDir, "_svl-inner");
                    if (ExtractArchiveToDirectory(nestedModpackArchive, innerTempDir))
                    {
                        modpackJsonPath = FindValidSvlManifest(innerTempDir);
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
            // manifest.json 既可能是 CurseForge 整合包清单，也可能是普通 Mod
            // 或发行包元数据。只取符合 CurseForge 结构的候选项。
            var manifestJsonPath = FindValidCurseforgeManifest(tempDir);
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
                    global::SVL.Core.Platform.Diagnostics.RuntimeDebugLog.WriteLine($"[ModpackTypeDetector] 解析 manifest.json 失败: {ex.Message}");
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
            global::SVL.Core.Platform.Diagnostics.RuntimeDebugLog.WriteLine($"[ModpackTypeDetector] 检测整合包类型失败: {filePath} - {ex}");
            return result;
        }
    }

    private static void ParseCollectionJson(string collectionJsonPath, ModpackDetectionResult result)
    {
        try
        {
            using var reader = new StreamReader(
                collectionJsonPath,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            var jsonContent = reader.ReadToEnd();
            using var doc = JsonDocument.Parse(jsonContent, CompatibleJsonOptions);
            var root = doc.RootElement;

            if (TryGetPropertyIgnoreCase(root, "info", out var info))
            {
                if (TryGetPropertyIgnoreCase(info, "name", out var name))
                {
                    result.ModpackName = GetJsonString(name);
                }

                if (TryGetPropertyIgnoreCase(info, "author", out var author))
                {
                    result.ModpackAuthor = GetJsonString(author);
                }

                if (TryGetPropertyIgnoreCase(info, "description", out var desc))
                {
                    result.ModpackDescription = GetJsonString(desc);
                }

                if (TryGetPropertyIgnoreCase(info, "gameVersions", out var versions))
                {
                    result.ModpackVersion = GetFirstJsonString(versions);
                }
            }

            if (TryGetPropertyIgnoreCase(root, "mods", out var mods) && mods.ValueKind == JsonValueKind.Array)
            {
                result.ModCount = mods.GetArrayLength();
            }
        }
        catch (Exception ex)
        {
            global::SVL.Core.Platform.Diagnostics.RuntimeDebugLog.WriteLine($"[ModpackTypeDetector] 解析 collection.json 失败: {ex.Message}");
        }
    }

    private static void ParseModpackJson(string modpackJsonPath, ModpackDetectionResult result)
    {
        try
        {
            // 旧 WPF 导出包可能使用 UTF-16；StreamReader 自动识别 BOM，避免
            // 类型检测阶段先失败，后续安装器根本拿不到 manifest 信息。
            using var reader = new StreamReader(
                modpackJsonPath,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            var jsonContent = reader.ReadToEnd();
            using var doc = JsonDocument.Parse(jsonContent, CompatibleJsonOptions);
            var root = doc.RootElement;

            if (TryGetPropertyIgnoreCase(root, "name", out var name))
            {
                result.ModpackName = GetJsonString(name);
            }

            if (TryGetPropertyIgnoreCase(root, "version", out var ver))
            {
                result.ModpackVersion = GetJsonString(ver);
            }

            if (TryGetPropertyIgnoreCase(root, "author", out var author))
            {
                result.ModpackAuthor = GetJsonString(author);
            }

            if (TryGetPropertyIgnoreCase(root, "description", out var desc))
            {
                result.ModpackDescription = GetJsonString(desc);
            }

            if (TryGetPropertyIgnoreCase(root, "mods", out var modsList) && modsList.ValueKind == JsonValueKind.Array)
            {
                result.ModCount = modsList.GetArrayLength();
            }
        }
        catch (Exception ex)
        {
            global::SVL.Core.Platform.Diagnostics.RuntimeDebugLog.WriteLine($"[ModpackTypeDetector] 解析 modpack.json 失败: {ex.Message}");
            result.ModpackName ??= Path.GetFileNameWithoutExtension(result.FilePath);
        }
    }

    private static string CreateTempDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SVL", "modpack_detect", Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    /// <summary>解压 ZIP/CFModpack/7z 到临时目录。</summary>
    private static string? ExtractArchiveToTemp(string archivePath)
    {
        var tempDir = CreateTempDirectory();
        return ExtractArchiveToDirectory(archivePath, tempDir) ? tempDir : null;
    }

    /// <summary>将 ZIP/CFModpack/7z 解压到指定临时目录，并在失败时清理该目录。</summary>
    private static bool ExtractArchiveToDirectory(string archivePath, string tempDir)
    {
        try
        {
            Directory.CreateDirectory(tempDir);
            if (ArchiveExtractor.IsSevenZip(archivePath))
            {
                ArchiveExtractor.ExtractSevenZipToDirectory(archivePath, tempDir);
                return true;
            }

            using var archive = ZipFile.OpenRead(archivePath);
            var destinationRoot = Path.GetFullPath(tempDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    // 目录项
                    continue;
                }

                var destinationPath = Path.Combine(tempDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                destinationPath = Path.GetFullPath(destinationPath);
                if (!destinationPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"ZIP 归档包含越界路径: {entry.FullName}");
                }

                var destinationDir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(destinationDir) && !Directory.Exists(destinationDir))
                {
                    Directory.CreateDirectory(destinationDir);
                }

                using var stream = entry.Open();
                using var fileStream = File.Create(destinationPath);
                stream.CopyTo(fileStream);
            }

            return true;
        }
        catch (Exception ex)
        {
            global::SVL.Core.Platform.Diagnostics.RuntimeDebugLog.WriteLine($"[ModpackTypeDetector] 解压归档失败: {archivePath} - {ex}");
            CleanupTempDirectory(tempDir);
            return false;
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
                var subFile = FindFileByNameIgnoreCase(subDirs[0], fileName, SearchOption.TopDirectoryOnly);
                if (!string.IsNullOrWhiteSpace(subFile))
                {
                    return subFile;
                }
            }

            return FindFileByNameIgnoreCase(directory, fileName, SearchOption.AllDirectories);
        }
        catch
        {
            return null;
        }
    }

    private static string? FindFileByNameIgnoreCase(
        string directory,
        string fileName,
        SearchOption searchOption)
    {
        return Directory.EnumerateFiles(directory, "*", searchOption)
            .FirstOrDefault(path => string.Equals(
                Path.GetFileName(path),
                fileName,
                StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindValidCollectionManifest(string directory)
    {
        return FindMatchingFile(directory, "collection.json", LooksLikeCollectionManifest);
    }

    private static string? FindValidSvlManifest(string directory)
    {
        return FindMatchingFile(directory, "modpack.json", LooksLikeSvlManifest);
    }

    private static string? FindValidCurseforgeManifest(string directory)
    {
        return FindMatchingFile(directory, "manifest.json", LooksLikeCurseforgeManifest);
    }

    private static string? FindMatchingFile(
        string directory,
        string fileName,
        Func<string, bool> predicate)
    {
        try
        {
            var candidates = new List<string>();
            var direct = Path.Combine(directory, fileName);
            if (File.Exists(direct))
            {
                candidates.Add(direct);
            }

            candidates.AddRange(
                Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                    .Where(path => !string.Equals(path, direct, StringComparison.OrdinalIgnoreCase))
                    .Where(path => string.Equals(
                        Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => path.Count(character =>
                        character == Path.DirectorySeparatorChar ||
                        character == Path.AltDirectorySeparatorChar)));

            return candidates.FirstOrDefault(predicate);
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeCollectionManifest(string path)
    {
        try
        {
            using var reader = new StreamReader(
                path,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            using var document = JsonDocument.Parse(reader.ReadToEnd(), CompatibleJsonOptions);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   TryGetPropertyIgnoreCase(root, "mods", out var mods) &&
                   mods.ValueKind == JsonValueKind.Array;
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeSvlManifest(string path)
    {
        try
        {
            using var reader = new StreamReader(
                path,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            using var document = JsonDocument.Parse(reader.ReadToEnd(), CompatibleJsonOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // SVL 导出包至少应包含名称、Mod 列表或 SMAPI 版本字段。
            // 这样可排除 { formatVersion, files } 一类外层打包元数据。
            return TryGetPropertyIgnoreCase(root, "name", out var name) &&
                       name.ValueKind == JsonValueKind.String &&
                       !string.IsNullOrWhiteSpace(name.GetString()) ||
                   TryGetPropertyIgnoreCase(root, "title", out var title) &&
                       title.ValueKind == JsonValueKind.String &&
                       !string.IsNullOrWhiteSpace(title.GetString()) ||
                   TryGetPropertyIgnoreCase(root, "mods", out var mods) &&
                       mods.ValueKind == JsonValueKind.Array ||
                   TryGetPropertyIgnoreCase(root, "smapi_version", out _) ||
                   TryGetPropertyIgnoreCase(root, "smapiVersion", out _);
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeCurseforgeManifest(string manifestJsonPath)
    {
        try
        {
            // CurseForge 导出包通常是 UTF-8，但 Windows 工具也可能写成 UTF-16。
            // 这里是类型检测的第一关，不能让编码问题在进入安装器前把整合包判成未知。
            using var reader = new StreamReader(
                manifestJsonPath,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            using var document = JsonDocument.Parse(reader.ReadToEnd(), CompatibleJsonOptions);
            var root = document.RootElement;
            return TryGetPropertyIgnoreCase(root, "manifestVersion", out var manifestVersion)
                   && TryGetInt32(manifestVersion, out _)
                   && TryGetPropertyIgnoreCase(root, "files", out var files)
                   && (files.ValueKind == JsonValueKind.Array || files.ValueKind == JsonValueKind.Null);
        }
        catch
        {
            return false;
        }
    }

    private static string? GetFirstJsonString(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var text = GetJsonString(item);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            return null;
        }

        return GetJsonString(value);
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string propertyName,
        out JsonElement property)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var candidate in element.EnumerateObject())
            {
                if (string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    property = candidate.Value;
                    return true;
                }
            }
        }

        property = default;
        return false;
    }

    private static string? GetJsonString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static bool TryGetInt32(JsonElement value, out int number)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number))
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out number))
        {
            return true;
        }

        number = 0;
        return false;
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
            global::SVL.Core.Platform.Diagnostics.RuntimeDebugLog.WriteLine($"[ModpackTypeDetector] 清理临时目录失败: {tempDir} - {ex.Message}");
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

                var any = FindFileByNameIgnoreCase(directory, name, SearchOption.AllDirectories);
                if (!string.IsNullOrEmpty(any))
                {
                    return any;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            global::SVL.Core.Platform.Diagnostics.RuntimeDebugLog.WriteLine($"[ModpackTypeDetector] 查找整合包图标失败: {ex.Message}");
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

            var exact = candidates.FirstOrDefault(File.Exists);
            if (!string.IsNullOrWhiteSpace(exact))
            {
                return exact;
            }

            foreach (var candidate in candidates)
            {
                var directory = Path.GetDirectoryName(candidate);
                var fileName = Path.GetFileName(candidate);
                var match = string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName)
                    ? null
                    : FindFileByNameIgnoreCase(directory, fileName, SearchOption.TopDirectoryOnly);
                if (!string.IsNullOrWhiteSpace(match))
                {
                    return match;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

}
