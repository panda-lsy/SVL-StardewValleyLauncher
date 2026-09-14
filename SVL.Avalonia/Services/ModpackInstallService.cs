using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using SVL.Core.Platform.Abstractions;
using SVL.Core.Platform.IO;

namespace SVL.Avalonia.Services;

/// <summary>整合包安装进度回调。</summary>
public sealed class ModpackInstallProgress
{
    /// <summary>总体进度百分比（0-100）。</summary>
    public int Percent { get; set; }

    /// <summary>当前步骤描述（如 "步骤 2/6: 安装 SMAPI"）。</summary>
    public string StepText { get; set; } = string.Empty;

    /// <summary>子进度文本（如 "3/93 已完成"），空表示无子进度。</summary>
    public string SubProgressText { get; set; } = string.Empty;

    /// <summary>子进度百分比（0-100），-1 表示无子进度。</summary>
    public int SubProgress { get; set; } = -1;
}

/// <summary>整合包安装结果。</summary>
public sealed class ModpackInstallResult
{
    public bool IsSuccess { get; init; }
    public bool IsCancelled { get; init; }
    public string Message { get; init; } = string.Empty;
    public string RuntimePath { get; init; } = string.Empty;
    public string VersionRootPath { get; init; } = string.Empty;
    public List<string> FailedMods { get; init; } = [];
    public List<string> InstalledMods { get; init; } = [];

    public static ModpackInstallResult Success(string runtimePath, string versionRootPath, List<string> installedMods, List<string>? failedMods = null) => new()
    {
        IsSuccess = true,
        Message = "整合包安装完成",
        RuntimePath = runtimePath,
        VersionRootPath = versionRootPath,
        InstalledMods = installedMods,
        FailedMods = failedMods ?? []
    };

    public static ModpackInstallResult Failed(string message, List<string>? failedMods = null) => new()
    {
        IsSuccess = false,
        Message = message,
        FailedMods = failedMods ?? []
    };

    public static ModpackInstallResult Cancelled(string message) => new()
    {
        IsSuccess = false,
        IsCancelled = true,
        Message = message
    };
}

/// <summary>
/// 整合包安装服务。承载 SVL 6 步流程和 Curseforge manifest 流程。
/// 对齐旧架构 SvlModpackInstallTask（6 步）和 CurseforgeModpackDownloadTask（4 步安装）。
/// </summary>
public sealed class ModpackInstallService
{
    // Modpack 与 Collection 安装可能由统一下载队列并行触发，但两者会共用
    // %TEMP%/SVL/smapi/SMAPI-<version>.zip。这里必须按“版本”而不是按 Base
    // 路径加锁：不同 Base 仍会写入同一个全局临时文件，按 Base 分锁无法阻止
    // 一个任务清理或覆盖另一个任务正在校验的 SMAPI 包。
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SmapiResolveLocks = new(StringComparer.OrdinalIgnoreCase);

    // Mod 作者常用的 JSON 编辑器会留下注释或尾逗号；旧 WPF 读取链路对此兼容，
    // Avalonia 导入也必须保持一致，否则安装成功后 Mod 管理页仍会读不到 manifest。
    private static readonly JsonDocumentOptions ManifestJsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static readonly JsonSerializerOptions CurseforgeManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private sealed class ModSourceDownloadResult
    {
        public bool IsSuccess { get; init; }
        public string Message { get; init; } = string.Empty;

        public static ModSourceDownloadResult Success() => new() { IsSuccess = true };

        public static ModSourceDownloadResult Failed(string message) => new()
        {
            Message = string.IsNullOrWhiteSpace(message) ? "未知下载错误" : message
        };
    }

    /// <summary>
    /// 统一表示 sources.json 中的来源凭证。
    /// 历史版本同时存在嵌套 source 对象、source 字符串以及顶层字段三种形态，
    /// 导入端不能把格式差异当成“没有下载来源”。
    /// </summary>
    private sealed record ModSourceDescriptor(
        string? Platform,
        string? DownloadUrl,
        string? FileName,
        string? ModId,
        string? ProjectId,
        string? FileId,
        string? Repository);

    private readonly IGameInstallPathLocator _gameInstallPathLocator;
    private readonly ISmapiInstallService _smapiInstallService;
    private readonly HttpDownloadService _httpDownloadService;
    private readonly RemoteCatalogService _remoteCatalogService;
    private readonly AppUserSettingsStore _settingsStore;
    private readonly NexusModDownloadResolverService _nexusResolver;
    private readonly INxmLinkParser _nxmLinkParser;
    private readonly BrowserDownloadFallbackService? _browserFallback;

