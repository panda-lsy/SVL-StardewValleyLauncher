using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using SVL.Avalonia.Models;
using SVL.Avalonia.Services;
using System.Diagnostics;

namespace SVL.Avalonia.ViewModels;

public partial class NexusLoginDialogViewModel : ObservableObject
{
    private static string _sharedLastOAuthAuthorizeUrl = string.Empty;
    private readonly NexusAuthService _nexusAuthService;
    private readonly NexusOAuthService _nexusOAuthService;
    private readonly string _existingOAuthAccessToken;
    private readonly string _existingOAuthRefreshToken;
    private readonly string _existingUserName;
    private readonly string _existingMembershipType;
    private readonly int _existingUserId;
    private NexusOAuthStartResult? _oauthStart;
    private string _lastOAuthAuthorizeUrl = string.Empty;

    [ObservableProperty]
    private string _apiKey = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "请输入 Nexus API Key 后点击验证";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isLoggedIn;

    public bool IsNotLoggedIn => !IsLoggedIn;

    [ObservableProperty]
    private string _userName = string.Empty;

    [ObservableProperty]
    private string _membershipType = string.Empty;

    [ObservableProperty]
    private string _loginButtonText = "登录 Nexus Mods";

    [ObservableProperty]
    private string _oauthCallbackInput = string.Empty;

    [ObservableProperty]
    private string _oauthStatusMessage = "点击“开始 OAuth 授权”后，在浏览器完成登录并粘贴回调 URL 或 code";

    public bool CanRetryOpenAuthorizePage => !string.IsNullOrWhiteSpace(_lastOAuthAuthorizeUrl);

    public bool CanCopyAuthorizeUrl => !string.IsNullOrWhiteSpace(_lastOAuthAuthorizeUrl);

    public bool ShowReopenBrowserButton => CanRetryOpenAuthorizePage;

    public string RollbackAuthorizeUrlPreview => BuildAuthorizeUrlPreview(_lastOAuthAuthorizeUrl);

    public event EventHandler<NexusLoginResult?>? RequestClose;

    public NexusLoginDialogViewModel(
        NexusAuthService nexusAuthService,
        NexusOAuthService nexusOAuthService,
        string existingApiKey,
        string existingOAuthAccessToken,
        string existingOAuthRefreshToken,
        string existingUserName,
        string existingMembershipType,
        int existingUserId)
    {
        _nexusAuthService = nexusAuthService;
        _nexusOAuthService = nexusOAuthService;
        ApiKey = existingApiKey;
        _existingOAuthAccessToken = existingOAuthAccessToken ?? string.Empty;
        _existingOAuthRefreshToken = existingOAuthRefreshToken ?? string.Empty;
        _existingUserName = existingUserName ?? string.Empty;
        _existingMembershipType = existingMembershipType ?? string.Empty;
        _existingUserId = existingUserId;
        _lastOAuthAuthorizeUrl = _sharedLastOAuthAuthorizeUrl;
    }

    public async Task InitializeAsync()
    {
        await LoadLoginStateAsync();
    }

    private async Task LoadLoginStateAsync()
    {
        try
        {
            StatusMessage = "加载中...";

            if (string.IsNullOrWhiteSpace(_existingOAuthAccessToken))
            {
                IsLoggedIn = false;
                StatusMessage = string.IsNullOrWhiteSpace(ApiKey)
                    ? "未登录"
                    : "已填写 API Key，可点击验证登录";
                return;
            }

            UserName = _existingUserName;
            MembershipType = _existingMembershipType;

            var validate = await _nexusOAuthService.ValidateAccessTokenAsync(_existingOAuthAccessToken);
            if (validate.IsSuccess)
            {
                IsLoggedIn = true;
                UserName = string.IsNullOrWhiteSpace(validate.UserName) ? UserName : validate.UserName;
                MembershipType = string.IsNullOrWhiteSpace(validate.MembershipType) ? MembershipType : validate.MembershipType;
                StatusMessage = $"已登录: {UserName}";
                return;
            }

            if (!string.IsNullOrWhiteSpace(_existingOAuthRefreshToken))
            {
                StatusMessage = "正在刷新 Token...";
                var refresh = await _nexusOAuthService.RefreshAccessTokenAsync(_existingOAuthRefreshToken);
                if (refresh.IsSuccess && refresh.Token != null)
                {
                    var profile = refresh.Profile;
                    IsLoggedIn = true;
                    UserName = string.IsNullOrWhiteSpace(profile.UserName) ? UserName : profile.UserName;
                    MembershipType = string.IsNullOrWhiteSpace(profile.MembershipType) ? MembershipType : profile.MembershipType;
                    StatusMessage = $"已登录: {UserName}";

                    RequestClose?.Invoke(this, new NexusLoginResult
                    {
                        ApiKey = ApiKey.Trim(),
                        IsOAuthLogin = true,
                        OAuthAccessToken = refresh.Token.AccessToken,
                        OAuthRefreshToken = refresh.Token.RefreshToken,
                        OAuthIdToken = refresh.Token.IdToken,
                        UserName = UserName,
                        MembershipType = MembershipType,
                        UserId = profile.UserId > 0 ? profile.UserId : _existingUserId
                    });
                    return;
                }
            }

            IsLoggedIn = false;
            StatusMessage = "未登录";
        }
        catch
        {
            IsLoggedIn = false;
            StatusMessage = "未登录";
        }
    }

