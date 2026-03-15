using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using SVL.Core.Logging;
using SVL.Core.Stardew.Mod.SMAPI;

namespace SVL.Core.Stardew.Instance;

/// <summary>
/// 游戏路径信息（用于版本选择界面）
/// </summary>
public class GamePathInfo : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>
    /// 唯一标识
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    private string _name = string.Empty;
    /// <summary>
    /// 显示名称
    /// </summary>
    public string Name
    {
        get => _name;
        set
        {
            if (_name != value)
            {
                var oldName = _name;

                // 如果开启了版本隔离，重命名隔离目录
                if (EnableIsolation && !string.IsNullOrEmpty(oldName))
                {
                    try
                    {
                        Logging.Log.Info($"[GamePathInfo] Renaming instance: {oldName} -> {value}");

                        var success = InstanceIsolationService.RenameIsolationDirectory(
                            GamePath,
                            oldName,
                            value);

                        if (!success)
                        {
                            Logging.Log.Warn($"[GamePathInfo] Failed to rename isolation directory from {oldName} to {value}");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logging.Log.Error(ex, $"[GamePathInfo] Error renaming isolation directory: {oldName} -> {value}");
                        return;
                    }
                }

                _name = value;

                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// 游戏路径
    /// </summary>
    public string GamePath { get; set; } = string.Empty;

    /// <summary>
    /// 游戏版本（完整版本号）
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// 显示版本（如：Vanilla 1.6.0）
    /// </summary>
    public string DisplayVersion => FormatVersion(Version);

    private bool _isSMAPIInstance;
    /// <summary>
    /// 是否为 SMAPI 实例（启动方式为 StardewModdingAPI.exe）
    /// </summary>
    public bool IsSMAPIInstance
    {
        get => _isSMAPIInstance;
        set
        {
            if (_isSMAPIInstance != value)
            {
                _isSMAPIInstance = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// 是否为原版实例（启动方式为 Stardew Valley.exe）
    /// </summary>
    public bool IsVanillaInstance => !IsSMAPIInstance;

    /// <summary>
    /// SMAPI 版本（仅 SMAPI 实例有效）
    /// </summary>
    public string SMAPIVersion { get; set; } = string.Empty;

    /// <summary>
    /// 该路径是否安装了 SMAPI（用于判断是否可以创建 SMAPI 实例）
    /// </summary>
    public bool HasSMAPIInstalled { get; set; }

    private bool _isDefault;
    /// <summary>
    /// 是否为默认启动实例
    /// </summary>
    public bool IsDefault
    {
        get => _isDefault;
        set
        {
            if (_isDefault != value)
            {
                _isDefault = value;
                OnPropertyChanged();
            }
        }
    }

    private string? _customIcon;
    /// <summary>
    /// 自定义图标路径（如果为 null，则使用默认图标）
    /// </summary>
    public string? CustomIcon
    {
        get => _customIcon;
        set
        {
            if (_customIcon != value)
            {
                _customIcon = value;
                OnPropertyChanged();
            }
        }
    }

    private bool _isFavorite;
    /// <summary>
    /// 是否为收藏实例
    /// </summary>
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite != value)
            {
                _isFavorite = value;
                OnPropertyChanged();
            }
        }
    }

    private List<string> _tags = new();
    /// <summary>
    /// 标签列表（例如：["Base", "Latest"]）
    /// </summary>
    public List<string> Tags
    {
        get => _tags;
        set
        {
            if (_tags != value)
            {
                _tags = value ?? new List<string>();
                OnPropertyChanged();
            }
        }
    }

    private string _description = string.Empty;
    /// <summary>
    /// 实例描述
    /// </summary>
    public string Description
    {
        get => _description;
        set
        {
            if (_description != value)
            {
                _description = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// 获取显示的图标路径
    /// </summary>
    public string GetIconPath()
    {
        if (!string.IsNullOrEmpty(CustomIcon) && (CustomIcon.StartsWith("/") || File.Exists(CustomIcon)))
        {
            return CustomIcon;
        }

        // 回退：自动从版本隔离目录加载实例图标
        if (EnableIsolation && !string.IsNullOrEmpty(GamePath) && !string.IsNullOrEmpty(Name))
        {
            try
            {
                var versionPath = InstanceIsolationService.GetVersionPath(GamePath, Name);
                if (Directory.Exists(versionPath))
                {
                    var extensions = new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };
                    foreach (var ext in extensions)
                    {
                        var candidate = Path.Combine(versionPath, $".svl-instance-icon{ext}");
                        if (File.Exists(candidate))
                        {
                            CustomIcon = candidate;
                            return candidate;
                        }
                    }
                }
            }
            catch
            {
                // 忽略图标探测异常，回退默认图标
            }
        }

        return IsSMAPIInstance ? "/Images/Modded.png" : "/Images/Vanilla.png";
    }

    #region 实例设置

    private bool _enableIsolation = false;
    /// <summary>
    /// 版本隔离（Mod 独立，存档共享）
    /// </summary>
    public bool EnableIsolation
    {
        get => _enableIsolation;
        set
        {
            if (_enableIsolation != value)
            {
                _enableIsolation = value;
                OnPropertyChanged();
            }
        }
    }

    private string _windowTitle = string.Empty;
    /// <summary>
    /// 游戏窗口标题
    /// </summary>
    public string WindowTitle
    {
        get => _windowTitle;
        set
        {
            if (_windowTitle != value)
            {
                _windowTitle = value;
                OnPropertyChanged();
            }
        }
    }

    private string _customArguments = string.Empty;
    /// <summary>
    /// 自定义启动参数
    /// </summary>
    public string CustomArguments
    {
        get => _customArguments;
        set
        {
            if (_customArguments != value)
            {
                _customArguments = value;
                OnPropertyChanged();
            }
        }
    }

    private bool _autoConnectServer;
    /// <summary>
    /// 自动进入服务器
    /// </summary>
    public bool AutoConnectServer
    {
        get => _autoConnectServer;
        set
        {
            if (_autoConnectServer != value)
            {
                _autoConnectServer = value;
                OnPropertyChanged();
            }
        }
    }

    private string _serverAddress = string.Empty;
    /// <summary>
    /// 服务器地址
    /// </summary>
    public string ServerAddress
    {
        get => _serverAddress;
        set
        {
            if (_serverAddress != value)
            {
                _serverAddress = value;
                OnPropertyChanged();
            }
        }
    }

    private string _steamInviteCode = string.Empty;
    /// <summary>
    /// Steam 邀请码
    /// </summary>
    public string SteamInviteCode
    {
        get => _steamInviteCode;
        set
        {
            if (_steamInviteCode != value)
            {
                _steamInviteCode = value;
                OnPropertyChanged();
            }
        }
    }

    private bool _overrideSteamLaunchOptions;
    /// <summary>
    /// 是否覆写 Steam 启动参数为该实例
    /// </summary>
    public bool OverrideSteamLaunchOptions
    {
        get => _overrideSteamLaunchOptions;
        set
        {
            if (_overrideSteamLaunchOptions != value)
            {
                _overrideSteamLaunchOptions = value;
                OnPropertyChanged();
            }
        }
    }

    private string _steamLaunchOptions = string.Empty;
    /// <summary>
    /// Steam 启动参数覆写文本
    /// </summary>
    public string SteamLaunchOptions
    {
        get => _steamLaunchOptions;
        set
        {
            if (_steamLaunchOptions != value)
            {
                _steamLaunchOptions = value;
                OnPropertyChanged();
            }
        }
    }

    #endregion

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    }

    private static string FormatVersion(string fullVersion)
    {
        if (string.IsNullOrEmpty(fullVersion))
            return "Unknown";

        try
        {
            // 版本格式：1.6.15.24354
            var parts = fullVersion.Split('.');
            if (parts.Length >= 3)
            {
                return $"{parts[0]}.{parts[1]}.{parts[2]}";
            }
            return fullVersion;
        }
        catch
        {
            return fullVersion;
        }
    }
}

/// <summary>
/// 游戏路径条目（用于左侧路径列表）
/// </summary>
public class GamePathEntry
{
    /// <summary>
    /// 显示名称（例如：Stardew Valley）
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 游戏路径
    /// </summary>
    public string GamePath { get; set; } = string.Empty;

    /// <summary>
    /// 游戏版本
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// 该路径下的所有实例（Vanilla + SMAPI）
    /// </summary>
    public List<GamePathInfo> Instances { get; set; } = new();

    /// <summary>
    /// 显示版本
    /// </summary>
    public string DisplayVersion => string.IsNullOrEmpty(Version) ? "未知版本" : Version;
}

/// <summary>
/// 游戏实例服务
/// </summary>
public static class GamePathService
{
    /// <summary>
    /// 从游戏路径获取版本号
    /// </summary>
    public static string GetGameVersion(string gamePath)
    {
        if (string.IsNullOrEmpty(gamePath) || !Directory.Exists(gamePath))
            return string.Empty;

        try
        {
            // 方法1: 从 deps.json 读取（推荐）
            var depsPath = Path.Combine(gamePath, "Stardew Valley.deps.json");
            if (File.Exists(depsPath))
            {
                var json = JsonDocument.Parse(File.ReadAllText(depsPath));
                var targets = json.RootElement.GetProperty("targets");

                // 尝试获取 win-x64 目标
                if (targets.TryGetProperty(".NETCoreApp,Version=v6.0/win-x64", out var win64Target))
                {
                    foreach (var item in win64Target.EnumerateObject())
                    {
                        if (item.Name.StartsWith("Stardew Valley/"))
                        {
                            return item.Name.Split('/')[1];
                        }
                    }
                }

                // 备用：尝试 .NETCoreApp,Version=v6.0
                if (targets.TryGetProperty(".NETCoreApp,Version=v6.0", out var target))
                {
                    foreach (var item in target.EnumerateObject())
                    {
                        if (item.Name.StartsWith("Stardew Valley/"))
                        {
                            return item.Name.Split('/')[1];
                        }
                    }
                }
            }

            // 方法2: 从 DLL 文件版本读取（备用）
            var dllPath = Path.Combine(gamePath, "Stardew Valley.dll");
            if (File.Exists(dllPath))
            {
                var versionInfo = FileVersionInfo.GetVersionInfo(dllPath);
                return versionInfo.FileVersion ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"读取游戏版本失败：{ex.Message}\n路径：{gamePath}", "错误");
        }

        return string.Empty;
    }

    /// <summary>
    /// 检查是否安装了 SMAPI（与 IsValidGamePath 同样的检测逻辑）
    /// </summary>
    public static bool CheckSMAPI(string gamePath, out string smapiVersion)
    {
        smapiVersion = string.Empty;

        if (string.IsNullOrEmpty(gamePath) || !Directory.Exists(gamePath))
            return false;

        try
        {
            // 直接检查根目录的 StardewModdingAPI.exe（和 IsValidGamePath 一样的方式）
            var smapiExe = Path.Combine(gamePath, "StardewModdingAPI.exe");
            if (File.Exists(smapiExe))
            {
                smapiVersion = SmapApiService.GetInstalledSmapiVersion(gamePath) ?? "Unknown";
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SMAPI检测] 检测过程异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 检查游戏是否可运行（验证 exe 存在）
    /// </summary>
    public static bool IsValidGamePath(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return false;

        var exePath = Path.Combine(path, "Stardew Valley.exe");
        return File.Exists(exePath);
    }

    /// <summary>
    /// 从游戏路径创建路径信息（可能返回多个实例）
    /// </summary>
    public static List<GamePathInfo> CreateGamePathInfos(string gamePath, string name = null)
    {
        var results = new List<GamePathInfo>();
        var version = GetGameVersion(gamePath);
        var hasSMAPI = CheckSMAPI(gamePath, out var smapiVersion);

        // 基础名称
        var baseName = name ?? "Stardew Valley";

        if (hasSMAPI)
        {
            // 创建两个实例：原版和 SMAPI 版
            // 1. 原版实例
            results.Add(new GamePathInfo
            {
                Name = baseName,
                GamePath = gamePath,
                Version = version,
                IsSMAPIInstance = false,
                HasSMAPIInstalled = true,
                Tags = new List<string> { "Base" }
            });

            // 2. SMAPI 实例
            results.Add(new GamePathInfo
            {
                Name = $"{baseName} (SMAPI)",
                GamePath = gamePath,
                Version = version,
                IsSMAPIInstance = true,
                SMAPIVersion = smapiVersion,
                HasSMAPIInstalled = true,
                Tags = new List<string> { "Base" }
            });
        }
        else
        {
            // 只有原版
            results.Add(new GamePathInfo
            {
                Name = baseName,
                GamePath = gamePath,
                Version = version,
                IsSMAPIInstance = false,
                HasSMAPIInstalled = false,
                Tags = new List<string> { "Base" }
            });
        }

        return results;
    }

    /// <summary>
    /// 从游戏路径创建路径信息（保持向后兼容）
    /// </summary>
    public static GamePathInfo CreateGamePathInfo(string gamePath, string name = null)
    {
        var infos = CreateGamePathInfos(gamePath, name);
        return infos.Count > 0 ? infos[0] : new GamePathInfo();
    }

    /// <summary>
    /// 扫描versions文件夹中的版本隔离实例
    /// </summary>
    /// <param name="gamePath">游戏根目录</param>
    /// <param name="existingInstances">已存在的实例列表（用于避免重复）</param>
    /// <returns>发现的版本隔离实例列表</returns>
    public static List<GamePathInfo> ScanVersionIsolatedInstances(string gamePath, List<GamePathInfo>? existingInstances = null)
    {
        var results = new List<GamePathInfo>();
        existingInstances ??= new List<GamePathInfo>();

        try
        {
            var versionsPath = Path.Combine(gamePath, "versions");
            if (!Directory.Exists(versionsPath))
            {
                Log.Info($"[GamePathService] versions文件夹不存在: {versionsPath}");
                return results;
            }

            Log.Info($"[GamePathService] 开始扫描versions文件夹: {versionsPath}");
            Log.Info($"[GamePathService] 已有 {existingInstances.Count} 个实例: {string.Join(", ", existingInstances.Select(i => i.Name))}");

            // 获取所有子目录
            var versionFolders = Directory.GetDirectories(versionsPath);
            Log.Info($"[GamePathService] 发现 {versionFolders.Length} 个版本文件夹");

            foreach (var versionFolder in versionFolders)
            {
                try
                {
                    var folderName = Path.GetFileName(versionFolder);
                    Log.Info($"[GamePathService] 检查版本文件夹: {folderName}");

                    // 检查是否已经是已存在实例（通过名称判断）
                    var existingInstance = existingInstances.FirstOrDefault(i => i.Name == folderName);
                    if (existingInstance != null)
                    {
                        Log.Info($"[GamePathService] 版本 {folderName} 已存在于实例列表中（跳过添加）");
                        // 更新现有实例的隔离标记
                        if (!existingInstance.EnableIsolation)
                        {
                            existingInstance.EnableIsolation = true;
                            Log.Info($"[GamePathService] 更新实例 {folderName} 的隔离标记为true");
                        }
                        continue;
                    }

                    // 检查是否为有效的游戏目录（包含game链接或游戏文件）
                    var gameLinkPath = Path.Combine(versionFolder, "game");
                    var hasGameLink = Directory.Exists(gameLinkPath);
                    Log.Info($"[GamePathService] game链接存在: {hasGameLink} (路径: {gameLinkPath})");

                    // 检查是否是SMAPI实例
                    var smapiExePath = hasGameLink
                        ? Path.Combine(gameLinkPath, "StardewModdingAPI.exe")
                        : Path.Combine(versionFolder, "StardewModdingAPI.exe");
                    var isSMAPI = File.Exists(smapiExePath);
                    Log.Info($"[GamePathService] SMAPI存在: {isSMAPI} (路径: {smapiExePath})");

                    // 获取版本信息
                    var targetPath = hasGameLink ? gameLinkPath : versionFolder;
                    var version = GetGameVersion(targetPath);
                    var smapiVersion = isSMAPI ? GetSMAPIVersion(smapiExePath) : string.Empty;
                    Log.Info($"[GamePathService] 版本: {version}, SMAPI版本: {smapiVersion}");

                    // 创建新实例
                    var newInstance = new GamePathInfo
                    {
                        Id = Guid.NewGuid().ToString(),
                        Name = folderName,
                        GamePath = gamePath,
                        Version = version,
                        IsSMAPIInstance = isSMAPI,
                        SMAPIVersion = smapiVersion,
                        EnableIsolation = true,  // 标记为版本隔离
                        HasSMAPIInstalled = isSMAPI
                    };

                    results.Add(newInstance);
                    Log.Info($"[GamePathService] ✓ 发现版本隔离实例: {folderName} (SMAPI: {isSMAPI}, 版本: {version})");
                }
                catch (Exception ex)
                {
                    Log.Error($"[GamePathService] 处理版本文件夹失败: {versionFolder}", ex);
                }
            }

            Log.Info($"[GamePathService] 扫描完成，发现 {results.Count} 个新版本隔离实例");
        }
        catch (Exception ex)
        {
            Log.Error($"[GamePathService] 扫描versions文件夹失败", ex);
        }

        return results;
    }

    /// <summary>
    /// 获取SMAPI版本
    /// </summary>
    private static string GetSMAPIVersion(string smapiExePath)
    {
        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(smapiExePath);
            return versionInfo.FileVersion ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    /// <summary>
    /// 自动搜索游戏目录
    /// </summary>
    public static string[] AutoDetectGamePaths()
    {
        var results = new System.Collections.Generic.List<string>();
        var checkedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // 方法1: 从 Steam 注册表读取安装路径
            try
            {
                var steamPath = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam",
                    "InstallPath",
                    null) as string;

                if (!string.IsNullOrEmpty(steamPath) && Directory.Exists(steamPath))
                {
                    Log.Info($"[AutoDetect] 找到 Steam 安装路径: {steamPath}");

                    // 读取 Steam 库文件夹
                    var libraryFoldersPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                    if (File.Exists(libraryFoldersPath))
                    {
                        var libraryPaths = ParseSteamLibraryFolders(libraryFoldersPath);
                        foreach (var libPath in libraryPaths)
                        {
                            var gamePath = Path.Combine(libPath, "steamapps", "common", "Stardew Valley");
                            if (Directory.Exists(gamePath) && IsValidGamePath(gamePath) && !checkedPaths.Contains(gamePath))
                            {
                                results.Add(gamePath);
                                checkedPaths.Add(gamePath);
                                Log.Info($"[AutoDetect] 从 Steam 库找到游戏: {gamePath}");
                            }
                        }
                    }

                    // 也检查 Steam 默认路径
                    var defaultGamePath = Path.Combine(steamPath, "steamapps", "common", "Stardew Valley");
                    if (Directory.Exists(defaultGamePath) && IsValidGamePath(defaultGamePath) && !checkedPaths.Contains(defaultGamePath))
                    {
                        results.Add(defaultGamePath);
                        checkedPaths.Add(defaultGamePath);
                        Log.Info($"[AutoDetect] 从 Steam 默认路径找到游戏: {defaultGamePath}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[AutoDetect] 读取 Steam 注册表失败: {ex.Message}");
            }

            // 方法2: GOG Galaxy 注册表路径
            try
            {
                var gogPath = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\GOG.com\Games\1453375253",
                    "PATH",
                    null) as string;

                if (!string.IsNullOrEmpty(gogPath) && Directory.Exists(gogPath) && 
                    IsValidGamePath(gogPath) && !checkedPaths.Contains(gogPath))
                {
                    results.Add(gogPath);
                    checkedPaths.Add(gogPath);
                    Log.Info($"[AutoDetect] 从 GOG 注册表找到游戏: {gogPath}");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[AutoDetect] 读取 GOG 注册表失败: {ex.Message}");
            }

            // 方法3: Xbox Game Pass 路径
            try
            {
                var xboxPaths = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ModifiableWindowsApps", "StardewValley"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ModifiableWindowsApps", "StardewValley"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "StardewValley")
                };

                foreach (var xboxPath in xboxPaths)
                {
                    if (Directory.Exists(xboxPath) && IsValidGamePath(xboxPath) && !checkedPaths.Contains(xboxPath))
                    {
                        // Xbox 版本的游戏可能在子目录中
                        var contentPath = Path.Combine(xboxPath, "Content");
                        if (Directory.Exists(contentPath) && IsValidGamePath(contentPath))
                        {
                            results.Add(contentPath);
                            checkedPaths.Add(contentPath);
                            Log.Info($"[AutoDetect] 从 Xbox Game Pass 找到游戏: {contentPath}");
                        }
                        else
                        {
                            results.Add(xboxPath);
                            checkedPaths.Add(xboxPath);
                            Log.Info($"[AutoDetect] 从 Xbox Game Pass 找到游戏: {xboxPath}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[AutoDetect] 检查 Xbox Game Pass 路径失败: {ex.Message}");
            }

            // 方法4: 扫描所有驱动器的常见路径
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;

                try
                {
                    // 搜索常见的安装位置
                    var searchPaths = new[]
                    {
                        Path.Combine(drive.Name, "Games", "Stardew Valley"),
                        Path.Combine(drive.Name, "Games", "StardewValley"),
                        Path.Combine(drive.Name, "Game", "Stardew Valley"),
                        Path.Combine(drive.Name, "Stardew Valley"),
                        Path.Combine(drive.Name, "StardewValley"),
                        Path.Combine(drive.Name, "Program Files (x86)", "Steam", "steamapps", "common", "Stardew Valley"),
                        Path.Combine(drive.Name, "Program Files", "Steam", "steamapps", "common", "Stardew Valley"),
                        Path.Combine(drive.Name, "SteamLibrary", "steamapps", "common", "Stardew Valley"),
                        Path.Combine(drive.Name, "Steam", "steamapps", "common", "Stardew Valley"),
                    };

                    foreach (var path in searchPaths)
                    {
                        if (Directory.Exists(path) && IsValidGamePath(path) && !checkedPaths.Contains(path))
                        {
                            results.Add(path);
                            checkedPaths.Add(path);
                            Log.Info($"[AutoDetect] 从驱动器扫描找到游戏: {path}");
                        }
                    }
                }
                catch
                {
                    // 忽略无法访问的驱动器
                }
            }

            Log.Info($"[AutoDetect] 检测完成，共找到 {results.Count} 个游戏路径");
        }
        catch (Exception ex)
        {
            Log.Error($"[AutoDetect] 自动检测失败: {ex.Message}");
        }

        return results.ToArray();
    }

    /// <summary>
    /// 解析 Steam libraryfolders.vdf 文件
    /// </summary>
    private static List<string> ParseSteamLibraryFolders(string vdfPath)
    {
        var results = new List<string>();

        try
        {
            var lines = File.ReadAllLines(vdfPath);
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                // 查找 "path" 行
                if (trimmedLine.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = trimmedLine.Split('"');
                    if (parts.Length >= 4)
                    {
                        var path = parts[3].Replace(@"\\", @"\");
                        if (Directory.Exists(path))
                        {
                            results.Add(path);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[AutoDetect] 解析 Steam 库文件失败: {ex.Message}");
        }

        return results;
    }

    /// <summary>
    /// 将实例列表按路径分组
    /// </summary>
    public static List<GamePathEntry> GroupInstancesByPath(List<GamePathInfo> instances)
    {
        var pathEntries = new List<GamePathEntry>();

        // 按路径分组
        var grouped = instances.GroupBy(i => i.GamePath);

        foreach (var group in grouped)
        {
            var firstInstance = group.First();

            var entry = new GamePathEntry
            {
                DisplayName = Path.GetFileName(group.Key), // 使用文件夹名称作为显示名称
                GamePath = group.Key,
                Version = firstInstance.Version,
                Instances = group.ToList()
            };

            pathEntries.Add(entry);
        }

        return pathEntries;
    }
}
