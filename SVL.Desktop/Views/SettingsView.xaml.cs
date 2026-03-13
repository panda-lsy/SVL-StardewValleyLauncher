using System;
using System.Diagnostics;
using SVL.Core.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SVL.Core.App;
using SVL.Core.Logging;
using SVL.Desktop.Controls;

namespace SVL.Desktop.Views;

/// <summary>
/// SettingsView.xaml 的交互逻辑
/// </summary>
public partial class SettingsView : UserControl
{
    private Button? _currentTab;

    public SettingsView()
    {
        InitializeComponent();
        SetActiveTab(TabBasic);
    }

    /// <summary>
    /// SettingsView 加载时初始化 PasswordBox
    /// </summary>
    private void OnSettingsViewLoaded(object sender, RoutedEventArgs e)
    {
        // 延迟执行，确保数据绑定已完成
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (DataContext is ViewModels.SettingsViewModel viewModel)
            {
                // 刷新 NexusMods OAuth 登录状态
                viewModel.RefreshNexusLoginStatus();

                // 订阅 ActiveTabIndex 变化事件
                viewModel.PropertyChanged += (s, args) =>
                {
                    if (args.PropertyName == nameof(ViewModels.SettingsViewModel.ActiveTabIndex))
                    {
                        SwitchToTabByIndex(viewModel.ActiveTabIndex);
                    }
                };

                // 检查是否需要立即切换到 API 选项卡
                if (viewModel.ActiveTabIndex != 0)
                {
                    SwitchToTabByIndex(viewModel.ActiveTabIndex);
                }
            }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 根据索引切换选项卡
    /// </summary>
    private void SwitchToTabByIndex(int index)
    {
        Button targetTab = index switch
        {
            1 => TabApi,
            2 => TabPersonalization,
            3 => TabOther,
            4 => TabAbout,
            _ => TabBasic
        };

        SetActiveTab(targetTab);
    }

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            SetActiveTab(button);
        }
    }

    private void SetActiveTab(Button activeTab)
    {
        // 重置所有标签页样式
        ResetTabStyle(TabBasic);
        ResetTabStyle(TabApi);
        ResetTabStyle(TabPersonalization);
        ResetTabStyle(TabOther);
        ResetTabStyle(TabAbout);

        // 设置活动标签页样式（使用 DynamicResource 保持主题响应）
        // 使用半透明背景样式，通过 SetResourceReference 保持动态绑定
        activeTab.SetResourceReference(Button.BackgroundProperty, "ColorBrush2");
        activeTab.Opacity = 0.7; // 通过 Opacity 实现半透明效果
        activeTab.Foreground = new SolidColorBrush(Colors.White);
        _currentTab = activeTab;

        // 隐藏所有面板
        PanelBasic.Visibility = Visibility.Collapsed;
        PanelApi.Visibility = Visibility.Collapsed;
        PanelPersonalization.Visibility = Visibility.Collapsed;
        PanelOther.Visibility = Visibility.Collapsed;
        PanelAbout.Visibility = Visibility.Collapsed;

        // 显示选中的面板
        switch (activeTab.Tag.ToString())
        {
            case "0":
                PanelBasic.Visibility = Visibility.Visible;
                break;
            case "1":
                PanelApi.Visibility = Visibility.Visible;
                break;
            case "2":
                PanelPersonalization.Visibility = Visibility.Visible;
                break;
            case "3":
                PanelOther.Visibility = Visibility.Visible;
                break;
            case "4":
                PanelAbout.Visibility = Visibility.Visible;
                // 切换到关于页面时自动检查更新
                if (DataContext is ViewModels.SettingsViewModel viewModel)
                {
                    viewModel.CheckUpdateCommand.Execute(null);
                }
                break;
        }
    }

    private void ResetTabStyle(Button tab)
    {
        tab.Background = Brushes.Transparent;
        tab.SetResourceReference(Button.ForegroundProperty, "ColorBrush1");
        tab.Opacity = 1.0; // 重置透明度
    }

    /// <summary>
    /// TextBox 获得焦点时清除占位符
    /// </summary>
    private void OnTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox && textBox.Tag is string placeholder && textBox.Text == placeholder)
        {
            textBox.Text = string.Empty;
        }
    }

    /// <summary>
    /// 打开 NexusMods SSO 登录对话框
    /// </summary>
    private void OnNexusLoginClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Log.Info("[SettingsView] 点击登录 Nexus Mods 按钮");

            // 获取主窗口
            var mainWindow = Application.Current.MainWindow;
            if (mainWindow == null)
            {
                Log.Error("[SettingsView] 无法找到主窗口");
                SvlMessageBox.Error("无法找到主窗口");
                return;
            }

            Log.Info("[SettingsView] 创建 NexusLoginDialog");

            // 创建并显示登录对话框
            var loginDialog = new NexusLoginDialog();
            loginDialog.Show(mainWindow);

            Log.Info("[SettingsView] NexusLoginDialog 已关闭");

            // 登录成功后刷新状态
            if (loginDialog.DataContext is ViewModels.NexusLoginDialogViewModel loginViewModel)
            {
                // 更新设置页面的登录状态显示
                if (DataContext is ViewModels.SettingsViewModel settingsViewModel)
                {
                    Log.Info("[SettingsView] 刷新登录状态");
                    settingsViewModel.RefreshNexusLoginStatus();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SettingsView] 打开登录对话框失败");
            SvlMessageBox.Error($"打开登录对话框失败：{ex.Message}", "错误", $"详细信息：{ex}");
        }
    }

    /// <summary>
    /// NexusMods 登出
    /// </summary>
    private void OnNexusLogoutClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is ViewModels.SettingsViewModel vm && vm.IsNexusLoginExpired)
            {
                OnNexusLoginClick(sender, e);
                return;
            }

            if (SvlMessageBox.Confirm(
                "确定要登出 NexusMods 账户吗？",
                "确认登出"))
            {
                // 清除登录状态
                if (DataContext is ViewModels.SettingsViewModel settingsViewModel)
                {
                    settingsViewModel.NexusLogout();
                }
            }
        }
        catch (Exception ex)
        {
            SvlMessageBox.Error($"登出失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 手动注册 NXM 协议
    /// </summary>
    private void OnRegisterNxmProtocolClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Log.Info("[SettingsView] 用户手动注册 NXM 协议");

            // 获取可执行文件路径用于调试
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            Log.Info($"[SettingsView] 当前进程路径: {exePath}");

            // 尝试注册
            var success = NxmProtocolService.ManualRegister();

            if (success)
            {
                var info = NxmProtocolService.GetRegistrationInfo();
                SvlMessageBox.Success($"NXM 协议注册成功！\n\n{info}");

                // 刷新状态显示
                if (DataContext is ViewModels.SettingsViewModel settingsViewModel)
                {
                    settingsViewModel.CheckNxmProtocolStatus();
                }
            }
            else
            {
                var info = NxmProtocolService.GetRegistrationInfo();
                SvlMessageBox.Error($"NXM 协议注册失败。\n\n{info}\n\n请查看日志了解详细信息。");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SettingsView] 注册 NXM 协议失败");
            SvlMessageBox.Error($"注册失败：{ex.Message}");
        }
    }
}
