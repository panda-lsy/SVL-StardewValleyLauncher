using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SVL.Core.Logging;

namespace SVL.Core.Stardew.Instance;

/// <summary>
/// 版本隔离服务（Mod 隔离，存档使用系统默认路径）
/// 新架构：将游戏文件安装到 instances/实例名称/versions/，通过符号链接访问游戏本体
/// </summary>
public static class InstanceIsolationService
{
    /// <summary>
    /// 获取实例的版本隔离路径（新架构）
    /// </summary>
    /// <param name="instancePath">实例路径（instances/实例名称）</param>
    /// <returns>实例的版本目录路径（instances/实例名称/versions）</returns>
    public static string GetInstanceVersionPath(string instancePath)
    {
        return Path.Combine(instancePath, "versions");
    }

    /// <summary>
    /// 获取版本隔离的根目录（旧架构，保留兼容性）
    /// </summary>
    public static string GetIsolationRootPath(string gamePath)
    {
        return Path.Combine(gamePath, "versions");
    }

    /// <summary>
    /// 获取指定版本的隔离路径（旧架构，保留兼容性）
    /// </summary>
    public static string GetVersionPath(string gamePath, string instanceName)
    {
        var isolationRoot = GetIsolationRootPath(gamePath);
        var versionPath = Path.Combine(isolationRoot, instanceName);
        return versionPath;
    }

    /// <summary>
    /// 生成版本文件夹名称（基于实例名称）
    /// </summary>
    public static string GenerateVersionFolderName(string instanceName, bool isSMAPI)
    {
        // 直接使用实例名称作为文件夹名称
        // 如果需要区分 SMAPI/Vanilla，可以在名称后添加标识
        return instanceName;
    }

    /// <summary>
    /// 重命名隔离目录（当实例名称改变时调用）
    /// </summary>
    /// <param name="gamePath">游戏根目录</param>
    /// <param name="oldName">旧实例名称</param>
    /// <param name="newName">新实例名称</param>
    /// <returns>是否成功</returns>
    public static bool RenameIsolationDirectory(string gamePath, string oldName, string newName)
    {
        try
        {
            Logging.Log.Info($"[Isolation] Renaming isolation directory: {oldName} -> {newName}");

            var oldPath = GetVersionPath(gamePath, oldName);
            var newPath = GetVersionPath(gamePath, newName);

            // 检查旧目录是否存在
            if (!Directory.Exists(oldPath))
            {
                Logging.Log.Info($"[Isolation] Old isolation directory not found: {oldPath} (may not be initialized yet)");
                return true; // 如果旧目录不存在，视为成功（可能还没初始化）
            }

            // 检查新目录是否已存在
            if (Directory.Exists(newPath))
            {
                Logging.Log.Warn($"[Isolation] Target isolation directory already exists: {newPath}");
                return false;
            }

            // 验证新名称
            if (!IsValidVersionName(newName))
            {
                Logging.Log.Error($"[Isolation] Invalid new instance name: {newName}");
                return false;
            }

            // 重命名目录
            Directory.Move(oldPath, newPath);
            Logging.Log.Info($"[Isolation] ✓ Successfully renamed isolation directory to: {newPath}");

            return true;
        }
        catch (Exception ex)
        {
            Logging.Log.Error(ex, "[Isolation] Failed to rename isolation directory");
            return false;
        }
    }

