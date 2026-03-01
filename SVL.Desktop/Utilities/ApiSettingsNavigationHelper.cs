using System;
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
                    _ = dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (mainViewModel.LeftPanelContent is SettingsViewModel settingsViewModel)
                        {
                            settingsViewModel.RefreshNexusLoginStatus();
                            settingsViewModel.SwitchToApiTab();
                        }
                    }), System.Windows.Threading.DispatcherPriority.Loaded);
                }
            }));
        }
        catch (Exception ex)
        {
            Log.Warn($"[{caller}] 跳转到设置页面失败", ex);
        }
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
