using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using SVL.Avalonia.Models;
using SVL.Avalonia.Services;
using SVL.Core.Platform.Abstractions;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;

namespace SVL.Avalonia.ViewModels;

public partial class SettingsPageViewModel : ObservableObject
{
    private readonly AppUserSettingsStore _settingsStore;
    private readonly DialogService _dialogService;
    private readonly NexusAuthService _nexusAuthService;
    private readonly NexusOAuthService _nexusOAuthService;
    private readonly LauncherUpdateService _launcherUpdateService;
    private readonly IExternalProcessService _externalProcessService;
    private readonly INxmProtocolRegistrationService _nxmProtocolRegistrationService;
    private readonly LocalizationService _localizationService;
    private readonly ImageResourceService _imageResourceService;
    private CancellationTokenSource? _autoSaveCts;

    public ObservableCollection<string> Tabs { get; } = ["基本设置", "下载设置", "个性化", "其他", "关于"];

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private string _statusMessage = "设置已加载";

    [ObservableProperty]
    private string _pageTitleText = "设置";

    [ObservableProperty]
    private string _tabBasicText = "基本设置";

    [ObservableProperty]
    private string _tabDownloadText = "下载设置";

    [ObservableProperty]
    private string _tabPersonalizationText = "个性化";

    [ObservableProperty]
    private string _tabOtherText = "其他";

    [ObservableProperty]
    private string _tabAboutText = "关于";

    [ObservableProperty]
    private string _otherSectionSubtitleText = "运行时行为与调试选项";

    [ObservableProperty]
    private string _showNotificationsLabelText = "显示通知";

    [ObservableProperty]
    private string _showModTypeFilterNoticeLabelText = "来源为全部时提示类型筛选不可用";

    [ObservableProperty]
    private string _debugModeLabelText = "启用调试模式";

    [ObservableProperty]
    private string _minimizeOnStartupLabelText = "启动时最小化到托盘";

    [ObservableProperty]
    private string _minimizeOnCloseLabelText = "关闭窗口时最小化到托盘";

    [ObservableProperty]
    private string _themeModeLabelText = "主题模式";

    [ObservableProperty]
    private string _uiLanguageLabelText = "界面语言";

    [ObservableProperty]
    private string _fontSizeLabelText = "界面字体大小";

    [ObservableProperty]
    private string _transparencyLabelText = "启用透明效果";

    [ObservableProperty]
    private string _saveButtonText = "保存设置";

    [ObservableProperty]
    private string _instanceAutoConnectLabelText = "启动时自动连接服务器";

    [ObservableProperty]
    private string _instanceServerAddressLabelText = "服务器地址";

    [ObservableProperty]
    private string _instanceSteamInviteCodeLabelText = "Steam 邀请码";

    [ObservableProperty]
    private string _settingsPath = string.Empty;

    [ObservableProperty]
    private string _operationPathHint = "操作路径：基本设置 -> 下载设置 -> 个性化 -> 其他 -> 关于";

    [ObservableProperty]
    private string _updateCardTitleText = "更新设置";

    [ObservableProperty]
    private string _autoUpdateCheckLabelText = "启动时自动检查更新";

    [ObservableProperty]
    private string _autoDownloadUpdateLabelText = "发现新版本时自动下载";

    [ObservableProperty]
    private string _updateChannelLabelText = "更新通道";

    [ObservableProperty]
    private string _updateSourcePreferenceLabelText = "更新源偏好";

    [ObservableProperty]
    private string _checkUpdateButtonText = "立即检查更新";

    [ObservableProperty]
    private string _updateStatusLabelText = "更新状态";

    [ObservableProperty]
    private string _latestVersionLabelText = "最新版本";

    [ObservableProperty]
    private string _updateSourceLabelText = "更新源";

    [ObservableProperty]
    private string _nxmProtocolCardTitleText = "NXM 协议";

    [ObservableProperty]
    private string _nxmProtocolStatusLabelText = "状态";

    [ObservableProperty]
    private string _nxmAutoRegisterLabelText = "启动时尝试注册 NXM 协议";

    [ObservableProperty]
    private string _nxmProtocolDescriptionText = "用于浏览器中的 nxm:// 链接快速回传到启动器下载页。";

    [ObservableProperty]
    private string _nxmRegisterNowButtonText = "重新注册 NXM 协议";

    [ObservableProperty]
    private string _basicCardIconSource = string.Empty;

    [ObservableProperty]
    private string _downloadCardIconSource = string.Empty;

    [ObservableProperty]
    private string _nexusCardIconSource = string.Empty;

    [ObservableProperty]
    private string _updateCardIconSource = string.Empty;

    [ObservableProperty]
    private string _nxmProtocolCardIconSource = string.Empty;

    [ObservableProperty]
    private string _personalizationCardIconSource = string.Empty;

    [ObservableProperty]
    private string _otherCardIconSource = string.Empty;

    [ObservableProperty]
    private string _aboutCardIconSource = string.Empty;

    [ObservableProperty]
    private string _gameWindowTitle = "<default>";

    [ObservableProperty]
    private string _launcherTitle = "Stardew Valley Launcher";

    [ObservableProperty]
    private string _launcherAppName = "SVL";

    [ObservableProperty]
    private int _selectedLauncherVisibilityIndex = 3;

    [ObservableProperty]
    private int _fontSize = 14;

    [ObservableProperty]
    private bool _enableTransparency = true;

    [ObservableProperty]
    private bool _instanceAutoConnectServer;

    [ObservableProperty]
    private string _instanceServerAddress = string.Empty;

    [ObservableProperty]
    private string _instanceSteamInviteCode = string.Empty;

    [ObservableProperty]
    private bool _enableDownloadCache = true;

    [ObservableProperty]
    private bool _enableDownloadProxy;

    [ObservableProperty]
    private string _downloadProxyUrl = string.Empty;

    [ObservableProperty]
    private string _downloadProxyUserName = string.Empty;

    [ObservableProperty]
    private string _downloadProxyPassword = string.Empty;

    [ObservableProperty]
    private bool _enableDownloadFloatingTaskButton = true;

    [ObservableProperty]
    private bool _enableAutoUpdateCheck = true;

    [ObservableProperty]
    private bool _autoDownloadUpdate;

    [ObservableProperty]
    private string _selectedUpdateChannel = "稳定版";

    [ObservableProperty]
    private string _selectedUpdateSource = "GitHub (推荐)";

    [ObservableProperty]
    private string _skippedUpdateVersion = string.Empty;

    [ObservableProperty]
    private string _updateStatusText = "尚未检查更新";

    [ObservableProperty]
    private string _latestVersionText = "-";

    [ObservableProperty]
    private string _updateSourceText = "-";

    [ObservableProperty]
    private bool _isCheckingLauncherUpdate;

    [ObservableProperty]
    private bool _registerNxmProtocolOnStartup = true;

    [ObservableProperty]
    private string _nxmProtocolStatusText = "待确认";

    [ObservableProperty]
    private string _selectedCollectionConflictStrategy = "覆盖";

    [ObservableProperty]
    private int _selectedCollectionDownloadParallelism = 4;

    [ObservableProperty]
    private int _selectedDownloadThreads = 4;

    [ObservableProperty]
    private int _selectedModUpdateConcurrency = 4;

    [ObservableProperty]
    private int _selectedModLocalizationConcurrency = 4;

    [ObservableProperty]
    private string _selectedThemeMode = "跟随系统";

    [ObservableProperty]
    private string _selectedThemeStyle = "星露谷（默认）";

    [ObservableProperty]
    private string _selectedColorScheme = "天空蓝";

    [ObservableProperty]
    private bool _isDarkMode;

    // ================================================================
    // 缓存管理
    // ================================================================

    [ObservableProperty]
    private string _totalCacheSizeText = "计算中...";

    [ObservableProperty]
    private int _totalCacheFileCount;

    [ObservableProperty]
    private string _communityLocalizationCacheSizeText = "-";

    [ObservableProperty]
    private string _smapiDownloadsCacheSizeText = "-";

