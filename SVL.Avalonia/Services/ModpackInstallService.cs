using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using SVL.Core.Platform.Abstractions;

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
    private readonly IGameInstallPathLocator _gameInstallPathLocator;
    private readonly ISmapiInstallService _smapiInstallService;
    private readonly HttpDownloadService _httpDownloadService;
    private readonly RemoteCatalogService _remoteCatalogService;
    private readonly AppUserSettingsStore _settingsStore;
    private readonly NexusModDownloadResolverService _nexusResolver;
    private readonly INxmLinkParser _nxmLinkParser;

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
        INxmLinkParser nxmLinkParser)
    {
        _gameInstallPathLocator = gameInstallPathLocator;
        _smapiInstallService = smapiInstallService;
        _httpDownloadService = httpDownloadService;
        _remoteCatalogService = remoteCatalogService;
        _settingsStore = settingsStore;
        _nexusResolver = nexusResolver;
        _nxmLinkParser = nxmLinkParser;
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
    public async Task<ModpackInstallResult> InstallSvlModpackAsync(
        string zipPath,
        string instanceName,
        string targetGamePath,
        Action<ModpackInstallProgress>? onProgress,
        CancellationToken cancellationToken = default)
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
            ZipExtractor.ExtractToDirectory(zipPath, tempDir);

            // 处理嵌套 modpack.zip 结构
            var nestedZip = Path.Combine(tempDir, "modpack.zip");
            if (File.Exists(nestedZip))
            {
                var innerDir = Path.Combine(tempDir, "_inner");
                Directory.CreateDirectory(innerDir);
                ZipExtractor.ExtractToDirectory(nestedZip, innerDir);
                // 内层解压后使用内层目录作为工作目录
                foreach (var entry in Directory.GetFileSystemEntries(innerDir))
                {
                    var dest = Path.Combine(tempDir, Path.GetFileName(entry));
                    if (!Directory.Exists(dest) && !File.Exists(dest))
                    {
                        if (Directory.Exists(entry)) Directory.Move(entry, dest);
                        else File.Move(entry, dest);
                    }
                }
            }

            var modpackJsonPath = Path.Combine(tempDir, "modpack.json");
            if (!File.Exists(modpackJsonPath))
            {
                // 可能在子目录
                modpackJsonPath = Directory.GetFiles(tempDir, "modpack.json", SearchOption.AllDirectories).FirstOrDefault()
                    ?? string.Empty;
            }

            string? smapiVersion = null;
            string? modpackName = null;
            if (File.Exists(modpackJsonPath))
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(modpackJsonPath, cancellationToken));
                if (doc.RootElement.TryGetProperty("smapi_version", out var sv)) smapiVersion = sv.GetString();
                if (doc.RootElement.TryGetProperty("name", out var nm)) modpackName = nm.GetString();
            }

            // 读取 sources.json（步 4 用）
            List<JsonElement> sourcesList = [];
            var sourcesJsonPath = Path.Combine(tempDir, "sources.json");
            if (File.Exists(sourcesJsonPath))
            {
                var sourcesJson = await File.ReadAllTextAsync(sourcesJsonPath, cancellationToken);
                if (JsonSerializer.Deserialize<JsonElement>(sourcesJson).ValueKind == JsonValueKind.Array)
                {
                    sourcesList = JsonSerializer.Deserialize<JsonElement>(sourcesJson).EnumerateArray().ToList();
                }
            }

            onProgress?.Invoke(new ModpackInstallProgress { Percent = 5, StepText = "步骤 1/6: 清单读取完成" });

            // ===== 步 2: 安装 SMAPI =====
            onProgress?.Invoke(new ModpackInstallProgress { Percent = 8, StepText = "步骤 2/6: 安装 SMAPI" });
            cancellationToken.ThrowIfCancellationRequested();

            // 整合包中的 SMAPI 相关目录只是安装包/附带 Mod，不能替代运行时安装。
            // 始终通过经过校验的 SMAPI 安装包创建可启动的隔离实例。
            var smapiZipPath = await ResolveSmapiZipAsync(smapiVersion, onProgress, cancellationToken);
            if (string.IsNullOrWhiteSpace(smapiZipPath))
            {
                return ModpackInstallResult.Failed("无法获取经过校验的 SMAPI 安装包，已停止整合包安装");
            }

            var smapiResult = await _smapiInstallService.InstallFromZipAsync(
                smapiZipPath, gamePath, instanceName, cancellationToken: cancellationToken);
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
            var modsSourceDir = Path.Combine(tempDir, "mods");
            if (Directory.Exists(modsSourceDir))
            {
                foreach (var modDir in Directory.GetDirectories(modsSourceDir))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var modName = Path.GetFileName(modDir);
                    if (SmapiRelatedDirs.Contains(modName)) continue;

                    var targetModDir = Path.Combine(modsPath, modName);
                    if (Directory.Exists(targetModDir)) Directory.Delete(targetModDir, true);
                    CopyDirectory(modDir, targetModDir);
                    bundledModNames.Add(modName);
                    installedMods.Add(modName);
                }

                // 兼容旧格式：mods/ 无子目录时直接解压根目录的 mod 文件
                var rootModFiles = Directory.GetFiles(modsSourceDir, "manifest.json", SearchOption.TopDirectoryOnly);
                if (rootModFiles.Length > 0 && Directory.GetDirectories(modsSourceDir).Length == 0)
                {
                    foreach (var manifestFile in rootModFiles)
                    {
                        var modDir = Path.GetDirectoryName(manifestFile)!;
                        var modName = Path.GetFileName(modDir);
                        var targetModDir = Path.Combine(modsPath, modName);
                        CopyDirectory(modDir, targetModDir);
                        bundledModNames.Add(modName);
                        installedMods.Add(modName);
                    }
                }
            }

            onProgress?.Invoke(new ModpackInstallProgress { Percent = 45, StepText = $"步骤 3/6: 已解压 {bundledModNames.Count} 个 Mod" });

            // ===== 步 4: 下载 sources.json 中的未打包 Mod =====
            onProgress?.Invoke(new ModpackInstallProgress { Percent = 47, StepText = "步骤 4/6: 下载未打包 Mod" });
            cancellationToken.ThrowIfCancellationRequested();

            var downloableMods = sourcesList
                .Where(s => s.TryGetProperty("name", out _) &&
                           (!s.TryGetProperty("bundled", out var bundled) || !bundled.GetBoolean()))
                .ToList();

            var totalToDownload = downloableMods.Count;
            var downloadedCount = 0;

            foreach (var source in downloableMods)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var modName = source.GetProperty("name").GetString() ?? "unknown";

                try
                {
                    var success = await DownloadModFromSourceAsync(source, modsPath, cancellationToken);
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
                    onProgress?.Invoke(new ModpackInstallProgress
                    {
                        Percent = 47 + (int)(18.0 * downloadedCount / Math.Max(1, totalToDownload)),
                        StepText = "步骤 4/6: 下载未打包 Mod",
                        SubProgressText = $"跳过 {modName}: {ex.Message}"
                    });
                }

                downloadedCount++;
                onProgress?.Invoke(new ModpackInstallProgress
                {
                    Percent = 47 + (int)(18.0 * downloadedCount / Math.Max(1, totalToDownload)),
                    StepText = "步骤 4/6: 下载未打包 Mod",
                    SubProgressText = $"{downloadedCount}/{totalToDownload} 已处理"
                });
            }

            onProgress?.Invoke(new ModpackInstallProgress { Percent = 65, StepText = $"步骤 4/6: 下载完成（{failedMods.Count} 个失败）", SubProgress = -1 });

            // ===== 步 5: 应用 settings/ =====
            onProgress?.Invoke(new ModpackInstallProgress { Percent = 67, StepText = "步骤 5/6: 应用配置覆盖" });
            cancellationToken.ThrowIfCancellationRequested();

            var settingsDir = Path.Combine(tempDir, "settings");
            if (Directory.Exists(settingsDir))
            {
                foreach (var file in Directory.GetFiles(settingsDir, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(settingsDir, file);
                    // settings/ 下的文件覆盖到 runtimePath 对应位置（如 Mods/xxx/config.json）
                    var target = Path.Combine(runtimePath, relative);
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

            // 提取整合包图标
            var iconPath = ExtractPackIcon(tempDir, versionRoot);

            // 保存实例到 InstanceRegistryStore
            SaveInstanceRecord(instanceName, runtimePath);

            onProgress?.Invoke(new ModpackInstallProgress { Percent = 100, StepText = "步骤 6/6: 安装完成" });

            return ModpackInstallResult.Success(runtimePath, versionRoot, installedMods, failedMods);
        }
        catch (OperationCanceledException)
        {
            CleanupVersionDirectory(gamePath, instanceName);
            return ModpackInstallResult.Cancelled("整合包安装已取消");
        }
        catch (Exception ex)
        {
            CleanupVersionDirectory(gamePath, instanceName);
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
    public async Task<ModpackInstallResult> InstallCurseforgeModpackAsync(
        string zipPath,
        string instanceName,
        string targetGamePath,
        Action<ModpackInstallProgress>? onProgress,
        CancellationToken cancellationToken = default)
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
            ZipExtractor.ExtractToDirectory(zipPath, tempDir);

            var manifestPath = Path.Combine(tempDir, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                manifestPath = Directory.GetFiles(tempDir, "manifest.json", SearchOption.AllDirectories).FirstOrDefault()
                    ?? string.Empty;
            }

            if (!File.Exists(manifestPath))
            {
                return ModpackInstallResult.Failed("整合包缺少 manifest.json，无法识别为 Curseforge 格式");
            }

            var manifest = JsonSerializer.Deserialize<CurseforgeManifest>(
                await File.ReadAllTextAsync(manifestPath, cancellationToken),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (manifest == null || manifest.Files.Count == 0)
            {
                return ModpackInstallResult.Failed("manifest.json 解析失败或文件列表为空");
            }

            onProgress?.Invoke(new ModpackInstallProgress { Percent = 5, StepText = $"manifest 解析完成: {manifest.Files.Count} 个文件" });

            // ===== 阶段 2: 安装 SMAPI =====
            onProgress?.Invoke(new ModpackInstallProgress { Percent = 8, StepText = "安装 SMAPI" });
            cancellationToken.ThrowIfCancellationRequested();

            var smapiZipPath = await ResolveSmapiZipAsync(null, onProgress, cancellationToken);
            if (string.IsNullOrWhiteSpace(smapiZipPath))
            {
                return ModpackInstallResult.Failed("无法获取经过校验的 SMAPI 安装包，已停止整合包安装");
            }

            var smapiResult = await _smapiInstallService.InstallFromZipAsync(
                smapiZipPath, gamePath, instanceName, cancellationToken: cancellationToken);
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

            var totalMods = manifest.Files.Count;
            var completedMods = 0;
            using var semaphore = new SemaphoreSlim(3, 3); // 并发 3，对齐旧架构

            var tasks = manifest.Files.Select(async file =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var success = await DownloadCurseforgeModAsync(
                        file.ProjectId, file.FileId, modsPath, cancellationToken);

                    var done = Interlocked.Increment(ref completedMods);
                    onProgress?.Invoke(new ModpackInstallProgress
                    {
                        Percent = 15 + (int)(60.0 * done / totalMods),
                        StepText = "下载 Mod 文件",
                        SubProgressText = $"{done}/{totalMods} 已完成"
                    });

                    if (success)
                    {
                        lock (installedMods)
                        {
                            installedMods.Add($"cf-{file.ProjectId}-{file.FileId}");
                        }
                    }
                    else
                    {
                        lock (failedMods)
                        {
                            failedMods.Add($"cf-{file.ProjectId}-{file.FileId}");
                        }
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks);

            onProgress?.Invoke(new ModpackInstallProgress
            {
                Percent = 75,
                StepText = $"Mod 下载完成（{failedMods.Count} 个失败）",
                SubProgress = -1
            });

            // ===== 阶段 4: 处理 overrides =====
            onProgress?.Invoke(new ModpackInstallProgress { Percent = 77, StepText = "应用 overrides" });
            cancellationToken.ThrowIfCancellationRequested();

            var overridesDir = Path.Combine(tempDir, manifest.Overrides ?? "overrides");
            if (Directory.Exists(overridesDir))
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

            // ===== 阶段 5: 保存实例配置 =====
            onProgress?.Invoke(new ModpackInstallProgress { Percent = 87, StepText = "保存实例配置" });
            cancellationToken.ThrowIfCancellationRequested();

            var iconPath = ExtractPackIcon(tempDir, versionRoot);
            SaveInstanceRecord(instanceName, runtimePath);

            onProgress?.Invoke(new ModpackInstallProgress { Percent = 100, StepText = "安装完成" });

            return ModpackInstallResult.Success(runtimePath, versionRoot, installedMods, failedMods);
        }
        catch (OperationCanceledException)
        {
            CleanupVersionDirectory(gamePath, instanceName);
            return ModpackInstallResult.Cancelled("Curseforge 整合包安装已取消");
        }
        catch (Exception ex)
        {
            CleanupVersionDirectory(gamePath, instanceName);
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

    private string ResolveGamePath()
    {
        return _gameInstallPathLocator.TryLocateSteamStardewPath()
            ?? _gameInstallPathLocator.TryLocateGogStardewPath()
            ?? string.Empty;
    }

    /// <summary>解析安装目标游戏路径。优先使用用户从路径列表选择的路径，否则回退到自动探测。</summary>
    private string ResolveTargetGamePath(string targetGamePath)
    {
        if (!string.IsNullOrWhiteSpace(targetGamePath) && Directory.Exists(targetGamePath))
        {
            return targetGamePath;
        }

        return ResolveGamePath();
    }

    /// <summary>获取 SMAPI zip 路径。优先从缓存命中，否则从 RemoteCatalog 获取最新版下载 URL。</summary>
    private async Task<string?> ResolveSmapiZipAsync(string? preferredVersion, Action<ModpackInstallProgress>? onProgress, CancellationToken ct)
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

            // 缓存文件不是 SMAPI 包则删除
            if (File.Exists(cachedPath))
            {
                try { File.Delete(cachedPath); } catch { }
            }
        }

        // 从 RemoteCatalog 获取 SMAPI 版本列表
        var smapiVersions = await _remoteCatalogService.GetSmapiVersionEntriesAsync(perPage: 10);
        if (smapiVersions == null || smapiVersions.Count == 0)
        {
            onProgress?.Invoke(new ModpackInstallProgress { StepText = "警告: 无法获取 SMAPI 版本列表，跳过 SMAPI 安装" });
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

        await _httpDownloadService.DownloadAsync(smapiEntry.DownloadUrl, zipPath, null, ct);

        // 下载后校验是否为 SMAPI 官方安装包（含 install.dat），避免传入非 SMAPI 包导致安装失败
        if (!IsSmapiInstallerZip(zipPath))
        {
            // GitHub 发布的 SMAPI 安装包是 double-zipped（外层 zip 内只有内层 zip），
            // 尝试解压外层得到真正的 SMAPI 安装包
            TryUnwrapDoubleZipped(zipPath);
        }

        // 再次校验（可能在 double-zipped 解包后通过）
        if (!IsSmapiInstallerZip(zipPath))
        {
            try { File.Delete(zipPath); } catch { }
            throw new InvalidDataException($"SMAPI 压缩包下载后校验失败（可能下载不完整、地址失效或非 SMAPI 官方包）: {smapiEntry.DownloadUrl}");
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
        if (!File.Exists(path) || new FileInfo(path).Length <= 1024)
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

    /// <summary>从 sources.json 的单个条目下载 Mod。支持直链和 Nexus NXM 链接。</summary>
    private async Task<bool> DownloadModFromSourceAsync(JsonElement source, string modsPath, CancellationToken ct)
    {
        if (!source.TryGetProperty("source", out var sourceProp) || sourceProp.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var downloadUrl = sourceProp.TryGetProperty("downloadUrl", out var urlProp) ? urlProp.GetString() : null;
        var modName = source.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "unknown" : "unknown";
        var safeModName = InstanceRuntimePathResolver.SanitizeFileNameComponent(modName, "unknown");

        // 直链下载
        if (!string.IsNullOrWhiteSpace(downloadUrl) &&
            Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) &&
            (uri.Scheme == "http" || uri.Scheme == "https"))
        {
            var fileName = InstanceRuntimePathResolver.SanitizeFileNameComponent(
                Path.GetFileName(uri.LocalPath), $"{safeModName}.zip");
            var zipPath = Path.Combine(modsPath, "_downloads", fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);

            await _httpDownloadService.DownloadAsync(downloadUrl, zipPath, null, ct);

            // 解压到 mod 目录
            var modDir = Path.Combine(modsPath, safeModName);
            if (Directory.Exists(modDir)) Directory.Delete(modDir, true);
            Directory.CreateDirectory(modDir);
            ZipExtractor.ExtractToDirectory(zipPath, modDir);
            try { File.Delete(zipPath); } catch { }
            return true;
        }

        // Nexus NXM 链接
        var modId = sourceProp.TryGetProperty("modId", out var modIdProp) ? modIdProp.GetString() : null;
        var fileId = sourceProp.TryGetProperty("fileId", out var fileIdProp) ? fileIdProp.GetString() : null;

        if (!string.IsNullOrWhiteSpace(modId) && !string.IsNullOrWhiteSpace(fileId) &&
            long.TryParse(modId, out var nxmModId) && long.TryParse(fileId, out var nxmFileId))
        {
            var nxmLink = $"nxm://stardewvalley/mods/{nxmModId}/files/{nxmFileId}";
            if (_nxmLinkParser.TryParse(nxmLink, out var nxmInfo, out _))
            {
                var settings = _settingsStore.Load();
                var resolved = await _nexusResolver.ResolveDownloadUrlAsync(
                    nxmInfo, settings.NexusApiKey, settings.NexusOAuthAccessToken, ct);

                if (resolved.IsSuccess)
                {
                    var fileName = InstanceRuntimePathResolver.SanitizeFileNameComponent(
                        resolved.FileName, $"{safeModName}.zip");
                    var zipPath = Path.Combine(modsPath, "_downloads", fileName);
                    Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);

                    await _httpDownloadService.DownloadAsync(resolved.DownloadUrl, zipPath, null, ct);

                    var modDir = Path.Combine(modsPath, safeModName);
                    if (Directory.Exists(modDir)) Directory.Delete(modDir, true);
                    Directory.CreateDirectory(modDir);
                    ZipExtractor.ExtractToDirectory(zipPath, modDir);
                    try { File.Delete(zipPath); } catch { }
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 下载单个 Curseforge mod 文件。
    /// 下载后解压到 mod 目录，校验解压结果包含 manifest.json（SMAPI mod 标准）或至少有内容。
    /// 解压失败或结果为空时返回 false，避免"下载成功但 Mod 未安装"的误报。
    /// </summary>
    private async Task<bool> DownloadCurseforgeModAsync(long projectId, long fileId, string modsPath, CancellationToken ct)
    {
        var downloadUrl = await _remoteCatalogService.ResolveCurseforgeFileDownloadUrlAsync(projectId, fileId, "", ct);
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            return false;
        }

        var fileName = $"cf-{projectId}-{fileId}.zip";
        var zipPath = Path.Combine(modsPath, "_downloads", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);

        await _httpDownloadService.DownloadAsync(downloadUrl, zipPath, null, ct);

        // 校验下载文件是有效 zip（防止 CDN 返回 HTML 错误页等非 zip 内容导致解压异常）
        if (!IsValidZipFile(zipPath))
        {
            try { File.Delete(zipPath); } catch { }
            return false;
        }

        var modDir = Path.Combine(modsPath, $"cf-{projectId}-{fileId}");
        if (Directory.Exists(modDir)) Directory.Delete(modDir, true);
        Directory.CreateDirectory(modDir);

        try
        {
            ZipExtractor.ExtractToDirectory(zipPath, modDir);
        }
        catch
        {
            // 解压失败：清理空目录和下载文件，返回 false
            try { Directory.Delete(modDir, true); } catch { }
            try { File.Delete(zipPath); } catch { }
            return false;
        }

        try { File.Delete(zipPath); } catch { }

        // 校验解压结果：mod 目录必须非空，且包含 manifest.json（SMAPI mod 标准清单）
        // 部分内容 mod 可能无 manifest.json，但至少应有文件存在
        if (!Directory.Exists(modDir) || !Directory.EnumerateFileSystemEntries(modDir, "*", SearchOption.AllDirectories).Any())
        {
            try { Directory.Delete(modDir, true); } catch { }
            return false;
        }

        return true;
    }

    /// <summary>从 sources.json 的 source 字段写 svl-source.json 到各 mod 目录。</summary>
    private static void WriteSourceCredentials(List<JsonElement> sourcesList, string modsPath)
    {
        foreach (var source in sourcesList)
        {
            try
            {
                if (!source.TryGetProperty("name", out var nameProp)) continue;
                var modName = nameProp.GetString();
                if (string.IsNullOrWhiteSpace(modName)) continue;
                var safeModName = InstanceRuntimePathResolver.SanitizeFileNameComponent(modName, "unknown");

                if (!source.TryGetProperty("source", out var sourceProp) || sourceProp.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var modDir = Path.Combine(modsPath, safeModName);
                if (!Directory.Exists(modDir)) continue;

                var sourceJson = sourceProp.GetRawText();
                var sourceFilePath = Path.Combine(modDir, "svl-source.json");
                File.WriteAllText(sourceFilePath, sourceJson);
            }
            catch { }
        }
    }

    /// <summary>
    /// 提取整合包图标到版本目录。
    /// 写入 .svl-instance-icon-smapi.png（SMAPI 实例图标文件名），使 InstanceIconResolver 能正确解析。
    /// 整合包安装 SMAPI 后即为 SMAPI 实例，使用 SMAPI 专属图标文件名可：
    /// 1. 被 ResolveIconPath(isSmapiInstance=true) 命中，显示整合包自定义图标
    /// 2. 阻止后续 TryWriteDefaultSmapiIcon 写入默认 Modded.png（检测到已有自定义图标时跳过）
    /// </summary>
    private static string? ExtractPackIcon(string tempDir, string versionRoot)
    {
        try
        {
            var iconCandidates = new[] { "modpack-icon.png", "pack-icon.png", "icon.png", "logo.png", "thumbnail.png" };
            foreach (var name in iconCandidates)
            {
                var iconPath = Path.Combine(tempDir, name);
                if (File.Exists(iconPath))
                {
                    var destPath = Path.Combine(versionRoot, ".svl-instance-icon-smapi.png");
                    File.Copy(iconPath, destPath, true);
                    return destPath;
                }

                // 在子目录查找
                var found = Directory.GetFiles(tempDir, name, SearchOption.AllDirectories).FirstOrDefault();
                if (!string.IsNullOrEmpty(found))
                {
                    var destPath = Path.Combine(versionRoot, ".svl-instance-icon-smapi.png");
                    File.Copy(found, destPath, true);
                    return destPath;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>保存实例记录到 InstanceRegistryStore。</summary>
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

    /// <summary>取消/失败时清理版本隔离目录。参考旧架构先处理 Content junction 避免误删源目录。</summary>
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
    public int ManifestVersion { get; set; }

    [JsonPropertyName("files")]
    public List<CurseforgeManifestFile> Files { get; set; } = [];

    [JsonPropertyName("overrides")]
    public string? Overrides { get; set; }
}

internal sealed class CurseforgeManifestFile
{
    [JsonPropertyName("projectID")]
    public long ProjectId { get; set; }

    [JsonPropertyName("fileID")]
    public long FileId { get; set; }

    [JsonPropertyName("required")]
    public bool Required { get; set; } = true;
}
