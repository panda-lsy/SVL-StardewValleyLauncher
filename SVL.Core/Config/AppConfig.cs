using System;
using System.IO;
using System.Text.Json;
using SVL.Core.Logging;
using SVL.Core.Security;

namespace SVL.Core.Config;

/// <summary>
/// 应用配置服务
/// </summary>
public static class AppConfig
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SVL",
        "app.json");

    private static AppSettings? _cachedSettings;

    /// <summary>
    /// 获取应用设置
    /// </summary>
    public static AppSettings GetSettings()
    {
        if (_cachedSettings != null)
        {
            return _cachedSettings;
        }

        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null)
                {
                    // 解密 API Keys
                    if (!string.IsNullOrEmpty(settings.CurseforgeApiKey))
                    {
                        settings.CurseforgeApiKey = SecureString.Decrypt(settings.CurseforgeApiKey);
                    }
                    if (!string.IsNullOrEmpty(settings.NexusModsApiKey))
                    {
                        settings.NexusModsApiKey = SecureString.Decrypt(settings.NexusModsApiKey);
                    }

                    if (!string.IsNullOrEmpty(settings.NexusModsPassword))
                    {
                        settings.NexusModsPassword = SecureString.Decrypt(settings.NexusModsPassword);
                    }

                    // OAuth Token 不需要加密

                    _cachedSettings = settings;
                    Log.Info("[AppConfig] ✓ 已加载应用配置");
                    return _cachedSettings;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[AppConfig] 加载应用配置失败");
        }

        // 返回默认设置
        _cachedSettings = new AppSettings();
        return _cachedSettings;
    }

    /// <summary>
    /// 保存应用设置
    /// </summary>
    public static bool SaveSettings(AppSettings settings)
    {
        try
        {
            // 创建副本并加密 API Keys
            var settingsToSave = new AppSettings
            {
                GameWindowTitle = settings.GameWindowTitle,
                LauncherTitle = settings.LauncherTitle,
                LauncherAppName = settings.LauncherAppName,
                LauncherVisibility = settings.LauncherVisibility,
                WindowSizeMode = settings.WindowSizeMode,
                CustomWindowWidth = settings.CustomWindowWidth,
                CustomWindowHeight = settings.CustomWindowHeight,

                // 默认下载源
                SmapiDefaultSource = settings.SmapiDefaultSource,
                ModDefaultSource = settings.ModDefaultSource,
                MaxConcurrentModDownloads = settings.MaxConcurrentModDownloads,

                // NexusMods
                EnableNexusModsSearchCache = settings.EnableNexusModsSearchCache,
                EnableDownloadCache = settings.EnableDownloadCache,

                // 缓存
                CacheRetentionMinutes = settings.CacheRetentionMinutes,

                // 加密 API Keys
                CurseforgeApiKey = !string.IsNullOrEmpty(settings.CurseforgeApiKey)
                    ? SecureString.Encrypt(settings.CurseforgeApiKey)
                    : null,
                NexusModsApiKey = !string.IsNullOrEmpty(settings.NexusModsApiKey)
                    ? SecureString.Encrypt(settings.NexusModsApiKey)
                    : null,
                NexusModsEmail = settings.NexusModsEmail,  // 邮箱不需要加密
                NexusModsPassword = !string.IsNullOrEmpty(settings.NexusModsPassword)
                    ? SecureString.Encrypt(settings.NexusModsPassword)
                    : null,

                // OAuth 登录用户信息（不需要加密）
                NexusModsOAuthToken = settings.NexusModsOAuthToken,
                NexusModsOAuthRefreshToken = settings.NexusModsOAuthRefreshToken,
                NexusModsOAuthIdToken = settings.NexusModsOAuthIdToken,
                NexusModsOAuthUserName = settings.NexusModsOAuthUserName,
                NexusModsOAuthMembershipType = settings.NexusModsOAuthMembershipType,
                NexusModsOAuthAvatarUrl = settings.NexusModsOAuthAvatarUrl,
                NexusModsOAuthAvatarLocalPath = settings.NexusModsOAuthAvatarLocalPath,

                ThemeMode = settings.ThemeMode,
                ThemeStyleName = settings.ThemeStyleName,
                ThemeColorScheme = settings.ThemeColorScheme,
                PrimaryColor = settings.PrimaryColor,
                Language = settings.Language,
                EnableAnimations = settings.EnableAnimations,
                EnableTransparency = settings.EnableTransparency,
                FontSize = settings.FontSize,
                AutoCheckUpdates = settings.AutoCheckUpdates,
                MinimizeToTrayOnStartup = settings.MinimizeToTrayOnStartup,
                MinimizeToTrayOnClose = settings.MinimizeToTrayOnClose,
                ShowNotifications = settings.ShowNotifications,
                LogLevel = settings.LogLevel,
                DebugMode = settings.DebugMode,

                // 启动器更新设置（之前漏掉的字段）
                AutoDownloadUpdate = settings.AutoDownloadUpdate,
                ShowUpdateNotification = settings.ShowUpdateNotification,
                PreferredUpdateSource = settings.PreferredUpdateSource,
                SkippedUpdateVersion = settings.SkippedUpdateVersion,
                CheckPrereleaseUpdates = settings.CheckPrereleaseUpdates
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(settingsToSave, options);

            var directory = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(ConfigPath, json);
            _cachedSettings = settings;

            Log.Info("[AppConfig] ✓ 已保存应用配置（API Keys 已加密）");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[AppConfig] 保存应用配置失败");
            return false;
        }
    }

    /// <summary>
    /// 清除缓存的设置
    /// </summary>
    public static void ClearCache()
    {
        _cachedSettings = null;
    }
}

