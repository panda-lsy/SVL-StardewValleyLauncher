using System;
using System.Threading.Tasks;
using SVL.Core.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SVL.Core.Config;
using SVL.Core.Download.NexusMods;
using SVL.Core.Logging;
using NexusModsClient = SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsClient;

namespace SVL.Desktop.ViewModels;

/// <summary>
/// Nexus Mods 登录对话框 ViewModel
/// 使用 OAuth 2.0 PKCE 认证
/// </summary>
public partial class NexusLoginDialogViewModel : ObservableObject
{
    private static string _sharedLastAuthUrl = string.Empty;
    private string _lastAuthUrl = string.Empty;

    [ObservableProperty]
    private bool _isLoggedIn;

    [ObservableProperty]
    private string _userName = string.Empty;

    [ObservableProperty]
    private string _membershipType = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "未登录";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _loginButtonText = "🔑 登录 Nexus Mods";

    public bool ShowReopenBrowserButton => !string.IsNullOrWhiteSpace(_lastAuthUrl);

    /// <summary>
    /// 请求关闭事件
    /// </summary>
    public event EventHandler? RequestClose;

    public NexusLoginDialogViewModel()
    {
        _lastAuthUrl = _sharedLastAuthUrl;

        // 不要在构造函数中调用异步方法，避免死锁
        // 登录状态将在对话框显示后异步加载
        StatusMessage = "加载中...";
    }

    [RelayCommand]
    private void ReopenBrowserPage()
    {
        if (string.IsNullOrWhiteSpace(_lastAuthUrl))
        {
            StatusMessage = "暂无可重新打开的授权页面，请先点击登录。";
            return;
        }

        ProcessEx.OpenUrl(_lastAuthUrl);
        StatusMessage = "已重新打开浏览器授权页，请在浏览器完成授权后返回。";
        Log.Info($"[NexusLogin] 通过独立按钮重新打开浏览器授权页: {_lastAuthUrl}");
    }

    /// <summary>
    /// 对话框加载完成后调用
    /// </summary>
    public async Task InitializeAsync()
    {
        await LoadLoginStateAsync();
    }