    [ObservableProperty]
    private string _downloadInstallCacheSizeText = "-";

    [ObservableProperty]
    private string _imageCacheSizeText = "-";

    [ObservableProperty]
    private string _downloadsCacheSizeText = "-";

    [ObservableProperty]
    private string _gameDownloadCacheSizeText = "-";

    [ObservableProperty]
    private string _nexusDownloadCacheSizeText = "-";

    [ObservableProperty]
    private int _cacheRetentionMinutes = 5;

    [ObservableProperty]
    private int _downloadCacheRetentionMinutes = 10080;

    [ObservableProperty]
    private bool _isCleaningCache;

    /// <summary>过期缓存清理的保留时长选项（分钟）。</summary>
    public ObservableCollection<int> CacheRetentionOptionsMinutes { get; } =
    [5, 15, 30, 60, 180, 720, 1440, 10080, 43200];

    [ObservableProperty]
    private string _selectedLocalizationSource = "Gitee";

    public ObservableCollection<string> ThemeStyleOptions { get; } =
    [
        "星露谷（默认）",
        "天空蓝", "森林绿", "薰衣紫", "海洋青", "樱花粉", "夕阳橙"
    ];

    [ObservableProperty]
    private string _selectedUiLanguage = "zh-CN";

    [ObservableProperty]
    private bool _showNotifications = true;

    [ObservableProperty]
    private bool _showModTypeFilterDisabledNotice = true;

    [ObservableProperty]
    private bool _debugMode;

    [ObservableProperty]
    private bool _minimizeToTrayOnStartup;

    [ObservableProperty]
    private bool _minimizeToTrayOnClose;

    [ObservableProperty]
    private string _nexusApiKey = string.Empty;

    [ObservableProperty]
    private string _nexusOAuthAccessToken = string.Empty;

    [ObservableProperty]
    private string _nexusOAuthRefreshToken = string.Empty;

    [ObservableProperty]
    private string _nexusOAuthIdToken = string.Empty;

    [ObservableProperty]
    private string _nexusUserName = string.Empty;

    [ObservableProperty]
    private string _nexusMembershipType = string.Empty;

    [ObservableProperty]
    private int _nexusUserId;

    [ObservableProperty]
    private string _nexusStatus = "未登录";

    [ObservableProperty]
    private bool _enableNexusAuthNotification = true;

    public ObservableCollection<string> ThemeModes { get; } = ["浅色", "深色", "跟随系统"];

    public ObservableCollection<string> UiLanguages { get; } = ["zh-CN", "en-US"];

    public ObservableCollection<string> CollectionConflictStrategies { get; } = ["覆盖", "跳过", "仅备份"];

    /// <summary>与旧 WPF 设置一致的界面字体大小选项。</summary>
    public ObservableCollection<int> FontSizeOptions { get; } = [12, 13, 14, 15, 16, 18, 20];

    /// <summary>游戏启动后启动器的可见性行为，顺序与旧 WPF LauncherVisibility 枚举一致。</summary>
    public ObservableCollection<string> LauncherVisibilityOptions { get; } =
    [
        "游戏启动后立即关闭",
        "游戏启动后隐藏，游戏退出后自动关闭",
        "游戏启动后隐藏，游戏退出后重新打开",
        "游戏启动后最小化",
        "游戏启动后仍保持不变"
    ];

    public ObservableCollection<string> UpdateChannels { get; } = ["稳定版", "预览版"];

    public ObservableCollection<string> UpdateSourceOptions { get; } = ["GitHub (推荐)", "Gitee (国内加速)"];

    public ObservableCollection<string> LocalizationSourceOptions { get; } = ["Gitee", "GitHub"];

    public ObservableCollection<int> CollectionDownloadParallelismOptions { get; } = [1, 2, 3, 4, 5, 6, 7, 8];

    /// <summary>下载线程数可选项（多线程分片下载）。</summary>
    public ObservableCollection<int> DownloadThreadOptions { get; } = [1, 2, 3, 4, 6, 8, 12, 16];

    /// <summary>Mod 更新/汉化检测的并发线程数选项。</summary>
    public ObservableCollection<int> ModCheckConcurrencyOptions { get; } = [1, 2, 3, 4, 6, 8, 12, 16];

    public bool IsNexusLoggedIn => !string.IsNullOrWhiteSpace(NexusApiKey) || !string.IsNullOrWhiteSpace(NexusOAuthAccessToken);

    public bool ShowNexusLoginGuide => !IsNexusLoggedIn;

    public string NexusDisplayName => string.IsNullOrWhiteSpace(NexusUserName)
        ? "未识别用户"
        : $"{NexusUserName} ({NexusMembershipType})";

    public bool CanCheckLauncherUpdate => !IsCheckingLauncherUpdate;

    public SettingsPageViewModel(
        AppUserSettingsStore settingsStore,
        DialogService dialogService,
        NexusAuthService nexusAuthService,
        NexusOAuthService nexusOAuthService,
        LauncherUpdateService launcherUpdateService,
        IExternalProcessService externalProcessService,
        INxmProtocolRegistrationService nxmProtocolRegistrationService,
        LocalizationService localizationService,
        ImageResourceService imageResourceService)
    {
        _settingsStore = settingsStore;
        _dialogService = dialogService;
        _nexusAuthService = nexusAuthService;
        _nexusOAuthService = nexusOAuthService;
        _launcherUpdateService = launcherUpdateService;
        _externalProcessService = externalProcessService;
        _nxmProtocolRegistrationService = nxmProtocolRegistrationService;
        _localizationService = localizationService;
        _imageResourceService = imageResourceService;
        _localizationService.LanguageChanged += ApplyLocalizedTexts;
        _imageResourceService.ResourcesChanged += ApplyImageResources;
        ApplyLocalizedTexts();
        ApplyImageResources();
        SettingsPath = _settingsStore.GetSettingsPath();

        var loaded = _settingsStore.Load();
        ApplySettings(loaded);
        RefreshNxmProtocolStatus();
        StatusMessage = "设置已从本地加载";

        // 初始化版本号显示
        var ver = _launcherUpdateService.CurrentVersion;
        AppVersion = ver.Revision > 0
            ? $"{ver.Major}.{ver.Minor}.{ver.Build}.{ver.Revision}"
            : $"{ver.Major}.{ver.Minor}.{ver.Build}";

        _ = TryRefreshOAuthTokenSilentlyAsync();
    }

    /// <summary>当前启动器版本号文本。</summary>
    [ObservableProperty]
    private string _appVersion = "1.0.0.0";

    /// <summary>赞助支持：打开爱发电赞助页。</summary>
    [RelayCommand]
    private void Sponsor()
    {
        _externalProcessService.TryOpenUrl("https://ifdian.net/a/mcshengxia");
    }

    /// <summary>查看源码：打开 GitHub 仓库。</summary>
    [RelayCommand]
    private void ViewSource()
    {
        _externalProcessService.TryOpenUrl("https://github.com/panda-lsy/SVL-StardewValleyLauncher");
    }

    /// <summary>打开官网。</summary>
    [RelayCommand]
    private void OpenHomePage()
    {
        _externalProcessService.TryOpenUrl("https://svl.qzz.io/");
    }

    /// <summary>显示全局游戏窗口标题占位符说明。</summary>
    [RelayCommand]
    private async Task ShowWindowTitleHelp()
    {
        const string helpText =
            "窗口标题支持以下占位符：\n\n" +
            "<name>：当前实例名称\n" +
            "<ver>：当前游戏版本\n" +
            "<smver>：当前 SMAPI 版本\n" +
            "<modscount>：已加载的 Mod 数量\n\n" +
            "留空或填写 <default> 可恢复默认标题。占位符会在启动游戏时替换为当前实例信息。";

        await _dialogService.ShowWindowTitleHelpDialogAsync(helpText, "窗口标题占位符帮助");
    }

