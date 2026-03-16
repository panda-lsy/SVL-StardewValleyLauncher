using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SVL.Core.App;
using SVL.Core.Config;
using SVL.Core.IO;
using SVL.Core.Logging;
using SVL.Desktop.Controls;
using SVL.Desktop.Services;

namespace SVL.Desktop.ViewModels;

/// <summary>
/// 设置页面 ViewModel
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly List<string> _launcherVisibilityOptions;
    private readonly List<string> _windowSizeModeOptions;
    private readonly List<string> _themeModeOptions;
    private readonly List<string> _logLevelOptions;
    private readonly List<string> _languageOptions;
    private readonly List<string> _fontSizeOptions;

    // 避免 LoadSettings 过程中触发主题重新应用（防止配色方案重置）
    private bool _suppressThemeApplication;

    // SMAPI 下载源选项
    public List<string> SmapiSources { get; } = new()
    {
        "全部",
        "GitHub",
        "Curseforge",
        "NexusMods"
    };

    // Mod 下载源选项
    public List<string> ModSources { get; } = new()
    {
        "全部",
        "Curseforge",
        "NexusMods"
    };

    public List<string> LocalizationSources { get; } = new()
    {
        "Gitee",
        "GitHub"
    };

    // 启动器更新源选项
    public List<string> UpdateSourceOptions { get; } = new()
    {
        "GitHub (推荐)",
        "Gitee (国内加速)"
    };

    private System.Threading.CancellationTokenSource? _autoSaveCts;

    // 避免 LoadSettings 过程中触发“默认下载源立即保存”
    private bool _suppressDefaultSourceImmediateSave;

    public SettingsViewModel()
    {
        // 初始化选项列表
        _launcherVisibilityOptions = new List<string>
        {
            "游戏启动后立即关闭",
            "游戏启动后隐藏，游戏退出后自动关闭",
            "游戏启动后隐藏，游戏退出后重新打开",
            "游戏启动后最小化",
            "游戏启动后仍保持不变"
        };

        _windowSizeModeOptions = new List<string>
        {
            "全屏",
            "默认",
            "与启动器尺寸一致",
            "自定义尺寸",
            "最大化"
        };

        _themeModeOptions = new List<string>
        {
            "浅色",
            "深色",
            "跟随系统"
        };

        _logLevelOptions = new List<string>
        {
            "Debug",
            "Info",
            "Warning",
            "Error",
            "None"
        };

        _languageOptions = new List<string>
        {
            "简体中文",
            "English"
        };

        _fontSizeOptions = new List<string> { "12", "13", "14", "15", "16", "18", "20" };

        // 初始化 Material You 配色方案列表
        InitializeColorSchemes();

        // 初始化应用版本号
        var version = LauncherUpdateService.CurrentVersion;
        AppVersion = version.Revision > 0
            ? $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}"
            : $"{version.Major}.{version.Minor}.{version.Build}";

        // 加载设置
        LoadSettings();

        // 初始化缓存大小
        _ = UpdateCacheSizeAsync();
    }

    // ===== 基本设置 =====

    [ObservableProperty]
    private string _gameWindowTitle = "<default>";

    /// <summary>
    /// 图片缓存大小（格式化字符串）
    /// </summary>
    [ObservableProperty]
    private string _cacheSize = "计算中...";

    /// <summary>
    /// 下载文件缓存大小（格式化字符串）
    /// </summary>
    [ObservableProperty]
    private string _downloadCacheSize = "计算中...";

    [ObservableProperty]
    private int _imageCacheCount;

    [ObservableProperty]
    private int _downloadCacheCount;

    // 圆环图：角度（度），默认从 -90°（正上方）开始顺时针
    [ObservableProperty]
    private double _imageCacheStartAngle = -90;

    [ObservableProperty]
    private double _imageCacheSweepAngle;

    [ObservableProperty]
    private double _downloadCacheStartAngle = -90;

    [ObservableProperty]
    private double _downloadCacheSweepAngle;

    [ObservableProperty]
    private double _searchCacheStartAngle = -90;

    [ObservableProperty]
    private double _searchCacheSweepAngle;

    [ObservableProperty]
    private double _nexusSearchStartAngle = -90;

    [ObservableProperty]
    private double _nexusSearchSweepAngle;

    [ObservableProperty]
    private double _curseforgeSearchStartAngle = -90;

    [ObservableProperty]
    private double _curseforgeSearchSweepAngle;

    [ObservableProperty]
    private double _githubSearchStartAngle = -90;

    [ObservableProperty]
    private double _githubSearchSweepAngle;

    [ObservableProperty]
    private int _totalSearchCacheCount;

    [ObservableProperty]
    private int _nexusSearchCacheCount;

    [ObservableProperty]
    private int _curseforgeSearchCacheCount;

    [ObservableProperty]
    private int _githubSearchCacheCount;

    /// <summary>
    /// 是否正在清除缓存
    /// </summary>
    [ObservableProperty]
    private bool _isClearingCache = false;

    /// <summary>
    /// 是否启用 NexusMods 搜索结果缓存（搜索记录缓存）
    /// </summary>
    [ObservableProperty]
    private bool _enableNexusModsSearchCache = false;

    /// <summary>
    /// 是否启用下载文件缓存
    /// </summary>
    [ObservableProperty]
    private bool _enableDownloadCache = true;

    /// <summary>
    /// 是否正在清除 NexusMods 搜索缓存
    /// </summary>
    [ObservableProperty]
    private bool _isClearingNexusModsSearchCache = false;

    /// <summary>
    /// 缓存时长（分钟），默认 60。
    /// </summary>
    [ObservableProperty]
    private int _cacheRetentionMinutes = 60;

    public List<int> CacheRetentionOptionsMinutes { get; } = new() { 15, 30, 60, 360, 1440 };

    public List<string> CacheRetentionOptionsText { get; } = new() { "15 分钟", "30 分钟", "1 小时", "6 小时", "24 小时" };

    [ObservableProperty]
    private int _selectedCacheRetentionIndex = 2; // 60 分钟

    [ObservableProperty]
    private string _launcherTitle = "Stardew Valley Launcher";

    [ObservableProperty]
    private string _launcherAppName = "SVL";

    [ObservableProperty]
    private int _selectedLauncherVisibilityIndex = 3; // 默认：游戏启动后最小化

    [ObservableProperty]
    private int _selectedWindowSizeModeIndex = 1; // 默认：默认

    [ObservableProperty]
    private int _customWindowWidth = 1280;

    [ObservableProperty]
    private int _customWindowHeight = 720;

    /// <summary>
    /// 当前选中的设置选项卡索引（0=基本设置, 1=API与账户, 2=个性化, 3=其他）
    /// </summary>
    [ObservableProperty]
    private int _activeTabIndex = 0;

    /// <summary>
    /// 切换到 API 与账户设置选项卡
    /// </summary>
    public void SwitchToApiTab()
    {
        ActiveTabIndex = 1;
        Log.Info("[SettingsViewModel] 切换到 API 与账户设置选项卡");
    }

    // ===== 默认下载源设置 =====

    /// <summary>
    /// SMAPI 默认下载源
    /// </summary>
    [ObservableProperty]
    private string _smapiDefaultSource = "全部";

    /// <summary>
    /// Mod 默认下载源
    /// </summary>
    [ObservableProperty]
    private string _modDefaultSource = "全部";

    [ObservableProperty]
    private string _localizationPreferredSource = "Gitee";

    /// <summary>
    /// Mod 更新检测并发线程数（1-16）
    /// </summary>
    [ObservableProperty]
    private int _maxConcurrentModUpdateChecks = 4;

    /// <summary>
    /// Mod 汉化检测并发线程数（1-16）
    /// </summary>
    [ObservableProperty]
    private int _maxConcurrentModLocalizationChecks = 4;

    /// <summary>
    /// SMAPI 默认下载源变化时保存到配置
    /// </summary>
    partial void OnSmapiDefaultSourceChanged(string value)
    {
        Log.Info($"[SettingsViewModel] SMAPI 默认下载源变更: {value}");
        if (_suppressDefaultSourceImmediateSave)
            return;

        SaveSettings();
    }

    /// <summary>
    /// Mod 默认下载源变化时保存到配置
    /// </summary>
    partial void OnModDefaultSourceChanged(string value)
    {
        Log.Info($"[SettingsViewModel] Mod 默认下载源变更: {value}");
        if (_suppressDefaultSourceImmediateSave)
            return;

        SaveSettings();
    }

    partial void OnLocalizationPreferredSourceChanged(string value)
    {
        Log.Info($"[SettingsViewModel] 社区本地化源变更: {value}");
        if (_suppressDefaultSourceImmediateSave)
            return;

        SaveSettings();
    }

    // ===== API 设置 =====

    [ObservableProperty]
    private string _nexusModsApiKey = string.Empty;

    /// <summary>
    /// NexusMods OAuth 登录状态
    /// </summary>
    [ObservableProperty]
    private bool _isNexusLoggedIn = false;

    [ObservableProperty]
    private bool _isNexusLoginExpired = false;

    public string NexusLoginStatusText => IsNexusLoginExpired ? "⚠ 请重新登录" : "✓ 已登录";

    public string NexusLoginActionText => IsNexusLoginExpired ? "重新登录" : "登出";

    /// <summary>
    /// NexusMods 用户名（OAuth 登录后）
    /// </summary>
    [ObservableProperty]
    private string _nexusUserName = string.Empty;

    /// <summary>
    /// NexusMods 会员类型（Premium/Free）
    /// </summary>
    [ObservableProperty]
    private string _nexusMembershipType = string.Empty;

    /// <summary>
    /// NexusMods 用户头像URL
    /// </summary>
    [ObservableProperty]
    private string _nexusAvatarUrl = string.Empty;

    /// <summary>
    /// NexusMods API 统计信息
    /// </summary>
    [ObservableProperty]
    private string _nexusApiStatistics = "未获取";

    /// <summary>
    /// NexusMods 每小时请求数
    /// </summary>
    [ObservableProperty]
    private string _nexusHourlyRequests = "-";

    /// <summary>
    /// NexusMods 每日请求数
    /// </summary>
    [ObservableProperty]
    private string _nexusDailyRequests = "-";

    // ===== 个性化设置 =====

    [ObservableProperty]
    private int _selectedThemeModeIndex = 2; // 默认：跟随系统

    [ObservableProperty]
    private string _primaryColor = "#7C4DFF";

    [ObservableProperty]
    private int _selectedLanguageIndex = 0; // 默认：简体中文

    [ObservableProperty]
    private bool _enableAnimations = true;

    [ObservableProperty]
    private bool _enableTransparency = true;

    [ObservableProperty]
    private int _selectedFontSizeIndex = 2; // 默认：14

    // ===== 主题风格 =====

    /// <summary>当前选中的主题风格（0=星露谷默认, 1=Material You）</summary>
    [ObservableProperty]
    private int _selectedThemeStyleIndex = 0;

    /// <summary>当前选中的 Material You 配色方案索引</summary>
    [ObservableProperty]
    private int _selectedColorSchemeIndex = 0;

    /// <summary>主题风格选项</summary>
    public List<string> ThemeStyleOptions { get; } = new() { "星露谷（默认）", "Material You" };

    /// <summary>Material You 配色方案列表</summary>
    public ObservableCollection<ColorSchemeItem> ColorSchemeItems { get; } = new();

    /// <summary>是否显示 Material You 配色选项</summary>
    public bool ShowMaterialYouOptions => SelectedThemeStyleIndex == 1;

    partial void OnSelectedThemeStyleIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ShowMaterialYouOptions));
        if (!_suppressThemeApplication)
            ApplySelectedTheme();
    }

    partial void OnSelectedColorSchemeIndexChanged(int value)
    {
        if (!_suppressThemeApplication && SelectedThemeStyleIndex == 1) // Material You
        {
            ApplySelectedTheme();
        }
    }

    /// <summary>通过点击配色方案卡片选择</summary>
    [RelayCommand]
    private void SelectColorScheme(ColorSchemeItem? item)
    {
        if (item == null) return;
        var idx = ColorSchemeItems.IndexOf(item);
        if (idx >= 0)
            SelectedColorSchemeIndex = idx;
    }

    /// <summary>应用当前选中的主题</summary>
    private void ApplySelectedTheme()
    {
        try
        {
            ThemeService.AnimateTransitions = EnableAnimations;

            if (SelectedThemeStyleIndex == 0)
            {
                ThemeService.ApplyStardewTheme();
            }
            else if (SelectedThemeStyleIndex == 1)
            {
                var schemes = Enum.GetValues(typeof(MaterialYouColorScheme)).Cast<MaterialYouColorScheme>().ToArray();
                var idx = Math.Max(0, Math.Min(SelectedColorSchemeIndex, schemes.Length - 1));
                ThemeService.ApplyMaterialYouTheme(schemes[idx]);
            }

            ThemeService.SaveToConfig();
            AutoSave();
        }
        catch (Exception ex)
        {
            Log.Warn($"[SettingsViewModel] 应用主题失败: {ex.Message}");
        }
    }

    /// <summary>初始化 Material You 配色方案列表</summary>
    private void InitializeColorSchemes()
    {
        ColorSchemeItems.Clear();
        foreach (MaterialYouColorScheme scheme in Enum.GetValues(typeof(MaterialYouColorScheme)))
        {
            ColorSchemeItems.Add(new ColorSchemeItem
            {
                Name = ThemeService.GetColorSchemeName(scheme),
                Scheme = scheme,
                PreviewColor = new SolidColorBrush(ThemeService.GetColorSchemePreviewColor(scheme))
            });
        }
    }

    // ===== 其他设置 =====

    [ObservableProperty]
    private bool _autoCheckUpdates = true;

    [ObservableProperty]
    private bool _minimizeToTrayOnStartup = false;

    [ObservableProperty]
    private bool _minimizeToTrayOnClose = false;

    [ObservableProperty]
    private bool _showNotifications = true;

    [ObservableProperty]
    private bool _showModTypeFilterDisabledNotice = true;

    [ObservableProperty]
    private int _selectedLogLevelIndex = 1; // 默认：Info

    [ObservableProperty]
    private bool _debugMode = false;

    // ===== 启动器更新设置 =====

    [ObservableProperty]
    private bool _checkPrereleaseUpdates = false;

    // ===== 选项列表（只读） =====

    public List<string> LauncherVisibilityOptions => _launcherVisibilityOptions;
    public List<string> WindowSizeModeOptions => _windowSizeModeOptions;
    public List<string> ThemeModeOptions => _themeModeOptions;
    public List<string> LogLevelOptions => _logLevelOptions;
    public List<string> LanguageOptions => _languageOptions;
    public List<string> FontSizeOptions => _fontSizeOptions;

    // ===== 命令 =====

    /// <summary>
    /// 保存设置命令
    /// </summary>
    [RelayCommand]
    private void SaveSettings()
    {
        try
        {
            // 获取当前设置作为基础
            var currentSettings = AppConfig.GetSettings();

            // 计算用户信息：只有在用户已登录（有 OAuth Token）时才保留
            string? userName = null;
            string? membershipType = null;
            string? avatarUrl = null;
            string? avatarLocalPath = null;

            if (!string.IsNullOrWhiteSpace(currentSettings.NexusModsOAuthToken))
            {
                // 用户已登录，保留用户信息
                userName = currentSettings.NexusModsOAuthUserName;
                membershipType = currentSettings.NexusModsOAuthMembershipType;
                avatarUrl = currentSettings.NexusModsOAuthAvatarUrl;
                avatarLocalPath = currentSettings.NexusModsOAuthAvatarLocalPath;
            }
            // 如果已登出（没有 OAuth Token），用户信息保持为 null

            var settings = new AppSettings
            {
                // 基本设置
                GameWindowTitle = GameWindowTitle,
                LauncherTitle = LauncherTitle,
                LauncherAppName = LauncherAppName,
                LauncherVisibility = (LauncherVisibilityBehavior)SelectedLauncherVisibilityIndex,
                WindowSizeMode = (WindowSizeMode)SelectedWindowSizeModeIndex,
                CustomWindowWidth = CustomWindowWidth,
                CustomWindowHeight = CustomWindowHeight,

                // 默认下载源
                SmapiDefaultSource = SmapiDefaultSource,
                ModDefaultSource = ModDefaultSource,
                LocalizationPreferredSource = LocalizationPreferredSource,
                MaxConcurrentModUpdateChecks = MaxConcurrentModUpdateChecks,
                MaxConcurrentModLocalizationChecks = MaxConcurrentModLocalizationChecks,

                // NexusMods
                EnableNexusModsSearchCache = EnableNexusModsSearchCache,
                EnableDownloadCache = EnableDownloadCache,

                // 缓存
                CacheRetentionMinutes = CacheRetentionMinutes,

                // API 设置
                NexusModsApiKey = string.IsNullOrWhiteSpace(NexusModsApiKey) ? null : NexusModsApiKey,

                // OAuth Token（不在此处保存，由登录流程保存）
                NexusModsOAuthToken = currentSettings.NexusModsOAuthToken,
                NexusModsOAuthRefreshToken = currentSettings.NexusModsOAuthRefreshToken,

                // 用户信息设置（通过 OAuth 登录后自动填充）
                NexusModsOAuthUserName = userName,
                NexusModsOAuthMembershipType = membershipType,
                NexusModsOAuthAvatarUrl = avatarUrl,
                NexusModsOAuthAvatarLocalPath = avatarLocalPath,

                // 个性化设置
                ThemeMode = (ThemeMode)SelectedThemeModeIndex,
                ThemeStyleName = ThemeService.CurrentStyle.ToString(),
                ThemeColorScheme = ThemeService.CurrentColorScheme.ToString(),
                PrimaryColor = PrimaryColor,
                Language = SelectedLanguageIndex == 0 ? "zh-CN" : "en-US",
                EnableAnimations = EnableAnimations,
                EnableTransparency = EnableTransparency,
                FontSize = int.Parse(FontSizeOptions[SelectedFontSizeIndex]),

                // 其他设置
                AutoCheckUpdates = AutoCheckUpdates,
                MinimizeToTrayOnStartup = MinimizeToTrayOnStartup,
                MinimizeToTrayOnClose = MinimizeToTrayOnClose,
                ShowNotifications = ShowNotifications,
                ShowModTypeFilterDisabledNotice = ShowModTypeFilterDisabledNotice,
                LogLevel = (LogLevel)SelectedLogLevelIndex,
                DebugMode = DebugMode,

                // 启动器更新设置
                AutoDownloadUpdate = AutoDownloadUpdate,
                ShowUpdateNotification = ShowUpdateNotification,
                PreferredUpdateSource = PreferredUpdateSourceIndex,
                CheckPrereleaseUpdates = CheckPrereleaseUpdates
            };

            var success = AppConfig.SaveSettings(settings);

            if (success)
            {
                StatusMessage = "✓ 设置已保存";
                Log.Info("[SettingsViewModel] 设置已保存");
            }
            else
            {
                StatusMessage = "✗ 保存失败";
                Log.Error("[SettingsViewModel] 保存设置失败");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"✗ 保存失败: {ex.Message}";
            Log.Error(ex, "[SettingsViewModel] 保存设置时发生错误");
        }
    }

    /// <summary>
    /// 清除缓存命令
    /// </summary>
    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        // 兼容旧 UI：清除缓存按钮等同于清除图片缓存
        await ClearImageCacheAsync();
    }

    /// <summary>
    /// 清除图片缓存命令
    /// </summary>
    [RelayCommand]
    private async Task ClearImageCacheAsync()
    {
        try
        {
            if (IsClearingCache)
                return;

            IsClearingCache = true;
            StatusMessage = "正在清除图片缓存...";

            await System.Threading.Tasks.Task.Run(() =>
            {
                var count = ImageCacheService.ClearCache();
                Log.Info($"[SettingsViewModel] 已清除图片缓存文件 {count} 个");
            });

            await UpdateCacheSizeAsync();
            StatusMessage = "✓ 图片缓存已清除";
        }
        catch (Exception ex)
        {
            StatusMessage = "清除失败";
            Log.Error(ex, "[SettingsViewModel] 清除图片缓存失败");
        }
        finally
        {
            IsClearingCache = false;
        }
    }

    /// <summary>
    /// 清除下载文件缓存命令
    /// </summary>
    [RelayCommand]
    private async Task ClearDownloadCacheAsync()
    {
        try
        {
            if (IsClearingCache)
                return;

            IsClearingCache = true;
            StatusMessage = "正在清除下载文件缓存...";

            // 先获取清除前的缓存数量
            var nexusBefore = SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsCacheService.GetCacheCount();
            var cfBefore = SVL.Core.IO.DownloadCacheService.GetCacheCount();

            await System.Threading.Tasks.Task.Run(async () =>
            {
                // 清除 NexusMods 下载缓存
                await SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsCacheService.ClearAsync();

                // 清除 Curseforge 下载缓存（MOD 和整合包）
                SVL.Core.IO.DownloadCacheService.ClearAllCache();
            });

            Log.Info($"[SettingsViewModel] 已清除 NexusMods 缓存文件 {nexusBefore} 个");
            Log.Info($"[SettingsViewModel] 已清除 Curseforge 下载缓存文件 {cfBefore} 个");

            await UpdateCacheSizeAsync();
            StatusMessage = "✓ 下载文件缓存已清除";
        }
        catch (Exception ex)
        {
            StatusMessage = "清除失败";
            Log.Error(ex, "[SettingsViewModel] 清除下载文件缓存失败");
        }
        finally
        {
            IsClearingCache = false;
        }
    }

    /// <summary>
    /// 清除搜索缓存命令（NexusMods / Curseforge / GitHub）
    /// </summary>
    [RelayCommand]
    private async Task ClearSearchCacheAsync()
    {
        try
        {
            if (IsClearingCache)
                return;

            IsClearingCache = true;
            StatusMessage = "正在清除搜索缓存...";

            await SVL.Core.IO.SearchCacheService.ClearSourceAsync("nexus");
            await SVL.Core.IO.SearchCacheService.ClearSourceAsync("curseforge");
            await SVL.Core.IO.SearchCacheService.ClearSourceAsync("github");

            await UpdateCacheSizeAsync();
            StatusMessage = "✓ 搜索缓存已清除";
        }
        catch (Exception ex)
        {
            StatusMessage = "清除失败";
            Log.Error(ex, "[SettingsViewModel] 清除搜索缓存失败");
        }
        finally
        {
            IsClearingCache = false;
        }
    }

    /// <summary>
    /// 按缓存时长清理过期缓存
    /// </summary>
    [RelayCommand]
    private async Task ClearExpiredCacheAsync()
    {
        try
        {
            if (IsClearingCache)
                return;

            IsClearingCache = true;
            StatusMessage = "正在清理过期缓存...";

            var retention = TimeSpan.FromMinutes(CacheRetentionMinutes <= 0 ? 60 : CacheRetentionMinutes);

            var searchCleanupSources = 3;
            var imageRemoved = 0;

            await System.Threading.Tasks.Task.Run(() =>
            {
                SVL.Core.IO.SearchCacheService.CleanupExpired("nexus", retention);
                SVL.Core.IO.SearchCacheService.CleanupExpired("curseforge", retention);
                SVL.Core.IO.SearchCacheService.CleanupExpired("github", retention);
                imageRemoved = SVL.Core.IO.ImageCacheService.CleanupExpired(retention);
            });

            var downloadRemoved = await SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsCacheService.CleanupExpiredAsync(retention);

            await UpdateCacheSizeAsync();

            var totalRemoved = imageRemoved + downloadRemoved;
            StatusMessage = $"✓ 已清理过期缓存（搜索缓存源 {searchCleanupSources} 个，文件 {totalRemoved} 项）";

            Log.Info($"[SettingsViewModel] 已清理过期缓存: 搜索源={searchCleanupSources}, 图片={imageRemoved}, 下载={downloadRemoved}");
        }
        catch (Exception ex)
        {
            StatusMessage = "清理失败";
            Log.Error(ex, "[SettingsViewModel] 清理过期缓存失败");
        }
        finally
        {
            IsClearingCache = false;
        }
    }

    /// <summary>
    /// 清除 NexusMods 搜索缓存命令（搜索记录缓存）
    /// </summary>
    [RelayCommand]
    private async Task ClearNexusModsSearchCacheAsync()
    {
        try
        {
            if (IsClearingNexusModsSearchCache)
                return;

            IsClearingNexusModsSearchCache = true;
            StatusMessage = "正在清除 NexusMods 搜索缓存...";

            await SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsService.ClearCacheAsync();
            await SVL.Core.IO.SearchCacheService.ClearSourceAsync("nexus");

            await UpdateCacheSizeAsync();

            StatusMessage = "✓ NexusMods 搜索缓存已清除";
            Log.Info("[SettingsViewModel] NexusMods 搜索缓存已清除");
        }
        catch (Exception ex)
        {
            StatusMessage = "清除失败";
            Log.Error(ex, "[SettingsViewModel] 清除 NexusMods 搜索缓存失败");
        }
        finally
        {
            IsClearingNexusModsSearchCache = false;
        }
    }

    /// <summary>
    /// 打开缓存文件夹命令
    /// </summary>
    [RelayCommand]
    private void OpenCacheFolder()
    {
        try
        {
            var cachePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SVL");
            if (!Directory.Exists(cachePath))
            {
                Directory.CreateDirectory(cachePath);
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = cachePath,
                UseShellExecute = true
            });
            StatusMessage = "✓ 已打开缓存文件夹";
            Log.Info($"[SettingsViewModel] 已打开缓存文件夹: {cachePath}");
        }
        catch (Exception ex)
        {
            StatusMessage = "打开缓存文件夹失败";
            Log.Error(ex, "[SettingsViewModel] 打开缓存文件夹失败");
            SvlMessageBox.Warning($"无法打开缓存文件夹：{ex.Message}", "打开失败");
        }
    }

    /// <summary>
    /// 更新缓存大小
    /// </summary>
    private async Task UpdateCacheSizeAsync()
    {
        try
        {
            Log.Debug($"[Settings] SearchCacheService.IsEnabled = {SVL.Core.IO.SearchCacheService.IsEnabled}, EnableNexusModsSearchCache = {EnableNexusModsSearchCache}");

            var retention = TimeSpan.FromMinutes(CacheRetentionMinutes <= 0 ? 60 : CacheRetentionMinutes);

            var snapshot = await System.Threading.Tasks.Task.Run(() =>
            {
                // 仅统计缓存，不在刷新视图时执行清理，避免误删其他缓存

                // 图片缓存
                var imageBytes = SVL.Core.IO.ImageCacheService.GetCacheSize();
                var imageCount = SVL.Core.IO.ImageCacheService.GetCacheCount();

                // 下载缓存（Nexus 下载 ZIP）
                var nexusMb = SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsCacheService.GetCacheSize();
                var nexusBytes = (long)(nexusMb * 1024 * 1024);
                var nexusCount = SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsCacheService.GetCacheCount();

                // Curseforge 下载缓存（MOD 和整合包）
                var curseforgeBytes = SVL.Core.IO.DownloadCacheService.GetCacheSize();
                var curseforgeCount = SVL.Core.IO.DownloadCacheService.GetCacheCount();

                // 合并所有下载缓存
                var downloadBytes = nexusBytes + curseforgeBytes;
                var downloadCount = nexusCount + curseforgeCount;

                // 搜索缓存条数（使用不同变量名避免与下载缓存冲突）
                var nexusSearchCount = SVL.Core.IO.SearchCacheService.GetEntryCount("nexus", retention);
                var cfSearchCount = SVL.Core.IO.SearchCacheService.GetEntryCount("curseforge", retention);
                var ghSearchCount = SVL.Core.IO.SearchCacheService.GetEntryCount("github", retention);

                Log.Debug($"[Settings] 搜索缓存统计: nexus={nexusSearchCount}, curseforge={cfSearchCount}, github={ghSearchCount}");

                return (imageBytes, imageCount, downloadBytes, downloadCount, nexusSearchCount, cfSearchCount, ghSearchCount);
            });

            CacheSize = FormatBytes(snapshot.imageBytes);
            ImageCacheCount = snapshot.imageCount;
            DownloadCacheSize = FormatBytes(snapshot.downloadBytes);
            DownloadCacheCount = snapshot.downloadCount;
            NexusSearchCacheCount = snapshot.nexusSearchCount;
            CurseforgeSearchCacheCount = snapshot.cfSearchCount;
            GithubSearchCacheCount = snapshot.ghSearchCount;
            TotalSearchCacheCount = snapshot.nexusSearchCount + snapshot.cfSearchCount + snapshot.ghSearchCount;

            Log.Debug($"[Settings] 搜索缓存总计: {TotalSearchCacheCount} 条");

            // 圆环角度 - 三个部分：图片缓存、下载文件缓存、搜索缓存
            // 搜索缓存按条数计算，每条按1KB权重计算
            var searchWeight = TotalSearchCacheCount * 1024L;
            var total = snapshot.imageBytes + snapshot.downloadBytes + searchWeight;

            if (total <= 0)
            {
                ImageCacheStartAngle = -90;
                ImageCacheSweepAngle = 0;
                DownloadCacheStartAngle = -90;
                DownloadCacheSweepAngle = 0;
                SearchCacheStartAngle = -90;
                SearchCacheSweepAngle = 0;
            }
            else
            {
                var imageSweep = 360.0 * snapshot.imageBytes / total;
                var downloadSweep = 360.0 * snapshot.downloadBytes / total;
                var searchSweep = 360.0 * searchWeight / total;

                ImageCacheStartAngle = -90;
                ImageCacheSweepAngle = imageSweep;
                DownloadCacheStartAngle = -90 + imageSweep;
                DownloadCacheSweepAngle = downloadSweep;
                SearchCacheStartAngle = -90 + imageSweep + downloadSweep;
                SearchCacheSweepAngle = searchSweep;
            }

            // 搜索缓存来源分布圆环角度
            var totalSearch = TotalSearchCacheCount;
            if (totalSearch <= 0)
            {
                NexusSearchStartAngle = -90;
                NexusSearchSweepAngle = 0;
                CurseforgeSearchStartAngle = -90;
                CurseforgeSearchSweepAngle = 0;
                GithubSearchStartAngle = -90;
                GithubSearchSweepAngle = 0;
            }
            else
            {
                var nexusSweep = 360.0 * snapshot.nexusSearchCount / totalSearch;
                var cfSweep = 360.0 * snapshot.cfSearchCount / totalSearch;
                var ghSweep = 360.0 * snapshot.ghSearchCount / totalSearch;

                NexusSearchStartAngle = -90;
                NexusSearchSweepAngle = nexusSweep;
                CurseforgeSearchStartAngle = -90 + nexusSweep;
                CurseforgeSearchSweepAngle = cfSweep;
                GithubSearchStartAngle = -90 + nexusSweep + cfSweep;
                GithubSearchSweepAngle = ghSweep;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SettingsViewModel] 更新缓存大小失败");
            CacheSize = "未知";
            DownloadCacheSize = "未知";
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;

        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }

        return $"{len:0.##} {sizes[order]}";
    }

    /// <summary>
    /// 重置设置命令
    /// </summary>
    [RelayCommand]
    private void ResetSettings()
    {
        // 重置为默认值
        GameWindowTitle = "<default>";
        LauncherTitle = "Stardew Valley Launcher";
        LauncherAppName = "SVL";
        SelectedLauncherVisibilityIndex = 3;
        SelectedWindowSizeModeIndex = 1;
        CustomWindowWidth = 1280;
        CustomWindowHeight = 720;

        NexusModsApiKey = string.Empty;

        SelectedThemeModeIndex = 2;
        PrimaryColor = "#7C4DFF";
        SelectedLanguageIndex = 0;
        EnableAnimations = true;
        EnableTransparency = true;
        SelectedFontSizeIndex = 2;

        AutoCheckUpdates = true;
        MinimizeToTrayOnStartup = false;
        MinimizeToTrayOnClose = false;
        ShowNotifications = true;
        SelectedLogLevelIndex = 1;
        DebugMode = false;

        StatusMessage = "✓ 设置已重置为默认值";
        Log.Info("[SettingsViewModel] 设置已重置");
    }

    /// <summary>
    /// 测试 NexusMods API 命令
    /// </summary>
    [RelayCommand]
    private async Task TestNexusModsApiAsync()
    {
        try
        {
            NexusModsApiStatus = "正在测试...";

            // 使用 OAuth Token 验证
            var isValid = await SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsClient.ValidateAccessTokenAsync();

            if (isValid)
            {
                NexusModsApiStatus = "✓ Access Token 有效";
                Log.Info("[SettingsViewModel] NexusMods Access Token 验证成功");
            }
            else
            {
                NexusModsApiStatus = "✗ Access Token 无效或未登录，请先通过 OAuth 登录";
                Log.Warn("[SettingsViewModel] NexusMods Access Token 验证失败");
            }
        }
        catch (Exception ex)
        {
            NexusModsApiStatus = $"✗ 验证失败: {ex.Message}";
            Log.Error(ex, "[SettingsViewModel] NexusMods Access Token 验证失败");
        }
    }


    /// <summary>
    /// 刷新 NexusMods OAuth 登录状态（使用 Access Token）
    /// </summary>
    public void RefreshNexusLoginStatus()
    {
        try
        {
            var settings = AppConfig.GetSettings();
            var hasToken = !string.IsNullOrEmpty(settings.NexusModsOAuthToken);
            var hasUserProfile = !string.IsNullOrWhiteSpace(settings.NexusModsOAuthUserName)
                                 || !string.IsNullOrWhiteSpace(settings.NexusModsOAuthMembershipType)
                                 || !string.IsNullOrWhiteSpace(settings.NexusModsOAuthAvatarUrl)
                                 || !string.IsNullOrWhiteSpace(settings.NexusModsOAuthAvatarLocalPath);

            IsNexusLoginExpired = !hasToken && hasUserProfile;
            IsNexusLoggedIn = hasToken || IsNexusLoginExpired;

            if (IsNexusLoggedIn)
            {
                NexusUserName = settings.NexusModsOAuthUserName ?? string.Empty;
                NexusMembershipType = settings.NexusModsOAuthMembershipType ?? "Free";

                // 优先使用本地缓存头像（用户信息存储在 OAuth* 字段中）
                var localAvatar = settings.NexusModsOAuthAvatarLocalPath;
                if (!string.IsNullOrEmpty(localAvatar) && System.IO.File.Exists(localAvatar))
                {
                    // 本地文件需要使用 file:// 协议
                    NexusAvatarUrl = new Uri(localAvatar).AbsoluteUri;
                    Log.Info($"[SettingsViewModel] 使用本地缓存的头像: {localAvatar}");
                }
                else
                {
                    // 如果本地缓存不存在，使用在线URL
                    NexusAvatarUrl = settings.NexusModsOAuthAvatarUrl ?? string.Empty;

                    // 如果有在线URL，后台下载缓存
                    if (!string.IsNullOrEmpty(settings.NexusModsOAuthAvatarUrl) && !string.IsNullOrEmpty(settings.NexusModsOAuthUserName))
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var cachedPath = await AvatarCacheService.DownloadAndCacheAvatarAsync(
                                    settings.NexusModsOAuthAvatarUrl,
                                    settings.NexusModsOAuthUserName);

                                if (!string.IsNullOrEmpty(cachedPath))
                                {
                                    var updatedSettings = AppConfig.GetSettings();
                                    updatedSettings.NexusModsOAuthAvatarLocalPath = cachedPath;
                                    AppConfig.SaveSettings(updatedSettings);

                                    // 在 UI 线程更新头像显示
                                    System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                                    {
                                        NexusAvatarUrl = new Uri(cachedPath).AbsoluteUri;
                                    }));
                                }
                            }
                            catch (Exception ex)
                            {
                                Log.Warn("[SettingsViewModel] 后台下载头像失败", ex);
                            }
                        });
                    }
                }

                // 仅在 Access Token 有效时刷新统计
                if (hasToken)
                {
                    RefreshNexusStatistics();
                }
                else
                {
                    NexusHourlyRequests = "-";
                    NexusDailyRequests = "-";
                    NexusApiStatistics = "Access Token 已过期";
                }

                Log.Info("[SettingsViewModel] NexusMods OAuth 登录状态已刷新");
            }
            else
            {
                NexusUserName = string.Empty;
                NexusMembershipType = string.Empty;
                NexusAvatarUrl = string.Empty;
                NexusHourlyRequests = "-";
                NexusDailyRequests = "-";
                NexusApiStatistics = "未获取";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SettingsViewModel] 刷新 NexusMods 登录状态失败");
        }
    }

    /// <summary>
    /// 刷新 NexusMods API 统计信息
    /// </summary>
    public void RefreshNexusStatistics()
    {
        try
        {
            var rateLimit = SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsClient.RateLimit;
            if (rateLimit.IsInitialized)
            {
                var hourlyUsed = rateLimit.HourlyLimit - rateLimit.HourlyRemaining;
                NexusHourlyRequests = $"{hourlyUsed}/{rateLimit.HourlyLimit}";

                if (rateLimit.DailyLimit > 0)
                {
                    var dailyUsed = rateLimit.DailyLimit - rateLimit.DailyRemaining;
                    NexusDailyRequests = $"{dailyUsed}/{rateLimit.DailyLimit}";
                }
                else
                {
                    NexusDailyRequests = "-";
                }

                NexusApiStatistics = rateLimit.GetStatusText();
                Log.Info($"[SettingsViewModel] NexusMods 统计已刷新: {NexusApiStatistics}");
            }
            else
            {
                NexusHourlyRequests = "-";
                NexusDailyRequests = "-";
                NexusApiStatistics = "未获取";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SettingsViewModel] 刷新 NexusMods 统计信息失败");
            NexusHourlyRequests = "-";
            NexusDailyRequests = "-";
            NexusApiStatistics = "获取失败";
        }
    }

    /// <summary>
    /// NexusMods OAuth 登出（清除 Access Token）
    /// </summary>
    public void NexusLogout()
    {
        try
        {
            var settings = AppConfig.GetSettings();

            // 清除 OAuth Token
            settings.NexusModsOAuthToken = null;
            settings.NexusModsOAuthRefreshToken = null;
            // 清除用户信息
            settings.NexusModsOAuthUserName = null;
            settings.NexusModsOAuthMembershipType = null;
            settings.NexusModsOAuthAvatarUrl = null;
            settings.NexusModsOAuthAvatarLocalPath = null;

            // 保存配置（这会清除用户信息）
            AppConfig.SaveSettings(settings);
            AppConfig.ClearCache();

            // 重置状态
            IsNexusLoggedIn = false;
            IsNexusLoginExpired = false;
            NexusUserName = string.Empty;
            NexusMembershipType = string.Empty;
            NexusAvatarUrl = string.Empty;
            NexusHourlyRequests = "-";
            NexusDailyRequests = "-";
            NexusApiStatistics = "未获取";

            StatusMessage = "已登出 NexusMods";
            Log.Info("[SettingsViewModel] NexusMods OAuth 已登出");

            // 不要调用 SaveSettings()，因为会重新保存用户信息
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SettingsViewModel] NexusMods 登出失败");
            StatusMessage = "登出失败";
        }
    }

    partial void OnIsNexusLoginExpiredChanged(bool value)
    {
        OnPropertyChanged(nameof(NexusLoginStatusText));
        OnPropertyChanged(nameof(NexusLoginActionText));
    }
    /// <summary>
    /// 显示窗口标题占位符帮助
    /// </summary>
    [RelayCommand]
    private void ShowWindowTitleHelp()
    {
        try
        {
            var dialog = new SVL.Desktop.Controls.WindowTitleHelpDialog
            {
                Owner = System.Windows.Application.Current.MainWindow
            };

            dialog.LoadPlaceholders();
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SettingsViewModel] 显示帮助对话框失败");
        }
    }

    // ===== 状态属性 =====

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>
    /// 应用版本号
    /// </summary>
    [ObservableProperty]
    private string _appVersion;

    [ObservableProperty]
    private string _nexusModsApiStatus = string.Empty;

    [ObservableProperty]
    private string _nxmProtocolStatus = "检查中...";

    // ===== 启动器更新 =====

    /// <summary>
    /// 最新版本号
    /// </summary>
    [ObservableProperty]
    private string _latestVersion = "检查中...";

    /// <summary>
    /// 更新源名称
    /// </summary>
    [ObservableProperty]
    private string _updateSource = "GitHub";

    /// <summary>
    /// 更新状态消息
    /// </summary>
    [ObservableProperty]
    private string _updateStatusMessage = string.Empty;

    /// <summary>
    /// 是否正在检查更新
    /// </summary>
    [ObservableProperty]
    private bool _isCheckingUpdate = false;

    /// <summary>
    /// 有新版本时自动下载更新
    /// </summary>
    [ObservableProperty]
    private bool _autoDownloadUpdate = false;

    /// <summary>
    /// 有新版本时显示提示
    /// </summary>
    [ObservableProperty]
    private bool _showUpdateNotification = true;

    /// <summary>
    /// 首选更新源索引 (0=GitHub, 1=Gitee)
    /// </summary>
    [ObservableProperty]
    private int _preferredUpdateSourceIndex = 0;

    /// <summary>
    /// 是否有可用更新
    /// </summary>
    [ObservableProperty]
    private bool _hasUpdateAvailable = false;

    /// <summary>
    /// 最新版本的下载 URL
    /// </summary>
    [ObservableProperty]
    private string _latestReleaseUrl = string.Empty;

    /// <summary>
    /// 最新版本信息（用于显示更新对话框）
    /// </summary>
    private Core.App.ReleaseInfo? _latestReleaseInfo;

    /// <summary>
    /// 更新按钮文本（根据是否有更新变化）
    /// </summary>
    [ObservableProperty]
    private string _updateButtonText = "检查更新";

    // ===== 辅助方法 =====

    /// <summary>
    /// 打开赞助页面
    /// </summary>
    [RelayCommand]
    private void Sponsor()
    {
        try
        {
            ProcessEx.OpenUrl("https://ifdian.net/a/mcshengxia");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SettingsViewModel] 打开赞助页面失败");
            StatusMessage = "打开赞助页面失败";
        }
    }

    /// <summary>
    /// 打开源码页面
    /// </summary>
    [RelayCommand]
    private void ViewSource()
    {
        try
        {
            ProcessEx.OpenUrl("https://github.com/panda-lsy/SVL-StardewValleyLauncher");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SettingsViewModel] 打开源码页面失败");
            StatusMessage = "打开源码页面失败";
        }
    }

    /// <summary>
    /// 检查启动器更新
    /// </summary>
    [RelayCommand]
    private async Task CheckUpdate()
    {
        if (IsCheckingUpdate) return;

        try
        {
            IsCheckingUpdate = true;
            UpdateStatusMessage = "正在检查更新...";

            var preferGitee = PreferredUpdateSourceIndex == 1;
            var result = await LauncherUpdateService.CheckForUpdateAsync(preferGitee, CheckPrereleaseUpdates);

            if (result.Success)
            {
                LatestVersion = result.LatestVersion.ToString();
                UpdateSource = result.Source;
                HasUpdateAvailable = result.HasUpdate;
                _latestReleaseInfo = result.ReleaseInfo;

                if (result.HasUpdate)
                {
                    UpdateStatusMessage = $"发现新版本 {result.LatestVersion}！";
                    LatestReleaseUrl = result.ReleaseInfo?.HtmlUrl ?? "";

                    // 自动显示更新对话框
                    if (result.ReleaseInfo != null)
                    {
                        await ShowUpdateDialogAsync();
                    }
                }
                else
                {
                    UpdateStatusMessage = "已是最新版本";
                }

                Log.Info($"[SettingsViewModel] 检查更新完成: 当前={result.CurrentVersion}, 最新={result.LatestVersion}, 有更新={result.HasUpdate}");
            }
            else
            {
                UpdateStatusMessage = result.ErrorMessage ?? "检查更新失败";
                Log.Warn($"[SettingsViewModel] 检查更新失败: {result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            UpdateStatusMessage = $"检查更新失败: {ex.Message}";
            Log.Error(ex, "[SettingsViewModel] 检查更新异常");
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    /// <summary>
    /// 打开更新对话框（当有更新可用时）
    /// </summary>
    [RelayCommand]
    private async Task ShowUpdateDialog()
    {
        if (_latestReleaseInfo == null)
        {
            // 如果没有缓存的版本信息，重新检查更新
            await CheckUpdate();
            return;
        }

        await ShowUpdateDialogAsync();
    }

    /// <summary>
    /// 显示更新对话框的内部方法
    /// </summary>
    private async Task ShowUpdateDialogAsync()
    {
        if (_latestReleaseInfo == null) return;

        try
        {
            // 标记本次启动已显示过更新弹窗，防止启动时再次自动检测
            App.MarkUpdateDialogShown();

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var currentVersion = LauncherUpdateService.CurrentVersion;

                var dialog = new Controls.UpdateDialog(currentVersion, _latestReleaseInfo)
                {
                    Owner = Application.Current.MainWindow
                };

                dialog.ShowDialog();
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SettingsViewModel] 显示更新对话框失败");
        }
    }

    /// <summary>
    /// 打开最新版本页面
    /// </summary>
    [RelayCommand]
    private void OpenLatestRelease()
    {
        if (!string.IsNullOrEmpty(LatestReleaseUrl))
        {
            ProcessEx.OpenUrl(LatestReleaseUrl);
        }
    }

    /// <summary>
    /// 从配置文件加载设置
    /// </summary>
    private void LoadSettings()
    {
        try
        {
            var settings = AppConfig.GetSettings();

            // 基本设置
            GameWindowTitle = settings.GameWindowTitle ?? "<default>";
            LauncherTitle = settings.LauncherTitle ?? "Stardew Valley Launcher";
            LauncherAppName = settings.LauncherAppName ?? "SVL";
            SelectedLauncherVisibilityIndex = (int)settings.LauncherVisibility;
            SelectedWindowSizeModeIndex = (int)settings.WindowSizeMode;
            CustomWindowWidth = settings.CustomWindowWidth;
            CustomWindowHeight = settings.CustomWindowHeight;

            // API 设置
            NexusModsApiKey = settings.NexusModsApiKey ?? string.Empty;

            // 个性化设置
            SelectedThemeModeIndex = (int)settings.ThemeMode;
            PrimaryColor = settings.PrimaryColor ?? "#7C4DFF";
            SelectedLanguageIndex = settings.Language == "zh-CN" ? 0 : 1;
            EnableAnimations = settings.EnableAnimations;
            EnableTransparency = settings.EnableTransparency;
            SelectedFontSizeIndex = FontSizeOptions.IndexOf(settings.FontSize.ToString());
            if (SelectedFontSizeIndex < 0) SelectedFontSizeIndex = 2;

            // 主题风格（先抑制自动应用，等两个索引都设好后再统一应用）
            _suppressThemeApplication = true;
            if (Enum.TryParse<ThemeStyle>(settings.ThemeStyleName ?? "Stardew", out var themeStyle))
            {
                SelectedThemeStyleIndex = themeStyle == ThemeStyle.MaterialYou ? 1 : 0;
            }
            if (Enum.TryParse<MaterialYouColorScheme>(settings.ThemeColorScheme ?? "Blue", out var colorScheme))
            {
                var schemes = Enum.GetValues(typeof(MaterialYouColorScheme)).Cast<MaterialYouColorScheme>().ToArray();
                SelectedColorSchemeIndex = Array.IndexOf(schemes, colorScheme);
                if (SelectedColorSchemeIndex < 0) SelectedColorSchemeIndex = 0;
            }
            _suppressThemeApplication = false;
            ApplySelectedTheme();

            // 其他设置
            AutoCheckUpdates = settings.AutoCheckUpdates;
            MinimizeToTrayOnStartup = settings.MinimizeToTrayOnStartup;
            MinimizeToTrayOnClose = settings.MinimizeToTrayOnClose;
            ShowNotifications = settings.ShowNotifications;
            ShowModTypeFilterDisabledNotice = settings.ShowModTypeFilterDisabledNotice;
            SelectedLogLevelIndex = (int)settings.LogLevel;
            DebugMode = settings.DebugMode;

            // 启动器更新设置
            AutoDownloadUpdate = settings.AutoDownloadUpdate;
            ShowUpdateNotification = settings.ShowUpdateNotification;
            PreferredUpdateSourceIndex = settings.PreferredUpdateSource;
            CheckPrereleaseUpdates = settings.CheckPrereleaseUpdates;

            // 默认下载源（加载时不要触发立即保存）
            _suppressDefaultSourceImmediateSave = true;
            SmapiDefaultSource = settings.SmapiDefaultSource ?? "全部";
            ModDefaultSource = settings.ModDefaultSource ?? "全部";
            LocalizationPreferredSource = string.IsNullOrWhiteSpace(settings.LocalizationPreferredSource) ? "Gitee" : settings.LocalizationPreferredSource;
            MaxConcurrentModUpdateChecks = Math.Max(1, Math.Min(16, settings.MaxConcurrentModUpdateChecks));
            MaxConcurrentModLocalizationChecks = Math.Max(1, Math.Min(16, settings.MaxConcurrentModLocalizationChecks));
            _suppressDefaultSourceImmediateSave = false;

            // NexusMods
            EnableNexusModsSearchCache = settings.EnableNexusModsSearchCache;
            EnableDownloadCache = settings.EnableDownloadCache;

            // 缓存时长
            CacheRetentionMinutes = settings.CacheRetentionMinutes <= 0 ? 60 : settings.CacheRetentionMinutes;
            var idx = CacheRetentionOptionsMinutes.IndexOf(CacheRetentionMinutes);
            SelectedCacheRetentionIndex = idx >= 0 ? idx : 2;

            ApplyCacheSettingsToRuntime();

            ThemeService.AnimateTransitions = EnableAnimations;
            ThemeService.SetThemeMode((ThemeMode)SelectedThemeModeIndex);
            ThemeService.SetTransparencyEnabled(EnableTransparency);
            ApplyFontSizeToRuntime();
            ApplyLanguageToRuntime();

            Log.Info("[SettingsViewModel] ✓ 已加载设置");

            // 刷新 OAuth 登录状态
            RefreshNexusLoginStatus();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SettingsViewModel] 加载设置失败");
            StatusMessage = "加载设置失败";
        }

        // 检查 NXM 协议状态
        CheckNxmProtocolStatus();
    }

    /// <summary>
    /// 检查 NXM 协议注册状态
    /// </summary>
    public void CheckNxmProtocolStatus()
    {
        try
        {
            var isRegistered = SVL.Core.App.NxmProtocolService.IsProtocolRegistered();
            NxmProtocolStatus = isRegistered ? "✓ 已注册" : "✗ 未注册";

            Log.Info($"[SettingsViewModel] NXM 协议状态: {NxmProtocolStatus}");
        }
        catch (Exception ex)
        {
            Log.Warn("[SettingsViewModel] 检查 NXM 协议状态失败", ex);
            NxmProtocolStatus = "检查失败";
        }
    }

    /// <summary>
    /// 属性变更时自动保存
    /// </summary>
    partial void OnGameWindowTitleChanged(string value)
    {
        AutoSave();
    }

    partial void OnLauncherTitleChanged(string value)
    {
        AutoSave();
        // 触发热重载事件
        try
        {
            LauncherConfigService.NotifyLauncherTitleChanged(value);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SettingsViewModel] Failed to notify launcher title change");
        }
    }

    partial void OnEnableNexusModsSearchCacheChanged(bool value)
    {
        AutoSave();
        ApplyCacheSettingsToRuntime();
    }

    partial void OnEnableDownloadCacheChanged(bool value)
    {
        AutoSave();
    }

    partial void OnSelectedCacheRetentionIndexChanged(int value)
    {
        if (value >= 0 && value < CacheRetentionOptionsMinutes.Count)
        {
            CacheRetentionMinutes = CacheRetentionOptionsMinutes[value];
        }

        AutoSave();
        ApplyCacheSettingsToRuntime();
    }

    private void ApplyCacheSettingsToRuntime()
    {
        try
        {
            SVL.Core.IO.SearchCacheService.IsEnabled = EnableNexusModsSearchCache;
            var minutes = CacheRetentionMinutes <= 0 ? 60 : CacheRetentionMinutes;
            SVL.Core.IO.SearchCacheService.DefaultTtl = TimeSpan.FromMinutes(minutes);
        }
        catch (Exception ex)
        {
            Log.Warn("[SettingsViewModel] 应用缓存设置到运行时失败", ex);
        }
    }

    partial void OnLauncherAppNameChanged(string value)
    {
        AutoSave();
        // 触发热重载事件
        try
        {
            LauncherConfigService.NotifyLauncherAppNameChanged(value);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SettingsViewModel] Failed to notify launcher app name change");
        }
    }

    partial void OnSelectedLauncherVisibilityIndexChanged(int value)
    {
        AutoSave();
    }

    partial void OnSelectedWindowSizeModeIndexChanged(int value)
    {
        AutoSave();
    }

    partial void OnCustomWindowWidthChanged(int value)
    {
        AutoSave();
    }

    partial void OnCustomWindowHeightChanged(int value)
    {
        AutoSave();
    }

    partial void OnNexusModsApiKeyChanged(string value)
    {
        AutoSave();
        // NexusMods 使用 OAuth 2.0 PKCE 认证
        // API Key 字段已弃用，仅保留以保持兼容性
        // 此方法用于检测 API Key 变化并触发自动保存
        if (!string.IsNullOrWhiteSpace(value))
        {
            Log.Info("[Settings] NexusMods API Key 已更新（已弃用）");
        }
    }

    partial void OnSelectedThemeModeIndexChanged(int value)
    {
        ThemeService.SetThemeMode((ThemeMode)value);
        AutoSave();
    }

    partial void OnPrimaryColorChanged(string value)
    {
        AutoSave();
    }

    partial void OnSelectedLanguageIndexChanged(int value)
    {
        ApplyLanguageToRuntime();
        AutoSave();
    }

    partial void OnEnableAnimationsChanged(bool value)
    {
        ThemeService.AnimateTransitions = value;
        AutoSave();
    }

    partial void OnEnableTransparencyChanged(bool value)
    {
        ThemeService.SetTransparencyEnabled(value);
        AutoSave();
    }

    partial void OnSelectedFontSizeIndexChanged(int value)
    {
        ApplyFontSizeToRuntime();
        AutoSave();
    }

    private void ApplyFontSizeToRuntime()
    {
        if (SelectedFontSizeIndex < 0 || SelectedFontSizeIndex >= FontSizeOptions.Count)
            return;

        if (!double.TryParse(FontSizeOptions[SelectedFontSizeIndex], out var size))
            return;

        Application.Current.Resources["GlobalFontSize"] = size;
        if (Application.Current.MainWindow != null)
            Application.Current.MainWindow.FontSize = size;
    }

    private void ApplyLanguageToRuntime()
    {
        var cultureName = SelectedLanguageIndex == 0 ? "zh-CN" : "en-US";
        var culture = new CultureInfo(cultureName);

        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        if (Application.Current.MainWindow != null)
            Application.Current.MainWindow.Language = XmlLanguage.GetLanguage(culture.IetfLanguageTag);

        StatusMessage = cultureName == "zh-CN"
            ? "✓ 语言已切换（部分界面可能需要重启后完全生效）"
            : "✓ Language switched (some UI text may require restart)";
    }

    partial void OnAutoCheckUpdatesChanged(bool value)
    {
        AutoSave();
    }

    partial void OnMinimizeToTrayOnStartupChanged(bool value)
    {
        AutoSave();
    }

    partial void OnMinimizeToTrayOnCloseChanged(bool value)
    {
        AutoSave();
    }

    partial void OnShowNotificationsChanged(bool value)
    {
        AutoSave();
    }

    partial void OnShowModTypeFilterDisabledNoticeChanged(bool value)
    {
        AutoSave();
    }

    partial void OnMaxConcurrentModUpdateChecksChanged(int value)
    {
        if (_suppressDefaultSourceImmediateSave)
            return;

        if (value < 1)
        {
            MaxConcurrentModUpdateChecks = 1;
            return;
        }

        if (value > 16)
        {
            MaxConcurrentModUpdateChecks = 16;
            return;
        }

        AutoSave();
    }

    partial void OnMaxConcurrentModLocalizationChecksChanged(int value)
    {
        if (_suppressDefaultSourceImmediateSave)
            return;

        if (value < 1)
        {
            MaxConcurrentModLocalizationChecks = 1;
            return;
        }

        if (value > 16)
        {
            MaxConcurrentModLocalizationChecks = 16;
            return;
        }

        AutoSave();
    }

    [RelayCommand]
    private void OpenLocalizationContributionPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://svl.qzz.io/contribute.html",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Warn("[SettingsViewModel] 打开本地化贡献页面失败", ex);
            StatusMessage = $"无法打开贡献页面: {ex.Message}";
        }
    }

    partial void OnSelectedLogLevelIndexChanged(int value)
    {
        AutoSave();
    }

    partial void OnDebugModeChanged(bool value)
    {
        AutoSave();
    }

    partial void OnAutoDownloadUpdateChanged(bool value)
    {
        AutoSave();
    }

    partial void OnShowUpdateNotificationChanged(bool value)
    {
        AutoSave();
    }

    partial void OnPreferredUpdateSourceIndexChanged(int value)
    {
        // 使用 Dispatcher 延迟执行，避免快速切换时 WPF 样式系统崩溃
        // 这是 WPF ComboBox 快速切换时的已知问题 (NullReferenceException in StyleHelper)
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            // 立即更新显示的更新源名称
            UpdateSource = value == 1 ? "Gitee" : "GitHub";
            
            // 清除新选择源的缓存，确保从该源重新获取
            LauncherUpdateService.ClearCache(preferGitee: value == 1);
            
            // 重置更新状态，让用户用新源重新检查
            HasUpdateAvailable = false;
            _latestReleaseInfo = null;
            UpdateStatusMessage = "切换更新源后请点击检查更新";
            
            // 立即保存设置（不使用延迟，确保检查更新时配置已保存）
            SaveSettings();
        }), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    partial void OnCheckPrereleaseUpdatesChanged(bool value)
    {
        // 清除缓存，确保使用新的 prerelease 设置重新获取
        LauncherUpdateService.ClearAllCache();
        AutoSave();
    }

    /// <summary>
    /// 自动保存设置（延迟 1000ms，防抖）
    /// </summary>
    private async void AutoSave()
    {
        try
        {
            // 取消之前的自动保存任务
            _autoSaveCts?.Cancel();
            _autoSaveCts = new System.Threading.CancellationTokenSource();

            // 延迟 1000ms 后保存（防抖）
            await Task.Delay(1000, _autoSaveCts.Token);

            SaveSettings();
        }
        catch (TaskCanceledException)
        {
            // 用户继续输入，取消保存
        }
        catch
        {
            // 忽略自动保存错误
        }
    }

    /// <summary>
    /// 当 HasUpdateAvailable 变化时更新按钮文本
    /// </summary>
    partial void OnHasUpdateAvailableChanged(bool value)
    {
        UpdateButtonText = value ? "查看更新" : "检查更新";
    }
}

/// <summary>
/// Material You 配色方案项（用于 UI 列表绑定）
/// </summary>
public class ColorSchemeItem
{
    public string Name { get; set; } = "";
    public MaterialYouColorScheme Scheme { get; set; }
    public SolidColorBrush PreviewColor { get; set; } = new SolidColorBrush(Colors.Gray);
}
