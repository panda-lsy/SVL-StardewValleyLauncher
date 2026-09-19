using SVL.Core.Platform.Abstractions;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SVL.Core.Platform.IO;

namespace SVL.Avalonia.Services;

public sealed class DownloadInstallService
{
    private readonly IGameInstallPathLocator _gameInstallPathLocator;
    private readonly string _installRoot;
    private readonly string _backupRoot;

    /// <summary>
    /// 当前 Mods 安装路径解析器。由 DownloadPageViewModel 设置，
    /// 优先使用用户当前选中的实例路径，而非自动探测的 Steam/GOG 路径。
    /// </summary>
    public Func<string?>? CurrentModsPathResolver { get; set; }

    /// <summary>
    /// 在在线 Mod 安装覆盖现有目录前创建一个可被 Mod 管理页识别的备份。
    /// 备份放在目标实例根目录的 ModsBackup，而不是临时下载目录，便于用户
    /// 后续从“备份”标签页恢复。
    /// </summary>
    public bool TryBackupExistingModDirectory(
        string targetModsPath,
        string existingModPath,
        out string backupPath)
    {
        return TryBackupExistingModDirectory(
            targetModsPath,
            existingModPath,
            replacementFolderNames: null,
            sourceArchiveName: null,
            out backupPath);
    }