/// <summary>
/// 应用设置模型
/// </summary>
public class AppSettings
{
    // ===== 基本设置 =====

    /// <summary>
    /// 游戏窗口标题
    /// </summary>
    public string GameWindowTitle { get; set; } = "Stardew Valley";

    /// <summary>
    /// 自定义启动器标题
    /// </summary>
    public string LauncherTitle { get; set; } = "Stardew Valley Launcher";

    /// <summary>
    /// 启动器应用名称（显示在左上角 Logo 旁边）
    /// </summary>
    public string LauncherAppName { get; set; } = "SVL";

    /// <summary>
    /// 启动器可见性行为
    /// </summary>
    public LauncherVisibilityBehavior LauncherVisibility { get; set; } = LauncherVisibilityBehavior.Minimize;

    /// <summary>
    /// 窗口大小模式
    /// </summary>
    public WindowSizeMode WindowSizeMode { get; set; } = WindowSizeMode.Default;

    /// <summary>
    /// 自定义窗口宽度
    /// </summary>
    public int CustomWindowWidth { get; set; } = 1280;

    /// <summary>
    /// 自定义窗口高度
    /// </summary>
    public int CustomWindowHeight { get; set; } = 720;

    // ===== API 设置 =====

    /// <summary>
    /// Curseforge API Key
    /// </summary>
    public string? CurseforgeApiKey { get; set; }

    /// <summary>
    /// NexusMods API Key
    /// </summary>
    public string? NexusModsApiKey { get; set; }

    /// <summary>
    /// NexusMods 邮箱或用户名（默认账号）
    /// </summary>
    public string? NexusModsEmail { get; set; }

    /// <summary>
    /// NexusMods 密码（加密存储）
    /// </summary>
    public string? NexusModsPassword { get; set; }

    /// <summary>
    /// NexusMods OAuth 访问令牌
    /// </summary>
    public string? NexusModsOAuthToken { get; set; }

    /// <summary>
    /// NexusMods OAuth 刷新令牌
    /// </summary>
    public string? NexusModsOAuthRefreshToken { get; set; }

    /// <summary>
    /// NexusMods OAuth ID Token (JWT，包含用户信息)
    /// </summary>
    public string? NexusModsOAuthIdToken { get; set; }

    /// <summary>
    /// NexusMods 用户名（通过 OAuth 登录后自动填充）
    /// </summary>
    public string? NexusModsOAuthUserName { get; set; }

    /// <summary>
    /// NexusMods 会员类型（通过 OAuth 登录后自动填充）
    /// </summary>
    public string? NexusModsOAuthMembershipType { get; set; }

    /// <summary>
    /// NexusMods 用户头像URL（通过 OAuth 登录后自动填充）
    /// </summary>
    public string? NexusModsOAuthAvatarUrl { get; set; }

    /// <summary>
    /// NexusMods 用户本地头像路径（缓存）
    /// </summary>
    public string? NexusModsOAuthAvatarLocalPath { get; set; }

    /// <summary>
    /// 是否为 NexusMods Premium 用户（根据 MembershipType 自动判断）
    /// </summary>
    public bool IsNexusModsPremium
    {
        get
        {
            if (string.IsNullOrEmpty(NexusModsOAuthMembershipType))
                return false;

            var type = NexusModsOAuthMembershipType.ToLower();
            return type.Contains("premium") || type.Contains("supporter") || type.Contains("lifetime");
        }
    }

    // ===== 个性化设置 =====

    /// <summary>
    /// 主题模式
    /// </summary>
    public ThemeMode ThemeMode { get; set; } = ThemeMode.System;

    /// <summary>
    /// 主题风格名称（Stardew / MaterialYou）
    /// </summary>
    public string ThemeStyleName { get; set; } = "Stardew";

    /// <summary>
    /// Material You 配色方案名称（Blue / Green / Purple / Teal / Pink / Orange）
    /// </summary>
    public string ThemeColorScheme { get; set; } = "Blue";

