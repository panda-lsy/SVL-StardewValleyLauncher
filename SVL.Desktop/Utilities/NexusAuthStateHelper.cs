using System;
using SVL.Core.Config;
using SVL.Core.Logging;
using SVL.Desktop.Controls;
using SVL.Desktop.ViewModels;

namespace SVL.Desktop.Utilities;

internal static class NexusAuthStateHelper
{
    public static bool IsUnauthorized(Exception ex)
    {
        if (ex is SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsTokenExpiredException)
            return true;

        if (ex is System.Net.Http.HttpRequestException httpEx)
        {
            var httpMessage = httpEx.Message ?? string.Empty;
            if (httpMessage.IndexOf("401", StringComparison.OrdinalIgnoreCase) >= 0 ||
                httpMessage.IndexOf("unauthorized", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        var message = ex?.Message ?? string.Empty;
        return message.IndexOf("401", StringComparison.OrdinalIgnoreCase) >= 0 ||
               message.IndexOf("unauthorized", StringComparison.OrdinalIgnoreCase) >= 0 ||
               message.IndexOf("token 已过期", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static void HandleTokenExpired(string scene, string caller, bool showNotification = true, bool navigateToSettings = true)
    {
        try
        {
            var settings = AppConfig.GetSettings();
            if (!string.IsNullOrEmpty(settings.NexusModsOAuthToken))
            {
                settings.NexusModsOAuthToken = null;
                settings.NexusModsOAuthRefreshToken = null;
                AppConfig.SaveSettings(settings);
                Log.Info($"[{caller}] [{scene}] 已清除过期 NexusMods Token");
            }

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;

            _ = dispatcher.BeginInvoke(new Action(() =>
            {
                if (System.Windows.Application.Current?.MainWindow is MainWindow mainWindow &&
                    mainWindow.DataContext is MainWindowViewModel mainViewModel &&
                    mainViewModel.LeftPanelContent is SettingsViewModel settingsViewModel)
                {
                    settingsViewModel.RefreshNexusLoginStatus();
                }

                if (showNotification)
                {
                    FloatingNotificationControl.Show(
                        title: "NexusMods 登录已过期",
                        message: "请在设置页面重新登录 NexusMods 账户。",
                        autoCloseDelay: 6000,
                        notificationType: NotificationType.Warning
                    );
                }

                if (navigateToSettings)
                {
                    ApiSettingsNavigationHelper.NavigateToApiTab(caller);
                }
            }));
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[{caller}] [{scene}] 处理 NexusMods 登录过期失败");
        }
    }
}