    private void ApplySettings(AppUserSettings settings)
    {
        GameWindowTitle = settings.GameWindowTitle;
        LauncherTitle = settings.LauncherTitle;
        LauncherAppName = settings.LauncherAppName;
        SelectedLauncherVisibilityIndex = Math.Clamp(settings.LauncherVisibility, 0, LauncherVisibilityOptions.Count - 1);
        InstanceAutoConnectServer = settings.InstanceAutoConnectServer;
        InstanceServerAddress = settings.InstanceServerAddress;
        InstanceSteamInviteCode = settings.InstanceSteamInviteCode;
        EnableDownloadCache = settings.EnableDownloadCache;
        CacheRetentionMinutes = Math.Clamp(settings.CacheRetentionMinutes <= 0 ? 5 : settings.CacheRetentionMinutes, 1, 10080);
        DownloadCacheRetentionMinutes = Math.Clamp(
            settings.DownloadCacheRetentionMinutes <= 0 ? 10080 : settings.DownloadCacheRetentionMinutes,
            60,
            43200);
        SelectedLocalizationSource = string.IsNullOrWhiteSpace(settings.LocalizationPreferredSource) ? "Gitee" : settings.LocalizationPreferredSource;
        EnableDownloadProxy = settings.EnableDownloadProxy;
        DownloadProxyUrl = settings.DownloadProxyUrl;
        DownloadProxyUserName = settings.DownloadProxyUserName;
        DownloadProxyPassword = settings.DownloadProxyPassword;
        EnableDownloadFloatingTaskButton = settings.EnableDownloadFloatingTaskButton;
        EnableAutoUpdateCheck = settings.EnableAutoUpdateCheck;
        AutoDownloadUpdate = settings.AutoDownloadUpdate;
        SelectedUpdateChannel = string.IsNullOrWhiteSpace(settings.UpdateChannel) ? "稳定版" : settings.UpdateChannel;
        SelectedUpdateSource = string.IsNullOrWhiteSpace(settings.PreferredUpdateSource) ? "GitHub (推荐)" : settings.PreferredUpdateSource;
        SkippedUpdateVersion = settings.SkippedLauncherVersion;
        RegisterNxmProtocolOnStartup = settings.RegisterNxmProtocolOnStartup;
        NxmProtocolStatusText = RegisterNxmProtocolOnStartup ? "已启用自动注册" : "未启用自动注册";
        SelectedCollectionConflictStrategy = string.IsNullOrWhiteSpace(settings.CollectionInstallConflictStrategy)
            ? "覆盖"
            : settings.CollectionInstallConflictStrategy;
        SelectedCollectionDownloadParallelism = Math.Clamp(settings.CollectionDownloadParallelism, 1, 8);
        SelectedDownloadThreads = SnapToDownloadThreadOption(settings.DownloadSegmentThreads);
        SelectedModUpdateConcurrency = Math.Clamp(settings.MaxConcurrentModUpdateChecks, 1, 16);
        SelectedModLocalizationConcurrency = Math.Clamp(settings.MaxConcurrentModLocalizationChecks, 1, 16);
        SelectedThemeMode = settings.ThemeMode;
        IsDarkMode = ThemeService.IsDarkMode;
        EnableTransparency = settings.EnableTransparency;
        FontSize = FontSizeOptions.Contains(settings.FontSize) ? settings.FontSize : 14;

        // 主题风格
        var styleName = settings.ThemeStyleName ?? "Stardew";
        var schemeName = settings.ThemeColorScheme ?? "Blue";
        SelectedThemeStyle = ResolveThemeDisplayName(styleName, schemeName);

        // 恢复主题
        ThemeService.RestoreFromSettings(settings);

        SelectedUiLanguage = string.IsNullOrWhiteSpace(settings.UiLanguage) ? "zh-CN" : settings.UiLanguage;
        ShowNotifications = settings.ShowNotifications;
        ShowModTypeFilterDisabledNotice = settings.ShowModTypeFilterDisabledNotice;
        DebugMode = settings.DebugMode;
        MinimizeToTrayOnStartup = settings.MinimizeToTrayOnStartup;
        MinimizeToTrayOnClose = settings.MinimizeToTrayOnClose;
        SelectedTabIndex = Math.Clamp(settings.SettingsTabIndex, 0, Tabs.Count - 1);
        NexusApiKey = settings.NexusApiKey;
        NexusOAuthAccessToken = settings.NexusOAuthAccessToken;
        NexusOAuthRefreshToken = settings.NexusOAuthRefreshToken;
        NexusOAuthIdToken = settings.NexusOAuthIdToken;
        NexusUserName = settings.NexusUserName;
        NexusMembershipType = settings.NexusMembershipType;
        NexusUserId = settings.NexusUserId;
        NexusStatus = IsNexusLoggedIn ? "已登录" : "未登录";
        EnableNexusAuthNotification = !settings.SuppressNexusAuthNotification;
    }

    private AppUserSettings BuildSettings()
    {
        var settings = _settingsStore.Load();
        settings.GameWindowTitle = GameWindowTitle;
        settings.LauncherTitle = LauncherTitle;
        settings.LauncherAppName = LauncherAppName;
        settings.LauncherVisibility = Math.Clamp(SelectedLauncherVisibilityIndex, 0, LauncherVisibilityOptions.Count - 1);
        settings.InstanceAutoConnectServer = InstanceAutoConnectServer;
        settings.InstanceServerAddress = InstanceServerAddress?.Trim() ?? string.Empty;
        settings.InstanceSteamInviteCode = InstanceSteamInviteCode?.Trim() ?? string.Empty;
        settings.EnableDownloadCache = EnableDownloadCache;
        settings.CacheRetentionMinutes = Math.Clamp(CacheRetentionMinutes, 1, 10080);
        settings.DownloadCacheRetentionMinutes = Math.Clamp(DownloadCacheRetentionMinutes, 60, 43200);
        settings.LocalizationPreferredSource = SelectedLocalizationSource;
        settings.EnableDownloadProxy = EnableDownloadProxy;
        settings.DownloadProxyUrl = DownloadProxyUrl?.Trim() ?? string.Empty;
        settings.DownloadProxyUserName = DownloadProxyUserName?.Trim() ?? string.Empty;
        settings.DownloadProxyPassword = DownloadProxyPassword ?? string.Empty;
        settings.EnableDownloadFloatingTaskButton = EnableDownloadFloatingTaskButton;
        settings.EnableAutoUpdateCheck = EnableAutoUpdateCheck;
        settings.AutoDownloadUpdate = AutoDownloadUpdate;
        settings.UpdateChannel = SelectedUpdateChannel;
        settings.PreferredUpdateSource = SelectedUpdateSource;
        settings.SkippedLauncherVersion = SkippedUpdateVersion;
        settings.RegisterNxmProtocolOnStartup = RegisterNxmProtocolOnStartup;
        settings.CollectionInstallConflictStrategy = SelectedCollectionConflictStrategy;
        settings.CollectionDownloadParallelism = Math.Clamp(SelectedCollectionDownloadParallelism, 1, 8);
        settings.DownloadSegmentThreads = Math.Clamp(SelectedDownloadThreads, 1, 16);
        settings.MaxConcurrentModUpdateChecks = Math.Clamp(SelectedModUpdateConcurrency, 1, 16);
        settings.MaxConcurrentModLocalizationChecks = Math.Clamp(SelectedModLocalizationConcurrency, 1, 16);
        settings.ThemeMode = SelectedThemeMode;
        settings.UiLanguage = SelectedUiLanguage;
        settings.FontSize = FontSizeOptions.Contains(FontSize) ? FontSize : 14;
        settings.EnableTransparency = EnableTransparency;

        // 保存主题
        ResolveThemeStyleAndScheme(SelectedThemeStyle, out var styleName, out var schemeName);
        settings.ThemeStyleName = styleName;
        settings.ThemeColorScheme = schemeName;
        ThemeService.SaveToSettings(settings);
        settings.ShowNotifications = ShowNotifications;
        settings.ShowModTypeFilterDisabledNotice = ShowModTypeFilterDisabledNotice;
        settings.DebugMode = DebugMode;
        settings.MinimizeToTrayOnStartup = MinimizeToTrayOnStartup;
        settings.MinimizeToTrayOnClose = MinimizeToTrayOnClose;
        settings.SettingsTabIndex = Math.Clamp(SelectedTabIndex, 0, Tabs.Count - 1);
        settings.NexusApiKey = NexusApiKey;
        settings.NexusOAuthAccessToken = NexusOAuthAccessToken;
        settings.NexusOAuthRefreshToken = NexusOAuthRefreshToken;
        settings.NexusOAuthIdToken = NexusOAuthIdToken;
        settings.NexusUserName = NexusUserName;
        settings.NexusMembershipType = NexusMembershipType;
        settings.NexusUserId = NexusUserId;
        settings.SuppressNexusAuthNotification = !EnableNexusAuthNotification;
        return settings;
    }

