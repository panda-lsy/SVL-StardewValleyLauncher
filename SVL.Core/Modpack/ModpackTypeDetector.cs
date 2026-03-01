using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using ICSharpCode.SharpZipLib.Zip;
using SVL.Core.IO;
using SVL.Core.Logging;

namespace SVL.Core.Modpack;

/// <summary>
/// 整合包类型
/// </summary>
public enum ModpackType
{
    /// <summary>
    /// Curseforge 整合包（包含 manifest.json）
    /// </summary>
    Curseforge,

    /// <summary>
    /// Nexus Collection（包含 collection.json）
    /// </summary>
    NexusCollection,

    /// <summary>
    /// SVL 整合包（包含 modpack.json）
    /// </summary>
    SVL,

    /// <summary>
    /// 未知类型
    /// </summary>
    Unknown
}

/// <summary>
/// 整合包类型检测结果
/// </summary>
public class ModpackDetectionResult
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

    /// <summary>
    /// Curseforge manifest（如果是 Curseforge 类型）
    /// </summary>
    public CurseforgeModpackManifest? CurseforgeManifest { get; set; }
}

/// <summary>
/// 整合包类型检测器
/// </summary>
public static class ModpackTypeDetector
{
    /// <summary>
    /// 检测文件是否是支持的整合包文件
    /// </summary>
    public static bool IsSupportedFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return false;

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext == ".zip" || ext == ".7z" || ext == ".cfmodpack";
    }

    /// <summary>
    /// 检测整合包类型
    /// </summary>
    public static ModpackDetectionResult Detect(string filePath)
    {
        var result = new ModpackDetectionResult
        {
            FilePath = filePath
        };

        try
        {
            if (!File.Exists(filePath))
            {
                result.Type = ModpackType.Unknown;
                result.ErrorMessage = "文件不存在";
                return result;
            }

            var ext = Path.GetExtension(filePath).ToLowerInvariant();

            // 根据扩展名选择解压方式
            string tempDir;
            if (ext == ".7z")
            {
                // 7z 文件，使用 SevenZipService
                tempDir = CreateTempDirectory();
                if (!SevenZipService.Extract(filePath, tempDir))
                {
                    result.Type = ModpackType.Unknown;
                    result.ErrorMessage = "无法解压 7z 文件";
                    CleanupTempDirectory(tempDir);
                    return result;
                }
            }
            else if (ext == ".zip" || ext == ".cfmodpack")
            {
                // ZIP 文件，使用 SharpZipLib
                tempDir = ExtractZipToTemp(filePath);
                if (string.IsNullOrEmpty(tempDir))
                {
                    result.Type = ModpackType.Unknown;
                    result.ErrorMessage = "无法解压 ZIP 文件";
                    return result;
                }
            }
            else
            {
                result.Type = ModpackType.Unknown;
                result.ErrorMessage = $"不支持的文件格式: {ext}";
                return result;
            }

            result.TempExtractPath = tempDir;

            // 检查是否包含 collection.json（Nexus Collection）
            var collectionJsonPath = FindFileInDirectory(tempDir, "collection.json");
            if (!string.IsNullOrEmpty(collectionJsonPath))
            {
                result.Type = ModpackType.NexusCollection;

                // 解析 collection.json 获取整合包信息
                try
                {
                    var jsonContent = File.ReadAllText(collectionJsonPath);
                    using var doc = JsonDocument.Parse(jsonContent);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("info", out var info))
                    {
                        if (info.TryGetProperty("name", out var name))
                            result.ModpackName = name.GetString();
                        if (info.TryGetProperty("author", out var author))
                            result.ModpackAuthor = author.GetString();
                        if (info.TryGetProperty("description", out var desc))
                            result.ModpackDescription = desc.GetString();
                        if (info.TryGetProperty("gameVersions", out var versions) && versions.GetArrayLength() > 0)
                            result.ModpackVersion = versions[0].GetString();
                    }

                    if (root.TryGetProperty("mods", out var mods) && mods.ValueKind == JsonValueKind.Array)
                    {
                        result.ModCount = mods.GetArrayLength();
                    }

                    Log.Info($"[ModpackTypeDetector] 检测到 Nexus Collection: {result.ModpackName ?? filePath}, Mod 数量: {result.ModCount}");
                }
                catch (Exception ex)
                {
                    Log.Warn($"[ModpackTypeDetector] 解析 collection.json 失败: {ex.Message}");
                    Log.Info($"[ModpackTypeDetector] 检测到 Nexus Collection: {filePath}");
                }

                return result;
            }

            // 检查是否为 SVL 整合包（包含 modpack.json）— 优先于 Curseforge 检测
            // 因为从 Curseforge 安装的整合包导出为 SVL 格式后可能同时包含 manifest.json 和 modpack.json
            // 支持两种结构：
            //   1. 直接包含 modpack.json（纯整合包）
            //   2. 包含 SVL.exe + modpack.zip（捆绑启动器的整合包）
            var modpackJsonPath = FindFileInDirectory(tempDir, "modpack.json");
            var nestedModpackZip = FindFileInDirectory(tempDir, "modpack.zip");

            if (!string.IsNullOrEmpty(modpackJsonPath) || !string.IsNullOrEmpty(nestedModpackZip))
            {
                result.Type = ModpackType.SVL;

                // 如果是嵌套结构（有 modpack.zip），先解压内层 zip 以获取 modpack.json
                if (string.IsNullOrEmpty(modpackJsonPath) && !string.IsNullOrEmpty(nestedModpackZip))
                {
                    var innerTempDir = ExtractZipToTemp(nestedModpackZip);
                    if (!string.IsNullOrEmpty(innerTempDir))
                    {
                        modpackJsonPath = FindFileInDirectory(innerTempDir, "modpack.json");
                        // 保存内层解压路径到 TempExtractPath，供后续安装使用
                        // 我们保留外层 tempDir 不变，因为它包含完整结构
                    }
                }

                // 解析 modpack.json 获取整合包信息
                if (!string.IsNullOrEmpty(modpackJsonPath))
                {
                    try
                    {
                        var jsonContent = File.ReadAllText(modpackJsonPath);
                        using var doc = JsonDocument.Parse(jsonContent);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("name", out var name))
                            result.ModpackName = name.GetString();
                        if (root.TryGetProperty("version", out var ver))
                            result.ModpackVersion = ver.GetString();
                        if (root.TryGetProperty("author", out var author))
                            result.ModpackAuthor = author.GetString();
                        if (root.TryGetProperty("description", out var desc))
                            result.ModpackDescription = desc.GetString();
                        if (root.TryGetProperty("mods", out var modsList) && modsList.ValueKind == JsonValueKind.Array)
                            result.ModCount = modsList.GetArrayLength();

                        Log.Info($"[ModpackTypeDetector] 检测到 SVL 整合包: {result.ModpackName ?? filePath}, Mod 数量: {result.ModCount}");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[ModpackTypeDetector] 解析 modpack.json 失败: {ex.Message}");
                        result.ModpackName = Path.GetFileNameWithoutExtension(filePath);
                    }
                }
                else
                {
                    result.ModpackName = Path.GetFileNameWithoutExtension(filePath);
                    Log.Info($"[ModpackTypeDetector] 检测到 SVL 整合包（嵌套结构）: {filePath}");
                }

                return result;
            }

            // 检查是否包含 manifest.json（Curseforge）
            var manifestJsonPath = FindFileInDirectory(tempDir, "manifest.json");
            if (!string.IsNullOrEmpty(manifestJsonPath))
            {
                result.Type = ModpackType.Curseforge;

                // 解析 manifest
                try
                {
                    result.CurseforgeManifest = CurseforgeModpackParser.Parse(filePath);
                    result.ModpackName = result.CurseforgeManifest.Name;
                    result.ModpackVersion = result.CurseforgeManifest.Version;
                    result.ModpackAuthor = result.CurseforgeManifest.Author;
                    result.ModpackDescription = result.CurseforgeManifest.Description;
                    result.ModCount = result.CurseforgeManifest.Files.Count;
                }
                catch (Exception ex)
                {
                    Log.Warn($"[ModpackTypeDetector] 解析 manifest.json 失败: {ex.Message}");
                }

                Log.Info($"[ModpackTypeDetector] 检测到 Curseforge 整合包: {filePath}");
                return result;
            }

            // 未知类型
            result.Type = ModpackType.Unknown;
            result.ErrorMessage = "未找到 collection.json、manifest.json 或 modpack.json，无法识别整合包类型";
            CleanupTempDirectory(tempDir);
            return result;
        }
        catch (Exception ex)
        {
            result.Type = ModpackType.Unknown;
            result.ErrorMessage = $"检测失败: {ex.Message}";
            Log.Error(ex, $"[ModpackTypeDetector] 检测整合包类型失败: {filePath}");
            return result;
        }
    }

    /// <summary>
    /// 创建临时目录
    /// </summary>
    private static string CreateTempDirectory()
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            "SVL",
            "modpack_detect",
            Guid.NewGuid().ToString());

        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    /// <summary>
    /// 解压 ZIP 文件到临时目录
    /// </summary>
    private static string? ExtractZipToTemp(string zipPath)
    {
        try
        {
            var tempDir = CreateTempDirectory();

            using var zipFile = new ZipFile(zipPath);

            foreach (ZipEntry entry in zipFile)
            {
                if (entry.IsDirectory)
                    continue;

                var destinationPath = Path.Combine(tempDir, entry.Name.Replace('/', Path.DirectorySeparatorChar));
                var destinationDir = Path.GetDirectoryName(destinationPath);

                if (!string.IsNullOrEmpty(destinationDir) && !Directory.Exists(destinationDir))
                {
                    Directory.CreateDirectory(destinationDir);
                }

                using var stream = zipFile.GetInputStream(entry);
                using var fileStream = File.Create(destinationPath);
                stream.CopyTo(fileStream);
            }

            return tempDir;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[ModpackTypeDetector] 解压 ZIP 失败: {zipPath}");
            return null;
        }
    }

    /// <summary>
    /// 在目录中查找指定文件
    /// </summary>
    private static string? FindFileInDirectory(string directory, string fileName)
    {
        try
        {
            // 首先检查根目录
            var rootFile = Path.Combine(directory, fileName);
            if (File.Exists(rootFile))
                return rootFile;

            // 检查子目录（处理压缩包内有一层目录的情况）
            var subDirs = Directory.GetDirectories(directory);
            if (subDirs.Length == 1)
            {
                var subFile = Path.Combine(subDirs[0], fileName);
                if (File.Exists(subFile))
                    return subFile;
            }

            // 递归搜索
            var files = Directory.GetFiles(directory, fileName, SearchOption.AllDirectories);
            return files.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 清理临时目录
    /// </summary>
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
            Log.Warn($"[ModpackTypeDetector] 清理临时目录失败: {tempDir}", ex);
        }
    }
}
