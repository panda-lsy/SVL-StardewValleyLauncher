using System;
using System.Threading.Tasks;
using SVL.Core.Logging;
using SVL.Desktop.Controls;
using SVL.Desktop.ViewModels;

namespace SVL.Desktop.Utilities;

internal static class ApiSettingsNavigationHelper
{
    public static void NavigateToApiTab(string caller)
    {
        try
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;

            _ = dispatcher.BeginInvoke(new Action(() =>
            {
                if (System.Windows.Application.Current?.MainWindow is MainWindow mainWindow &&
                    mainWindow.DataContext is MainWindowViewModel mainViewModel)
                {
                    mainViewModel.CurrentPage = PageType.Settings;
                    EnsureSettingsViewModelReadyAndRefresh(mainViewModel, dispatcher, caller);
                }
            }));
        }
        catch (Exception ex)
        {
            Log.Warn($"[{caller}] 跳转到设置页面失败", ex);
        }
    }

    private static async void EnsureSettingsViewModelReadyAndRefresh(MainWindowViewModel mainViewModel, System.Windows.Threading.Dispatcher dispatcher, string caller)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var refreshed = false;

            await dispatcher.InvokeAsync(() =>
            {
                if (mainViewModel.LeftPanelContent is SettingsViewModel settingsViewModel)
                {
                    settingsViewModel.RefreshNexusLoginStatus();
                    settingsViewModel.SwitchToApiTab();
                    refreshed = true;
                }
            }, System.Windows.Threading.DispatcherPriority.Loaded);

            if (refreshed)
                return;

            await Task.Delay(100);
        }

        Log.Warn($"[{caller}] 跳转到设置页面后未能及时获取 SettingsViewModel，登录状态刷新可能延迟");
    }

    public static void ShowApiConfigWarningAndNavigate(string caller, string message, string title = "API 配置提示")
    {
        try
        {
            FloatingNotificationControl.Show(
                title: title,
                message: message,
                autoCloseDelay: 8000,
                notificationType: NotificationType.Warning
            );
        }
        catch (Exception ex)
        {
            Log.Warn($"[{caller}] 显示 API 配置提示失败", ex);
        }

        NavigateToApiTab(caller);
    }
}
