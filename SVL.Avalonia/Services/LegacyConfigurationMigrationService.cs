using SVL.Avalonia.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SVL.Avalonia.Services;

/// <summary>
/// 将旧 WPF 版本的配置和实例列表迁移到 Avalonia 的独立配置目录。
///
/// 旧版本的全局配置位于 %LOCALAPPDATA%/SVL/app.json 和 gamepath.json，
/// 历史版本的 Nexus API Key 还可能单独位于 %LOCALAPPDATA%/SVL/nexusmods.json；
/// 实例列表通常位于旧程序目录/SVL/instances.json；部分历史版本也写入了
/// %LOCALAPPDATA%/SVL/instances.json 或旧程序目录根部，因此这里会探测所有
/// 已存在的历史位置并合并，而不是只取第一个位置。
/// </summary>
public sealed class LegacyConfigurationMigrationService
{
    private const string MigrationMarkerFileName = ".legacy-migration-v1.json";

    private readonly AppUserSettingsStore _settingsStore;
    private readonly InstanceRegistryStore _instanceRegistryStore;
    private readonly string _legacyRoot;
    private readonly string? _legacyInstancesPath;
    private readonly string _markerPath;

    public LegacyConfigurationMigrationService(
        AppUserSettingsStore settingsStore,
        InstanceRegistryStore instanceRegistryStore,
        string? legacyRoot = null,
        string? legacyInstancesPath = null)
    {
        _settingsStore = settingsStore;
        _instanceRegistryStore = instanceRegistryStore;
        _legacyRoot = string.IsNullOrWhiteSpace(legacyRoot)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SVL")
            : Path.GetFullPath(legacyRoot);
        _legacyInstancesPath = string.IsNullOrWhiteSpace(legacyInstancesPath)
            ? null
            : Path.GetFullPath(legacyInstancesPath);
        _markerPath = Path.Combine(
            Path.GetDirectoryName(_settingsStore.GetSettingsPath()) ?? _legacyRoot,
            MigrationMarkerFileName);
    }

    public LegacyConfigurationMigrationResult Migrate()
    {
        // 标记只代表上一次探测到的来源已经处理完，不能把它当成永久屏蔽。
        // 例如首次启动时旧实例文件尚未落到旧程序目录，下一次启动应继续探测；
        // 同样，旧 WPF 运行期间新增的实例也应能被补迁移。导入逻辑本身是幂等的，
        // 这里只在“没有新增来源且已记录文件都未变化”时跳过。
        if (File.Exists(_markerPath) &&
            !HasLegacySourcesChangedSinceMarker() &&
            HasMigrationTargets())
        {
            return LegacyConfigurationMigrationResult.AlreadyCompleted;
        }

        var result = new LegacyConfigurationMigrationResult();
        var currentSettings = _settingsStore.Load();
        var hadCurrentSettings = _settingsStore.Exists;
        var settingsChanged = false;
        var sourceFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var appConfigPath = Path.Combine(_legacyRoot, "app.json");
        if (File.Exists(appConfigPath))
        {
            sourceFiles.Add(appConfigPath);
            try
            {
                using var document = JsonDocument.Parse(ReadTextFileWithBom(appConfigPath));
                settingsChanged |= ImportLegacyAppSettings(
                    document.RootElement,
                    currentSettings,
                    hadCurrentSettings,
                    result);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"读取旧应用配置失败: {ex.Message}");
            }
        }

