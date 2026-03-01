using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SVL.Core.Logging;

namespace SVL.Core.Stardew.Instance;

/// <summary>
/// 游戏文件列表服务
/// 负责扫描、保存和加载游戏本体文件列表
/// </summary>
public static class GameFilesListService
{
    private static readonly string FilesListCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SVL",
        "game_files_cache");

    /// <summary>
    /// 游戏文件列表配置
    /// </summary>
    public class GameFilesList
    {
        public string GamePath { get; set; } = string.Empty;
        public string GameVersion { get; set; } = string.Empty;
        public DateTime ScanTime { get; set; }
        public List<string> Files { get; set; } = new();
        public List<string> DirectoriesToCopy { get; set; } = new(); // 需要复制的目录（如 steam_settings）
    }

    /// <summary>
    /// 扫描游戏本体文件
    /// </summary>
    /// <param name="gamePath">游戏本体路径</param>
    /// <returns>文件列表</returns>
    public static GameFilesList ScanGameFiles(string gamePath)
    {
        try
        {
            Log.Info($"[GameFilesList] 开始扫描游戏文件: {gamePath}");

            if (!Directory.Exists(gamePath))
            {
                throw new DirectoryNotFoundException($"游戏路径不存在: {gamePath}");
            }

            // 获取游戏版本
            var gameVersion = GamePathService.GetGameVersion(gamePath);

            var result = new GameFilesList
            {
                GamePath = gamePath,
                GameVersion = gameVersion,
                ScanTime = DateTime.Now
            };

            // *** SMAPI 文件黑名单：这些文件不应从 Base 复制 ***
            // SMAPI 文件应由 SMAPI 安装程序从安装包中提取
            var smapiFilesBlacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "StardewModdingAPI.exe",
                "StardewModdingAPI.dll",
                "StardewModdingAPI.deps.json",
                "StardewModdingAPI.config.json",
                "Mono.Cecil.dll",
                "Mono.Cecil.Rocks.dll",
                "MonoMod.RuntimeDetour.dll",
                "MonoMod.Utils.dll",
                "install.dat",
                "install on Linux.sh",
                "install on macOS.command",
                "install on Windows.bat",
                "README.txt",
                "Linux",
                "macOS",
                "mcs"
            };

            // 扫描所有文件（不包括文件夹），但排除 SMAPI 文件
            var allFiles = Directory.GetFiles(gamePath)
                .Select(f => Path.GetFileName(f))
                .Where(f => !string.IsNullOrEmpty(f))
                .Where(f => !smapiFilesBlacklist.Contains(f!))  // 排除 SMAPI 文件
                .OrderBy(f => f)
                .ToList()!;

            result.Files = allFiles;

            // 定义需要复制的目录（这些目录需要完整复制到版本目录）
            var directoriesToCopy = new List<string>();
            var steamSettingsPath = Path.Combine(gamePath, "steam_settings");
            if (Directory.Exists(steamSettingsPath))
            {
                directoriesToCopy.Add("steam_settings");
            }

            result.DirectoriesToCopy = directoriesToCopy;

            Log.Info($"[GameFilesList] 扫描完成：发现 {allFiles.Count} 个文件，{directoriesToCopy.Count} 个目录");
            Log.Info($"[GameFilesList] 游戏版本: {gameVersion}");

            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[GameFilesList] 扫描游戏文件失败");
            throw;
        }
    }

    /// <summary>
    /// 保存文件列表到缓存
    /// </summary>
    /// <param name="gamePath">游戏路径</param>
    /// <param name="filesList">文件列表</param>
    public static void SaveFilesList(string gamePath, GameFilesList filesList)
    {
        try
        {
            // 创建缓存目录
            if (!Directory.Exists(FilesListCacheDir))
            {
                Directory.CreateDirectory(FilesListCacheDir);
            }

            // 生成缓存文件名（使用游戏路径的哈希）
            using var md5 = System.Security.Cryptography.MD5.Create();
            var hashBytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(gamePath));
            var pathHash = BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 16);
            var cacheFilePath = Path.Combine(FilesListCacheDir, $"files_{pathHash}.json");

            // 序列化为 JSON
            var json = JsonSerializer.Serialize(filesList, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(cacheFilePath, json);

            Log.Info($"[GameFilesList] 文件列表已保存到缓存: {cacheFilePath}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[GameFilesList] 保存文件列表失败");
            throw;
        }
    }

    /// <summary>
    /// 从缓存加载文件列表
    /// </summary>
    /// <param name="gamePath">游戏路径</param>
    /// <returns>文件列表，如果缓存不存在则返回 null</returns>
    public static GameFilesList? LoadFilesList(string gamePath)
    {
        try
        {
            // 生成缓存文件名
            using var md5 = System.Security.Cryptography.MD5.Create();
            var hashBytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(gamePath));
            var pathHash = BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 16);
            var cacheFilePath = Path.Combine(FilesListCacheDir, $"files_{pathHash}.json");

            if (!File.Exists(cacheFilePath))
            {
                Log.Info($"[GameFilesList] 缓存文件不存在: {cacheFilePath}");
                return null;
            }

            // 反序列化 JSON
            var json = File.ReadAllText(cacheFilePath);
            var filesList = JsonSerializer.Deserialize<GameFilesList>(json);

            if (filesList != null)
            {
                Log.Info($"[GameFilesList] 从缓存加载文件列表: {filesList.Files.Count} 个文件，扫描时间: {filesList.ScanTime}");
            }

            return filesList;
        }
        catch (Exception ex)
        {
            Log.Warn("[GameFilesList] 加载文件列表失败", ex);
            return null;
        }
    }

    /// <summary>
    /// 获取或扫描文件列表
    /// </summary>
    /// <param name="gamePath">游戏路径</param>
    /// <param name="forceRescan">是否强制重新扫描</param>
    /// <returns>文件列表</returns>
    public static GameFilesList GetOrScanFilesList(string gamePath, bool forceRescan = false)
    {
        if (!forceRescan)
        {
            var cached = LoadFilesList(gamePath);
            if (cached != null)
            {
                // 检查缓存是否过期（7天）
                if ((DateTime.Now - cached.ScanTime).TotalDays < 7)
                {
                    Log.Info($"[GameFilesList] 使用缓存的文件列表（扫描时间: {cached.ScanTime}）");
                    return cached;
                }
                else
                {
                    Log.Info($"[GameFilesList] 缓存已过期（{(DateTime.Now - cached.ScanTime).TotalDays:F1}天），重新扫描");
                }
            }
        }

        // 扫描并保存
        var filesList = ScanGameFiles(gamePath);
        SaveFilesList(gamePath, filesList);
        return filesList;
    }

    /// <summary>
    /// 复制游戏文件到目标目录
    /// </summary>
    /// <param name="gamePath">游戏本体路径</param>
    /// <param name="targetPath">目标路径（版本目录）</param>
    /// <param name="filesList">文件列表</param>
    public static void CopyGameFiles(string gamePath, string targetPath, GameFilesList? filesList = null)
    {
        try
        {
            Log.Info($"[GameFilesList] 开始复制游戏文件...");
            Log.Info($"[GameFilesList] 源路径: {gamePath}");
            Log.Info($"[GameFilesList] 目标路径: {targetPath}");

            // 获取或扫描文件列表
            filesList ??= GetOrScanFilesList(gamePath);

            // 创建目标目录
            if (!Directory.Exists(targetPath))
            {
                Directory.CreateDirectory(targetPath);
            }

            var copiedFiles = 0;
            var copiedDirs = 0;

            // 复制文件
            foreach (var fileName in filesList.Files)
            {
                var sourceFile = Path.Combine(gamePath, fileName);
                var destFile = Path.Combine(targetPath, fileName);

                if (File.Exists(sourceFile))
                {
                    // 如果目标文件已存在，先删除
                    if (File.Exists(destFile))
                    {
                        File.Delete(destFile);
                    }

                    // 复制文件
                    File.Copy(sourceFile, destFile, overwrite: true);
                    copiedFiles++;

                    if (copiedFiles % 50 == 0)
                    {
                        Log.Info($"[GameFilesList] 已复制 {copiedFiles}/{filesList.Files.Count} 个文件...");
                    }
                }
                else
                {
                    Log.Warn($"[GameFilesList] 源文件不存在，跳过: {fileName}");
                }
            }

            Log.Info($"[GameFilesList] ✓ 已复制 {copiedFiles} 个文件");

            // 复制特殊目录（如 steam_settings）
            foreach (var dirName in filesList.DirectoriesToCopy)
            {
                var sourceDir = Path.Combine(gamePath, dirName);
                var destDir = Path.Combine(targetPath, dirName);

                if (Directory.Exists(sourceDir))
                {
                    CopyDirectoryRecursive(sourceDir, destDir);
                    copiedDirs++;
                    Log.Info($"[GameFilesList] ✓ 已复制目录: {dirName}");
                }
            }

            Log.Info($"[GameFilesList] ✓ 已复制 {copiedDirs} 个目录");
            Log.Info($"[GameFilesList] ✓ 游戏文件复制完成");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[GameFilesList] 复制游戏文件失败");
            throw;
        }
    }

    /// <summary>
    /// 递归复制目录
    /// </summary>
    private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        // 复制文件
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var fileName = Path.GetFileName(file);
            var destFile = Path.Combine(targetDir, fileName);
            File.Copy(file, destFile, true);
        }

        // 复制子目录
        foreach (var directory in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(directory);
            var destDir = Path.Combine(targetDir, dirName);
            CopyDirectoryRecursive(directory, destDir);
        }
    }
}