    private void ApplyLocalizedTexts()
    {
        PageTitleText = _localizationService.Get("Settings.Title");
        TabBasicText = _localizationService.Get("Settings.Tab.Basic");
        TabDownloadText = _localizationService.Get("Settings.Tab.Download");
        TabPersonalizationText = _localizationService.Get("Settings.Tab.Personalization");
        TabOtherText = _localizationService.Get("Settings.Tab.Other");
        TabAboutText = _localizationService.Get("Settings.Tab.About");
        OtherSectionSubtitleText = _localizationService.Get("Settings.Other.Subtitle");
        ShowNotificationsLabelText = _localizationService.Get("Settings.Other.ShowNotifications");
        ShowModTypeFilterNoticeLabelText = _localizationService.Get("Settings.Other.TypeFilterNotice");
        DebugModeLabelText = _localizationService.Get("Settings.Other.DebugMode");
        MinimizeOnStartupLabelText = _localizationService.Get("Settings.Other.MinimizeOnStartup");
        MinimizeOnCloseLabelText = _localizationService.Get("Settings.Other.MinimizeOnClose");
        ThemeModeLabelText = _localizationService.Get("Settings.ThemeMode");
        UiLanguageLabelText = _localizationService.Get("Settings.UiLanguage");
        FontSizeLabelText = _localizationService.Get("Settings.FontSize");
        TransparencyLabelText = _localizationService.Get("Settings.Transparency");
        SaveButtonText = _localizationService.Get("Settings.Save");
        InstanceAutoConnectLabelText = _localizationService.Get("Settings.Basic.AutoConnect");
        InstanceServerAddressLabelText = _localizationService.Get("Settings.Basic.ServerAddress");
        InstanceSteamInviteCodeLabelText = _localizationService.Get("Settings.Basic.SteamInviteCode");
        OperationPathHint = _localizationService.Get("Settings.OperationPath");
        UpdateCardTitleText = _localizationService.Get("Settings.Card.Update");
        AutoUpdateCheckLabelText = _localizationService.Get("Settings.Update.AutoCheck");
        AutoDownloadUpdateLabelText = _localizationService.Get("Settings.Update.AutoDownload");
        UpdateChannelLabelText = _localizationService.Get("Settings.Update.Channel");
        UpdateSourcePreferenceLabelText = _localizationService.Get("Settings.Update.SourcePreference");
        CheckUpdateButtonText = _localizationService.Get("Settings.Update.CheckNow");
        UpdateStatusLabelText = _localizationService.Get("Settings.Update.StatusLabel");
        LatestVersionLabelText = _localizationService.Get("Settings.Update.LatestLabel");
        UpdateSourceLabelText = _localizationService.Get("Settings.Update.SourceLabel");
        NxmProtocolCardTitleText = _localizationService.Get("Settings.Card.NxmProtocol");
        NxmProtocolStatusLabelText = _localizationService.Get("Settings.Nxm.Status");
        NxmAutoRegisterLabelText = _localizationService.Get("Settings.Nxm.AutoRegister");
        NxmProtocolDescriptionText = _localizationService.Get("Settings.Nxm.Description");
        NxmRegisterNowButtonText = _localizationService.Get("Settings.Nxm.RegisterNow");
    }

    private void ApplyImageResources()
    {
        BasicCardIconSource = _imageResourceService.Get("settings.card.basic");
        DownloadCardIconSource = _imageResourceService.Get("settings.card.download");
        NexusCardIconSource = _imageResourceService.Get("settings.card.nexus");
        UpdateCardIconSource = _imageResourceService.Get("settings.card.update");
        NxmProtocolCardIconSource = _imageResourceService.Get("settings.card.nxm");
        PersonalizationCardIconSource = _imageResourceService.Get("settings.card.personalization");
        OtherCardIconSource = _imageResourceService.Get("settings.card.other");
        AboutCardIconSource = _imageResourceService.Get("settings.card.about");
    }

    public bool IsBasicTab => SelectedTabIndex == 0;

    public bool IsDownloadTab => SelectedTabIndex == 1;

    public bool IsPersonalizationTab => SelectedTabIndex == 2;

    public bool IsOtherTab => SelectedTabIndex == 3;

    public bool IsAboutTab => SelectedTabIndex == 4;

    /// <summary>接管下载 URL 输入框。</summary>
    [ObservableProperty]
    private string _takeoverDownloadUrl = string.Empty;

    /// <summary>下载分段线程数（接管下载/分片下载用）。</summary>
    [ObservableProperty]
    private int _downloadSegmentThreads = 4;

    /// <summary>接管下载请求事件：(url, targetPath) → 由 MainWindowViewModel 转发到 DownloadPage 入队。</summary>
    public event Action<string, string>? TakeoverDownloadRequested;

    /// <summary>用户退出 Nexus 账户事件：由 MainWindowViewModel 转发到 DownloadPage 重置通知抑制状态。</summary>
    public event Action? NexusLoggedOut;

    partial void OnSelectedTabIndexChanged(int value)
    {
        var normalizedIndex = Math.Clamp(value, 0, Tabs.Count - 1);
        if (normalizedIndex != value)
        {
            SelectedTabIndex = normalizedIndex;
            return;
        }

        OnPropertyChanged(nameof(IsBasicTab));
        OnPropertyChanged(nameof(IsDownloadTab));
        OnPropertyChanged(nameof(IsPersonalizationTab));
        OnPropertyChanged(nameof(IsOtherTab));
        OnPropertyChanged(nameof(IsAboutTab));

        StatusMessage = $"当前标签：{Tabs[normalizedIndex]}";
    }

    partial void OnLauncherTitleChanged(string value)
    {
        StatusMessage = "启动器标题已修改（已自动保存）";
        ScheduleAutoSave();
    }

    partial void OnLauncherAppNameChanged(string value)
    {
        StatusMessage = "启动器简称已修改（已自动保存）";
        ScheduleAutoSave();
    }

    partial void OnEnableDownloadCacheChanged(bool value)
    {
        StatusMessage = value ? "已启用下载缓存（已自动保存）" : "已禁用下载缓存（已自动保存）";
        ScheduleAutoSave();
    }

    partial void OnSelectedThemeModeChanged(string value)
    {
        var followSystem = value.Contains("跟随", StringComparison.OrdinalIgnoreCase) ||
                           value.Contains("System", StringComparison.OrdinalIgnoreCase);
        var isDark = value.Contains("深") || value.Contains("暗") || value.Contains("Dark", StringComparison.OrdinalIgnoreCase);
        ThemeService.SetThemeMode(isDark, followSystem);
        IsDarkMode = ThemeService.IsDarkMode;
        StatusMessage = $"主题模式切换为：{value}（已自动保存）";
        ScheduleAutoSave();
    }

    partial void OnSelectedThemeStyleChanged(string value)
    {
        System.Diagnostics.Debug.WriteLine($"[Settings] Theme style changed to: {value}");
        ApplySelectedTheme();
        StatusMessage = $"配色方案切换为：{value}（已自动保存）";
        ScheduleAutoSave();
    }

    partial void OnIsDarkModeChanged(bool value)
    {
        ThemeService.SetDarkMode(value);
    }