        var gamePathConfigPath = Path.Combine(_legacyRoot, "gamepath.json");
        var legacyGamePath = string.Empty;
        if (File.Exists(gamePathConfigPath))
        {
            sourceFiles.Add(gamePathConfigPath);
            try
            {
                using var document = JsonDocument.Parse(ReadTextFileWithBom(gamePathConfigPath));
                legacyGamePath = document.RootElement.ValueKind == JsonValueKind.String
                    ? document.RootElement.GetString() ?? string.Empty
                    : GetString(document.RootElement, "GamePath", "Path") ?? string.Empty;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"读取旧游戏路径配置失败: {ex.Message}");
            }
        }

        // 很早的 WPF 版本把 Nexus API Key 单独保存为 nexusmods.json，
        // 后来才合并到 app.json。不能只迁移 app.json，否则用户升级后
        // 会表现为“配置迁移成功但 Nexus 需要重新登录”。
        var legacyNexusConfigPath = Path.Combine(_legacyRoot, "nexusmods.json");
        if (File.Exists(legacyNexusConfigPath))
        {
            sourceFiles.Add(legacyNexusConfigPath);
            try
            {
                using var document = JsonDocument.Parse(ReadTextFileWithBom(legacyNexusConfigPath));
                var legacyApiKey = GetString(
                    document.RootElement,
                    "ApiKey",
                    "apiKey",
                    "NexusModsApiKey",
                    "nexusModsApiKey");
                if (!string.IsNullOrWhiteSpace(legacyApiKey) &&
                    string.IsNullOrWhiteSpace(currentSettings.NexusApiKey))
                {
                    currentSettings.NexusApiKey = DecryptLegacySecret(legacyApiKey.Trim());
                    result.ImportedSettingsCount++;
                    settingsChanged = true;
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"读取旧 Nexus 配置失败: {ex.Message}");
            }
        }

        var legacyInstancesPaths = ResolveLegacyInstancesPaths();
        var legacyInstances = new List<LegacyInstanceRecord>();
        var defaultInstanceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var instancesPath in legacyInstancesPaths)
        {
            sourceFiles.Add(instancesPath);
            try
            {
                legacyInstances.AddRange(ReadLegacyInstances(instancesPath));
            }
            catch (Exception ex)
            {
                result.Errors.Add($"读取旧实例列表失败 ({instancesPath}): {ex.Message}");
            }

            var defaultInstanceId = ReadDefaultInstanceId(instancesPath);
            if (!string.IsNullOrWhiteSpace(defaultInstanceId))
            {
                defaultInstanceIds.Add(defaultInstanceId);
            }

            var defaultInstancePath = ResolveLegacyDefaultInstancePath(instancesPath);
            if (!string.IsNullOrWhiteSpace(defaultInstancePath) && File.Exists(defaultInstancePath))
            {
                sourceFiles.Add(defaultInstancePath);
            }
        }

        if (!string.IsNullOrWhiteSpace(legacyGamePath))
        {
            legacyInstances.Add(new LegacyInstanceRecord
            {
                Name = Path.GetFileName(legacyGamePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                GamePath = legacyGamePath,
                IsDefault = true
            });
        }

        if (legacyInstances.Count > 0)
        {
            settingsChanged |= ImportLegacyInstances(
                legacyInstances,
                defaultInstanceIds,
                currentSettings,
                result);
        }

        if (settingsChanged)
        {
            _settingsStore.Save(currentSettings);
        }

        // 没有旧文件时不写标记，允许以后用户把旧配置放回正确位置后再次尝试。
        // 有旧文件但某个文件损坏时也不写标记，下一次启动可以继续补迁移。
        if (sourceFiles.Count > 0 && result.Errors.Count == 0)
        {
            WriteMarker(sourceFiles, result);
        }

        result.HasSources = sourceFiles.Count > 0;
        result.SettingsChanged = settingsChanged;
        return result;
    }

    private bool ImportLegacyAppSettings(
        JsonElement root,
        AppUserSettings settings,
        bool hadCurrentSettings,
        LegacyConfigurationMigrationResult result)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            result.Errors.Add("旧应用配置不是 JSON 对象");
            return false;
        }

        var defaults = new AppUserSettings();
        var changed = false;

        changed |= ImportString(root, "GameWindowTitle", settings.GameWindowTitle, defaults.GameWindowTitle,
            value => settings.GameWindowTitle = string.Equals(value, "Stardew Valley", StringComparison.Ordinal)
                ? defaults.GameWindowTitle
                : value, result, ref hadCurrentSettings,
            currentDefaultPredicate: value => string.Equals(value, "<default>", StringComparison.OrdinalIgnoreCase) ||
                                              string.Equals(value, "Stardew Valley", StringComparison.OrdinalIgnoreCase));
        changed |= ImportString(root, "LauncherTitle", settings.LauncherTitle, defaults.LauncherTitle,
            value => settings.LauncherTitle = value, result, ref hadCurrentSettings);
        changed |= ImportString(root, "LauncherAppName", settings.LauncherAppName, defaults.LauncherAppName,
            value => settings.LauncherAppName = value, result, ref hadCurrentSettings);

        if (TryGetLegacyEnumToken(root, "WindowSizeMode", out var windowSizeMode) &&
            (!hadCurrentSettings || string.Equals(settings.WindowSizeMode, defaults.WindowSizeMode, StringComparison.Ordinal)))
        {
            var normalizedWindowSizeMode = NormalizeWindowSizeMode(windowSizeMode);
            if (!string.IsNullOrWhiteSpace(normalizedWindowSizeMode))
            {
                settings.WindowSizeMode = normalizedWindowSizeMode;
                result.ImportedSettingsCount++;
                changed = true;
            }
        }

        changed |= ImportInt(root, "CustomWindowWidth", settings.CustomWindowWidth, defaults.CustomWindowWidth,
            value => settings.CustomWindowWidth = Math.Clamp(value, 600, 7680), result, ref hadCurrentSettings);
        changed |= ImportInt(root, "CustomWindowHeight", settings.CustomWindowHeight, defaults.CustomWindowHeight,
            value => settings.CustomWindowHeight = Math.Clamp(value, 400, 4320), result, ref hadCurrentSettings);

        if (TryGetLegacyEnumToken(root, "ThemeMode", out var themeMode) &&
            (!hadCurrentSettings || string.Equals(settings.ThemeMode, defaults.ThemeMode, StringComparison.Ordinal)))
        {
            var normalizedThemeMode = NormalizeThemeMode(themeMode);
            if (!string.IsNullOrWhiteSpace(normalizedThemeMode))
            {
                settings.ThemeMode = normalizedThemeMode;
                result.ImportedSettingsCount++;
                changed = true;
            }
        }

        changed |= ImportString(root, "ThemeStyleName", settings.ThemeStyleName, defaults.ThemeStyleName,
            value => settings.ThemeStyleName = value, result, ref hadCurrentSettings);
        changed |= ImportString(root, "ThemeColorScheme", settings.ThemeColorScheme, defaults.ThemeColorScheme,
            value => settings.ThemeColorScheme = value, result, ref hadCurrentSettings);
        changed |= ImportString(root, "Language", settings.UiLanguage, defaults.UiLanguage,
            value => settings.UiLanguage = value, result, ref hadCurrentSettings);

        changed |= ImportBool(root, "EnableAnimations", settings.EnableAnimations, defaults.EnableAnimations,
            value => settings.EnableAnimations = value, result, ref hadCurrentSettings);
        changed |= ImportBool(root, "AutoCheckUpdates", settings.EnableAutoUpdateCheck, defaults.EnableAutoUpdateCheck,
            value => settings.EnableAutoUpdateCheck = value, result, ref hadCurrentSettings);
        changed |= ImportBool(root, "AutoDownloadUpdate", settings.AutoDownloadUpdate, defaults.AutoDownloadUpdate,
            value => settings.AutoDownloadUpdate = value, result, ref hadCurrentSettings);
        changed |= ImportBool(root, "MinimizeToTrayOnStartup", settings.MinimizeToTrayOnStartup, defaults.MinimizeToTrayOnStartup,
            value => settings.MinimizeToTrayOnStartup = value, result, ref hadCurrentSettings);
        changed |= ImportBool(root, "MinimizeToTrayOnClose", settings.MinimizeToTrayOnClose, defaults.MinimizeToTrayOnClose,
            value => settings.MinimizeToTrayOnClose = value, result, ref hadCurrentSettings);
        changed |= ImportBool(root, "ShowNotifications", settings.ShowNotifications, defaults.ShowNotifications,
            value => settings.ShowNotifications = value, result, ref hadCurrentSettings);
        changed |= ImportBool(root, "DebugMode", settings.DebugMode, defaults.DebugMode,
            value => settings.DebugMode = value, result, ref hadCurrentSettings);

        changed |= ImportString(root, "SmapiDefaultSource", settings.DefaultSmapiSource, defaults.DefaultSmapiSource,
            value => settings.DefaultSmapiSource = value, result, ref hadCurrentSettings);
        changed |= ImportString(root, "ModDefaultSource", settings.DefaultModSource, defaults.DefaultModSource,
            value => settings.DefaultModSource = value, result, ref hadCurrentSettings);
        changed |= ImportString(root, "LocalizationPreferredSource", settings.LocalizationPreferredSource, defaults.LocalizationPreferredSource,
            value => settings.LocalizationPreferredSource = value, result, ref hadCurrentSettings);
        changed |= ImportInt(root, "MaxConcurrentModDownloads", settings.CollectionDownloadParallelism, defaults.CollectionDownloadParallelism,
            value => settings.CollectionDownloadParallelism = Math.Clamp(value, 1, 8), result, ref hadCurrentSettings);
        changed |= ImportInt(root, "DownloadSegmentThreads", settings.DownloadSegmentThreads, defaults.DownloadSegmentThreads,
            value => settings.DownloadSegmentThreads = Math.Clamp(value, 1, 16), result, ref hadCurrentSettings);
        changed |= ImportInt(root, "MaxConcurrentModUpdateChecks", settings.MaxConcurrentModUpdateChecks, defaults.MaxConcurrentModUpdateChecks,
            value => settings.MaxConcurrentModUpdateChecks = Math.Clamp(value, 1, 16), result, ref hadCurrentSettings);
        changed |= ImportBool(root, "EnableNexusModsSearchCache", settings.EnableNexusModsSearchCache, defaults.EnableNexusModsSearchCache,
            value => settings.EnableNexusModsSearchCache = value, result, ref hadCurrentSettings);
        changed |= ImportBool(root, "EnableDownloadCache", settings.EnableDownloadCache, defaults.EnableDownloadCache,
            value => settings.EnableDownloadCache = value, result, ref hadCurrentSettings);
        changed |= ImportBool(root, "EnableDownloadFloatingTaskButton", settings.EnableDownloadFloatingTaskButton, defaults.EnableDownloadFloatingTaskButton,
            value => settings.EnableDownloadFloatingTaskButton = value, result, ref hadCurrentSettings);
        changed |= ImportInt(root, "CacheRetentionMinutes", settings.CacheRetentionMinutes, defaults.CacheRetentionMinutes,
            value => settings.CacheRetentionMinutes = Math.Clamp(value, 1, 10080), result, ref hadCurrentSettings);
        changed |= ImportInt(root, "DownloadCacheRetentionMinutes", settings.DownloadCacheRetentionMinutes, defaults.DownloadCacheRetentionMinutes,
            value => settings.DownloadCacheRetentionMinutes = Math.Clamp(value, 60, 43200), result, ref hadCurrentSettings);

        changed |= ImportString(root, "LogLevel", settings.LogLevel, defaults.LogLevel,
            value => settings.LogLevel = NormalizeLogLevel(value), result, ref hadCurrentSettings);
        changed |= ImportString(root, "SkippedUpdateVersion", settings.SkippedLauncherVersion, defaults.SkippedLauncherVersion,
            value => settings.SkippedLauncherVersion = value, result, ref hadCurrentSettings);

        if (TryGetLegacyEnumToken(root, "PreferredUpdateSource", out var updateSource) &&
            (!hadCurrentSettings || string.Equals(settings.PreferredUpdateSource, defaults.PreferredUpdateSource, StringComparison.Ordinal)))
        {
            var normalizedUpdateSource = NormalizeUpdateSource(updateSource);
            if (!string.IsNullOrWhiteSpace(normalizedUpdateSource))
            {
                settings.PreferredUpdateSource = normalizedUpdateSource;
                result.ImportedSettingsCount++;
                changed = true;
            }
        }

        // WPF 使用 CheckPrereleaseUpdates，Avalonia 将其合并为更新通道。
        if (TryGetBool(root, "CheckPrereleaseUpdates", out var checkPrereleaseUpdates) &&
            (!hadCurrentSettings || string.Equals(settings.UpdateChannel, defaults.UpdateChannel, StringComparison.Ordinal)))
        {
            settings.UpdateChannel = checkPrereleaseUpdates ? "预览版" : "稳定版";
            result.ImportedSettingsCount++;
            changed = true;
        }

        if (TryGetString(root, "NexusModsApiKey", out var encryptedApiKey) &&
            !string.IsNullOrWhiteSpace(encryptedApiKey) &&
            (string.IsNullOrWhiteSpace(settings.NexusApiKey) || !hadCurrentSettings))
        {
            var apiKey = DecryptLegacySecret(encryptedApiKey);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                settings.NexusApiKey = apiKey;
                changed = true;
            }
        }

        changed |= ImportString(root, "NexusModsOAuthToken", settings.NexusOAuthAccessToken, defaults.NexusOAuthAccessToken,
            value => settings.NexusOAuthAccessToken = value, result, ref hadCurrentSettings);
        changed |= ImportString(root, "NexusModsOAuthRefreshToken", settings.NexusOAuthRefreshToken, defaults.NexusOAuthRefreshToken,
            value => settings.NexusOAuthRefreshToken = value, result, ref hadCurrentSettings);
        changed |= ImportString(root, "NexusModsOAuthIdToken", settings.NexusOAuthIdToken, defaults.NexusOAuthIdToken,
            value => settings.NexusOAuthIdToken = value, result, ref hadCurrentSettings);
        changed |= ImportString(root, "NexusModsOAuthUserName", settings.NexusUserName, defaults.NexusUserName,
            value => settings.NexusUserName = value, result, ref hadCurrentSettings);
        changed |= ImportString(root, "NexusModsOAuthMembershipType", settings.NexusMembershipType, defaults.NexusMembershipType,
            value => settings.NexusMembershipType = value, result, ref hadCurrentSettings);

        if (TryGetInt(root, "NexusUserId", out var userId) &&
            (!hadCurrentSettings || settings.NexusUserId == defaults.NexusUserId))
        {
            settings.NexusUserId = userId;
            changed = true;
        }

        return changed;
    }

    private bool ImportLegacyInstances(
        IReadOnlyList<LegacyInstanceRecord> legacyInstances,
        IReadOnlySet<string> defaultInstanceIds,
        AppUserSettings settings,
        LegacyConfigurationMigrationResult result)
    {
        var existing = _instanceRegistryStore.LoadManualInstances();
        var existingPaths = existing
            .Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .Select(item => NormalizePath(item.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        LegacyInstanceRecord? preferred = null;

        foreach (var group in legacyInstances
                     .Where(item => !string.IsNullOrWhiteSpace(item.GamePath))
                     .GroupBy(item => NormalizePath(item.GamePath), StringComparer.OrdinalIgnoreCase))
        {
            var path = group.First().GamePath.Trim().Trim('"');
            if (!Directory.Exists(path))
            {
                // 路径列表本来就会过滤失效路径，避免把已卸载的 WPF 记录迁移成死记录。
                continue;
            }

            var baseRecord = group.FirstOrDefault(item =>
                !item.EnableIsolation || item.Tags.Contains("Base", StringComparer.OrdinalIgnoreCase))
                ?? group.FirstOrDefault();
            var name = string.IsNullOrWhiteSpace(baseRecord?.Name)
                ? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : baseRecord!.Name.Trim();

            if (existingPaths.Add(NormalizePath(path)))
            {
                existing.Add(new ManualInstanceRecord { Name = name, Path = path });
                result.ImportedInstanceCount++;
                changed = true;
            }

            var candidate = group.FirstOrDefault(item =>
                                  item.IsDefault ||
                                  (!string.IsNullOrWhiteSpace(item.Id) &&
                                   defaultInstanceIds.Contains(item.Id)))
                            ?? group.FirstOrDefault();
            if (candidate != null && preferred == null)
            {
                preferred = candidate;
            }

            foreach (var item in group.Where(item => item.IsFavorite))
            {
                // WPF 的隔离实例记录 GamePath 指向 Base，Avalonia 的收藏键使用实际运行目录。
                // 迁移时还原到 versions/<实例名>（兼容旧布局下的 /game），否则刷新
                // InstancesPage 后收藏状态无法匹配。
                var favoritePath = ResolveLegacyInstanceRuntimePath(item);
                var key = BuildFavoriteKey(favoritePath, item.IsSMAPIInstance);
                if (!settings.FavoriteInstanceKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    settings.FavoriteInstanceKeys.Add(key);
                    changed = true;
                }
            }
        }

        if (changed)
        {
            _instanceRegistryStore.SaveManualInstances(existing);
        }

        if (preferred != null && string.IsNullOrWhiteSpace(settings.PreferredInstancePath))
        {
            settings.PreferredInstancePath = ResolveLegacyInstanceRuntimePath(preferred);
            settings.InstanceName = preferred.Name.Trim();
            settings.InstanceDescription = preferred.Description.Trim();
            settings.IsFavoriteInstance = preferred.IsFavorite;
            settings.GameWindowTitle = string.IsNullOrWhiteSpace(preferred.WindowTitle)
                ? settings.GameWindowTitle
                : preferred.WindowTitle.Trim();
            settings.InstanceCustomLaunchArguments = preferred.CustomArguments.Trim();
            settings.InstanceAutoConnectServer = preferred.AutoConnectServer;
            settings.InstanceServerAddress = preferred.ServerAddress.Trim();
            settings.InstanceSteamInviteCode = preferred.SteamInviteCode.Trim();
            settings.OverrideSteamLaunchOptions = preferred.OverrideSteamLaunchOptions;
            settings.SteamLaunchOptions = preferred.SteamLaunchOptions.Trim();
            settings.PreferredLaunchMode = preferred.IsSMAPIInstance ? "SMAPI" : "Vanilla";
            changed = true;
            result.SettingsChanged = true;
        }

        return changed;
    }

    /// <summary>
    /// 将 WPF 实例记录转换成 Avalonia 使用的运行时路径。
    /// WPF 的 instances.json 对隔离实例保存 Base 路径 + Name；Avalonia 的
    /// PreferredInstancePath/收藏键则保存 versions/&lt;Name&gt;（必要时再进入 game）。
    /// 找不到隔离目录时回退 Base，避免迁移后启动设置指向不存在的路径。
    /// </summary>
    private static string ResolveLegacyInstanceRuntimePath(LegacyInstanceRecord record)
    {
        var basePath = record.GamePath.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return basePath;
        }

        try
        {
            basePath = Path.GetFullPath(basePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            // 保留原始路径，后续由实例刷新流程继续处理。
        }

        var isBaseRecord = !record.EnableIsolation ||
                           record.Tags.Contains("Base", StringComparer.OrdinalIgnoreCase);
        if (isBaseRecord)
        {
            return basePath;
        }

        // 少数历史版本曾把 GamePath 直接写成 versions/<name> 或 versions/<name>/game。
        if (IsVersionScopedPath(basePath))
        {
            return InstanceRuntimePathResolver.Resolve(basePath);
        }

        if (string.IsNullOrWhiteSpace(record.Name))
        {
            return basePath;
        }

        var versionRoot = Path.Combine(basePath, "versions", record.Name.Trim());
        return Directory.Exists(versionRoot)
            ? InstanceRuntimePathResolver.Resolve(versionRoot)
            : basePath;
    }

    private static bool IsVersionScopedPath(string path)
    {
        try
        {
            var current = new DirectoryInfo(path);
            while (current != null)
            {
                if (string.Equals(current.Name, "versions", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                current = current.Parent;
            }
        }
        catch
        {
            // 非标准路径按 Base 路径处理。
        }

        return false;
    }

    private IReadOnlyList<string> ResolveLegacyInstancesPaths()
    {
        if (!string.IsNullOrWhiteSpace(_legacyInstancesPath))
        {
            return File.Exists(_legacyInstancesPath) ? [_legacyInstancesPath] : [];
        }

        var executableDirectory = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty);
        var candidates = new[]
        {
            Path.Combine(_legacyRoot, "instances.json"),
            Path.Combine(AppContext.BaseDirectory, "SVL", "instances.json"),
            Path.Combine(AppContext.BaseDirectory, "instances.json"),
            Path.Combine(Environment.CurrentDirectory, "SVL", "instances.json"),
            Path.Combine(Environment.CurrentDirectory, "instances.json"),
            Path.Combine(executableDirectory ?? string.Empty, "SVL", "instances.json"),
            Path.Combine(executableDirectory ?? string.Empty, "instances.json")
        };

        // WPF 的 SettingsService 使用“旧程序目录/SVL/instances.json”，而
        // Avalonia 发布目录通常是同一仓库下的 SVL.Avalonia；两者并排时，上面的
        // 当前目录探测不到 SVL.Desktop 下的实例列表。只沿当前应用目录、工作目录
        // 和显式 legacyRoot 的父级向上探测有限层级，并只检查约定的旧应用目录名，
        // 不扫描整个磁盘，避免启动时产生不可控的 IO 或误读其它应用数据。
        var expandedCandidates = candidates.ToList();
        foreach (var anchor in new[]
                 {
                     AppContext.BaseDirectory,
                     Environment.CurrentDirectory,
                     executableDirectory,
                     _legacyRoot
                 })
        {
            foreach (var directory in EnumerateLegacyApplicationDirectories(anchor))
            {
                expandedCandidates.Add(Path.Combine(directory, "instances.json"));
            }
        }

        return expandedCandidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .ToArray();
    }

    private static IEnumerable<string> EnumerateLegacyApplicationDirectories(string? anchor)
    {
        if (string.IsNullOrWhiteSpace(anchor))
        {
            yield break;
        }

        DirectoryInfo? current;
        try
        {
            current = new DirectoryInfo(anchor);
        }
        catch
        {
            yield break;
        }

        // 发布目录一般位于仓库/安装目录的两层以内；再向上没有实际收益，
        // 但有限上限可避免把常见用户目录下其它项目误当成旧配置来源。
        for (var depth = 0; current != null && depth <= 4; depth++, current = current.Parent)
        {
            yield return Path.Combine(current.FullName, "SVL");

            foreach (var legacyDirectoryName in new[] { "SVL.Desktop", "SVL.WPF", "SVLLauncher" })
            {
                yield return Path.Combine(current.FullName, legacyDirectoryName, "SVL");
            }

            IEnumerable<DirectoryInfo> children;
            try
            {
                children = current.EnumerateDirectories();
            }
            catch
            {
                continue;
            }

            foreach (var child in children.Where(child =>
                         child.Name.Contains("SVL", StringComparison.OrdinalIgnoreCase)))
            {
                yield return Path.Combine(child.FullName, "SVL");
            }
        }
    }

    private IEnumerable<string> EnumerateLegacySourceCandidates()
    {
        var candidates = new List<string>
        {
            Path.Combine(_legacyRoot, "app.json"),
            Path.Combine(_legacyRoot, "gamepath.json"),
            Path.Combine(_legacyRoot, "nexusmods.json")
        };

        foreach (var instancesPath in ResolveLegacyInstancesPaths())
        {
            candidates.Add(instancesPath);
            var defaultPath = ResolveLegacyDefaultInstancePath(instancesPath);
            if (!string.IsNullOrWhiteSpace(defaultPath))
            {
                candidates.Add(defaultPath);
            }
        }

        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private bool HasLegacySourcesChangedSinceMarker()
    {
        try
        {
            using var markerDocument = JsonDocument.Parse(ReadTextFileWithBom(_markerPath));
            var markerRoot = markerDocument.RootElement;
            var completedAt = markerRoot.TryGetProperty("CompletedAt", out var completedAtElement) &&
                              completedAtElement.ValueKind == JsonValueKind.String &&
                              DateTimeOffset.TryParse(completedAtElement.GetString(), out var parsedCompletedAt)
                ? parsedCompletedAt
                : DateTimeOffset.MinValue;

            var knownSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (markerRoot.TryGetProperty("Sources", out var sourceElement) &&
                sourceElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in sourceElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                    {
                        continue;
                    }

                    knownSources.Add(Path.GetFullPath(item.GetString()!));
                }
            }

            foreach (var candidate in EnumerateLegacySourceCandidates().Where(File.Exists))
            {
                if (!knownSources.Contains(candidate) ||
                    File.GetLastWriteTimeUtc(candidate) > completedAt.UtcDateTime)
                {
                    return true;
                }
            }
        }
        catch
        {
            // 标记损坏或格式过旧时重新探测一次，比静默跳过迁移更安全。
            return true;
        }

        return false;
    }

    private bool HasMigrationTargets()
    {
        // 迁移标记只表示来源文件已经处理过，不能证明目标文件仍然存在。
        // 用户清理配置、恢复备份或升级过程中发生文件损坏后，必须允许旧 WPF
        // 数据再次写入 Avalonia 目录，否则启动器会永久停留在“已迁移”状态。
        if (!_settingsStore.Exists && EnumerateLegacySourceCandidates().Any(File.Exists))
        {
            return false;
        }

        if (!_instanceRegistryStore.Exists &&
            (ResolveLegacyInstancesPaths().Count > 0 ||
             File.Exists(Path.Combine(_legacyRoot, "gamepath.json"))))
        {
            return false;
        }

        return true;
    }

    private static List<LegacyInstanceRecord> ReadLegacyInstances(string path)
    {
        using var document = JsonDocument.Parse(ReadTextFileWithBom(path));
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, "Instances", out var instances))
        {
            root = instances;
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("旧实例列表不是 JSON 数组");
        }

        var result = new List<LegacyInstanceRecord>();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var pathValue = GetString(item, "GamePath", "Path");
            if (string.IsNullOrWhiteSpace(pathValue))
            {
                continue;
            }

            var tags = new List<string>();
            if (TryGetProperty(item, "Tags", out var tagElement) && tagElement.ValueKind == JsonValueKind.Array)
            {
                tags.AddRange(tagElement.EnumerateArray()
                    .Select(tag => tag.ValueKind == JsonValueKind.String ? tag.GetString() : null)
                    .Where(tag => !string.IsNullOrWhiteSpace(tag))!
                    .Select(tag => tag!.Trim()));
            }

            result.Add(new LegacyInstanceRecord
            {
                Id = GetString(item, "Id") ?? string.Empty,
                Name = GetString(item, "Name") ?? string.Empty,
                GamePath = pathValue,
                IsSMAPIInstance = GetBool(item, "IsSMAPIInstance", "IsSmapiInstance"),
                IsDefault = GetBool(item, "IsDefault"),
                IsFavorite = GetBool(item, "IsFavorite"),
                EnableIsolation = GetBool(item, "EnableIsolation"),
                Description = GetString(item, "Description") ?? string.Empty,
                WindowTitle = GetString(item, "WindowTitle") ?? string.Empty,
                CustomArguments = GetString(item, "CustomArguments", "CustomLaunchArguments") ?? string.Empty,
                AutoConnectServer = GetBool(item, "AutoConnectServer"),
                ServerAddress = GetString(item, "ServerAddress") ?? string.Empty,
                SteamInviteCode = GetString(item, "SteamInviteCode") ?? string.Empty,
                OverrideSteamLaunchOptions = GetBool(item, "OverrideSteamLaunchOptions"),
                SteamLaunchOptions = GetString(item, "SteamLaunchOptions") ?? string.Empty,
                Tags = tags
            });
        }

        return result;
    }

    private string ReadDefaultInstanceId(string? instancesPath)
    {
        var path = ResolveLegacyDefaultInstancePath(instancesPath);
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return File.Exists(path) ? ReadTextFileWithBom(path).Trim().Trim('"') : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private string ResolveLegacyDefaultInstancePath(string? instancesPath)
    {
        var directory = !string.IsNullOrWhiteSpace(instancesPath)
            ? Path.GetDirectoryName(instancesPath)
            : _legacyRoot;
        return string.IsNullOrWhiteSpace(directory)
            ? string.Empty
            : Path.Combine(directory, "default_instance.json");
    }

    private void WriteMarker(IEnumerable<string> sourceFiles, LegacyConfigurationMigrationResult result)
    {
        try
        {
            var marker = new
            {
                Version = 1,
                CompletedAt = DateTimeOffset.UtcNow,
                Sources = sourceFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList(),
                ImportedSettings = result.ImportedSettingsCount,
                ImportedInstances = result.ImportedInstanceCount
            };
            AtomicFileWriter.WriteUtf8(
                _markerPath,
                JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }));
            result.Completed = true;
        }
        catch (Exception ex)
        {
            result.Errors.Add($"保存迁移标记失败: {ex.Message}");
        }
    }

    private static bool ImportString(
        JsonElement root,
        string propertyName,
        string current,
        string defaultValue,
        Action<string> setter,
        LegacyConfigurationMigrationResult result,
        ref bool hadCurrentSettings,
        bool countChange = true,
        Func<string, bool>? currentDefaultPredicate = null)
    {
        if (!TryGetString(root, propertyName, out var value) || string.IsNullOrWhiteSpace(value) ||
            (hadCurrentSettings && !(currentDefaultPredicate?.Invoke(current) ??
                                     string.Equals(current, defaultValue, StringComparison.Ordinal))))
        {
            return false;
        }

        setter(value.Trim());
        if (countChange)
        {
            result.ImportedSettingsCount++;
        }

        return true;
    }

    /// <summary>读取旧配置并按 BOM 自动识别 UTF-8/UTF-16/UTF-32。</summary>
    private static string ReadTextFileWithBom(string path)
    {
        return ManifestTextReader.ReadAllText(path);
    }

    private static bool ImportBool(
        JsonElement root,
        string propertyName,
        bool current,
        bool defaultValue,
        Action<bool> setter,
        LegacyConfigurationMigrationResult result,
        ref bool hadCurrentSettings,
        bool countChange = true)
    {
        if (!TryGetBool(root, propertyName, out var value) ||
            (hadCurrentSettings && current != defaultValue))
        {
            return false;
        }

        setter(value);
        if (countChange)
        {
            result.ImportedSettingsCount++;
        }

        return true;
    }

    private static bool ImportInt(
        JsonElement root,
        string propertyName,
        int current,
        int defaultValue,
        Action<int> setter,
        LegacyConfigurationMigrationResult result,
        ref bool hadCurrentSettings)
    {
        if (!TryGetInt(root, propertyName, out var value) ||
            (hadCurrentSettings && current != defaultValue))
        {
            return false;
        }

        setter(value);
        result.ImportedSettingsCount++;
        return true;
    }

    private static string NormalizeLogLevel(string value)
    {
        if (int.TryParse(value, out var numeric))
        {
            return numeric switch
            {
                0 => "Debug",
                1 => "Info",
                2 => "Warning",
                3 => "Error",
                4 => "Critical",
                _ => "Info"
            };
        }

        return value switch
        {
            "Warn" => "Warning",
            "WARNING" => "Warning",
            _ => value
        };
    }

    private static string NormalizeWindowSizeMode(string value)
    {
        if (int.TryParse(value, out var numeric))
        {
            return numeric switch
            {
                0 => "全屏",
                1 or 2 => "默认",
                3 => "自定义",
                4 => "最大化",
                _ => string.Empty
            };
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "fullscreen" or "全屏" => "全屏",
            "default" or "sameaslauncher" or "默认" or "与启动器尺寸一致" => "默认",
            "custom" or "自定义" => "自定义",
            "maximized" or "最大化" => "最大化",
            _ => string.Empty
        };
    }

    private static string NormalizeThemeMode(string value)
    {
        if (int.TryParse(value, out var numeric))
        {
            return numeric switch
            {
                0 => "浅色",
                1 => "深色",
                2 => "跟随系统",
                _ => string.Empty
            };
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "light" or "浅色" => "浅色",
            "dark" or "深色" => "深色",
            "system" or "跟随系统" => "跟随系统",
            _ => string.Empty
        };
    }

    private static string NormalizeUpdateSource(string value)
    {
        if (int.TryParse(value, out var numeric))
        {
            return numeric switch
            {
                0 => "GitHub (推荐)",
                1 => "Gitee (国内加速)",
                _ => string.Empty
            };
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "github" or "github (推荐)" => "GitHub (推荐)",
            "gitee" or "gitee (国内加速)" => "Gitee (国内加速)",
            _ => string.Empty
        };
    }

    private static string DecryptLegacySecret(string value)
    {
        // 与 WPF 版 SVL.Core.Security.SecureString 保持兼容；迁移完成后
        // Avalonia 配置以本地 JSON 形式保存，仍只在本机配置目录中使用。
        try
        {
            using var sha256 = SHA256.Create();
            var key = sha256.ComputeHash(Encoding.UTF8.GetBytes("SVL_SecureKey_2024"));
            using var md5 = MD5.Create();
            var iv = md5.ComputeHash(Encoding.UTF8.GetBytes("SVL_InitVec_2024"));
            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var decryptor = aes.CreateDecryptor();
            var encrypted = Convert.FromBase64String(value);
            var plain = decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);
            var decoded = Encoding.UTF8.GetString(plain);
            return string.IsNullOrWhiteSpace(decoded) ? value : decoded;
        }
        catch
        {
            // 兼容旧文件中已经是明文的 API Key，或损坏/非 Base64 内容。
            return value;
        }
    }

    private static string BuildFavoriteKey(string path, bool isSmapi)
    {
        return $"{NormalizePath(path)}|{(isSmapi ? "SMAPI" : "VANILLA")}";
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim().Trim('"'))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim().Trim('"').TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(element, name, out var value))
            {
                if (value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }

                if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                {
                    return value.ToString();
                }
            }
        }

        return null;
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = GetString(element, name) ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetInt(JsonElement element, string name, out int value)
    {
        value = 0;
        if (!TryGetProperty(element, name, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value))
        {
            return true;
        }

        return property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out value);
    }

    private static bool TryGetLegacyEnumToken(JsonElement element, string name, out string value)
    {
        value = GetString(element, name) ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool GetBool(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetBool(element, name, out var value))
            {
                return value;
            }
        }

        return false;
    }

    private static bool TryGetBool(JsonElement element, string name, out bool value)
    {
        value = false;
        if (!TryGetProperty(element, name, out var property))
        {
            return false;
        }

        if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
        {
            value = number != 0;
            return true;
        }

        return property.ValueKind == JsonValueKind.String &&
               bool.TryParse(property.GetString(), out value);
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private sealed class LegacyInstanceRecord
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string GamePath { get; init; } = string.Empty;
        public bool IsSMAPIInstance { get; init; }
        public bool IsDefault { get; init; }
        public bool IsFavorite { get; init; }
        public bool EnableIsolation { get; init; }
        public string Description { get; init; } = string.Empty;
        public string WindowTitle { get; init; } = string.Empty;
        public string CustomArguments { get; init; } = string.Empty;
        public bool AutoConnectServer { get; init; }
        public string ServerAddress { get; init; } = string.Empty;
        public string SteamInviteCode { get; init; } = string.Empty;
        public bool OverrideSteamLaunchOptions { get; init; }
        public string SteamLaunchOptions { get; init; } = string.Empty;
        public List<string> Tags { get; init; } = [];
    }
}

public sealed class LegacyConfigurationMigrationResult
{
    internal static LegacyConfigurationMigrationResult AlreadyCompleted => new() { Completed = true };

    public bool Completed { get; internal set; }
    public bool HasSources { get; internal set; }
    public bool SettingsChanged { get; internal set; }
    public int ImportedSettingsCount { get; internal set; }
    public int ImportedInstanceCount { get; internal set; }
    public List<string> Errors { get; } = [];
}
