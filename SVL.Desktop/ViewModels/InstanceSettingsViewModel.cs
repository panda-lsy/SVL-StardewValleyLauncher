using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SVL.Core.Stardew.Instance;
using SVL.Desktop.Controls;

namespace SVL.Desktop.ViewModels;

/// <summary>
/// 版本设置 - 设置页面的 ViewModel
/// </summary>
public partial class InstanceSettingsViewModel : ObservableObject
{
    private MainWindowViewModel _mainViewModel;
    private GamePathInfo _instance;

    public InstanceSettingsViewModel(MainWindowViewModel mainViewModel, GamePathInfo instance)
    {
        _mainViewModel = mainViewModel;
        _instance = instance;

        LoadSettings();
    }

    /// <summary>
    /// 游戏窗口标题
    /// </summary>
    [ObservableProperty]
    private string _windowTitle = string.Empty;

    /// <summary>
    /// 自定义启动参数
    /// </summary>
    [ObservableProperty]
    private string _customArguments = string.Empty;

    /// <summary>
    /// 自动进入服务器
    /// </summary>
    [ObservableProperty]
    private bool _autoConnectServer;

    /// <summary>
    /// 服务器地址
    /// </summary>
    [ObservableProperty]
    private string _serverAddress = string.Empty;

    /// <summary>
    /// Steam 邀请码
    /// </summary>
    [ObservableProperty]
    private string _steamInviteCode = string.Empty;

    /// <summary>
    /// 是否覆写 Steam 启动参数为该实例
    /// </summary>
    [ObservableProperty]
    private bool _overrideSteamLaunchOptions;

    /// <summary>
    /// Steam 启动参数覆写文本
    /// </summary>
    [ObservableProperty]
    private string _steamLaunchOptions = string.Empty;

    /// <summary>
    /// 是否显示切换到 SMAPI 提示（Base 原版且已安装 SMAPI）
    /// </summary>
    public bool ShowSwitchToSMAPITip => !_instance.IsSMAPIInstance && _instance.HasSMAPIInstalled;

    /// <summary>
    /// 切换到 SMAPI 提示文本
    /// </summary>
    public string SwitchToSMAPITipText => "检测到该路径已安装SMAPI，切换到SMAPI 版本以启用Mod管理功能。";

    public string DefaultSteamLaunchOptions => BuildDefaultSteamLaunchOptions();

    public string SteamLaunchOptionsPreview
    {
        get
        {
            var options = (SteamLaunchOptions ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(options))
                options = DefaultSteamLaunchOptions;

            return string.IsNullOrWhiteSpace(options)
                ? "（无法生成默认参数，请确认实例路径）"
                : options;
        }
    }

    /// <summary>
    /// 从实例加载设置
    /// </summary>
    private void LoadSettings()
    {
        if (!_instance.IsSMAPIInstance && _instance.EnableIsolation)
        {
            _instance.EnableIsolation = false;
        }

        // 从实例配置加载设置
        WindowTitle = _instance.WindowTitle;
        CustomArguments = _instance.CustomArguments;
        AutoConnectServer = _instance.AutoConnectServer;
        ServerAddress = _instance.ServerAddress;
        SteamInviteCode = _instance.SteamInviteCode;
        OverrideSteamLaunchOptions = _instance.OverrideSteamLaunchOptions;
        SteamLaunchOptions = _instance.SteamLaunchOptions;

        if (string.IsNullOrWhiteSpace(SteamLaunchOptions))
        {
            SteamLaunchOptions = DefaultSteamLaunchOptions;
        }

        // 如果窗口标题为空，使用默认模板
        if (string.IsNullOrEmpty(WindowTitle))
        {
            WindowTitle = SVL.Core.Stardew.Launch.WindowTitlePlaceholderService.GetDefaultTitleTemplate(_instance.IsSMAPIInstance);
        }

        OnPropertyChanged(nameof(SteamLaunchOptionsPreview));
        OnPropertyChanged(nameof(DefaultSteamLaunchOptions));
    }