    // SMAPI 相关目录（解压 mods/ 时跳过）
    // 对齐旧架构 SvlModpackInstallTask.SmapiRelatedDirs，覆盖安装器目录和附带模组目录
    private static readonly HashSet<string> SmapiRelatedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "StardewModdingAPI", "StardewModdingAPI.Toolkit", "smapi-internal",
        "SMAPI Installer", "SMAPI", "SMAPIInstaller",
        "SMAPI.ConsoleCommands", "ConsoleCommands",
        "SMAPI.SaveBackup", "SaveBackup",
        "SMAPI.ErrorHandler", "ErrorHandler"
    };

    public ModpackInstallService(
        IGameInstallPathLocator gameInstallPathLocator,
        ISmapiInstallService smapiInstallService,
        HttpDownloadService httpDownloadService,
        RemoteCatalogService remoteCatalogService,
        AppUserSettingsStore settingsStore,
        NexusModDownloadResolverService nexusResolver,
        INxmLinkParser nxmLinkParser,
        BrowserDownloadFallbackService? browserFallback = null)
    {
        _gameInstallPathLocator = gameInstallPathLocator;
        _smapiInstallService = smapiInstallService;
        _httpDownloadService = httpDownloadService;
        _remoteCatalogService = remoteCatalogService;
        _settingsStore = settingsStore;
        _nexusResolver = nexusResolver;
        _nxmLinkParser = nxmLinkParser;
        _browserFallback = browserFallback;
    }

    // ================================================================
    // SVL 整合包 6 步流程
    // ================================================================

    /// <summary>
    /// 安装 SVL 格式整合包（modpack.json）。6 步流程对齐旧 SvlModpackInstallTask。
    /// </summary>
    /// <param name="zipPath">整合包 zip 文件路径（或含 modpack.json 的已解压目录）。</param>
    /// <param name="instanceName">版本隔离实例名。</param>
    /// <param name="targetGamePath">用户指定的目标游戏路径（来自路径列表）。空则自动探测。</param>
    /// <param name="onProgress">进度回调。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="customIconPath">整合包旁路图标路径（如导出包同目录的 icon.png）。</param>
    public async Task<ModpackInstallResult> InstallSvlModpackAsync(
        string zipPath,
        string instanceName,
        string targetGamePath,
        Action<ModpackInstallProgress>? onProgress,
        CancellationToken cancellationToken = default,
        string? customIconPath = null,
        bool updateExisting = false)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
        {
            return ModpackInstallResult.Failed("整合包文件不存在");
        }

        if (string.IsNullOrWhiteSpace(instanceName))
        {
            instanceName = Path.GetFileNameWithoutExtension(zipPath);
        }

        var instanceNameValidation = InstanceNameValidator.Validate(instanceName);
        if (!instanceNameValidation.IsValid)
        {
            return ModpackInstallResult.Failed($"实例名称无效: {instanceNameValidation.ErrorMessage}");
        }
        instanceName = instanceName.Trim();

        var gamePath = ResolveTargetGamePath(targetGamePath);
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            return ModpackInstallResult.Failed("未检测到游戏目录，无法安装整合包");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "SVL", "modpack_install", Guid.NewGuid().ToString());
        var installedMods = new List<string>();
        var failedMods = new List<string>();

        try
        {
            // ===== 步 1: 读取 modpack.json 清单 =====
            onProgress?.Invoke(new ModpackInstallProgress { Percent = 2, StepText = "步骤 1/6: 读取清单" });
            cancellationToken.ThrowIfCancellationRequested();

            Directory.CreateDirectory(tempDir);
            ExtractPackageArchive(zipPath, tempDir);

            // 支持三类实际导出结构：平铺包、外层目录包、SVL.exe + modpack.zip。
            // 后续所有资源都以 manifest 所在目录为根，避免只找到清单却仍从外层
            // 查找 mods/settings/sources，导致整合包“安装完成但 Mod 全部丢失”。
            var packageRoot = PrepareSvlPackageRoot(tempDir);
            var modpackJsonPath = FindValidSvlManifestFile(packageRoot) ?? string.Empty;

            if (!File.Exists(modpackJsonPath))
            {
                return ModpackInstallResult.Failed("整合包缺少 modpack.json，无法识别为 SVL 格式");
            }

            string? smapiVersion;
            string? modpackName;
            List<JsonElement> manifestModEntries = [];
            using (var doc = JsonDocument.Parse(
                       await ReadTextFileWithBomAsync(modpackJsonPath, cancellationToken),
                       ManifestJsonOptions))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return ModpackInstallResult.Failed("modpack.json 格式无效，根节点必须是对象");
                }

                smapiVersion = GetJsonString(doc.RootElement, "smapi_version", "smapiVersion");
                modpackName = GetJsonString(doc.RootElement, "name", "title");
                if (TryGetJsonPropertyIgnoreCase(doc.RootElement, "mods", out var manifestMods) &&
                    manifestMods.ValueKind == JsonValueKind.Array)
                {
                    // JsonDocument 在此 if 作用域结束时释放，必须 Clone 后再交给
                    // sources.json 缺失时的兼容回退逻辑。
                    manifestModEntries = manifestMods.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.Object)
                        .Select(item => item.Clone())
                        .ToList();
                }
            }

            // 读取 sources.json（步 4 用）
            List<JsonElement> sourcesList = [];
            var sourcesJsonPath = FindFileInDirectory(packageRoot, "sources.json");
            if (!string.IsNullOrWhiteSpace(sourcesJsonPath))
            {
                var sourcesJson = await ReadTextFileWithBomAsync(sourcesJsonPath, cancellationToken);
                using var sourcesDoc = JsonDocument.Parse(sourcesJson, ManifestJsonOptions);
                sourcesList = ParseSourceEntries(sourcesDoc.RootElement);
            }

            // 某些早期/第三方 SVL 导出把来源字段直接放在 modpack.json 的 mods
            // 条目里，而不是单独生成 sources.json。仅在 sources.json 没有提供
            // 条目时回退，避免两个清单重复下载同一个 Mod。
            if (sourcesList.Count == 0 && manifestModEntries.Count > 0)
            {
                sourcesList = manifestModEntries;
            }
            else if (sourcesList.Count > 0 && manifestModEntries.Count > 0)
            {
                // 某些导出器会同时生成两份清单：sources.json 只有名称/目录名，
                // 而 modpack.json.mods 才保留完整的 platform/projectId/fileId。
                // 不能因为 sources.json 非空就完全忽略后者，否则旧包会把本来
                // 可以安装的 Mod 误判为“缺少来源”。按稳定身份合并，并只补缺失
                // 字段；这样不会让较新的 sources.json 被旧清单覆盖。
                sourcesList = MergeSourceEntriesWithManifest(
                    sourcesList,
                    manifestModEntries);
            }

            onProgress?.Invoke(new ModpackInstallProgress { Percent = 5, StepText = "步骤 1/6: 清单读取完成" });

            // ===== 步 2: 安装 SMAPI =====
            onProgress?.Invoke(new ModpackInstallProgress { Percent = 8, StepText = "步骤 2/6: 安装 SMAPI" });
            cancellationToken.ThrowIfCancellationRequested();

            // 整合包中的 SMAPI 相关目录只是安装包/附带 Mod，不能替代运行时安装。
            // 始终通过经过校验的 SMAPI 安装包创建可启动的隔离实例。
            var smapiZipPath = await ResolveSmapiZipAsync(
                smapiVersion,
                instanceName,
                gamePath,
                onProgress,
                cancellationToken,
                allowCurrentInstance: updateExisting);
            if (string.IsNullOrWhiteSpace(smapiZipPath))
            {
                return ModpackInstallResult.Failed("无法获取经过校验的 SMAPI 安装包，已停止整合包安装");
            }

            var smapiResult = await _smapiInstallService.InstallFromZipAsync(
                smapiZipPath,
                gamePath,
                instanceName,
                cancellationToken: cancellationToken,
                updateExisting: updateExisting);
            if (!smapiResult.IsSuccess)
            {
                return smapiResult.IsCancelled
                    ? ModpackInstallResult.Cancelled($"SMAPI 安装已取消: {smapiResult.Message}")
                    : ModpackInstallResult.Failed($"SMAPI 安装失败: {smapiResult.Message}");
            }

            var versionRoot = Path.Combine(gamePath, "versions", instanceName);
            var runtimePath = InstanceRuntimePathResolver.Resolve(versionRoot);
            var modsPath = Path.Combine(runtimePath, "Mods");
            Directory.CreateDirectory(modsPath);

            onProgress?.Invoke(new ModpackInstallProgress { Percent = 20, StepText = "步骤 2/6: SMAPI 就绪" });

            // ===== 步 3: 解压 mods/ =====
            onProgress?.Invoke(new ModpackInstallProgress { Percent = 22, StepText = "步骤 3/6: 解压 Mod 文件" });
            cancellationToken.ThrowIfCancellationRequested();

            var bundledModNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var modsSourceDir = FindDirectoryByNameIgnoreCase(packageRoot, "mods");

            void InstallBundledModOrRecordFailure(string modDirectory)
            {
                var modName = Path.GetFileName(modDirectory);
                if (InstallBundledModDirectory(
                        modDirectory,
                        modsPath,
                        modName,
                        out var installedNames,
                        out var failedNames))
                {
                    foreach (var installedName in installedNames)
                    {
                        bundledModNames.Add(installedName);
                        installedMods.Add(installedName);
                    }
                }

                // 之前这里只忽略 false，导致内置 Mod 没有 manifest、manifest 损坏或
                // 复制失败时，整合包仍会显示“安装完成”且失败列表为空。保留实例收尾，
                // 但把该条目纳入失败统计，便于任务页重试/报告。
                if (failedNames.Count == 0 && installedNames.Count == 0)
                {
                    failedNames.Add(modName);
                }

                foreach (var failedName in failedNames.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    failedMods.Add($"{failedName}: {GetBundledModInstallFailureReason(modDirectory)}");
                }
            }

            if (!string.IsNullOrWhiteSpace(modsSourceDir))
            {
                foreach (var modDir in Directory.GetDirectories(modsSourceDir))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var modName = Path.GetFileName(modDir);
                    if (SmapiRelatedDirs.Contains(modName)) continue;

                    InstallBundledModOrRecordFailure(modDir);
                }

                // 兼容旧格式：mods/ 无子目录时直接解压根目录的 mod 文件
                var rootModFiles = Directory.EnumerateFiles(modsSourceDir, "*", SearchOption.TopDirectoryOnly)
                    .Where(path => string.Equals(
                        Path.GetFileName(path), "manifest.json", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (rootModFiles.Length > 0 && Directory.GetDirectories(modsSourceDir).Length == 0)
                {
                    foreach (var manifestFile in rootModFiles)
                    {
                        var modDir = Path.GetDirectoryName(manifestFile)!;
                        InstallBundledModOrRecordFailure(modDir);
                    }
                }
            }
            else
            {
                // 兼容上游旧版 ModpackManager：Mod 目录直接位于压缩包根目录，
                // 而不是 mods/ 下（manifest.json/files.json 是包元数据）。
                foreach (var modDir in EnumerateLegacyBundledModDirectories(packageRoot))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    InstallBundledModOrRecordFailure(modDir);
                }
            }

            onProgress?.Invoke(new ModpackInstallProgress
            {
                Percent = 45,
                StepText = failedMods.Count > 0
                    ? $"步骤 3/6: 已解压 {bundledModNames.Count} 个 Mod，失败 {failedMods.Count} 个"
                    : $"步骤 3/6: 已解压 {bundledModNames.Count} 个 Mod"
            });

            // ===== 步 4: 下载 sources.json 中的未打包 Mod =====
            onProgress?.Invoke(new ModpackInstallProgress { Percent = 47, StepText = "步骤 4/6: 下载未打包 Mod" });
            cancellationToken.ThrowIfCancellationRequested();

            var downloadCandidates = sourcesList
                .Where(s => s.ValueKind == JsonValueKind.Object)
                .Where(s => !string.IsNullOrWhiteSpace(GetJsonString(
                    s,
                    "name",
                    "modName",
                    "id",
                    "uniqueId",
                    "directoryName",
                    "directory")))
                .Where(s => !TryGetJsonPropertyIgnoreCase(s, "bundled", out var bundled) ||
                            bundled.ValueKind != JsonValueKind.True)
                .Where(s => !TryGetJsonPropertyIgnoreCase(s, "enabled", out var enabled) || enabled.ValueKind != JsonValueKind.False)
                .ToList();

            // bundled/已存在的 Mod 不会进入下载队列，不能计入“待下载总数”；
            // 否则这些条目被跳过后，界面会一直显示未完成的数量。
            var downloableMods = new List<JsonElement>();
            foreach (var source in downloadCandidates)
            {
                var modName = GetJsonString(
                    source,
                    "name",
                    "modName",
                    "id",
                    "uniqueId",
                    "directoryName",
                    "directory") ?? "unknown";
                var sourceDirectoryName = GetJsonString(source, "directoryName");
                var expectedDirectoryName = SanitizeModDirectoryName(sourceDirectoryName, modName);
                if (bundledModNames.Contains(expectedDirectoryName) ||
                    IsInstalledModDirectory(Path.Combine(modsPath, expectedDirectoryName)) ||
                    IsMatchingInstalledMod(modsPath, source, expectedDirectoryName))
                {
                    bundledModNames.Add(expectedDirectoryName);
                    continue;
                }

                downloableMods.Add(source);
            }

            var totalToDownload = downloableMods.Count;
            var downloadedCount = 0;
            onProgress?.Invoke(new ModpackInstallProgress
            {
                Percent = 47,
                StepText = "步骤 4/6: 下载未打包 Mod",
                SubProgress = totalToDownload > 0 ? 0 : -1,
                SubProgressText = totalToDownload > 0
                    ? $"0/{totalToDownload} 已处理，剩余 {totalToDownload} 个 Mod"
                    : "没有待下载 Mod"
            });

            foreach (var source in downloableMods)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var modName = GetJsonString(
                    source,
                    "name",
                    "modName",
                    "id",
                    "uniqueId",
                    "directoryName",
                    "directory") ?? "unknown";

                try
                {
                    // 无来源条目不能在候选阶段直接过滤掉，否则导出包重新导入后
                    // 会被误计为“没有待下载 Mod”，最终只显示安装完成而不提示丢失项。
                    // 在处理阶段显式失败，用户才能知道需要补充哪个 Mod 的来源。
                    var downloadResult = !HasActionableDownloadSource(source)
                        ? ModSourceDownloadResult.Failed("缺少可用下载来源（请补充平台、项目 ID/FileID 或直链）")
                        : await DownloadModFromSourceWithRetryAsync(source, modsPath, cancellationToken, onProgress);
                    if (downloadResult.IsSuccess)
                    {
                        installedMods.Add(modName);
                    }
                    else
                    {
                        failedMods.Add($"{modName}: {downloadResult.Message}");
                        onProgress?.Invoke(new ModpackInstallProgress
                        {
                            Percent = 47 + (int)(18.0 * downloadedCount / Math.Max(1, totalToDownload)),
                            StepText = "步骤 4/6: 下载未打包 Mod",
                            SubProgress = CalculateCompletionPercent(downloadedCount, totalToDownload),
                            SubProgressText = $"{modName}: 下载失败（{downloadResult.Message}）"
                        });
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failedMods.Add($"{modName}: {ex.Message}");
                    onProgress?.Invoke(new ModpackInstallProgress
                    {
                        Percent = 47 + (int)(18.0 * downloadedCount / Math.Max(1, totalToDownload)),
                        StepText = "步骤 4/6: 下载未打包 Mod",
                        SubProgress = CalculateCompletionPercent(downloadedCount, totalToDownload),
                        SubProgressText = $"跳过 {modName}: {ex.Message}"
                    });
                }

                downloadedCount++;
                onProgress?.Invoke(new ModpackInstallProgress
                {
                    Percent = 47 + (int)(18.0 * downloadedCount / Math.Max(1, totalToDownload)),
                    StepText = "步骤 4/6: 下载未打包 Mod",
                    SubProgress = CalculateCompletionPercent(downloadedCount, totalToDownload),
                    SubProgressText = $"{downloadedCount}/{totalToDownload} 已处理，剩余 {Math.Max(0, totalToDownload - downloadedCount)} 个 Mod"
                });
            }

            onProgress?.Invoke(new ModpackInstallProgress
            {
                Percent = 65,
                StepText = $"步骤 4/6: 下载完成（{failedMods.Count} 个失败）",
                // “已处理”包含失败项；只要仍有失败，整合包就没有完整完成，
                // 子进度不能提前渲染满格，避免用户把“全部尝试过”误看成“全部安装成功”。
                SubProgress = CalculateFinalSubProgress(totalToDownload, failedMods.Count),
                SubProgressText = totalToDownload > 0
                    ? failedMods.Count > 0
                        ? $"已处理 {downloadedCount} 个 Mod，其中失败 {failedMods.Count} 个"
                        : $"已处理 {downloadedCount} 个 Mod，剩余 0 个 Mod"
                    : "没有待下载 Mod"
            });

            // ===== 步 5: 应用 settings/ =====
            onProgress?.Invoke(new ModpackInstallProgress { Percent = 67, StepText = "步骤 5/6: 应用配置覆盖" });
            cancellationToken.ThrowIfCancellationRequested();

            var settingsDir = FindDirectoryByNameIgnoreCase(packageRoot, "settings");
            if (!string.IsNullOrWhiteSpace(settingsDir))
            {
                foreach (var file in Directory.GetFiles(settingsDir, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(settingsDir, file);
                    // 新格式是 settings/Mods/<Mod>/...；上游旧格式是
                    // settings/<Mod>/...，两者都要覆盖到 runtimePath/Mods 下。
                    var targetRelative = relative;
                    var firstSegment = relative
                        .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault();
                    if (!string.Equals(firstSegment, "Mods", StringComparison.OrdinalIgnoreCase))
                    {
                        targetRelative = Path.Combine("Mods", relative);
                    }

                    // 导出清单记录的是导出时的目录名，但 Nexus/CurseForge
                    // 压缩包安装后会以 manifest.json 所在目录为准整理目录。
                    // 例如设置位于 settings/Mods/Actual Mod，而实际 Mod 目录
                    // 是 ActualMod；直接复制会产生第二个“无 manifest”目录，导致
                    // 配置看似写入成功但游戏实际读不到。
                    var relativeSegments = relative
                        .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
                    var settingsModIndex = string.Equals(firstSegment, "Mods", StringComparison.OrdinalIgnoreCase)
                        ? 1
                        : 0;
                    var hasSettingsMod = string.Equals(firstSegment, "Mods", StringComparison.OrdinalIgnoreCase)
                        ? relativeSegments.Length > 1
                        : relativeSegments.Length > 0;
                    if (hasSettingsMod)
                    {
                        var configuredModName = relativeSegments[settingsModIndex];
                        var actualModDirectory = ResolveInstalledModSettingsDirectory(
                            modsPath,
                            configuredModName);
                        if (!string.IsNullOrWhiteSpace(actualModDirectory))
                        {
                            var remainingSegments = relativeSegments[(settingsModIndex + 1)..];
                            targetRelative = remainingSegments.Length == 0
                                ? Path.Combine("Mods", actualModDirectory)
                                : Path.Combine(
                                    new[] { "Mods", actualModDirectory }.Concat(remainingSegments).ToArray());
                        }
                    }

                    var target = Path.Combine(runtimePath, targetRelative);
                    var targetParent = Path.GetDirectoryName(target);
                    if (!string.IsNullOrWhiteSpace(targetParent)) Directory.CreateDirectory(targetParent);
                    File.Copy(file, target, true);
                }
            }

            onProgress?.Invoke(new ModpackInstallProgress { Percent = 80, StepText = "步骤 5/6: 配置覆盖完成" });

            // ===== 步 6: 保存实例配置 =====
            onProgress?.Invoke(new ModpackInstallProgress { Percent = 82, StepText = "步骤 6/6: 保存实例配置" });
            cancellationToken.ThrowIfCancellationRequested();

            // 写 svl-source.json 到各 mod 目录（从 sources.json 中的 source 字段恢复）
            WriteSourceCredentials(sourcesList, modsPath);
            // 导入完成后立即修复旧导出/部分写入造成的复合来源断链，
            // 不要求用户先离开再重新进入 Mod 管理页才能看到正确状态。
            RepairCompositeSourceCredentials(modsPath);

            // 提取整合包图标
            var iconPath = ExtractPackIcon(packageRoot, versionRoot, customIconPath, tempDir);

            // 保存实例到 InstanceRegistryStore
            SaveInstanceRecord(instanceName, runtimePath);

            // 还有失败 Mod 时，流程虽已完成收尾，但整合包并未完整安装。
            // 保持 99% 能让任务页明确区分“部分完成”和真正的 100% 完成。
            var finalPercent = failedMods.Count > 0 ? 99 : 100;
            onProgress?.Invoke(new ModpackInstallProgress
            {
                Percent = finalPercent,
                StepText = failedMods.Count > 0
                    ? $"步骤 6/6: 安装完成（{failedMods.Count} 个 Mod 失败，可重试）"
                    : "步骤 6/6: 安装完成"
            });

            return ModpackInstallResult.Success(runtimePath, versionRoot, installedMods, failedMods);
        }
        catch (OperationCanceledException)
        {
            if (!updateExisting)
            {
                CleanupVersionDirectory(gamePath, instanceName);
            }

            return ModpackInstallResult.Cancelled("整合包安装已取消");
        }
        catch (Exception ex)
        {
            if (!updateExisting)
            {
                CleanupVersionDirectory(gamePath, instanceName);
            }

            return ModpackInstallResult.Failed($"整合包安装失败: {ex.Message}", failedMods);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    // ================================================================
    // Curseforge 整合包流程
    // ================================================================

    /// <summary>
    /// 安装 Curseforge 格式整合包（manifest.json）。4 步安装对齐旧 CurseforgeModpackDownloadTask。
    /// </summary>
    /// <param name="zipPath">整合包 zip 文件路径。</param>
    /// <param name="instanceName">版本隔离实例名。</param>
    /// <param name="targetGamePath">用户指定的目标游戏路径（来自路径列表）。空则自动探测。</param>
    /// <param name="onProgress">进度回调。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="customIconPath">整合包旁路图标路径（如导出包同目录的 icon.png）。</param>
    public async Task<ModpackInstallResult> InstallCurseforgeModpackAsync(
        string zipPath,
        string instanceName,
        string targetGamePath,
        Action<ModpackInstallProgress>? onProgress,
        CancellationToken cancellationToken = default,
        string? customIconPath = null,
        bool updateExisting = false)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
        {
            return ModpackInstallResult.Failed("整合包文件不存在");
        }

        if (string.IsNullOrWhiteSpace(instanceName))
        {
            instanceName = Path.GetFileNameWithoutExtension(zipPath);
        }

        var instanceNameValidation = InstanceNameValidator.Validate(instanceName);
        if (!instanceNameValidation.IsValid)
        {
            return ModpackInstallResult.Failed($"实例名称无效: {instanceNameValidation.ErrorMessage}");
        }
        instanceName = instanceName.Trim();

        var gamePath = ResolveTargetGamePath(targetGamePath);
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            return ModpackInstallResult.Failed("未检测到游戏目录，无法安装整合包");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "SVL", "modpack_install", Guid.NewGuid().ToString());
        var installedMods = new List<string>();
        var failedMods = new List<string>();

        try
        {
            // ===== 阶段 1: 解析 manifest + 解压 =====
            onProgress?.Invoke(new ModpackInstallProgress { Percent = 2, StepText = "解析 manifest 并解压" });
            cancellationToken.ThrowIfCancellationRequested();

            Directory.CreateDirectory(tempDir);
            ExtractPackageArchive(zipPath, tempDir);

            var manifestPath = FindValidCurseforgeManifestFile(tempDir) ?? string.Empty;
            var packageRoot = string.IsNullOrWhiteSpace(manifestPath)
                ? tempDir
                : Path.GetDirectoryName(manifestPath) ?? tempDir;

            if (!File.Exists(manifestPath))
            {
                return ModpackInstallResult.Failed("整合包缺少 manifest.json，无法识别为 Curseforge 格式");
            }

            var manifest = JsonSerializer.Deserialize<CurseforgeManifest>(
                await ReadTextFileWithBomAsync(manifestPath, cancellationToken),
                CurseforgeManifestJsonOptions);

            if (manifest == null)
            {
                return ModpackInstallResult.Failed("manifest.json 解析失败");
            }

            // CurseForge 允许整合包只包含 overrides，或把 files 显式写成 null。
            // 这两种情况都不是清单损坏：应继续执行 SMAPI/overrides 阶段，
            // 不能因为没有可下载 Mod 就在清单阶段提前失败。
            manifest.Files ??= [];

            onProgress?.Invoke(new ModpackInstallProgress { Percent = 5, StepText = $"manifest 解析完成: {manifest.Files.Count} 个文件" });

            // ===== 阶段 2: 安装 SMAPI =====
            onProgress?.Invoke(new ModpackInstallProgress { Percent = 8, StepText = "安装 SMAPI" });
            cancellationToken.ThrowIfCancellationRequested();

            // CurseForge 的 manifest 通常会把 SMAPI 自身作为一条 ProjectID=898372
            // 的文件记录。优先使用这条稳定的 ProjectID/FileID 下载或缓存，避免
            // SMAPI 版本目录暂时不可用时整合包被错误地判定为无法安装；失败后仍
            // 回退到通用的版本目录/已有实例/官方 latest 链路。
            var manifestSmapiFile = manifest.Files.FirstOrDefault(file =>
                file.ProjectId == 898372 && file.FileId > 0);
            string? smapiZipPath = null;
            if (manifestSmapiFile != null)
            {
                // 显式 FileID 的临时归档路径是全局共享的；多个整合包并行导入时
                // 必须和通用 SMAPI 解析一样串行化检查、下载和归一化。
                var manifestSmapiLock = GetSmapiResolveLock(
                    gamePath,
                    $"curseforge-file-{manifestSmapiFile.FileId}");
                await manifestSmapiLock.WaitAsync(cancellationToken);
                try
                {
                    smapiZipPath = await ResolveCurseforgeSmapiArchiveAsync(
                        manifestSmapiFile.FileId,
                        onProgress,
                        cancellationToken);
                }
                finally
                {
                    manifestSmapiLock.Release();
                }
            }

            smapiZipPath ??= await ResolveSmapiZipAsync(
                null,
                instanceName,
                gamePath,
                onProgress,
                cancellationToken,
                allowCurrentInstance: updateExisting);
            if (string.IsNullOrWhiteSpace(smapiZipPath))
            {
                return ModpackInstallResult.Failed("无法获取经过校验的 SMAPI 安装包，已停止整合包安装");
            }

            var smapiResult = await _smapiInstallService.InstallFromZipAsync(
                smapiZipPath,
                gamePath,
                instanceName,
                cancellationToken: cancellationToken,
                updateExisting: updateExisting);
            if (!smapiResult.IsSuccess)
            {
                return smapiResult.IsCancelled
                    ? ModpackInstallResult.Cancelled($"SMAPI 安装已取消: {smapiResult.Message}")
                    : ModpackInstallResult.Failed($"SMAPI 安装失败: {smapiResult.Message}");
            }

            var versionRoot = Path.Combine(gamePath, "versions", instanceName);
            var runtimePath = InstanceRuntimePathResolver.Resolve(versionRoot);
            var modsPath = Path.Combine(runtimePath, "Mods");
            Directory.CreateDirectory(modsPath);

            onProgress?.Invoke(new ModpackInstallProgress { Percent = 15, StepText = "SMAPI 就绪" });

            // ===== 阶段 3: 并发下载 manifest 中的 mods =====
            onProgress?.Invoke(new ModpackInstallProgress { Percent = 17, StepText = "下载 Mod 文件" });
            cancellationToken.ThrowIfCancellationRequested();

            // CurseForge manifest 会把 SMAPI 本身也列在 files 中，但 SMAPI 已在
            // 上一阶段由独立安装流程处理；required=false 的条目则是用户可选的
            // Mod，不能因为没有下载它们就把整合包标成失败。旧 WPF 流程明确跳过
            // 这两类条目，Avalonia 也必须保持相同语义。
            var installableFiles = manifest.Files
                .Where(file => file.Required && file.ProjectId != 898372)
                .ToList();
            var skippedManifestFiles = manifest.Files.Count - installableFiles.Count;
            var totalMods = installableFiles.Count;
            var completedMods = 0;
            onProgress?.Invoke(new ModpackInstallProgress
            {
                Percent = 17,
                StepText = "下载 Mod 文件",
                SubProgress = 0,
                SubProgressText = skippedManifestFiles > 0
                    ? $"0/{totalMods} 已完成，剩余 {totalMods} 个 Mod（跳过 {skippedManifestFiles} 个）"
                    : $"0/{totalMods} 已完成，剩余 {totalMods} 个 Mod"
            });
            var parallelism = Math.Clamp(_settingsStore.Load().CollectionDownloadParallelism, 1, 10);
            using var semaphore = new SemaphoreSlim(parallelism, parallelism);

            var tasks = installableFiles.Select(async file =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var success = false;
                    var failureMessage = string.Empty;
                    try
                    {
                        var downloadResult = await DownloadCurseforgeModAsync(
                            file.ProjectId, file.FileId, modsPath, cancellationToken);
                        success = downloadResult.IsSuccess;
                        failureMessage = downloadResult.Message;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failureMessage = ex.Message;
                        // 单个 Mod 的 CDN/API/解压异常不能中断整个整合包。
                        onProgress?.Invoke(new ModpackInstallProgress
                        {
                            Percent = 15 + (int)(60.0 * completedMods / totalMods),
                            StepText = "下载 Mod 文件",
                            SubProgress = CalculateCompletionPercent(completedMods, totalMods),
                            SubProgressText = $"{file.ProjectId}/{file.FileId} 失败: {ex.Message}"
                        });
                    }

                    if (success)
                    {
                        lock (installedMods) installedMods.Add($"cf-{file.ProjectId}-{file.FileId}");
                    }
                    else
                    {
                        var displayName = $"cf-{file.ProjectId}-{file.FileId}";
                        lock (failedMods)
                        {
                            failedMods.Add(string.IsNullOrWhiteSpace(failureMessage)
                                ? displayName
                                : $"{displayName}: {failureMessage}");
                        }
                    }
                }
                finally
                {
                    semaphore.Release();
                }

                var done = Interlocked.Increment(ref completedMods);
                onProgress?.Invoke(new ModpackInstallProgress
                {
                    Percent = 15 + (int)(60.0 * done / totalMods),
                    StepText = "下载 Mod 文件",
                    SubProgress = CalculateCompletionPercent(done, totalMods),
                    SubProgressText = $"{done}/{totalMods} 已完成，剩余 {Math.Max(0, totalMods - done)} 个 Mod"
                });
            }).ToList();

            await Task.WhenAll(tasks);

            onProgress?.Invoke(new ModpackInstallProgress
            {
                Percent = 75,
                StepText = skippedManifestFiles > 0
                    ? $"Mod 下载完成（{failedMods.Count} 个失败，跳过 {skippedManifestFiles} 个）"
                    : $"Mod 下载完成（{failedMods.Count} 个失败）",
                SubProgress = CalculateFinalSubProgress(totalMods, failedMods.Count),
                SubProgressText = failedMods.Count > 0
                    ? $"已处理 {completedMods} 个 Mod，其中失败 {failedMods.Count} 个"
                    : $"已完成 {completedMods} 个 Mod，剩余 0 个 Mod"
            });

            // ===== 阶段 4: 处理 overrides =====
            onProgress?.Invoke(new ModpackInstallProgress { Percent = 77, StepText = "应用 overrides" });
            cancellationToken.ThrowIfCancellationRequested();

            var overrideDirectoryName = string.IsNullOrWhiteSpace(manifest.Overrides)
                ? "overrides"
                : manifest.Overrides.Trim();
            var overridesDir = string.Empty;
            try
            {
                var candidate = Path.GetFullPath(Path.Combine(packageRoot, overrideDirectoryName));
                // 清单来自外部归档，不能允许 overrides=../... 或绝对路径读取包外内容。
                // 使用带目录分隔符的边界判断，避免 packageRoot2 被误认为 packageRoot 的子目录。
                if (IsPathUnderDirectory(candidate, packageRoot))
                {
                    overridesDir = candidate;
                }
            }
            catch
            {
                overridesDir = string.Empty;
            }

            if (!Directory.Exists(overridesDir) &&
                !string.IsNullOrWhiteSpace(overridesDir) &&
                !overrideDirectoryName.Contains(Path.DirectorySeparatorChar) &&
                !overrideDirectoryName.Contains(Path.AltDirectorySeparatorChar))
            {
                overridesDir = FindDirectoryByNameIgnoreCase(packageRoot, overrideDirectoryName) ?? string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(overridesDir) && Directory.Exists(overridesDir))
            {
                foreach (var entry in Directory.GetFileSystemEntries(overridesDir, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(overridesDir, entry);
                    var target = Path.Combine(runtimePath, relative);
                    if (Directory.Exists(entry))
                    {
                        CopyDirectory(entry, target);
                    }
                    else
                    {
                        var targetParent = Path.GetDirectoryName(target);
                        if (!string.IsNullOrWhiteSpace(targetParent)) Directory.CreateDirectory(targetParent);
                        File.Copy(entry, target, true);
                    }
                }
            }

            onProgress?.Invoke(new ModpackInstallProgress { Percent = 85, StepText = "overrides 应用完成" });

            // CurseForge 清单按文件逐项安装；同一个归档仍可能展开为父 Mod
            // 和多个同级 ContentPack。统一在安装收尾阶段整理来源树，避免
            // 某个子 Mod 因单独写入来源而再次显示“缺少来源/可更新”。
            RepairCompositeSourceCredentials(modsPath);

            // ===== 阶段 5: 保存实例配置 =====
            onProgress?.Invoke(new ModpackInstallProgress { Percent = 87, StepText = "保存实例配置" });
            cancellationToken.ThrowIfCancellationRequested();

            var iconPath = ExtractPackIcon(packageRoot, versionRoot, customIconPath, tempDir);
            SaveInstanceRecord(instanceName, runtimePath);

            var finalPercent = failedMods.Count > 0 ? 99 : 100;
            onProgress?.Invoke(new ModpackInstallProgress
            {
                Percent = finalPercent,
                StepText = failedMods.Count > 0
                    ? $"安装完成（{failedMods.Count} 个 Mod 失败，可重试）"
                    : "安装完成"
            });

            return ModpackInstallResult.Success(runtimePath, versionRoot, installedMods, failedMods);
        }
        catch (OperationCanceledException)
        {
            if (!updateExisting)
            {
                CleanupVersionDirectory(gamePath, instanceName);
            }

            return ModpackInstallResult.Cancelled("Curseforge 整合包安装已取消");
        }
        catch (Exception ex)
        {
            if (!updateExisting)
            {
                CleanupVersionDirectory(gamePath, instanceName);
            }

            return ModpackInstallResult.Failed($"Curseforge 整合包安装失败: {ex.Message}", failedMods);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    // ================================================================
    // 辅助方法
    // ================================================================

    /// <summary>
    /// 读取带 BOM 的 JSON 文本。整合包和 Mod 发布包可能由 Windows 工具以 UTF-16
    /// 保存；File.ReadAllText 默认按 UTF-8 解码时会把这类 manifest 读成乱码，进而
    /// 误报“无法从 manifest.json 读取信息”。StreamReader 会根据 BOM 选择 UTF-8/16/32。
    /// </summary>
    private static string ReadTextFileWithBom(string path)
    {
        return ManifestTextReader.ReadAllText(path);
    }

    private static async Task<string> ReadTextFileWithBomAsync(
        string path,
        CancellationToken cancellationToken)
    {
        return await ManifestTextReader.ReadAllTextAsync(path, cancellationToken);
    }

    private string ResolveGamePath()
    {
        return _gameInstallPathLocator.TryLocateSteamStardewPath()
            ?? _gameInstallPathLocator.TryLocateGogStardewPath()
            ?? _gameInstallPathLocator.TryLocateXboxStardewPath()
            ?? string.Empty;
    }

    /// <summary>解析安装目标游戏路径。优先使用用户从路径列表选择的路径，否则回退到自动探测。</summary>
    private string ResolveTargetGamePath(string targetGamePath)
    {
        if (!string.IsNullOrWhiteSpace(targetGamePath) && Directory.Exists(targetGamePath))
        {
            return InstanceRuntimePathResolver.ResolveBasePath(targetGamePath);
        }

        return InstanceRuntimePathResolver.ResolveBasePath(ResolveGamePath());
    }

    /// <summary>
    /// 解析 SVL 整合包的实际内容根目录。
    /// 上游导出有“平铺包”“外层目录包”和“SVL.exe + modpack.zip”三种布局；
    /// 以 modpack.json 所在目录为根可以让 sources/mods/settings/icon 始终成套读取。
    /// </summary>
    private static string PrepareSvlPackageRoot(string tempDir)
    {
        var manifestPath = FindValidSvlManifestFile(tempDir);
        if (!string.IsNullOrWhiteSpace(manifestPath))
        {
            return Path.GetDirectoryName(manifestPath) ?? tempDir;
        }

        var nestedArchivePath = FindFileInDirectory(tempDir, "modpack.zip") ??
                                FindFileInDirectory(tempDir, "modpack.7z");
        if (string.IsNullOrWhiteSpace(nestedArchivePath))
        {
            return tempDir;
        }

        var innerDir = Path.Combine(tempDir, "_svl-inner");
        Directory.CreateDirectory(innerDir);
        ExtractPackageArchive(nestedArchivePath, innerDir);
        manifestPath = FindValidSvlManifestFile(innerDir);
        return string.IsNullOrWhiteSpace(manifestPath)
            ? innerDir
            : Path.GetDirectoryName(manifestPath) ?? innerDir;
    }

    private static string? FindFileInDirectory(string directory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        try
        {
            var direct = Path.Combine(directory, fileName);
            if (File.Exists(direct))
            {
                return direct;
            }

            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .FirstOrDefault(path => string.Equals(
                    Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static string? FindDirectoryByNameIgnoreCase(string directory, string directoryName)
    {
        if (string.IsNullOrWhiteSpace(directory) ||
            string.IsNullOrWhiteSpace(directoryName) ||
            !Directory.Exists(directory))
        {
            return null;
        }

        try
        {
            var direct = Path.Combine(directory, directoryName);
            if (Directory.Exists(direct))
            {
                return direct;
            }

            return Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => string.Equals(
                    Path.GetFileName(path), directoryName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 合并 SVL 的 sources.json 与 modpack.json.mods。
    ///
    /// 两份清单并存时，sources.json 通常负责导出时的目录/文件名，
    /// modpack.json.mods 则可能保留来源对象。合并不能简单使用 JsonNode 的
    /// 覆盖写法：空字符串、0 和仅含平台名的 source 都可能遮蔽另一份清单中
    /// 的有效值。这里按 Mod 名称、目录名和 UniqueID 建立匹配，并递归补齐
    /// 缺失值；未出现在 sources.json 且确实有可执行来源的 manifest 条目也会
    /// 追加，避免整合包静默漏装。
    /// </summary>
    private static List<JsonElement> MergeSourceEntriesWithManifest(
        IReadOnlyList<JsonElement> sourceEntries,
        IReadOnlyList<JsonElement> manifestEntries)
    {
        var manifestKeyToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < manifestEntries.Count; index++)
        {
            foreach (var key in GetSourceEntryIdentityKeys(manifestEntries[index]))
            {
                if (!manifestKeyToIndex.ContainsKey(key))
                {
                    manifestKeyToIndex[key] = index;
                }
            }
        }

        var matchedManifestIndexes = new HashSet<int>();
        var mergedEntries = new List<JsonElement>(sourceEntries.Count + manifestEntries.Count);
        foreach (var sourceEntry in sourceEntries)
        {
            var matchIndex = -1;
            foreach (var key in GetSourceEntryIdentityKeys(sourceEntry))
            {
                if (manifestKeyToIndex.TryGetValue(key, out var candidateIndex))
                {
                    matchIndex = candidateIndex;
                    break;
                }
            }

            if (matchIndex < 0)
            {
                mergedEntries.Add(sourceEntry.Clone());
                continue;
            }

            matchedManifestIndexes.Add(matchIndex);
            mergedEntries.Add(MergeJsonObjects(
                sourceEntry,
                manifestEntries[matchIndex]));
        }

        // sources.json 可能遗漏整个条目。只追加含有可执行来源的 manifest
        // 项，避免把 bundled/仅描述信息的条目误放进下载队列。
        for (var index = 0; index < manifestEntries.Count; index++)
        {
            if (matchedManifestIndexes.Contains(index) ||
                !HasActionableDownloadSource(manifestEntries[index]))
            {
                continue;
            }

            mergedEntries.Add(manifestEntries[index].Clone());
        }

        return mergedEntries;
    }

    private static IEnumerable<string> GetSourceEntryIdentityKeys(JsonElement entry)
    {
        foreach (var (propertyName, identityName) in new[]
                 {
                     ("name", "name"),
                     ("modName", "name"),
                     ("displayName", "name"),
                     ("title", "name"),
                     ("id", "id"),
                     ("uniqueId", "unique"),
                     ("uniqueID", "unique"),
                     ("unique_id", "unique"),
                     ("directoryName", "directory")
                 })
        {
            var value = GetJsonString(entry, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                var normalizedValue = value.Trim();
                yield return identityName + ":" + normalizedValue;

                // 旧清单常把 SMAPI 的 UniqueID 写在通用的 id 字段中；
                // 数字 id 则通常是 CurseForge/Nexus 项目 ID，不能与 UniqueID
                // 混用，否则可能把两个不同来源的 Mod 错合并。
                if (propertyName.Equals("id", StringComparison.OrdinalIgnoreCase) &&
                    !long.TryParse(normalizedValue, out _))
                {
                    yield return "unique:" + normalizedValue;
                }
            }
        }
    }

    private static JsonElement MergeJsonObjects(JsonElement primary, JsonElement supplement)
    {
        if (primary.ValueKind != JsonValueKind.Object ||
            supplement.ValueKind != JsonValueKind.Object)
        {
            return primary.Clone();
        }

        var primaryNode = JsonNode.Parse(primary.GetRawText()) as JsonObject;
        var supplementNode = JsonNode.Parse(supplement.GetRawText()) as JsonObject;
        if (primaryNode == null || supplementNode == null)
        {
            return primary.Clone();
        }

        MergeJsonObjectProperties(primaryNode, supplementNode);
        using var mergedDocument = JsonDocument.Parse(primaryNode.ToJsonString());
        return mergedDocument.RootElement.Clone();
    }

    private static void MergeJsonObjectProperties(JsonObject primary, JsonObject supplement)
    {
        foreach (var (propertyName, supplementValue) in supplement)
        {
            if (supplementValue == null)
            {
                continue;
            }

            if (!TryGetJsonObjectProperty(primary, propertyName, out var primaryValue) ||
                IsEmptyJsonValue(primaryValue))
            {
                primary[propertyName] = JsonNode.Parse(supplementValue.ToJsonString());
                continue;
            }

            if (primaryValue is JsonObject primaryObject &&
                supplementValue is JsonObject supplementObject)
            {
                MergeJsonObjectProperties(primaryObject, supplementObject);
                continue;
            }

            // sources.json 可能写成 { "source": "NexusMods" }，而 manifest
            // 使用 { "source": { "modId": ..., "fileId": ... } }。提升为对象
            // 并把原字符串保留成 type/url，避免丢失平台信息。
            if (propertyName.Equals("source", StringComparison.OrdinalIgnoreCase) &&
                primaryValue is JsonValue primaryText &&
                supplementValue is JsonObject supplementSource &&
                primaryText.TryGetValue<string>(out var sourceText) &&
                !string.IsNullOrWhiteSpace(sourceText))
            {
                var promotedSource = supplementSource.DeepClone() as JsonObject ?? new JsonObject();
                if (Uri.TryCreate(sourceText, UriKind.Absolute, out var sourceUri) &&
                    (sourceUri.Scheme == Uri.UriSchemeHttp ||
                     sourceUri.Scheme == Uri.UriSchemeHttps ||
                     sourceUri.Scheme.Equals("nxm", StringComparison.OrdinalIgnoreCase)))
                {
                    if (!TryGetJsonObjectProperty(promotedSource, "url", out var urlValue) ||
                        IsEmptyJsonValue(urlValue))
                    {
                        promotedSource["url"] = sourceText;
                    }
                }
                else if (!TryGetJsonObjectProperty(promotedSource, "type", out var typeValue) ||
                         IsEmptyJsonValue(typeValue))
                {
                    promotedSource["type"] = sourceText;
                }

                primary[propertyName] = promotedSource;
            }
        }
    }

    private static bool TryGetJsonObjectProperty(
        JsonObject json,
        string propertyName,
        out JsonNode? value)
    {
        foreach (var (name, node) in json)
        {
            if (string.Equals(name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = node;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool IsEmptyJsonValue(JsonNode? value)
    {
        if (value == null)
        {
            return true;
        }

        if (value is not JsonValue jsonValue)
        {
            return false;
        }

        if (jsonValue.TryGetValue<string>(out var text))
        {
            return string.IsNullOrWhiteSpace(text);
        }

        if (jsonValue.TryGetValue<long>(out var number))
        {
            return number <= 0;
        }

        return false;
    }

    private static string? FindValidSvlManifestFile(string directory)
    {
        return FindMatchingManifestFile(directory, "modpack.json", LooksLikeSvlManifestFile);
    }

    private static string? FindValidCurseforgeManifestFile(string directory)
    {
        return FindMatchingManifestFile(directory, "manifest.json", LooksLikeCurseforgeManifestFile);
    }

    private static string? FindMatchingManifestFile(
        string directory,
        string fileName,
        Func<string, bool> predicate)
    {
        try
        {
            var direct = Path.Combine(directory, fileName);
            var candidates = new List<string>();
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

    private static bool LooksLikeSvlManifestFile(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(ReadTextFileWithBom(path), ManifestJsonOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // SVL 清单至少有名称、Mod 列表或 SMAPI 版本字段。仅有
            // formatVersion/files 的外层打包元数据不能作为安装根。
            var hasName = TryGetJsonPropertyIgnoreCase(root, "name", out var name) &&
                          name.ValueKind == JsonValueKind.String &&
                          !string.IsNullOrWhiteSpace(name.GetString());
            var hasTitle = TryGetJsonPropertyIgnoreCase(root, "title", out var title) &&
                           title.ValueKind == JsonValueKind.String &&
                           !string.IsNullOrWhiteSpace(title.GetString());
            var hasMods = TryGetJsonPropertyIgnoreCase(root, "mods", out var mods) &&
                          mods.ValueKind == JsonValueKind.Array;
            var hasSmapiVersion = TryGetJsonPropertyIgnoreCase(root, "smapi_version", out _) ||
                                  TryGetJsonPropertyIgnoreCase(root, "smapiVersion", out _);
            return hasName || hasTitle || hasMods || hasSmapiVersion;
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeCurseforgeManifestFile(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(ReadTextFileWithBom(path), ManifestJsonOptions);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   TryGetJsonPropertyIgnoreCase(root, "manifestVersion", out var manifestVersion) &&
                   (manifestVersion.ValueKind == JsonValueKind.Number ||
                    manifestVersion.ValueKind == JsonValueKind.String) &&
                   TryGetJsonPropertyIgnoreCase(root, "files", out var files) &&
                   (files.ValueKind == JsonValueKind.Array || files.ValueKind == JsonValueKind.Null);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 解压整合包本体。整合包检测器已经接受 .7z；安装阶段必须使用同一套入口，
    /// 否则文件可以被识别并进入任务队列，却会在这里被 ZipExtractor 当成 ZIP 拒绝。
    /// .cfmodpack 虽然扩展名不同，但内容仍是 ZIP，由 ZipExtractor 负责兼容处理。
    /// </summary>
    private static void ExtractPackageArchive(string archivePath, string destinationDirectory)
    {
        if (ArchiveExtractor.IsSevenZip(archivePath))
        {
            ArchiveExtractor.ExtractSevenZipToDirectory(archivePath, destinationDirectory);
            return;
        }

        ZipExtractor.ExtractToDirectory(archivePath, destinationDirectory);
    }

    /// <summary>列出上游旧格式中直接位于包根目录的已打包 Mod 目录。</summary>
    private static IEnumerable<string> EnumerateLegacyBundledModDirectories(string packageRoot)
    {
        var excludedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mods", "settings", "launcher", "_svl-inner", "_inner", "_downloads"
        };

        IEnumerable<string> directories;
        try
        {
            directories = Directory.GetDirectories(packageRoot);
        }
        catch
        {
            yield break;
        }

        foreach (var directory in directories)
        {
            if (excludedNames.Contains(Path.GetFileName(directory)) ||
                !ContainsManifest(directory))
            {
                continue;
            }

            yield return directory;
        }
    }

    private static bool ContainsManifest(string directory)
    {
        return EnumerateFilesSafe(directory, "manifest.json").Count > 0;
    }

    /// <summary>
    /// 逐目录枚举文件，隔离单个无权限/损坏目录，并跳过重解析点。
    ///
    /// Directory.EnumerateFiles(..., AllDirectories) 遇到一个不可访问目录就会
    /// 终止整个枚举。Mods/整合包目录中可能存在残留联接点或异常目录；这里让
    /// 每个目录独立失败，保证其它有效 manifest 仍能被发现，也避免跟随链接离开
    /// 当前安装根目录。
    /// </summary>
    internal static IReadOnlyList<string> EnumerateFilesSafe(
        string rootDirectory,
        string? fileName = null)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
        {
            return [];
        }

        var pending = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<string>();
        try
        {
            pending.Push(Path.GetFullPath(rootDirectory));
        }
        catch
        {
            return [];
        }

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.GetFiles(current, "*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(fileName) ||
                            string.Equals(Path.GetFileName(file), fileName, StringComparison.OrdinalIgnoreCase))
                        {
                            results.Add(file);
                        }
                    }
                    catch
                    {
                        // 单个文件属性读取失败不应影响同目录的其它文件。
                    }
                }
            }
            catch
            {
                // 当前目录文件枚举失败，仍继续处理已发现的子目录。
            }

            string[] directories;
            try
            {
                directories = Directory.GetDirectories(current, "*", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            foreach (var directory in directories)
            {
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    pending.Push(Path.GetFullPath(directory));
                }
                catch
                {
                    // 单个子目录无权限或路径异常时跳过，保留其它分支。
                }
            }
        }

        return results;
    }

    /// <summary>
    /// 获取一个 Mod 压缩包中真正包含 manifest.json 的目录。
    /// 用 manifest 的直接父目录而不是压缩包第一层目录，兼容 Nexus/Curseforge
    /// 常见的“发行包名/实际 Mod 目录/manifest.json”多层包装。
    /// </summary>
    private static List<string> GetInstallableManifestDirectories(string rootDirectory)
    {
        var manifestDirectories = new List<string>();
        try
        {
            manifestDirectories = EnumerateFilesSafe(rootDirectory, "manifest.json")
                .Select(path => Path.GetDirectoryName(path))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.GetFullPath(path!))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()!;
        }
        catch
        {
            return [];
        }

        // 先过滤清单内容。某些发行包外层会带一个不属于 Mod 的 manifest.json
        // （甚至是损坏的打包元数据），同时内层才是真正可安装的 Mod。若先按
        // 目录层级过滤，外层坏清单会把内层有效清单遮掉，最终表现为“下载成功
        // 但安装后无法从 manifest.json 读取信息”。
        var validManifestDirectories = manifestDirectories
            .Where(IsInstalledModDirectory)
            .ToList();

        // 若一个复合 Mod 根目录自身有有效 manifest，同时子目录还有 manifest，
        // 只安装根目录，避免把子内容错误拆成多个顶层 Mod。
        return validManifestDirectories
            .Where(directory => !validManifestDirectories.Any(parent =>
                !string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase) &&
                IsPathUnderDirectory(directory, parent)))
            .ToList();
    }

    /// <summary>
    /// 返回归档中可直接安装的 Mod 根目录。
    ///
    /// 这个入口供旧的多文件 Collection 安装器复用，避免它自行枚举所有
    /// manifest.json 后把发行包外层的 Name-only 清单也当成 Mod。实际规则必须
    /// 与普通 Mod、SVL/CurseForge 整合包保持一致：清单有效、外层包装不抢占
    /// 内层真实 Mod，并且只返回最外层可安装根目录。
    /// </summary>
    internal static IReadOnlyList<string> FindInstallableModDirectories(string rootDirectory)
    {
        return GetInstallableManifestDirectories(rootDirectory);
    }

    private static bool IsPathUnderDirectory(string path, string directory)
    {
        var normalizedDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeModDirectoryName(string? directoryName, string fallbackName)
    {
        return InstanceRuntimePathResolver.SanitizeFileNameComponent(
            string.IsNullOrWhiteSpace(directoryName) ? fallbackName : directoryName,
            "unknown");
    }

    /// <summary>
    /// 解析 Mod 的实际安装目录名。
    ///
    /// 下载任务名通常是“cf-项目ID-文件ID”或“Nexus 文件名”，不能拿来覆盖
    /// 压缩包中的 Mod 目录名。若 manifest 位于压缩包的实际 Mod 子目录，优先使用
    /// 该目录名；若 manifest 直接位于压缩包根目录，则使用 manifest 的 Name/UniqueID。
    /// </summary>
    internal static string ResolveInstalledModDirectoryName(
        string manifestDirectory,
        string extractionRoot,
        string preferredName)
    {
        try
        {
            var normalizedManifestDirectory = Path.GetFullPath(manifestDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedExtractionRoot = Path.GetFullPath(extractionRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (!string.Equals(normalizedManifestDirectory, normalizedExtractionRoot, StringComparison.OrdinalIgnoreCase))
            {
                var directoryName = Path.GetFileName(normalizedManifestDirectory);
                // 部分 CurseForge/Nexus 导入包会把 manifest 直接放在
                // `cf-项目ID-文件ID` 或 `File 文件ID_...` 目录下。这只是下载器
                // 的临时命名，不是 Mod 的安装目录；优先用 manifest 元数据，
                // 否则会把机器名写进 Mods，导致后续配置覆盖/来源匹配失效。
                var generatedManifestName = TryReadModManifestName(normalizedManifestDirectory);
                if (LooksLikeGeneratedModDirectoryName(directoryName) &&
                    !string.IsNullOrWhiteSpace(generatedManifestName))
                {
                    return SanitizeModDirectoryName(generatedManifestName, preferredName);
                }

                if (!string.IsNullOrWhiteSpace(directoryName))
                {
                    return SanitizeModDirectoryName(directoryName, preferredName);
                }
            }

            var manifestName = TryReadModManifestName(normalizedManifestDirectory);
            return SanitizeModDirectoryName(manifestName, preferredName);
        }
        catch
        {
            return SanitizeModDirectoryName(preferredName, "unknown");
        }
    }

    private static string ResolveInstallTargetName(
        string manifestDirectory,
        string extractionRoot,
        string preferredName,
        string? modsPath,
        ISet<string> reservedTargetNames,
        bool isSingleManifest,
        int index)
    {
        var existingDirectory = FindExistingModDirectoryByManifestIdentity(
            manifestDirectory,
            modsPath,
            reservedTargetNames);
        if (!string.IsNullOrWhiteSpace(existingDirectory))
        {
            var existingName = SanitizeModDirectoryName(
                Path.GetFileName(existingDirectory),
                preferredName);
            if (reservedTargetNames.Add(existingName))
            {
                return existingName;
            }
        }

        var candidate = isSingleManifest
            ? ResolveInstalledModDirectoryName(manifestDirectory, extractionRoot, preferredName)
            : SanitizeModDirectoryName(Path.GetFileName(manifestDirectory), $"mod-{index}");
        candidate = SanitizeModDirectoryName(candidate, $"mod-{index}");
        if (reservedTargetNames.Add(candidate))
        {
            return candidate;
        }

        // 极少数归档会给多个 manifest 使用同名目录。不能让后一个清单
        // 静默覆盖前一个，给它一个稳定的后缀以保留两个 Mod。
        var suffix = 2;
        var uniqueCandidate = $"{candidate}-{suffix}";
        while (!reservedTargetNames.Add(uniqueCandidate))
        {
            suffix++;
            uniqueCandidate = $"{candidate}-{suffix}";
        }

        return uniqueCandidate;
    }

    private static string? FindExistingModDirectoryByManifestIdentity(
        string manifestDirectory,
        string? modsPath,
        ISet<string> reservedTargetNames)
    {
        if (string.IsNullOrWhiteSpace(modsPath) || !Directory.Exists(modsPath) ||
            !TryReadModManifestIdentity(manifestDirectory, out var sourceName, out var sourceUniqueId))
        {
            return null;
        }

        List<string> directories;
        try
        {
            directories = Directory.GetDirectories(modsPath)
                .Where(path =>
                {
                    var leaf = Path.GetFileName(path);
                    return !string.IsNullOrWhiteSpace(leaf) &&
                           !reservedTargetNames.Contains(leaf) &&
                           !string.Equals(leaf, "_downloads", StringComparison.OrdinalIgnoreCase) &&
                           !string.Equals(leaf, "_staging", StringComparison.OrdinalIgnoreCase) &&
                           IsInstalledModDirectory(path);
                })
                .ToList();
        }
        catch
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(sourceUniqueId))
        {
            foreach (var directory in directories)
            {
                if (TryReadModManifestIdentity(directory, out _, out var existingUniqueId) &&
                    string.Equals(sourceUniqueId, existingUniqueId, StringComparison.OrdinalIgnoreCase))
                {
                    return directory;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(sourceName))
        {
            foreach (var directory in directories)
            {
                if (TryReadModManifestIdentity(directory, out var existingName, out _) &&
                    string.Equals(sourceName, existingName, StringComparison.OrdinalIgnoreCase))
                {
                    return directory;
                }
            }
        }

        return null;
    }

    private static bool TryReadModManifestIdentity(
        string modDirectory,
        out string name,
        out string uniqueId)
    {
        name = string.Empty;
        uniqueId = string.Empty;
        try
        {
            var manifestPath = FindManifestPath(modDirectory);
            if (string.IsNullOrWhiteSpace(manifestPath))
            {
                return false;
            }

            using var document = JsonDocument.Parse(
                ReadTextFileWithBom(manifestPath),
                ManifestJsonOptions);
            var root = document.RootElement;
            name = GetJsonString(root, "Name", "name") ?? string.Empty;
            uniqueId = GetJsonString(root, "UniqueID", "UniqueId", "unique_id") ?? string.Empty;
            return !string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(uniqueId);
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeGeneratedModDirectoryName(string? directoryName)
    {
        if (string.IsNullOrWhiteSpace(directoryName))
        {
            return false;
        }

        var name = directoryName.Trim();
        return Regex.IsMatch(name, @"^cf-\d+-\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
               Regex.IsMatch(name, @"^file\s+\d+[_\s]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
               Regex.IsMatch(name, @"^(?:nexus|nxm)[-_]\d+(?:[-_]\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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

    private static string? TryReadModManifestName(string manifestDirectory)
    {
        try
        {
            var manifestPath = FindManifestPath(manifestDirectory);
            if (string.IsNullOrWhiteSpace(manifestPath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(ReadTextFileWithBom(manifestPath), ManifestJsonOptions);
            return GetJsonString(document.RootElement, "Name", "name", "UniqueID", "uniqueId");
        }
        catch
        {
            return null;
        }
    }

    private static bool IsInstalledModDirectory(string modDirectory)
    {
        if (!Directory.Exists(modDirectory))
        {
            return false;
        }

        try
        {
            var manifestPath = Directory.EnumerateFiles(modDirectory, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => string.Equals(
                    Path.GetFileName(path),
                    "manifest.json",
                    StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(manifestPath))
            {
                return false;
            }

            // 仅存在 manifest.json 文件还不代表 Mod 安装成功；损坏的 JSON 或
            // 外层打包元数据对象会在 Mod 管理页再次触发“无法从 manifest.json
            // 读取信息”。至少要有一个 Mod 身份字段，才能作为安装根。
            using var document = JsonDocument.Parse(ReadTextFileWithBom(manifestPath), ManifestJsonOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var root = document.RootElement;
            var modName = GetJsonString(root, "Name", "name");
            var uniqueId = GetJsonString(root, "UniqueID", "UniqueId", "unique_id");
            if (string.IsNullOrWhiteSpace(modName) && string.IsNullOrWhiteSpace(uniqueId))
            {
                return false;
            }

            // CurseForge/整合包外层也可能有一个可解析的 manifest.json，
            // 但它只有 Name/Version 或 files/manifestVersion 等打包字段。
            // 这类目录不能作为 Mod 根，否则它会按父目录规则遮蔽真正的内层
            // Mod，并在安装后让管理页继续报“无法从 manifest.json 读取信息”。
            var hasStrongModIdentity =
                !string.IsNullOrWhiteSpace(uniqueId) ||
                TryGetJsonPropertyIgnoreCase(root, "EntryDll", out _) ||
                TryGetJsonPropertyIgnoreCase(root, "ContentPackFor", out _) ||
                TryGetJsonPropertyIgnoreCase(root, "UpdateKeys", out _);
            var hasPackageShape =
                TryGetJsonPropertyIgnoreCase(root, "files", out _) ||
                TryGetJsonPropertyIgnoreCase(root, "manifestVersion", out _) ||
                TryGetJsonPropertyIgnoreCase(root, "manifestType", out _) ||
                TryGetJsonPropertyIgnoreCase(root, "minecraft", out _) ||
                TryGetJsonPropertyIgnoreCase(root, "formatVersion", out _) ||
                TryGetJsonPropertyIgnoreCase(root, "modpackVersion", out _);

            if (hasPackageShape)
            {
                return hasStrongModIdentity;
            }

            // Name-only 的旧 Mod 仍保持兼容；但如果它包住了另一个 manifest，
            // 它就是发行包外层包装目录，不应抢占内层实际 Mod。
            return hasStrongModIdentity || !HasNestedManifest(manifestPath);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasNestedManifest(string manifestPath)
    {
        var directory = Path.GetDirectoryName(manifestPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        return EnumerateFilesSafe(directory, "manifest.json")
            .Any(path => !string.Equals(path, manifestPath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 将导出包中的 Mod 目录名映射到安装后 manifest 所在的实际目录。
    /// 下载包经常使用“发行包目录/实际 Mod 目录”的布局，目录名可能从
    /// Content Patcher 变为 ContentPatcher；只有目录名匹配时不能覆盖配置，
    /// 还需要读取 manifest 的 Name/UniqueID。
    /// </summary>
    private static string ResolveInstalledModSettingsDirectory(
        string modsPath,
        string configuredModName)
    {
        if (string.IsNullOrWhiteSpace(modsPath) ||
            string.IsNullOrWhiteSpace(configuredModName) ||
            !Directory.Exists(modsPath))
        {
            return string.Empty;
        }

        var sanitizedName = SanitizeModDirectoryName(configuredModName, "unknown");
        var exactPath = Path.Combine(modsPath, sanitizedName);
        if (IsInstalledModDirectory(exactPath))
        {
            return Path.GetFileName(exactPath);
        }

        try
        {
            foreach (var directory in Directory.GetDirectories(modsPath))
            {
                var directoryName = Path.GetFileName(directory);
                if (string.Equals(directoryName, "_downloads", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(directoryName, "_staging", StringComparison.OrdinalIgnoreCase) ||
                    !IsInstalledModDirectory(directory))
                {
                    continue;
                }

                var manifestPath = Path.Combine(directory, "manifest.json");
                using var document = JsonDocument.Parse(ReadTextFileWithBom(manifestPath), ManifestJsonOptions);
                var manifestName = GetJsonString(document.RootElement, "Name", "name");
                var manifestUniqueId = GetJsonString(
                    document.RootElement,
                    "UniqueID",
                    "UniqueId",
                    "unique_id");
                if (string.Equals(configuredModName, directoryName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(configuredModName, manifestName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(configuredModName, manifestUniqueId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(sanitizedName, directoryName, StringComparison.OrdinalIgnoreCase))
                {
                    return directoryName;
                }
            }
        }
        catch
        {
            // 单个损坏 manifest 或目录权限问题不应阻断其它配置文件复制。
        }

        return string.Empty;
    }

    /// <summary>
    /// 判断来源条目对应的 Mod 是否已经安装。
    ///
    /// directoryName 只是打包时记录的目录名，不一定等于最终安装目录：Nexus/CurseForge
    /// 下载包经常是“文件名目录/实际 Mod 目录/manifest.json”，安装时会整理成 manifest
    /// 的 Name 或 UniqueID。因此重装整合包时必须读取一级 Mod 目录的 manifest，避免重复
    /// 打开浏览器或重新下载同一个 Mod。
    /// </summary>
    private static bool IsMatchingInstalledMod(
        string modsPath,
        JsonElement source,
        string expectedDirectoryName)
    {
        if (string.IsNullOrWhiteSpace(modsPath) || !Directory.Exists(modsPath))
        {
            return false;
        }

        var sourceName = GetJsonString(source, "name", "modName");
        var sourceUniqueId = GetJsonString(source, "uniqueId", "uniqueID", "unique_id");
        TryGetModSourceDescriptor(source, out var sourceDescriptor);

        try
        {
            foreach (var directory in Directory.GetDirectories(modsPath))
            {
                var directoryName = Path.GetFileName(directory);
                if (string.Equals(directoryName, "_downloads", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(directoryName, "_staging", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(directoryName, "__collection_reports", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(directoryName, expectedDirectoryName, StringComparison.OrdinalIgnoreCase))
                {
                    return IsInstalledModDirectory(directory);
                }

                if (IsMatchingSourceCredential(
                        directory,
                        sourceDescriptor.Platform,
                        sourceDescriptor.ProjectId,
                        sourceDescriptor.FileId))
                {
                    return true;
                }

                var manifestPath = FindManifestPath(directory);
                if (string.IsNullOrWhiteSpace(manifestPath))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(ReadTextFileWithBom(manifestPath), ManifestJsonOptions);
                    var manifestName = GetJsonString(document.RootElement, "Name", "name");
                    var manifestUniqueId = GetJsonString(document.RootElement, "UniqueID", "UniqueId", "unique_id");

                    if ((!string.IsNullOrWhiteSpace(sourceUniqueId) &&
                         string.Equals(sourceUniqueId, manifestUniqueId, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(sourceName) &&
                         string.Equals(sourceName, manifestName, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
                catch
                {
                    // 单个损坏 manifest 不应阻断其它目录的匹配。
                }
            }
        }
        catch
        {
            // 目录权限/并发变化时交给后续下载流程处理。
        }

        return false;
    }

    private static string GetBundledModInstallFailureReason(string sourceDirectory)
    {
        try
        {
            return GetInstallableManifestDirectories(sourceDirectory).Count == 0
                ? "未找到有效 manifest.json"
                : "manifest.json 无效或文件复制失败";
        }
        catch (Exception ex)
        {
            return $"读取内置 Mod 失败: {ex.Message}";
        }
    }

    /// <summary>把已打包 Mod 整理为 Mods/&lt;Mod目录&gt;，同时去除多余外层目录。</summary>
    internal static bool InstallBundledModDirectory(
        string sourceDirectory,
        string modsPath,
        string preferredName,
        out List<string> installedNames)
    {
        return InstallBundledModDirectory(
            sourceDirectory,
            modsPath,
            preferredName,
            out installedNames,
            out _);
    }

    /// <summary>
    /// 安装内置 Mod，同时返回复制失败的目录名。
    /// 旧的四参数重载保留给已有调用方；导入流程使用此重载，避免复合包中
    /// 某个 Mod 复制失败时被其它成功目录掩盖。
    /// </summary>
    internal static bool InstallBundledModDirectory(
        string sourceDirectory,
        string modsPath,
        string preferredName,
        out List<string> installedNames,
        out List<string> failedNames)
    {
        installedNames = [];
        failedNames = [];
        var manifestDirectories = GetInstallableManifestDirectories(sourceDirectory);
        var reservedTargetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (manifestDirectories.Count == 0)
        {
            failedNames.Add(preferredName);
            return false;
        }

        for (var i = 0; i < manifestDirectories.Count; i++)
        {
            var manifestDirectory = manifestDirectories[i];
            if (!IsInstalledModDirectory(manifestDirectory))
            {
                // manifest 文件存在但 JSON 损坏时，禁止把坏目录复制到 Mods。
                failedNames.Add(Path.GetFileName(manifestDirectory));
                continue;
            }

            var targetName = ResolveInstallTargetName(
                manifestDirectory,
                sourceDirectory,
                preferredName,
                modsPath,
                reservedTargetNames,
                manifestDirectories.Count == 1,
                i + 1);
            var targetDirectory = Path.Combine(modsPath, targetName);

            try
            {
                ReplaceDirectoryFromSource(manifestDirectory, targetDirectory, modsPath);
                if (IsInstalledModDirectory(targetDirectory))
                {
                    installedNames.Add(targetName);
                }
                else
                {
                    failedNames.Add(targetName);
                }
            }
            catch
            {
                failedNames.Add(targetName);
            }
        }

        return installedNames.Count > 0;
    }

    /// <summary>解压并安装单个 Mod 下载包，保证 manifest 位于 Mods 的一级子目录。</summary>
    internal static bool InstallDownloadedModArchive(
        string zipPath,
        string modsPath,
        string preferredName,
        out List<string> installedNames)
    {
        installedNames = [];
        if (!IsValidModArchive(zipPath))
        {
            return false;
        }

        var stagingDirectory = Path.Combine(modsPath, "_staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            ExtractModArchive(zipPath, stagingDirectory);
            var manifestDirectories = GetInstallableManifestDirectories(stagingDirectory);
            var reservedTargetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (manifestDirectories.Count == 0)
            {
                return false;
            }

            for (var i = 0; i < manifestDirectories.Count; i++)
            {
                var manifestDirectory = manifestDirectories[i];
                if (!IsInstalledModDirectory(manifestDirectory))
                {
                    // 先验证暂存目录中的 manifest，再替换目标目录，避免
                    // 损坏的下载包留下不可解析的 Mods 子目录。
                    continue;
                }

                var targetName = ResolveInstallTargetName(
                    manifestDirectory,
                    stagingDirectory,
                    preferredName,
                    modsPath,
                    reservedTargetNames,
                    manifestDirectories.Count == 1,
                    i + 1);
                var targetDirectory = Path.Combine(modsPath, targetName);

                ReplaceDirectoryFromSource(manifestDirectory, targetDirectory, modsPath);
                if (IsInstalledModDirectory(targetDirectory))
                {
                    installedNames.Add(targetName);
                }
            }

            return installedNames.Count > 0;
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingDirectory))
                {
                    Directory.Delete(stagingDirectory, true);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// 预览一个 Mod 目录/归档最终会写入的一级目录名。
    /// 安装前先调用此方法可以发现“更新/覆盖”目标，避免在没有提示的情况下
    /// 直接替换已有 Mod。预览使用独立临时目录，不会修改 Mods 目录。
    /// </summary>
    internal static IReadOnlyList<string> GetInstallTargetNames(string sourcePath, string preferredName)
    {
        return GetInstallTargetNames(sourcePath, preferredName, null);
    }

    /// <summary>
    /// 预览安装目录名，并在可能时复用目标 Mods 中已有的同一 Mod 目录。
    /// 复合压缩包的外层目录名不一定等于 manifest 的 Name；例如一个归档同时
    /// 包含多个子 Mod 时，必须先按 UniqueID/Name 找到现有目录，才能执行更新而
    /// 不是另建一个包装目录，导致旧版本继续被游戏加载。
    /// </summary>
    internal static IReadOnlyList<string> GetInstallTargetNames(
        string sourcePath,
        string preferredName,
        string? modsPath)
    {
        var temporaryDirectory = string.Empty;
        try
        {
            string root;
            if (Directory.Exists(sourcePath))
            {
                root = sourcePath;
            }
            else if (File.Exists(sourcePath) && IsValidModArchive(sourcePath))
            {
                temporaryDirectory = Path.Combine(
                    Path.GetTempPath(),
                    "SVL",
                    "mod-install-preview",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temporaryDirectory);
                ExtractModArchive(sourcePath, temporaryDirectory);
                root = temporaryDirectory;
            }
            else
            {
                return [];
            }

            var manifestDirectories = GetInstallableManifestDirectories(root);
            var names = new List<string>();
            var reservedTargetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < manifestDirectories.Count; i++)
            {
                var manifestDirectory = manifestDirectories[i];
                if (!IsInstalledModDirectory(manifestDirectory))
                {
                    continue;
                }

                var targetName = ResolveInstallTargetName(
                    manifestDirectory,
                    root,
                    preferredName,
                    modsPath,
                    reservedTargetNames,
                    manifestDirectories.Count == 1,
                    i + 1);
                if (!string.IsNullOrWhiteSpace(targetName))
                {
                    names.Add(targetName);
                }
            }

            return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch
        {
            return [];
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryDirectory))
            {
                try
                {
                    if (Directory.Exists(temporaryDirectory))
                    {
                        Directory.Delete(temporaryDirectory, true);
                    }
                }
                catch
                {
                    // 临时目录清理失败不应阻断安装前的冲突确认。
                }
            }
        }
    }

    /// <summary>
    /// 供下载队列和平台缓存校验“可安装的 Mod 归档”。仅有 ZIP/7z 文件头并不够：
    /// Nexus/CurseForge 返回的错误页、SMAPI 安装包或其它合法压缩包都可能通过基础
    /// 归档校验，必须再确认包内至少有一个可解析的 Mod manifest.json。
    /// </summary>
    internal static bool IsValidModArchiveFile(string path)
    {
        if (!IsValidModArchive(path))
        {
            return false;
        }

        var stagingDirectory = Path.Combine(
            Path.GetTempPath(),
            "SVL",
            "mod-archive-check",
            Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            ExtractModArchive(path, stagingDirectory);
            return GetInstallableManifestDirectories(stagingDirectory).Count > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingDirectory))
                {
                    Directory.Delete(stagingDirectory, true);
                }
            }
            catch
            {
                // 校验临时目录清理失败不应影响后续下载流程。
            }
        }
    }

    /// <summary>
    /// 获取 SMAPI zip 路径。
    /// 顺序：精确缓存 → 兼容缓存 → 目标 Base 已安装实例 → 远程目录下载。
    /// </summary>
    private async Task<string?> ResolveSmapiZipAsync(
        string? preferredVersion,
        string instanceName,
        string gamePath,
        Action<ModpackInstallProgress>? onProgress,
        CancellationToken ct,
        bool allowCurrentInstance = false)
    {
        var resolveLock = GetSmapiResolveLock(gamePath, preferredVersion);
        await resolveLock.WaitAsync(ct);
        try
        {
            return await ResolveSmapiZipCoreAsync(
                preferredVersion,
                instanceName,
                gamePath,
                onProgress,
                ct,
                allowCurrentInstance);
        }
        finally
        {
            resolveLock.Release();
        }
    }

    /// <summary>
    /// 为 Collection/其它整合包入口提供同一套 SMAPI 解析链路。
    ///
    /// Collection 原先有一份独立实现，容易在缓存校验、GitHub latest 回退、
    /// 双层 ZIP 归一化等修复后出现行为分叉。这里仅转换进度模型，解析本身
    /// 始终委托本服务的主流程。
    /// </summary>
    internal Task<string?> ResolveSmapiZipForCollectionAsync(
        string? preferredVersion,
        string instanceName,
        string gamePath,
        Action<CollectionInstallProgress>? onProgress,
        CancellationToken cancellationToken,
        bool allowCurrentInstance = false)
    {
        Action<ModpackInstallProgress>? progressAdapter = onProgress == null
            ? null
            : progress => onProgress(new CollectionInstallProgress
            {
                Percent = progress.Percent,
                StepText = progress.StepText,
                SubProgress = progress.SubProgress,
                SubProgressText = progress.SubProgressText
            });

        return ResolveSmapiZipAsync(
            preferredVersion,
            instanceName,
            gamePath,
            progressAdapter,
            cancellationToken,
            allowCurrentInstance);
    }

    /// <summary>
    /// 从 CurseForge manifest 的明确 SMAPI 文件记录解析安装包。
    /// 这条路径不依赖 SMAPI 版本列表，只依赖 manifest 中已经固定的 FileID，
    /// 并使用与普通 CurseForge Mod 相同的稳定缓存键。
    /// </summary>
    private async Task<string?> ResolveCurseforgeSmapiArchiveAsync(
        long fileId,
        Action<ModpackInstallProgress>? onProgress,
        CancellationToken ct)
    {
        const long smapiProjectId = 898372;
        if (fileId <= 0)
        {
            return null;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "SVL", "smapi");
        Directory.CreateDirectory(tempDir);

        if (CurseforgeDownloadCache.TryGet(
                smapiProjectId,
                fileId,
                out var cachedPath,
                TryNormalizeSmapiArchive))
        {
            onProgress?.Invoke(new ModpackInstallProgress
            {
                StepText = $"复用整合包清单中的 CurseForge SMAPI 缓存（FileID: {fileId}）"
            });
            return cachedPath;
        }

        var zipPath = Path.Combine(tempDir, $"SMAPI-CurseForge-{fileId}.zip");
        if (TryNormalizeSmapiArchive(zipPath))
        {
            CurseforgeDownloadCache.Save(
                smapiProjectId,
                fileId,
                zipPath,
                TryNormalizeSmapiArchive);
            return zipPath;
        }

        if (File.Exists(zipPath))
        {
            try { File.Delete(zipPath); } catch { }
        }

        try
        {
            var downloadUrl = await _remoteCatalogService.ResolveCurseforgeFileDownloadUrlAsync(
                smapiProjectId,
                fileId,
                string.Empty,
                ct);
            if (!IsLikelyDirectDownloadUrl(downloadUrl))
            {
                onProgress?.Invoke(new ModpackInstallProgress
                {
                    StepText = $"清单中的 CurseForge SMAPI FileID={fileId} 暂无可用直链，回退通用解析"
                });
                return null;
            }

            onProgress?.Invoke(new ModpackInstallProgress
            {
                StepText = $"按整合包清单下载 CurseForge SMAPI（FileID: {fileId}）"
            });
            await _httpDownloadService.DownloadAsync(
                downloadUrl,
                zipPath,
                null,
                ct,
                cacheValidator: TryNormalizeSmapiArchive);

            if (!TryNormalizeSmapiArchive(zipPath))
            {
                throw new InvalidDataException("CurseForge 清单中的 SMAPI 文件不是有效安装包");
            }

            CurseforgeDownloadCache.Save(
                smapiProjectId,
                fileId,
                zipPath,
                TryNormalizeSmapiArchive);
            return zipPath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            onProgress?.Invoke(new ModpackInstallProgress
            {
                StepText = $"清单中的 CurseForge SMAPI 不可用，回退通用解析: {ex.Message}"
            });
            try
            {
                foreach (var path in new[] { zipPath, zipPath + ".part", zipPath + ".part.json" })
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
            }
            catch
            {
                // 清理失败不应阻止通用 SMAPI 链路继续尝试。
            }

            return null;
        }
    }

    /// <summary>
    /// 获取跨 Modpack/Collection 共用的 SMAPI 解析锁。
    /// </summary>
    internal static SemaphoreSlim GetSmapiResolveLock(string gamePath, string? preferredVersion)
    {
        var normalizedVersion = NormalizeSmapiVersionForFileName(preferredVersion);
        var key = $"smapi-cache|{normalizedVersion}";
        return SmapiResolveLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
    }

    private async Task<string?> ResolveSmapiZipCoreAsync(
        string? preferredVersion,
        string instanceName,
        string gamePath,
        Action<ModpackInstallProgress>? onProgress,
        CancellationToken ct,
        bool allowCurrentInstance = false)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SVL", "smapi");
        Directory.CreateDirectory(tempDir);

        // 缓存命中（需通过 SMAPI 包签名校验，防止缓存污染导致 install.dat 缺失）
        if (!string.IsNullOrWhiteSpace(preferredVersion))
        {
            var normalizedPreferredVersion = NormalizeSmapiVersionForFileName(preferredVersion);
            var cachedPath = Path.Combine(tempDir, $"SMAPI-{normalizedPreferredVersion}.zip");
            if (TryNormalizeSmapiArchive(cachedPath) &&
                IsCompatibleSmapiVersion(
                    preferredVersion,
                    SmapiPackageVersionInspector.TryReadVersion(cachedPath) ?? normalizedPreferredVersion))
            {
                return cachedPath;
            }

            // 缓存文件不是 SMAPI 包则删除
            if (File.Exists(cachedPath))
            {
                try { File.Delete(cachedPath); } catch { }
            }
        }

        // 清单版本不一定和缓存文件名完全一致（如带 "SMAPI " 前缀），
        // 继续寻找同一大版本中不低于清单要求的有效缓存。
        var compatibleCachedPath = Directory.EnumerateFiles(tempDir, "SMAPI-*.zip", SearchOption.TopDirectoryOnly)
            .Where(TryNormalizeSmapiArchive)
            .Where(path => IsCompatibleSmapiVersion(
                preferredVersion,
                SmapiPackageVersionInspector.TryReadVersion(path) ?? ExtractSmapiVersionFromFileName(path)))
            .OrderByDescending(path => ParseSmapiVersion(
                SmapiPackageVersionInspector.TryReadVersion(path) ?? ExtractSmapiVersionFromFileName(path)) ?? new Version(0, 0, 0))
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(compatibleCachedPath))
        {
            onProgress?.Invoke(new ModpackInstallProgress { StepText = $"复用已缓存 SMAPI: {Path.GetFileName(compatibleCachedPath)}" });
            return compatibleCachedPath;
        }

        // 旧版 Nexus 导出只保存 ModID/版本文本，无法直接按 FileID 命中缓存。
        // 在打开浏览器和请求远程版本目录前扫描 SMAPI(ModID=2400) 缓存，
        // 只接受经过 install.dat 校验的完整安装包。
        if (NexusDownloadCache.TryGetAnyForMod(
                2400,
                path => TryNormalizeSmapiArchive(path) &&
                        SmapiPackageVersionInspector.IsCompatible(preferredVersion, path),
                out var nexusCachedPath))
        {
            onProgress?.Invoke(new ModpackInstallProgress
            {
                StepText = $"复用已缓存 Nexus SMAPI: {Path.GetFileName(nexusCachedPath)}"
            });
            return nexusCachedPath;
        }

        // 远程服务不可用时，从目标 Base 下已有 SMAPI 实例生成临时安装包。
        var installedPackageDir = Path.Combine(tempDir, "installed");
        if (InstalledSmapiPackageBuilder.TryCreate(
                gamePath,
                preferredVersion,
                allowCurrentInstance ? null : instanceName,
                installedPackageDir,
                out var installedPackagePath, out var sourceRuntimePath, out var installedVersion))
        {
            onProgress?.Invoke(new ModpackInstallProgress
            {
                StepText = $"复用已有 SMAPI {installedVersion}（来源: {sourceRuntimePath}）"
            });
            return installedPackagePath;
        }

        // 从 RemoteCatalog 获取 SMAPI 版本列表
        List<Models.SmapiVersionEntry>? smapiVersions;
        try
        {
            smapiVersions = await _remoteCatalogService.GetSmapiVersionEntriesAsync(
                perPage: 10,
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            onProgress?.Invoke(new ModpackInstallProgress { StepText = $"警告: SMAPI 目录不可用，尝试官方版本地址（{ex.Message}）" });
            var fallbackPackage = await TryDownloadKnownSmapiReleaseAsync(
                preferredVersion,
                tempDir,
                onProgress,
                ct);
            if (!string.IsNullOrWhiteSpace(fallbackPackage))
            {
                return fallbackPackage;
            }

            return null;
        }
        if (smapiVersions == null || smapiVersions.Count == 0)
        {
            onProgress?.Invoke(new ModpackInstallProgress { StepText = "警告: 无法获取 SMAPI 版本列表，尝试读取官方最新版" });
            var latestEntry = await _remoteCatalogService.GetLatestSmapiVersionEntryAsync(ct);
            if (latestEntry != null)
            {
                smapiVersions = [latestEntry];
                onProgress?.Invoke(new ModpackInstallProgress
                {
                    StepText = $"已获取官方 SMAPI 最新版 {latestEntry.Version}，继续校验安装包"
                });
            }
        }

        if (smapiVersions == null || smapiVersions.Count == 0)
        {
            onProgress?.Invoke(new ModpackInstallProgress { StepText = "警告: 官方最新版不可用，尝试按清单版本地址" });
            var fallbackPackage = await TryDownloadKnownSmapiReleaseAsync(
                preferredVersion,
                tempDir,
                onProgress,
                ct);
            if (!string.IsNullOrWhiteSpace(fallbackPackage))
            {
                return fallbackPackage;
            }

            onProgress?.Invoke(new ModpackInstallProgress { StepText = "警告: 无法获取经过校验的 SMAPI 安装包，跳过 SMAPI 安装" });
            return null;
        }

        var preferredVersionKey = NormalizeSmapiVersionForFileName(preferredVersion);
        var smapiEntry = string.IsNullOrWhiteSpace(preferredVersion)
            ? smapiVersions[0]
            : smapiVersions.FirstOrDefault(v =>
                  string.Equals(
                      NormalizeSmapiVersionForFileName(v.Version),
                      preferredVersionKey,
                      StringComparison.OrdinalIgnoreCase))
              ?? smapiVersions
                  .Where(v => IsCompatibleSmapiVersion(preferredVersion, v.Version))
                  .OrderByDescending(v => ParseSmapiVersion(v.Version) ?? new Version(0, 0, 0))
                  .FirstOrDefault();

        if (smapiEntry == null)
        {
            onProgress?.Invoke(new ModpackInstallProgress
            {
                StepText = $"SMAPI 目录没有兼容版本 {preferredVersion}，尝试官方版本地址"
            });
            var fallbackPackage = await TryDownloadKnownSmapiReleaseAsync(
                preferredVersion,
                tempDir,
                onProgress,
                ct);
            if (!string.IsNullOrWhiteSpace(fallbackPackage))
            {
                return fallbackPackage;
            }

            onProgress?.Invoke(new ModpackInstallProgress
            {
                StepText = $"未找到兼容的 SMAPI 版本: {preferredVersion}"
            });
            return null;
        }

        var version = NormalizeSmapiVersionForFileName(smapiEntry.Version);
        var zipPath = Path.Combine(tempDir, $"SMAPI-{version}.zip");

        if (TryNormalizeSmapiArchive(zipPath))
        {
            return zipPath;
        }

        // 缓存文件损坏则删除
        if (File.Exists(zipPath))
        {
            try { File.Delete(zipPath); } catch { }
        }

        if (string.IsNullOrWhiteSpace(smapiEntry.DownloadUrl))
        {
            onProgress?.Invoke(new ModpackInstallProgress { StepText = "警告: SMAPI 下载地址为空，跳过 SMAPI 安装" });
            return null;
        }

        await _httpDownloadService.DownloadAsync(
            smapiEntry.DownloadUrl,
            zipPath,
            null,
            ct,
            cacheValidator: TryNormalizeSmapiArchive);

        // 下载后校验是否为 SMAPI 官方安装包（含 install.dat），避免传入非 SMAPI 包导致安装失败
        if (!TryNormalizeSmapiArchive(zipPath))
        {
            // GitHub 发布的 SMAPI 安装包是 double-zipped（外层 zip 内只有内层 zip），
            // 尝试解压外层得到真正的 SMAPI 安装包
            TryUnwrapDoubleZipped(zipPath);
        }

        // 再次校验（可能在 double-zipped 解包后通过）
        if (!TryNormalizeSmapiArchive(zipPath))
        {
            try { File.Delete(zipPath); } catch { }
            throw new InvalidDataException($"SMAPI 压缩包下载后校验失败（可能下载不完整、地址失效或非 SMAPI 官方包）: {smapiEntry.DownloadUrl}");
        }

        return zipPath;
    }

    /// <summary>
    /// 目录 API 不可用时，按清单里的明确版本访问 SMAPI 官方 Release。
    /// 不对 latest 猜测地址，避免将未知版本安装到整合包实例；下载后仍必须
    /// 通过 install.dat 校验，且兼容 GitHub 的 double-zipped 发布包。
    /// </summary>
    private async Task<string?> TryDownloadKnownSmapiReleaseAsync(
        string? preferredVersion,
        string tempDir,
        Action<ModpackInstallProgress>? onProgress,
        CancellationToken ct)
    {
        var version = NormalizeSmapiVersionForFileName(preferredVersion);
        if (string.IsNullOrWhiteSpace(preferredVersion) ||
            string.Equals(version, "latest", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // SMAPI 的 manifest 版本常带 .0（例如 4.5.1.0），而 GitHub Release
        // 标签/资产名通常使用 4.5.1。先尝试完整版本，再逐层去掉末尾的 .0，
        // 避免目录服务暂时不可用时把一个可用的官方版本误判成不存在。
        foreach (var releaseVersion in GetOfficialSmapiReleaseVersionCandidates(version))
        {
            var downloadUrl = BuildOfficialSmapiReleaseUrl(releaseVersion);
            var zipPath = Path.Combine(tempDir, $"SMAPI-{releaseVersion}.zip");
            try
            {
                if (TryNormalizeSmapiArchive(zipPath))
                {
                    return zipPath;
                }

                if (File.Exists(zipPath))
                {
                    try { File.Delete(zipPath); } catch { }
                }

                onProgress?.Invoke(new ModpackInstallProgress
                {
                    StepText = $"尝试复用官方 SMAPI {releaseVersion} 发布包"
                });
                await _httpDownloadService.DownloadAsync(
                    downloadUrl,
                    zipPath,
                    null,
                    ct,
                    cacheValidator: TryNormalizeSmapiArchive);

                if (!TryNormalizeSmapiArchive(zipPath))
                {
                    TryUnwrapDoubleZipped(zipPath);
                }

                if (TryNormalizeSmapiArchive(zipPath))
                {
                    return zipPath;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                onProgress?.Invoke(new ModpackInstallProgress
                {
                    StepText = $"官方 SMAPI {releaseVersion} 发布包不可用: {ex.Message}"
                });
            }

            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
        }

        return null;
    }

    private static string BuildOfficialSmapiReleaseUrl(string version)
    {
        var escapedVersion = Uri.EscapeDataString(version);
        return $"https://github.com/Pathoschild/SMAPI/releases/download/{escapedVersion}/SMAPI-{escapedVersion}-installer.zip";
    }

    private static IEnumerable<string> GetOfficialSmapiReleaseVersionCandidates(string version)
    {
        var current = version.Trim();
        while (!string.IsNullOrWhiteSpace(current))
        {
            yield return current;

            if (!current.EndsWith(".0", StringComparison.Ordinal) ||
                current.Count(character => character == '.') < 2)
            {
                yield break;
            }

            current = current[..^2];
        }
    }

    private static string ExtractSmapiVersionFromFileName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var match = System.Text.RegularExpressions.Regex.Match(name, @"\d+(?:\.\d+){1,3}");
        return match.Success ? match.Value : string.Empty;
    }

    private static Version? ParseSmapiVersion(string? value)
    {
        return Version.TryParse(value, out var version) ? version : null;
    }

    private static bool IsCompatibleSmapiVersion(string? preferred, string? candidate)
    {
        var preferredVersion = ParseSmapiVersion(preferred?.Replace("SMAPI", string.Empty, StringComparison.OrdinalIgnoreCase).Trim());
        var candidateVersion = ParseSmapiVersion(candidate);

        // 没有明确的目标版本时可以接受任意已校验的 SMAPI 包；但目标版本明确时，
        // 无法从缓存文件名/包内容确认版本就不能复用，避免把 latest 或未知版本包
        // 错当成清单要求的 SMAPI。
        return preferredVersion == null ||
               (candidateVersion != null &&
                preferredVersion.Major == candidateVersion.Major &&
                candidateVersion >= preferredVersion);
    }

    private static int CalculateCompletionPercent(int completed, int total)
    {
        if (total <= 0)
        {
            return -1;
        }

        if (completed >= total)
        {
            return 100;
        }

        // 未完成的任务永远不能把进度条渲染成满格。
        return Math.Clamp((int)Math.Floor(completed * 100.0 / total), 0, 99);
    }

    /// <summary>
    /// 计算下载阶段收尾时的子进度。所有条目都已尝试不代表所有 Mod 都安装成功；
    /// 存在失败项时必须保留未满状态，避免任务页提前显示完整下载。
    /// </summary>
    internal static int CalculateFinalSubProgress(int total, int failed)
    {
        if (total <= 0)
        {
            return -1;
        }

        return failed > 0 ? 99 : 100;
    }

    /// <summary>
    /// 尝试解包双重压缩的 zip（GitHub 发布的 SMAPI 安装包名含 double-zipped）。
    /// 如果 zip 内只包含 zip 文件，解压外层得到内层 zip，用内层 zip 替换原文件。
    /// </summary>
    internal static void TryUnwrapDoubleZipped(string zipPath)
    {
        var tempPath = zipPath + ".inner.zip";
        try
        {
            using (var stream = File.OpenRead(zipPath))
            using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read))
            {
                // 检查是否所有 entry 都是 .zip 文件（double-zipped 特征）
                var zipEntries = zip.Entries
                    .Where(e => e.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var fileEntries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
                if (zipEntries.Count != 1 || fileEntries.Count != 1)
                {
                    return; // 不是 double-zipped
                }

                // 解压第一个 zip entry 到临时文件；必须先释放 ZipArchive，
                // 否则 Windows 仍持有外层 zip 的句柄，替换原文件会失败。
                using var entryStream = zipEntries[0].Open();
                using var output = File.Create(tempPath);
                entryStream.CopyTo(output);
            }

            File.Copy(tempPath, zipPath, true);
        }
        catch
        {
            // best-effort，失败则保持原文件，后续校验会报错
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    /// <summary>
    /// 将缓存或下载得到的 SMAPI 归一化为真正的安装包。
    /// Nexus 历史缓存可能保存 GitHub 的 double-zipped 外层 ZIP；必须在
    /// install.dat 校验前先拆开，否则按 ModID 扫描缓存时会被误判为无效。
    /// </summary>
    internal static bool TryNormalizeSmapiArchive(string archivePath)
    {
        if (IsSmapiInstallerZip(archivePath))
        {
            return true;
        }

        TryUnwrapDoubleZipped(archivePath);
        return IsSmapiInstallerZip(archivePath);
    }

    private static string NormalizeSmapiVersionForFileName(string? rawVersion)
    {
        var version = rawVersion?.Trim() ?? string.Empty;
        if (version.StartsWith("SMAPI ", StringComparison.OrdinalIgnoreCase))
        {
            version = version["SMAPI ".Length..].Trim();
        }

        return InstanceRuntimePathResolver.SanitizeFileNameComponent(version, "latest");
    }

    /// <summary>校验 zip 文件完整性：存在、大小合理、可被 ZipArchive 打开。</summary>
    private static bool IsValidZipFile(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length <= 0)
            return false;
        try
        {
            using var stream = File.OpenRead(path);
            using var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
            return zip.Entries.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidModArchive(string path)
    {
        if (ArchiveExtractor.IsSevenZip(path))
        {
            try
            {
                using var archive = SharpCompress.Archives.SevenZip.SevenZipArchive.OpenArchive(path);
                return archive.Entries.Any(entry => !entry.IsDirectory);
            }
            catch
            {
                return false;
            }
        }

        return IsValidZipFile(path);
    }

    private static void ExtractModArchive(string archivePath, string destinationDirectory)
    {
        if (ArchiveExtractor.IsSevenZip(archivePath))
        {
            ArchiveExtractor.ExtractSevenZipToDirectory(archivePath, destinationDirectory);
            return;
        }

        ZipExtractor.ExtractToDirectory(archivePath, destinationDirectory);
    }

    /// <summary>
    /// 校验 zip 是否为 SMAPI 官方安装包：检查是否存在 install.dat（SMAPI 4.x 结构为
    /// "SMAPI x.x.x installer/internal/&lt;platform&gt;/install.dat"）。
    /// 防止缓存污染或下载错误导致传入非 SMAPI 包给 InstallFromZipAsync。
    /// </summary>
    private static bool IsSmapiInstallerZip(string path)
    {
        if (!IsValidZipFile(path))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
            return zip.Entries.Any(e =>
                e.Name.Equals("install.dat", StringComparison.OrdinalIgnoreCase) &&
                e.FullName.IndexOf("internal", StringComparison.OrdinalIgnoreCase) >= 0);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>从 sources.json 的单个条目下载 Mod。支持直链、Nexus 和 Curseforge 来源。</summary>
    private async Task<ModSourceDownloadResult> DownloadModFromSourceWithRetryAsync(
        JsonElement source,
        string modsPath,
        CancellationToken ct,
        Action<ModpackInstallProgress>? onProgress)
    {
        const int maxAttempts = 3;
        Exception? lastException = null;
        var modName = GetJsonString(
            source,
            "name",
            "modName",
            "id",
            "uniqueId",
            "directoryName",
            "directory") ?? "unknown";

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var result = await DownloadModFromSourceAsync(source, modsPath, ct, onProgress);
                if (result.IsSuccess ||
                    attempt >= maxAttempts ||
                    !ShouldRetryModSourceFailure(result.Message))
                {
                    return result;
                }

                // 解析器将一部分网络/CDN/归档问题转成 Failed 而不是抛异常；
                // 这些结果同样需要进入有限重试。永久性问题（缺少来源、登录、
                // 浏览器回调取消等）由 ShouldRetryModSourceFailure 排除，避免重复
                // 弹窗或重复等待同一个 NXM 回调。
                lastException = new InvalidOperationException(result.Message);
                onProgress?.Invoke(new ModpackInstallProgress
                {
                    Percent = 47,
                    StepText = "步骤 4/6: 下载未打包 Mod",
                    SubProgressText = $"{modName}: 第 {attempt + 1}/{maxAttempts} 次重试（{result.Message}）"
                });
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (attempt >= maxAttempts)
                {
                    break;
                }

                onProgress?.Invoke(new ModpackInstallProgress
                {
                    Percent = 47,
                    StepText = "步骤 4/6: 下载未打包 Mod",
                    SubProgressText = $"{modName}: 第 {attempt + 1}/{maxAttempts} 次重试（{ex.Message}）"
                });
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct);
            }
        }

        return ModSourceDownloadResult.Failed(
            lastException?.Message ?? "Mod 下载发生未知异常");
    }

    /// <summary>
    /// 判断单个 Mod 来源失败是否值得重试。
    ///
    /// 下载/解析链路为了让整合包继续处理其它条目，会把很多失败包装成
    /// ModSourceDownloadResult.Failed。不能只在 catch 中重试，否则网络抖动、
    /// CDN 签名短暂失效和归档校验失败都会变成一次性失败；但来源缺失、用户取消
    /// 浏览器回调或登录要求是确定性问题，重试只会重复等待/弹窗。
    /// </summary>
    internal static bool ShouldRetryModSourceFailure(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return true;
        }

        var permanentMarkers = new[]
        {
            "缺少可用下载来源",
            "来源缺失",
            "未识别的 Mod 下载来源",
            "source 字段缺失",
            "未收到有效的 Nexus NXM 文件回调",
            "未收到 Nexus NXM 回调",
            "未收到浏览器 NXM 回调",
            "浏览器下载回退超时",
            "浏览器回退链接无效",
            "请登录",
            "需要登录",
            "已取消",
            "取消"
        };

        return !permanentMarkers.Any(marker =>
            message.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>从 sources.json 的单个条目下载 Mod。支持直链、Nexus 和 Curseforge 来源。</summary>
    private async Task<ModSourceDownloadResult> DownloadModFromSourceAsync(
        JsonElement source,
        string modsPath,
        CancellationToken ct,
        Action<ModpackInstallProgress>? onProgress = null)
    {
        if (!TryGetModSourceDescriptor(source, out var descriptor))
        {
            return ModSourceDownloadResult.Failed("source 字段缺失或格式不受支持");
        }

        var sourceText = descriptor.DownloadUrl;
        var downloadUrl = descriptor.DownloadUrl;
        if (!string.IsNullOrWhiteSpace(downloadUrl) &&
            downloadUrl.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
        {
            sourceText = downloadUrl;
            downloadUrl = null;
        }
        var modName = GetJsonString(
            source,
            "name",
            "modName",
            "id",
            "uniqueId",
            "directoryName",
            "directory") ?? "unknown";
        // 导出包会携带原始目录名，优先复用它以便 settings/Mods/<目录名> 能准确覆盖。
        var modDirectoryName = GetJsonString(source, "directoryName");
        var safeModName = SanitizeModDirectoryName(modDirectoryName, modName);

        var platform = descriptor.Platform ?? string.Empty;
        var projectId = descriptor.ProjectId;
        var fileId = descriptor.FileId;
        var repository = descriptor.Repository;

        if (IsGitHubPlatform(platform) &&
            string.IsNullOrWhiteSpace(downloadUrl) &&
            RemoteCatalogService.TryNormalizeGitHubRepository(repository, out var normalizedRepository))
        {
            var githubRelease = await _remoteCatalogService.CheckGitHubModUpdateAsync(
                normalizedRepository,
                string.Empty,
                ct);
            if (!githubRelease.IsChecked || string.IsNullOrWhiteSpace(githubRelease.DownloadUrl))
            {
                return ModSourceDownloadResult.Failed(
                    string.IsNullOrWhiteSpace(githubRelease.Message)
                        ? $"GitHub 仓库 {normalizedRepository} 没有可下载的稳定 Release"
                        : githubRelease.Message);
            }

            repository = normalizedRepository;
            downloadUrl = githubRelease.DownloadUrl;
        }

        if (IsCurseforgePlatform(platform) &&
            TryParsePositiveLong(projectId, out var cachedProjectId) &&
            TryParsePositiveLong(fileId, out var cachedCurseforgeFileId) &&
            TryInstallCachedCurseforgeMod(cachedProjectId, cachedCurseforgeFileId, modsPath, safeModName))
        {
            return ModSourceDownloadResult.Success();
        }

        // Nexus 来源的 downloadUrl 可能是 mods/{id}?tab=files&file_id={id}
        // 页面，而不是 CDN 压缩包。把页面 URL 解析成稳定 ID 后交给 Nexus
        // resolver，避免把 HTML 保存到下载目录再报“解压失败”。
        if (TryParseNexusPageIds(downloadUrl, out var pageModId, out var pageFileId))
        {
            platform = "NexusMods";
            if (string.IsNullOrWhiteSpace(projectId))
            {
                projectId = pageModId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (string.IsNullOrWhiteSpace(fileId))
            {
                fileId = pageFileId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            downloadUrl = null;
        }
        else if (NexusSourceParser.TryParseModId(downloadUrl, out var pageOnlyModId))
        {
            // Nexus 的 mods/{id}?tab=files 页面不是压缩包。旧导出可能只有
            // 页面地址或 Mod ID，没有 FileID；清空 downloadUrl 后进入下方
            // “按 Mod ID 查询/浏览器回调”分支，避免把 HTML 保存成 zip。
            platform = "NexusMods";
            if (string.IsNullOrWhiteSpace(projectId))
            {
                projectId = pageOnlyModId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            downloadUrl = null;
        }

        // 早期来源凭证有时只保存两个数字 ID，没有平台字段；历史实现按 Nexus
        // 处理，这里保留该兼容行为，且必须放在直链分支之前，确保安装成功后
        // 仍以 Nexus 来源写回并参与缓存/导出，而不是被记录成 Direct。
        if (string.IsNullOrWhiteSpace(platform) &&
            TryParsePositiveLong(projectId, out _) &&
            TryParsePositiveLong(fileId, out _))
        {
            platform = "NexusMods";
        }

        // 直链下载。CurseForge 的详情页/文件页虽然也是 HTTP URL，但响应通常是
        // HTML；带 project/file ID 的条目必须先走下面的 curse.tools/CDN 解析，
        // 否则会把网页保存成 zip，最终表现为“下载成功但 Mod 安装失败”。
        var isCurseforgePageUrl = IsCurseforgePlatform(platform) &&
                                  !CanUseDirectDownloadUrl(platform, downloadUrl);
        if (!string.IsNullOrWhiteSpace(downloadUrl) &&
            Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) &&
            (uri.Scheme == "http" || uri.Scheme == "https") &&
            !isCurseforgePageUrl)
        {
            // Nexus 详情/历史导出可能同时保存真实 CDN 直链和 ModID/FileID。
            // 直链是短期签名地址，不能因为它存在就绕过稳定缓存；否则再次
            // 导入会重新请求 CDN，甚至在签名过期后误报下载失败。
            if (IsNexusPlatform(platform) &&
                TryParsePositiveLong(projectId, out var directNexusModId) &&
                TryParsePositiveLong(fileId, out var directNexusFileId) &&
                NexusDownloadCache.TryGet(
                    directNexusModId,
                    directNexusFileId,
                    out var cachedNexusPath,
                    IsValidModArchiveFile) &&
                InstallDownloadedModArchive(
                    cachedNexusPath,
                    modsPath,
                    safeModName,
                    out var cachedInstalledNames))
            {
                WriteModpackSourceCredentialsWithRepository(
                    modsPath,
                    cachedInstalledNames,
                    "NexusMods",
                    directNexusModId,
                    directNexusFileId,
                    downloadUrl,
                    descriptor.FileName ?? Path.GetFileName(cachedNexusPath));
                return ModSourceDownloadResult.Success();
            }

            return await DownloadModFromUrlAsync(
                    downloadUrl,
                    safeModName,
                    modsPath,
                    ct,
                    descriptor.FileName,
                    sourcePlatform: string.IsNullOrWhiteSpace(platform) ? descriptor.Platform : platform,
                    sourceProjectId: ParsePositiveLongOrNull(projectId),
                    sourceFileId: ParsePositiveLongOrNull(fileId),
                    sourceRepository: repository)
                ? ModSourceDownloadResult.Success()
                : ModSourceDownloadResult.Failed("直链下载或 Mod 解压校验失败");
        }

        // 兼容 source 直接保存为 nxm://... 的旧清单。
        NxmLinkInfo? parsedNxm = null;
        if (!string.IsNullOrWhiteSpace(sourceText) &&
            _nxmLinkParser.TryParse(sourceText, out var directNxm, out _))
        {
            parsedNxm = directNxm;
        }

        if (parsedNxm != null)
        {
            platform = "NexusMods";
            projectId = parsedNxm.ModId.ToString();
            fileId = parsedNxm.FileId.ToString();
        }

        // CurseForge 清单里常见的是网页地址（例如
        // www.curseforge.com/.../files/<id>），它不是压缩包直链。若同时有
        // projectId/fileId，必须优先通过 curse.tools 解析真实 CDN 地址，
        // 否则会把 HTML 页面保存成 zip，最终表现为“下载成功但 Mod 安装失败”。
        if (IsCurseforgePlatform(platform) &&
            TryParsePositiveLong(projectId, out var curseforgeProjectId))
        {
            var hasCurseforgeFileId = TryParsePositiveLong(fileId, out var curseforgeFileId);
            var directCurseforgeUrl = IsLikelyDirectDownloadUrl(downloadUrl)
                ? downloadUrl ?? string.Empty
                : string.Empty;
            // 已有真实 CDN/归档直链时必须原样使用。尤其是只有 projectId、
            // 没有 fileId 的旧清单，不能为了“补解析”调用列表接口并误选成该项目的
            // 另一个文件。网页/API 地址才交给 resolver 解析。
            var resolvedCurseforgeUrl = !string.IsNullOrWhiteSpace(directCurseforgeUrl)
                ? directCurseforgeUrl
                : await _remoteCatalogService.ResolveCurseforgeFileDownloadUrlAsync(
                    curseforgeProjectId,
                    hasCurseforgeFileId ? curseforgeFileId : 0,
                    string.Empty,
                    ct);
            if (!string.IsNullOrWhiteSpace(resolvedCurseforgeUrl))
            {
                downloadUrl = resolvedCurseforgeUrl;
                if (string.IsNullOrWhiteSpace(fileId) &&
                    TryParseCurseforgeFileIdFromCdnUrl(downloadUrl, out var cdnFileId))
                {
                    // 仅有 projectId 的旧来源也要把解析出的真实 FileID 写回
                    // svl-source.json，这样下一次导出/导入才能精确复用文件。
                    fileId = cdnFileId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            else
            {
                return ModSourceDownloadResult.Failed("CurseForge 文件下载地址解析失败");
            }
        }

        // Curseforge 来源：projectId/fileId 需要先通过 curse.tools 解析 CDN 地址。
        if (IsCurseforgePlatform(platform) &&
            TryParsePositiveLong(projectId, out var resolvedCurseforgeProjectId))
        {
            // 上面的分支已经把网页/旧格式来源解析成 CDN 地址；这里保留
            // 直链来源的统一安装分支，并确保来源 ID 仍写入安装目录。
            if (!string.IsNullOrWhiteSpace(downloadUrl) &&
                IsLikelyDirectDownloadUrl(downloadUrl))
            {
                return await DownloadModFromUrlAsync(
                        downloadUrl,
                        safeModName,
                        modsPath,
                        ct,
                        descriptor.FileName,
                        sourcePlatform: "Curseforge",
                        sourceProjectId: resolvedCurseforgeProjectId,
                        sourceFileId: ParsePositiveLongOrNull(fileId))
                    ? ModSourceDownloadResult.Success()
                    : ModSourceDownloadResult.Failed("CurseForge 下载或 Mod 解压校验失败");
            }

            return ModSourceDownloadResult.Failed("CurseForge 文件下载地址解析失败");
        }

        // Nexus NXM 链接（兼容旧的 modId/fileId 字段，也接受新的 projectId/fileId）。
        var modId = projectId;

        if (IsNexusPlatform(platform) && TryParsePositiveLong(modId, out var nxmModId))
        {
            var hasFileId = TryParsePositiveLong(fileId, out var nxmFileId);
            if (!hasFileId)
            {
                // 旧导入包可能只有 ModID；如果此前已经下载过该 Mod，缓存文件名
                // 仍带有真实 FileID。先复用缓存并回写 FileID，避免无谓地打开
                // Nexus 页面等待浏览器回调。
                if (NexusDownloadCache.TryGetAnyForMod(nxmModId, IsValidModArchiveFile, out var cachedAnyPath) &&
                    InstallDownloadedModArchive(
                        cachedAnyPath,
                        modsPath,
                        safeModName,
                        out var cachedAnyInstalledNames))
                {
                    var cachedFileId = TryGetNexusFileIdFromCachePath(
                        cachedAnyPath,
                        nxmModId,
                        out var parsedCachedFileId)
                        ? parsedCachedFileId
                        : 0;
                    WriteModpackSourceCredentials(
                        modsPath,
                        cachedAnyInstalledNames,
                        "NexusMods",
                        nxmModId,
                        cachedFileId);
                    return ModSourceDownloadResult.Success();
                }

                var settings = _settingsStore.Load();
                var latest = await _nexusResolver.ResolveLatestDownloadUrlAsync(
                    nxmModId, "stardewvalley", settings, ct);
                if (latest.IsSuccess)
                {
                    var installed = await DownloadModFromUrlAsync(
                        latest.DownloadUrl,
                        safeModName,
                        modsPath,
                        ct,
                        latest.FileName,
                        sourcePlatform: "NexusMods",
                        sourceProjectId: nxmModId,
                        sourceFileId: latest.FileId);
                    if (installed)
                    {
                        return ModSourceDownloadResult.Success();
                    }

                    return ModSourceDownloadResult.Failed("Nexus 文件下载或 Mod 解压校验失败");
                }

                // 旧版导出包通常只有 modId，没有 fileId。未登录 API 时无法查询
                // files.json，但浏览器 Manual Download 会回传带 fileId/key 的 NXM
                // 链接，因此这里必须走“按 Mod ID 匹配任意文件”的回退路径。
                if (_browserFallback != null)
                {
                    var browserUrl = $"https://www.nexusmods.com/stardewvalley/mods/{nxmModId}?tab=files&nmm=1";
                    var callback = await _browserFallback.WaitForNxmModCallbackAsync(
                        nxmModId,
                        browserUrl,
                        message => onProgress?.Invoke(new ModpackInstallProgress
                        {
                            Percent = 47,
                            StepText = "步骤 4/6: 下载未打包 Mod",
                            SubProgressText = $"{modName}: {message}"
                        }),
                        ct);

                    if (!string.IsNullOrWhiteSpace(callback) &&
                        _nxmLinkParser.TryParse(callback, out var callbackInfo, out _))
                    {
                        var resolvedFromBrowser = await _nexusResolver.ResolveDownloadUrlAsync(
                            callbackInfo, settings.NexusApiKey, settings.NexusOAuthAccessToken, ct);
                        if (resolvedFromBrowser.IsSuccess)
                        {
                            var installed = await DownloadModFromUrlAsync(
                                resolvedFromBrowser.DownloadUrl,
                                safeModName,
                                modsPath,
                                ct,
                                resolvedFromBrowser.FileName,
                                sourcePlatform: "NexusMods",
                                sourceProjectId: nxmModId,
                                sourceFileId: resolvedFromBrowser.FileId);
                            if (installed)
                            {
                                return ModSourceDownloadResult.Success();
                            }

                            return ModSourceDownloadResult.Failed("浏览器回调后的 Nexus 文件下载或 Mod 解压校验失败");
                        }

                        return ModSourceDownloadResult.Failed(resolvedFromBrowser.Message);
                    }

                    return ModSourceDownloadResult.Failed("未收到有效的 Nexus NXM 文件回调");
                }

                return ModSourceDownloadResult.Failed(latest.Message);
            }

            // Nexus 缓存优先，命中时不再请求 API，也不再打开浏览器。
            if (NexusDownloadCache.TryGet(nxmModId, nxmFileId, out var cachedPath, IsValidModArchiveFile) &&
                InstallDownloadedModArchive(cachedPath, modsPath, safeModName, out var cachedInstalledNames))
            {
                // 缓存命中也必须写回来源凭证。否则第一次下载后的缓存重装虽然成功，
                // 但 Mod 目录没有 FileID，后续导出会再次丢失 Nexus 精确文件信息。
                WriteModpackSourceCredentialsWithRepository(
                    modsPath,
                    cachedInstalledNames,
                    "NexusMods",
                    nxmModId,
                    nxmFileId);
                return ModSourceDownloadResult.Success();
            }

            // 如果来源本身就是浏览器回传的 NXM 链接，必须保留其中的 key/expires/user_id。
            // 重新拼接一个不带凭据的 nxm:// 地址会把一次性下载凭据丢掉，导致未登录
            // 时明明已有可用 NXM 仍被错误地再次判定为“需要打开浏览器”。
            var nxmInfo = BuildNexusDownloadInfo(nxmModId, nxmFileId, parsedNxm);
            if (nxmInfo.ModId == nxmModId && nxmInfo.FileId == nxmFileId)
            {
                var settings = _settingsStore.Load();
                var resolved = await _nexusResolver.ResolveDownloadUrlAsync(
                    nxmInfo, settings.NexusApiKey, settings.NexusOAuthAccessToken, ct);

                if (!resolved.IsSuccess && _browserFallback != null)
                {
                    var browserUrl = $"https://www.nexusmods.com/stardewvalley/mods/{nxmModId}?tab=files&file_id={nxmFileId}&nmm=1";
                    var callback = await _browserFallback.WaitForNxmCallbackAsync(
                        nxmModId,
                        nxmFileId,
                        browserUrl,
                        message => onProgress?.Invoke(new ModpackInstallProgress
                        {
                            Percent = 47,
                            StepText = "步骤 4/6: 下载未打包 Mod",
                            SubProgressText = $"{modName}: {message}"
                        }),
                        ct);
                    if (!string.IsNullOrWhiteSpace(callback) &&
                        _nxmLinkParser.TryParse(callback, out var callbackInfo, out _))
                    {
                        resolved = await _nexusResolver.ResolveDownloadUrlAsync(
                            callbackInfo, settings.NexusApiKey, settings.NexusOAuthAccessToken, ct);
                    }
                }

                if (resolved.IsSuccess)
                {
                    return await DownloadModFromUrlAsync(
                        resolved.DownloadUrl,
                        safeModName,
                        modsPath,
                        ct,
                        resolved.FileName,
                        sourcePlatform: "NexusMods",
                        sourceProjectId: nxmModId,
                        sourceFileId: nxmFileId)
                        ? ModSourceDownloadResult.Success()
                        : ModSourceDownloadResult.Failed("Nexus 文件下载或 Mod 解压校验失败");
                }

                return ModSourceDownloadResult.Failed(resolved.Message);
            }
        }

        return ModSourceDownloadResult.Failed("未识别的 Mod 下载来源");
    }

    /// <summary>
    /// 构造 Nexus 下载请求时保留来源链接中的一次性凭据。
    /// 清单可能只保存了 ModID/FileID，此时使用不带凭据的标准链接；
    /// 如果来源本身是浏览器回传的 NXM 链接，则不能重新拼接链接，否则
    /// key/expires/user_id 会丢失，未登录状态下会被错误地再次要求打开浏览器。
    /// </summary>
    internal static NxmLinkInfo BuildNexusDownloadInfo(
        long modId,
        long fileId,
        NxmLinkInfo? parsedNxm)
    {
        if (parsedNxm != null &&
            parsedNxm.ResourceType == NxmResourceType.ModFile &&
            parsedNxm.ModId == modId &&
            parsedNxm.FileId == fileId)
        {
            return parsedNxm;
        }

        return new NxmLinkInfo
        {
            ResourceType = NxmResourceType.ModFile,
            GameDomain = "stardewvalley",
            ModId = modId,
            FileId = fileId
        };
    }

    private static bool TryParseNexusPageIds(
        string? url,
        out long modId,
        out long fileId)
    {
        return NexusSourceParser.TryParsePageIds(url, out modId, out fileId);
    }

    /// <summary>
    /// 读取 sources.json 的来源信息并兼容历史格式：
    ///
    /// 1. source: { platform, projectId, fileId }
    /// 2. source: { source: "Nexus", collection, file }
    /// 3. source: "Nexus"，ID 位于条目顶层
    /// 4. platform/projectId/fileId 或 downloadUrl 直接位于条目顶层
    /// </summary>
    private static bool TryGetModSourceDescriptor(
        JsonElement sourceEntry,
        out ModSourceDescriptor descriptor)
    {
        descriptor = new ModSourceDescriptor(null, null, null, null, null, null, null);
        if (sourceEntry.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var hasSource = TryGetJsonPropertyIgnoreCase(sourceEntry, "source", out var sourceValue);
        var isNestedObject = hasSource && sourceValue.ValueKind == JsonValueKind.Object;
        var credential = isNestedObject
            ? sourceValue
            : sourceEntry;

        var sourceText = hasSource && sourceValue.ValueKind == JsonValueKind.String
            ? sourceValue.GetString()
            : null;

        var platform = GetJsonString(
            credential,
            "platform",
            "site",
            "provider",
            "service",
            "source",
                "type",
                "sourceType");
        if (string.IsNullOrWhiteSpace(platform) && isNestedObject)
        {
            platform = GetJsonString(
                sourceEntry,
                "platform",
                "site",
                "provider",
                "service",
                "type",
                "sourceType");
        }

        var downloadUrl = GetJsonString(credential, "downloadUrl", "download_url", "url", "uri", "nxmUrl", "nxm_url");
        if (string.IsNullOrWhiteSpace(downloadUrl) && isNestedObject)
        {
            downloadUrl = GetJsonString(sourceEntry, "downloadUrl", "download_url", "url", "uri", "nxmUrl", "nxm_url");
        }

        var fileName = GetJsonString(credential, "fileName", "file_name", "filename");
        if (string.IsNullOrWhiteSpace(fileName) && isNestedObject)
        {
            fileName = GetJsonString(sourceEntry, "fileName", "file_name", "filename");
        }

        // Nexus Collection/第三方导出常把真实文件名写成 logicalFilename，
        // 而不是 svl-source.json 使用的 fileName。即使另一个字段已经提供了
        // 可读文件名，也要保留这个提示用于从 “File 7448774_...” 补回
        // FileID；否则导入时会退化成按 ModID 查询最新文件或再次打开浏览器。
        var logicalFileName = GetJsonString(
            credential,
            "logicalFilename",
            "logical_filename",
            "logicalFileName");
        if (string.IsNullOrWhiteSpace(logicalFileName) && isNestedObject)
        {
            logicalFileName = GetJsonString(
                sourceEntry,
                "logicalFilename",
                "logical_filename",
                "logicalFileName");
        }
        if (string.IsNullOrWhiteSpace(fileName) && !string.IsNullOrWhiteSpace(logicalFileName))
        {
            fileName = logicalFileName;
        }

        var repository = GetJsonString(
            credential,
            "repository",
            "repo",
            "githubRepository",
            "github_repository");
        if (string.IsNullOrWhiteSpace(repository) && isNestedObject)
        {
            repository = GetJsonString(
                sourceEntry,
                "repository",
                "repo",
                "githubRepository",
                "github_repository");
        }

        var modId = GetPositiveIdString(credential, "modId", "mod_id", "nexusModId", "nexus_mod_id");
        if (string.IsNullOrWhiteSpace(modId) && isNestedObject)
        {
            modId = GetPositiveIdString(sourceEntry, "modId", "mod_id", "nexusModId", "nexus_mod_id");
        }

        // Nexus 旧格式叫 collection，CurseForge/新格式通常叫 projectId。
        var projectId = GetPositiveIdString(
            credential,
            "projectId",
            "project_id",
            "project",
            "projectID",
            "modProjectId",
            "mod_project_id",
            "collection",
            "curseforgeProjectId",
            "curseforge_project_id");
        if (string.IsNullOrWhiteSpace(projectId) && isNestedObject)
        {
            projectId = GetPositiveIdString(
                sourceEntry,
                "projectId",
                "project_id",
                "project",
                "projectID",
                "modProjectId",
                "mod_project_id",
                "collection",
                "curseforgeProjectId",
                "curseforge_project_id");
        }

        if (string.IsNullOrWhiteSpace(projectId))
        {
            projectId = modId;
        }

        var fileId = GetPositiveIdString(credential, "fileId", "file_id", "file", "nexusFileId", "nexus_file_id");
        if (string.IsNullOrWhiteSpace(fileId) && isNestedObject)
        {
            fileId = GetPositiveIdString(sourceEntry, "fileId", "file_id", "file", "nexusFileId", "nexus_file_id");
        }

        if (!string.IsNullOrWhiteSpace(sourceText))
        {
            if (Uri.TryCreate(sourceText, UriKind.Absolute, out var sourceUri) &&
                (sourceUri.Scheme == Uri.UriSchemeHttp ||
                 sourceUri.Scheme == Uri.UriSchemeHttps ||
                 sourceUri.Scheme.Equals("nxm", StringComparison.OrdinalIgnoreCase)))
            {
                if (string.IsNullOrWhiteSpace(downloadUrl))
                {
                    downloadUrl = sourceText;
                }
            }
            else if (string.IsNullOrWhiteSpace(platform))
            {
                platform = sourceText;
            }

            if (sourceText.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
            {
                platform = "GitHub";
                repository = sourceText["github:".Length..].Trim();
            }
        }

        if (string.Equals(platform, "github", StringComparison.OrdinalIgnoreCase) &&
            RemoteCatalogService.TryNormalizeGitHubRepository(repository, out var normalizedRepository))
        {
            platform = "GitHub";
            repository = normalizedRepository;
        }

        // 仅当 Unknown 同时伴随 URL 时将其视为缺失，允许从 URL 推断平台；
        // 没有 URL 的 Unknown + 两个数字仍然保持不可执行，不能武断地当作 Nexus。
        if (IsUnknownSourceToken(platform) && !string.IsNullOrWhiteSpace(downloadUrl))
        {
            platform = string.Empty;
        }

        // 某些旧导出把 URL 填在 platform/source 字段里。
        if (string.IsNullOrWhiteSpace(downloadUrl) &&
            Uri.TryCreate(platform, UriKind.Absolute, out var platformUri) &&
            (platformUri.Scheme == Uri.UriSchemeHttp ||
             platformUri.Scheme == Uri.UriSchemeHttps ||
             platformUri.Scheme.Equals("nxm", StringComparison.OrdinalIgnoreCase)))
        {
            downloadUrl = platform;
            platform = string.Empty;
        }

        // 一些旧导出只保留 CurseForge 的 CDN/网页 URL，没有写 platform；
        // 必须在直链分支前补出来源，否则虽然能下载，安装后仍无法写回
        // projectId/fileId，下一次导出会丢失 CurseForge 信息。
        if (string.IsNullOrWhiteSpace(platform) && IsLikelyCurseforgeUrl(downloadUrl))
        {
            platform = "Curseforge";
        }

        // CurseForge 的导出/旧来源有时只保留真实 Forge CDN 直链和
        // projectId，没有单独保存 fileId。CDN 路径中的
        // /files/{前段}/{后 3 位}/ 实际就是完整 FileID；在统一描述符阶段
        // 回填后，直链安装、来源写回和后续导出都会使用同一个完整 FileID。
        if (IsCurseforgePlatform(platform) &&
            string.IsNullOrWhiteSpace(fileId) &&
            TryParseCurseforgeFileIdFromCdnUrl(downloadUrl, out var cdnFileId))
        {
            fileId = cdnFileId.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }

        // Nexus 页面地址是稳定来源，不是可直接解压的压缩包；在清单读取阶段
        // 就回填页面中的 ModID/FileID，后续安装可直接进入 Nexus resolver，
        // 也避免空字符串字段在安装阶段被当成“没有来源”。
        if (TryParseNexusPageIds(downloadUrl, out var nexusPageModId, out var nexusPageFileId))
        {
            platform = "NexusMods";
            if (string.IsNullOrWhiteSpace(projectId))
            {
                projectId = nexusPageModId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (string.IsNullOrWhiteSpace(fileId))
            {
                fileId = nexusPageFileId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        else if (NexusSourceParser.TryParseModId(downloadUrl, out var nexusOnlyModId))
        {
            platform = "NexusMods";
            if (string.IsNullOrWhiteSpace(projectId))
            {
                projectId = nexusOnlyModId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        // 旧来源凭证有时只保存两个数字 ID，没有平台字段。提前归一化，
        // 让调用方在进入直链下载前就能保留 Nexus 身份并复用缓存/导出。
        if (string.IsNullOrWhiteSpace(platform) &&
            TryParsePositiveLong(projectId, out _) &&
            TryParsePositiveLong(fileId, out _))
        {
            platform = "NexusMods";
        }

        // 早期导出/整合包清单有时只保留一个可读性较差的来源标识，典型形式为
        // "cf-1012214-5312529"，它可能出现在 source、name 或 directoryName 中，
        // 而不是拆成 platform/projectId/fileId。必须在进入下载分支前恢复这两个 ID，
        // 否则 Content Patcher 等 Mod 会被统一判定为“没有来源”。
        if (TryParseCurseforgeToken(
                new[]
                {
                    sourceText,
                    platform,
                    GetJsonString(sourceEntry, "name", "modName", "id", "directoryName"),
                    GetJsonString(credential, "name", "modName", "id", "directoryName"),
                    fileName,
                    GetJsonString(credential, "file"),
                    isNestedObject ? GetJsonString(sourceEntry, "file") : null
                },
                out var canonicalCurseforgeProjectId,
                out var canonicalCurseforgeFileId))
        {
            platform = "Curseforge";
            // 旧导出不仅可能缺字段，也可能明确写入空字符串；两者都应由
            // canonical token 恢复，不能只使用 null 合并。
            if (string.IsNullOrWhiteSpace(projectId))
            {
                projectId = canonicalCurseforgeProjectId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            if (string.IsNullOrWhiteSpace(fileId))
            {
                fileId = canonicalCurseforgeFileId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        // 第三方导出有时只保存 CurseForge 网页/API/CDN 地址。即使没有
        // 单独的 projectId/fileId 字段，地址中的 /mods/<project>/files/<file>
        // 或 Forge CDN 的 /files/<前段>/<后 3 位>/ 仍是稳定来源信息；在描述符
        // 阶段回填，后续缓存、安装和 svl-source.json 才能共享同一组 ID。
        if ((string.IsNullOrWhiteSpace(platform) || IsCurseforgePlatform(platform)) &&
            TryParseCurseforgeIdsFromUrl(
                downloadUrl,
                out var urlCurseforgeProjectId,
                out var urlCurseforgeFileId))
        {
            platform = "Curseforge";
            if (string.IsNullOrWhiteSpace(projectId) && urlCurseforgeProjectId > 0)
            {
                projectId = urlCurseforgeProjectId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            if (urlCurseforgeFileId > 0 &&
                (!TryParsePositiveLong(fileId, out var existingUrlFileId) ||
                 existingUrlFileId != urlCurseforgeFileId))
            {
                fileId = urlCurseforgeFileId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        // Nexus 下载文件名通常形如“File 7448774_ Content Patcher 2.9.0 2.9.0.zip”。
        // 当旧清单已有 ModID/页面 URL、但没有 FileID 时，回填文件名中的 ID，避免
        // 退化到打开 Nexus 页面等待浏览器回调。
        var nexusFileNameHints = new[]
        {
            fileName,
            logicalFileName,
            GetJsonString(credential, "file"),
            isNestedObject ? GetJsonString(sourceEntry, "file") : null
        };

        if (IsNexusPlatform(platform) && string.IsNullOrWhiteSpace(fileId))
        {
            foreach (var hint in nexusFileNameHints)
            {
                if (!TryParseNexusFileIdFromFileName(hint, out var fileNameNexusFileId))
                {
                    continue;
                }

                fileId = fileNameNexusFileId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                break;
            }
        }

        descriptor = new ModSourceDescriptor(
            platform,
            downloadUrl,
            fileName,
            modId,
            projectId,
            fileId,
            repository);
        return hasSource ||
               !string.IsNullOrWhiteSpace(platform) ||
               !string.IsNullOrWhiteSpace(downloadUrl) ||
               !string.IsNullOrWhiteSpace(projectId) ||
               !string.IsNullOrWhiteSpace(fileId) ||
               !string.IsNullOrWhiteSpace(repository);
    }

    private static long? ParsePositiveLongOrNull(string? value)
    {
        return TryParsePositiveLong(value, out var result) ? result : null;
    }

    /// <summary>
    /// 读取来源 ID 时跳过 0、负数和空字符串，继续尝试历史别名。
    /// 早期导出器常同时写出 { projectId: 0, project: 1012214 }，普通
    /// GetJsonString 会在第一个字段处停止，最终把一个可安装条目误报成无来源。
    /// </summary>
    private static string? GetPositiveIdString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetJsonPropertyIgnoreCase(element, propertyName, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number)
            {
                if (value.TryGetInt64(out var numericValue) && numericValue > 0)
                {
                    return numericValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()?.Trim();
                if (TryParsePositiveLong(text, out _))
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static bool IsUnknownSourceToken(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ||
               string.Equals(value.Trim(), "unknown", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value.Trim(), "未知", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNexusPlatform(string? platform)
    {
        return string.Equals(platform, "Nexus", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(platform, "NexusMods", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(platform, "Nexus Mod", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCurseforgePlatform(string? platform)
    {
        return string.Equals(platform, "Curseforge", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(platform, "CurseForge", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(platform, "Curse", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGitHubPlatform(string? platform)
    {
        return string.Equals(platform, "GitHub", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(platform, "Github", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseCurseforgeToken(
        IEnumerable<string?> values,
        out long projectId,
        out long fileId)
    {
        projectId = 0;
        fileId = 0;
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var match = System.Text.RegularExpressions.Regex.Match(
                value,
                @"(?:^|[^a-z0-9])(?:cf|curseforge)[-_ ](\d+)[-_ ](\d+)(?:[^0-9]|$)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success &&
                long.TryParse(match.Groups[1].Value, out projectId) &&
                long.TryParse(match.Groups[2].Value, out fileId) &&
                projectId > 0 &&
                fileId > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseNexusFileIdFromFileName(string? fileName, out long fileId)
    {
        return DownloadOptionIdentityParser.TryExtractFileId(fileName, out fileId);
    }

    internal static bool TryParseCurseforgeFileIdFromCdnUrl(string? value, out long fileId)
    {
        fileId = 0;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        var host = uri.Host.TrimEnd('.');
        if (!host.Equals("forgecdn.net", StringComparison.OrdinalIgnoreCase) &&
            !host.EndsWith(".forgecdn.net", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index + 2 < segments.Length; index++)
        {
            if (!segments[index].Equals("files", StringComparison.OrdinalIgnoreCase) ||
                !long.TryParse(segments[index + 1], out var thousands) ||
                !long.TryParse(segments[index + 2], out var remainder) ||
                thousands <= 0 ||
                remainder is < 0 or > 999)
            {
                continue;
            }

            fileId = checked(thousands * 1000 + remainder);
            return fileId > 0;
        }

        return false;
    }

    /// <summary>
    /// 从 CurseForge 网页/API/CDN 地址恢复稳定的项目 ID 和文件 ID。
    ///
    /// 常见来源包括：
    ///   /mods/1012214/files/5312529
    ///   /v1/cf/mods/1012214/files/5312529
    ///   /files/5312/529/ContentPatcher.zip
    ///   ?projectId=1012214&amp;fileId=5312529
    ///
    /// 页面 slug 本身不包含项目数字 ID，因此只在地址确实携带数字时返回成功。
    /// </summary>
    internal static bool TryParseCurseforgeIdsFromUrl(
        string? value,
        out long projectId,
        out long fileId)
    {
        projectId = 0;
        fileId = 0;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        var host = uri.Host.Trim().TrimEnd('.');
        var isCurseforgeHost = host.Equals("curseforge.com", StringComparison.OrdinalIgnoreCase) ||
                               host.EndsWith(".curseforge.com", StringComparison.OrdinalIgnoreCase) ||
                               host.Equals("curse.tools", StringComparison.OrdinalIgnoreCase) ||
                               host.EndsWith(".curse.tools", StringComparison.OrdinalIgnoreCase) ||
                               host.Equals("forgecdn.net", StringComparison.OrdinalIgnoreCase) ||
                               host.EndsWith(".forgecdn.net", StringComparison.OrdinalIgnoreCase);
        if (!isCurseforgeHost)
        {
            return false;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index + 1 < segments.Length; index++)
        {
            if (segments[index].Equals("mods", StringComparison.OrdinalIgnoreCase) &&
                TryParsePositiveLong(segments[index + 1], out var parsedProjectId))
            {
                projectId = parsedProjectId;
            }

            if (!segments[index].Equals("files", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryParsePositiveLong(segments[index + 1], out var firstFileSegment))
            {
                continue;
            }

            // Forge CDN 将完整 FileID 拆成 /files/<前段>/<后 3 位>/；
            // 网页/API 地址通常只在 files 后保留一个完整数字。
            if (host.Equals("forgecdn.net", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".forgecdn.net", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 2 < segments.Length &&
                    long.TryParse(segments[index + 2], out var remainder) &&
                    remainder is >= 0 and <= 999)
                {
                    try
                    {
                        fileId = checked(firstFileSegment * 1000 + remainder);
                    }
                    catch (OverflowException)
                    {
                        fileId = 0;
                    }
                }
            }
            else
            {
                fileId = firstFileSegment;
            }
        }

        // 某些 API 变体不把 ID 放在路径中，而是放在查询参数中。
        // 仅在值为正整数时接受，避免把 slug 或签名参数误认成来源 ID。
        var query = uri.Query;
        projectId = projectId > 0
            ? projectId
            : TryParsePositiveQueryId(query, "projectId", "project", "modId");
        fileId = fileId > 0
            ? fileId
            : TryParsePositiveQueryId(query, "fileId", "file");

        return projectId > 0 || fileId > 0;
    }

    private static long TryParsePositiveQueryId(string query, params string[] keys)
    {
        if (string.IsNullOrWhiteSpace(query) || keys.Length == 0)
        {
            return 0;
        }

        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0 || separator == part.Length - 1)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(part[..separator].Replace('+', ' '));
            if (!keys.Any(candidate => key.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var raw = Uri.UnescapeDataString(part[(separator + 1)..].Replace('+', ' '));
            if (TryParsePositiveLong(raw, out var result))
            {
                return result;
            }
        }

        return 0;
    }

    internal static bool IsLikelyCurseforgeUrl(string? value)
    {
        return DownloadUrlPolicy.IsLikelyCurseforgeUrl(value);
    }

    internal static bool IsLikelyDirectDownloadUrl(string? value)
    {
        return DownloadUrlPolicy.IsLikelyDirectDownloadUrl(value);
    }

    /// <summary>
    /// 判断来源 URL 是否可以直接作为压缩包下载。CurseForge 页面必须先解析
    /// project/file ID；其它平台保留历史上的归档扩展名或 /download 直链兼容规则。
    /// </summary>
    private static bool CanUseDirectDownloadUrl(string? platform, string? value)
    {
        return !IsCurseforgePlatform(platform) || IsLikelyDirectDownloadUrl(value);
    }

    private static bool HasActionableDownloadSource(JsonElement source)
    {
        if (!TryGetModSourceDescriptor(source, out var descriptor))
        {
            return false;
        }

        if (Uri.TryCreate(descriptor.DownloadUrl, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps ||
             uri.Scheme.Equals("nxm", StringComparison.OrdinalIgnoreCase)))
        {
            // CurseForge 文件页/API 地址虽然是 HTTP，但不能直接交给下载器。
            // 没有 ProjectID 时连 resolver 也无法定位文件，应在候选阶段明确报
            // “缺少来源”，而不是进入一次必然失败的下载流程。
            if (IsCurseforgePlatform(descriptor.Platform) &&
                !CanUseDirectDownloadUrl(descriptor.Platform, descriptor.DownloadUrl))
            {
                return TryParsePositiveLong(descriptor.ProjectId, out _);
            }

            return true;
        }

        var hasProjectId = TryParsePositiveLong(descriptor.ProjectId, out _);
        var hasFileId = TryParsePositiveLong(descriptor.FileId, out _);
        if (IsGitHubPlatform(descriptor.Platform))
        {
            return RemoteCatalogService.TryNormalizeGitHubRepository(descriptor.Repository, out _);
        }
        if (IsNexusPlatform(descriptor.Platform))
        {
            // Nexus 旧清单允许只保存 ModID，后续可通过 API/浏览器回调补齐
            // FileID；只有孤立 FileID 没有可解析的目标。
            return hasProjectId;
        }

        if (IsCurseforgePlatform(descriptor.Platform))
        {
            // CurseForge 的文件解析必须以 ProjectID 为入口；FileID 单独存在
            // 不能构造稳定的 curse.tools/CDN 请求。
            return hasProjectId;
        }

        // 没有平台字段的历史来源仅在同时具备两个 ID 时按 Nexus 兼容处理。
        return string.IsNullOrWhiteSpace(descriptor.Platform) && hasProjectId && hasFileId;
    }

    private async Task<bool> DownloadModFromUrlAsync(
        string downloadUrl,
        string safeModName,
        string modsPath,
        CancellationToken ct,
        string? suggestedFileName = null,
        long? nexusModId = null,
        long? nexusFileId = null,
        string? sourcePlatform = null,
        long? sourceProjectId = null,
        long? sourceFileId = null,
        string? sourceRepository = null)
    {
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return false;
        }

        // 同一整合包中可能有重复来源条目，或者用户在安装过程中重复触发导入。
        // 来源凭证命中时直接复用实际目录，避免第二次请求直链并覆盖已安装内容。
        if (sourceProjectId is > 0 && sourceFileId is > 0 &&
            !string.IsNullOrWhiteSpace(sourcePlatform))
        {
            var existingDirectories = FindInstalledModDirectoriesBySource(
                modsPath,
                sourcePlatform,
                sourceProjectId.Value,
                sourceFileId.Value);
            if (existingDirectories.Count > 0)
            {
                return true;
            }
        }

        var fileName = InstanceRuntimePathResolver.SanitizeFileNameComponent(
            string.IsNullOrWhiteSpace(suggestedFileName) ? Path.GetFileName(uri.LocalPath) : suggestedFileName,
            $"{safeModName}.zip");
        var zipPath = Path.Combine(modsPath, "_downloads", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);

        await _httpDownloadService.DownloadAsync(
            downloadUrl,
            zipPath,
            null,
            ct,
            cacheValidator: IsValidModArchiveFile);
        try
        {
            if (!IsValidModArchive(zipPath))
            {
                return false;
            }

            var effectiveNexusModId = nexusModId ??
                (IsNexusPlatform(sourcePlatform) ? sourceProjectId : null);
            var effectiveNexusFileId = nexusFileId ??
                (IsNexusPlatform(sourcePlatform) ? sourceFileId : null);
            if (effectiveNexusModId is > 0 && effectiveNexusFileId is > 0)
            {
                NexusDownloadCache.Save(
                    effectiveNexusModId.Value,
                    effectiveNexusFileId.Value,
                    zipPath,
                    IsValidModArchiveFile);
            }

            var installed = InstallDownloadedModArchive(zipPath, modsPath, safeModName, out var installedNames);
            var effectiveSourceProjectId = sourceProjectId ?? effectiveNexusModId;
            var effectiveSourceFileId = sourceFileId ?? effectiveNexusFileId;
            var effectiveSourcePlatform = !string.IsNullOrWhiteSpace(sourcePlatform)
                ? sourcePlatform
                : effectiveNexusModId is > 0 && effectiveNexusFileId is > 0
                    ? "NexusMods"
                    : null;
            if (installed)
            {
                if (IsCurseforgePlatform(effectiveSourcePlatform) &&
                    effectiveSourceProjectId is > 0 && effectiveSourceFileId is > 0)
                {
                    CurseforgeDownloadCache.Save(
                        effectiveSourceProjectId.Value, effectiveSourceFileId.Value,
                        zipPath, IsValidModArchiveFile);
                }

                // 必须使用解压器实际返回的目录名。Nexus/CurseForge 压缩包常带
                // 外层发行包目录，不能假设它一定等于任务名，否则 FileID 会写到
                // 不存在的路径，导出时仍然看不到来源文件信息。
                WriteModpackSourceCredentialsWithRepository(
                    modsPath,
                    installedNames,
                    string.IsNullOrWhiteSpace(effectiveSourcePlatform)
                        ? "Direct"
                        : effectiveSourcePlatform,
                    effectiveSourceProjectId ?? 0,
                    effectiveSourceFileId ?? 0,
                    downloadUrl,
                    suggestedFileName,
                    sourceRepository);
            }

            return installed;
        }
        finally
        {
            try { File.Delete(zipPath); } catch { }
        }
    }

    internal static bool TryInstallCachedCurseforgeMod(
        long projectId, long fileId, string modsPath, string modName)
    {
        return TryInstallCachedCurseforgeMod(
            projectId,
            fileId,
            modsPath,
            modName,
            out _);
    }

    internal static bool TryInstallCachedCurseforgeMod(
        long projectId,
        long fileId,
        string modsPath,
        string modName,
        out IReadOnlyList<string> installedNames)
    {
        installedNames = [];
        // 当前实例的未完成/待整理归档优先于共享缓存，保证恢复任务继续使用
        // 自己已经下载的内容；全局缓存仅作为回退。
        var localCachedPath = Path.Combine(
            modsPath,
            "_downloads",
            $"cf-{projectId}-{fileId}.zip");
        var cachedPath = IsValidModArchiveFile(localCachedPath)
            ? localCachedPath
            : null;
        if (cachedPath == null &&
            !CurseforgeDownloadCache.TryGet(
                projectId,
                fileId,
                out cachedPath,
                IsValidModArchiveFile))
        {
            return false;
        }

        if (!InstallDownloadedModArchive(cachedPath, modsPath, modName, out var names))
        {
            return false;
        }

        installedNames = names;
        WriteModpackSourceCredentials(modsPath, names, "Curseforge", projectId, fileId);
        return true;
    }

    private static string? GetJsonString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetJsonPropertyIgnoreCase(element, propertyName, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }

                // 旧导出可能保留空的标准字段，并把实际值写入别名字段。
                // 继续查找，避免空 projectId/fileId 遮蔽 project/file。
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number)
            {
                return value.ToString();
            }
        }

        return null;
    }

    /// <summary>
    /// 读取 SVL 来源清单并兼容不同导出器的根布局。
    ///
    /// 当前导出使用顶层数组；早期/第三方导出还会使用
    /// { "mods": [...] }、{ "sources": [...] } 或把单个来源对象直接写在根节点。
    /// 返回值必须 Clone，调用方会在 JsonDocument 释放后继续使用这些条目。
    /// </summary>
    private static List<JsonElement> ParseSourceEntries(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object)
                .Select(item => item.Clone())
                .ToList();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        foreach (var wrapperName in new[] { "mods", "sources", "entries", "items", "modSources" })
        {
            if (!TryGetJsonPropertyIgnoreCase(root, wrapperName, out var wrapped))
            {
                continue;
            }

            if (wrapped.ValueKind == JsonValueKind.Array)
            {
                return wrapped.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.Object)
                    .Select(item => item.Clone())
                    .ToList();
            }

            if (wrapped.ValueKind == JsonValueKind.Object &&
                LooksLikeSourceEntry(wrapped))
            {
                return [wrapped.Clone()];
            }

            // 部分第三方/早期导出把来源写成以 Mod 名称为 key 的对象：
            // { "Content Patcher": { "platform": "Curseforge", ... } }。
            // 它不是单个来源对象，不能直接交给 TryGetModSourceDescriptor；
            // 这里把 key 补成 name，保持后续目录匹配和失败提示可读。
            var wrappedMapEntries = ParseSourceEntryMap(wrapped);
            if (wrappedMapEntries.Count > 0)
            {
                return wrappedMapEntries;
            }
        }

        if (LooksLikeSourceEntry(root))
        {
            return [root.Clone()];
        }

        return ParseSourceEntryMap(root);
    }

    private static List<JsonElement> ParseSourceEntryMap(JsonElement map)
    {
        if (map.ValueKind != JsonValueKind.Object || LooksLikeSourceEntry(map))
        {
            return [];
        }

        var entries = new List<JsonElement>();
        foreach (var property in map.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Object &&
                LooksLikeSourceEntry(property.Value))
            {
                entries.Add(CloneSourceEntryWithFallbackName(property.Value, property.Name));
            }
            else if (property.Value.ValueKind == JsonValueKind.String &&
                     !string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                // 兼容 { "Mod Name": "https://..." } 的最简来源清单。
                entries.Add(CreateSourceEntryFromString(property.Name, property.Value.GetString()!));
            }
        }

        return entries;
    }

    private static JsonElement CloneSourceEntryWithFallbackName(
        JsonElement sourceEntry,
        string fallbackName)
    {
        if (TryGetJsonPropertyIgnoreCase(sourceEntry, "name", out var name) &&
            name.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(name.GetString()))
        {
            return sourceEntry.Clone();
        }

        var node = JsonNode.Parse(sourceEntry.GetRawText()) as JsonObject ?? new JsonObject();
        node["name"] = fallbackName;
        return ParseClonedJsonElement(node.ToJsonString());
    }

    private static JsonElement CreateSourceEntryFromString(string name, string source)
    {
        var node = new JsonObject
        {
            ["name"] = name,
            ["source"] = source
        };
        return ParseClonedJsonElement(node.ToJsonString());
    }

    private static JsonElement ParseClonedJsonElement(string json)
    {
        using var document = JsonDocument.Parse(json, ManifestJsonOptions);
        return document.RootElement.Clone();
    }

    private static bool LooksLikeSourceEntry(JsonElement element)
    {
        // 映射布局和单条来源使用同一套别名规则，否则仅含 site/project/file
        // 的条目会在进入实际来源解析之前被静默过滤。
        return TryGetModSourceDescriptor(element, out _);
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
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static bool TryParsePositiveLong(string? value, out long result)
    {
        return long.TryParse(value, out result) && result > 0;
    }

    private static bool TryGetNexusFileIdFromCachePath(
        string cachePath,
        long modId,
        out long fileId)
    {
        return NexusDownloadCache.TryGetFileIdFromCachePath(cachePath, modId, out fileId);
    }

    /// <summary>
    /// 下载单个 Curseforge mod 文件。
    /// 下载后解压到 mod 目录，校验解压结果包含 manifest.json（SMAPI mod 标准）或至少有内容。
    /// 解压失败或结果为空时返回 false，避免"下载成功但 Mod 未安装"的误报。
    /// </summary>
    private async Task<(bool IsSuccess, string Message)> DownloadCurseforgeModAsync(
        long projectId,
        long fileId,
        string modsPath,
        CancellationToken ct)
    {
        // CurseForge 整合包可能被重复导入。安装成功后会在实际 Mod 目录写入
        // svl-source.json；优先按来源凭证复用，而不是再次请求 CDN。这里不能只
        // 看 cf-{projectId}-{fileId} 目录，因为归档解压后的目录通常是 manifest.Name。
        var existingDirectories = FindInstalledModDirectories(
            modsPath,
            directoryName: null,
            modName: null,
            uniqueId: null,
            sourcePlatform: "Curseforge",
            sourceProjectId: projectId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            sourceFileId: fileId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (existingDirectories.Count > 0)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[Modpack] 复用已安装的 CurseForge Mod project={projectId}, file={fileId}");
            // 兼容旧版本已经安装的条目：即使无需重新解压，也要把旧的
            // sourceKind=modpack 升级为逐 Mod 的 modpack-entry，并补齐压缩包
            // 内部的 parentMod/childMods 关系，否则管理页仍会沿用旧的来源
            // 状态，嵌套子 Mod 也无法在导出/恢复时还原父级。
            WriteCurseforgeModpackSourceCredentials(
                modsPath,
                existingDirectories.Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!)
                    .ToList(),
                "Curseforge",
                projectId,
                fileId);
            return (true, "已复用已安装 Mod");
        }

        var fileName = $"cf-{projectId}-{fileId}.zip";
        var zipPath = Path.Combine(modsPath, "_downloads", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);

        // 上一次下载可能已经完成，但进程在解压/写入 Mods 前退出；当前实例的
        // 归档优先于共享缓存，避免恢复过程中被其它来源/任务的同名缓存干扰。
        if (IsValidModArchiveFile(zipPath) &&
            InstallDownloadedModArchive(
                zipPath,
                modsPath,
                fileName,
                out var cachedInstalledNames))
        {
            WriteCurseforgeModpackSourceCredentials(
                modsPath,
                cachedInstalledNames,
                "Curseforge",
                projectId,
                fileId);
            return (true, "已复用缓存并安装 Mod");
        }

        // CDN 地址可能已过期，但 ProjectID/FileID 是稳定身份。当前实例缓存
        // 未命中时，再查全局缓存，让下载页、整合包和不同 Base 之间共享归档。
        if (CurseforgeDownloadCache.TryGet(
                projectId,
                fileId,
                out var globalCachedPath,
                IsValidModArchiveFile))
        {
            if (InstallDownloadedModArchive(
                    globalCachedPath,
                    modsPath,
                    fileName,
                    out var globalCachedInstalledNames))
            {
                WriteCurseforgeModpackSourceCredentials(
                    modsPath,
                    globalCachedInstalledNames,
                    "Curseforge",
                    projectId,
                    fileId);
                return (true, "已复用全局缓存并安装 Mod");
            }
        }

        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var downloadUrl = await _remoteCatalogService.ResolveCurseforgeFileDownloadUrlAsync(projectId, fileId, "", ct);
                if (!IsLikelyDirectDownloadUrl(downloadUrl))
                {
                    throw new InvalidOperationException("未解析到 CurseForge 可下载压缩包地址");
                }

                TryDeleteDownloadArtifacts(zipPath);
                await _httpDownloadService.DownloadAsync(
                    downloadUrl,
                    zipPath,
                    null,
                    ct,
                    cacheValidator: IsValidModArchiveFile);

                // 校验并整理目录：Curseforge 下载包同样可能带发行包名/Mod 名两层目录，
                // manifest 必须最终位于 Mods/<Mod>/manifest.json。
                var installed = InstallDownloadedModArchive(
                    zipPath,
                    modsPath,
                    $"cf-{projectId}-{fileId}",
                    out var installedNames);
                if (installed)
                {
                    CurseforgeDownloadCache.Save(
                        projectId,
                        fileId,
                        zipPath,
                        IsValidModArchiveFile);
                    WriteCurseforgeModpackSourceCredentials(
                        modsPath,
                        installedNames,
                        "Curseforge",
                        projectId,
                        fileId);
                    return (true, "下载并安装成功");
                }

                throw new InvalidDataException("下载包中未找到可安装的 manifest.json");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                TryDeleteDownloadArtifacts(zipPath);
                if (attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct);
                }
            }
        }

        System.Diagnostics.Debug.WriteLine(
            $"[Modpack] CurseForge Mod 下载失败 project={projectId}, file={fileId}: {lastError?.Message}");
        return (
            false,
            lastError?.Message ?? "未知下载错误");
    }

    private static void TryDeleteDownloadArtifacts(string zipPath)
    {
        foreach (var path in new[] { zipPath, zipPath + ".part", zipPath + ".part.json" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // 下次下载仍会重新校验，清理失败不应遮蔽根因。
            }
        }
    }

    private static void WriteModpackSourceCredentials(
        string modsPath,
        IReadOnlyList<string> installedNames,
        string? platform,
        long projectId,
        long fileId,
        string? downloadUrl = null,
        string? fileName = null)
    {
        WriteModpackSourceCredentialsWithRepository(
            modsPath,
            installedNames,
            platform,
            projectId,
            fileId,
            downloadUrl,
            fileName,
            repository: null);
    }

    private static void WriteModpackSourceCredentialsWithRepository(
        string modsPath,
        IReadOnlyList<string> installedNames,
        string? platform,
        long projectId,
        long fileId,
        string? downloadUrl = null,
        string? fileName = null,
        string? repository = null)
    {
        WriteModpackSourceCredentialsCore(
            modsPath,
            installedNames,
            platform,
            projectId,
            fileId,
            downloadUrl,
            fileName,
            repository,
            isModpackSource: false);
    }

    private static void WriteCurseforgeModpackSourceCredentials(
        string modsPath,
        IReadOnlyList<string> installedNames,
        string platform,
        long projectId,
        long fileId)
    {
        WriteModpackSourceCredentialsCore(
            modsPath,
            installedNames,
            platform,
            projectId,
            fileId,
            downloadUrl: null,
            fileName: null,
            isModpackSource: true);
    }

    private static void WriteModpackSourceCredentialsCore(
        string modsPath,
        IReadOnlyList<string> installedNames,
        string? platform,
        long projectId,
        long fileId,
        string? downloadUrl = null,
        string? fileName = null,
        string? repository = null,
        bool isModpackSource = false)
    {
        if (string.IsNullOrWhiteSpace(platform) && string.IsNullOrWhiteSpace(downloadUrl) ||
            installedNames == null ||
            installedNames.Count == 0)
        {
            return;
        }

        var effectiveFileId = fileId;
        if (IsCurseforgePlatform(platform) &&
            TryParseCurseforgeFileIdFromCdnUrl(downloadUrl, out var cdnFileId))
        {
            effectiveFileId = cdnFileId;
        }

        // 一个下载归档可能同时包含“父 Mod + 多个 ContentPack”。解压器会
        // 将这些 manifest 分别整理到 Mods 一级目录，单纯按 installedName
        // 逐个写来源会把每个子 Mod 都误标成独立来源。先尝试从 manifest
        // 的 ContentPackFor 关系恢复归档内的父子树；失败时再沿用旧的
        // 单目录/真正嵌套目录逻辑。
        var installedDirectories = installedNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => Path.Combine(modsPath, name))
            .Where(IsInstalledModDirectory)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (TryBuildCompositeInstalledModTree(
                installedDirectories,
                out var compositeParentDirectory,
                out var compositeChildDirectories))
        {
            var compositeSource = BuildInstalledSourceCredentialNode(
                platform,
                projectId,
                effectiveFileId,
                downloadUrl,
                fileName,
                repository,
                isModpackSource ? "modpack-entry" : null);
            var compositeAllDirectories = new[] { compositeParentDirectory }
                .Concat(compositeChildDirectories)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var compositeChildrenByParent = compositeAllDirectories.ToDictionary(
                directory => directory,
                _ => new List<string>(),
                StringComparer.OrdinalIgnoreCase);
            compositeChildrenByParent[compositeParentDirectory].AddRange(compositeChildDirectories);
            WriteNestedSourceCredentialTree(
                modsPath,
                compositeParentDirectory,
                compositeSource,
                compositeChildrenByParent,
                compositeAllDirectories);
            return;
        }

        foreach (var installedName in installedNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(installedName))
            {
                continue;
            }

            try
            {
                var modDir = Path.Combine(modsPath, installedName);
                if (!Directory.Exists(modDir))
                {
                    continue;
                }

                // CurseForge manifest.files 是“整合包内每个文件”的映射，
                // 这里的 project/file ID 应写到本次实际安装出来的每个 Mod，
                // 而不是把整个 Modpack 当成一个无来源的项目。
                var source = BuildInstalledSourceCredentialNode(
                    platform,
                    projectId,
                    effectiveFileId,
                    downloadUrl,
                    fileName,
                    repository,
                    isModpackSource ? "modpack-entry" : null);

                // 一个下载文件也可能是“父 Mod + 多个嵌套子 Mod”。安装器会
                // 保留这棵目录树，因此来源凭证也必须按同样的树写入：父目录
                // 持有真实来源，子目录只记录 parentMod，避免把父文件误当成
                // 子 Mod 的独立更新源。
                var nestedDirectories = FindNestedInstalledModDirectories(modDir);
                var allDirectories = new[] { modDir }
                    .Concat(nestedDirectories)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var childDirectoriesByParent = allDirectories.ToDictionary(
                    directory => directory,
                    _ => new List<string>(),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var childDirectory in nestedDirectories)
                {
                    var parentDirectory = allDirectories
                        .Where(candidate =>
                            !string.Equals(candidate, childDirectory, StringComparison.OrdinalIgnoreCase) &&
                            IsPathUnderDirectory(childDirectory, candidate))
                        .OrderByDescending(candidate => candidate.Length)
                        .FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(parentDirectory) &&
                        childDirectoriesByParent.TryGetValue(parentDirectory, out var children))
                    {
                        children.Add(childDirectory);
                    }
                }

                WriteNestedSourceCredentialTree(
                    modsPath,
                    modDir,
                    source,
                    childDirectoriesByParent,
                    allDirectories);
            }
            catch
            {
                // 来源凭证是增强信息，写入失败不应让 Modpack 安装失败。
            }
        }
    }

    private static JsonObject BuildInstalledSourceCredentialNode(
        string? platform,
        long projectId,
        long fileId,
        string? downloadUrl,
        string? fileName,
        string? repository,
        string? sourceKind)
    {
        var source = new JsonObject();
        if (!string.IsNullOrWhiteSpace(platform))
        {
            source["platform"] = NormalizeSourcePlatform(platform);
        }

        if (projectId > 0)
        {
            source["projectId"] = projectId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (fileId > 0)
        {
            source["fileId"] = fileId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(downloadUrl))
        {
            source["downloadUrl"] = downloadUrl.Trim();
        }

        if (!string.IsNullOrWhiteSpace(fileName))
        {
            source["fileName"] = fileName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(repository))
        {
            source["repository"] = repository.Trim();
        }

        if (!string.IsNullOrWhiteSpace(sourceKind))
        {
            source["sourceKind"] = sourceKind;
        }

        return source;
    }

    private static List<string> FindNestedInstalledModDirectories(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
        {
            return [];
        }

        try
        {
            return EnumerateFilesSafe(rootDirectory, "manifest.json")
                .Select(path => Path.GetDirectoryName(path))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.GetFullPath(path!))
                .Where(path => !string.Equals(path, rootDirectory, StringComparison.OrdinalIgnoreCase))
                .Where(path => IsInstalledModDirectory(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path.Length)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// 从同一归档实际安装出的多个一级 Mod 中识别唯一的父 Mod。
    ///
    /// 许多 CurseForge/Nexus 发布包的目录结构是：一个 DLL Mod 加上多个
    /// ContentPack，解压整理后它们是兄弟目录，不能依赖路径嵌套判断。只有
    /// “恰好一个无 ContentPackFor 的根 + 其余全部带 ContentPackFor”时才
    /// 自动建立父子关系，避免把一个同时打包的多个独立 DLL Mod 错合并。
    /// </summary>
    private static bool TryBuildCompositeInstalledModTree(
        IReadOnlyCollection<string> installedDirectories,
        out string parentDirectory,
        out List<string> childDirectories)
    {
        parentDirectory = string.Empty;
        childDirectories = [];
        if (installedDirectories == null || installedDirectories.Count < 2)
        {
            return false;
        }

        var roots = new List<string>();
        var children = new List<string>();
        foreach (var directory in installedDirectories)
        {
            if (!TryReadContentPackForFlag(directory, out var isContentPack))
            {
                return false;
            }

            if (isContentPack)
            {
                children.Add(directory);
            }
            else
            {
                roots.Add(directory);
            }
        }

        if (roots.Count != 1 || children.Count == 0 ||
            roots.Count + children.Count != installedDirectories.Count)
        {
            return false;
        }

        parentDirectory = roots[0];
        childDirectories = children;
        return true;
    }

    private static bool TryReadContentPackForFlag(
        string modDirectory,
        out bool isContentPack)
    {
        isContentPack = false;
        try
        {
            var manifestPath = FindManifestPath(modDirectory);
            if (string.IsNullOrWhiteSpace(manifestPath))
            {
                return false;
            }

            using var document = JsonDocument.Parse(
                ReadTextFileWithBom(manifestPath),
                ManifestJsonOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !TryGetJsonPropertyIgnoreCase(
                    document.RootElement,
                    "ContentPackFor",
                    out var contentPackFor))
            {
                return true;
            }

            isContentPack = contentPackFor.ValueKind switch
            {
                JsonValueKind.Object => contentPackFor.EnumerateObject().Any(),
                JsonValueKind.String => !string.IsNullOrWhiteSpace(contentPackFor.GetString()),
                _ => false
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteNestedSourceCredentialTree(
        string modsPath,
        string rootDirectory,
        JsonObject rootSource,
        IReadOnlyDictionary<string, List<string>> childDirectoriesByParent,
        IReadOnlyCollection<string> allDirectories)
    {
        var sourceByDirectory = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase)
        {
            [rootDirectory] = rootSource
        };

        foreach (var directory in allDirectories.Where(path =>
                     !string.Equals(path, rootDirectory, StringComparison.OrdinalIgnoreCase)))
        {
            var parentDirectory = childDirectoriesByParent
                .Where(pair => pair.Value.Any(child =>
                    string.Equals(child, directory, StringComparison.OrdinalIgnoreCase)))
                .Select(pair => pair.Key)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(parentDirectory))
            {
                continue;
            }

            var parentReference = CreateParentModReference(modsPath, parentDirectory);
            var childSource = ReadSourceCredentialNode(directory) ?? new JsonObject();

            // 当前归档中的嵌套目录都是父文件的一部分，不能用旧的
            // project/file ID 伪装成可单独更新的子 Mod。无论来源是
            // Modpack、Collection 还是普通 Mod 下载，都统一记录父级来源；
            // 这样更新父包或撤回更新时不会因子目录名称变化而丢失关系。
            RemoveIndependentSourceFields(childSource);
            childSource["sourceKind"] = "parent-inherited";

            childSource["isParentMod"] = HasChildren(childDirectoriesByParent, directory);
            childSource["parentMod"] = parentReference;
            childSource["childMods"] = BuildChildReferences(
                modsPath,
                childDirectoriesByParent.TryGetValue(directory, out var children) ? children : []);
            sourceByDirectory[directory] = childSource;
        }

        foreach (var pair in childDirectoriesByParent)
        {
            if (!sourceByDirectory.TryGetValue(pair.Key, out var source))
            {
                continue;
            }

            var children = pair.Value;
            source["isParentMod"] = children.Count > 0;
            source["childMods"] = BuildChildReferences(modsPath, children);
        }

        foreach (var directory in allDirectories)
        {
            if (!sourceByDirectory.TryGetValue(directory, out var source))
            {
                continue;
            }

            try
            {
                AtomicFileWriter.WriteUtf8(
                    Path.Combine(directory, "svl-source.json"),
                    source.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // 来源凭证是增强信息，写入失败不应让 Modpack 安装失败。
            }
        }

        // 更新包可能带多个来源文件。清理整棵目录，避免旧子 Mod 的
        // hasUpdate=true 在安装完成后再次被管理页读出。复合归档整理后
        // 子 Mod 可能是 rootDirectory 的兄弟目录，也必须逐一清理。
        foreach (var directory in allDirectories)
        {
            ClearPersistedModUpdateState(directory);
        }
    }

    private static bool HasChildren(
        IReadOnlyDictionary<string, List<string>> childDirectoriesByParent,
        string directory)
    {
        return childDirectoriesByParent.TryGetValue(directory, out var children) && children.Count > 0;
    }

    private static JsonArray BuildChildReferences(
        string modsPath,
        IReadOnlyCollection<string> childDirectories)
    {
        var references = new JsonArray();
        foreach (var childDirectory in childDirectories)
        {
            TryReadModManifestIdentity(childDirectory, out var name, out var uniqueId);
            var relativePath = Path.GetRelativePath(modsPath, childDirectory)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            references.Add(new JsonObject
            {
                ["id"] = string.IsNullOrWhiteSpace(uniqueId) ? relativePath : uniqueId,
                ["name"] = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(childDirectory) : name,
                ["relativePath"] = relativePath,
                ["uniqueId"] = uniqueId
            });
        }

        return references;
    }

    private static JsonObject CreateParentModReference(string modsPath, string parentDirectory)
    {
        TryReadModManifestIdentity(parentDirectory, out var name, out var uniqueId);
        var relativePath = Path.GetRelativePath(modsPath, parentDirectory)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        return new JsonObject
        {
            ["id"] = string.IsNullOrWhiteSpace(uniqueId) ? relativePath : uniqueId,
            ["name"] = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(parentDirectory) : name,
            ["relativePath"] = relativePath
        };
    }

    private static JsonObject? ReadSourceCredentialNode(string modDirectory)
    {
        try
        {
            var sourcePath = Path.Combine(modDirectory, "svl-source.json");
            return File.Exists(sourcePath)
                ? JsonNode.Parse(ReadTextFileWithBom(sourcePath)) as JsonObject
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool HasIndependentSourceCredential(JsonObject source)
    {
        var platform = GetJsonNodeString(source, "platform");
        var projectId = GetJsonNodeString(source, "projectId", "project_id", "modId");
        var repository = GetJsonNodeString(source, "repository", "repo", "githubRepository");
        var downloadUrl = GetJsonNodeString(source, "downloadUrl", "download_url", "url");
        return !string.IsNullOrWhiteSpace(downloadUrl) ||
               (IsGitHubPlatform(platform) &&
                RemoteCatalogService.TryNormalizeGitHubRepository(repository, out _)) ||
               ((IsNexusPlatform(platform) || IsCurseforgePlatform(platform)) &&
                TryParsePositiveLong(projectId, out _));
    }

    private static string GetJsonNodeString(JsonObject source, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!source.TryGetPropertyValue(propertyName, out var value) || value is not JsonValue jsonValue)
            {
                continue;
            }

            if (jsonValue.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }

            if (jsonValue.TryGetValue<long>(out var number) && number > 0)
            {
                return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return string.Empty;
    }

    private static void RemoveIndependentSourceFields(JsonObject source)
    {
        foreach (var propertyName in new[]
                 {
                     "platform", "projectId", "project_id", "modId", "mod_id", "fileId", "file_id",
                     "downloadUrl", "download_url", "url", "fileName", "file_name", "hasUpdate",
                     "latestVersion", "updateStatus", "updateUrl", "updateFileId"
                 })
        {
            source.Remove(propertyName);
        }
    }

    /// <summary>
    /// 将单个外部下载条目的来源凭证写入实际安装出来的 Mod 目录。
    /// Collection 的清单没有 SVL sources.json 那样的条目映射，因此由
    /// CollectionInstallService 在每个归档成功整理后调用本方法。
    /// </summary>
    internal static void WriteSourceCredentialForInstalledMod(
        string modsPath,
        IReadOnlyList<string> installedNames,
        string? platform,
        long? projectId,
        long? fileId,
        string? downloadUrl = null,
        string? fileName = null)
    {
        WriteSourceCredentialForInstalledModWithRepository(
            modsPath,
            installedNames,
            platform,
            projectId,
            fileId,
            downloadUrl,
            fileName,
            repository: null);
    }

    internal static void WriteSourceCredentialForInstalledModWithRepository(
        string modsPath,
        IReadOnlyList<string> installedNames,
        string? platform,
        long? projectId,
        long? fileId,
        string? downloadUrl,
        string? fileName,
        string? repository)
    {
        // Collection 的清单没有 SVL sources.json 那样的条目映射，但一个
        // Collection 归档同样可能解出父 Mod 和多个 ContentPack。复用统一
        // 的来源树写入逻辑，保证 Collection 与 Modpack 的更新/回滚语义一致。
        WriteModpackSourceCredentialsCore(
            modsPath,
            installedNames,
            platform,
            projectId.GetValueOrDefault(),
            fileId.GetValueOrDefault(),
            downloadUrl,
            fileName,
            repository,
            isModpackSource: false);
    }

    /// <summary>
    /// 清理安装包目录及其子 Mod 的持久化更新快照。
    ///
    /// hasUpdate/latestVersion/updateStatus 等字段是本地检测缓存，不属于
    /// 下载包的来源凭证。安装新包后必须清掉整棵复合目录，等待下一次更新检测
    /// 根据新 manifest 的版本重新计算；否则旧包内携带的 svl-source.json 会
    /// 在重载 Mod 管理页时把“可更新”恢复出来。
    /// </summary>
    internal static void ClearPersistedModUpdateState(string modDirectory)
    {
        if (string.IsNullOrWhiteSpace(modDirectory) || !Directory.Exists(modDirectory))
        {
            return;
        }

        foreach (var sourcePath in EnumerateFilesSafe(modDirectory, "svl-source.json"))
        {
            try
            {
                using var document = JsonDocument.Parse(ReadTextFileWithBom(sourcePath));
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    values[property.Name] = property.Value.Clone();
                }

                values["hasUpdate"] = JsonSerializer.SerializeToElement(false);
                values["latestVersion"] = JsonSerializer.SerializeToElement(string.Empty);
                values["updateStatus"] = JsonSerializer.SerializeToElement("未检查");
                values["updateUrl"] = JsonSerializer.SerializeToElement(string.Empty);
                values["updateFileId"] = JsonSerializer.SerializeToElement(string.Empty);
                AtomicFileWriter.WriteUtf8(
                    sourcePath,
                    JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // 单个旧来源文件损坏或被占用时，不影响其它子 Mod 的清理。
            }
        }
    }

    /// <summary>
    /// 修复旧版本把同一归档中的父 Mod、ContentPack 分别记录成独立来源
    /// 的情况。
    ///
    /// 旧数据经常只保留 projectId，不同子条目还带着旧 fileId；因此按
    /// “平台 + 项目 ID”分组，再用 manifest 的 ContentPackFor 形态确认
    /// “恰好一个父 Mod + 一个或多个 ContentPack”。只修改来源凭证，不
    /// 移动、删除或覆盖用户 Mod 文件。
    /// </summary>
    internal static void RepairCompositeSourceCredentials(string modsPath)
    {
        if (string.IsNullOrWhiteSpace(modsPath) || !Directory.Exists(modsPath))
        {
            return;
        }

        try
        {
            var topLevelDirectories = EnumerateTopLevelDirectoriesSafe(modsPath)
                .ToList();
            var installedDirectories = topLevelDirectories
                .Where(IsInstalledModDirectory)
                .ToList();
            foreach (var topLevelDirectory in topLevelDirectories)
            {
                foreach (var nestedDirectory in FindNestedInstalledModDirectories(topLevelDirectory))
                {
                    if (!installedDirectories.Contains(nestedDirectory, StringComparer.OrdinalIgnoreCase))
                    {
                        installedDirectories.Add(nestedDirectory);
                    }
                }
            }

            var candidates = installedDirectories
                .Select(directory => new
                {
                    Directory = directory,
                    Source = ReadSourceCredentialNode(directory)
                })
                .Where(item => item.Source != null)
                .Select(item =>
                {
                    var platform = NormalizeSourcePlatform(
                        GetJsonNodeString(item.Source!, "platform", "provider", "site") ?? string.Empty);
                    var projectId = GetJsonNodeString(
                        item.Source!,
                        "projectId",
                        "project_id",
                        "modId",
                        "mod_id") ?? string.Empty;
                    return new
                    {
                        item.Directory,
                        item.Source,
                        Platform = platform,
                        ProjectId = projectId
                    };
                })
                .Where(item =>
                    !string.IsNullOrWhiteSpace(item.Platform) &&
                    TryParsePositiveLong(item.ProjectId, out _))
                .GroupBy(
                    item => $"{item.Platform}:{item.ProjectId}",
                    StringComparer.OrdinalIgnoreCase);

            foreach (var group in candidates)
            {
                var sourceDirectories = group
                    .Select(item => item.Directory)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var parentCandidates = sourceDirectories
                    .Where(directory =>
                        TryReadContentPackForFlag(directory, out var isContentPack) &&
                        !isContentPack)
                    .ToList();
                if (parentCandidates.Count != 1)
                {
                    // 同一项目下存在多个独立根 Mod 时不能猜测父子关系。
                    // 只有唯一的非 ContentPack 根目录才允许自动恢复。
                    continue;
                }

                var parentDirectory = parentCandidates[0];
                var directories = sourceDirectories
                    .Where(directory =>
                        string.Equals(directory, parentDirectory, StringComparison.OrdinalIgnoreCase) ||
                        IsMatchingSourceCredential(
                            directory,
                            group.Key.Split(':', 2)[0],
                            group.Key.Split(':', 2).Length == 2 ? group.Key.Split(':', 2)[1] : null,
                            expectedFileId: null))
                    .ToList();

                // 旧导入可能只给父 Mod 写入来源，子 ContentPack 没有
                // svl-source.json。用 manifest 的 UniqueID 命名空间补齐这些
                // 同级子目录；不使用“同平台/同项目”作为唯一条件，避免把
                // 同项目下的独立 Mod 错误合并到这棵来源树。
                foreach (var directory in installedDirectories)
                {
                    if (string.Equals(directory, parentDirectory, StringComparison.OrdinalIgnoreCase) ||
                        directories.Contains(directory, StringComparer.OrdinalIgnoreCase) ||
                        !TryReadContentPackForFlag(directory, out var isContentPack) ||
                        !isContentPack)
                    {
                        continue;
                    }

                    if (IsManifestNestedContentPackOf(parentDirectory, directory))
                    {
                        directories.Add(directory);
                    }
                }

                directories = directories
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (!TryBuildCompositeInstalledModTree(
                        directories,
                        out var resolvedParentDirectory,
                        out var childDirectories))
                {
                    continue;
                }

                var parentSource = ReadSourceCredentialNode(resolvedParentDirectory) ??
                                   group.Select(item => item.Source)
                                       .FirstOrDefault(source => source != null)?
                                       .DeepClone() as JsonObject;
                if (parentSource == null)
                {
                    continue;
                }

                var needsRepair =
                    !string.Equals(
                        GetJsonNodeString(parentSource, "sourceKind"),
                        "modpack-entry",
                        StringComparison.OrdinalIgnoreCase) ||
                    childDirectories.Any(childDirectory =>
                    {
                        var childSource = ReadSourceCredentialNode(childDirectory);
                        return childSource == null ||
                               !string.Equals(
                                   GetJsonNodeString(childSource, "sourceKind"),
                                   "parent-inherited",
                                   StringComparison.OrdinalIgnoreCase);
                    });
                if (!needsRepair)
                {
                    continue;
                }

                parentSource["sourceKind"] = "modpack-entry";
                parentSource.Remove("parentMod");
                var allDirectories = new[] { resolvedParentDirectory }
                    .Concat(childDirectories)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var childrenByParent = allDirectories.ToDictionary(
                    directory => directory,
                    _ => new List<string>(),
                    StringComparer.OrdinalIgnoreCase);
                childrenByParent[resolvedParentDirectory].AddRange(childDirectories);
                WriteNestedSourceCredentialTree(
                    modsPath,
                    resolvedParentDirectory,
                    parentSource,
                    childrenByParent,
                    allDirectories);
            }
        }
        catch
        {
            // 旧来源修复是增强性的兼容迁移；单个实例目录异常不应阻断
            // Mod 管理页加载或其它实例操作。
        }
    }

    private static bool IsManifestNestedContentPackOf(
        string parentDirectory,
        string childDirectory)
    {
        if (!TryReadModManifestIdentity(parentDirectory, out var parentName, out var parentUniqueId) ||
            !TryReadModManifestIdentity(childDirectory, out var childName, out var childUniqueId))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(parentUniqueId) &&
            !string.IsNullOrWhiteSpace(childUniqueId) &&
            childUniqueId.StartsWith(parentUniqueId + ".", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 少数旧 Mod 没有稳定 UniqueID，但会用“父名称 - 子名称”命名。
        // 只有父名称非空且确实带分隔符时才启用这个较弱的回退。
        return !string.IsNullOrWhiteSpace(parentName) &&
               !string.IsNullOrWhiteSpace(childName) &&
               childName.StartsWith(parentName + " - ", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSourcePlatform(string platform)
    {
        return platform.Trim() switch
        {
            var value when value.Equals("nexus", StringComparison.OrdinalIgnoreCase) => "NexusMods",
            var value when value.Equals("nexusmods", StringComparison.OrdinalIgnoreCase) => "NexusMods",
            var value when value.Equals("curseforge", StringComparison.OrdinalIgnoreCase) => "Curseforge",
            var value when value.Equals("curse", StringComparison.OrdinalIgnoreCase) => "Curseforge",
            var value when value.Equals("github", StringComparison.OrdinalIgnoreCase) => "GitHub",
            var value => value
        };
    }

    /// <summary>从 sources.json 的 source 字段写 svl-source.json 到各 mod 目录。</summary>
    private static void WriteSourceCredentials(List<JsonElement> sourcesList, string modsPath)
    {
        // 导出清单可能同时包含父条目和“逐 Mod”子条目。先处理带有
        // childMods/parentMod 关系的父条目，后续才能保护已经写好的继承凭证。
        foreach (var source in sourcesList
                     .OrderByDescending(HasExplicitCompositeSourceRelation))
        {
            try
            {
                var modName = GetJsonString(
                    source,
                    "name",
                    "modName",
                    "id",
                    "uniqueId",
                    "directoryName",
                    "directory");
                if (string.IsNullOrWhiteSpace(modName)) continue;
                var directoryName = GetJsonString(source, "directoryName");

                if (!TryGetModSourceDescriptor(source, out var descriptor))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(descriptor.Platform) &&
                    string.IsNullOrWhiteSpace(descriptor.DownloadUrl) &&
                    string.IsNullOrWhiteSpace(descriptor.ProjectId) &&
                    string.IsNullOrWhiteSpace(descriptor.FileId) &&
                    string.IsNullOrWhiteSpace(descriptor.Repository))
                {
                    continue;
                }

                var sourceJson = BuildSourceCredentialJson(source, descriptor);
                var incomingFileId = descriptor.FileId;
                var sourceUniqueId = GetJsonString(source, "uniqueId", "uniqueID", "unique_id") ??
                                     GetJsonString(source, "unique_id");

                // 下载包的实际目录通常由 manifest.Name 决定（例如 Content Patcher），
                // 不一定等于来源条目里的 cf-项目ID-文件ID。先尝试显式目录名/来源名，
                // 再按 manifest 的 Name 与 UniqueID 匹配，避免凭证被写进不存在的目录。
                var candidateDirectories = FindInstalledModDirectories(
                    modsPath,
                    directoryName,
                    modName,
                    sourceUniqueId,
                    descriptor.Platform,
                    descriptor.ProjectId,
                    descriptor.FileId);
                foreach (var modDir in candidateDirectories)
                {
                    var sourceFilePath = Path.Combine(modDir, "svl-source.json");

                    // 父归档已经为该目录写入 parent-inherited 后，清单中的
                    // 子 Mod 独立条目不能再次覆盖它，否则下一次更新会把
                    // 子 Mod 当成独立来源。显式父条目仍允许写入自己的根目录。
                    if (!HasExplicitCompositeSourceRelation(source) &&
                        HasPersistedParentSourceRelation(modDir))
                    {
                        continue;
                    }

                    // 浏览器回退可能已经写入了真实 fileId。不要让导入包中
                    // fileId 为空的旧 source 再次覆盖它，否则下一次导出仍然
                    // 会丢失可直接复用的 Nexus 文件定位信息。
                    if (!TryParsePositiveLong(incomingFileId, out _) && File.Exists(sourceFilePath))
                    {
                        try
                        {
                            using var existingDocument = JsonDocument.Parse(
                                ReadTextFileWithBom(sourceFilePath),
                                ManifestJsonOptions);
                            var existingFileId = GetJsonString(existingDocument.RootElement, "fileId", "file_id");
                            if (TryParsePositiveLong(existingFileId, out _))
                            {
                                continue;
                            }
                        }
                        catch
                        {
                            // 现有凭证损坏时，继续用清单中的合法 source 覆盖。
                        }
                    }

                    AtomicFileWriter.WriteUtf8(sourceFilePath, sourceJson);
                    WriteInheritedChildSourceCredentials(modsPath, modDir, source);
                }
            }
            catch { }
        }
    }

    private static bool HasExplicitCompositeSourceRelation(JsonElement source)
    {
        if (TryGetJsonPropertyIgnoreCase(source, "childMods", out var childMods) &&
            childMods.ValueKind == JsonValueKind.Array &&
            childMods.GetArrayLength() > 0)
        {
            return true;
        }

        return TryGetJsonPropertyIgnoreCase(source, "parentMod", out var parentMod) &&
               parentMod.ValueKind == JsonValueKind.Object;
    }

    private static bool HasPersistedParentSourceRelation(string modDirectory)
    {
        var source = ReadSourceCredentialNode(modDirectory);
        if (source == null)
        {
            return false;
        }

        if (string.Equals(
                GetJsonNodeString(source, "sourceKind"),
                "parent-inherited",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                GetJsonNodeString(source, "sourceKind"),
                "modpack-parent-inherited",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return source.TryGetPropertyValue("parentMod", out var parentMod) &&
               parentMod is JsonObject;
    }

    private static void WriteInheritedChildSourceCredentials(
        string modsPath,
        string parentDirectory,
        JsonElement sourceEntry)
    {
        if (!TryGetJsonPropertyIgnoreCase(sourceEntry, "childMods", out var childMods) ||
            childMods.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var parentReference = CreateParentModReference(modsPath, parentDirectory);
        foreach (var childReference in childMods.EnumerateArray())
        {
            var childDirectory = ResolveInheritedChildDirectory(
                modsPath,
                parentDirectory,
                childReference);
            if (string.IsNullOrWhiteSpace(childDirectory))
            {
                continue;
            }

            var childSource = ReadSourceCredentialNode(childDirectory) ?? new JsonObject();
            // childMods 是父文件内部目录映射的权威关系。即使旧版本曾经给
            // 子目录写过独立 project/file，也必须清掉，避免它在下一次更新
            // 检查时被误认为可独立下载的来源；真正可撤回的信息由 parentMod
            // 和父级来源共同提供。
            RemoveIndependentSourceFields(childSource);
            childSource["sourceKind"] = "parent-inherited";

            childSource["parentMod"] = parentReference.DeepClone();
            childSource["isParentMod"] = false;
            childSource["childMods"] = new JsonArray();
            try
            {
                AtomicFileWriter.WriteUtf8(
                    Path.Combine(childDirectory, "svl-source.json"),
                    childSource.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // 单个嵌套子 Mod 的凭证写入失败，不应让其它来源条目失败。
            }
        }
    }

    private static string? ResolveInheritedChildDirectory(
        string modsPath,
        string parentDirectory,
        JsonElement childReference)
    {
        var relativePath = GetJsonString(childReference, "relativePath", "path");
        if (!string.IsNullOrWhiteSpace(relativePath))
        {
            var candidate = ResolveModRelativePath(modsPath, relativePath);
            if (!string.IsNullOrWhiteSpace(candidate) &&
                IsInstalledModDirectory(candidate) &&
                IsMatchingChildManifest(candidate, childReference))
            {
                return candidate;
            }
        }

        // 导入时目录名可能已经从 cf-项目ID-文件ID 或旧父目录名整理成
        // manifest.Name。用 UniqueID 优先、Name 次之扫描父目录和 Mods 根下
        // 的真实 Mod。后者用于“一个归档解出多个一级 Mod”被展平后的兄弟目录。
        var expectedUniqueId = GetJsonString(childReference, "uniqueId", "uniqueID", "unique_id");
        var expectedName = GetJsonString(childReference, "name", "modName", "displayName");
        var fallbackDirectories = FindNestedInstalledModDirectories(parentDirectory)
            .Concat(EnumerateTopLevelDirectoriesSafe(modsPath))
            .Where(directory =>
                !string.Equals(directory, parentDirectory, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in fallbackDirectories)
        {
            if (!TryReadModManifestIdentity(directory, out var actualName, out var actualUniqueId))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(expectedUniqueId) &&
                string.Equals(expectedUniqueId, actualUniqueId, StringComparison.OrdinalIgnoreCase))
            {
                return directory;
            }

            if (!string.IsNullOrWhiteSpace(expectedName) &&
                string.Equals(expectedName, actualName, StringComparison.OrdinalIgnoreCase))
            {
                return directory;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateTopLevelDirectoriesSafe(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            return Directory.GetDirectories(directory);
        }
        catch
        {
            // 兼容回退扫描是增强能力；单个目录无权限时不应阻断其它来源。
            return [];
        }
    }

    private static bool IsMatchingChildManifest(
        string directory,
        JsonElement childReference)
    {
        var expectedUniqueId = GetJsonString(childReference, "uniqueId", "uniqueID", "unique_id");
        var expectedName = GetJsonString(childReference, "name", "modName", "displayName");
        if (string.IsNullOrWhiteSpace(expectedUniqueId) && string.IsNullOrWhiteSpace(expectedName))
        {
            return true;
        }

        if (!TryReadModManifestIdentity(directory, out var actualName, out var actualUniqueId))
        {
            return false;
        }

        return (!string.IsNullOrWhiteSpace(expectedUniqueId) &&
                string.Equals(expectedUniqueId, actualUniqueId, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrWhiteSpace(expectedName) &&
                string.Equals(expectedName, actualName, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ResolveModRelativePath(string modsPath, string relativePath)
    {
        try
        {
            var normalizedRelativePath = relativePath
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(modsPath, normalizedRelativePath));
            return IsPathUnderDirectory(candidate, modsPath) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildSourceCredentialJson(
        JsonElement sourceEntry,
        ModSourceDescriptor descriptor)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (TryGetJsonPropertyIgnoreCase(sourceEntry, "source", out var sourceValue) &&
            sourceValue.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in sourceValue.EnumerateObject())
            {
                values[property.Name] = property.Value.Clone();
            }
        }

        // 复合 Mod 的层级关系位于 sources.json 条目顶层，而不是 source 凭证对象内。
        // 导入后必须把它们一并写回每个实际 Mod 目录，否则导出包二次导入时会丢失
        // 子 Mod 分组；其它未知顶层字段仍不写入，避免把整合包元数据污染到凭证。
        foreach (var relationPropertyName in new[] { "isParentMod", "parentMod", "childMods" })
        {
            if (TryGetJsonPropertyIgnoreCase(sourceEntry, relationPropertyName, out var relationValue))
            {
                values[relationPropertyName] = relationValue.Clone();
            }
        }

        // sources.json 的每一条记录都代表一个可独立定位的整合包 Mod。
        // 没有显式 sourceKind 的旧导出也要标记为 modpack-entry，避免导入后
        // 被兼容逻辑误判成“只有整合包来源、没有 Mod 来源”。嵌套子 Mod
        // 随后会由 WriteInheritedChildSourceCredentials 改为 parent-inherited。
        if (!values.Keys.Any(name => string.Equals(name, "sourceKind", StringComparison.OrdinalIgnoreCase)))
        {
            values["sourceKind"] = JsonSerializer.SerializeToElement("modpack-entry");
        }

        if (!string.IsNullOrWhiteSpace(descriptor.Platform))
        {
            values["platform"] = JsonSerializer.SerializeToElement(descriptor.Platform);
        }

        if (!string.IsNullOrWhiteSpace(descriptor.ProjectId))
        {
            values["projectId"] = JsonSerializer.SerializeToElement(descriptor.ProjectId);
        }

        if (!string.IsNullOrWhiteSpace(descriptor.FileId))
        {
            values["fileId"] = JsonSerializer.SerializeToElement(descriptor.FileId);
        }

        if (!string.IsNullOrWhiteSpace(descriptor.ModId) &&
            !values.ContainsKey("modId"))
        {
            values["modId"] = JsonSerializer.SerializeToElement(descriptor.ModId);
        }

        if (!string.IsNullOrWhiteSpace(descriptor.FileName) &&
            !values.ContainsKey("fileName"))
        {
            values["fileName"] = JsonSerializer.SerializeToElement(descriptor.FileName);
        }

        if (!string.IsNullOrWhiteSpace(descriptor.DownloadUrl) &&
            !values.ContainsKey("downloadUrl"))
        {
            values["downloadUrl"] = JsonSerializer.SerializeToElement(descriptor.DownloadUrl);
        }

        if (!string.IsNullOrWhiteSpace(descriptor.Repository) &&
            !values.ContainsKey("repository"))
        {
            values["repository"] = JsonSerializer.SerializeToElement(descriptor.Repository);
        }

        // 没有 source 包装时，descriptor 已经从条目顶层提取完毕；仅写标准凭证字段，
        // 不把 name/directoryName 等整合包元数据误写进 Mod 目录。
        return JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true });
    }

    private static IReadOnlyList<string> FindInstalledModDirectories(
        string modsPath,
        string? directoryName,
        string? modName,
        string? uniqueId,
        string? sourcePlatform = null,
        string? sourceProjectId = null,
        string? sourceFileId = null)
    {
        var result = new List<string>();
        var exactCandidates = new[] { directoryName, modName }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Path.Combine(
                modsPath,
                InstanceRuntimePathResolver.SanitizeFileNameComponent(value!, "unknown")))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in exactCandidates)
        {
            // sources.json 的显式 directoryName 是安装器刚刚整理出的目标目录；
            // 写凭证阶段允许它暂时只有文件（旧版导出与测试夹具都可能没有
            // manifest），而“按来源复用已有安装”仍由下面的扫描分支严格要求
            // IsInstalledModDirectory，避免空目录跳过真实下载。
            if (Directory.Exists(candidate))
            {
                result.Add(candidate);
            }
        }

        // 已按显式目录找到时无需扫描整个 Mods；名称匹配优先且不会误伤其它同名来源。
        if (result.Count > 0 || !Directory.Exists(modsPath))
        {
            return result;
        }

        foreach (var directory in Directory.GetDirectories(modsPath))
        {
            var leaf = Path.GetFileName(directory);
            if (string.Equals(leaf, "_downloads", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(leaf, "_staging", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsMatchingSourceCredential(directory, sourcePlatform, sourceProjectId, sourceFileId))
            {
                result.Add(directory);
                continue;
            }

            var manifestPath = FindManifestPath(directory);
            if (string.IsNullOrWhiteSpace(manifestPath))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(ReadTextFileWithBom(manifestPath), ManifestJsonOptions);
                var manifestName = GetJsonString(document.RootElement, "Name", "name");
                var manifestUniqueId = GetJsonString(document.RootElement, "UniqueID", "UniqueId", "unique_id");
                if ((!string.IsNullOrWhiteSpace(uniqueId) &&
                     string.Equals(uniqueId, manifestUniqueId, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(modName) &&
                     string.Equals(modName, manifestName, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add(directory);
                }
            }
            catch
            {
                // 单个损坏 manifest 不应影响其它 Mod 的来源凭证写回。
            }
        }

        return result;
    }

    /// <summary>
    /// 按平台项目/文件 ID 查找已经安装的 Mod。只依据来源凭证判断，供
    /// Collection 与整合包导入在下载缓存不存在时复用现有安装目录。
    /// </summary>
    internal static IReadOnlyList<string> FindInstalledModDirectoriesBySource(
        string modsPath,
        string platform,
        long projectId,
        long fileId)
    {
        if (projectId <= 0 || fileId <= 0)
        {
            return [];
        }

        return FindInstalledModDirectories(
                modsPath,
                directoryName: null,
                modName: null,
                uniqueId: null,
                sourcePlatform: platform,
                sourceProjectId: projectId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                sourceFileId: fileId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
    }

    private static bool IsMatchingSourceCredential(
        string modDirectory,
        string? expectedPlatform,
        string? expectedProjectId,
        string? expectedFileId)
    {
        if (string.IsNullOrWhiteSpace(modDirectory) ||
            string.IsNullOrWhiteSpace(expectedProjectId) ||
            // svl-source.json 只记录下载来源，不能单独证明 Mod 已安装。
            // 失败的解压/中断的重试可能留下只有来源文件的目录；若这里直接
            // 命中，后续流程会跳过下载，最终在 Mod 管理页显示“无法读取
            // manifest.json”。来源复用必须同时要求一级目录存在有效清单。
            !IsInstalledModDirectory(modDirectory))
        {
            return false;
        }

        foreach (var fileName in new[] { "svl-source.json", ".source.json" })
        {
            var path = Path.Combine(modDirectory, fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(ReadTextFileWithBom(path), ManifestJsonOptions);
                if (!TryGetModSourceDescriptor(document.RootElement, out var existing))
                {
                    continue;
                }

                var samePlatform = string.IsNullOrWhiteSpace(expectedPlatform) ||
                    string.Equals(expectedPlatform, existing.Platform, StringComparison.OrdinalIgnoreCase) ||
                    (IsNexusPlatform(expectedPlatform) && IsNexusPlatform(existing.Platform)) ||
                    (IsCurseforgePlatform(expectedPlatform) && IsCurseforgePlatform(existing.Platform));
                var sameProject = string.Equals(
                    expectedProjectId,
                    existing.ProjectId,
                    StringComparison.OrdinalIgnoreCase);
                var sameFile = string.IsNullOrWhiteSpace(expectedFileId) ||
                    string.Equals(expectedFileId, existing.FileId, StringComparison.OrdinalIgnoreCase);

                if (samePlatform && sameProject && sameFile)
                {
                    return true;
                }
            }
            catch
            {
                // 单个来源文件损坏时继续尝试另一个来源文件/manifest 匹配。
            }
        }

        return false;
    }

    /// <summary>
    /// 提取整合包图标到版本目录。
    /// 写入 .svl-instance-icon-smapi.png（SMAPI 实例图标文件名），使 InstanceIconResolver 能正确解析。
    ///
    /// packageRoot 是实际清单根目录，但“启动器 + modpack.zip”导出格式的 icon.png
    /// 可能位于外层 archiveExtractRoot。因此这里同时检查传入的自定义图标、清单根目录
    /// 和整个压缩包解压根目录，避免导入后丢失导出包图标。
    /// </summary>
    private static string? ExtractPackIcon(
        string packageRoot,
        string versionRoot,
        string? customIconPath = null,
        string? archiveExtractRoot = null)
    {
        try
        {
            var destPath = Path.Combine(versionRoot, ".svl-instance-icon-smapi.png");

            // 部分失败重试会重新执行保存阶段。此时版本目录已经存在，且图标可能
            // 是玩家在版本设置中刚刚个性化更改的；不能被整合包里的 icon.png 覆盖。
            // 只有 SMAPI 安装流程明确标记的默认图标允许被整合包自定义图标替换。
            if (File.Exists(destPath) &&
                new FileInfo(destPath).Length > 0 &&
                !InstanceIconResolver.IsGeneratedSmapiIcon(versionRoot))
            {
                return destPath;
            }

            if (!string.IsNullOrWhiteSpace(customIconPath) && File.Exists(customIconPath))
            {
                File.Copy(customIconPath, destPath, true);
                InstanceIconResolver.TryDeleteGeneratedSmapiIconMarker(versionRoot);
                return destPath;
            }

            var iconCandidates = new[]
            {
                "modpack-icon.png", "modpack-icon.jpg", "modpack-icon.jpeg", "modpack-icon.webp", "modpack-icon.gif",
                "pack-icon.png", "pack-icon.jpg", "pack-icon.jpeg", "pack-icon.webp", "pack-icon.gif",
                "icon.png", "icon.jpg", "icon.jpeg", "icon.webp", "icon.gif",
                "logo.png", "logo.jpg", "logo.jpeg", "logo.webp", "logo.gif",
                "thumbnail.png", "thumbnail.jpg", "thumbnail.jpeg", "thumbnail.webp", "thumbnail.gif",
                "cover.png", "cover.jpg", "cover.jpeg", "cover.webp", "cover.gif"
            };

            var roots = new[] { packageRoot, archiveExtractRoot }
                .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                .Select(path => Path.GetFullPath(path!))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 先查各根目录，确保外层导出包的 icon.png 不会被 mods 子目录里的同名文件抢先命中。
            foreach (var root in roots)
            {
                foreach (var name in iconCandidates)
                {
                    var iconPath = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
                        .FirstOrDefault(path => string.Equals(
                            Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(iconPath))
                    {
                        File.Copy(iconPath, destPath, true);
                        InstanceIconResolver.TryDeleteGeneratedSmapiIconMarker(versionRoot);
                        return destPath;
                    }
                }
            }

            // 再兼容图标位于外层目录/发布包目录下的情况。
            foreach (var root in roots)
            {
                foreach (var name in iconCandidates)
                {
                    var found = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                        .FirstOrDefault(path => string.Equals(
                            Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(found))
                    {
                        File.Copy(found, destPath, true);
                        InstanceIconResolver.TryDeleteGeneratedSmapiIconMarker(versionRoot);
                        return destPath;
                    }
                }
            }
        }
        catch
        {
            // 图标是可选元数据，不应使整合包安装失败。
        }

        return null;
    }

    /// <summary>保存实例记录到 InstanceRegistryStore。</summary>
    private static void SaveInstanceRecord(string instanceName, string runtimePath)
    {
        try
        {
            var store = new InstanceRegistryStore();
            // 实例名只在同一个路径下唯一；不同 Base 允许拥有同名整合包。
            // 必须在注册表内部原子 Upsert，避免并行安装的读-改-写互相覆盖。
            store.UpsertManualInstance(instanceName, runtimePath);
        }
        catch { }
    }

    /// <summary>
    /// 取消/失败时清理本次新建的版本隔离目录。
    /// 参考旧架构先处理 Content junction 避免误删源目录，再将用户可见目录移入回收站。
    /// </summary>
    private static void CleanupVersionDirectory(string gamePath, string instanceName)
    {
        try
        {
            var versionRoot = Path.Combine(gamePath, "versions", instanceName);
            if (!Directory.Exists(versionRoot)) return;

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

            if (!RecycleBinService.TryMoveToRecycleBin(versionRoot, out _))
            {
                // 回收站不可用时保留目录，绝不能为了清理失败半成品而物理删除用户内容。
                return;
            }
        }
        catch { }
    }

    private static bool IsJunctionOrSymlink(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
        }
        catch { return false; }
    }

    private static void RemoveJunction(string junctionPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "rm",
                Arguments = OperatingSystem.IsWindows() ? $"/c rmdir \"{junctionPath}\"" : $"\"{junctionPath}\"",
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
        catch { }
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, dir);
            Directory.CreateDirectory(Path.Combine(targetDir, relative));
        }
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var targetFile = Path.Combine(targetDir, relative);
            var parent = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
            File.Copy(file, targetFile, true);
        }
    }

    /// <summary>
    /// 以目录事务替换一个已安装 Mod。
    ///
    /// 直接删除目标目录再复制会在文件复制失败、杀毒软件短暂锁定文件或磁盘空间
    /// 不足时留下半个 Mod，甚至丢失原来的可用版本。先把新内容复制到 Mods/_staging，
    /// 再把旧目录移到同一暂存根，最后移动新目录；替换成功后旧目录必须进入系统
    /// 回收站，不能随着暂存目录被物理删除；任一步失败都尽量恢复旧目录。
    /// </summary>
    private static void ReplaceDirectoryFromSource(
        string sourceDir,
        string targetDir,
        string modsPath)
    {
        var transactionRoot = Path.Combine(
            modsPath,
            "_staging",
            $"replace-{Guid.NewGuid():N}");
        var stagedDirectory = Path.Combine(transactionRoot, "new");
        var backupDirectory = Path.Combine(transactionRoot, "old");
        var targetExisted = Directory.Exists(targetDir);
        var preserveTransaction = false;

        try
        {
            Directory.CreateDirectory(transactionRoot);
            CopyDirectory(sourceDir, stagedDirectory);
            if (!IsInstalledModDirectory(stagedDirectory))
            {
                throw new InvalidDataException("暂存的 Mod 缺少有效 manifest.json");
            }

            if (targetExisted)
            {
                Directory.Move(targetDir, backupDirectory);
            }

            try
            {
                Directory.Move(stagedDirectory, targetDir);
            }
            catch
            {
                if (targetExisted && Directory.Exists(backupDirectory) && !Directory.Exists(targetDir))
                {
                    try
                    {
                        Directory.Move(backupDirectory, targetDir);
                    }
                    catch
                    {
                        // 原目录仍在 backupDirectory，不能在 finally 中把它一起删除。
                        preserveTransaction = true;
                    }
                }

                throw;
            }

            if (targetExisted && Directory.Exists(backupDirectory))
            {
                if (RecycleBinService.TryMoveToRecycleBin(backupDirectory, out var recycleError))
                {
                    return;
                }

                // 回收站不可用时，不能让 finally 物理删除旧 Mod。先把新目录移回
                // 暂存位置，再恢复旧目录；恢复成功后删除的只是不再使用的新内容。
                var rollbackSucceeded = false;
                try
                {
                    if (Directory.Exists(targetDir))
                    {
                        Directory.Move(targetDir, stagedDirectory);
                    }

                    if (Directory.Exists(backupDirectory))
                    {
                        Directory.Move(backupDirectory, targetDir);
                    }

                    rollbackSucceeded = Directory.Exists(targetDir);
                }
                catch
                {
                    preserveTransaction = true;
                }

                if (rollbackSucceeded)
                {
                    try
                    {
                        if (Directory.Exists(stagedDirectory))
                        {
                            Directory.Delete(stagedDirectory, true);
                        }
                    }
                    catch
                    {
                        // 新内容属于本次事务产生的临时数据，清理失败不覆盖回滚结果。
                    }

                    throw new IOException($"无法将原有 Mod 移入回收站，已回滚本次整合包更新：{recycleError}");
                }

                throw new IOException($"无法将原有 Mod 移入回收站，且回滚失败；旧目录仍保留在：{backupDirectory}。原因：{recycleError}");
            }
        }
        finally
        {
            try
            {
                if (!preserveTransaction && Directory.Exists(transactionRoot))
                {
                    Directory.Delete(transactionRoot, true);
                }
            }
            catch
            {
                // 暂存清理失败不影响已经完成的替换；启动/刷新时的临时目录清理
                // 会再次处理遗留目录。
            }
        }
    }
}

/// <summary>Curseforge manifest 的精简模型（内部使用，避免依赖 SVL.Core.Platform.Modpack）。</summary>
internal sealed class CurseforgeManifest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("manifestVersion")]
    [JsonConverter(typeof(FlexibleInt32JsonConverter))]
    public int ManifestVersion { get; set; }

    [JsonPropertyName("files")]
    private List<CurseforgeManifestFile> _files = [];

    /// <summary>允许部分导出器把无 Mod 的整合包写成 files: null。</summary>
    public List<CurseforgeManifestFile> Files
    {
        get => _files;
        set => _files = value ?? [];
    }

    [JsonPropertyName("overrides")]
    public string? Overrides { get; set; }
}

internal sealed class CurseforgeManifestFile
{
    [JsonPropertyName("projectID")]
    [JsonConverter(typeof(FlexibleInt64JsonConverter))]
    public long ProjectId { get; set; }

    [JsonPropertyName("fileID")]
    [JsonConverter(typeof(FlexibleInt64JsonConverter))]
    public long FileId { get; set; }

    [JsonPropertyName("required")]
    public bool Required { get; set; } = true;
}

/// <summary>兼容部分 CurseForge 导出工具把项目/文件 ID 写成数字字符串。</summary>
internal sealed class FlexibleInt64JsonConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var number))
        {
            return number;
        }

        if (reader.TokenType == JsonTokenType.String &&
            long.TryParse(reader.GetString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var textNumber))
        {
            return textNumber;
        }

        if (reader.TokenType == JsonTokenType.Null)
        {
            return 0;
        }

        throw new JsonException("CurseForge 项目/文件 ID 不是有效的整数");
    }

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }
}

/// <summary>兼容部分导出工具把 CurseForge manifestVersion 写成数字字符串。</summary>
internal sealed class FlexibleInt32JsonConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number))
        {
            return number;
        }

        if (reader.TokenType == JsonTokenType.String &&
            int.TryParse(reader.GetString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var textNumber))
        {
            return textNumber;
        }

        if (reader.TokenType == JsonTokenType.Null)
        {
            return 0;
        }

        throw new JsonException("CurseForge manifestVersion 不是有效的整数");
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }
}
