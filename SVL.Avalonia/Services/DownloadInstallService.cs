using SVL.Core.Platform.Abstractions;
using System.IO.Compression;
using System.Text.Json;

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

    public async Task<DownloadInstallResult> InstallAsync(string downloadedFilePath, string taskName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(downloadedFilePath) || !File.Exists(downloadedFilePath))
        {
            return DownloadInstallResult.Failed("下载文件不存在，无法安装");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var targetModsPath = ResolveTargetModsPath();
        var safeName = CreateSafeFolderName(taskName);
        var installPath = Path.Combine(targetModsPath, safeName);

        try
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (Directory.Exists(installPath))
                {
                    Directory.Delete(installPath, true);
                }

                if (IsZipFile(downloadedFilePath))
                {
                    Directory.CreateDirectory(installPath);
                    ZipExtractor.ExtractToDirectory(downloadedFilePath, installPath);
                    return;
                }

                Directory.CreateDirectory(installPath);
                var targetFile = Path.Combine(installPath, Path.GetFileName(downloadedFilePath));
                File.Copy(downloadedFilePath, targetFile, true);
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

        return DownloadInstallResult.Success(installPath, [safeName]);
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

                    if (!IsZipFile(file))
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
                    ZipExtractor.ExtractToDirectory(file, extractDir);

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

                        var modName = Path.GetFileName(modDir);
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

                            Directory.Delete(targetModDir, true);
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
                    if (string.IsNullOrWhiteSpace(file) || !File.Exists(file) || !IsZipFile(file))
                    {
                        continue;
                    }

                    var extractDir = Path.Combine(previewRoot, $"part-{i + 1}");
                    Directory.CreateDirectory(extractDir);
                    ZipExtractor.ExtractToDirectory(file, extractDir);

                    foreach (var modDir in DiscoverModDirectories(extractDir))
                    {
                        discoveredMods.Add(Path.GetFileName(modDir));
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
                    Directory.Delete(targetModDir, true);
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
                    Directory.Delete(targetModDir, true);
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
            var manifestPath = Path.Combine(modDir, "manifest.json");

            if (!File.Exists(manifestPath))
            {
                errors.Add($"{modName}: 缺少 manifest.json");
                continue;
            }

            try
            {
                using var stream = File.OpenRead(manifestPath);
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;

                var uniqueId = root.TryGetProperty("UniqueID", out var uniqueIdProp)
                    ? uniqueIdProp.GetString() ?? string.Empty
                    : string.Empty;
                var version = root.TryGetProperty("Version", out var versionProp)
                    ? versionProp.GetString() ?? string.Empty
                    : string.Empty;

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
        var gamePath = _gameInstallPathLocator.TryLocateSteamStardewPath() ?? _gameInstallPathLocator.TryLocateGogStardewPath();
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
        var manifests = Directory
            .EnumerateFiles(extractPath, "manifest.json", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return manifests;
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

    private static bool IsZipFile(string path)
    {
        var ext = Path.GetExtension(path);
        return string.Equals(ext, ".zip", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".cfmodpack", StringComparison.OrdinalIgnoreCase);
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
