using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using SVL.Core.Platform.Abstractions;

namespace SVL.Avalonia.Services;

/// <summary>Collection 安装进度回调。</summary>
public sealed class CollectionInstallProgress
{
    public int Percent { get; set; }
    public string StepText { get; set; } = string.Empty;
    public string SubProgressText { get; set; } = string.Empty;
    public int SubProgress { get; set; } = -1;
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
    public List<string> InstalledMods { get; init; } = [];

    public static CollectionInstallResult Success(string runtimePath, string versionRootPath, List<string> installedMods, List<string>? failedMods = null) => new()
    {
        IsSuccess = true,
        Message = "Collection 安装完成",
        RuntimePath = runtimePath,
        VersionRootPath = versionRootPath,
        InstalledMods = installedMods,
        FailedMods = failedMods ?? []
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

/// <summary>
/// Nexus Collection 安装服务。承载 Collection 7z 下载 → collection.json 解析 → SMAPI 优先 → Phase 分阶段下载安装 Mod。
/// 对齐旧架构 NexusCollectionWizardTask，但不包含浏览器交互（复用 BrowserDownloadFallbackService）。
/// </summary>
public sealed class CollectionInstallService
{
    private const long SmapiModId = 2400;
    private const string GameDomain = "stardewvalley";

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
    public async Task<CollectionInstallResult> InstallCollectionAsync(
        string collectionSlug,
        int revision,
        string instanceName,
        Action<CollectionInstallProgress>? onProgress,
        CancellationToken cancellationToken = default,
        string? gameBasePath = null)
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

        var gamePath = !string.IsNullOrWhiteSpace(gameBasePath) ? gameBasePath : ResolveGamePath();
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            return CollectionInstallResult.Failed("未检测到游戏目录，无法安装 Collection");
        }

        var settings = _settingsStore.Load();
        if (string.IsNullOrWhiteSpace(settings.NexusApiKey) && string.IsNullOrWhiteSpace(settings.NexusOAuthAccessToken))
        {
            return CollectionInstallResult.Failed("Nexus 未登录，无法下载 Collection");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "SVL", "collections", Guid.NewGuid().ToString());

        try
        {
            // ===== 阶段 1: 下载 Collection 7z =====
            onProgress?.Invoke(new CollectionInstallProgress { Percent = 2, StepText = "下载 Collection 数据" });
            cancellationToken.ThrowIfCancellationRequested();

            Directory.CreateDirectory(tempDir);

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

            await _httpDownloadService.DownloadAsync(resolveResult.DownloadUrl, archivePath, null, cancellationToken);

            onProgress?.Invoke(new CollectionInstallProgress { Percent = 5, StepText = "Collection 下载完成" });

            return await InstallFromArchiveCoreAsync(archivePath, instanceName, gamePath, tempDir, onProgress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            CleanupVersionDirectory(gamePath, instanceName);
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

    /// <summary>
    /// 从本地 Collection 7z 文件安装（拖拽场景）。跳过下载阶段，从解析 collection.json 开始。
    /// 对齐旧架构 NexusCollectionWizardTask 构造函数 A（直接提供已下载文件路径）。
    /// </summary>
    /// <param name="archivePath">本地 Collection 7z/zip 文件路径。</param>
    /// <param name="instanceName">版本隔离实例名。</param>
    /// <param name="onProgress">进度回调。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<CollectionInstallResult> InstallCollectionFromArchiveAsync(
        string archivePath,
        string instanceName,
        Action<CollectionInstallProgress>? onProgress,
        CancellationToken cancellationToken = default,
        string? gameBasePath = null)
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

        var gamePath = !string.IsNullOrWhiteSpace(gameBasePath) ? gameBasePath : ResolveGamePath();
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
            return await InstallFromArchiveCoreAsync(tempArchivePath, instanceName, gamePath, tempDir, onProgress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            CleanupVersionDirectory(gamePath, instanceName);
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
        CancellationToken cancellationToken)
    {
        var installedMods = new List<string>();
        var failedMods = new List<string>();

        try
        {
            // ===== 阶段 2: 解压并解析 collection.json =====
            onProgress?.Invoke(new CollectionInstallProgress { Percent = 7, StepText = "解析 collection.json" });
            cancellationToken.ThrowIfCancellationRequested();

            var extractDir = Path.Combine(tempDir, "extracted");
            Directory.CreateDirectory(extractDir);

            // 尝试解压（7z 需要 7z 工具，zip 可直接解压；先尝试 zip）
            try
            {
                ZipExtractor.ExtractToDirectory(archivePath, extractDir);
            }
            catch
            {
                // 非 zip 格式（可能是 7z），回退到把文件当作 collection.json 直接处理
                // 如果是 7z 且系统有 7z 命令，尝试调用
                if (!TryExtractWith7Zip(archivePath, extractDir))
                {
                    return CollectionInstallResult.Failed("无法解压 Collection 压缩包（仅支持 zip 格式，7z 需系统安装 7-Zip）");
                }
            }

            // 查找 collection.json
            var collectionJsonPath = Path.Combine(extractDir, "collection.json");
            if (!File.Exists(collectionJsonPath))
            {
                collectionJsonPath = Directory.GetFiles(extractDir, "collection.json", SearchOption.AllDirectories).FirstOrDefault()
                    ?? string.Empty;
            }

            if (!File.Exists(collectionJsonPath))
            {
                return CollectionInstallResult.Failed("Collection 压缩包中未找到 collection.json");
            }

            var collection = JsonSerializer.Deserialize<NexusCollectionJson>(
                await File.ReadAllTextAsync(collectionJsonPath, cancellationToken),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

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
            var smapiZipPath = await ResolveSmapiZipAsync(smapiMod?.Version, onProgress, cancellationToken);
            if (string.IsNullOrWhiteSpace(smapiZipPath))
            {
                return CollectionInstallResult.Failed("无法获取经过校验的 SMAPI 安装包，已停止 Collection 安装");
            }

            var smapiResult = await _smapiInstallService.InstallFromZipAsync(
                smapiZipPath, gamePath, instanceName, cancellationToken: cancellationToken);
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

            foreach (var phaseGroup in modGroups)
            {
                foreach (var mod in phaseGroup.OrderBy(m => m.Name ?? string.Empty))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var modName = mod.Name ?? $"mod-{mod.Source?.ModId}";

                    try
                    {
                        var success = await DownloadAndInstallCollectionModAsync(mod, modsPath, cancellationToken);
                        if (success)
                        {
                            installedMods.Add(modName);
                        }
                        else
                        {
                            failedMods.Add(modName);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        failedMods.Add(modName);
                        onProgress?.Invoke(new CollectionInstallProgress
                        {
                            Percent = 17 + (int)(60.0 * completedMods / Math.Max(1, totalToDownload)),
                            StepText = "下载安装 Mod",
                            SubProgressText = $"跳过 {modName}: {ex.Message}"
                        });
                    }

                    completedMods++;
                    onProgress?.Invoke(new CollectionInstallProgress
                    {
                        Percent = 17 + (int)(60.0 * completedMods / Math.Max(1, totalToDownload)),
                        StepText = $"阶段 {phaseGroup.Key}: 下载安装 Mod",
                        SubProgressText = $"{completedMods}/{totalToDownload} 已处理"
                    });
                }
            }

            onProgress?.Invoke(new CollectionInstallProgress
            {
                Percent = 78,
                StepText = $"Mod 下载安装完成（{failedMods.Count} 个失败）",
                SubProgress = -1
            });

            // ===== 阶段 5: 应用 bundled 文件 =====
            onProgress?.Invoke(new CollectionInstallProgress { Percent = 80, StepText = "应用 bundled 文件" });
            cancellationToken.ThrowIfCancellationRequested();

            var bundledDir = Path.Combine(extractDir, "bundled");
            if (Directory.Exists(bundledDir))
            {
                foreach (var dir in Directory.GetDirectories(bundledDir))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var modName = Path.GetFileName(dir);
                    var targetDir = Path.Combine(modsPath, modName);
                    if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
                    CopyDirectory(dir, targetDir);
                    installedMods.Add(modName);
                }
            }

            onProgress?.Invoke(new CollectionInstallProgress { Percent = 85, StepText = "bundled 文件应用完成" });

            // ===== 阶段 6: 保存实例配置 =====
            onProgress?.Invoke(new CollectionInstallProgress { Percent = 87, StepText = "保存实例配置" });
            cancellationToken.ThrowIfCancellationRequested();

            SaveInstanceRecord(instanceName, runtimePath);

            onProgress?.Invoke(new CollectionInstallProgress { Percent = 100, StepText = "Collection 安装完成" });

            return CollectionInstallResult.Success(runtimePath, versionRoot, installedMods, failedMods);
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

    private string ResolveGamePath()
    {
        return _gameInstallPathLocator.TryLocateSteamStardewPath()
            ?? _gameInstallPathLocator.TryLocateGogStardewPath()
            ?? string.Empty;
    }

    private static bool IsSmapiName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (!name.StartsWith("SMAPI", StringComparison.OrdinalIgnoreCase)) return false;
        var exclude = new[] { "Component", "Dependency", "Extension", "Addon", "Patch" };
        return !exclude.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string?> ResolveSmapiZipAsync(string? preferredVersion, Action<CollectionInstallProgress>? onProgress, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SVL", "smapi");
        Directory.CreateDirectory(tempDir);

        // 缓存命中（需通过 SMAPI 包签名校验，防止缓存污染导致 install.dat 缺失）
        if (!string.IsNullOrWhiteSpace(preferredVersion))
        {
            var normalizedPreferredVersion = NormalizeSmapiVersionForFileName(preferredVersion);
            var cachedPath = Path.Combine(tempDir, $"SMAPI-{normalizedPreferredVersion}.zip");
            if (IsSmapiInstallerZip(cachedPath))
            {
                return cachedPath;
            }

            if (File.Exists(cachedPath))
            {
                try { File.Delete(cachedPath); } catch { }
            }
        }

        // 从 RemoteCatalog 获取 SMAPI 版本列表
        var smapiVersions = await _remoteCatalogService.GetSmapiVersionEntriesAsync(perPage: 10);
        if (smapiVersions == null || smapiVersions.Count == 0)
        {
            onProgress?.Invoke(new CollectionInstallProgress { StepText = "警告: 无法获取 SMAPI 版本列表" });
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
              ?? smapiVersions[0];

        var version = NormalizeSmapiVersionForFileName(smapiEntry.Version);
        var zipPath = Path.Combine(tempDir, $"SMAPI-{version}.zip");

        if (IsSmapiInstallerZip(zipPath))
        {
            return zipPath;
        }

        if (File.Exists(zipPath))
        {
            try { File.Delete(zipPath); } catch { }
        }

        if (string.IsNullOrWhiteSpace(smapiEntry.DownloadUrl))
        {
            onProgress?.Invoke(new CollectionInstallProgress { StepText = "警告: SMAPI 下载地址为空" });
            return null;
        }

        await _httpDownloadService.DownloadAsync(smapiEntry.DownloadUrl, zipPath, null, ct);

        // 下载后校验是否为 SMAPI 官方安装包
        if (!IsSmapiInstallerZip(zipPath))
        {
            // GitHub 发布的 SMAPI 安装包是 double-zipped，尝试解压外层
            TryUnwrapDoubleZipped(zipPath);
        }

        // 再次校验（可能在 double-zipped 解包后通过）
        if (!IsSmapiInstallerZip(zipPath))
        {
            try { File.Delete(zipPath); } catch { }
            onProgress?.Invoke(new CollectionInstallProgress { StepText = $"警告: SMAPI 压缩包校验失败（非 SMAPI 官方包）: {smapiEntry.DownloadUrl}" });
            return null;
        }

        return zipPath;
    }

    /// <summary>
    /// 尝试解包双重压缩的 zip（GitHub 发布的 SMAPI 安装包名含 double-zipped）。
    /// 如果 zip 内只包含 zip 文件，解压外层得到内层 zip，用内层 zip 替换原文件。
    /// </summary>
    private static void TryUnwrapDoubleZipped(string zipPath)
    {
        var tempPath = zipPath + ".inner.zip";
        try
        {
            using (var stream = File.OpenRead(zipPath))
            using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read))
            {
                var zipEntries = zip.Entries
                    .Where(e => e.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var fileEntries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
                if (zipEntries.Count != 1 || fileEntries.Count != 1)
                {
                    return;
                }

                using var entryStream = zipEntries[0].Open();
                using var output = File.Create(tempPath);
                entryStream.CopyTo(output);
            }

            File.Copy(tempPath, zipPath, true);
        }
        catch
        {
            // best-effort
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
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

    /// <summary>校验 zip 是否为 SMAPI 官方安装包（含 install.dat）。</summary>
    private static bool IsSmapiInstallerZip(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length <= 1024)
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

    /// <summary>下载并安装单个 Collection Mod。</summary>
    private async Task<bool> DownloadAndInstallCollectionModAsync(NexusCollectionJsonMod mod, string modsPath, CancellationToken ct)
    {
        var source = mod.Source;
        if (source == null) return false;

        // 直链下载（browse/direct/manual 类型）
        if (!string.IsNullOrWhiteSpace(source.Url) &&
            Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == "http" || uri.Scheme == "https"))
        {
            return await DownloadModFromUrlAsync(source.Url, mod.Name ?? "unknown", modsPath, ct);
        }

        // Nexus NXM 下载（nexus 类型）
        if (source.ModId > 0 && source.FileId > 0)
        {
            return await DownloadNexusModAsync(source.ModId, source.FileId, mod.Name ?? "unknown", modsPath, ct);
        }

        return false;
    }

    private async Task<bool> DownloadModFromUrlAsync(string url, string modName, string modsPath, CancellationToken ct)
    {
        var safeModName = InstanceRuntimePathResolver.SanitizeFileNameComponent(modName, "unknown");
        var fileName = InstanceRuntimePathResolver.SanitizeFileNameComponent(
            Path.GetFileName(new Uri(url).LocalPath), $"{safeModName}.zip");

        var zipPath = Path.Combine(modsPath, "_downloads", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);

        await _httpDownloadService.DownloadAsync(url, zipPath, null, ct);

        var modDir = Path.Combine(modsPath, safeModName);
        if (Directory.Exists(modDir)) Directory.Delete(modDir, true);
        Directory.CreateDirectory(modDir);

        try
        {
            ZipExtractor.ExtractToDirectory(zipPath, modDir);
        }
        catch
        {
            var destFile = Path.Combine(modDir, fileName);
            File.Copy(zipPath, destFile, true);
        }

        try { File.Delete(zipPath); } catch { }
        return true;
    }

    private async Task<bool> DownloadNexusModAsync(long modId, long fileId, string modName, string modsPath, CancellationToken ct)
    {
        var nxmLink = $"nxm://{GameDomain}/mods/{modId}/files/{fileId}";
        if (!_nxmLinkParser.TryParse(nxmLink, out var nxmInfo, out _))
        {
            return false;
        }

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
                    return false;
                }

                return await DownloadModFromUrlAsync(fallbackResolved.DownloadUrl, modName, modsPath, ct);
            }

            return false;
        }

        return await DownloadModFromUrlAsync(resolved.DownloadUrl, modName, modsPath, ct);
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
            records.RemoveAll(r => string.Equals(r.Name, instanceName, StringComparison.OrdinalIgnoreCase));
            records.Add(new ManualInstanceRecord { Name = instanceName, Path = runtimePath });
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
