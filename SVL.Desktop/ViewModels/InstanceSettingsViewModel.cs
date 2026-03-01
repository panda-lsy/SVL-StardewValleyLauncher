using System;
using System.Linq;
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
    /// 版本隔离
    /// </summary>
    [ObservableProperty]
    private bool _enableIsolation;

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
    /// 从实例加载设置
    /// </summary>
    private void LoadSettings()
    {
        // 从实例配置加载设置
        EnableIsolation = _instance.EnableIsolation;
        WindowTitle = _instance.WindowTitle;
        CustomArguments = _instance.CustomArguments;
        AutoConnectServer = _instance.AutoConnectServer;
        ServerAddress = _instance.ServerAddress;
        SteamInviteCode = _instance.SteamInviteCode;

        // 如果窗口标题为空，使用默认模板
        if (string.IsNullOrEmpty(WindowTitle))
        {
            WindowTitle = SVL.Core.Stardew.Launch.WindowTitlePlaceholderService.GetDefaultTitleTemplate(_instance.IsSMAPIInstance);
        }
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
            _instance.EnableIsolation = EnableIsolation;
            _instance.WindowTitle = WindowTitle;
            _instance.CustomArguments = CustomArguments;
            _instance.AutoConnectServer = AutoConnectServer;
            _instance.ServerAddress = ServerAddress;
            _instance.SteamInviteCode = SteamInviteCode;

            // 如果开启了版本隔离，初始化隔离目录
            if (EnableIsolation)
            {
                var instanceFolderName = InstanceIsolationService.GenerateVersionFolderName(
                    _instance.Name,
                    _instance.IsSMAPIInstance);

                System.Diagnostics.Debug.WriteLine($"[InstanceSettings] Initializing isolation directories for: {instanceFolderName}");

                // 验证实例名称
                if (!InstanceIsolationService.IsValidVersionName(instanceFolderName))
                {
                    SvlMessageBox.Error(
                        $"实例名称无效：{instanceFolderName}\n\n实例名称包含非法字符或使用了保留名称。\n\n请修改实例名称后再开启版本隔离。");
                    return;
                }

                var success = InstanceIsolationService.InitializeIsolationDirectories(
                    _instance.GamePath,
                    instanceFolderName,
                    _instance.IsSMAPIInstance);

                if (!success)
                {
                    if (!SvlMessageBox.Confirm(
                        "初始化版本隔离目录失败，是否仍要保存设置？\n\n游戏可能无法以隔离模式启动。",
                        "警告"))
                    {
                        return; // 用户取消保存
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[InstanceSettings] ✓ Isolation directories initialized successfully");
                }
            }

            // 重新加载所有实例，更新当前实例，然后保存
            var allInstances = SettingsService.LoadInstances();
            System.Diagnostics.Debug.WriteLine($"[InstanceSettings] Loaded {allInstances.Count} instances from config");

            // 查找并更新对应的实例
            var existingInstance = allInstances.FirstOrDefault(i => i.Id == _instance.Id);
            if (existingInstance != null)
            {
                System.Diagnostics.Debug.WriteLine($"[InstanceSettings] Found existing instance: {existingInstance.Name}");

                // 更新已存在的实例
                existingInstance.EnableIsolation = _instance.EnableIsolation;
                existingInstance.WindowTitle = _instance.WindowTitle;
                existingInstance.CustomArguments = _instance.CustomArguments;
                existingInstance.AutoConnectServer = _instance.AutoConnectServer;
                existingInstance.ServerAddress = _instance.ServerAddress;
                existingInstance.SteamInviteCode = _instance.SteamInviteCode;

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
            EnableIsolation = true;
            WindowTitle = SVL.Core.Stardew.Launch.WindowTitlePlaceholderService.GetDefaultTitleTemplate(_instance.IsSMAPIInstance);
            CustomArguments = string.Empty;
            AutoConnectServer = false;
            ServerAddress = string.Empty;
            SteamInviteCode = string.Empty;

            System.Diagnostics.Debug.WriteLine($"[InstanceSettings] Reset to default: WindowTitle={WindowTitle}");
        }
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
}