    private void ApplySelectedTheme()
    {
        var themes = ThemeService.GetAvailableThemes();
        System.Diagnostics.Debug.WriteLine($"[Settings] Looking for theme: '{SelectedThemeStyle}' among {themes.Count} themes");
        foreach (var theme in themes)
        {
            if (string.Equals(theme.DisplayName, SelectedThemeStyle, StringComparison.Ordinal))
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] Match found! Applying '{theme.DisplayName}'...");
                ThemeService.ApplyTheme(theme);
                return;
            }
        }
        System.Diagnostics.Debug.WriteLine($"[Settings] WARNING: No matching theme for '{SelectedThemeStyle}'");
    }

    partial void OnSelectedUiLanguageChanged(string value)
    {
        StatusMessage = $"界面语言切换为：{value}（已自动保存）";
        ScheduleAutoSave();
    }

    partial void OnShowNotificationsChanged(bool value)
    {
        StatusMessage = value ? "已启用通知（已自动保存）" : "已禁用通知（已自动保存）";
        ScheduleAutoSave();
    }

    partial void OnDebugModeChanged(bool value)
    {
        StatusMessage = value ? "已启用调试模式（已自动保存）" : "已禁用调试模式（已自动保存）";
        ScheduleAutoSave();
    }

    partial void OnMinimizeToTrayOnStartupChanged(bool value)
    {
        StatusMessage = value ? "已启用启动时最小化到托盘（已自动保存）" : "已禁用启动时最小化到托盘（已自动保存）";
        ScheduleAutoSave();
    }

    partial void OnMinimizeToTrayOnCloseChanged(bool value)
    {
        StatusMessage = value ? "已启用关闭时最小化到托盘（已自动保存）" : "已禁用关闭时最小化到托盘（已自动保存）";
        ScheduleAutoSave();
    }

    partial void OnSelectedCollectionConflictStrategyChanged(string value)
    {
        StatusMessage = $"Collection 冲突策略切换为：{value}（已自动保存）";
        ScheduleAutoSave();
    }

    partial void OnSelectedCollectionDownloadParallelismChanged(int value)
    {
        StatusMessage = $"Collection 下载并发切换为：{Math.Clamp(value, 1, 8)}（已自动保存）";
        ScheduleAutoSave();
    }

    partial void OnSelectedDownloadThreadsChanged(int value)
    {
        StatusMessage = $"下载线程数切换为：{value}（已自动保存）";
        ScheduleAutoSave();
    }

    /// <summary>将任意线程数吸附到最近的可选项（如 5 → 4 或 6）。</summary>
    private int SnapToDownloadThreadOption(int value)
    {
        var clamped = Math.Clamp(value, 1, 16);
        return DownloadThreadOptions.Aggregate((best, next) =>
            Math.Abs(next - clamped) < Math.Abs(best - clamped) ? next : best);
    }

    partial void OnEnableAutoUpdateCheckChanged(bool value)
    {
        StatusMessage = value ? "已启用自动检查更新（已自动保存）" : "已禁用自动检查更新（已自动保存）";
        ScheduleAutoSave();
    }

    partial void OnSelectedLauncherVisibilityIndexChanged(int value)
    {
        var normalized = Math.Clamp(value, 0, LauncherVisibilityOptions.Count - 1);
        if (normalized != value)
        {
            SelectedLauncherVisibilityIndex = normalized;
            return;
        }

        StatusMessage = $"游戏启动后行为切换为：{LauncherVisibilityOptions[normalized]}（已自动保存）";
        ScheduleAutoSave();
    }

    partial void OnFontSizeChanged(int value)
    {
        if (!FontSizeOptions.Contains(value))
        {
            FontSize = 14;
            return;
        }

        StatusMessage = $"{FontSizeLabelText}切换为：{value}（已自动保存）";
        ScheduleAutoSave();
    }

    partial void OnEnableTransparencyChanged(bool value)
    {
        ThemeService.SetTransparencyEnabled(value);
        StatusMessage = value ? "已启用透明效果（已自动保存）" : "已禁用透明效果（已自动保存）";
        ScheduleAutoSave();
    }

    partial void OnShowModTypeFilterDisabledNoticeChanged(bool value)
    {
        StatusMessage = value ? "已启用类型筛选不可用提示（已自动保存）" : "已禁用类型筛选不可用提示（已自动保存）";
        ScheduleAutoSave();
    }

    partial void OnSelectedModUpdateConcurrencyChanged(int value)
    {
        StatusMessage = $"Mod 更新检测并发切换为：{Math.Clamp(value, 1, 16)}（已自动保存）";
        ScheduleAutoSave();
    }

    partial void OnSelectedModLocalizationConcurrencyChanged(int value)
    {
        StatusMessage = $"Mod 汉化检测并发切换为：{Math.Clamp(value, 1, 16)}（已自动保存）";
        ScheduleAutoSave();
    }

    partial void OnAutoDownloadUpdateChanged(bool value)
    {
        StatusMessage = value ? "已启用自动下载更新（已自动保存）" : "已禁用自动下载更新（已自动保存）";
        ScheduleAutoSave();
    }

    partial void OnSelectedUpdateChannelChanged(string value)
    {
        StatusMessage = $"更新通道切换为：{value}（已自动保存）";
        ScheduleAutoSave();
    }

    partial void OnSelectedUpdateSourceChanged(string value)
    {
        StatusMessage = $"更新源偏好切换为：{value}（已自动保存）";
        ScheduleAutoSave();
    }

    partial void OnIsCheckingLauncherUpdateChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCheckLauncherUpdate));
    }

    partial void OnRegisterNxmProtocolOnStartupChanged(bool value)
    {
        if (value)
        {
            TryRegisterNxmProtocolInternal(updateStatusMessage: false);
            StatusMessage = "已启用 NXM 启动自动注册（已自动保存）";
        }
        else
        {
            RefreshNxmProtocolStatus();
            StatusMessage = "已禁用 NXM 启动自动注册（已自动保存）";
        }
        ScheduleAutoSave();
    }

    partial void OnNexusApiKeyChanged(string value)
    {
        OnPropertyChanged(nameof(IsNexusLoggedIn));
        OnPropertyChanged(nameof(ShowNexusLoginGuide));
    }

    partial void OnNexusOAuthAccessTokenChanged(string value)
    {
        OnPropertyChanged(nameof(IsNexusLoggedIn));
        OnPropertyChanged(nameof(ShowNexusLoginGuide));
    }

    partial void OnNexusUserNameChanged(string value)
    {
        OnPropertyChanged(nameof(NexusDisplayName));
    }

    partial void OnNexusMembershipTypeChanged(string value)
    {
        OnPropertyChanged(nameof(NexusDisplayName));
    }

    partial void OnEnableNexusAuthNotificationChanged(bool value)
    {
        StatusMessage = value ? "已启用 Nexus 登录失效提醒（已自动保存）" : "已关闭 Nexus 登录失效提醒（已自动保存）";
        ScheduleAutoSave();
    }

    partial void OnCacheRetentionMinutesChanged(int value)
    {
        var normalized = Math.Clamp(value, 1, 10080);
        if (normalized != value)
        {
            CacheRetentionMinutes = normalized;
            return;
        }

        StatusMessage = $"缓存保留时长已设为 {CacheRetentionMinutes} 分钟（已自动保存）";
        ScheduleAutoSave();
    }

    partial void OnDownloadCacheRetentionMinutesChanged(int value)
    {
        var normalized = Math.Clamp(value, 60, 43200);
        if (normalized != value)
        {
            DownloadCacheRetentionMinutes = normalized;
            return;
        }

        StatusMessage = $"下载归档缓存保留时长已设为 {FormatRetentionMinutes(DownloadCacheRetentionMinutes)}（已自动保存）";
        ScheduleAutoSave();
    }

    private static string FormatRetentionMinutes(int minutes)
    {
        return minutes switch
        {
            >= 10080 when minutes % 10080 == 0 => $"{minutes / 10080} 周",
            >= 1440 when minutes % 1440 == 0 => $"{minutes / 1440} 天",
            >= 60 when minutes % 60 == 0 => $"{minutes / 60} 小时",
            _ => $"{minutes} 分钟"
        };
    }

    [RelayCommand]
    private void SelectTab(object? index)
    {
        var parsed = index switch
        {
            int intValue => intValue,
            string textValue when int.TryParse(textValue, out var intValue) => intValue,
            _ => SelectedTabIndex
        };

        SelectedTabIndex = Math.Clamp(parsed, 0, Tabs.Count - 1);
        if (SelectedTabIndex == 3) // 高级/缓存管理标签页
        {
            _ = RefreshCacheStatisticsAsync();
        }
    }

    /// <summary>刷新所有缓存类别的统计信息。</summary>
    [RelayCommand]
    private async Task RefreshCacheStatisticsAsync()
    {
        var statistics = await Task.Run(() =>
        {
            return new
            {
                Total = CacheManagementService.GetTotalStatistics(),
                CommunityLocalization = CacheManagementService.GetStatistics(CacheCategory.CommunityLocalization),
                SmapiDownloads = CacheManagementService.GetStatistics(CacheCategory.SmapiDownloads),
                DownloadInstall = CacheManagementService.GetStatistics(CacheCategory.DownloadInstall),
                Images = CacheManagementService.GetStatistics(CacheCategory.Images),
                Downloads = CacheManagementService.GetStatistics(CacheCategory.DownloadsCache),
                Game = CacheManagementService.GetStatistics(CacheCategory.Game),
                Nexus = CacheManagementService.GetStatistics(CacheCategory.Nexus)
            };

        });

        void ApplyStatistics()
        {
            TotalCacheSizeText = statistics.Total.DisplaySize;
            TotalCacheFileCount = statistics.Total.FileCount;
            CommunityLocalizationCacheSizeText = statistics.CommunityLocalization.DisplaySize;
            SmapiDownloadsCacheSizeText = statistics.SmapiDownloads.DisplaySize;
            DownloadInstallCacheSizeText = statistics.DownloadInstall.DisplaySize;
            ImageCacheSizeText = statistics.Images.DisplaySize;
            DownloadsCacheSizeText = statistics.Downloads.DisplaySize;
            GameDownloadCacheSizeText = statistics.Game.DisplaySize;
            NexusDownloadCacheSizeText = statistics.Nexus.DisplaySize;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyStatistics();
        }
        else
        {
            await Dispatcher.UIThread.InvokeAsync(ApplyStatistics);
        }
    }

    /// <summary>清理社区汉化缓存。</summary>
    [RelayCommand]
    private void ClearCommunityLocalizationCache()
    {
        CacheManagementService.Clear(CacheCategory.CommunityLocalization);
        StatusMessage = "社区汉化缓存已清理";
        _ = RefreshCacheStatisticsAsync();
    }

    /// <summary>清理 SMAPI 下载缓存。</summary>
    [RelayCommand]
    private void ClearSmapiDownloadsCache()
    {
        CacheManagementService.Clear(CacheCategory.SmapiDownloads);
        StatusMessage = "SMAPI 下载缓存已清理";
        _ = RefreshCacheStatisticsAsync();
    }

    /// <summary>清理下载安装临时文件。</summary>
    [RelayCommand]
    private void ClearDownloadInstallCache()
    {
        CacheManagementService.Clear(CacheCategory.DownloadInstall);
        StatusMessage = "下载安装临时文件已清理";
        _ = RefreshCacheStatisticsAsync();
    }

    /// <summary>清理远程图片与图标缓存。</summary>
    [RelayCommand]
    private void ClearImageCache()
    {
        CacheManagementService.Clear(CacheCategory.Images);
        StatusMessage = "图片/图标缓存已清理";
        _ = RefreshCacheStatisticsAsync();
    }

    /// <summary>清理下载文件缓存。</summary>
    [RelayCommand]
    private void ClearDownloadsCache()
    {
        CacheManagementService.Clear(CacheCategory.DownloadsCache);
        StatusMessage = "下载文件缓存已清理";
        _ = RefreshCacheStatisticsAsync();
    }

    /// <summary>清理游戏本体下载缓存。</summary>
    [RelayCommand]
    private void ClearGameDownloadCache()
    {
        CacheManagementService.Clear(CacheCategory.Game);
        StatusMessage = "游戏本体下载缓存已清理";
        _ = RefreshCacheStatisticsAsync();
    }

    /// <summary>清理 Nexus 下载缓存。</summary>
    [RelayCommand]
    private void ClearNexusDownloadCache()
    {
        CacheManagementService.Clear(CacheCategory.Nexus);
        StatusMessage = "Nexus 下载缓存已清理";
        _ = RefreshCacheStatisticsAsync();
    }

    /// <summary>清理所有缓存。</summary>
    [RelayCommand]
    private void ClearAllCache()
    {
        CacheManagementService.ClearAll();
        StatusMessage = "所有缓存已清理";
        _ = RefreshCacheStatisticsAsync();
    }

    /// <summary>按保留时长清理所有已过期缓存。</summary>
    [RelayCommand]
    private async Task ClearExpiredCache()
    {
        if (IsCleaningCache)
        {
            return;
        }

        IsCleaningCache = true;
        try
        {
            var retentionMinutes = Math.Clamp(DownloadCacheRetentionMinutes, 60, 43200);
            var removed = await Task.Run(() => CacheManagementService.ClearExpired(TimeSpan.FromMinutes(retentionMinutes)));
            StatusMessage = $"已清理过期缓存 {removed} 个文件（保留 {retentionMinutes} 分钟）";
            await RefreshCacheStatisticsAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"清理过期缓存失败: {ex.Message}";
        }
        finally
        {
            IsCleaningCache = false;
        }
    }

    /// <summary>打开 SVL 缓存根目录。</summary>
    [RelayCommand]
    private void OpenCacheFolder()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SVL");

        try
        {
            Directory.CreateDirectory(path);
            var opened = _externalProcessService.TryOpenPath(path);
            StatusMessage = opened ? $"已打开缓存目录: {path}" : "打开缓存目录失败";
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开缓存目录失败: {ex.Message}";
        }
    }

    partial void OnSelectedLocalizationSourceChanged(string value)
    {
        ScheduleAutoSave();
        StatusMessage = $"社区汉化源切换为：{value}（已自动保存）";
    }

    [RelayCommand]
    private void SaveSettings()
    {
        _settingsStore.Save(BuildSettings());
        _localizationService.SetLanguage(SelectedUiLanguage);
        StatusMessage = $"设置已保存（{System.DateTime.Now:HH:mm:ss}）";
    }

    /// <summary>
    /// 将 Avalonia 配置恢复为默认值。
    ///
    /// 实例路径、实例名称、收藏列表属于用户数据，不是应用偏好设置，
    /// 因此恢复设置时必须保留，避免用户因为点击“恢复默认”而丢失实例入口。
    /// Nexus 登录凭据则按旧 WPF 行为清空，并由确认对话框明确告知用户。
    /// </summary>
    [RelayCommand]
    private async Task ResetSettings()
    {
        var confirmed = await _dialogService.ShowConfirmAsync(
            "恢复默认设置",
            "将恢复界面、下载、主题和启动器偏好设置，并退出 Nexus 登录。\n\n现有游戏实例、实例路径和收藏列表不会被删除。是否继续？");
        if (!confirmed)
        {
            return;
        }

        _autoSaveCts?.Cancel();

        var current = _settingsStore.Load();
        var defaults = new AppUserSettings
        {
            // 实例注册属于数据层，不能随应用偏好一起重置。
            InstanceName = current.InstanceName,
            InstanceDescription = current.InstanceDescription,
            IsFavoriteInstance = current.IsFavoriteInstance,
            PreferredInstancePath = current.PreferredInstancePath,
            FavoriteInstanceKeys = current.FavoriteInstanceKeys?.ToList() ?? []
        };

        ApplySettings(defaults);
        _settingsStore.Save(BuildSettings());
        _localizationService.SetLanguage(SelectedUiLanguage);
        RefreshNxmProtocolStatus();
        StatusMessage = "设置已恢复默认值（实例数据未改变）";
    }

    /// <summary>从磁盘重新加载设置（用于外部导航进入设置页时刷新状态）。</summary>
    public void ReloadFromSettings()
    {
        try
        {
            ApplySettings(_settingsStore.Load());
        }
        catch
        {
            // 忽略加载失败，保留现有内存状态
        }
    }

    /// <summary>防抖自动保存：设置项变更后 1000ms 无新变更则持久化，避免遗忘保存。</summary>
    private void ScheduleAutoSave()
    {
        _autoSaveCts?.Cancel();
        _autoSaveCts = new CancellationTokenSource();
        var token = _autoSaveCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1000, token);
                if (token.IsCancellationRequested) return;
                _settingsStore.Save(BuildSettings());
            }
            catch (OperationCanceledException)
            {
                // 被新的变更取消，正常
            }
        });
    }

    [RelayCommand]
    private async Task SaveTakeoverDownloadAs()
    {
        var url = TakeoverDownloadUrl?.Trim();
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            StatusMessage = "请输入有效的 http/https 下载地址";
            return;
        }

        var suggestedFileName = GuessFileNameFromUrl(uri);

        // 用 Avalonia StorageProvider 弹出保存文件选择器
        if (global::Avalonia.Application.Current?.ApplicationLifetime is global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is { } owner)
        {
            var fileTypes = new[]
            {
                new global::Avalonia.Platform.Storage.FilePickerFileType("所有文件") { Patterns = ["*.*"] }
            };
            var options = new global::Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = "接管下载 - 另存为",
                SuggestedFileName = suggestedFileName,
                FileTypeChoices = fileTypes
            };

            var storageFile = await owner.StorageProvider.SaveFilePickerAsync(options);
            if (storageFile == null)
            {
                return;
            }

            var targetPath = storageFile.Path.IsAbsoluteUri
                ? Uri.UnescapeDataString(storageFile.Path.LocalPath)
                : storageFile.Path.ToString();

            TakeoverDownloadRequested?.Invoke(url, targetPath);
            StatusMessage = $"已添加接管下载任务：{Path.GetFileName(targetPath)}";
        }
    }

    private static string GuessFileNameFromUrl(Uri uri)
    {
        try
        {
            var fileName = Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }
        }
        catch
        {
            // 忽略，回退到默认名
        }

        return "download";
    }

    [RelayCommand]
    private void OpenSettingsJson()
    {
        var path = _settingsStore.GetSettingsPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            StatusMessage = "设置文件不存在";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
            StatusMessage = $"已打开: {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task OpenNexusLoginAsync()
    {
        var result = await _dialogService.ShowNexusLoginAsync(
            NexusApiKey,
            NexusOAuthAccessToken,
            NexusOAuthRefreshToken,
            NexusUserName,
            NexusMembershipType,
            NexusUserId,
            _nexusAuthService,
            _nexusOAuthService);
        if (result == null)
        {
            return;
        }

        NexusApiKey = result.ApiKey;
        NexusOAuthAccessToken = result.OAuthAccessToken;
        NexusOAuthRefreshToken = result.OAuthRefreshToken;
        NexusOAuthIdToken = result.OAuthIdToken;
        NexusUserName = result.UserName;
        NexusMembershipType = result.MembershipType;
        NexusUserId = result.UserId;
        NexusStatus = result.IsOAuthLogin ? "已登录（OAuth）" : "已登录（API Key 已验证）";

        _settingsStore.Save(BuildSettings());
        StatusMessage = "Nexus 登录状态已更新并保存";
    }

    [RelayCommand]
    private async Task ValidateNexusAsync()
    {
        if (string.IsNullOrWhiteSpace(NexusApiKey) && string.IsNullOrWhiteSpace(NexusOAuthAccessToken))
        {
            NexusStatus = "未登录";
            StatusMessage = "请先登录 Nexus";
            return;
        }

        NexusStatus = "正在验证...";
        if (!string.IsNullOrWhiteSpace(NexusApiKey))
        {
            var apiKeyResult = await _nexusAuthService.ValidateApiKeyAsync(NexusApiKey);
            if (!apiKeyResult.IsSuccess)
            {
                NexusStatus = apiKeyResult.Message;
                StatusMessage = "Nexus 验证失败";
                return;
            }

            NexusUserName = apiKeyResult.UserName;
            NexusMembershipType = apiKeyResult.MembershipType;
            NexusUserId = apiKeyResult.UserId;
            NexusStatus = "已登录（API Key 验证通过）";
            _settingsStore.Save(BuildSettings());
            StatusMessage = "Nexus 状态验证成功";
            return;
        }

        var oauthResult = await _nexusOAuthService.ValidateAccessTokenAsync(NexusOAuthAccessToken);
        if (!oauthResult.IsSuccess)
        {
            if (!string.IsNullOrWhiteSpace(NexusOAuthRefreshToken))
            {
                NexusStatus = "OAuth Token 无效，正在自动刷新...";
                var refreshed = await RefreshOAuthTokenInternalAsync();
                if (!refreshed)
                {
                    NexusStatus = oauthResult.Message;
                    StatusMessage = "Nexus OAuth 验证失败";
                    return;
                }

                oauthResult = await _nexusOAuthService.ValidateAccessTokenAsync(NexusOAuthAccessToken);
                if (!oauthResult.IsSuccess)
                {
                    NexusStatus = oauthResult.Message;
                    StatusMessage = "Nexus OAuth 验证失败";
                    return;
                }
            }
            else
            {
                NexusStatus = oauthResult.Message;
                StatusMessage = "Nexus OAuth 验证失败";
                return;
            }
        }

        NexusUserName = oauthResult.UserName;
        NexusMembershipType = oauthResult.MembershipType;
        NexusUserId = oauthResult.UserId;
        NexusStatus = "已登录（OAuth 验证通过）";
        _settingsStore.Save(BuildSettings());
        StatusMessage = "Nexus 状态验证成功";
    }

    [RelayCommand]
    private async Task RefreshOAuthTokenAsync()
    {
        if (string.IsNullOrWhiteSpace(NexusOAuthRefreshToken))
        {
            StatusMessage = "当前没有可用的 OAuth Refresh Token";
            return;
        }

        NexusStatus = "正在刷新 OAuth Token...";
        var ok = await RefreshOAuthTokenInternalAsync();
        if (ok)
        {
            NexusStatus = "已登录（OAuth Token 已刷新）";
            StatusMessage = "OAuth Token 刷新成功";
            return;
        }

        NexusStatus = "OAuth Token 刷新失败";
        StatusMessage = "请重新登录 Nexus";
    }

    [RelayCommand]
    private void LogoutNexus()
    {
        NexusApiKey = string.Empty;
        NexusOAuthAccessToken = string.Empty;
        NexusOAuthRefreshToken = string.Empty;
        NexusOAuthIdToken = string.Empty;
        NexusUserName = string.Empty;
        NexusMembershipType = string.Empty;
        NexusUserId = 0;
        NexusStatus = "未登录";
        EnableNexusAuthNotification = true;
        _settingsStore.Save(BuildSettings());
        StatusMessage = "已退出 Nexus";
        NexusLoggedOut?.Invoke();
    }

    /// <summary>由 MainWindowViewModel 启动时自动检查调用：复用弹窗逻辑，避免重复检查。</summary>
    public async Task ShowUpdateDialogFromAutoCheckAsync(LauncherUpdateCheckResult result)
    {
        if (IsCheckingLauncherUpdate)
        {
            return;
        }

        IsCheckingLauncherUpdate = true;
        try
        {
            var release = result.ReleaseInfo!;
            UpdateStatusText = string.Format(_localizationService.Get("Settings.Update.Found"), release.TagName);
            StatusMessage = UpdateStatusText;

            var action = await _dialogService.ShowUpdateDialogAsync(
                result.CurrentVersion,
                release,
                result.Source,
                _launcherUpdateService,
                autoDownload: AutoDownloadUpdate);
            if (action == UpdateDialogAction.SkipVersion)
            {
                SkippedUpdateVersion = release.TagName;
                _settingsStore.Save(BuildSettings());
            }
        }
        finally
        {
            IsCheckingLauncherUpdate = false;
        }
    }

    [RelayCommand]
    private async Task CheckLauncherUpdate()
    {
        if (IsCheckingLauncherUpdate)
        {
            return;
        }

        IsCheckingLauncherUpdate = true;
        UpdateStatusText = _localizationService.Get("Settings.Update.Checking");
        StatusMessage = UpdateStatusText;

        try
        {
            var includePrerelease = string.Equals(SelectedUpdateChannel, "预览版", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(SelectedUpdateChannel, "preview", StringComparison.OrdinalIgnoreCase);
            var preferGitee = SelectedUpdateSource.Contains("Gitee", StringComparison.OrdinalIgnoreCase);

            var result = await _launcherUpdateService.CheckForUpdateAsync(includePrerelease, preferGitee);
            if (!result.Success || result.ReleaseInfo == null)
            {
                var failedMessage = string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? _localizationService.Get("Settings.Update.Failed")
                    : result.ErrorMessage;
                UpdateStatusText = failedMessage;
                StatusMessage = failedMessage;
                return;
            }

            LatestVersionText = $"v{result.LatestVersion}";
            UpdateSourceText = result.Source;

            if (!result.HasUpdate)
            {
                UpdateStatusText = _localizationService.Get("Settings.Update.UpToDate");
                StatusMessage = UpdateStatusText;
                return;
            }

            var release = result.ReleaseInfo;
            var releaseTag = release.TagName;

            if (!string.IsNullOrWhiteSpace(SkippedUpdateVersion) &&
                string.Equals(SkippedUpdateVersion, releaseTag, StringComparison.OrdinalIgnoreCase))
            {
                UpdateStatusText = string.Format(_localizationService.Get("Settings.Update.Skipped"), releaseTag);
                StatusMessage = UpdateStatusText;
                return;
            }

            UpdateStatusText = string.Format(_localizationService.Get("Settings.Update.Found"), releaseTag);
            StatusMessage = UpdateStatusText;

            var action = await _dialogService.ShowUpdateDialogAsync(
                result.CurrentVersion,
                release,
                result.Source,
                _launcherUpdateService,
                autoDownload: AutoDownloadUpdate);
            if (action == UpdateDialogAction.SkipVersion)
            {
                SkippedUpdateVersion = releaseTag;
                _settingsStore.Save(BuildSettings());
                UpdateStatusText = string.Format(_localizationService.Get("Settings.Update.Skipped"), releaseTag);
                StatusMessage = UpdateStatusText;
                return;
            }

            if (action == UpdateDialogAction.DownloadAndInstall)
            {
                // 应用内下载安装已完成，安装程序已启动，启动器将被关闭
                UpdateStatusText = "更新已下载，安装程序已启动，启动器即将关闭。";
                StatusMessage = UpdateStatusText;
                return;
            }

            if (action == UpdateDialogAction.OpenRelease)
            {
                var opened = !string.IsNullOrWhiteSpace(release.HtmlUrl) && _externalProcessService.TryOpenUrl(release.HtmlUrl);
                UpdateStatusText = opened
                    ? _localizationService.Get("Settings.Update.OpenReleaseSuccess")
                    : _localizationService.Get("Settings.Update.OpenReleaseFailed");
                StatusMessage = UpdateStatusText;
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText = $"{_localizationService.Get("Settings.Update.Failed")}: {ex.Message}";
            StatusMessage = UpdateStatusText;
        }
        finally
        {
            IsCheckingLauncherUpdate = false;
        }
    }

    [RelayCommand]
    private void RegisterNxmProtocolNow()
    {
        var result = TryRegisterNxmProtocolInternal(updateStatusMessage: true);
        StatusMessage = result.Message;
    }

    private void RefreshNxmProtocolStatus()
    {
        var status = _nxmProtocolRegistrationService.GetStatus();
        NxmProtocolStatusText = status.Message;
    }

    private NxmProtocolRegistrationResult TryRegisterNxmProtocolInternal(bool updateStatusMessage)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            NxmProtocolStatusText = "无法定位启动器路径，NXM 协议注册失败";
            return new NxmProtocolRegistrationResult
            {
                IsSuccess = false,
                IsSupported = OperatingSystem.IsWindows(),
                IsRegistered = false,
                Message = NxmProtocolStatusText
            };
        }

        var result = _nxmProtocolRegistrationService.TryRegister(executablePath);
        NxmProtocolStatusText = result.Message;

        if (updateStatusMessage)
        {
            StatusMessage = result.Message;
        }

        return result;
    }

    private async Task TryRefreshOAuthTokenSilentlyAsync()
    {
        if (!string.IsNullOrWhiteSpace(NexusOAuthAccessToken) && !string.IsNullOrWhiteSpace(NexusOAuthRefreshToken))
        {
            var validation = await _nexusOAuthService.ValidateAccessTokenAsync(NexusOAuthAccessToken);
            if (validation.IsSuccess)
            {
                return;
            }

            var refreshed = await RefreshOAuthTokenInternalAsync();
            if (refreshed)
            {
                NexusStatus = "已登录（OAuth Token 已自动刷新）";
                StatusMessage = "检测到过期 Token，已自动刷新";
                return;
            }

            NexusStatus = "登录已失效（OAuth Token 过期，请重新登录）";
            StatusMessage = "Nexus OAuth Token 已过期且无法自动刷新，请重新登录";
            return;
        }

        if (!string.IsNullOrWhiteSpace(NexusApiKey))
        {
            var apiKeyResult = await _nexusAuthService.ValidateApiKeyAsync(NexusApiKey);
            if (apiKeyResult.IsSuccess)
            {
                return;
            }

            if (apiKeyResult.Message.Contains("无效") || apiKeyResult.Message.Contains("过期"))
            {
                NexusStatus = "登录已失效（API Key 无效或已过期，请重新登录）";
                StatusMessage = "Nexus API Key 验证失败，请重新登录";
            }
        }
    }

    private async Task<bool> RefreshOAuthTokenInternalAsync()
    {
        var refreshResult = await _nexusOAuthService.RefreshAccessTokenAsync(NexusOAuthRefreshToken);
        if (!refreshResult.IsSuccess || refreshResult.Token == null)
        {
            return false;
        }

        NexusOAuthAccessToken = refreshResult.Token.AccessToken;
        if (!string.IsNullOrWhiteSpace(refreshResult.Token.RefreshToken))
        {
            NexusOAuthRefreshToken = refreshResult.Token.RefreshToken;
        }

        if (!string.IsNullOrWhiteSpace(refreshResult.Token.IdToken))
        {
            NexusOAuthIdToken = refreshResult.Token.IdToken;
        }

        if (!string.IsNullOrWhiteSpace(refreshResult.Profile.UserName))
        {
            NexusUserName = refreshResult.Profile.UserName;
            NexusMembershipType = refreshResult.Profile.MembershipType;
            NexusUserId = refreshResult.Profile.UserId;
        }

        _settingsStore.Save(BuildSettings());
        return true;
    }

    private static string ResolveThemeDisplayName(string styleName, string schemeName)
    {
        if (string.Equals(styleName, "Stardew", StringComparison.OrdinalIgnoreCase))
            return "星露谷（默认）";

        return schemeName switch
        {
            "Blue" => "天空蓝",
            "Green" => "森林绿",
            "Purple" => "薰衣紫",
            "Teal" => "海洋青",
            "Pink" => "樱花粉",
            "Orange" => "夕阳橙",
            _ => "天空蓝"
        };
    }

    private static void ResolveThemeStyleAndScheme(string displayName, out string styleName, out string schemeName)
    {
        if (string.Equals(displayName, "星露谷（默认）", StringComparison.Ordinal))
        {
            styleName = "Stardew";
            schemeName = "Blue";
            return;
        }

        styleName = "MaterialYou";
        schemeName = displayName switch
        {
            "天空蓝" => "Blue",
            "森林绿" => "Green",
            "薰衣紫" => "Purple",
            "海洋青" => "Teal",
            "樱花粉" => "Pink",
            "夕阳橙" => "Orange",
            _ => "Blue"
        };
    }
}
