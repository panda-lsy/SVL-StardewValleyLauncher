using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SVL.Avalonia.Models;
using SVL.Core.Platform.Abstractions;
using SVL.Core.Platform.IO;
using SVL.Core.Platform.Modpack;

namespace SVL.Avalonia.Services;

/// <summary>Collection 安装进度回调。</summary>
public sealed class CollectionInstallProgress
{
    public int Percent { get; set; }
    public string StepText { get; set; } = string.Empty;
    public string SubProgressText { get; set; } = string.Empty;
    public int SubProgress { get; set; } = -1;

    /// <summary>逐 Mod 进度；为空表示这是普通阶段进度。</summary>
    public string ModName { get; set; } = string.Empty;

    public int ModPhase { get; set; } = 1;

    public bool ModOptional { get; set; }

    public CollectionModTaskState? ModState { get; set; }

    public string ModMessage { get; set; } = string.Empty;

    /// <summary>Collection 清单为当前 Mod 提供的手动来源地址。</summary>
    public string ModSourceUrl { get; set; } = string.Empty;

    /// <summary>当前 Mod 需要用户手动下载或补充来源。</summary>
    public bool ModRequiresManualAction { get; set; }
}

/// <summary>Collection 安装结果。</summary>
public sealed class CollectionInstallResult
{
    public bool IsSuccess { get; init; }
    public bool IsCancelled { get; init; }
    public string Message { get; init; } = string.Empty;
    public string RuntimePath { get; init; } = string.Empty;
    public string VersionRootPath { get; init; } = string.Empty;
    public List<string> FailedMods { get; init; } = [];
    /// <summary>可选 Mod 自动安装失败，等待用户明确选择跳过或补装。</summary>
    public List<string> PendingOptionalMods { get; init; } = [];
    public List<string> InstalledMods { get; init; } = [];

    public static CollectionInstallResult Success(
        string runtimePath,
        string versionRootPath,
        List<string> installedMods,
        List<string>? failedMods = null,
        List<string>? pendingOptionalMods = null) => new()
    {
        IsSuccess = true,
        Message = "Collection 安装完成",
        RuntimePath = runtimePath,
        VersionRootPath = versionRootPath,
        InstalledMods = installedMods,
        FailedMods = failedMods ?? [],
        PendingOptionalMods = pendingOptionalMods ?? []
    };

    public static CollectionInstallResult Failed(string message, List<string>? failedMods = null) => new()
    {
        IsSuccess = false,
        Message = message,
        FailedMods = failedMods ?? []
    };

    public static CollectionInstallResult Cancelled(string message) => new()
    {
        IsSuccess = false,
        IsCancelled = true,
        Message = message
    };
}

/// <summary>Collection 单个 Mod 的安装结果，保留可展示给用户的失败原因。</summary>
internal readonly record struct CollectionModInstallResult(
    bool IsSuccess,
    string Message,
    IReadOnlyList<string> InstalledNames)
{
    public static CollectionModInstallResult Success(
        string message = "安装成功",
        IReadOnlyList<string>? installedNames = null) =>
        new(true, message, installedNames ?? Array.Empty<string>());

    public static CollectionModInstallResult Failed(string message) =>
        new(false, message, Array.Empty<string>());
}

/// <summary>
/// Nexus Collection 安装服务。承载 Collection 7z 下载 → collection.json 解析 → SMAPI 优先 → Phase 分阶段下载安装 Mod。
/// 对齐旧架构 NexusCollectionWizardTask，但不包含浏览器交互（复用 BrowserDownloadFallbackService）。
/// </summary>
public sealed class CollectionInstallService
{
    private const long SmapiModId = 2400;
    private const string GameDomain = "stardewvalley";