    /// <summary>
    /// 备份覆盖更新前的 Mod，并在原目录和备份目录中写入更新链。
    /// 旧重载保留给已有调用方；更新任务使用此重载记录新归档的目标目录名，
    /// 这样即使旧目录名和新目录名不同，恢复时也能找到并处理新目录。
    /// </summary>
    public bool TryBackupExistingModDirectory(
        string targetModsPath,
        string existingModPath,
        IReadOnlyList<string>? replacementFolderNames,
        string? sourceArchiveName,
        out string backupPath)
    {
        backupPath = string.Empty;
        if (string.IsNullOrWhiteSpace(targetModsPath) ||
            string.IsNullOrWhiteSpace(existingModPath) ||
            !Directory.Exists(targetModsPath) ||
            !Directory.Exists(existingModPath))
        {
            return false;
        }

        var source = string.Empty;
        var originalMovedToBackup = false;
        try
        {
            var modsRoot = Path.GetFullPath(targetModsPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            source = Path.GetFullPath(existingModPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var sourceParent = Path.GetDirectoryName(source);
            if (!string.Equals(sourceParent, modsRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var originalFolderName = Path.GetFileName(source);
            TryReadManifestIdentity(source, out var originalDisplayName, out var originalUniqueId);
            var backupRoot = Path.Combine(
                Directory.GetParent(modsRoot)?.FullName ?? modsRoot,
                "ModsBackup");
            Directory.CreateDirectory(backupRoot);
            var snapshotName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{
                CreateSafeFolderName(originalFolderName)}_{Guid.NewGuid():N}";
            backupPath = Path.Combine(backupRoot, snapshotName);

            var updateChain = replacementFolderNames == null
                ? null
                : BuildUpdateChain(
                    originalFolderName,
                    replacementFolderNames,
                    sourceArchiveName,
                    originalUniqueId);
            if (updateChain != null)
            {
                var chainPath = Path.Combine(source, ".svl-update-chain.json");
                File.WriteAllText(
                    chainPath,
                    JsonSerializer.Serialize(updateChain, new JsonSerializerOptions { WriteIndented = true }));
            }
            if (updateChain != null && replacementFolderNames is { Count: > 0 })
            {
                // 更新链已经落盘后，优先在同一文件系统内直接移动旧目录。
                // 这样新归档可以使用自己的目录名（旧 A、新 BCD），恢复时
                // 再根据链路移除 BCD 并还原 A。跨卷/特殊文件系统不支持移动时，
                // 回退为复制，仍保证更新前有完整备份。
                try
                {
                    Directory.Move(source, backupPath);
                    originalMovedToBackup = true;
                }
                catch (IOException)
                {
                    CopyDirectory(source, backupPath, overwrite: true);
                }
            }
            else
            {
                CopyDirectory(source, backupPath, overwrite: true);
            }

            var metadata = new
            {
                OriginalFolderName = originalFolderName,
                DisplayName = string.IsNullOrWhiteSpace(originalDisplayName)
                    ? originalFolderName
                    : originalDisplayName,
                Version = "未知版本",
                Author = string.Empty,
                Description = "在线安装覆盖前自动备份",
                UniqueId = originalUniqueId,
                CreatedAt = DateTime.Now,
                UpdateChain = updateChain
            };
            File.WriteAllText(
                Path.Combine(backupPath, ".svl-backup.json"),
                JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch
        {
            var preservedBackupPath = string.Empty;
            // Directory.Move 已成功但后续元数据落盘失败时，调用方会中止安装。
            // 此时必须把原目录还原到 Mods，否则一次“备份失败”会让用户的
            // 当前实例暂时缺少原有 Mod。还原失败则保留带更新链的备份，
            // 让用户仍可从备份页恢复，而不是删除最后一份可用数据。
            if (originalMovedToBackup &&
                !string.IsNullOrWhiteSpace(source) &&
                !string.IsNullOrWhiteSpace(backupPath) &&
                Directory.Exists(backupPath) &&
                !Directory.Exists(source))
            {
                try
                {
                    Directory.Move(backupPath, source);
                    var restoredChainPath = Path.Combine(source, ".svl-update-chain.json");
                    if (File.Exists(restoredChainPath))
                    {
                        File.Delete(restoredChainPath);
                    }

                    backupPath = string.Empty;
                }
                catch
                {
                    // 保留备份路径，交由下面的保护逻辑和备份页处理。
                }
            }

            if (!string.IsNullOrWhiteSpace(backupPath) && Directory.Exists(backupPath))
            {
                try
                {
                    // 移动成功后保留备份，避免元数据写入失败反而丢失用户的
                    // 原版本；下次刷新/手动恢复仍可找回它。这里不能用
                    // Directory.Delete：backupPath 可能就是唯一的原 Mod 快照。
                    if (!File.Exists(Path.Combine(backupPath, ".svl-update-chain.json")))
                    {
                        // 无法判断该目录是半成品还是已移动的用户原版时，保留它
                        // 供备份页/用户人工处理，不做不可恢复的物理删除。
                        System.Diagnostics.Debug.WriteLine(
                            $"[DownloadInstallService] 保留缺少更新链的备份目录: {backupPath}");
                    }

                    preservedBackupPath = backupPath;
                }
                catch
                {
                    // 备份路径本身可能已经不可读；不能为了清理而物理删除，
                    // 保持现场交给后续刷新/人工处理。
                }
            }
            // 还原成功时为空；还原失败或复制半成品仍存在时，把路径交给调用方，
            // 让任务日志/备份页能够指出可恢复现场的位置。
            backupPath = preservedBackupPath;
            return false;
        }
    }

    private static ModUpdateChainMetadata BuildUpdateChain(
        string originalFolderName,
        IReadOnlyList<string>? replacementFolderNames,
        string? sourceArchiveName,
        string originalUniqueId)
    {
        return new ModUpdateChainMetadata
        {
            OperationId = Guid.NewGuid().ToString("N"),
            OriginalFolderName = originalFolderName,
            OriginalUniqueId = originalUniqueId,
            ReplacementFolderNames = (replacementFolderNames ?? [])
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => Path.GetFileName(name.Trim()))
                .Where(name => !string.IsNullOrWhiteSpace(name) && name is not "." and not "..")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            SourceArchiveName = sourceArchiveName ?? string.Empty,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    private static bool TryReadManifestIdentity(
        string modDirectory,
        out string name,
        out string uniqueId)
    {
        name = string.Empty;
        uniqueId = string.Empty;
        try
        {
            var manifestPath = Directory
                .EnumerateFiles(modDirectory, "manifest.json", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(manifestPath))
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;
            name = GetJsonString(root, "Name", "name");
            uniqueId = GetJsonString(root, "UniqueID", "UniqueId", "unique_id");
            return !string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(uniqueId);
        }
        catch
        {
            return false;
        }
    }

    private static string GetJsonString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString()?.Trim() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    public DownloadInstallService(IGameInstallPathLocator gameInstallPathLocator)
    {
        _gameInstallPathLocator = gameInstallPathLocator;

        _installRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SVL",
            "Avalonia",
            "InstalledMods");

        _backupRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SVL",
            "Avalonia",
            "InstallBackups");

        Directory.CreateDirectory(_installRoot);
        Directory.CreateDirectory(_backupRoot);
    }

    public async Task<DownloadInstallResult> InstallAsync(
        string downloadedFilePath,
        string taskName,
        CancellationToken cancellationToken = default,
        string? sourcePlatform = null,
        long? sourceProjectId = null,
        long? sourceFileId = null,
        string? sourceDownloadUrl = null,
        string? sourceFileName = null,
        string? sourceRepository = null)
    {
        if (string.IsNullOrWhiteSpace(downloadedFilePath) || !File.Exists(downloadedFilePath))
        {
            return DownloadInstallResult.Failed("下载文件不存在，无法安装");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var targetModsPath = ResolveTargetModsPath();
        var safeName = CreateSafeFolderName(taskName);
        var installPath = Path.Combine(targetModsPath, safeName);
        var installedNames = new List<string>();
        var isArchive = IsArchiveFile(downloadedFilePath);

        try
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (isArchive)
                {
                    // Mod 发布包常见为“发行包名/实际 Mod 目录/manifest.json”。
                    // 以 manifest 的父目录为安装根，避免 Mods/任务名/发行包名/...
                    // 这种游戏无法识别的多余嵌套；同时拒绝没有 manifest 的损坏/错误下载。
                    if (!ModpackInstallService.InstallDownloadedModArchive(
                        downloadedFilePath, targetModsPath, safeName, out var archiveInstalledNames))
                    {
                        throw new InvalidDataException("Mod 压缩包中未找到有效的 manifest.json");
                    }

                    installedNames.AddRange(archiveInstalledNames);
                    return;
                }

                // 压缩包安装由 ModpackInstallService 负责事务式替换。
                // 只有普通文件安装才在复制前处理同名目录，避免损坏压缩包
                // 替换前误删旧 Mod，导致新包安装失败后无法恢复。
                if (Directory.Exists(installPath))
                {
                    if (!RecycleBinService.TryMoveToRecycleBin(installPath, out var recycleError))
                    {
                        throw new IOException($"无法将原有 Mod 移入回收站：{recycleError}");
                    }
                }

                Directory.CreateDirectory(installPath);
                var targetFile = Path.Combine(installPath, Path.GetFileName(downloadedFilePath));
                File.Copy(downloadedFilePath, targetFile, true);
                installedNames.Add(safeName);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return DownloadInstallResult.Cancelled("安装已取消");
        }
        catch (Exception ex)
        {
            return DownloadInstallResult.Failed($"安装失败: {ex.Message}");
        }

        WriteSourceCredentials(
            targetModsPath,
            installedNames,
            sourcePlatform,
            sourceProjectId,
            sourceFileId,
            sourceDownloadUrl,
            sourceFileName,
            sourceRepository);

        var primaryName = installedNames.FirstOrDefault() ?? safeName;
        return DownloadInstallResult.Success(Path.Combine(targetModsPath, primaryName), installedNames);
    }

    public async Task<DownloadInstallResult> InstallCollectionAsync(
        IReadOnlyList<string> downloadedFiles,
        string taskName,
        CollectionInstallConflictStrategy conflictStrategy = CollectionInstallConflictStrategy.Overwrite,
        IReadOnlyList<CollectionConflictPreviewItem>? conflictPreviewItems = null,
        CancellationToken cancellationToken = default)
    {
        if (downloadedFiles.Count == 0)
        {
            return DownloadInstallResult.Failed("Collection 下载文件为空，无法安装");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var targetModsPath = ResolveTargetModsPath();
        var safeName = CreateSafeFolderName(taskName);
        var sessionName = $"{safeName}-{DateTime.Now:yyyyMMddHHmmss}";
        var backupSessionPath = Path.Combine(_backupRoot, sessionName);
        var extractRoot = Path.Combine(_installRoot, "_collection_extract", sessionName);

        var installedItems = new List<string>();
        var installedModDirectories = new List<string>();
        var replacedModNames = new List<string>();
        var addedModNames = new List<string>();
        var actualActions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var overwrittenCount = 0;
        var skippedCount = 0;
        var backupOnlyCount = 0;
        var addedCount = 0;
        string reportPath = string.Empty;
        CollectionValidationResult validationResult = CollectionValidationResult.Empty;

        try
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(backupSessionPath);
                Directory.CreateDirectory(extractRoot);

                for (var i = 0; i < downloadedFiles.Count; i++)
                {
                    var file = downloadedFiles[i];
                    if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
                    {
                        continue;
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    if (!IsArchiveFile(file))
                    {
                        var payloadDir = Path.Combine(targetModsPath, "__collection_payload");
                        Directory.CreateDirectory(payloadDir);
                        var payloadTarget = Path.Combine(payloadDir, $"part-{i + 1}-{Path.GetFileName(file)}");
                        File.Copy(file, payloadTarget, true);
                        installedItems.Add(Path.GetFileName(payloadTarget));
                        continue;
                    }

                    var extractDir = Path.Combine(extractRoot, $"part-{i + 1}");
                    Directory.CreateDirectory(extractDir);
                    ExtractArchive(file, extractDir);

                    var foundModDirs = DiscoverModDirectories(extractDir);
                    if (foundModDirs.Count == 0)
                    {
                        var rawTarget = Path.Combine(targetModsPath, "__collection_raw", $"part-{i + 1}");
                        CopyDirectory(extractDir, rawTarget, true);
                        installedItems.Add($"raw-part-{i + 1}");
                        continue;
                    }

                    foreach (var modDir in foundModDirs)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // Collection 多文件入口与普通 Mod/整合包安装必须使用同一
                        // 目录名规则。Nexus/CurseForge 经常把 manifest 直接放在
                        // cf-项目ID-文件ID 或 File 文件名目录中，这只是下载器的
                        // 临时目录名，不能把它原样写入 Mods。
                        var modName = ModpackInstallService.ResolveInstalledModDirectoryName(
                            modDir,
                            extractDir,
                            Path.GetFileName(modDir));
                        var targetModDir = Path.Combine(targetModsPath, modName);

                        if (Directory.Exists(targetModDir))
                        {
                            var backupDir = Path.Combine(backupSessionPath, modName);
                            CopyDirectory(targetModDir, backupDir, true);

                            if (conflictStrategy == CollectionInstallConflictStrategy.Skip)
                            {
                                installedItems.Add($"[跳过]{modName}");
                                actualActions[modName] = "跳过";
                                skippedCount++;
                                continue;
                            }

                            if (conflictStrategy == CollectionInstallConflictStrategy.BackupOnly)
                            {
                                installedItems.Add($"[仅备份]{modName}");
                                actualActions[modName] = "仅备份";
                                backupOnlyCount++;
                                continue;
                            }

                            if (!RecycleBinService.TryMoveToRecycleBin(targetModDir, out var recycleError))
                            {
                                throw new IOException($"无法将原有 Mod 移入回收站：{recycleError}");
                            }
                            replacedModNames.Add(modName);
                        }
                        else
                        {
                            addedModNames.Add(modName);
                        }

                        CopyDirectory(modDir, targetModDir, true);
                        installedItems.Add(modName);
                        installedModDirectories.Add(targetModDir);
                        actualActions[modName] = Directory.Exists(Path.Combine(backupSessionPath, modName)) ? "覆盖" : "新增安装";
                        if (actualActions[modName] == "覆盖")
                        {
                            overwrittenCount++;
                        }
                        else
                        {
                            addedCount++;
                        }
                    }
                }

                validationResult = ValidateInstalledMods(installedModDirectories);

                var previewMismatches = BuildPreviewMismatches(conflictPreviewItems, actualActions);

                reportPath = WriteCollectionInstallReport(
                    targetModsPath,
                    taskName,
                    downloadedFiles,
                    installedItems,
                    backupSessionPath,
                    sessionName,
                    conflictStrategy,
                    conflictPreviewItems,
                    validationResult,
                    previewMismatches,
                    overwrittenCount,
                    skippedCount,
                    backupOnlyCount,
                    addedCount,
                    false,
                    []);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            var rollbackErrors = TryRollbackCollectionInstall(targetModsPath, backupSessionPath, replacedModNames, addedModNames);
            var rollbackText = rollbackErrors.Count == 0
                ? "已执行回滚"
                : $"回滚有 {rollbackErrors.Count} 项问题";
            return DownloadInstallResult.Cancelled($"Collection 安装已取消，{rollbackText}");
        }
        catch (Exception ex)
        {
            var rollbackErrors = TryRollbackCollectionInstall(targetModsPath, backupSessionPath, replacedModNames, addedModNames);
            var rollbackText = rollbackErrors.Count == 0
                ? "已执行回滚"
                : $"回滚有 {rollbackErrors.Count} 项问题";
            return DownloadInstallResult.Failed($"Collection 安装失败: {ex.Message}（{rollbackText}）");
        }

        var uniqueInstalledItems = installedItems
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return DownloadInstallResult.Success(
            targetModsPath,
            uniqueInstalledItems,
            backupSessionPath,
            reportPath,
            validationResult.IsValid,
            validationResult.Errors);
    }

    public async Task<IReadOnlyList<CollectionConflictPreviewItem>> PreviewCollectionConflictsAsync(
        IReadOnlyList<string> downloadedFiles,
        CollectionInstallConflictStrategy conflictStrategy,
        CancellationToken cancellationToken = default)
    {
        if (downloadedFiles.Count == 0)
        {
            return [];
        }

        var targetModsPath = ResolveTargetModsPath();
        var previewSession = DateTime.Now.ToString("yyyyMMddHHmmssfff");
        var previewRoot = Path.Combine(_installRoot, "_collection_preview", previewSession);

        var discoveredMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(previewRoot);

                for (var i = 0; i < downloadedFiles.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var file = downloadedFiles[i];
                    if (string.IsNullOrWhiteSpace(file) || !File.Exists(file) || !IsArchiveFile(file))
                    {
                        continue;
                    }

                    var extractDir = Path.Combine(previewRoot, $"part-{i + 1}");
                    Directory.CreateDirectory(extractDir);
                    ExtractArchive(file, extractDir);

                    foreach (var modDir in DiscoverModDirectories(extractDir))
                    {
                        discoveredMods.Add(
                            ModpackInstallService.ResolveInstalledModDirectoryName(
                                modDir,
                                extractDir,
                                Path.GetFileName(modDir)));
                    }
                }
            }, cancellationToken);
        }
        finally
        {
            try
            {
                if (Directory.Exists(previewRoot))
                {
                    Directory.Delete(previewRoot, true);
                }
            }
            catch
            {
                // Keep preview cleanup best-effort to avoid blocking main flow.
            }
        }

        var previewItems = discoveredMods
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(modName =>
            {
                var targetModDir = Path.Combine(targetModsPath, modName);
                var exists = Directory.Exists(targetModDir);
                var action = exists
                    ? conflictStrategy switch
                    {
                        CollectionInstallConflictStrategy.Skip => "跳过",
                        CollectionInstallConflictStrategy.BackupOnly => "仅备份",
                        _ => "覆盖"
                    }
                    : "新增安装";

                return new CollectionConflictPreviewItem
                {
                    ModName = modName,
                    Exists = exists,
                    PlannedAction = action
                };
            })
            .ToList();

        return previewItems;
    }

    private static string WriteCollectionInstallReport(
        string targetModsPath,
        string taskName,
        IReadOnlyList<string> sourceFiles,
        IReadOnlyList<string> installedItems,
        string backupPath,
        string sessionName,
        CollectionInstallConflictStrategy conflictStrategy,
        IReadOnlyList<CollectionConflictPreviewItem>? conflictPreviewItems,
        CollectionValidationResult validationResult,
        IReadOnlyList<string> previewMismatches,
        int overwrittenCount,
        int skippedCount,
        int backupOnlyCount,
        int addedCount,
        bool rollbackApplied,
        IReadOnlyList<string> rollbackErrors)
    {
        var reportDir = Path.Combine(targetModsPath, "__collection_reports");
        Directory.CreateDirectory(reportDir);

        var reportPath = Path.Combine(reportDir, $"{sessionName}-install-report.json");

        var report = new CollectionInstallReport
        {
            TaskName = taskName,
            CreatedAtUtc = DateTime.UtcNow,
            TargetModsPath = targetModsPath,
            ConflictStrategy = conflictStrategy.ToDisplayName(),
            BackupPath = backupPath,
            ValidationPassed = validationResult.IsValid,
            ValidationErrors = validationResult.Errors.ToList(),
            PreviewMismatchItems = previewMismatches.ToList(),
            OverwrittenCount = overwrittenCount,
            SkippedCount = skippedCount,
            BackupOnlyCount = backupOnlyCount,
            AddedCount = addedCount,
            RollbackApplied = rollbackApplied,
            RollbackErrors = rollbackErrors.ToList(),
            ConflictPreviewItems = (conflictPreviewItems ?? [])
                .Select(item => $"{item.ModName} => {item.PlannedAction}")
                .ToList(),
            SourceFiles = sourceFiles.Where(File.Exists).ToList(),
            InstalledItems = installedItems
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        };

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(reportPath, json);
        return reportPath;
    }

    private static List<string> BuildPreviewMismatches(
        IReadOnlyList<CollectionConflictPreviewItem>? conflictPreviewItems,
        IReadOnlyDictionary<string, string> actualActions)
    {
        var mismatches = new List<string>();
        if (conflictPreviewItems == null || conflictPreviewItems.Count == 0)
        {
            return mismatches;
        }

        var previewMap = conflictPreviewItems
            .GroupBy(item => item.ModName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().PlannedAction, StringComparer.OrdinalIgnoreCase);

        foreach (var kv in actualActions)
        {
            if (!previewMap.TryGetValue(kv.Key, out var plannedAction))
            {
                mismatches.Add($"{kv.Key}: 预览缺失，实际={kv.Value}");
                continue;
            }

            if (!string.Equals(plannedAction, kv.Value, StringComparison.Ordinal))
            {
                mismatches.Add($"{kv.Key}: 预览={plannedAction}, 实际={kv.Value}");
            }
        }

        foreach (var preview in previewMap)
        {
            if (!actualActions.ContainsKey(preview.Key))
            {
                mismatches.Add($"{preview.Key}: 预览={preview.Value}, 实际=未处理");
            }
        }

        return mismatches;
    }

    private static List<string> TryRollbackCollectionInstall(
        string targetModsPath,
        string backupSessionPath,
        IReadOnlyList<string> replacedModNames,
        IReadOnlyList<string> addedModNames)
    {
        var errors = new List<string>();

        foreach (var modName in replacedModNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var targetModDir = Path.Combine(targetModsPath, modName);
                var backupDir = Path.Combine(backupSessionPath, modName);
                if (!Directory.Exists(backupDir))
                {
                    continue;
                }

                if (Directory.Exists(targetModDir))
                {
                    if (!RecycleBinService.TryMoveToRecycleBin(targetModDir, out var recycleError))
                    {
                        errors.Add($"清理恢复中的 {modName} 失败：{recycleError}");
                        continue;
                    }
                }

                CopyDirectory(backupDir, targetModDir, true);
            }
            catch (Exception ex)
            {
                errors.Add($"恢复 {modName} 失败: {ex.Message}");
            }
        }

        foreach (var modName in addedModNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var targetModDir = Path.Combine(targetModsPath, modName);
                if (Directory.Exists(targetModDir))
                {
                    if (!RecycleBinService.TryMoveToRecycleBin(targetModDir, out var recycleError))
                    {
                        errors.Add($"清理新增 {modName} 失败：{recycleError}");
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"清理新增 {modName} 失败: {ex.Message}");
            }
        }

        return errors;
    }

    private static CollectionValidationResult ValidateInstalledMods(IReadOnlyList<string> modDirectories)
    {
        var errors = new List<string>();
        var seenUniqueIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var modDir in modDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var modName = Path.GetFileName(modDir);
            var manifestPath = FindManifestPath(modDir);

            if (string.IsNullOrWhiteSpace(manifestPath))
            {
                errors.Add($"{modName}: 缺少 manifest.json");
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(ReadTextFileWithBom(manifestPath));
                var root = doc.RootElement;

                // SMAPI 清单通常使用 PascalCase，但第三方打包器/转换器可能
                // 输出 camelCase、全小写，甚至把版本号写成 JSON 数值。安装校验
                // 必须与 Mod 管理页的宽松解析规则一致，否则合法 Mod 会被误报
                // 为“缺少 UniqueID/Version”，整合包也会出现假失败。
                var uniqueId = GetManifestValue(root, "UniqueID", "UniqueId", "unique_id");
                var version = GetManifestValue(root, "Version", "version");

                if (string.IsNullOrWhiteSpace(uniqueId))
                {
                    errors.Add($"{modName}: manifest 缺少 UniqueID");
                }
                else if (!seenUniqueIds.Add(uniqueId))
                {
                    errors.Add($"{modName}: UniqueID 重复 ({uniqueId})");
                }

                if (string.IsNullOrWhiteSpace(version))
                {
                    errors.Add($"{modName}: manifest 缺少 Version");
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{modName}: manifest 解析失败 ({ex.Message})");
            }
        }

        return errors.Count == 0
            ? CollectionValidationResult.Empty
            : new CollectionValidationResult(false, errors);
    }

    /// <summary>读取 Mod manifest，兼容 UTF-8/UTF-16 BOM，避免合法清单被误判为损坏。</summary>
    private static string ReadTextFileWithBom(string path)
    {
        return ManifestTextReader.ReadAllText(path);
    }

    private static string GetManifestValue(JsonElement root, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetJsonPropertyIgnoreCase(root, propertyName, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? string.Empty;
            }

            if (value.ValueKind == JsonValueKind.Number)
            {
                return value.ToString();
            }
        }

        return string.Empty;
    }