    /// <summary>
    /// 主色调
    /// </summary>
    public string PrimaryColor { get; set; } = "#7C4DFF";

    /// <summary>
    /// 语言
    /// </summary>
    public string Language { get; set; } = "zh-CN";

    /// <summary>
    /// 启用动画
    /// </summary>
    public bool EnableAnimations { get; set; } = true;

    /// <summary>
    /// 启用透明效果
    /// </summary>
    public bool EnableTransparency { get; set; } = true;

    /// <summary>
    /// 字体大小
    /// </summary>
    public int FontSize { get; set; } = 14;

    // ===== 其他设置 =====

    /// <summary>
    /// 自动检查更新
    /// </summary>
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>
    /// 启动时最小化到托盘
    /// </summary>
    public bool MinimizeToTrayOnStartup { get; set; } = false;

    /// <summary>
    /// 关闭时最小化到托盘
    /// </summary>
    public bool MinimizeToTrayOnClose { get; set; } = false;

    /// <summary>
    /// 显示通知
    /// </summary>
    public bool ShowNotifications { get; set; } = true;

    /// <summary>
    /// SMAPI 默认下载源
    /// </summary>
    public string? SmapiDefaultSource { get; set; } = "全部";

    /// <summary>
    /// Mod 默认下载源
    /// </summary>
    public string? ModDefaultSource { get; set; } = "全部";

    /// <summary>
    /// 整合包安装时的最大并发下载数（1-10）
    /// </summary>
    public int MaxConcurrentModDownloads { get; set; } = 3;

    /// <summary>
    /// 启用 NexusMods 搜索结果缓存（搜索记录缓存）
    /// </summary>
    public bool EnableNexusModsSearchCache { get; set; } = false;

    /// <summary>
    /// 启用下载文件缓存
    /// </summary>
    public bool EnableDownloadCache { get; set; } = true;

    /// <summary>
    /// 缓存时长（分钟），用于搜索缓存/下载缓存等 TTL 控制。
    /// 默认 60 分钟。
    /// </summary>
    public int CacheRetentionMinutes { get; set; } = 60;

    /// <summary>
    /// 日志级别
    /// </summary>
    public Logging.LogLevel LogLevel { get; set; } = Logging.LogLevel.Info;

    /// <summary>
    /// 启用调试模式
    /// </summary>
    public bool DebugMode { get; set; } = false;

    // ===== 启动器更新设置 =====

    /// <summary>
    /// 有新版本时自动下载更新
    /// </summary>
    public bool AutoDownloadUpdate { get; set; } = false;

    /// <summary>
    /// 有新版本时显示提示
    /// </summary>
    public bool ShowUpdateNotification { get; set; } = true;

    /// <summary>
    /// 首选更新源 (0=GitHub, 1=Gitee)
    /// </summary>
    public int PreferredUpdateSource { get; set; } = 0;

    /// <summary>
    /// 跳过的更新版本号（用户点击"此版本不再提醒"后记录）
    /// </summary>
    public string? SkippedUpdateVersion { get; set; }

    /// <summary>
    /// 检查预发布版本更新（默认关闭）
    /// </summary>
    public bool CheckPrereleaseUpdates { get; set; } = false;
}

// ===== 枚举定义 =====

/// <summary>
/// 启动器可见性行为
/// </summary>
public enum LauncherVisibilityBehavior
{
    /// <summary>
    /// 游戏启动后立即关闭
    /// </summary>
    CloseImmediately,

    /// <summary>
    /// 游戏启动后隐藏，游戏退出后自动关闭
    /// </summary>
    HideAndCloseOnExit,

    /// <summary>
    /// 游戏启动后隐藏，游戏退出后重新打开
    /// </summary>
    HideAndRestoreOnExit,

    /// <summary>
    /// 游戏启动后最小化
    /// </summary>
    Minimize,

    /// <summary>
    /// 游戏启动后仍保持不变
    /// </summary>
    KeepUnchanged
}

/// <summary>
/// 窗口大小模式
/// </summary>
public enum WindowSizeMode
{
    /// <summary>
    /// 全屏
    /// </summary>
    Fullscreen,

    /// <summary>
    /// 默认
    /// </summary>
    Default,

    /// <summary>
    /// 与启动器尺寸一致
    /// </summary>
    SameAsLauncher,

    /// <summary>
    /// 自定义尺寸
    /// </summary>
    Custom,

    /// <summary>
    /// 最大化
    /// </summary>
    Maximized
}

/// <summary>
/// 主题模式
/// </summary>
public enum ThemeMode
{
    /// <summary>
    /// 浅色
    /// </summary>
    Light,

    /// <summary>
    /// 深色
    /// </summary>
    Dark,

    /// <summary>
    /// 跟随系统
    /// </summary>
    System
}