    /// <summary>
    /// 加载登录状态
    /// </summary>
    private async Task LoadLoginStateAsync()
    {
        try
        {
            var settings = AppConfig.GetSettings();
            var accessToken = settings.NexusModsOAuthToken;

            if (!string.IsNullOrEmpty(accessToken))
            {
                // 从配置加载用户信息
                UserName = settings.NexusModsOAuthUserName ?? string.Empty;
                MembershipType = settings.NexusModsOAuthMembershipType ?? "Unknown";

                // 验证 Access Token 是否有效
                var isValid = await NexusModsClient.ValidateAccessTokenAsync(accessToken);
                if (isValid)
                {
                    IsLoggedIn = true;
                    StatusMessage = $"已登录: {UserName}";
                    Log.Info($"[NexusLogin] ✓ Access Token 有效，用户: {UserName}");

                    // 检查头像缓存，如果不存在则下载
                    if (!string.IsNullOrEmpty(settings.NexusModsOAuthAvatarUrl) &&
                        !string.IsNullOrEmpty(settings.NexusModsOAuthUserName))
                    {
                        var localAvatar = settings.NexusModsOAuthAvatarLocalPath;
                        if (string.IsNullOrEmpty(localAvatar) || !System.IO.File.Exists(localAvatar))
                        {
                            // 异步下载头像（不阻塞UI）
                            _ = System.Threading.Tasks.Task.Run(async () =>
                            {
                                try
                                {
                                    var cachedPath = await SVL.Core.IO.AvatarCacheService.DownloadAndCacheAvatarAsync(
                                        settings.NexusModsOAuthAvatarUrl,
                                        settings.NexusModsOAuthUserName);

                                    if (!string.IsNullOrEmpty(cachedPath))
                                    {
                                        var updatedSettings = AppConfig.GetSettings();
                                        updatedSettings.NexusModsOAuthAvatarLocalPath = cachedPath;
                                        AppConfig.SaveSettings(updatedSettings);
                                        Log.Info("[NexusLogin] ✓ 头像已更新缓存");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log.Warn("[NexusLogin] 后台下载头像失败", ex);
                                }
                            });
                        }
                    }
                }
                else
                {
                    // Token 无效，尝试刷新
                    var refreshToken = settings.NexusModsOAuthRefreshToken;
                    if (!string.IsNullOrEmpty(refreshToken))
                    {
                        try
                        {
                            StatusMessage = "正在刷新 Token...";
                            // 创建新的 OAuthManager 实例来刷新 token
                            using var oauthManager = new NexusOAuthManager();
                            var newToken = await oauthManager.RefreshAccessTokenAsync(refreshToken);

                            // 保存新的 token
                            settings.NexusModsOAuthToken = newToken.AccessToken;
                            settings.NexusModsOAuthRefreshToken = newToken.RefreshToken;
                            AppConfig.SaveSettings(settings);
                            AppConfig.ClearCache();

                            IsLoggedIn = true;
                            StatusMessage = $"已登录: {UserName}";
                            Log.Info($"[NexusLogin] ✓ Token 刷新成功，用户: {UserName}");
                        }
                        catch
                        {
                            Log.Warn("[NexusLogin] Token 刷新失败，需要重新登录");
                            IsLoggedIn = false;
                            StatusMessage = "未登录";
                        }
                    }
                    else
                    {
                        IsLoggedIn = false;
                        StatusMessage = "未登录";
                    }
                }
            }
            else
            {
                IsLoggedIn = false;
                StatusMessage = "未登录";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NexusLogin] 加载登录状态失败");
            IsLoggedIn = false;
            StatusMessage = "未登录";
        }
    }

    /// <summary>
    /// 关闭命令
    /// </summary>
    [RelayCommand]
    private void Close()
    {
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 登录命令（启动 OAuth 2.0 PKCE 认证流程）
    /// </summary>
    [RelayCommand]
    private async Task LoginAsync()
    {
        NexusOAuthManager oauthManager = null;
        try
        {
            if (IsBusy)
            {
                Log.Info("[NexusLogin] 已在登录流程中，忽略重复点击");
                return;
            }

            IsBusy = true;
            StatusMessage = "正在连接到 Nexus...";

            Log.Info("[NexusLogin] 开始 OAuth 登录");

            // 每次登录创建新的 OAuthManager 实例
            oauthManager = new NexusOAuthManager();

            // 使用 OAuth 2.0 PKCE 认证
            var tokenResponse = await oauthManager.AuthenticateAsync(url =>
            {
                _lastAuthUrl = url;
                _sharedLastAuthUrl = url;
                OnPropertyChanged(nameof(ShowReopenBrowserButton));
                // 打开浏览器
                ProcessEx.OpenUrl(url);
                Log.Info($"[NexusLogin] 已打开浏览器: {url}");

                _ = Task.Run(async () =>
                {
                    await Task.Delay(5000);
                    var dispatcher = System.Windows.Application.Current?.Dispatcher;
                    if (dispatcher == null)
                        return;

                    dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (IsBusy && !string.IsNullOrWhiteSpace(_lastAuthUrl))
                        {
                            StatusMessage = "正在等待浏览器授权，如页面被关闭可点击“重新打开浏览器页面”。";
                        }
                    }));
                });
            });

            if (tokenResponse != null)
            {
                Log.Info("[NexusLogin] ✓ 获取到 OAuth Token");

                // 从 JWT id_token 解析用户信息
                StatusMessage = "正在解析用户信息...";
                var userInfo = oauthManager.GetUserInfo(tokenResponse.IdToken);

                // 保存 OAuth Token 和用户信息
                var settings = AppConfig.GetSettings();
                settings.NexusModsOAuthToken = tokenResponse.AccessToken;
                settings.NexusModsOAuthRefreshToken = tokenResponse.RefreshToken;
                settings.NexusModsOAuthIdToken = tokenResponse.IdToken;
                settings.NexusModsOAuthUserName = userInfo.Name;
                settings.NexusModsOAuthMembershipType = userInfo.IsPremium ? "Premium" : "Free";
                settings.NexusModsOAuthAvatarUrl = userInfo.Avatar;
                AppConfig.SaveSettings(settings);
                AppConfig.ClearCache();

                Log.Info("[NexusLogin] ✓ OAuth Token 和用户信息已保存");

                // 下载并缓存头像
                StatusMessage = "正在下载头像...";
                var cachedAvatarPath = await SVL.Core.IO.AvatarCacheService.DownloadAndCacheAvatarAsync(userInfo.Avatar, userInfo.Name);
                if (!string.IsNullOrEmpty(cachedAvatarPath))
                {
                    settings = AppConfig.GetSettings();
                    settings.NexusModsOAuthAvatarLocalPath = cachedAvatarPath;
                    AppConfig.SaveSettings(settings);
                    Log.Info("[NexusLogin] ✓ 头像已缓存");
                }

                // 更新登录状态
                UserName = userInfo.Name;
                MembershipType = userInfo.IsPremium ? "Premium" : "Free";
                IsLoggedIn = true;
                StatusMessage = $"已登录: {UserName}";
                _lastAuthUrl = string.Empty;
                _sharedLastAuthUrl = string.Empty;
                OnPropertyChanged(nameof(ShowReopenBrowserButton));

                Log.Info("[NexusLogin] ✓ 登录成功");

                // 关闭对话框
                RequestClose?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Log.Warn("[NexusLogin] 未获取到 OAuth Token");
                StatusMessage = "登录失败，未获取到 Token";
            }
        }
        catch (TimeoutException)
        {
            Log.Error("[NexusLogin] OAuth 登录超时");
            IsLoggedIn = false;
            StatusMessage = "登录超时，请重试";
            if (!string.IsNullOrWhiteSpace(_lastAuthUrl))
            {
                OnPropertyChanged(nameof(ShowReopenBrowserButton));
            }
        }
        catch (ObjectDisposedException ex)
        {
            Log.Error(ex, "[NexusLogin] OAuth 监听器已释放");
            IsLoggedIn = false;
            StatusMessage = "登录失败：本地回调监听已关闭。请点击“重新打开浏览器页面”继续授权。";
            OnPropertyChanged(nameof(ShowReopenBrowserButton));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NexusLogin] OAuth 登录失败");
            IsLoggedIn = false;
            StatusMessage = $"登录失败: {ex.Message}";
            if (!string.IsNullOrWhiteSpace(_lastAuthUrl))
            {
                OnPropertyChanged(nameof(ShowReopenBrowserButton));
            }
        }
        finally
        {
            // 释放 OAuthManager
            oauthManager?.Dispose();

            IsBusy = false;
        }
    }

    /// <summary>
    /// 登出命令
    /// </summary>
    [RelayCommand]
    private void Logout()
    {
        try
        {
            Log.Info("[NexusLogin] 用户登出");

            // 清除配置中的 OAuth Token 和用户信息
            var settings = AppConfig.GetSettings();
            settings.NexusModsOAuthToken = null;
            settings.NexusModsOAuthRefreshToken = null;
            settings.NexusModsApiKey = null;
            settings.NexusModsOAuthUserName = null;
            settings.NexusModsOAuthMembershipType = null;
            settings.NexusModsOAuthAvatarUrl = null;
            AppConfig.SaveSettings(settings);
            AppConfig.ClearCache();

            // 更新状态
            IsLoggedIn = false;
            UserName = string.Empty;
            MembershipType = string.Empty;
            StatusMessage = "已登出";

            Log.Info("[NexusLogin] ✓ 已登出");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NexusLogin] 登出失败");
        }
    }
}