    [RelayCommand]
    private void OpenApiKeyPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://next.nexusmods.com/settings/api-keys",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开浏览器失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ValidateAndLoginAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "正在验证 Nexus API Key...";

        var result = await _nexusAuthService.ValidateApiKeyAsync(ApiKey);
        if (!result.IsSuccess)
        {
            StatusMessage = result.Message;
            IsBusy = false;
            return;
        }

        StatusMessage = $"验证成功：{result.UserName} ({result.MembershipType})";
        UserName = result.UserName;
        MembershipType = result.MembershipType;
        IsLoggedIn = true;
        IsBusy = false;

        RequestClose?.Invoke(this, new NexusLoginResult
        {
            ApiKey = ApiKey.Trim(),
            IsOAuthLogin = false,
            UserName = result.UserName,
            MembershipType = result.MembershipType,
            UserId = result.UserId
        });
    }

    [RelayCommand]
    private void StartOAuthAuthorize()
    {
        _oauthStart = _nexusOAuthService.CreateAuthorizationUrl();
        _lastOAuthAuthorizeUrl = _oauthStart.AuthorizeUrl;
        _sharedLastOAuthAuthorizeUrl = _lastOAuthAuthorizeUrl;
        NotifyAuthorizeUrlChanged();
        OauthStatusMessage = "已打开授权页面，请完成授权后粘贴回调 URL 或 code";

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _oauthStart.AuthorizeUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            OauthStatusMessage = $"打开授权页面失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task StartOAuthAutoLoginAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        OauthStatusMessage = "正在启动本地回调监听并打开授权页面...";

        NexusOAuthTokenResult tokenResult;
        try
        {
            tokenResult = await _nexusOAuthService.AuthorizeWithLoopbackAsync(url =>
            {
                _lastOAuthAuthorizeUrl = url;
                _sharedLastOAuthAuthorizeUrl = url;
                global::Avalonia.Threading.Dispatcher.UIThread.Post(NotifyAuthorizeUrlChanged);
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });

                _ = Task.Run(async () =>
                {
                    await Task.Delay(5000);
                    if (IsBusy && !string.IsNullOrWhiteSpace(_lastOAuthAuthorizeUrl))
                    {
                        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            OauthStatusMessage = "正在等待浏览器授权，如页面被关闭可点击\u201c重开授权页\u201d。";
                        });
                    }
                });
            });
        }
        catch (Exception ex)
        {
            IsBusy = false;
            OauthStatusMessage = $"OAuth 授权过程出错: {ex.Message}";
            return;
        }

        IsBusy = false;
        if (!tokenResult.IsSuccess || tokenResult.Token == null)
        {
            _lastOAuthAuthorizeUrl = tokenResult.AuthorizeUrl;
            NotifyAuthorizeUrlChanged();

            OauthStatusMessage = tokenResult.FailureReason switch
            {
                NexusOAuthFailureReason.Timeout => "授权等待超时。可点击“重开授权页”继续，或使用手动 code 回填。",
                NexusOAuthFailureReason.ListenerStartFailed => "本地监听端口不可用。请关闭占用进程后重试，或切换手动 code 回填。",
                NexusOAuthFailureReason.UserCancelled => "你在浏览器中取消了授权，可重试授权。",
                NexusOAuthFailureReason.BrowserOpenFailed => "无法自动打开浏览器，请点击“重开授权页”手动打开。",
                _ => tokenResult.Message
            };
            return;
        }

        var profile = tokenResult.Profile;
        OauthStatusMessage = $"OAuth 自动登录成功：{profile.UserName}";
        UserName = profile.UserName;
        MembershipType = profile.MembershipType;
        IsLoggedIn = true;
        _lastOAuthAuthorizeUrl = string.Empty;
        _sharedLastOAuthAuthorizeUrl = string.Empty;
        NotifyAuthorizeUrlChanged();

        RequestClose?.Invoke(this, new NexusLoginResult
        {
            IsOAuthLogin = true,
            OAuthAccessToken = tokenResult.Token.AccessToken,
            OAuthRefreshToken = tokenResult.Token.RefreshToken,
            OAuthIdToken = tokenResult.Token.IdToken,
            UserName = profile.UserName,
            MembershipType = profile.MembershipType,
            UserId = profile.UserId
        });
    }

    [RelayCommand]
    private void RetryOpenAuthorizePage()
    {
        if (string.IsNullOrWhiteSpace(_lastOAuthAuthorizeUrl))
        {
            OauthStatusMessage = "当前没有可重开的授权页面，请重新开始 OAuth。";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _lastOAuthAuthorizeUrl,
                UseShellExecute = true
            });
            OauthStatusMessage = "已重开授权页面，请在浏览器完成授权后返回。";
        }
        catch (Exception ex)
        {
            OauthStatusMessage = $"重开授权页面失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CopyAuthorizeUrlAsync()
    {
        if (string.IsNullOrWhiteSpace(_lastOAuthAuthorizeUrl))
        {
            OauthStatusMessage = "当前没有可复制的授权地址";
            return;
        }

        var clipboard = GetClipboard();
        if (clipboard == null)
        {
            OauthStatusMessage = "当前环境不支持剪贴板";
            return;
        }

        await clipboard.SetTextAsync(_lastOAuthAuthorizeUrl);
        OauthStatusMessage = "授权地址已复制，可粘贴到浏览器打开";
    }

    [RelayCommand]
    private async Task CompleteOAuthLoginAsync()
    {
        if (_oauthStart == null)
        {
            OauthStatusMessage = "请先点击“开始 OAuth 授权”";
            return;
        }

        if (IsBusy)
        {
            return;
        }

        if (!NexusOAuthService.TryExtractCodeFromCallback(OauthCallbackInput, out var code, out var stateFromCallback))
        {
            OauthStatusMessage = "未能解析授权码，请粘贴完整回调 URL 或纯 code";
            return;
        }

        if (!string.IsNullOrWhiteSpace(stateFromCallback) &&
            !string.Equals(stateFromCallback, _oauthStart.State, StringComparison.Ordinal))
        {
            OauthStatusMessage = "state 校验失败，请重新发起授权";
            return;
        }

        IsBusy = true;
        OauthStatusMessage = "正在交换 Token...";

        var tokenResult = await _nexusOAuthService.ExchangeCodeAsync(code, _oauthStart.CodeVerifier, _oauthStart.RedirectUri);
        IsBusy = false;

        if (!tokenResult.IsSuccess || tokenResult.Token == null)
        {
            OauthStatusMessage = tokenResult.Message;
            return;
        }

        var profile = tokenResult.Profile;
        OauthStatusMessage = $"OAuth 登录成功：{profile.UserName}";
        UserName = profile.UserName;
        MembershipType = profile.MembershipType;
        IsLoggedIn = true;
        _lastOAuthAuthorizeUrl = string.Empty;
        _sharedLastOAuthAuthorizeUrl = string.Empty;
        NotifyAuthorizeUrlChanged();

        RequestClose?.Invoke(this, new NexusLoginResult
        {
            IsOAuthLogin = true,
            OAuthAccessToken = tokenResult.Token.AccessToken,
            OAuthRefreshToken = tokenResult.Token.RefreshToken,
            OAuthIdToken = tokenResult.Token.IdToken,
            UserName = profile.UserName,
            MembershipType = profile.MembershipType,
            UserId = profile.UserId
        });
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke(this, null);
    }

    [RelayCommand]
    private Task LoginAsync()
    {
        return StartOAuthAutoLoginAsync();
    }

    [RelayCommand]
    private void ReopenBrowserPage()
    {
        RetryOpenAuthorizePage();
    }

    [RelayCommand]
    private void Close()
    {
        Cancel();
    }

    [RelayCommand]
    private void Logout()
    {
        IsLoggedIn = false;
        UserName = string.Empty;
        MembershipType = string.Empty;
        ApiKey = string.Empty;
        OauthCallbackInput = string.Empty;
        OauthStatusMessage = "已登出";
        StatusMessage = "已登出";
        _lastOAuthAuthorizeUrl = string.Empty;
        _sharedLastOAuthAuthorizeUrl = string.Empty;
        NotifyAuthorizeUrlChanged();

        RequestClose?.Invoke(this, new NexusLoginResult
        {
            ApiKey = string.Empty,
            IsOAuthLogin = false,
            OAuthAccessToken = string.Empty,
            OAuthRefreshToken = string.Empty,
            OAuthIdToken = string.Empty,
            UserName = string.Empty,
            MembershipType = string.Empty,
            UserId = 0
        });
    }

    partial void OnIsLoggedInChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotLoggedIn));
    }

    private static global::Avalonia.Input.Platform.IClipboard? GetClipboard()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow?.Clipboard;
        }

        return null;
    }

    private void NotifyAuthorizeUrlChanged()
    {
        OnPropertyChanged(nameof(CanRetryOpenAuthorizePage));
        OnPropertyChanged(nameof(CanCopyAuthorizeUrl));
        OnPropertyChanged(nameof(ShowReopenBrowserButton));
        OnPropertyChanged(nameof(RollbackAuthorizeUrlPreview));
    }

    private static string BuildAuthorizeUrlPreview(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url.Length > 96 ? url[..96] + "..." : url;
        }

        var hostAndPath = uri.GetLeftPart(UriPartial.Path);
        return hostAndPath.Length > 96 ? hostAndPath[..96] + "..." : hostAndPath;
    }
}