    /// <summary>
    /// 验证实例名称是否已存在（复用 Curseforge 整合包的验证逻辑）
    /// </summary>
    /// <param name="instanceName">要验证的实例名称</param>
    /// <returns>(是否有效, 错误消息)</returns>
    public static (bool isValid, string errorMessage) ValidateInstanceName(string instanceName)
    {
        if (string.IsNullOrWhiteSpace(instanceName))
        {
            return (false, "实例名称不能为空");
        }

        // 检查是否包含非法字符
        var invalidChars = Path.GetInvalidFileNameChars();
        if (instanceName.IndexOfAny(invalidChars) >= 0)
        {
            return (false, "实例名称包含非法字符");
        }

        // 检查是否以点开头或结尾（Windows 限制）
        if (instanceName.StartsWith(".") || instanceName.EndsWith("."))
        {
            return (false, "实例名称不能以点开头或结尾");
        }

        // 检查是否包含 Windows 保留名称
        var reservedNames = new[] { "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

        var upperName = instanceName.ToUpper();
        if (reservedNames.Contains(upperName))
        {
            return (false, $"实例名称 '{instanceName}' 是系统保留名称");
        }

        // 检查长度限制（Windows MAX_PATH = 260）
        if (instanceName.Length > 100)
        {
            return (false, "实例名称过长（最多100个字符）");
        }

        // 检查实例名称是否已存在（复用 Curseforge 整合包的验证逻辑）
        var existingInstances = SettingsService.LoadInstances();
        if (existingInstances.Any(i => i.Name.Equals(instanceName, StringComparison.OrdinalIgnoreCase)))
        {
            return (false, $"实例名称 '{instanceName}' 已存在，请使用不同的名称");
        }

        return (true, "");
    }

    /// <summary>
    /// 验证实例名称是否已存在（包含文件系统版本目录检测）
    /// </summary>
    /// <param name="instanceName">要验证的实例名称</param>
    /// <param name="gamePath">游戏根目录（可选，如果不提供则只检查实例列表）</param>
    /// <returns>(是否有效, 错误消息)</returns>
    public static (bool isValid, string errorMessage) ValidateInstanceName(string instanceName, string? gamePath = null)
    {
        // 基础验证
        if (string.IsNullOrWhiteSpace(instanceName))
        {
            return (false, "实例名称不能为空");
        }

        // 检查是否包含非法字符
        var invalidChars = Path.GetInvalidFileNameChars();
        if (instanceName.IndexOfAny(invalidChars) >= 0)
        {
            return (false, "实例名称包含非法字符");
        }

        // 检查是否以点开头或结尾（Windows 限制）
        if (instanceName.StartsWith(".") || instanceName.EndsWith("."))
        {
            return (false, "实例名称不能以点开头或结尾");
        }

        // 检查是否包含 Windows 保留名称
        var reservedNames = new[] { "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

        var upperName = instanceName.ToUpper();
        if (reservedNames.Contains(upperName))
        {
            return (false, $"实例名称 '{instanceName}' 是系统保留名称");
        }

        // 检查长度限制（Windows MAX_PATH = 260）
        if (instanceName.Length > 100)
        {
            return (false, "实例名称过长（最多100个字符）");
        }

        // 检查实例名称是否已存在于实例列表
        var existingInstances = SettingsService.LoadInstances();
        if (existingInstances.Any(i => i.Name.Equals(instanceName, StringComparison.OrdinalIgnoreCase)))
        {
            return (false, $"实例名称 '{instanceName}' 已存在，请使用不同的名称");
        }

        // 如果提供了游戏路径，检查版本目录是否存在
        if (!string.IsNullOrEmpty(gamePath))
        {
            var versionPath = GetVersionPath(gamePath, instanceName);
            if (Directory.Exists(versionPath))
            {
                return (false, $"版本目录 '{instanceName}' 已存在，请使用不同的名称");
            }
        }

        return (true, "");
    }

    /// <summary>
    /// 检查版本目录是否存在（用于实时验证）
    /// </summary>
    /// <param name="gamePath">游戏根目录</param>
    /// <param name="instanceName">实例名称</param>
    /// <returns>版本目录是否存在</returns>
    public static bool VersionDirectoryExists(string gamePath, string instanceName)
    {
        var versionPath = GetVersionPath(gamePath, instanceName);
        return Directory.Exists(versionPath);
    }

    /// <summary>
    /// 验证版本名称是否可以作为文件夹名称
    /// </summary>
    public static bool IsValidVersionName(string instanceName)
    {
        if (string.IsNullOrWhiteSpace(instanceName))
            return false;

        // 检查是否包含非法字符
        var invalidChars = Path.GetInvalidFileNameChars();
        if (instanceName.IndexOfAny(invalidChars) >= 0)
            return false;

        // 检查是否以点开头或结尾（Windows 限制）
        if (instanceName.StartsWith(".") || instanceName.EndsWith("."))
            return false;

        // 检查是否包含 Windows 保留名称
        var reservedNames = new[] { "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

        var upperName = instanceName.ToUpper();
        if (reservedNames.Contains(upperName))
            return false;

        // 检查长度限制（Windows MAX_PATH = 260）
        if (instanceName.Length > 100) // 保守限制，给路径其他部分留空间
            return false;

        return true;
    }

    /// <summary>
    /// 获取 Content 符号链接路径（指向游戏根目录的 Content）
    /// </summary>
    public static string GetContentLinkPath(string gamePath, string instanceName)
    {
        return Path.Combine(GetVersionPath(gamePath, instanceName), "Content");
    }

    /// <summary>
    /// 获取 Mods 隔离路径
    /// </summary>
    public static string GetIsolatedModsPath(string gamePath, string instanceName)
    {
        return Path.Combine(GetVersionPath(gamePath, instanceName), "Mods");
    }

    /// <summary>
    /// 获取 Logs 隔离路径
    /// </summary>
    public static string GetIsolatedLogsPath(string gamePath, string instanceName)
    {
        return Path.Combine(GetVersionPath(gamePath, instanceName), "logs");
    }

    /// <summary>
    /// 初始化实例隔离目录结构（新架构）
    /// </summary>
    /// <param name="instancePath">实例路径（instances/实例名称）</param>
    /// <param name="gameBasePath">游戏本体路径</param>
    /// <param name="isSMAPI">是否为 SMAPI 实例</param>
    public static bool InitializeInstanceDirectories(string instancePath, string gameBasePath, bool isSMAPI)
    {
        try
        {
            var versionPath = GetInstanceVersionPath(instancePath);
            var gameLinkPath = Path.Combine(versionPath, "game");
            var modsPath = Path.Combine(versionPath, "Mods");
            var logsPath = Path.Combine(versionPath, "logs");

            // 创建版本根目录
            if (!Directory.Exists(versionPath))
            {
                Directory.CreateDirectory(versionPath);
                Logging.Log.Info($"[Isolation] 创建实例版本目录: {versionPath}");
            }

            // 创建 game 符号链接（指向游戏本体目录）
            if (!Directory.Exists(gameLinkPath))
            {
                try
                {
                    Logging.Log.Info($"[Isolation] 创建 game 符号链接: {gameLinkPath} -> {gameBasePath}");
                    CreateSymbolicLink(gameBasePath, gameLinkPath);
                }
                catch (Exception ex)
                {
                    Logging.Log.Warn("[Isolation] 无法创建符号链接，将创建目录连接", ex);
                    // 备选：使用 mklink 命令创建目录连接
                    CreateDirectoryJunction(gameBasePath, gameLinkPath);
                }
            }

            // 创建独立的 Mods、Logs 目录
            if (!Directory.Exists(modsPath))
            {
                Directory.CreateDirectory(modsPath);
                Logging.Log.Info($"[Isolation] 创建 Mods 隔离目录: {modsPath}");
            }

            if (!Directory.Exists(logsPath))
            {
                Directory.CreateDirectory(logsPath);
                Logging.Log.Info($"[Isolation] 创建 Logs 隔离目录: {logsPath}");
            }

            // 如果是 SMAPI 实例，需要从游戏本体复制 SMAPI 运行时文件
            if (isSMAPI)
            {
                CopySMAPIRuntimeFiles(gameBasePath, versionPath);
            }

            return true;
        }
        catch (Exception ex)
        {
            Logging.Log.Error(ex, "[Isolation] 初始化实例隔离目录失败");
            return false;
        }
    }

    /// <summary>
    /// 初始化隔离目录结构（旧架构，保留兼容性）
    /// </summary>
    public static bool InitializeIsolationDirectories(string gamePath, string instanceName, bool isSMAPI)
    {
        try
        {
            var versionPath = GetVersionPath(gamePath, instanceName);
            var contentLinkPath = GetContentLinkPath(gamePath, instanceName);  // 改为 Content 链接
            var modsPath = GetIsolatedModsPath(gamePath, instanceName);
            var logsPath = GetIsolatedLogsPath(gamePath, instanceName);

            // 创建版本根目录
            if (!Directory.Exists(versionPath))
            {
                Directory.CreateDirectory(versionPath);
                Logging.Log.Info($"[Isolation] 创建版本目录: {versionPath}");
            }

            // *** 新架构：不再创建游戏文件的符号链接 ***
            // 游戏文件现在由 GameFilesListService.CopyGameFiles 复制到版本目录
            // 这样可以避免符号链接的问题，并且更符合用户的需求

            // 创建 Content 符号链接（指向游戏根目录的 Content，所有版本共用）
            var gameContentPath = Path.Combine(gamePath, "Content");
            if (!Directory.Exists(contentLinkPath) && Directory.Exists(gameContentPath))
            {
                try
                {
                    Logging.Log.Info($"[Isolation] 创建 Content 目录链接: {contentLinkPath} -> {gameContentPath}");
                    CreateSymbolicLink(contentLinkPath, gameContentPath);
                }
                catch (Exception ex)
                {
                    Logging.Log.Warn("[Isolation] 无法创建符号链接，将创建目录连接", ex);
                    // 备选：使用 mklink 命令创建目录连接
                    CreateDirectoryJunction(contentLinkPath, gameContentPath);
                }
            }

            // 创建独立的 Mods 目录（不包含 Saves，存档使用系统默认路径）
            if (!Directory.Exists(modsPath))
            {
                Directory.CreateDirectory(modsPath);
                Logging.Log.Info($"[Isolation] 创建 Mods 隔离目录: {modsPath}");
            }

            // 如果是 SMAPI 实例，需要复制 SMAPI 运行时文件
            if (isSMAPI)
            {
                CopySMAPIRuntimeFiles(gamePath, versionPath);
            }

            return true;
        }
        catch (Exception ex)
        {
            Logging.Log.Error(ex, "[Isolation] 初始化隔离目录失败");
            return false;
        }
    }

    /// <summary>
    /// 复制 SMAPI 运行时文件到版本目录
    /// </summary>
    private static void CopySMAPIRuntimeFiles(string gamePath, string versionPath)
    {
        try
        {
            var smapiExePath = Path.Combine(gamePath, "StardewModdingAPI.exe");
            var targetExePath = Path.Combine(versionPath, "StardewModdingAPI.exe");

            // 如果目标已经存在 SMAPI.exe，跳过复制
            if (File.Exists(targetExePath))
            {
                Logging.Log.Info("[Isolation] SMAPI 运行时文件已存在，跳过复制");
                return;
            }

            // 复制 SMAPI 相关文件
            var smapiFiles = new[]
            {
                "StardewModdingAPI.exe",
                "StardewModdingAPI.dll",
                "StardewModdingAPI.deps.json",
                "Mono.Cecil.dll",
                "Mono.Cecil.Rocks.dll",
                "MonoMod.RuntimeDetour.dll",
                "MonoMod.Utils.dll"
            };

            foreach (var file in smapiFiles)
            {
                var sourcePath = Path.Combine(gamePath, file);
                var targetPath = Path.Combine(versionPath, file);

                if (File.Exists(sourcePath))
                {
                    File.Copy(sourcePath, targetPath, overwrite: true);
                    Logging.Log.Info($"[Isolation] 复制 SMAPI 文件: {file}");
                }
            }

            // 复制 Linux/macOS 运行时目录（如果存在）
            var runtimeDirs = new[] { "Linux", "macOS" };
            foreach (var dir in runtimeDirs)
            {
                var sourceDir = Path.Combine(gamePath, dir);
                var targetDir = Path.Combine(versionPath, dir);

                if (Directory.Exists(sourceDir) && !Directory.Exists(targetDir))
                {
                    CopyDirectoryRecursive(sourceDir, targetDir);
                    Logging.Log.Info($"[Isolation] 复制运行时目录: {dir}");
                }
            }
        }
        catch (Exception ex)
        {
            Logging.Log.Warn("[Isolation] 复制 SMAPI 运行时文件失败（可能已安装）", ex);
        }
    }

    /// <summary>
    /// 获取隔离环境的实际启动路径
    /// </summary>
    /// <param name="gamePath">游戏根目录</param>
    /// <param name="instanceName">版本名称</param>
    /// <param name="isSMAPI">是否为 SMAPI 实例</param>
    /// <returns>实际启动路径</returns>
    public static string GetLaunchPath(string gamePath, string instanceName, bool isSMAPI)
    {
        if (isSMAPI)
        {
            // SMAPI: 使用版本目录中的 StardewModdingAPI.exe
            var versionPath = GetVersionPath(gamePath, instanceName);
            return Path.Combine(versionPath, "StardewModdingAPI.exe");
        }
        else
        {
            // 原版: 使用游戏根目录中的 Stardew Valley.exe
            // 但需要设置环境变量或参数指向隔离目录
            return Path.Combine(gamePath, "Stardew Valley.exe");
        }
    }

    /// <summary>
    /// 获取隔离环境的工作目录
    /// </summary>
    public static string GetWorkingDirectory(string gamePath, string instanceName, bool isSMAPI)
    {
        if (isSMAPI)
        {
            // SMAPI: 工作目录设为版本目录
            return GetVersionPath(gamePath, instanceName);
        }
        else
        {
            // 原版: 工作目录设为游戏根目录
            return gamePath;
        }
    }

    /// <summary>
    /// 创建目录连接（Junction，不需要管理员权限）
    /// 使用 mklink /J 命令
    /// </summary>
    private static void CreateDirectoryJunction(string targetPath, string linkPath)
    {
        try
        {
            // 删除可能存在的链接
            if (Directory.Exists(linkPath))
            {
                Directory.Delete(linkPath);
            }

            // 使用 cmd /c mklink 创建目录连接
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            process.Start();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                var error = process.StandardError.ReadToEnd();
                throw new Exception($"mklink failed: {error}");
            }

            Logging.Log.Info($"[Isolation] ✓ Created directory junction: {linkPath} -> {targetPath}");
        }
        catch (Exception ex)
        {
            Logging.Log.Error(ex, "[Isolation] Failed to create directory junction");
            throw;
        }
    }

    /// <summary>
    /// 创建符号链接（需要管理员权限或开发者模式）
    /// </summary>
    private static void CreateSymbolicLink(string targetPath, string linkPath)
    {
        try
        {
            // 删除可能存在的链接
            if (Directory.Exists(linkPath))
            {
                Directory.Delete(linkPath);
            }

            // 使用 cmd /c mklink 创建符号链接
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c mklink /D \"{linkPath}\" \"{targetPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            process.Start();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                var error = process.StandardError.ReadToEnd();
                throw new Exception($"mklink failed: {error}");
            }

            Logging.Log.Info($"[Isolation] ✓ Created symbolic link: {linkPath} -> {targetPath}");
        }
        catch (Exception ex)
        {
            Logging.Log.Error(ex, "[Isolation] Failed to create symbolic link");
            throw;
        }
    }

    /// <summary>
    /// 递归复制目录
    /// </summary>
    private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var fileName = Path.GetFileName(file);
            var destFile = Path.Combine(targetDir, fileName);
            File.Copy(file, destFile, true);
        }

        foreach (var directory in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(directory);
            var destDir = Path.Combine(targetDir, dirName);
            CopyDirectoryRecursive(directory, destDir);
        }
    }

    /// <summary>
    /// 创建文件符号链接或硬链接
    /// Windows 下文件符号链接需要管理员权限，这里优先使用硬链接
    /// </summary>
    private static void CreateFileSymbolicLinkOrJunction(string sourcePath, string linkPath)
    {
        try
        {
            // 尝试创建硬链接（不需要管理员权限）
            // 硬链接只能在同一卷内使用，但对于游戏文件通常没问题
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c mklink /H \"{linkPath}\" \"{sourcePath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            process.Start();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                var error = process.StandardError.ReadToEnd();
                throw new Exception($"mklink /H failed: {error}");
            }

            Logging.Log.Info($"[Isolation] ✓ Created hard link: {linkPath} -> {sourcePath}");
        }
        catch (Exception ex)
        {
            Logging.Log.Warn($"[Isolation] Failed to create hard link, will copy file instead: {ex.Message}");
            throw;
        }
    }
}