    private static readonly JsonSerializerOptions CollectionJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters =
        {
            new NexusCollectionJsonModSourceConverter(),
            new NexusCollectionJsonModConverter()
        }
    };

    private readonly IGameInstallPathLocator _gameInstallPathLocator;
    private readonly ISmapiInstallService _smapiInstallService;
    private readonly HttpDownloadService _httpDownloadService;
    private readonly RemoteCatalogService _remoteCatalogService;
    private readonly AppUserSettingsStore _settingsStore;
    private readonly NexusModDownloadResolverService _nexusResolver;
    private readonly INxmLinkParser _nxmLinkParser;
    private readonly BrowserDownloadFallbackService _browserFallback;
    private readonly ModpackInstallService _modpackInstallService;

    public CollectionInstallService(
        IGameInstallPathLocator gameInstallPathLocator,
        ISmapiInstallService smapiInstallService,
        HttpDownloadService httpDownloadService,
        RemoteCatalogService remoteCatalogService,
        AppUserSettingsStore settingsStore,
        NexusModDownloadResolverService nexusResolver,
        INxmLinkParser nxmLinkParser,
        BrowserDownloadFallbackService browserFallback,
        ModpackInstallService modpackInstallService)
    {
        _gameInstallPathLocator = gameInstallPathLocator;
        _smapiInstallService = smapiInstallService;
        _httpDownloadService = httpDownloadService;
        _remoteCatalogService = remoteCatalogService;
        _settingsStore = settingsStore;
        _nexusResolver = nexusResolver;
        _nxmLinkParser = nxmLinkParser;
        _browserFallback = browserFallback;
        _modpackInstallService = modpackInstallService;
    }

    /// <summary>
    /// 安装 Nexus Collection（从 NXM 链接完整流程）。完整流程：
    /// 1. 下载 Collection 7z
    /// 2. 解析 collection.json
    /// 3. 优先安装 SMAPI
    /// 4. 按 Phase 分阶段下载安装 Mod
    /// 5. 应用 bundled 文件
    /// 6. 保存实例配置
    /// </summary>
    /// <param name="collectionSlug">Collection slug（URL 标识符）。</param>
    /// <param name="revision">Collection 修订号（-1 表示最新）。</param>
    /// <param name="instanceName">版本隔离实例名。</param>
    /// <param name="onProgress">进度回调。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="gameBasePath">用户选择的游戏 Base 路径。</param>
    /// <param name="updateExisting">目标实例已存在时是否更新原实例。</param>
    /// <param name="resumeInstalledModNames">显式重试时允许复用的已完成条目名称。</param>
    /// <param name="skipOptionalModNames">用户已明确跳过的可选条目名称。</param>
    public async Task<CollectionInstallResult> InstallCollectionAsync(
        string collectionSlug,
        int revision,
        string instanceName,
        Action<CollectionInstallProgress>? onProgress,
        CancellationToken cancellationToken = default,
        string? gameBasePath = null,
        bool updateExisting = false,
        IReadOnlySet<string>? resumeInstalledModNames = null,
        IReadOnlySet<string>? skipOptionalModNames = null)
    {
        if (string.IsNullOrWhiteSpace(collectionSlug))
        {
            return CollectionInstallResult.Failed("Collection slug 不能为空");
        }

        if (string.IsNullOrWhiteSpace(instanceName))
        {
            instanceName = $"collection-{collectionSlug}";
        }

        var instanceNameValidation = InstanceNameValidator.Validate(instanceName);
        if (!instanceNameValidation.IsValid)
        {
            return CollectionInstallResult.Failed($"实例名称无效: {instanceNameValidation.ErrorMessage}");
        }
        instanceName = instanceName.Trim();

        var gamePath = !string.IsNullOrWhiteSpace(gameBasePath)
            ? InstanceRuntimePathResolver.ResolveBasePath(gameBasePath)
            : ResolveGamePath();
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            return CollectionInstallResult.Failed("未检测到游戏目录，无法安装 Collection");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "SVL", "collections", Guid.NewGuid().ToString());

        try
        {
            Directory.CreateDirectory(tempDir);

            // Collection 下载地址通常是短期签名 URL。按稳定的
            // gameDomain/slug/revision 命中缓存后，必须在认证检查和地址解析
            // 之前直接进入安装阶段，否则重启或无登录凭据时仍会重复打开浏览器。
            if (NexusCollectionDownloadCache.TryGet(
                    GameDomain,
                    collectionSlug,
                    revision,
                    out var cachedArchivePath,
                    IsValidCollectionArchive))
            {
                onProgress?.Invoke(new CollectionInstallProgress
                {
                    Percent = 2,
                    StepText = "复用已缓存 Collection，跳过浏览器和下载"
                });

                return await InstallFromArchiveCoreAsync(
                    cachedArchivePath,
                    instanceName,
                    gamePath,
                    tempDir,
                    onProgress,
                    cancellationToken,
                    null,
                    updateExisting,
                    resumeInstalledModNames,
                    skipOptionalModNames);
            }

            var settings = _settingsStore.Load();
            if (string.IsNullOrWhiteSpace(settings.NexusApiKey) &&
                string.IsNullOrWhiteSpace(settings.NexusOAuthAccessToken))
            {
                return CollectionInstallResult.Failed("Nexus 未登录，无法下载 Collection");
            }

            // ===== 阶段 1: 下载 Collection 7z =====
            onProgress?.Invoke(new CollectionInstallProgress { Percent = 2, StepText = "下载 Collection 数据" });
            cancellationToken.ThrowIfCancellationRequested();

            // 构造 NXM Collection 链接信息，解析下载地址
            var nxmInfo = new NxmLinkInfo
            {
                GameDomain = GameDomain,
                ResourceType = NxmResourceType.Collection,
                CollectionSlug = collectionSlug,
                RevisionNumber = revision
            };

            var resolveResult = await _nexusResolver.ResolveCollectionDownloadUrlAsync(
                nxmInfo, settings.NexusApiKey, settings.NexusOAuthAccessToken, cancellationToken);

            if (!resolveResult.IsSuccess || string.IsNullOrWhiteSpace(resolveResult.DownloadUrl))
            {
                return CollectionInstallResult.Failed($"Collection 下载地址解析失败: {resolveResult.Message}");
            }

            var archivePath = Path.Combine(tempDir, !string.IsNullOrWhiteSpace(resolveResult.FileName)
                ? resolveResult.FileName
                : $"collection-{collectionSlug}.7z");

            await _httpDownloadService.DownloadAsync(
                resolveResult.DownloadUrl,
                archivePath,
                null,
                cancellationToken,
                cacheValidator: IsValidCollectionArchive);

            // 只有完成下载且归档可被识别为 Collection 时才提升到稳定缓存；
            // HTML/登录页等错误响应不能污染下一次的缓存命中。
            NexusCollectionDownloadCache.Save(
                GameDomain,
                collectionSlug,
                revision,
                archivePath,
                IsValidCollectionArchive);

            onProgress?.Invoke(new CollectionInstallProgress { Percent = 5, StepText = "Collection 下载完成" });

            return await InstallFromArchiveCoreAsync(
                archivePath,
                instanceName,
                gamePath,
                tempDir,
                onProgress,
                cancellationToken,
                null,
                updateExisting: updateExisting,
                resumeInstalledModNames: resumeInstalledModNames,
                skipOptionalModNames: skipOptionalModNames);
        }
        catch (OperationCanceledException)
        {
            // 更新已有实例时，取消只应保留原目录；仅清理本次新建的半成品。
            if (!updateExisting)
            {
                CleanupVersionDirectory(gamePath, instanceName);
            }

            return CollectionInstallResult.Cancelled("Collection 安装已取消");
        }
        catch (Exception ex)
        {
            return CollectionInstallResult.Failed($"Collection 安装失败: {ex.Message}");
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>校验本地文件是否确实包含可识别的 Nexus Collection 清单。</summary>
    private static bool IsValidCollectionArchive(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            return false;
        }

        try
        {
            var detection = ModpackTypeDetector.Detect(archivePath);
            if (!string.IsNullOrWhiteSpace(detection.TempExtractPath))
            {
                ModpackTypeDetector.CleanupTempDirectory(detection.TempExtractPath);
            }

            return detection.Type == ModpackType.NexusCollection;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 从本地 Collection 7z 文件安装（拖拽场景）。跳过下载阶段，从解析 collection.json 开始。
    /// 对齐旧架构 NexusCollectionWizardTask 构造函数 A（直接提供已下载文件路径）。
    /// </summary>
    /// <param name="archivePath">本地 Collection 7z/zip 文件路径。</param>
    /// <param name="instanceName">版本隔离实例名。</param>
    /// <param name="onProgress">进度回调。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="gameBasePath">用户选择的游戏 Base 路径。</param>
    /// <param name="customIconPath">Collection 旁路图标路径，优先于压缩包内图标。</param>
    /// <param name="resumeInstalledModNames">显式重试时允许复用的已完成条目名称。</param>
    /// <param name="skipOptionalModNames">用户已明确跳过的可选条目名称。</param>
    public async Task<CollectionInstallResult> InstallCollectionFromArchiveAsync(
        string archivePath,
        string instanceName,
        Action<CollectionInstallProgress>? onProgress,
        CancellationToken cancellationToken = default,
        string? gameBasePath = null,
        string? customIconPath = null,
        bool updateExisting = false,
        IReadOnlySet<string>? resumeInstalledModNames = null,
        IReadOnlySet<string>? skipOptionalModNames = null)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            return CollectionInstallResult.Failed("Collection 压缩包文件不存在");
        }

        if (string.IsNullOrWhiteSpace(instanceName))
        {
            instanceName = $"collection-{Path.GetFileNameWithoutExtension(archivePath)}";
        }

        var instanceNameValidation = InstanceNameValidator.Validate(instanceName);
        if (!instanceNameValidation.IsValid)
        {
            return CollectionInstallResult.Failed($"实例名称无效: {instanceNameValidation.ErrorMessage}");
        }
        instanceName = instanceName.Trim();

        var gamePath = !string.IsNullOrWhiteSpace(gameBasePath)
            ? InstanceRuntimePathResolver.ResolveBasePath(gameBasePath)
            : ResolveGamePath();
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            return CollectionInstallResult.Failed("未检测到游戏目录，无法安装 Collection");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "SVL", "collections", Guid.NewGuid().ToString());

        try
        {
            Directory.CreateDirectory(tempDir);
            // 将本地压缩包复制到临时目录（避免解压污染原文件所在目录）
            var tempArchivePath = Path.Combine(tempDir, Path.GetFileName(archivePath));
            File.Copy(archivePath, tempArchivePath, true);

            onProgress?.Invoke(new CollectionInstallProgress { Percent = 5, StepText = "Collection 文件就绪" });
            return await InstallFromArchiveCoreAsync(
                tempArchivePath,
                instanceName,
                gamePath,
                tempDir,
                onProgress,
                cancellationToken,
                customIconPath,
                updateExisting,
                resumeInstalledModNames,
                skipOptionalModNames);
        }
        catch (OperationCanceledException)
        {
            if (!updateExisting)
            {
                CleanupVersionDirectory(gamePath, instanceName);
            }

            return CollectionInstallResult.Cancelled("Collection 安装已取消");
        }
        catch (Exception ex)
        {
            return CollectionInstallResult.Failed($"Collection 安装失败: {ex.Message}");
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>Collection 安装核心流程（阶段 2-6）：解压 → 解析 → SMAPI → Phase 分阶段 → bundled → 保存实例。</summary>
    private async Task<CollectionInstallResult> InstallFromArchiveCoreAsync(
        string archivePath,
        string instanceName,
        string gamePath,
        string tempDir,
        Action<CollectionInstallProgress>? onProgress,
        CancellationToken cancellationToken,
        string? customIconPath,
        bool updateExisting,
        IReadOnlySet<string>? resumeInstalledModNames,
        IReadOnlySet<string>? skipOptionalModNames)
    {
        var installedMods = new List<string>();
        var failedMods = new List<string>();
        var pendingOptionalMods = new List<string>();

        try
        {
            // ===== 阶段 2: 解压并解析 collection.json =====
            onProgress?.Invoke(new CollectionInstallProgress { Percent = 7, StepText = "解析 collection.json" });
            cancellationToken.ThrowIfCancellationRequested();

            var extractDir = Path.Combine(tempDir, "extracted");
            Directory.CreateDirectory(extractDir);

            // zip/cfmodpack 走现有双重回退；7z 走内置 SharpCompress，不要求系统安装 7-Zip。
            try
            {
                if (ArchiveExtractor.IsSevenZip(archivePath))
                {
                    ArchiveExtractor.ExtractSevenZipToDirectory(archivePath, extractDir);
                }
                else
                {
                    ZipExtractor.ExtractToDirectory(archivePath, extractDir);
                }
            }
            catch (Exception archiveException)
            {
                // 保留命令行 7z 作为极少数 SharpCompress 不兼容归档的兜底。
                if (!TryExtractWith7Zip(archivePath, extractDir))
                {
                    return CollectionInstallResult.Failed($"无法解压 Collection 压缩包: {archiveException.Message}");
                }
            }

            // 查找 collection.json
            // 外层目录可能携带同名说明文件或打包元数据；选择第一个文件会让
            // 内层真实 Collection 清单永远无法解析。按结构校验候选项后再确定根目录。
            var collectionJsonPath = FindValidCollectionManifestFile(extractDir) ?? string.Empty;

            if (!File.Exists(collectionJsonPath))
            {
                return CollectionInstallResult.Failed("Collection 压缩包中未找到 collection.json");
            }

            // Nexus/导出工具经常把整个 Collection 放在一个外层目录中。
            // collection.json 虽然可以递归找到，但 bundled 不能固定从解压根目录
            // 查找，否则会出现“安装完成但 bundled Mod 为 0 个”的静默结果。
            var collectionRoot = Path.GetDirectoryName(collectionJsonPath) ?? extractDir;

            var collection = JsonSerializer.Deserialize<NexusCollectionJson>(
                await ManifestTextReader.ReadAllTextAsync(collectionJsonPath, cancellationToken),
                CollectionJsonOptions);

            if (collection?.Info == null || collection.Mods == null)
            {
                return CollectionInstallResult.Failed("collection.json 解析失败或格式不正确");
            }

            var totalMods = collection.Mods.Length;
            onProgress?.Invoke(new CollectionInstallProgress
            {
                Percent = 10,
                StepText = $"collection.json 解析完成: {totalMods} 个 Mod"
            });

            // ===== 阶段 3: 优先安装 SMAPI =====
            onProgress?.Invoke(new CollectionInstallProgress { Percent = 12, StepText = "安装 SMAPI" });
            cancellationToken.ThrowIfCancellationRequested();

            var smapiMod = collection.Mods.FirstOrDefault(m => m.Source?.ModId == SmapiModId || IsSmapiName(m.Name));
            // Collection 清单如果固定了 SMAPI 的平台/FileID，必须先精确命中该
            // 归档；不能直接按“同大版本的任意 SMAPI”复用，否则清单锁定的
            // 安装包可能被更高版本替代。未命中时再进入统一解析器。
            var smapiZipPath = TryGetExactCachedSmapiArchive(smapiMod, out var cacheMessage)
                ? cacheMessage.Path
                : null;
            if (!string.IsNullOrWhiteSpace(smapiZipPath))
            {
                onProgress?.Invoke(new CollectionInstallProgress
                {
                    Percent = 13,
                    StepText = cacheMessage.Message
                });
            }

            smapiZipPath ??= await ResolveSmapiZipAsync(
                smapiMod?.Version,
                instanceName,
                gamePath,
                onProgress,
                cancellationToken,
                allowCurrentInstance: updateExisting);
            if (string.IsNullOrWhiteSpace(smapiZipPath))
            {
                return CollectionInstallResult.Failed("无法获取经过校验的 SMAPI 安装包，已停止 Collection 安装");
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
                    ? CollectionInstallResult.Cancelled($"SMAPI 安装已取消: {smapiResult.Message}")
                    : CollectionInstallResult.Failed($"SMAPI 安装失败: {smapiResult.Message}");
            }

            var versionRoot = Path.Combine(gamePath, "versions", instanceName);
            var runtimePath = InstanceRuntimePathResolver.Resolve(versionRoot);
            var modsPath = Path.Combine(runtimePath, "Mods");
            Directory.CreateDirectory(modsPath);

            onProgress?.Invoke(new CollectionInstallProgress { Percent = 15, StepText = "SMAPI 就绪" });

            // ===== 阶段 4: 按 Phase 分阶段下载安装 Mod =====
            onProgress?.Invoke(new CollectionInstallProgress { Percent = 17, StepText = "下载安装 Mod" });
            cancellationToken.ThrowIfCancellationRequested();

            // 过滤掉 SMAPI 和 bundle 类型，按 Phase 分组
            var modGroups = collection.Mods
                .Where(m => m.Source?.ModId != SmapiModId && !IsSmapiName(m.Name))
                .Where(m => !string.Equals(m.Source?.Type, "bundle", StringComparison.OrdinalIgnoreCase))
                .GroupBy(m => m.Phase > 0 ? m.Phase : 1)
                .OrderBy(g => g.Key)
                .ToList();

            var modsToDownload = modGroups.SelectMany(g => g).ToList();
            var completedMods = 0;
            var totalToDownload = modsToDownload.Count;
            var pendingOptionalCount = 0;

            onProgress?.Invoke(new CollectionInstallProgress
            {
                Percent = 17,
                StepText = "下载安装 Mod",
                SubProgress = totalToDownload > 0 ? 0 : -1,
                SubProgressText = totalToDownload > 0
                    ? $"0/{totalToDownload} 已处理"
                    : "没有待下载安装的 Mod"
            });

            // 先把完整清单推送给任务页，用户可以在安装开始时就看到所有
            // Mod 的等待状态；后续状态事件只更新对应条目，不再靠总进度猜测。
            foreach (var pendingMod in modsToDownload)
            {
                ReportCollectionModProgress(
                    onProgress,
                    pendingMod,
                    CollectionModTaskState.Pending,
                    "等待所属 Phase");
            }

            foreach (var phaseGroup in modGroups)
            {
                foreach (var mod in phaseGroup.OrderBy(m => m.Name ?? string.Empty))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var modName = mod.Name ?? $"mod-{mod.Source?.ModId}";

                    if (mod.Optional &&
                        skipOptionalModNames?.Contains(modName) == true)
                    {
                        ReportCollectionModProgress(
                            onProgress,
                            mod,
                            CollectionModTaskState.Skipped,
                            "此前已由用户选择跳过");
                        completedMods++;
                        onProgress?.Invoke(new CollectionInstallProgress
                        {
                            Percent = 17 + (int)(60.0 * completedMods / Math.Max(1, totalToDownload)),
                            StepText = $"阶段 {phaseGroup.Key}: 跳过可选 Mod",
                            SubProgress = CalculateCompletionPercent(completedMods, totalToDownload),
                            SubProgressText = $"{completedMods}/{totalToDownload} 已处理"
                        });
                        continue;
                    }

                    // 显式重试时，失败通常只是一两个来源暂时不可用；已经成功的
                    // 条目不应再次下载或覆盖。任务状态只是候选信号，必须再校验
                    // 当前 Mods 目录中的来源凭证或有效 manifest，防止残缺目录被
                    // 错误地当成已完成安装。
                    if (resumeInstalledModNames?.Contains(modName) == true &&
                        IsCollectionModAlreadyInstalled(mod, modsPath))
                    {
                        installedMods.Add(modName);
                        ReportCollectionModProgress(
                            onProgress,
                            mod,
                            CollectionModTaskState.Installed,
                            "此前已安装，重试时跳过");
                        completedMods++;
                        onProgress?.Invoke(new CollectionInstallProgress
                        {
                            Percent = 17 + (int)(60.0 * completedMods / Math.Max(1, totalToDownload)),
                            StepText = $"阶段 {phaseGroup.Key}: 复用已安装 Mod",
                            SubProgress = CalculateCompletionPercent(completedMods, totalToDownload),
                            SubProgressText = $"{completedMods}/{totalToDownload} 已处理"
                        });
                        continue;
                    }

                    ReportCollectionModProgress(
                        onProgress,
                        mod,
                        CollectionModTaskState.Downloading,
                        $"正在处理 Phase {phaseGroup.Key}");

                    try
                    {
                        var modResult = await DownloadAndInstallCollectionModWithRetryAsync(
                            mod,
                            modsPath,
                            cancellationToken,
                            onProgress);
                        if (modResult.IsSuccess)
                        {
                            var patchResult = ApplyCollectionPatches(
                                collectionRoot,
                                modsPath,
                                mod,
                                modResult.InstalledNames);
                            if (!patchResult.IsSuccess && mod.Patches is { Count: > 0 })
                            {
                                var message = patchResult.Message;
                                if (mod.Optional)
                                {
                                    pendingOptionalCount++;
                                    pendingOptionalMods.Add($"{modName}: {message}");
                                    ReportCollectionModProgress(
                                        onProgress,
                                        mod,
                                        CollectionModTaskState.NeedsDecision,
                                        $"可选 Mod 安装失败，请选择跳过或补装：{message}",
                                        requiresManualAction: true);
                                }
                                else
                                {
                                    failedMods.Add($"{modName}: {message}");
                                    ReportCollectionModProgress(
                                        onProgress,
                                        mod,
                                        CollectionModTaskState.Failed,
                                        message);
                                }
                                onProgress?.Invoke(new CollectionInstallProgress
                                {
                                    Percent = 17 + (int)(60.0 * completedMods / Math.Max(1, totalToDownload)),
                                    StepText = $"阶段 {phaseGroup.Key}: 补丁应用失败",
                                    SubProgress = CalculateCompletionPercent(completedMods, totalToDownload),
                                    SubProgressText = $"{modName}: {message}"
                                });
                            }
                            else
                            {
                                installedMods.Add(modName);
                                ReportCollectionModProgress(
                                    onProgress,
                                    mod,
                                    CollectionModTaskState.Installed,
                                    patchResult.AppliedCount > 0
                                        ? $"已安装，已应用 {patchResult.AppliedCount} 个补丁"
                                        : "安装成功");
                            }
                        }
                        else
                        {
                            if (mod.Optional)
                            {
                                pendingOptionalCount++;
                                pendingOptionalMods.Add($"{modName}: {modResult.Message}");
                                ReportCollectionModProgress(
                                    onProgress,
                                    mod,
                                    CollectionModTaskState.NeedsDecision,
                                    $"可选 Mod 安装失败，请选择跳过或补装：{modResult.Message}",
                                    requiresManualAction: true);
                            }
                            else
                            {
                                failedMods.Add($"{modName}: {modResult.Message}");
                                ReportCollectionModProgress(
                                    onProgress,
                                    mod,
                                    CollectionModTaskState.Failed,
                                    modResult.Message);
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        if (mod.Optional)
                        {
                            pendingOptionalCount++;
                            pendingOptionalMods.Add($"{modName}: {ex.Message}");
                            ReportCollectionModProgress(
                                onProgress,
                                mod,
                                CollectionModTaskState.NeedsDecision,
                                $"可选 Mod 安装失败，请选择跳过或补装：{ex.Message}",
                                requiresManualAction: true);
                        }
                        else
                        {
                            failedMods.Add($"{modName}: {ex.Message}");
                            ReportCollectionModProgress(
                                onProgress,
                                mod,
                                CollectionModTaskState.Failed,
                                ex.Message);
                        }
                        onProgress?.Invoke(new CollectionInstallProgress
                        {
                            Percent = 17 + (int)(60.0 * completedMods / Math.Max(1, totalToDownload)),
                            StepText = "下载安装 Mod",
                            SubProgress = CalculateCompletionPercent(completedMods, totalToDownload),
                            SubProgressText = $"跳过 {modName}: {ex.Message}"
                        });
                    }

                    completedMods++;
                    onProgress?.Invoke(new CollectionInstallProgress
                    {
                        Percent = 17 + (int)(60.0 * completedMods / Math.Max(1, totalToDownload)),
                        StepText = $"阶段 {phaseGroup.Key}: 下载安装 Mod",
                        SubProgress = CalculateCompletionPercent(completedMods, totalToDownload),
                        SubProgressText = $"{completedMods}/{totalToDownload} 已处理"
                    });
                }
            }

            onProgress?.Invoke(new CollectionInstallProgress
            {
                Percent = 78,
                StepText = pendingOptionalCount > 0
                    ? $"Mod 下载安装完成（{failedMods.Count} 个失败，{pendingOptionalCount} 个可选 Mod 待处理）"
                    : $"Mod 下载安装完成（{failedMods.Count} 个失败）",
                SubProgress = -1
            });

            // ===== 阶段 5: 应用 bundled 文件 =====
            onProgress?.Invoke(new CollectionInstallProgress { Percent = 80, StepText = "应用 bundled 文件" });
            cancellationToken.ThrowIfCancellationRequested();

            var bundledDir = FindDirectoryByNameIgnoreCase(collectionRoot, "bundled");
            if (string.IsNullOrWhiteSpace(bundledDir))
            {
                // 兼容少数把 collection.json 放在子目录、bundled 放在根目录的包。
                bundledDir = FindDirectoryByNameIgnoreCase(extractDir, "bundled");
            }
            if (Directory.Exists(bundledDir))
            {
                foreach (var dir in Directory.GetDirectories(bundledDir))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var modName = Path.GetFileName(dir);
                    // bundled 目录经常是“发行包名/实际 Mod 目录/manifest.json”，
                    // 不能直接复制外层目录，否则游戏会在 Mods 下多出一层嵌套。
                    if (ModpackInstallService.InstallBundledModDirectory(
                        dir,
                        modsPath,
                        modName,
                        out var installedNames,
                        out var bundledFailedNames))
                    {
                        installedMods.AddRange(installedNames);
                    }

                    if (bundledFailedNames.Count > 0)
                    {
                        failedMods.AddRange(
                            bundledFailedNames.Select(name =>
                                $"{name}: bundled 中未找到有效 manifest.json 或复制失败"));
                    }
                    else if (installedNames.Count == 0)
                    {
                        failedMods.Add($"{modName}: bundled 中未找到有效 manifest.json");
                    }
                }
            }

            onProgress?.Invoke(new CollectionInstallProgress { Percent = 85, StepText = "bundled 文件应用完成" });

            // ===== 阶段 6: 保存实例配置 =====
            onProgress?.Invoke(new CollectionInstallProgress { Percent = 87, StepText = "保存实例配置" });
            cancellationToken.ThrowIfCancellationRequested();

            // 以 collection.json 所在目录优先查找，避免外层归档或某个 bundled
            // Mod 里的 icon.png 抢走整合包自己的图标；extractDir 仍作为兼容回退。
            ExtractCollectionIcon(collectionRoot, versionRoot, customIconPath, extractDir);
            SaveInstanceRecord(instanceName, runtimePath);

            var finalPercent = failedMods.Count > 0 || pendingOptionalMods.Count > 0 ? 99 : 100;
            onProgress?.Invoke(new CollectionInstallProgress
            {
                Percent = finalPercent,
                StepText = failedMods.Count > 0 || pendingOptionalMods.Count > 0
                    ? $"Collection 安装完成（{failedMods.Count} 个 Mod 失败，{pendingOptionalMods.Count} 个可选 Mod 待处理）"
                    : "Collection 安装完成"
            });

            return CollectionInstallResult.Success(runtimePath, versionRoot, installedMods, failedMods, pendingOptionalMods);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return CollectionInstallResult.Failed($"Collection 安装失败: {ex.Message}", failedMods);
        }
    }

    // ================================================================
    // 内部方法
    // ================================================================

    private static CollectionPatchResult ApplyCollectionPatches(
        string collectionRoot,
        string modsPath,
        NexusCollectionJsonMod mod,
        IReadOnlyList<string> installedNames)
    {
        if (mod.Patches is not { Count: > 0 })
        {
            return new CollectionPatchResult(true, 0, 0, "没有需要应用的补丁");
        }

        var candidateNames = installedNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 少数缓存命中路径只返回成功状态，来源凭证仍能提供真实安装目录。
        if (candidateNames.Count == 0 && mod.Source is { ModId: > 0, FileId: > 0 } source)
        {
            candidateNames.AddRange(
                ModpackInstallService.FindInstalledModDirectoriesBySource(
                        modsPath,
                        ResolveCollectionSourcePlatform(source) ?? string.Empty,
                        source.ModId,
                        source.FileId)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Cast<string>());
        }

        if (candidateNames.Count == 0)
        {
            return new CollectionPatchResult(false, 0, mod.Patches.Count, "补丁对应的已安装 Mod 目录未找到");
        }

        var applied = 0;
        var skipped = 0;
        var messages = new List<string>();
        foreach (var name in candidateNames)
        {
            var modPath = Path.Combine(modsPath, name);
            var result = CollectionPatchService.ApplyPatches(
                collectionRoot,
                modPath,
                mod.Name ?? name,
                mod.Patches);
            applied += result.AppliedCount;
            skipped += result.SkippedCount;
            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                messages.Add(result.Message);
            }
        }

        return new CollectionPatchResult(
            applied > 0,
            applied,
            skipped,
            string.Join("；", messages.Distinct(StringComparer.Ordinal)));
    }

    private static void ReportCollectionModProgress(
        Action<CollectionInstallProgress>? onProgress,
        NexusCollectionJsonMod mod,
        CollectionModTaskState state,
        string message,
        bool requiresManualAction = false)
    {
        onProgress?.Invoke(new CollectionInstallProgress
        {
            ModName = mod.Name ?? $"mod-{mod.Source?.ModId}",
            ModPhase = mod.Phase > 0 ? mod.Phase : 1,
            ModOptional = mod.Optional,
            ModState = state,
            ModMessage = message,
            ModSourceUrl = mod.Source?.Url ?? string.Empty,
            ModRequiresManualAction = requiresManualAction || IsManualSourceRequiringUserAction(mod.Source)
        });
    }

    private static bool IsCollectionModAlreadyInstalled(
        NexusCollectionJsonMod mod,
        string modsPath)
    {
        if (string.IsNullOrWhiteSpace(modsPath) || !Directory.Exists(modsPath))
        {
            return false;
        }

        var source = mod.Source;
        if (source != null)
        {
            var platform = ResolveCollectionSourcePlatform(source);
            var projectId = source.ModId;
            var fileId = source.FileId;

            if (!string.IsNullOrWhiteSpace(source.Url) &&
                TryParseNexusPageIds(source.Url, out var pageModId, out var pageFileId))
            {
                projectId = projectId > 0 ? projectId : pageModId;
                fileId = fileId > 0 ? fileId : pageFileId;
            }

            if (fileId <= 0 &&
                DownloadOptionIdentityParser.TryExtractFileId(
                    source.LogicalFilename,
                    out var logicalFileId))
            {
                fileId = logicalFileId;
            }

            if (!string.IsNullOrWhiteSpace(platform) && projectId > 0 && fileId > 0 &&
                ModpackInstallService.FindInstalledModDirectoriesBySource(
                    modsPath,
                    platform,
                    projectId,
                    fileId).Count > 0)
            {
                return true;
            }
        }

        // 旧 Collection 可能没有完整的来源 ID。仅在目标目录中找到有效
        // manifest 且 Name 与清单条目一致时才允许名称回退，避免空目录/外层
        // 包装目录被误判为已安装。
        var expectedName = mod.Name?.Trim();
        if (string.IsNullOrWhiteSpace(expectedName))
        {
            return false;
        }

        try
        {
            foreach (var directory in Directory.GetDirectories(modsPath))
            {
                var leaf = Path.GetFileName(directory);
                if (string.Equals(leaf, "_downloads", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(leaf, "_staging", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var manifestPath = Directory
                    .EnumerateFiles(directory, "manifest.json", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (string.IsNullOrWhiteSpace(manifestPath))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var manifestName = document.RootElement.TryGetProperty("Name", out var nameValue)
                    ? nameValue.GetString()
                    : document.RootElement.TryGetProperty("name", out var lowerNameValue)
                        ? lowerNameValue.GetString()
                        : null;
                if (string.Equals(leaf, expectedName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(manifestName?.Trim(), expectedName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // 单个目录被占用或 manifest 损坏时，不影响重试其它条目；
            // 未能确认完整安装就继续正常下载流程。
        }

        return false;
    }

    private static bool IsManualSourceRequiringUserAction(NexusCollectionJsonModSource? source)
    {
        return source != null &&
               string.Equals(source.Type, "manual", StringComparison.OrdinalIgnoreCase) &&
               !ModpackInstallService.IsLikelyDirectDownloadUrl(source.Url);
    }

    private async Task<CollectionModInstallResult> DownloadAndInstallCollectionModWithRetryAsync(
        NexusCollectionJsonMod mod,
        string modsPath,
        CancellationToken ct,
        Action<CollectionInstallProgress>? onProgress)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var result = await DownloadAndInstallCollectionModAsync(mod, modsPath, ct);
                if (result.IsSuccess ||
                    attempt >= maxAttempts ||
                    !ModpackInstallService.ShouldRetryModSourceFailure(result.Message))
                {
                    return result;
                }

                // Collection 的解析/下载链路同样会把一部分网络、CDN 和归档问题
                // 包装成失败结果；这些结果不能跳过有限重试。来源缺失、登录、
                // 浏览器回调未完成和取消等确定性问题由共享策略排除，避免重复弹窗。
                onProgress?.Invoke(new CollectionInstallProgress
                {
                    Percent = 17,
                    StepText = "下载安装 Mod",
                    SubProgressText = $"{mod.Name ?? "unknown"}: 第 {attempt + 1}/{maxAttempts} 次重试（{result.Message}）"
                });
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt >= maxAttempts)
                {
                    throw;
                }

                onProgress?.Invoke(new CollectionInstallProgress
                {
                    Percent = 17,
                    StepText = "下载安装 Mod",
                    SubProgressText = $"{mod.Name ?? "unknown"}: 第 {attempt + 1}/{maxAttempts} 次重试（{ex.Message}）"
                });
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct);
            }
        }

        return CollectionModInstallResult.Failed("未知 Collection Mod 安装错误");
    }

    private string ResolveGamePath()
    {
        var detectedPath = _gameInstallPathLocator.TryLocateSteamStardewPath()
            ?? _gameInstallPathLocator.TryLocateGogStardewPath()
            ?? _gameInstallPathLocator.TryLocateXboxStardewPath()
            ?? string.Empty;
        return InstanceRuntimePathResolver.ResolveBasePath(detectedPath);
    }

    private static bool IsSmapiName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (!name.StartsWith("SMAPI", StringComparison.OrdinalIgnoreCase)) return false;
        var exclude = new[] { "Component", "Dependency", "Extension", "Addon", "Patch" };
        return !exclude.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetExactCachedSmapiArchive(
        NexusCollectionJsonMod? smapiMod,
        out (string Path, string Message) cacheInfo)
    {
        cacheInfo = (string.Empty, string.Empty);
        var source = smapiMod?.Source;
        if (source == null || source.FileId <= 0)
        {
            return false;
        }

        var isCurseforge = (!string.IsNullOrWhiteSpace(source.Type) &&
                            source.Type.Contains("curse", StringComparison.OrdinalIgnoreCase)) ||
                           IsLikelyCurseforgeUrl(source.Url);
        var validator = new Func<string, bool>(path =>
            ModpackInstallService.TryNormalizeSmapiArchive(path) &&
            SmapiPackageVersionInspector.IsCompatible(smapiMod?.Version, path));

        if (isCurseforge && source.ModId > 0 &&
            CurseforgeDownloadCache.TryGet(source.ModId, source.FileId, out var curseforgePath, validator))
        {
            cacheInfo = (
                curseforgePath,
                $"复用 Collection 清单指定的 CurseForge SMAPI 缓存（FileID: {source.FileId}）");
            return true;
        }

        // Nexus Collection 的 SMAPI 项目固定为 2400；只对这个项目执行精确
        // 命中，避免把其它平台的数字 ID 误送入 Nexus 缓存。
        if (!isCurseforge && source.ModId == SmapiModId &&
            NexusDownloadCache.TryGet(source.ModId, source.FileId, out var nexusPath, validator))
        {
            cacheInfo = (
                nexusPath,
                $"复用 Collection 清单指定的 Nexus SMAPI 缓存（FileID: {source.FileId}）");
            return true;
        }

        return false;
    }

    private async Task<string?> ResolveSmapiZipAsync(
        string? preferredVersion,
        string instanceName,
        string gamePath,
        Action<CollectionInstallProgress>? onProgress,
        CancellationToken ct,
        bool allowCurrentInstance = false)
    {
        // Collection 与 SVL 整合包必须共享同一套缓存、版本兼容和官方回退规则。
        // 解析器内部已经按 SMAPI 版本加锁，这里只保留 Collection 的进度模型。
        return await _modpackInstallService.ResolveSmapiZipForCollectionAsync(
            preferredVersion,
            instanceName,
            gamePath,
            onProgress,
            ct,
            allowCurrentInstance);
    }

    // 保留旧测试/兼容调用的稳定入口；实际解析统一由 ModpackInstallService
    // 承担，避免 Collection 再维护一套官方 Release 地址规则。
    private static string BuildOfficialSmapiReleaseUrl(string version)
    {
        var escapedVersion = Uri.EscapeDataString(version);
        return $"https://github.com/Pathoschild/SMAPI/releases/download/{escapedVersion}/SMAPI-{escapedVersion}-installer.zip";
    }

    private static string? FindValidCollectionManifestFile(string directory)
    {
        try
        {
            var direct = Path.Combine(directory, "collection.json");
            var candidates = new List<string>();
            if (File.Exists(direct))
            {
                candidates.Add(direct);
            }

            candidates.AddRange(
                ModpackInstallService.EnumerateFilesSafe(directory, "collection.json")
                    .Where(path => !string.Equals(path, direct, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => path.Count(character =>
                        character == Path.DirectorySeparatorChar ||
                        character == Path.AltDirectorySeparatorChar)));

            return candidates.FirstOrDefault(IsValidCollectionManifestFile);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsValidCollectionManifestFile(string path)
    {
        try
        {
            var collection = JsonSerializer.Deserialize<NexusCollectionJson>(
                ManifestTextReader.ReadAllText(path),
                CollectionJsonOptions);
            return collection?.Info != null && collection.Mods != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCompatibleSmapiVersion(string? preferred, string? candidate)
    {
        var preferredVersion = ParseSmapiVersion(preferred?.Replace("SMAPI", string.Empty, StringComparison.OrdinalIgnoreCase).Trim());
        var candidateVersion = ParseSmapiVersion(candidate);

        // 目标版本明确时，未知候选版本不能安全复用；否则名为 SMAPI-latest.zip
        // 或内容缺少版本目录的缓存会绕过版本兼容性检查。
        return preferredVersion == null ||
               (candidateVersion != null &&
                preferredVersion.Major == candidateVersion.Major &&
                candidateVersion >= preferredVersion);
    }

    private static Version? ParseSmapiVersion(string? value)
    {
        return Version.TryParse(value, out var version) ? version : null;
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

        return Math.Clamp((int)Math.Floor(completed * 100.0 / total), 0, 99);
    }

    /// <summary>下载并安装单个 Collection Mod。</summary>
    private async Task<CollectionModInstallResult> DownloadAndInstallCollectionModAsync(
        NexusCollectionJsonMod mod,
        string modsPath,
        CancellationToken ct)
    {
        var source = mod.Source;
        if (source == null)
        {
            return CollectionModInstallResult.Failed("缺少来源信息");
        }

        // manual 是 Collection 作者明确要求用户手动处理的来源。
        // 只有明确的归档直链才安全交给下载器；网页地址、NXM 入口和只有
        // 数字 ID 的条目不能当成压缩包下载，否则会把 HTML/登录页写入
        // 临时文件并掩盖真正原因。
        if (IsManualSourceRequiringUserAction(source))
        {
            return CollectionModInstallResult.Failed(BuildManualSourceMessage(mod, source));
        }

        var sourcePlatform = ResolveCollectionSourcePlatform(source);
        NxmLinkInfo? parsedNxm = null;

        // 部分旧 Collection 只保留 Nexus 页面 URL，modId/fileId 没有展开到字段。
        // 先从 NXM 或页面 URL 补回 ID，仍然走统一的 Nexus 下载/缓存链路。
        var resolvedModId = source.ModId;
        var resolvedFileId = source.FileId;
        if (!string.IsNullOrWhiteSpace(source.Url) &&
            _nxmLinkParser.TryParse(source.Url, out var nxmInfo, out _))
        {
            parsedNxm = nxmInfo;
            resolvedModId = nxmInfo.ModId;
            resolvedFileId = nxmInfo.FileId;
        }

        if ((resolvedModId <= 0 || resolvedFileId <= 0) &&
            TryParseNexusPageIds(source.Url, out var pageModId, out var pageFileId))
        {
            resolvedModId = pageModId;
            resolvedFileId = pageFileId;
        }

        // Nexus 文件页 URL 有时不带 file_id，Collection 标准字段会把实际
        // 文件名保存在 logicalFilename（例如“File 7448774_ ...zip”）。
        // 先从该稳定本地字段补回 FileID，再配合页面 URL 的 ModID 进入精确
        // 下载/缓存分支，不能退化成“按 ModID 选择最新文件”。
        if (resolvedFileId <= 0 &&
            DownloadOptionIdentityParser.TryExtractFileId(
                source.LogicalFilename,
                out var logicalFileId))
        {
            resolvedFileId = logicalFileId;
        }

        if (resolvedModId <= 0 &&
            NexusSourceParser.TryParseModId(source.Url, out var pageOnlyModIdFromUrl))
        {
            resolvedModId = pageOnlyModIdFromUrl;
            sourcePlatform ??= "NexusMods";
        }

        // Collection 也可能携带 CurseForge 的 project/file ID。来源类型优先于
        // “两个字段都是数字”的形状判断，避免把 CurseForge ID 错送到 Nexus API。
        if (string.Equals(sourcePlatform, "Curseforge", StringComparison.OrdinalIgnoreCase))
        {
            if (resolvedModId > 0 && resolvedFileId > 0 &&
                ModpackInstallService.TryInstallCachedCurseforgeMod(
                    resolvedModId,
                    resolvedFileId,
                    modsPath,
                    mod.Name ?? "unknown",
                    out var cachedCurseforgeNames))
            {
                return CollectionModInstallResult.Success(
                    "已复用 CurseForge 缓存并安装",
                    cachedCurseforgeNames);
            }

            if (resolvedModId > 0)
            {
                // 来源中已经有真实 CDN/归档直链时直接使用，避免仅有 projectId
                // 的旧 Collection 调列表接口后误选项目的第一个文件。
                var curseforgeUrl = ModpackInstallService.IsLikelyDirectDownloadUrl(source.Url)
                    ? source.Url!
                    : await _remoteCatalogService.ResolveCurseforgeFileDownloadUrlAsync(
                        resolvedModId,
                        resolvedFileId,
                        string.Empty,
                        ct);
                if (ModpackInstallService.IsLikelyDirectDownloadUrl(curseforgeUrl))
                {
                    var effectiveCurseforgeFileId = resolvedFileId > 0
                        ? resolvedFileId
                        : ModpackInstallService.TryParseCurseforgeFileIdFromCdnUrl(
                            curseforgeUrl,
                            out var parsedCurseforgeFileId)
                            ? parsedCurseforgeFileId
                            : 0;
                    var curseforgeResult = await DownloadModFromUrlAsync(
                        curseforgeUrl,
                        mod.Name ?? $"cf-{resolvedModId}-{effectiveCurseforgeFileId}",
                        modsPath,
                        ct,
                        sourcePlatform: "Curseforge",
                        sourceProjectId: resolvedModId,
                        sourceFileId: effectiveCurseforgeFileId > 0
                            ? effectiveCurseforgeFileId
                            : null);
                    return curseforgeResult.IsSuccess
                        ? CollectionModInstallResult.Success(
                            "已通过 CurseForge 下载并安装",
                            curseforgeResult.InstalledNames)
                        : CollectionModInstallResult.Failed("CurseForge 下载或安装失败，归档中可能没有有效 manifest.json");
                }
            }

            // 没有可解析的项目 ID 时，仍允许真正的直链来源继续走通用下载分支。
            if (!IsLikelyDirectDownloadUrl(source.Url))
            {
                return CollectionModInstallResult.Failed("CurseForge 来源缺少项目 ID/FileID 和可下载直链");
            }
        }

        // 旧 Collection 清单有时只保存 Nexus Mod 页面，没有 FileID。
        // 不能把该页面当成直链压缩包下载；优先尝试 API 选择最新文件，
        // API 不可用时再打开同一页面等待用户触发 NXM 回调。
        var sourceUrl = source.Url ?? string.Empty;
        if ((resolvedModId <= 0 || resolvedFileId <= 0) &&
            NexusSourceParser.TryParseModId(sourceUrl, out var pageOnlyModId))
        {
            // 旧 Collection 只保存 ModID 时也可以复用此前下载过的任意缓存；
            // 缓存名中的 FileID 会写回实例来源凭证，下一次即可精确命中。
            if (NexusDownloadCache.TryGetAnyForMod(pageOnlyModId, ModpackInstallService.IsValidModArchiveFile, out var cachedAnyPath) &&
                ModpackInstallService.InstallDownloadedModArchive(
                    cachedAnyPath,
                    modsPath,
                    mod.Name ?? "unknown",
                    out var cachedAnyInstalledNames))
            {
                var cachedFileId = TryGetNexusFileIdFromCachePath(
                    cachedAnyPath,
                    pageOnlyModId,
                    out var parsedCachedFileId)
                    ? parsedCachedFileId
                    : 0;
                ModpackInstallService.WriteSourceCredentialForInstalledMod(
                    modsPath,
                    cachedAnyInstalledNames,
                    "NexusMods",
                    pageOnlyModId,
                    cachedFileId > 0 ? cachedFileId : null,
                    fileName: Path.GetFileName(cachedAnyPath));
                return CollectionModInstallResult.Success("已复用 Nexus 缓存并安装", cachedAnyInstalledNames);
            }

            var settings = _settingsStore.Load();
            var latest = await _nexusResolver.ResolveLatestDownloadUrlAsync(
                pageOnlyModId,
                GameDomain,
                settings,
                ct);
            if (latest.IsSuccess && latest.FileId > 0)
            {
                var latestResult = await DownloadModFromUrlAsync(
                    latest.DownloadUrl,
                    mod.Name ?? "unknown",
                    modsPath,
                    ct,
                    "NexusMods",
                    pageOnlyModId,
                    latest.FileId);
                return latestResult.IsSuccess
                    ? CollectionModInstallResult.Success(
                        "已通过 Nexus 文件列表下载并安装",
                        latestResult.InstalledNames)
                    : CollectionModInstallResult.Failed("Nexus 文件列表已解析，但下载或安装失败");
            }

            var callback = await _browserFallback.WaitForNxmModCallbackAsync(
                pageOnlyModId,
                sourceUrl,
                null,
                ct);
            if (!string.IsNullOrWhiteSpace(callback) &&
                _nxmLinkParser.TryParse(callback, out var callbackInfo, out _) &&
                callbackInfo.ModId == pageOnlyModId &&
                callbackInfo.FileId > 0)
            {
                var callbackResult = await DownloadNexusModAsync(
                    pageOnlyModId,
                    callbackInfo.FileId,
                    mod.Name ?? "unknown",
                    modsPath,
                    ct,
                    callbackInfo);
                return callbackResult.IsSuccess
                    ? CollectionModInstallResult.Success(
                        "已通过 Nexus 浏览器回调下载并安装",
                        callbackResult.InstalledNames)
                    : CollectionModInstallResult.Failed("Nexus 浏览器回调已返回，但下载或安装失败");
            }

            return latest.IsSuccess
                ? CollectionModInstallResult.Failed("Nexus 文件列表未返回可用下载地址，且浏览器回调未完成")
                : CollectionModInstallResult.Failed($"Nexus 文件列表解析失败，且浏览器回调未完成: {latest.Message}");
        }

        // Nexus Collection 的 source 通常同时包含网页 URL 和 modId/fileId。
        // URL 是用于浏览器查看/授权的页面，不是可直接保存的压缩包；只要有
        // 完整 Nexus ID，就必须优先走 API/NXM/浏览器回退链路。
        if (resolvedModId > 0 && resolvedFileId > 0)
        {
            var nexusResult = await DownloadNexusModAsync(
                resolvedModId,
                resolvedFileId,
                mod.Name ?? "unknown",
                modsPath,
                ct,
                parsedNxm);
            return nexusResult.IsSuccess
                ? CollectionModInstallResult.Success(
                    "已下载并安装 Nexus Mod",
                    nexusResult.InstalledNames)
                : CollectionModInstallResult.Failed("Nexus Mod 下载或安装失败");
        }

        // 直链下载（browse/direct/manual 类型）。这里仅在没有可用 Nexus ID
        // 时执行，避免把 nexusmods.com 的详情页当成 Mod 压缩包。
        if (!string.IsNullOrWhiteSpace(source.Url) &&
            Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == "http" || uri.Scheme == "https"))
        {
            var result = await DownloadModFromUrlAsync(
                source.Url,
                mod.Name ?? "unknown",
                modsPath,
                ct,
                sourcePlatform,
                source.ModId > 0 ? source.ModId : null,
                source.FileId > 0 ? source.FileId : null);
            return result.IsSuccess
                ? CollectionModInstallResult.Success(
                    "已通过直链下载并安装",
                    result.InstalledNames)
                : CollectionModInstallResult.Failed("直链下载或安装失败，归档中可能没有有效 manifest.json");
        }

        return CollectionModInstallResult.Failed("来源缺少有效 Nexus ID 或 HTTP 下载直链");
    }

    private static string BuildManualSourceMessage(
        NexusCollectionJsonMod mod,
        NexusCollectionJsonModSource source)
    {
        var modName = string.IsNullOrWhiteSpace(mod.Name) ? "此 Mod" : mod.Name.Trim();
        var sourceHint = string.IsNullOrWhiteSpace(source.Url)
            ? "清单未提供可打开的来源地址，请补充来源信息。"
            : $"来源地址：{source.Url.Trim()}";
        return $"需要手动下载：{modName}。请在浏览器中完成下载后，将 Mod 压缩包拖入当前实例的 Mods 页面安装。{sourceHint}";
    }

    private static bool TryParseNexusPageIds(
        string? url,
        out long modId,
        out long fileId)
    {
        return NexusSourceParser.TryParsePageIds(url, out modId, out fileId);
    }

    private static string? ResolveCollectionSourcePlatform(NexusCollectionJsonModSource source)
    {
        if (!string.IsNullOrWhiteSpace(source.Type) &&
            source.Type.Contains("curse", StringComparison.OrdinalIgnoreCase))
        {
            return "Curseforge";
        }

        // 旧 Collection 导出有时只保留 CurseForge 网页/CDN 地址；
        // 若仅根据数字 ModID/FileID 判断，会把 CurseForge 项目误送到 Nexus。
        if (IsLikelyCurseforgeUrl(source.Url))
        {
            return "Curseforge";
        }

        if (source.ModId > 0 || source.FileId > 0 ||
            string.Equals(source.Type, "nexus", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(source.Type, "nxm", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(source.Type, "browse", StringComparison.OrdinalIgnoreCase))
        {
            return "NexusMods";
        }

        return null;
    }

    // 保留这个私有入口以兼容现有反射/回归测试；实际规则必须与 Modpack
    // 安装链路共用，避免 Collection 在未来又把 CurseForge 页面当作归档直链。
    private static bool IsLikelyCurseforgeUrl(string? value)
    {
        return ModpackInstallService.IsLikelyCurseforgeUrl(value);
    }

    // 保留入口名称以兼容既有调用方，规则统一由 ModpackInstallService 维护。
    private static bool IsLikelyDirectDownloadUrl(string? value)
    {
        return ModpackInstallService.IsLikelyDirectDownloadUrl(value);
    }

    private static bool TryGetNexusFileIdFromCachePath(
        string cachePath,
        long modId,
        out long fileId)
    {
        // Collection 的旧入口也会收到 WPF/Core 的 `mod_<ModID>_<FileID>.zip`
        // 缓存路径。统一委托共享解析器，避免新旧缓存命中逻辑出现分叉。
        return NexusDownloadCache.TryGetFileIdFromCachePath(cachePath, modId, out fileId);
    }

    private async Task<(bool IsSuccess, List<string> InstalledNames)> DownloadModFromUrlAsync(
        string url,
        string modName,
        string modsPath,
        CancellationToken ct,
        string? sourcePlatform,
        long? sourceProjectId,
        long? sourceFileId)
    {
        var installedNames = new List<string>();
        var safeModName = InstanceRuntimePathResolver.SanitizeFileNameComponent(modName, "unknown");

        // 下载缓存被清理后仍可依据实际 Mod 目录中的来源凭证复用安装结果，
        // 避免 Collection 重装时重新打开浏览器或请求 CDN。
        if (sourceProjectId is > 0 && sourceFileId is > 0)
        {
            var existingDirectories = ModpackInstallService.FindInstalledModDirectoriesBySource(
                modsPath,
                sourcePlatform ?? string.Empty,
                sourceProjectId.Value,
                sourceFileId.Value);
            if (existingDirectories.Count > 0)
            {
                installedNames.AddRange(existingDirectories
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Cast<string>());
                return (true, installedNames);
            }
        }

        var hasCurseforgeIdentity = string.Equals(
                sourcePlatform,
                "Curseforge",
                StringComparison.OrdinalIgnoreCase) &&
            sourceProjectId is > 0 &&
            sourceFileId is > 0;
        var curseforgeProjectId = sourceProjectId.GetValueOrDefault();
        var curseforgeFileId = sourceFileId.GetValueOrDefault();
        var fileName = hasCurseforgeIdentity
            ? $"cf-{curseforgeProjectId}-{curseforgeFileId}.zip"
            : InstanceRuntimePathResolver.SanitizeFileNameComponent(
                Path.GetFileName(new Uri(url).LocalPath), $"{safeModName}.zip");

        var zipPath = Path.Combine(modsPath, "_downloads", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);

        // CurseForge 直链也使用稳定的 project/file 缓存名。上一次下载完成但进程
        // 在整理 Mod 前退出时，当前实例归档优先，避免恢复过程中被其它任务的
        // 共享缓存干扰。
        if (hasCurseforgeIdentity &&
            ModpackInstallService.IsValidModArchiveFile(zipPath) &&
            ModpackInstallService.InstallDownloadedModArchive(
                zipPath,
                modsPath,
                safeModName,
                out installedNames))
        {
            ModpackInstallService.WriteSourceCredentialForInstalledMod(
                modsPath,
                installedNames,
                sourcePlatform,
                sourceProjectId,
                sourceFileId,
                url,
                fileName);
            return (true, installedNames);
        }

        // 当前实例缓存未命中时，再复用全局 CurseForge 缓存，避免 CDN 签名变化
        // 后重新联网下载同一个 ProjectID/FileID。
        if (hasCurseforgeIdentity &&
            CurseforgeDownloadCache.TryGet(
                curseforgeProjectId,
                curseforgeFileId,
                out var globalCachedPath,
                ModpackInstallService.IsValidModArchiveFile) &&
            ModpackInstallService.InstallDownloadedModArchive(
                globalCachedPath,
                modsPath,
                safeModName,
                out installedNames))
        {
            ModpackInstallService.WriteSourceCredentialForInstalledMod(
                modsPath,
                installedNames,
                sourcePlatform,
                sourceProjectId,
                sourceFileId,
                url,
                fileName);
            return (true, installedNames);
        }

        var keepDownloadedArchive = false;
        try
        {
            await _httpDownloadService.DownloadAsync(
                url,
                zipPath,
                null,
                ct,
                cacheValidator: ModpackInstallService.IsValidModArchiveFile);

            if (!ModpackInstallService.InstallDownloadedModArchive(
                    zipPath,
                    modsPath,
                    safeModName,
                    out installedNames))
            {
                return (false, installedNames);
            }

            if (string.Equals(sourcePlatform, "NexusMods", StringComparison.OrdinalIgnoreCase) &&
                sourceProjectId is > 0 && sourceFileId is > 0)
            {
                NexusDownloadCache.Save(
                    sourceProjectId.Value,
                    sourceFileId.Value,
                    zipPath,
                    ModpackInstallService.IsValidModArchiveFile);
            }

            if (hasCurseforgeIdentity)
            {
                CurseforgeDownloadCache.Save(
                    curseforgeProjectId,
                    curseforgeFileId,
                    zipPath,
                    ModpackInstallService.IsValidModArchiveFile);
            }

            ModpackInstallService.WriteSourceCredentialForInstalledMod(
                modsPath,
                installedNames,
                sourcePlatform,
                sourceProjectId,
                sourceFileId,
                url,
                fileName);
            keepDownloadedArchive = hasCurseforgeIdentity;
            return (true, installedNames);
        }
        finally
        {
            if (!keepDownloadedArchive)
            {
                try { File.Delete(zipPath); } catch { }
            }
        }
    }

    private async Task<(bool IsSuccess, List<string> InstalledNames)> DownloadNexusModAsync(
        long modId,
        long fileId,
        string modName,
        string modsPath,
        CancellationToken ct,
        NxmLinkInfo? parsedNxm = null)
    {
        var existingDirectories = ModpackInstallService.FindInstalledModDirectoriesBySource(
            modsPath,
            "NexusMods",
            modId,
            fileId);
        if (existingDirectories.Count > 0)
        {
            var installedNames = existingDirectories
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .ToList();
            return (true, installedNames);
        }

        // Collection 内的 Mod 也复用 Nexus 按 Mod/File ID 的缓存，避免重复打开浏览器。
        if (NexusDownloadCache.TryGet(modId, fileId, out var cachedPath, ModpackInstallService.IsValidModArchiveFile) &&
            ModpackInstallService.InstallDownloadedModArchive(cachedPath, modsPath, modName, out var cachedInstalledNames))
        {
            ModpackInstallService.WriteSourceCredentialForInstalledMod(
                modsPath, cachedInstalledNames, "NexusMods", modId, fileId, fileName: Path.GetFileName(cachedPath));
            return (true, cachedInstalledNames);
        }

        var nxmInfo = ModpackInstallService.BuildNexusDownloadInfo(modId, fileId, parsedNxm);

        var settings = _settingsStore.Load();
        var resolved = await _nexusResolver.ResolveDownloadUrlAsync(
            nxmInfo, settings.NexusApiKey, settings.NexusOAuthAccessToken, ct);

        if (!resolved.IsSuccess)
        {
            // NXM 解析失败（可能是非 Premium），尝试浏览器回退
            var browserUrl = $"https://www.nexusmods.com/{GameDomain}/mods/{modId}?tab=files&file_id={fileId}";
            var fallbackNxmLink = await _browserFallback.WaitForNxmCallbackAsync(
                modId, fileId, browserUrl, null, ct);

            if (fallbackNxmLink != null && _nxmLinkParser.TryParse(fallbackNxmLink, out var fallbackInfo, out _))
            {
                var fallbackResolved = await _nexusResolver.ResolveDownloadUrlAsync(
                    fallbackInfo, settings.NexusApiKey, settings.NexusOAuthAccessToken, ct);
                if (!fallbackResolved.IsSuccess)
                {
                    return (false, []);
                }

                return await DownloadModFromUrlAsync(
                    fallbackResolved.DownloadUrl,
                    modName,
                    modsPath,
                    ct,
                    "NexusMods",
                    modId,
                    fileId);
            }

            return (false, []);
        }

        return await DownloadModFromUrlAsync(
            resolved.DownloadUrl,
            modName,
            modsPath,
            ct,
            "NexusMods",
            modId,
            fileId);
    }

    private static bool TryExtractWith7Zip(string archivePath, string extractDir)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "7z",
                Arguments = $"x \"{archivePath}\" -o\"{extractDir}\" -y",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            var process = System.Diagnostics.Process.Start(psi);
            if (process == null) return false;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void SaveInstanceRecord(string instanceName, string runtimePath)
    {
        try
        {
            var store = new InstanceRegistryStore();
            var records = store.LoadManualInstances();
            // 实例名只在同一个路径下唯一；不同 Base 允许拥有同名 Collection。
            var existingIndex = records.FindIndex(r =>
                string.Equals(r.Path, runtimePath, StringComparison.OrdinalIgnoreCase));
            var record = new ManualInstanceRecord { Name = instanceName, Path = runtimePath };
            if (existingIndex >= 0)
            {
                records[existingIndex] = record;
            }
            else
            {
                records.Add(record);
            }
            store.SaveManualInstances(records);
        }
        catch { }
    }

    private static void CleanupVersionDirectory(string gamePath, string instanceName)
    {
        try
        {
            var versionRoot = Path.Combine(gamePath, "versions", instanceName);
            if (!Directory.Exists(versionRoot))
            {
                return;
            }

            // SMAPI 安装可能为 Content 创建 Junction；先移除连接，避免清理时误触源游戏目录。
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
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "rm",
                Arguments = OperatingSystem.IsWindows() ? $"/c rmdir \"{junctionPath}\"" : $"\"{junctionPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process != null)
            {
                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit(5000);
            }
        }
        catch { }
    }

    /// <summary>
    /// 提取 Collection 图标到 SMAPI 实例专属图标文件。
    /// Collection 安装必然创建 SMAPI 实例，因此不使用原版图标命名空间。
    /// </summary>
    private static string? ExtractCollectionIcon(
        string collectionRoot,
        string versionRoot,
        string? customIconPath,
        string? archiveExtractRoot = null)
    {
        try
        {
            var targetPath = Path.Combine(versionRoot, ".svl-instance-icon-smapi.png");

            // 部分安装重试时保留版本目录，玩家可能已经在版本设置中更换了图标；
            // 保存阶段不能用 Collection 包内的图标把这次个性化修改覆盖掉。
            // 但 SMAPI 安装流程写入的默认图标带有显式标记，必须允许包内自定义
            // Icon 替换它，否则 Collection 导入完成后始终显示 SMAPI 预设图标。
            if (File.Exists(targetPath) &&
                new FileInfo(targetPath).Length > 0 &&
                !InstanceIconResolver.IsGeneratedSmapiIcon(versionRoot))
            {
                return targetPath;
            }

            if (!string.IsNullOrWhiteSpace(customIconPath) && File.Exists(customIconPath))
            {
                File.Copy(customIconPath, targetPath, true);
                InstanceIconResolver.TryDeleteGeneratedSmapiIconMarker(versionRoot);
                return targetPath;
            }

            var candidates = new[]
            {
                "collection-icon.png", "collection-icon.jpg", "collection-icon.jpeg", "collection-icon.webp",
                "modpack-icon.png", "modpack-icon.jpg", "modpack-icon.jpeg", "modpack-icon.webp",
                "pack-icon.png", "pack-icon.jpg", "pack-icon.jpeg", "pack-icon.webp",
                "icon.png", "icon.jpg", "icon.jpeg", "icon.webp",
                "logo.png", "logo.jpg", "logo.jpeg", "logo.webp",
                "thumbnail.png", "thumbnail.jpg", "thumbnail.jpeg", "thumbnail.webp",
                "cover.png", "cover.jpg", "cover.jpeg", "cover.webp"
            };

            var roots = new[] { collectionRoot, archiveExtractRoot }
                .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                .Select(path => Path.GetFullPath(path!))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 先查 Collection 根目录，再查整个归档解压根目录的顶层，避免
            // bundled/<Mod>/icon.png 取代包根目录的 icon.png。
            foreach (var root in roots)
            {
                foreach (var name in candidates)
                {
                    var found = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
                        .FirstOrDefault(path => string.Equals(
                            Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase));
                    if (string.IsNullOrWhiteSpace(found))
                    {
                        continue;
                    }

                    File.Copy(found, targetPath, true);
                    InstanceIconResolver.TryDeleteGeneratedSmapiIconMarker(versionRoot);
                    return targetPath;
                }
            }

            // 最后兼容图标位于外层发布目录的布局。
            foreach (var root in roots)
            {
                foreach (var name in candidates)
                {
                    var found = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                        .FirstOrDefault(path => string.Equals(
                            Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase));
                    if (string.IsNullOrWhiteSpace(found))
                    {
                        continue;
                    }

                    File.Copy(found, targetPath, true);
                    InstanceIconResolver.TryDeleteGeneratedSmapiIconMarker(versionRoot);
                    return targetPath;
                }
            }
        }
        catch
        {
            // 图标是可选元数据，不应使 Collection 安装失败。
        }

        return null;
    }

    private static string? FindFileByNameIgnoreCase(string root, string fileName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return null;
            }

            return ModpackInstallService.EnumerateFilesSafe(root, fileName).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? FindDirectoryByNameIgnoreCase(string root, string directoryName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return null;
            }

            return Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                .Prepend(root)
                .FirstOrDefault(path => string.Equals(
                    Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                    directoryName,
                    StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

}

// ================================================================
// collection.json 模型（对齐旧架构 NexusCollectionJson）
// ================================================================

internal sealed class NexusCollectionJson
{
    [JsonPropertyName("info")]
    public NexusCollectionJsonInfo? Info { get; set; }

    [JsonPropertyName("mods")]
    public NexusCollectionJsonMod[]? Mods { get; set; }
}

internal sealed class NexusCollectionJsonInfo
{
    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("domainName")]
    public string? DomainName { get; set; }
}

internal sealed class NexusCollectionJsonMod
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("optional")]
    public bool Optional { get; set; }

    [JsonPropertyName("phase")]
    public int Phase { get; set; } = 1;

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("source")]
    public NexusCollectionJsonModSource? Source { get; set; }

    [JsonPropertyName("patches")]
    public Dictionary<string, string>? Patches { get; set; }
}

/// <summary>
/// 归一化 Collection Mod 的来源字段。
/// 标准 Nexus Collection 使用 mod.source；部分旧/第三方导出器会把
/// type、modId、fileId、url 直接放在 Mod 条目顶层，或同时在两层写入。
/// 统一转换后安装流程只需要读取 Source，避免整包被误报为缺少来源。
/// </summary>
internal sealed class NexusCollectionJsonModConverter : JsonConverter<NexusCollectionJsonMod>
{
    public override NexusCollectionJsonMod Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new NexusCollectionJsonMod();
        }

        var mod = new NexusCollectionJsonMod
        {
            Name = GetString(root, "name", "modName", "displayName"),
            Version = GetString(root, "version", "modVersion", "mod_version"),
            Optional = GetBoolean(root, "optional", "isOptional"),
            Phase = GetInt(root, "phase", "installPhase") ?? 1,
            Author = GetString(root, "author", "modAuthor"),
            Patches = GetPatches(root)
        };

        // 来源转换器同时读取顶层字段和嵌套 source，并负责 URL/Token 中的
        // 项目 ID、文件 ID 推断。没有任何有效来源字段时保持 null，让上层
        // 按“来源缺失”报告，而不是创建一个空来源对象。
        var source = JsonSerializer.Deserialize<NexusCollectionJsonModSource>(
            root.GetRawText(),
            options);
        if (source != null && HasSourceValues(source))
        {
            mod.Source = source;
        }

        return mod;
    }

    public override void Write(
        Utf8JsonWriter writer,
        NexusCollectionJsonMod value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (!string.IsNullOrWhiteSpace(value.Name)) writer.WriteString("name", value.Name);
        if (!string.IsNullOrWhiteSpace(value.Version)) writer.WriteString("version", value.Version);
        writer.WriteBoolean("optional", value.Optional);
        writer.WriteNumber("phase", value.Phase);
        if (!string.IsNullOrWhiteSpace(value.Author)) writer.WriteString("author", value.Author);
        if (value.Source != null)
        {
            writer.WritePropertyName("source");
            JsonSerializer.Serialize(writer, value.Source, options);
        }
        writer.WriteEndObject();
    }

    private static bool HasSourceValues(NexusCollectionJsonModSource source)
    {
        return !string.IsNullOrWhiteSpace(source.Type) ||
               source.ModId > 0 ||
               source.FileId > 0 ||
               !string.IsNullOrWhiteSpace(source.Url);
    }

    private static Dictionary<string, string>? GetPatches(JsonElement root)
    {
        if (!TryGetPropertyIgnoreCase(root, "patches", out var patches) ||
            patches.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in patches.EnumerateObject())
        {
            var value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(value))
            {
                result[property.Name] = value.Trim();
            }
        }

        return result.Count == 0 ? null : result;
    }

    private static string? GetString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyIgnoreCase(root, name, out var value) ||
                value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }

        return null;
    }

    private static bool GetBoolean(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyIgnoreCase(root, name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (value.ValueKind == JsonValueKind.String &&
                bool.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return false;
    }

    private static int? GetInt(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyIgnoreCase(root, name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String &&
                int.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement root,
        string name,
        out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}

internal sealed class NexusCollectionJsonModSource
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("modId")]
    public long ModId { get; set; }

    [JsonPropertyName("fileId")]
    public long FileId { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("fileSize")]
    public long FileSize { get; set; }

    [JsonPropertyName("logicalFilename")]
    public string? LogicalFilename { get; set; }
}

/// <summary>
/// 兼容 Nexus Collection 历史导出中 source 的多种形态：对象、NXM/网页 URL、
/// 以及 CurseForge 的 cf-project-file 标识。新旧 Collection 包都统一转换成
/// NexusCollectionJsonModSource，避免单个异常 source 让整个清单无法反序列化。
/// </summary>
internal sealed class NexusCollectionJsonModSourceConverter : JsonConverter<NexusCollectionJsonModSource>
{
    public override NexusCollectionJsonModSource Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.String)
        {
            return FromText(root.GetString());
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return new NexusCollectionJsonModSource();
        }

        var source = FromObject(root);
        if (TryGetPropertyIgnoreCase(root, "source", out var nestedSource))
        {
            var nested = nestedSource.ValueKind == JsonValueKind.String
                ? FromText(nestedSource.GetString())
                : nestedSource.ValueKind == JsonValueKind.Object
                    ? FromObject(nestedSource)
                    : new NexusCollectionJsonModSource();
            // 第三方导出器可能把标准字段写成空字符串，同时把真实值放在
            // 嵌套 source 中。不能使用 ??=：空字符串不是 null，会遮蔽内层
            // 的平台/URL，随后把可下载条目误报为“来源缺失”。
            if (string.IsNullOrWhiteSpace(source.Type)) source.Type = nested.Type;
            if (string.IsNullOrWhiteSpace(source.Url)) source.Url = nested.Url;
            if (source.ModId <= 0) source.ModId = nested.ModId;
            if (source.FileId <= 0) source.FileId = nested.FileId;
            if (source.FileSize <= 0) source.FileSize = nested.FileSize;
            if (string.IsNullOrWhiteSpace(source.LogicalFilename))
            {
                source.LogicalFilename = nested.LogicalFilename;
            }
        }

        ApplyTextInference(source, source.Type);
        ApplyTextInference(source, source.Url);
        // Collection 标准 logicalFilename 通常是唯一保存 FileID 的字段；
        // 复用下载页统一解析器，兼容“File 7448774_ ...zip”和旧下划线/空格形式。
        ApplyTextInference(source, source.LogicalFilename);
        return source;
    }

    public override void Write(
        Utf8JsonWriter writer,
        NexusCollectionJsonModSource value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (!string.IsNullOrWhiteSpace(value.Type)) writer.WriteString("type", value.Type);
        if (value.ModId > 0) writer.WriteNumber("modId", value.ModId);
        if (value.FileId > 0) writer.WriteNumber("fileId", value.FileId);
        if (!string.IsNullOrWhiteSpace(value.Url)) writer.WriteString("url", value.Url);
        if (value.FileSize > 0) writer.WriteNumber("fileSize", value.FileSize);
        if (!string.IsNullOrWhiteSpace(value.LogicalFilename)) writer.WriteString("logicalFilename", value.LogicalFilename);
        writer.WriteEndObject();
    }

    private static NexusCollectionJsonModSource FromObject(JsonElement root)
    {
        var source = new NexusCollectionJsonModSource
        {
            Type = GetString(root, "type", "platform", "site", "service", "sourceType", "provider"),
            ModId = GetLong(
                root,
                "modId",
                "mod_id",
                "projectId",
                "project_id",
                "project",
                "projectID",
                "modProjectId",
                "mod_project_id",
                "nexusModId",
                "nexus_mod_id",
                "collection"),
            FileId = GetLong(root, "fileId", "file_id", "nexusFileId", "nexus_file_id", "file"),
            Url = GetString(root, "url", "downloadUrl", "download_url", "uri", "nxmUrl", "nxm_url"),
            FileSize = GetLong(root, "fileSize", "file_size", "size"),
            LogicalFilename = GetString(root, "logicalFilename", "logical_filename", "fileName", "file_name")
        };
        ApplyTextInference(source, source.Type);
        ApplyTextInference(source, source.Url);
        return source;
    }

    private static NexusCollectionJsonModSource FromText(string? text)
    {
        var source = new NexusCollectionJsonModSource
        {
            Type = text?.Trim(),
            Url = text?.Trim()
        };
        ApplyTextInference(source, text);
        return source;
    }

    private static void ApplyTextInference(NexusCollectionJsonModSource source, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        // logicalFilename 常见为 “File 7448774_ Content Patcher.zip”；它不是
        // URL，不能交给 URI 解析，但仍包含 Nexus 的稳定 FileID。
        if (source.FileId <= 0 &&
            DownloadOptionIdentityParser.TryExtractFileId(text, out var fileIdFromText))
        {
            source.FileId = fileIdFromText;
        }

        var curseMatch = System.Text.RegularExpressions.Regex.Match(
            text,
            @"(?:^|[^a-z0-9])(?:cf|curseforge)[-_ ](\d+)[-_ ](\d+)(?:[^0-9]|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (curseMatch.Success &&
            long.TryParse(curseMatch.Groups[1].Value, out var curseProjectId) &&
            long.TryParse(curseMatch.Groups[2].Value, out var curseFileId))
        {
            source.Type = "Curseforge";
            if (source.ModId <= 0) source.ModId = curseProjectId;
            if (source.FileId <= 0) source.FileId = curseFileId;
            return;
        }

        var nxmMatch = System.Text.RegularExpressions.Regex.Match(text, @"(?i)mods/(\d+)/files/(\d+)");
        if (!nxmMatch.Success)
        {
            nxmMatch = System.Text.RegularExpressions.Regex.Match(
                text,
                @"(?i)mods/(\d+)[^#\r\n]*?[?&](?:file_id|fileId)=(\d+)");
        }
        if (nxmMatch.Success &&
            long.TryParse(nxmMatch.Groups[1].Value, out var nexusModId) &&
            long.TryParse(nxmMatch.Groups[2].Value, out var nexusFileId))
        {
            source.Type = "NexusMods";
            if (source.ModId <= 0) source.ModId = nexusModId;
            if (source.FileId <= 0) source.FileId = nexusFileId;
            return;
        }

        if (source.Type is null && text.Contains("nexus", StringComparison.OrdinalIgnoreCase))
        {
            source.Type = "NexusMods";
        }
        else if (source.Type is null &&
                 (text.Contains("curseforge", StringComparison.OrdinalIgnoreCase) ||
                  text.Contains("forgecdn", StringComparison.OrdinalIgnoreCase)))
        {
            source.Type = "Curseforge";
        }
    }

    private static string? GetString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetPropertyIgnoreCase(root, name, out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
            }
        }

        return null;
    }

    private static long GetLong(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyIgnoreCase(root, name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var textNumber)) return textNumber;
        }

        return 0;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