    private static bool TryGetJsonPropertyIgnoreCase(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private string ResolveTargetModsPath()
    {
        // 优先使用当前选中实例的 Mods 路径（由 DownloadPageViewModel 设置）
        if (CurrentModsPathResolver != null)
        {
            var currentModsPath = CurrentModsPathResolver();
            if (!string.IsNullOrWhiteSpace(currentModsPath))
            {
                Directory.CreateDirectory(currentModsPath);
                return currentModsPath;
            }
        }

        // 回退：自动探测 Steam/GOG 路径
        var gamePath = _gameInstallPathLocator.TryLocateSteamStardewPath()
            ?? _gameInstallPathLocator.TryLocateGogStardewPath()
            ?? _gameInstallPathLocator.TryLocateXboxStardewPath();
        if (!string.IsNullOrWhiteSpace(gamePath))
        {
            var modsPath = Path.Combine(gamePath, "Mods");
            Directory.CreateDirectory(modsPath);
            return modsPath;
        }

        var fallback = Path.Combine(_installRoot, "FallbackMods");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static List<string> DiscoverModDirectories(string extractPath)
    {
        // 旧 Collection 安装入口也必须跳过发行包外层的 Name-only
        // manifest，并在嵌套布局中只返回真正可安装的 Mod 根目录。
        return ModpackInstallService.FindInstallableModDirectories(extractPath).ToList();
    }

    private static string? FindManifestPath(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        var direct = Path.Combine(directory, "manifest.json");
        if (File.Exists(direct))
        {
            return direct;
        }

        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => string.Equals(
                    Path.GetFileName(path),
                    "manifest.json",
                    StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static void CopyDirectory(string sourceDir, string targetDir, bool overwrite)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var directory in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, directory);
            Directory.CreateDirectory(Path.Combine(targetDir, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var targetFile = Path.Combine(targetDir, relative);
            var targetParent = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrWhiteSpace(targetParent))
            {
                Directory.CreateDirectory(targetParent);
            }

            File.Copy(file, targetFile, overwrite);
        }
    }

    private static bool IsArchiveFile(string path)
    {
        return ArchiveExtractor.IsZip(path) ||
               ArchiveExtractor.IsSevenZip(path);
    }

    private static void ExtractArchive(string archivePath, string destinationDirectory)
    {
        if (ArchiveExtractor.IsSevenZip(archivePath))
        {
            ArchiveExtractor.ExtractSevenZipToDirectory(archivePath, destinationDirectory);
            return;
        }

        ZipExtractor.ExtractToDirectory(archivePath, destinationDirectory);
    }

    /// <summary>
    /// 将在线安装任务的来源写入实际 Mod 目录，供版本设置页导出时恢复
    /// CurseForge/Nexus 的 projectId 与 fileId。保留已有的本地化扩展字段。
    /// </summary>
    private static void WriteSourceCredentials(
        string modsPath,
        IReadOnlyList<string> installedNames,
        string? sourcePlatform,
        long? sourceProjectId,
        long? sourceFileId,
        string? sourceDownloadUrl,
        string? sourceFileName,
        string? sourceRepository)
    {
        // 普通在线 Mod、Modpack 和 Collection 可能使用相同的“父 Mod +
        // 多个 ContentPack”归档结构。统一交给来源树写入器，避免普通
        // Mod 下载路径仍把兄弟子 Mod 写成独立来源。
        ModpackInstallService.WriteSourceCredentialForInstalledModWithRepository(
            modsPath,
            installedNames,
            sourcePlatform,
            sourceProjectId,
            sourceFileId,
            sourceDownloadUrl,
            sourceFileName,
            sourceRepository);
    }

    private static string CreateSafeFolderName(string name)
    {
        return InstanceRuntimePathResolver.SanitizeFileNameComponent(name, "download-task");
    }
}

public enum CollectionInstallConflictStrategy
{
    Overwrite,
    Skip,
    BackupOnly
}

public static class CollectionInstallConflictStrategyExtensions
{
    public static CollectionInstallConflictStrategy Parse(string raw)
    {
        return raw switch
        {
            "跳过" => CollectionInstallConflictStrategy.Skip,
            "仅备份" => CollectionInstallConflictStrategy.BackupOnly,
            _ => CollectionInstallConflictStrategy.Overwrite
        };
    }

    public static string ToDisplayName(this CollectionInstallConflictStrategy strategy)
    {
        return strategy switch
        {
            CollectionInstallConflictStrategy.Skip => "跳过",
            CollectionInstallConflictStrategy.BackupOnly => "仅备份",
            _ => "覆盖"
        };
    }
}

public sealed class DownloadInstallResult
{
    public bool IsSuccess { get; init; }

    public bool IsCancelled { get; init; }

    public string Message { get; init; } = string.Empty;

    public string InstalledPath { get; init; } = string.Empty;

    public IReadOnlyList<string> InstalledItems { get; init; } = [];

    public string BackupPath { get; init; } = string.Empty;

    public string ReportPath { get; init; } = string.Empty;

    public bool ValidationPassed { get; init; } = true;

    public IReadOnlyList<string> ValidationErrors { get; init; } = [];

    public static DownloadInstallResult Success(
        string installedPath,
        IReadOnlyList<string>? installedItems = null,
        string backupPath = "",
        string reportPath = "",
        bool validationPassed = true,
        IReadOnlyList<string>? validationErrors = null)
    {
        return new DownloadInstallResult
        {
            IsSuccess = true,
            Message = "安装成功",
            InstalledPath = installedPath,
            InstalledItems = installedItems ?? [],
            BackupPath = backupPath,
            ReportPath = reportPath,
            ValidationPassed = validationPassed,
            ValidationErrors = validationErrors ?? []
        };
    }

    public static DownloadInstallResult Failed(string message)
    {
        return new DownloadInstallResult
        {
            IsSuccess = false,
            IsCancelled = false,
            Message = message
        };
    }

    public static DownloadInstallResult Cancelled(string message)
    {
        return new DownloadInstallResult
        {
            IsSuccess = false,
            IsCancelled = true,
            Message = message
        };
    }
}

internal sealed class CollectionInstallReport
{
    public string TaskName { get; init; } = string.Empty;

    public DateTime CreatedAtUtc { get; init; }

    public string TargetModsPath { get; init; } = string.Empty;

    public string ConflictStrategy { get; init; } = "覆盖";

    public string BackupPath { get; init; } = string.Empty;

    public bool ValidationPassed { get; init; } = true;

    public List<string> ValidationErrors { get; init; } = [];

    public List<string> PreviewMismatchItems { get; init; } = [];

    public int OverwrittenCount { get; init; }

    public int SkippedCount { get; init; }

    public int BackupOnlyCount { get; init; }

    public int AddedCount { get; init; }

    public bool RollbackApplied { get; init; }

    public List<string> RollbackErrors { get; init; } = [];

    public List<string> ConflictPreviewItems { get; init; } = [];

    public List<string> SourceFiles { get; init; } = [];

    public List<string> InstalledItems { get; init; } = [];
}

internal readonly record struct CollectionValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static CollectionValidationResult Empty => new(true, []);
}

public sealed class CollectionConflictPreviewItem
{
    public string ModName { get; init; } = string.Empty;

    public bool Exists { get; init; }

    public string PlannedAction { get; init; } = "新增安装";
}
