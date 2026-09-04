namespace SVL.Avalonia.Models;

public sealed class AppUserSettings
{
    public string GameWindowTitle { get; set; } = "<default>";

    public string InstanceCustomLaunchArguments { get; set; } = string.Empty;

    public bool InstanceAutoConnectServer { get; set; }

    public string InstanceServerAddress { get; set; } = string.Empty;

    public string InstanceSteamInviteCode { get; set; } = string.Empty;

    public string LauncherTitle { get; set; } = "Stardew Valley Launcher";

    public string LauncherAppName { get; set; } = "SVL";

    public bool EnableDownloadCache { get; set; } = true;

    public bool EnableDownloadProxy { get; set; }

    public string DownloadProxyUrl { get; set; } = string.Empty;

    public string DownloadProxyUserName { get; set; } = string.Empty;

    public string DownloadProxyPassword { get; set; } = string.Empty;

    public bool EnableDownloadFloatingTaskButton { get; set; } = true;

    public bool EnableAutoUpdateCheck { get; set; } = true;

    public string UpdateChannel { get; set; } = "稳定版";

    public string PreferredUpdateSource { get; set; } = "GitHub (推荐)";

    public string SkippedLauncherVersion { get; set; } = string.Empty;

    public bool RegisterNxmProtocolOnStartup { get; set; } = true;

    public string CollectionInstallConflictStrategy { get; set; } = "覆盖";

    public int CollectionDownloadParallelism { get; set; } = 4;

    public string ThemeMode { get; set; } = "跟随系统";

    public string ThemeStyleName { get; set; } = "Stardew";

    public string ThemeColorScheme { get; set; } = "Blue";

    public string UiLanguage { get; set; } = "zh-CN";

    public bool ShowNotifications { get; set; } = true;

    public bool DebugMode { get; set; }

    public string LogLevel { get; set; } = "Info";

    public bool MinimizeToTrayOnStartup { get; set; }

    public bool MinimizeToTrayOnClose { get; set; }

    public int SettingsTabIndex { get; set; } = 0;

    public string InstanceName { get; set; } = "Default Instance";

    public string InstanceDescription { get; set; } = string.Empty;

    public bool IsFavoriteInstance { get; set; }

    public string PreferredInstancePath { get; set; } = string.Empty;

    public List<string> FavoriteInstanceKeys { get; set; } = [];

    public bool OverrideSteamLaunchOptions { get; set; }

    public string SteamLaunchOptions { get; set; } = string.Empty;

    public string PreferredLaunchMode { get; set; } = "自动";

    public bool EnableSafeLaunch { get; set; }

    public string NexusApiKey { get; set; } = string.Empty;

    public string NexusOAuthAccessToken { get; set; } = string.Empty;

    public string NexusOAuthRefreshToken { get; set; } = string.Empty;

    public string NexusOAuthIdToken { get; set; } = string.Empty;

    public string NexusUserName { get; set; } = string.Empty;

    public string NexusMembershipType { get; set; } = string.Empty;

    public int NexusUserId { get; set; }

    /// <summary>是否永久屏蔽 NexusMods 登录失效提醒（可在设置页重新开启）。</summary>
    public bool SuppressNexusAuthNotification { get; set; }

    /// <summary>窗口尺寸模式：默认/最大化/自定义。</summary>
    public string WindowSizeMode { get; set; } = "默认";

    /// <summary>自定义窗口宽度（仅 WindowSizeMode=自定义 时生效）。</summary>
    public int CustomWindowWidth { get; set; } = 1050;

    /// <summary>自定义窗口高度（仅 WindowSizeMode=自定义 时生效）。</summary>
    public int CustomWindowHeight { get; set; } = 680;

    /// <summary>是否启用动画效果。</summary>
    public bool EnableAnimations { get; set; } = true;

    /// <summary>界面字体大小（pt）。</summary>
    public int FontSize { get; set; } = 14;

    /// <summary>下载分段线程数（1-16）。</summary>
    public int DownloadSegmentThreads { get; set; } = 4;

    /// <summary>Mod 更新检测并发数。</summary>
    public int MaxConcurrentModUpdateChecks { get; set; } = 4;

    /// <summary>SMAPI 默认下载源（GitHub/NexusMods）。</summary>
    public string DefaultSmapiSource { get; set; } = "GitHub";

    /// <summary>Mod 默认下载源（NexusMods/CurseForge）。</summary>
    public string DefaultModSource { get; set; } = "NexusMods";

    /// <summary>是否启用 NexusMods 搜索缓存。</summary>
    public bool EnableNexusModsSearchCache { get; set; } = true;

    /// <summary>NexusMods 搜索缓存保留时长（分钟）。</summary>
    public int CacheRetentionMinutes { get; set; } = 5;

    /// <summary>社区汉化首选源（GitHub / Gitee），默认 Gitee（国内访问更稳定）。</summary>
    public string LocalizationPreferredSource { get; set; } = "Gitee";
}