    /// <summary>
    /// 保存设置到实例配置
    /// </summary>
    [RelayCommand]
    private void SaveSettings()
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"[InstanceSettings] Saving settings for instance: {_instance.Id}");

            // 保存设置到实例对象
            _instance.WindowTitle = WindowTitle;
            _instance.CustomArguments = CustomArguments;
            _instance.AutoConnectServer = AutoConnectServer;
            _instance.ServerAddress = ServerAddress;
            _instance.SteamInviteCode = SteamInviteCode;
            _instance.OverrideSteamLaunchOptions = OverrideSteamLaunchOptions;
            _instance.SteamLaunchOptions = SteamLaunchOptions;

            // 重新加载所有实例，更新当前实例，然后保存
            var allInstances = SettingsService.LoadInstances();
            System.Diagnostics.Debug.WriteLine($"[InstanceSettings] Loaded {allInstances.Count} instances from config");

            // 查找并更新对应的实例
            var existingInstance = allInstances.FirstOrDefault(i => i.Id == _instance.Id);
            if (existingInstance != null)
            {
                System.Diagnostics.Debug.WriteLine($"[InstanceSettings] Found existing instance: {existingInstance.Name}");

                // 更新已存在的实例
                existingInstance.WindowTitle = _instance.WindowTitle;
                existingInstance.CustomArguments = _instance.CustomArguments;
                existingInstance.AutoConnectServer = _instance.AutoConnectServer;
                existingInstance.ServerAddress = _instance.ServerAddress;
                existingInstance.SteamInviteCode = _instance.SteamInviteCode;
                existingInstance.OverrideSteamLaunchOptions = _instance.OverrideSteamLaunchOptions;
                existingInstance.SteamLaunchOptions = _instance.SteamLaunchOptions;

                System.Diagnostics.Debug.WriteLine($"[InstanceSettings] Updated instance settings: WindowTitle={existingInstance.WindowTitle}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[InstanceSettings] Instance not found in config, adding it");
                // 如果实例不存在，添加到列表
                allInstances.Add(_instance);
            }

            // 保存到配置文件
            SettingsService.SaveInstances(allInstances);
            System.Diagnostics.Debug.WriteLine($"[InstanceSettings] Saved instances to config file");

            SvlMessageBox.Success("设置已保存");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[InstanceSettings] Save failed: {ex.Message}");
            SvlMessageBox.Error($"保存设置失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 重置为默认值
    /// </summary>
    [RelayCommand]
    private void ResetToDefault()
    {
        if (SvlMessageBox.Confirm(
            "确定要重置为默认设置吗？",
            "确认重置"))
        {
            WindowTitle = SVL.Core.Stardew.Launch.WindowTitlePlaceholderService.GetDefaultTitleTemplate(_instance.IsSMAPIInstance);
            CustomArguments = string.Empty;
            AutoConnectServer = false;
            ServerAddress = string.Empty;
            SteamInviteCode = string.Empty;
            OverrideSteamLaunchOptions = false;
            SteamLaunchOptions = string.Empty;

            System.Diagnostics.Debug.WriteLine($"[InstanceSettings] Reset to default: WindowTitle={WindowTitle}");
        }
    }

    partial void OnOverrideSteamLaunchOptionsChanged(bool value)
    {
        OnPropertyChanged(nameof(SteamLaunchOptionsPreview));
    }

    partial void OnSteamLaunchOptionsChanged(string value)
    {
        OnPropertyChanged(nameof(SteamLaunchOptionsPreview));
    }

    partial void OnCustomArgumentsChanged(string value)
    {
        OnPropertyChanged(nameof(SteamLaunchOptionsPreview));
        OnPropertyChanged(nameof(DefaultSteamLaunchOptions));
    }

    partial void OnWindowTitleChanged(string value)
    {
        OnPropertyChanged(nameof(DefaultSteamLaunchOptions));
    }

    [RelayCommand]
    private async Task WriteSteamLaunchOptionsAsync()
    {
        try
        {
            var options = (SteamLaunchOptions ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(options))
            {
                options = DefaultSteamLaunchOptions;
            }

            if (string.IsNullOrWhiteSpace(options))
            {
                SvlMessageBox.Error("无法生成 Steam 启动参数，请确认游戏路径和实例配置。", "写入失败");
                return;
            }

            var steamWasRunning = IsSteamRunning();
            var steamClosedByLauncher = false;
            if (steamWasRunning)
            {
                var approved = SvlMessageBox.Confirm(
                    "检测到 Steam 正在运行。\n\n需要先关闭 Steam 才能可靠写入启动参数。\n\n是否允许 SVL 自动关闭 Steam，写入后再重启 Steam？",
                    "需要关闭 Steam");
                if (!approved)
                {
                    return;
                }

                var closeResult = await Task.Run(TryCloseSteam);
                if (!closeResult.Success)
                {
                    SvlMessageBox.Error(closeResult.ErrorMessage, "写入失败");
                    return;
                }

                steamClosedByLauncher = true;
            }

            var writeResult = await Task.Run(() => TryWriteLaunchOptionsToSteamUserConfig(options));
            if (!writeResult.Success)
            {
                SvlMessageBox.Error(writeResult.ErrorMessage, "写入失败");
                return;
            }

            OverrideSteamLaunchOptions = true;
            SteamLaunchOptions = options;

            _instance.OverrideSteamLaunchOptions = OverrideSteamLaunchOptions;
            _instance.SteamLaunchOptions = SteamLaunchOptions;
            SaveInstanceToConfig();

            if (steamClosedByLauncher)
            {
                var restartResult = await Task.Run(TryStartSteam);
                if (!restartResult.Success)
                {
                    SvlMessageBox.Warning(
                        $"启动参数已写入，但自动重启 Steam 失败：{restartResult.ErrorMessage}\n\n请手动启动 Steam。",
                        "写入成功");
                    return;
                }
            }

            SvlMessageBox.Success($"已写入 Steam 启动参数（修改 {writeResult.UpdatedFileCount} 个配置，已匹配 {writeResult.MatchedFileCount} 个账号）。");
        }
        catch (Exception ex)
        {
            SvlMessageBox.Error($"写入 Steam 启动参数失败：{ex.Message}", "写入失败");
        }
    }

    private string BuildDefaultSteamLaunchOptions()
    {
        if (string.IsNullOrWhiteSpace(_instance.GamePath))
            return string.Empty;

        string smapiBasePath;
        smapiBasePath = _instance.EnableIsolation
            ? InstanceIsolationService.GetVersionPath(
                _instance.GamePath,
                InstanceIsolationService.GenerateVersionFolderName(_instance.Name, _instance.IsSMAPIInstance))
            : _instance.GamePath;

        var smapiExePath = Path.Combine(smapiBasePath, "StardewModdingAPI.exe");
        if (!File.Exists(smapiExePath))
        {
            var rootFallback = Path.Combine(_instance.GamePath, "StardewModdingAPI.exe");
            smapiExePath = File.Exists(rootFallback) ? rootFallback : smapiExePath;
        }

        var optionsBuilder = new StringBuilder($"\"{smapiExePath}\" %command%");
        var customArgs = (CustomArguments ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(customArgs))
        {
            optionsBuilder.Append(' ');
            optionsBuilder.Append(customArgs);
        }

        return optionsBuilder.ToString();
    }

    private static bool IsSteamRunning()
    {
        return Process.GetProcessesByName("steam").Length > 0;
    }

    private static (bool Success, string ErrorMessage) TryCloseSteam()
    {
        try
        {
            var processes = Process.GetProcessesByName("steam");
            if (processes.Length == 0)
            {
                return (true, string.Empty);
            }

            foreach (var proc in processes)
            {
                try
                {
                    if (proc.MainWindowHandle != IntPtr.Zero)
                    {
                        proc.CloseMainWindow();
                    }
                }
                catch
                {
                    // 忽略单个进程关闭异常，后续统一检查。
                }
            }

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (!IsSteamRunning())
                {
                    return (true, string.Empty);
                }

                System.Threading.Thread.Sleep(200);
            }

            // 若优雅关闭失败，则强制结束 Steam 进程。
            var remain = Process.GetProcessesByName("steam");
            foreach (var proc in remain)
            {
                try
                {
                    proc.Kill();
                }
                catch
                {
                    // 忽略单个进程强制关闭失败。
                }
            }

            var killDeadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < killDeadline)
            {
                if (!IsSteamRunning())
                {
                    return (true, string.Empty);
                }

                System.Threading.Thread.Sleep(200);
            }

            return (false, "无法关闭 Steam，请先手动关闭后再重试。");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static (bool Success, string ErrorMessage) TryStartSteam()
    {
        try
        {
            var steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null)?.ToString();
            if (string.IsNullOrWhiteSpace(steamPath))
            {
                steamPath = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null)?.ToString();
            }

            if (string.IsNullOrWhiteSpace(steamPath) || !Directory.Exists(steamPath))
            {
                return (false, "未找到 Steam 安装目录。");
            }

            var steamExe = Path.Combine(steamPath, "steam.exe");
            if (!File.Exists(steamExe))
            {
                return (false, "未找到 steam.exe。");
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = steamExe,
                UseShellExecute = false
            });

            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static (bool Success, int UpdatedFileCount, int MatchedFileCount, string ErrorMessage) TryWriteLaunchOptionsToSteamUserConfig(string launchOptions)
    {
        try
        {
            var steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null)?.ToString();
            if (string.IsNullOrWhiteSpace(steamPath))
            {
                steamPath = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null)?.ToString();
            }

            if (string.IsNullOrWhiteSpace(steamPath) || !Directory.Exists(steamPath))
            {
                return (false, 0, 0, "未找到 Steam 安装目录。请先启动 Steam，再重试。");
            }

            var userdataDir = Path.Combine(steamPath, "userdata");
            if (!Directory.Exists(userdataDir))
            {
                return (false, 0, 0, "未找到 Steam userdata 目录。请确认本机 Steam 已登录过账号。");
            }

            var configFiles = GetSteamLocalConfigFilesInPriorityOrder(userdataDir);
            if (configFiles.Count == 0)
            {
                return (false, 0, 0, "未找到任何 Steam 账号配置（userdata 下无 localconfig.vdf）。");
            }

            var updatedCount = 0;
            var matchedCount = 0;
            foreach (var configPath in configFiles)
            {
                if (TryUpsertLaunchOptionsInLocalConfig(configPath, "413150", launchOptions, out var changed, out var matched))
                {
                    if (matched)
                        matchedCount++;
                    if (changed)
                        updatedCount++;
                }
            }

            if (matchedCount == 0)
            {
                return (false, 0, 0, "未在 Steam 配置中找到 Stardew Valley（AppId 413150）条目。请先通过 Steam 启动一次游戏后再试。");
            }

            return (true, updatedCount, matchedCount, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, 0, 0, ex.Message);
        }
    }

    private static List<string> GetSteamLocalConfigFilesInPriorityOrder(string userdataDir)
    {
        var result = new List<string>();

        var activeUserText = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", null)?.ToString();
        if (long.TryParse(activeUserText, out _))
        {
            var activeConfig = Path.Combine(userdataDir, activeUserText, "config", "localconfig.vdf");
            if (File.Exists(activeConfig))
            {
                result.Add(activeConfig);
            }
        }

        var others = Directory.GetDirectories(userdataDir)
            .Where(d => long.TryParse(Path.GetFileName(d), out _))
            .Select(d => Path.Combine(d, "config", "localconfig.vdf"))
            .Where(File.Exists)
            .Where(p => !result.Contains(p, StringComparer.OrdinalIgnoreCase));

        result.AddRange(others);
        return result;
    }

    private static bool TryUpsertLaunchOptionsInLocalConfig(
        string configPath,
        string appId,
        string launchOptions,
        out bool changed,
        out bool matched)
    {
        changed = false;
        matched = false;

        var lines = File.ReadAllLines(configPath).ToList();
        if (lines.Count == 0)
            return false;

        var escaped = launchOptions.Replace("\\", "\\\\").Replace("\"", "\\\"");

        for (var i = 0; i < lines.Count; i++)
        {
            if (!string.Equals(lines[i].Trim(), "\"apps\"", StringComparison.Ordinal))
                continue;

            var appsBraceLine = FindNextNonEmptyLine(lines, i + 1);
            if (appsBraceLine < 0 || lines[appsBraceLine].Trim() != "{")
                continue;

            var appsEndLine = FindMatchingBraceLine(lines, appsBraceLine);
            if (appsEndLine < 0)
                continue;

            var appKey = $"\"{appId}\"";
            var appLine = -1;
            for (var j = appsBraceLine + 1; j < appsEndLine; j++)
            {
                if (string.Equals(lines[j].Trim(), appKey, StringComparison.Ordinal))
                {
                    appLine = j;
                    break;
                }
            }

            if (appLine >= 0)
            {
                matched = true;
                var appBodyStart = FindNextNonEmptyLine(lines, appLine + 1);
                if (appBodyStart < 0 || lines[appBodyStart].Trim() != "{")
                    return false;

                var appBodyEnd = FindMatchingBraceLine(lines, appBodyStart);
                if (appBodyEnd < 0)
                    return false;

                var launchLine = -1;
                for (var j = appBodyStart + 1; j < appBodyEnd; j++)
                {
                    if (lines[j].TrimStart().StartsWith("\"LaunchOptions\"", StringComparison.Ordinal))
                    {
                        launchLine = j;
                        break;
                    }
                }

                if (launchLine >= 0)
                {
                    var originalLine = lines[launchLine];
                    var keyPos = originalLine.IndexOf("\"LaunchOptions\"", StringComparison.Ordinal);
                    var valueStart = keyPos >= 0
                        ? originalLine.IndexOf('"', keyPos + "\"LaunchOptions\"".Length)
                        : -1;

                    if (valueStart < 0)
                    {
                        var prefixLen = originalLine.IndexOf('"');
                        var prefix = prefixLen >= 0 ? originalLine.Substring(0, prefixLen) : string.Empty;
                        var fallbackLine = $"{prefix}\"LaunchOptions\"\t\t\"{escaped}\"";
                        if (!string.Equals(originalLine, fallbackLine, StringComparison.Ordinal))
                        {
                            lines[launchLine] = fallbackLine;
                            changed = true;
                        }
                    }
                    else
                    {
                        var valueEnd = FindNextUnescapedQuote(originalLine, valueStart + 1);
                        if (valueEnd > valueStart)
                        {
                            var rewritten = originalLine.Substring(0, valueStart + 1)
                                            + escaped
                                            + originalLine.Substring(valueEnd);

                            if (!string.Equals(originalLine, rewritten, StringComparison.Ordinal))
                            {
                                lines[launchLine] = rewritten;
                                changed = true;
                            }
                        }
                    }
                }
                else
                {
                    var indent = GetLineIndent(lines[appLine]) + "\t";
                    lines.Insert(appBodyEnd, $"{indent}\"LaunchOptions\"\t\t\"{escaped}\"");
                    changed = true;
                }

                break;
            }
            else
            {
                var indent = GetLineIndent(lines[appsBraceLine]) + "\t";
                var appLines = new[]
                {
                    $"{indent}{appKey}",
                    $"{indent}{{",
                    $"{indent}\t\"LaunchOptions\"\t\t\"{escaped}\"",
                    $"{indent}}}"
                };
                lines.InsertRange(appsEndLine, appLines);
                matched = true;
                changed = true;
                break;
            }
        }

        if (changed)
        {
            File.WriteAllLines(configPath, lines, Encoding.UTF8);
        }

        return true;
    }

    private static int FindNextNonEmptyLine(List<string> lines, int startIndex)
    {
        for (var i = startIndex; i < lines.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]))
                return i;
        }

        return -1;
    }

    private static int FindMatchingBraceLine(List<string> lines, int openBraceLine)
    {
        var depth = 0;
        for (var i = openBraceLine; i < lines.Count; i++)
        {
            var line = lines[i];
            for (var c = 0; c < line.Length; c++)
            {
                if (line[c] == '{')
                    depth++;
                else if (line[c] == '}')
                {
                    depth--;
                    if (depth == 0)
                        return i;
                }
            }
        }

        return -1;
    }

    private static int FindNextUnescapedQuote(string text, int startIndex)
    {
        for (var i = startIndex; i < text.Length; i++)
        {
            if (text[i] != '"')
                continue;

            var slashCount = 0;
            for (var j = i - 1; j >= 0 && text[j] == '\\'; j--)
            {
                slashCount++;
            }

            if (slashCount % 2 == 0)
                return i;
        }

        return -1;
    }

    private static string GetLineIndent(string line)
    {
        var idx = 0;
        while (idx < line.Length && char.IsWhiteSpace(line[idx]))
            idx++;

        return idx > 0 ? line.Substring(0, idx) : string.Empty;
    }

    private void SaveInstanceToConfig()
    {
        var allInstances = SettingsService.LoadInstances();
        var existingInstance = allInstances.FirstOrDefault(i => i.Id == _instance.Id);
        if (existingInstance != null)
        {
            existingInstance.WindowTitle = _instance.WindowTitle;
            existingInstance.CustomArguments = _instance.CustomArguments;
            existingInstance.AutoConnectServer = _instance.AutoConnectServer;
            existingInstance.ServerAddress = _instance.ServerAddress;
            existingInstance.SteamInviteCode = _instance.SteamInviteCode;
            existingInstance.OverrideSteamLaunchOptions = _instance.OverrideSteamLaunchOptions;
            existingInstance.SteamLaunchOptions = _instance.SteamLaunchOptions;
        }
        else
        {
            allInstances.Add(_instance);
        }

        SettingsService.SaveInstances(allInstances);
    }

    /// <summary>
    /// 前往全局设置（WIP）
    /// </summary>
    [RelayCommand]
    private void NavigateToGlobalSettings()
    {
        SvlMessageBox.Info(
            "全局设置功能正在开发中...",
            "提示");
    }

    /// <summary>
    /// 显示 Placeholder 帮助
    /// </summary>
    [RelayCommand]
    private void ShowPlaceholderHelp()
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
            SVL.Core.Logging.Log.Error(ex, "[InstanceSettingsViewModel] 显示帮助对话框失败");
        }
    }

    /// <summary>
    /// 切换到 SMAPI 版本
    /// </summary>
    [RelayCommand]
    private void SwitchToSMAPIVersion()
    {
        try
        {
            if (_instance.IsSMAPIInstance)
            {
                SvlMessageBox.Info("当前已经是 SMAPI 版本。", "提示");
                return;
            }

            if (!_instance.HasSMAPIInstalled)
            {
                SvlMessageBox.Error("该路径未安装 SMAPI，无法切换到 SMAPI 版本。", "错误");
                return;
            }

            // 确认切换
            if (!SvlMessageBox.Confirm(
                $"确定要切换到 SMAPI 版本吗？\n\n当前 SMAPI 版本：{_instance.SMAPIVersion}\n\n切换后将启用自动安装功能。",
                "确认切换"))
            {
                return;
            }

            // 切换到 SMAPI 版本
            _instance.IsSMAPIInstance = true;

            // 保存到配置
            SaveInstanceToConfig();

            // 刷新显示
            OnPropertyChanged(nameof(ShowSwitchToSMAPITip));
            OnPropertyChanged(nameof(SwitchToSMAPITipText));

            SvlMessageBox.Success("已切换到 SMAPI 版本，请返回主页面查看。");

            // 触发全局事件通知刷新
            GlobalEvents.OnInstanceChanged(_instance.Id);
        }
        catch (Exception ex)
        {
            SVL.Core.Logging.Log.Error(ex, "[InstanceSettingsViewModel] 切换到 SMAPI 版本失败");
            SvlMessageBox.Error($"切换失败：{ex.Message}");
        }
    }
}
