using System.Diagnostics;
using System.IO.Compression;
using SVL.Core.Platform.Abstractions;

namespace SVL.Core.Platform.Services;

/// <summary>
/// SMAPI 安装服务实现。从 zip 包安装 SMAPI 到隔离实例目录。
/// SMAPI 4.x 安装包结构: "SMAPI x.x.x installer/internal/&lt;platform&gt;/install.dat"
/// install.dat 本质是 zip 文件（.dat 扩展名仅为避免用户混淆），解压后含 StardewModdingAPI.exe 等运行时文件。
/// 本服务采用手动安装方式（解压 install.dat + 复制文件 + 复制 deps.json），不运行交互式 installer。
/// 参考 SMAPI 官方 README 的 Manual install 说明。
/// </summary>
public sealed class SmapiInstallService : ISmapiInstallService
{
    public async Task<SmapiInstallResult> InstallFromZipAsync(
        string zipFilePath,
        string gameBasePath,
        string instanceName,
        CancellationToken cancellationToken = default,
        Action<string>? logger = null,
        Func<string, string, CancellationToken, Task>? zipExtractor = null,
        bool updateExisting = false)
    {
        if (string.IsNullOrWhiteSpace(zipFilePath) || !File.Exists(zipFilePath))
        {
            return SmapiInstallResult.Failed("SMAPI 压缩包不存在");
        }

        if (string.IsNullOrWhiteSpace(gameBasePath) || !Directory.Exists(gameBasePath))
        {
            return SmapiInstallResult.Failed("游戏基础路径无效");
        }

        var safeInstanceName = SanitizeFolderName(instanceName);
        if (string.IsNullOrWhiteSpace(safeInstanceName))
        {
            return SmapiInstallResult.Failed("实例名称无效");
        }

        var versionRoot = Path.Combine(gameBasePath, "versions", safeInstanceName);
        var isUpdate = false;
        if (Directory.Exists(versionRoot))
        {
            if (!updateExisting)
            {
                return SmapiInstallResult.Failed($"实例已存在: {safeInstanceName}");
            }

            isUpdate = true;
        }

        // 新装直接安装到版本文件夹，不再嵌套 /game/ 子目录；
        // 更新旧架构创建的实例（versions/<name>/game 布局）时沿用其原有运行时目录，避免用户 Mods 位置漂移
        var legacyRuntimePath = Path.Combine(versionRoot, "game");
        var runtimePath = isUpdate && IsValidGamePath(legacyRuntimePath)
            ? legacyRuntimePath
            : versionRoot;

        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "svl-smapi-install",
            Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(versionRoot);
            Directory.CreateDirectory(tempRoot);

            logger?.Invoke($"开始{(isUpdate ? "更新" : "安装")} SMAPI: zip={Path.GetFileName(zipFilePath)}, base={gameBasePath}, instance={safeInstanceName}");

            if (isUpdate)
            {
                // 更新模式：清空旧运行时文件，保留用户 Mods 与启动器元数据（.svl-*），
                // 参考旧架构 SmapiDownloadTask.CleanupVersionDirectoryForUpdate
                logger?.Invoke($"更新模式：清理旧版本目录（保留用户 Mods）: {runtimePath}");
                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CleanupRuntimeDirectoryForUpdate(runtimePath, logger);
                }, cancellationToken);
            }

            // 步 1: 复制基础游戏文件到版本目录
            logger?.Invoke("步 1/8: 开始复制基础游戏文件...");
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                CopyBaseGame(gameBasePath, runtimePath, cancellationToken, logger);
            }, cancellationToken);
            logger?.Invoke("步 1/8: 基础游戏文件复制完成");

            // 步 2: 解压 SMAPI 安装包到临时目录
            logger?.Invoke("步 2/8: 解压 SMAPI 安装包...");
            var extractPath = Path.Combine(tempRoot, "extract");
            Directory.CreateDirectory(extractPath);

            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (zipExtractor != null)
                {
                    zipExtractor(zipFilePath, extractPath, cancellationToken).GetAwaiter().GetResult();
                }
                else
                {
                    ZipFile.ExtractToDirectory(zipFilePath, extractPath, true);
                }
            }, cancellationToken);
            logger?.Invoke("步 2/8: 解压完成");

            // 步 3: 定位 install.dat（位于 internal/<platform>/ 目录）
            // 兼容 "double-zipped" 官方安装包：外层 zip 可能只包含一个嵌套的安装包 zip
            //（如 SMAPI-4.5.2-installer-double-zipped.zip 内只有 SMAPI-4.5.2-installer.zip），
            // 需要先把嵌套 zip 解包出来才能找到 install.dat。
            var installDatPath = FindInstallDat(extractPath);
            if (installDatPath == null)
            {
                logger?.Invoke("步 3/8: 未直接找到 install.dat，尝试解包嵌套安装包...");
                var unwrapped = await Task.Run(
                    () => TryUnwrapNestedInstallerZip(extractPath, cancellationToken),
                    cancellationToken);
                if (unwrapped)
                {
                    installDatPath = FindInstallDat(extractPath);
                    logger?.Invoke(installDatPath != null
                        ? $"步 3/8: 已解包嵌套安装包并定位 install.dat: {Path.GetRelativePath(extractPath, installDatPath)}"
                        : "步 3/8: 已解包嵌套安装包，但仍未找到 install.dat");
                }
            }

            if (installDatPath == null)
            {
                logger?.Invoke("步 3/8: 未找到 install.dat");
                // 更新模式下不回滚删除（目录中仍有基础游戏文件与用户 Mods），仅新装时清理避免残留"实例已存在"
                if (!isUpdate)
                {
                    CleanupVersionDirectory(versionRoot);
                }
                return SmapiInstallResult.Failed("未找到 install.dat，请确认下载的是 SMAPI 官方安装包");
            }
            logger?.Invoke($"步 3/8: 定位 install.dat: {Path.GetRelativePath(extractPath, installDatPath)}");

            // 步 4: 解压 install.dat 到临时目录，再复制到游戏目录
            logger?.Invoke("步 4/8: 解压 install.dat...");
            var payloadPath = Path.Combine(tempRoot, "payload");
            Directory.CreateDirectory(payloadPath);

            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                // install.dat 是 zip 格式，优先使用自定义解压器（支持 Deflate64 等）
                if (zipExtractor != null)
                {
                    zipExtractor(installDatPath, payloadPath, cancellationToken).GetAwaiter().GetResult();
                }
                else
                {
                    ZipFile.ExtractToDirectory(installDatPath, payloadPath, true);
                }
            }, cancellationToken);
            logger?.Invoke("步 4/8: 解压完成");

            // 步 5: 复制 payload 文件到游戏目录（覆盖同名文件）
            logger?.Invoke("步 5/8: 复制 SMAPI 运行时文件到游戏目录...");
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                CopyDirectory(payloadPath, runtimePath, overwrite: true, cancellationToken, logger);
            }, cancellationToken);
            logger?.Invoke("步 5/8: 复制完成");

            // 步 6: 复制 Stardew Valley.deps.json → StardewModdingAPI.deps.json（SMAPI 运行需要）
            var gameDepsJson = Path.Combine(runtimePath, "Stardew Valley.deps.json");
            var smapiDepsJson = Path.Combine(runtimePath, "StardewModdingAPI.deps.json");
            if (File.Exists(gameDepsJson) && !File.Exists(smapiDepsJson))
            {
                File.Copy(gameDepsJson, smapiDepsJson, true);
                logger?.Invoke("步 6/8: 已复制 StardewModdingAPI.deps.json");
            }
            else
            {
                logger?.Invoke("步 6/8: deps.json 已存在或游戏 deps.json 缺失，跳过");
            }

            // 步 7: 确保 Mods 目录存在
            Directory.CreateDirectory(Path.Combine(runtimePath, "Mods"));
            logger?.Invoke("步 7/8: Mods 目录已就绪");

            // 步 8: 验证 SMAPI 标记文件
            if (!HasSmapiMarkers(runtimePath))
            {
                logger?.Invoke("步 8/8: 验证失败 - 未检测到 SMAPI 标记文件");
                if (!isUpdate)
                {
                    CleanupVersionDirectory(versionRoot);
                }
                return SmapiInstallResult.Failed("安装后未检测到 SMAPI 可执行文件，请确认安装包与系统平台匹配");
            }
            logger?.Invoke("步 8/8: 验证通过 - 检测到 StardewModdingAPI.exe");

            logger?.Invoke($"SMAPI 安装成功: {runtimePath}");
            return SmapiInstallResult.Success(runtimePath, versionRoot);
        }
        catch (OperationCanceledException)
        {
            if (isUpdate)
            {
                logger?.Invoke("SMAPI 更新已取消，保留实例目录（用户 Mods 未受影响，可重新安装恢复运行时）");
                return SmapiInstallResult.Cancelled("SMAPI 更新已取消，实例目录已保留，请重新安装以恢复完整运行时");
            }

            logger?.Invoke("SMAPI 安装已取消，清理实例目录");
            CleanupVersionDirectory(versionRoot);
            return SmapiInstallResult.Cancelled("SMAPI 安装已取消");
        }
        catch (Exception ex)
        {
            if (isUpdate)
            {
                logger?.Invoke($"SMAPI 更新异常: {ex.Message}（保留实例目录，用户 Mods 未受影响）");
                return SmapiInstallResult.Failed($"SMAPI 更新失败: {ex.Message}，实例目录已保留，请重新安装以恢复完整运行时");
            }

            logger?.Invoke($"SMAPI 安装异常: {ex.Message}");
            CleanupVersionDirectory(versionRoot);
            return SmapiInstallResult.Failed($"SMAPI 安装失败: {ex.Message}");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    /// <summary>
    /// 查找解压目录中的 install.dat。
    /// SMAPI 4.x 安装包结构: "SMAPI x.x.x installer/internal/&lt;platform&gt;/install.dat"
    /// 优先按当前平台查找，回退到任意平台的 install.dat。
    /// </summary>
    private static string? FindInstallDat(string extractPath)
    {
        var platformDir = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsMacOS() ? "macOS"
            : "linux";

        // 优先查找 internal/<platform>/install.dat
        var installDat = Directory.EnumerateFiles(extractPath, "install.dat", SearchOption.AllDirectories)
            .FirstOrDefault(p => p.IndexOf(platformDir, StringComparison.OrdinalIgnoreCase) >= 0);

        if (!string.IsNullOrWhiteSpace(installDat) && File.Exists(installDat))
        {
            return installDat;
        }

        // 回退：任意平台的 install.dat
        installDat = Directory.EnumerateFiles(extractPath, "install.dat", SearchOption.AllDirectories)
            .FirstOrDefault();

        return !string.IsNullOrWhiteSpace(installDat) && File.Exists(installDat) ? installDat : null;
    }

    /// <summary>
    /// 处理 "double-zipped" 的 SMAPI 安装包：外层 zip 只含一个嵌套的安装包 zip 时，
    /// 逐个把嵌套 zip 就地解包到 extractPath（FindInstallDat 按 AllDirectories 递归查找，
    /// 因此子目录结构也无需展平），直到找到 install.dat 或没有更多可解包的 zip。
    /// </summary>
    private static bool TryUnwrapNestedInstallerZip(string extractPath, CancellationToken cancellationToken)
    {
        // 最多解包若干层，避免恶意/损坏包导致无限循环
        const int maxDepth = 4;
        for (var depth = 0; depth < maxDepth; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Directory.EnumerateFiles(extractPath, "install.dat", SearchOption.AllDirectories).Any())
            {
                return true;
            }

            var nestedZip = Directory.EnumerateFiles(extractPath, "*.zip", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(nestedZip))
            {
                return false;
            }

            try
            {
                ZipFile.ExtractToDirectory(nestedZip, extractPath, true);
                TryDeleteFile(nestedZip);
            }
            catch
            {
                // 单个 zip 解包失败则停止，交由上层报告"未找到 install.dat"
                return false;
            }
        }

        return Directory.EnumerateFiles(extractPath, "install.dat", SearchOption.AllDirectories).Any();
    }

    /// <summary>
    /// 更新模式清理：清空运行时目录中的旧版本文件，保留用户数据。
    /// 参考旧架构 SmapiDownloadTask.CleanupVersionDirectoryForUpdate：
    /// - Mods 目录整体保留，仅删除 SMAPI 附带模组（ConsoleCommands/SaveBackup），安装时会随新版本重新写入
    /// - .svl-* 前缀文件为启动器元数据（如自定义实例图标 .svl-instance-icon.*），保留
    /// - junction/symlink 子目录（如旧架构的 Content 连接）用 rmdir 移除，避免跟随连接误删源目录
    /// 清理为 best-effort：单个文件删除失败仅记日志，后续复制会覆盖同名文件。
    /// </summary>
    private static void CleanupRuntimeDirectoryForUpdate(string runtimePath, Action<string>? logger)
    {
        if (string.IsNullOrWhiteSpace(runtimePath) || !Directory.Exists(runtimePath))
        {
            return;
        }

        var modsPath = Path.Combine(runtimePath, "Mods");
        if (Directory.Exists(modsPath))
        {
            foreach (var bundledMod in new[] { "ConsoleCommands", "SaveBackup" })
            {
                var bundledPath = Path.Combine(modsPath, bundledMod);
                if (Directory.Exists(bundledPath))
                {
                    try
                    {
                        Directory.Delete(bundledPath, true);
                        logger?.Invoke($"  已删除 SMAPI 附带模组: {bundledMod}");
                    }
                    catch (Exception ex)
                    {
                        logger?.Invoke($"  删除 SMAPI 附带模组失败: {bundledMod} ({ex.Message})");
                    }
                }
            }
        }

        var dirInfo = new DirectoryInfo(runtimePath);
        foreach (var file in dirInfo.GetFiles())
        {
            if (file.Name.StartsWith(".svl-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                file.Delete();
            }
            catch (Exception ex)
            {
                logger?.Invoke($"  删除文件失败: {file.Name} ({ex.Message})");
            }
        }

        foreach (var dir in dirInfo.GetDirectories())
        {
            if (dir.Name.Equals("Mods", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (IsJunctionOrSymlink(dir.FullName))
                {
                    RemoveJunction(dir.FullName);
                }
                else
                {
                    dir.Delete(true);
                }
            }
            catch (Exception ex)
            {
                logger?.Invoke($"  删除目录失败: {dir.Name} ({ex.Message})");
            }
        }

        logger?.Invoke("  旧版本目录清理完成（用户 Mods 已保留）");
    }

    /// <summary>
    /// 清理已创建的版本目录（安装失败/取消时调用），避免重试时报"实例已存在"。
    /// 参考旧架构 SmapiDownloadTask.CleanupVersionDirectory：
    /// 先处理 Content 目录连接（junction），避免 Directory.Delete 跟随连接误删源目录。
    /// </summary>
    private static void CleanupVersionDirectory(string versionRoot)
    {
        if (string.IsNullOrWhiteSpace(versionRoot) || !Directory.Exists(versionRoot))
        {
            return;
        }

        try
        {
            // 检查 game/Content 是否为 junction/symlink，若是则用 rmdir 移除（不跟随）
            foreach (var contentPath in new[]
                     {
                         Path.Combine(versionRoot, "Content"),
                         Path.Combine(versionRoot, "game", "Content")
                     })
            {
                if (Directory.Exists(contentPath) && IsJunctionOrSymlink(contentPath))
                {
                    RemoveJunction(contentPath);
                }
            }

            Directory.Delete(versionRoot, true);
        }
        catch
        {
            // 清理失败不抛异常，避免影响错误传播
        }
    }

    /// <summary>判断路径是否为 junction 或 symbolic link。</summary>
    private static bool IsJunctionOrSymlink(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidGamePath(string path)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        var markers = new[]
        {
            "Stardew Valley.dll",
            "Stardew Valley.deps.json",
            "Stardew Valley.exe",
            "StardewValley.exe",
            "StardewValley",
            "Stardew Valley",
            "Stardew Valley.app"
        };

        return markers.Any(marker =>
            File.Exists(Path.Combine(path, marker)) ||
            Directory.Exists(Path.Combine(path, marker)));
    }

    /// <summary>移除 junction/symlink 目录（不跟随目标，仅删除链接本身）。</summary>
    private static void RemoveJunction(string junctionPath)
    {
        try
        {
            // 使用 cmd /c rmdir 移除 junction（不会删除 junction 指向的实际内容）
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c rmdir \"{junctionPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(psi);
            if (process != null)
            {
                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private static void CopyBaseGame(string sourcePath, string targetPath, CancellationToken cancellationToken, Action<string>? logger = null)
    {
        Directory.CreateDirectory(targetPath);

        // 策略（参考旧架构 InstanceIsolationService.InitializeIsolationDirectories）：
        // - Content 目录：创建 Junction（目录连接）指向源 Content，所有实例共享同一份游戏资源
        //   Junction 不需要管理员权限，同卷内可用；失败回退到复制
        // - 其他文件：直接 File.Copy（不再使用硬链接，避免日志误报"硬链接成功"但实际未生效的问题）
        var sourceContentPath = Path.Combine(sourcePath, "Content");
        var targetContentPath = Path.Combine(targetPath, "Content");
        var contentJunctionCreated = false;

        if (Directory.Exists(sourceContentPath) && !Directory.Exists(targetContentPath))
        {
            contentJunctionCreated = TryCreateDirectoryJunction(sourceContentPath, targetContentPath, logger);
            // Junction 失败时回退到复制 Content 目录（保证功能可用，只是占用更多磁盘空间）
        }

        // 统计需要复制的文件：Junction 成功时排除 Content（已链接），失败时包含 Content（回退复制）
        var skipContent = contentJunctionCreated;
        var allFiles = Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories)
            .Where(f =>
            {
                var relative = Path.GetRelativePath(sourcePath, f);
                return !ShouldSkipRelativePath(relative) && !ContainsReservedDeviceName(relative) && (!skipContent || !IsUnderContentDirectory(relative));
            })
            .ToList();
        var totalFiles = allFiles.Count;
        var copied = 0;
        logger?.Invoke($"  CopyBaseGame: 共 {totalFiles} 个文件待复制{(contentJunctionCreated ? "（Content 已 Junction 链接）" : "")}");

        // 创建非 Content 的子目录结构（Junction 成功时跳过 Content 子目录）
        foreach (var directory in Directory.EnumerateDirectories(sourcePath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourcePath, directory);
            if (ShouldSkipRelativePath(relative) || ContainsReservedDeviceName(relative))
            {
                continue;
            }
            if (skipContent && IsUnderContentDirectory(relative))
            {
                continue;
            }
            Directory.CreateDirectory(Path.Combine(targetPath, relative));
        }

        foreach (var file in allFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourcePath, file);
            var destination = Path.Combine(targetPath, relative);
            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }
            File.Copy(file, destination, true);
            copied++;

            if (logger != null && (copied % 200 == 0 || copied == totalFiles))
            {
                logger?.Invoke($"  CopyBaseGame 进度: {copied}/{totalFiles} 文件");
            }
        }

        logger?.Invoke($"  CopyBaseGame 完成: 复制={copied} 文件, Content={(contentJunctionCreated ? "Junction 链接" : "未处理")}");
    }

    /// <summary>判断相对路径是否位于 Content 目录下（Content 本身或其子目录内的文件）。</summary>
    private static bool IsUnderContentDirectory(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }
        var firstSegment = relativePath
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;
        return string.Equals(firstSegment, "Content", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 创建目录 Junction（mklink /J），不需要管理员权限，同卷内可用。
    /// 参考旧架构 InstanceIsolationService：Junction 比 SymbolicLink 更可靠（不需要管理员权限）。
    /// 失败时返回 false，由调用方回退到复制。
    /// </summary>
    private static bool TryCreateDirectoryJunction(string sourcePath, string targetPath, Action<string>? logger)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                // 非 Windows 平台不支持 Junction，回退到复制
                return false;
            }

            if (!Directory.Exists(sourcePath))
            {
                logger?.Invoke($"  Junction 跳过: 源 Content 不存在, source={sourcePath}");
                return false;
            }

            // 使用 cmd /c mklink /J 创建目录 Junction
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{targetPath}\" \"{sourcePath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(psi);
            if (process == null)
            {
                logger?.Invoke("  Junction 失败: 无法启动 cmd.exe");
                return false;
            }
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(5000);

            if (process.ExitCode != 0)
            {
                logger?.Invoke($"  Junction 失败: exitCode={process.ExitCode}, stderr={stderr.Trim()}");
                return false;
            }

            if (!Directory.Exists(targetPath))
            {
                logger?.Invoke("  Junction 失败: mklink 返回成功但目标目录不存在");
                return false;
            }

            logger?.Invoke($"  Junction 成功: {targetPath} -> {sourcePath}");
            return true;
        }
        catch (Exception ex)
        {
            logger?.Invoke($"  Junction 异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 尝试创建硬链接（已废弃，保留供未来按需启用）。
    /// 当前策略改为 Content 目录用 Junction，其他文件用 File.Copy，不再对单个文件创建硬链接。
    /// </summary>
    private static bool TryCreateHardLink(string sourcePath, string destinationPath, Action<string>? logger, ref int errorLoggedCount)
    {
        return false;
    }

    private static bool ShouldSkipRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            string.Equals(relativePath, ".", StringComparison.Ordinal))
        {
            return false;
        }

        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var firstSegment = relativePath
            .Split(separators, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(firstSegment))
        {
            return false;
        }

        return string.Equals(firstSegment, "versions", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(firstSegment, ".git", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(firstSegment, "Mods", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSmapiMarkers(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return false;
        }

        var markers = new[]
        {
            "StardewModdingAPI.exe",
            "StardewModdingAPI",
            "StardewModdingAPI.dll"
        };

        return markers.Any(marker => File.Exists(Path.Combine(path, marker)));
    }

    private static void CopyDirectory(string sourceDir, string targetDir, bool overwrite, CancellationToken cancellationToken, Action<string>? logger = null)
    {
        Directory.CreateDirectory(targetDir);

        var allFiles = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories)
            .Where(f => !ContainsReservedDeviceName(Path.GetRelativePath(sourceDir, f)))
            .ToList();
        var totalFiles = allFiles.Count;
        var copied = 0;
        logger?.Invoke($"  CopyDirectory: 共 {totalFiles} 个文件待复制");

        foreach (var directory in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceDir, directory);
            if (ContainsReservedDeviceName(relative))
            {
                continue;
            }
            Directory.CreateDirectory(Path.Combine(targetDir, relative));
        }

        foreach (var file in allFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceDir, file);
            var targetFile = Path.Combine(targetDir, relative);
            var parent = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.Copy(file, targetFile, overwrite);
            copied++;

            if (logger != null && (copied % 100 == 0 || copied == totalFiles))
            {
                logger?.Invoke($"  CopyDirectory 进度: {copied}/{totalFiles} 文件");
            }
        }
    }

    /// <summary>
    /// 检查相对路径中是否包含 Windows 保留设备名（nul/con/aux/prn/com1-9/lpt1-9）。
    /// 这些名称在 Windows 上被解释为设备而非文件，访问会抛出"参数错误"。
    /// </summary>
    private static bool ContainsReservedDeviceName(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var segments = relativePath.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            var name = segment;
            // 去除扩展名（nul.txt 也会被 Windows 解释为 nul 设备）
            var dotIndex = name.IndexOf('.');
            if (dotIndex >= 0)
            {
                name = name[..dotIndex];
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (IsReservedDeviceName(name))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsReservedDeviceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var upper = name.ToUpperInvariant();
        return upper switch
        {
            "CON" or "PRN" or "AUX" or "NUL" => true,
            _ when upper.Length == 4 && (upper.StartsWith("COM") || upper.StartsWith("LPT"))
                   && int.TryParse(upper[3..], out var n) && n >= 1 && n <= 9 => true,
            _ => false
        };
    }

    private static string SanitizeFolderName(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return string.Empty;
        }

        var cleaned = string.Concat(rawName.Trim().Split(GetWindowsInvalidFileNameChars()));
        cleaned = cleaned.Trim().Trim('.');
        return string.IsNullOrWhiteSpace(cleaned) || ContainsReservedDeviceName(cleaned)
            ? string.Empty
            : cleaned;
    }

    private static char[] GetWindowsInvalidFileNameChars()
    {
        return Path.GetInvalidFileNameChars()
            .Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' })
            .Distinct()
            .ToArray();
    }

    private static void TryDeleteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, true);
        }
        catch
        {
            // Keep temp cleanup best-effort.
        }
    }

    private static void TryDeleteFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // Keep temp cleanup best-effort.
        }
    }
}
