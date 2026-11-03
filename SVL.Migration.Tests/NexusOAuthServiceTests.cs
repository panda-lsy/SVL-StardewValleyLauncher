using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net;
using System.Net.Sockets;
using SVL.Avalonia.Services;
using SVL.Avalonia.ViewModels;
using SVL.Core.Platform.Services;
using System.Net.Http;
using System.Text;

namespace SVL.Migration.Tests;

[TestClass]
public class NexusOAuthServiceTests
{
    [TestMethod]
    public void CreateAuthorizationUrl_ShouldContainRequiredFields()
    {
        var service = new NexusOAuthService();

        var start = service.CreateAuthorizationUrl();

        Assert.IsFalse(string.IsNullOrWhiteSpace(start.AuthorizeUrl));
        StringAssert.Contains(start.AuthorizeUrl, "response_type=code");
        StringAssert.Contains(start.AuthorizeUrl, "code_challenge_method=S256");
        Assert.IsFalse(string.IsNullOrWhiteSpace(start.State));
        Assert.IsFalse(string.IsNullOrWhiteSpace(start.CodeVerifier));
        Assert.IsFalse(string.IsNullOrWhiteSpace(start.RedirectUri));
    }

    [TestMethod]
    public void TryExtractCodeFromCallback_WithCallbackUrl_ShouldSucceed()
    {
        var ok = NexusOAuthService.TryExtractCodeFromCallback(
            "http://127.0.0.1:12456/?code=test-code&state=test-state",
            out var code,
            out var state);

        Assert.IsTrue(ok);
        Assert.AreEqual("test-code", code);
        Assert.AreEqual("test-state", state);
    }

    [TestMethod]
    public void TryExtractCodeFromCallback_WithRawCode_ShouldSucceed()
    {
        var ok = NexusOAuthService.TryExtractCodeFromCallback(
            "raw-code",
            out var code,
            out var state);

        Assert.IsTrue(ok);
        Assert.AreEqual("raw-code", code);
        Assert.IsNull(state);
    }

    [TestMethod]
    public void TryExtractCodeFromCallback_WithUrlEncodedCode_ShouldDecode()
    {
        var ok = NexusOAuthService.TryExtractCodeFromCallback(
            "http://127.0.0.1:12456/?code=abc%2Bdef%2Fxyz%3D&state=s1",
            out var code,
            out var state);

        Assert.IsTrue(ok);
        Assert.AreEqual("abc+def/xyz=", code);
        Assert.AreEqual("s1", state);
    }

    [TestMethod]
    public void TryExtractCodeFromCallback_WithoutCode_ShouldFail()
    {
        var ok = NexusOAuthService.TryExtractCodeFromCallback(
            "http://127.0.0.1:12456/?state=test-only",
            out _,
            out _);

        Assert.IsFalse(ok);
    }

    [TestMethod]
    public void TryExtractCodeFromCallback_WithInvalidUrl_ShouldFail()
    {
        var ok = NexusOAuthService.TryExtractCodeFromCallback(
            "http://127.0.0.1:12456/%zz",
            out _,
            out _);

        Assert.IsFalse(ok);
    }

    [TestMethod]
    public void CreateAuthorizationUrl_WithCustomRedirect_ShouldUseCustomRedirect()
    {
        var service = new NexusOAuthService();

        var start = service.CreateAuthorizationUrl("http://127.0.0.1:13001");

        Assert.AreEqual("http://127.0.0.1:13001", start.RedirectUri);
        StringAssert.Contains(start.AuthorizeUrl, "redirect_uri=http%3A%2F%2F127.0.0.1%3A13001");
    }

    [TestMethod]
    public async Task AuthorizeWithLoopbackAsync_ShouldServeCallbackOnAnAvailableLoopbackPort()
    {
        using var httpClient = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        Task<HttpResponseMessage> callbackTask = null;
        var service = new NexusOAuthService();

        var result = await service.AuthorizeWithLoopbackAsync(authorizeUrl =>
        {
            var authorizeUri = new Uri(authorizeUrl);
            var redirectUri = GetQueryParameter(authorizeUri, "redirect_uri");
            var state = GetQueryParameter(authorizeUri, "state");
            var callbackUri = new Uri($"{redirectUri}/?error=access_denied&state={Uri.EscapeDataString(state)}");

            Assert.AreEqual("127.0.0.1", callbackUri.Host);
            Assert.IsTrue(callbackUri.Port > 0, "OAuth 必须使用监听器实际绑定的回环端口");
            callbackTask = httpClient.GetAsync(callbackUri);
        }, TimeSpan.FromSeconds(10));

        Assert.IsNotNull(callbackTask, "监听器启动后应打开授权页并允许触发回调");
        Assert.AreEqual(NexusOAuthFailureReason.UserCancelled, result.FailureReason);

        using var response = await callbackTask;
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "认证失败");
    }

    [TestMethod]
    public async Task AuthorizeWithLoopbackAsync_ShouldExchangeValidBrowserCallback()
    {
        var handler = new TokenExchangeHandler();
        using var tokenClient = new HttpClient(handler);
        using var browserClient = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        Task<HttpResponseMessage> callbackTask = null;
        NexusOAuthStartResult session = null;
        var service = new NexusOAuthService(tokenClient);

        var result = await service.AuthorizeWithLoopbackAsync(authorizeUrl =>
        {
            var authorizeUri = new Uri(authorizeUrl);
            var redirectUri = GetQueryParameter(authorizeUri, "redirect_uri");
            var state = GetQueryParameter(authorizeUri, "state");
            callbackTask = browserClient.GetAsync($"{redirectUri}/?code=synthetic-code&state={state}");
        }, TimeSpan.FromSeconds(5), started => session = started);

        Assert.IsTrue(result.IsSuccess, result.Message);
        using var response = await callbackTask;
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "授权已收到");
        var submitted = new Uri("http://127.0.0.1/?" + handler.RequestBody);
        Assert.AreEqual(session.RedirectUri, GetQueryParameter(submitted, "redirect_uri"));
        Assert.AreEqual(session.CodeVerifier, GetQueryParameter(submitted, "code_verifier"));
    }

    [TestMethod]
    public async Task AuthorizeWithLoopbackAsync_ShouldAcceptCallbackWhileBrowserPreconnectionIsIdle()
    {
        using var httpClient = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        using var preconnection = new TcpClient();
        Task<HttpResponseMessage> callbackTask = null;
        var service = new NexusOAuthService();

        var result = await service.AuthorizeWithLoopbackAsync(authorizeUrl =>
        {
            var authorizeUri = new Uri(authorizeUrl);
            var redirectUri = GetQueryParameter(authorizeUri, "redirect_uri");
            var state = GetQueryParameter(authorizeUri, "state");
            var callbackUri = new Uri($"{redirectUri}/?error=access_denied&state={Uri.EscapeDataString(state)}");

            callbackTask = Task.Run(async () =>
            {
                await preconnection.ConnectAsync(callbackUri.Host, callbackUri.Port);
                var incompleteRequest = Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {callbackUri.Authority}\r\n");
                await preconnection.GetStream().WriteAsync(incompleteRequest);
                await Task.Delay(100);
                return await httpClient.GetAsync(callbackUri);
            });
        }, TimeSpan.FromSeconds(3));

        Assert.AreEqual(NexusOAuthFailureReason.UserCancelled, result.FailureReason,
            "空闲预连接不能阻止同一授权会话的真实回调");
        using var response = await callbackTask;
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task AuthorizeWithLoopbackAsync_ShouldIgnoreUnrelatedRequestsAndWrongState()
    {
        using var httpClient = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        Task<(HttpStatusCode favicon, HttpStatusCode wrongState, HttpStatusCode callback)> browserTask = null;
        var service = new NexusOAuthService();

        var result = await service.AuthorizeWithLoopbackAsync(authorizeUrl =>
        {
            var authorizeUri = new Uri(authorizeUrl);
            var redirectUri = GetQueryParameter(authorizeUri, "redirect_uri");
            var state = GetQueryParameter(authorizeUri, "state");

            browserTask = Task.Run(async () =>
            {
                using var favicon = await httpClient.GetAsync($"{redirectUri}/favicon.ico");
                using var wrongState = await httpClient.GetAsync($"{redirectUri}/?code=not-a-real-code&state=incorrect");
                using var callback = await httpClient.GetAsync(
                    $"{redirectUri}/?error=access_denied&state={Uri.EscapeDataString(state)}");
                return (favicon.StatusCode, wrongState.StatusCode, callback.StatusCode);
            });
        }, TimeSpan.FromSeconds(3));

        Assert.AreEqual(NexusOAuthFailureReason.UserCancelled, result.FailureReason,
            "无关请求或错误 state 不能结束当前授权会话");
        var responses = await browserTask;
        Assert.AreEqual(HttpStatusCode.NotFound, responses.favicon);
        Assert.AreEqual(HttpStatusCode.BadRequest, responses.wrongState);
        Assert.AreEqual(HttpStatusCode.OK, responses.callback);
    }

    [TestMethod]
    public void TryExtractCodeForSession_ShouldRejectOldOrUnrelatedCallbackUrls()
    {
        var start = new NexusOAuthService().CreateAuthorizationUrl("http://127.0.0.1:13001");
        var valid = $"http://127.0.0.1:13001/?code=synthetic-code&state={start.State}";

        Assert.IsTrue(NexusOAuthService.TryExtractCodeForSession(valid, start, out var code, out _));
        Assert.AreEqual("synthetic-code", code);
        Assert.IsFalse(NexusOAuthService.TryExtractCodeForSession(
            $"http://127.0.0.1:13002/?code=synthetic-code&state={start.State}", start, out _, out _));
        Assert.IsFalse(NexusOAuthService.TryExtractCodeForSession(
            "http://127.0.0.1:13001/?code=synthetic-code&state=wrong", start, out _, out _));
        Assert.IsFalse(NexusOAuthService.TryExtractCodeForSession(
            "synthetic-code", start, out _, out _));
    }

    [TestMethod]
    public async Task AuthorizeWithLoopbackAsync_ShouldCompleteFromManualCallbackUsingSamePkceSession()
    {
        var handler = new TokenExchangeHandler();
        using var httpClient = new HttpClient(handler);
        var service = new NexusOAuthService(httpClient);
        var manualCallback = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        NexusOAuthStartResult session = null;

        var result = await service.AuthorizeWithLoopbackAsync(authorizeUrl =>
        {
            var authorizeUri = new Uri(authorizeUrl);
            var redirectUri = GetQueryParameter(authorizeUri, "redirect_uri");
            var state = GetQueryParameter(authorizeUri, "state");
            manualCallback.SetResult($"{redirectUri}/?code=synthetic-code&state={state}");
        }, TimeSpan.FromSeconds(5), started => session = started, manualCallback.Task);

        Assert.IsTrue(result.IsSuccess, result.Message);
        Assert.AreEqual("test-access-token", result.Token.AccessToken);
        var submitted = new Uri("http://127.0.0.1/?" + handler.RequestBody);
        Assert.AreEqual("synthetic-code", GetQueryParameter(submitted, "code"));
        Assert.AreEqual(session.RedirectUri, GetQueryParameter(submitted, "redirect_uri"));
        Assert.AreEqual(session.CodeVerifier, GetQueryParameter(submitted, "code_verifier"));
    }

    [TestMethod]
    public async Task NexusLoginDialog_ShouldAllowManualCallbackDuringAutomaticLogin()
    {
        var handler = new TokenExchangeHandler();
        using var httpClient = new HttpClient(handler);
        string authorizeUrl = null;
        string accessToken = null;
        var viewModel = new NexusLoginDialogViewModel(
            new NexusAuthService(), new NexusOAuthService(httpClient),
            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, 0,
            url => authorizeUrl = url);
        viewModel.RequestClose += (_, result) => accessToken = result?.OAuthAccessToken;

        var loginTask = viewModel.LoginCommand.ExecuteAsync(null);
        Assert.IsTrue(viewModel.IsBusy);
        Assert.IsTrue(viewModel.ShowManualOAuthCallback);
        Assert.IsNotNull(authorizeUrl);

        var authorizeUri = new Uri(authorizeUrl);
        var redirectUri = GetQueryParameter(authorizeUri, "redirect_uri");
        var state = GetQueryParameter(authorizeUri, "state");
        var oldAddress = new UriBuilder(redirectUri)
        {
            Port = new Uri(redirectUri).Port == 65535 ? 65534 : new Uri(redirectUri).Port + 1,
            Query = $"code=synthetic-code&state={state}"
        }.Uri;
        viewModel.OauthCallbackInput = oldAddress.ToString();
        await viewModel.CompleteOAuthLoginCommand.ExecuteAsync(null);
        Assert.IsTrue(viewModel.IsBusy, "旧回调地址不能结束当前授权会话");
        Assert.AreEqual(string.Empty, handler.RequestBody);

        viewModel.OauthCallbackInput = $"{redirectUri}/?code=synthetic-code&state={state}";
        await viewModel.CompleteOAuthLoginCommand.ExecuteAsync(null);
        await loginTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(viewModel.IsLoggedIn);
        Assert.AreEqual("test-access-token", accessToken);
        Assert.AreEqual(string.Empty, viewModel.OauthCallbackInput, "提交后不应继续显示授权码");
    }

    [TestMethod]
    public async Task NexusLoginDialog_ShouldStopLoopbackListenerWhenClosed()
    {
        string authorizeUrl = null;
        var viewModel = new NexusLoginDialogViewModel(
            new NexusAuthService(), new NexusOAuthService(),
            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, 0,
            url => authorizeUrl = url);

        var loginTask = viewModel.LoginCommand.ExecuteAsync(null);
        Assert.IsNotNull(authorizeUrl);
        var redirectUri = new Uri(GetQueryParameter(new Uri(authorizeUrl), "redirect_uri"));
        viewModel.CloseCommand.Execute(null);
        await loginTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsFalse(viewModel.IsBusy);
        Assert.IsFalse(viewModel.ShowManualOAuthCallback);
        using var probe = new TcpClient();
        await Assert.ThrowsExceptionAsync<SocketException>(async () =>
            await probe.ConnectAsync(redirectUri.Host, redirectUri.Port));
    }

    [TestMethod]
    public async Task NexusLoginDialog_ShouldCancelTokenExchangeWhenClosed()
    {
        var handler = new PendingTokenExchangeHandler();
        using var httpClient = new HttpClient(handler);
        string authorizeUrl = null;
        var viewModel = new NexusLoginDialogViewModel(
            new NexusAuthService(), new NexusOAuthService(httpClient),
            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, 0,
            url => authorizeUrl = url);

        var loginTask = viewModel.LoginCommand.ExecuteAsync(null);
        var authorizeUri = new Uri(authorizeUrl);
        var redirectUri = GetQueryParameter(authorizeUri, "redirect_uri");
        var state = GetQueryParameter(authorizeUri, "state");
        viewModel.OauthCallbackInput = $"{redirectUri}/?code=synthetic-code&state={state}";
        await viewModel.CompleteOAuthLoginCommand.ExecuteAsync(null);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.CloseCommand.Execute(null);
        await loginTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsFalse(viewModel.IsBusy);
        Assert.IsFalse(viewModel.IsLoggedIn);
        Assert.IsTrue(handler.WasCancelled);
    }

    [TestMethod]
    public void TokenResultFailed_ShouldKeepFailureReasonAndAuthorizeUrl()
    {
        var result = NexusOAuthTokenResult.Failed(
            "timeout",
            NexusOAuthFailureReason.Timeout,
            "https://users.nexusmods.com/oauth/authorize");

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(NexusOAuthFailureReason.Timeout, result.FailureReason);
        Assert.AreEqual("https://users.nexusmods.com/oauth/authorize", result.AuthorizeUrl);
    }

    [TestMethod]
    public void TokenResultFailed_WithoutReason_ShouldUseUnknown()
    {
        var result = NexusOAuthTokenResult.Failed("unexpected");

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(NexusOAuthFailureReason.Unknown, result.FailureReason);
    }

    [TestMethod]
    public void ParseUserFromIdToken_ShouldReadAvatarClaim()
    {
        var header = Base64UrlEncode("{}");
        var payload = Base64UrlEncode(
            "{\"name\":\"test-user\",\"sub\":\"123\",\"membership_role\":\"premium\",\"picture\":\"https://example.com/avatar.png\"}");
        var token = $"{header}.{payload}.signature";
        var method = typeof(NexusOAuthService).GetMethod(
            "ParseUserFromIdToken",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

        Assert.IsNotNull(method);
        var profile = (NexusOAuthProfile)method!.Invoke(null, [token])!;

        Assert.AreEqual("test-user", profile.UserName);
        Assert.AreEqual("Premium", profile.MembershipType);
        Assert.AreEqual(123, profile.UserId);
        Assert.AreEqual("https://example.com/avatar.png", profile.AvatarUrl);
    }

    [TestMethod]
    public void AvatarCache_ShouldKeepLegacyWpfFileNameAndRejectNonHttpUrls()
    {
        var path = AvatarCacheService.GetCachePath("user/name");

        Assert.IsTrue(path.EndsWith("user_name.png", StringComparison.OrdinalIgnoreCase));
        var task = AvatarCacheService.DownloadAndCacheAvatarAsync("file:///avatar.png", "user/name");
        Assert.IsNull(task.GetAwaiter().GetResult());
    }

    [TestMethod]
    public void NexusApiRateLimit_ShouldRecordHeadersAndFormatUsage()
    {
        NexusApiRateLimitService.Clear();
        using var response = new HttpResponseMessage();
        response.Headers.TryAddWithoutValidation("X-RL-Hourly-Limit", "100");
        response.Headers.TryAddWithoutValidation("X-RL-Hourly-Remaining", "73");
        response.Headers.TryAddWithoutValidation("X-RL-Daily-Limit", "1000");
        response.Headers.TryAddWithoutValidation("X-RL-Daily-Remaining", "901");

        NexusApiRateLimitService.Record(response.Headers);
        var snapshot = NexusApiRateLimitService.GetSnapshot();

        Assert.IsTrue(snapshot.IsInitialized);
        Assert.AreEqual("27/100", snapshot.HourlyUsageText);
        Assert.AreEqual("99/1000", snapshot.DailyUsageText);
        StringAssert.Contains(snapshot.StatusText, "小时剩余 73/100");
        StringAssert.Contains(snapshot.StatusText, "每日剩余 901/1000");
    }

    [TestMethod]
    public void NexusApiRateLimit_ShouldIgnoreResponsesWithoutRateLimitHeaders()
    {
        NexusApiRateLimitService.Clear();
        using var response = new HttpResponseMessage();

        NexusApiRateLimitService.Record(response.Headers);

        Assert.IsFalse(NexusApiRateLimitService.GetSnapshot().IsInitialized);
    }

    [TestMethod]
    public void NexusApiRateLimit_ShouldIsolateSubscriberExceptions()
    {
        NexusApiRateLimitService.Clear();
        var subscriberCalled = false;
        Action throwingSubscriber = () => throw new InvalidOperationException("simulated UI failure");
        Action healthySubscriber = () => subscriberCalled = true;
        NexusApiRateLimitService.SnapshotChanged += throwingSubscriber;
        NexusApiRateLimitService.SnapshotChanged += healthySubscriber;

        try
        {
            using var response = new HttpResponseMessage();
            response.Headers.TryAddWithoutValidation("X-RL-Hourly-Limit", "20");
            response.Headers.TryAddWithoutValidation("X-RL-Hourly-Remaining", "19");

            NexusApiRateLimitService.Record(response.Headers);

            Assert.IsTrue(subscriberCalled);
            Assert.IsTrue(NexusApiRateLimitService.GetSnapshot().IsInitialized);
        }
        finally
        {
            NexusApiRateLimitService.SnapshotChanged -= throwingSubscriber;
            NexusApiRateLimitService.SnapshotChanged -= healthySubscriber;
            NexusApiRateLimitService.Clear();
        }
    }

    [TestMethod]
    public void Settings_ShouldKeepStoredNexusProfileVisibleWhenTokenExpired()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-expired-nexus-profile-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var avatarPath = Path.Combine(root, "avatar.png");
        File.WriteAllBytes(avatarPath, [137, 80, 78, 71]);

        try
        {
            var settingsStore = new AppUserSettingsStore(root);
            settingsStore.Save(new SVL.Avalonia.Models.AppUserSettings
            {
                NexusUserName = "stored-user",
                NexusMembershipType = "Premium",
                NexusOAuthAvatarLocalPath = avatarPath
            });

            var localization = new LocalizationService(settingsStore);
            var viewModel = new SettingsPageViewModel(
                settingsStore,
                new DialogService(),
                new NexusAuthService(),
                new NexusOAuthService(),
                new LauncherUpdateService(),
                new ExternalProcessService(),
                new NxmProtocolRegistrationService(),
                localization,
                new ImageResourceService(localization));

            Assert.IsTrue(viewModel.IsNexusLoginExpired);
            Assert.IsTrue(viewModel.IsNexusLoggedIn, "旧资料仍应展示在账号卡片中，但不代表 Token 有效");
            Assert.IsFalse(viewModel.ShowNexusLoginGuide);
            Assert.AreEqual("stored-user", viewModel.NexusUserName);
            Assert.AreEqual(avatarPath, viewModel.NexusAvatarSource);
            Assert.AreEqual("重新登录 Nexus", viewModel.NexusLoginActionText);
            StringAssert.Contains(viewModel.NexusStatus, "失效");
            Assert.AreEqual("Access Token 已过期", viewModel.NexusApiStatistics);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // 测试结束时若系统仍短暂持有头像文件，忽略清理失败即可。
            }
        }
    }

    private static string Base64UrlEncode(string json)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string GetQueryParameter(Uri uri, string key)
    {
        foreach (var entry in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = entry.IndexOf('=');
            if (separator < 0 || !string.Equals(Uri.UnescapeDataString(entry[..separator]), key, StringComparison.Ordinal))
            {
                continue;
            }

            return Uri.UnescapeDataString(entry[(separator + 1)..].Replace("+", "%20", StringComparison.Ordinal));
        }

        Assert.Fail($"授权地址缺少 query 参数：{key}");
        return string.Empty;
    }

    private sealed class TokenExchangeHandler : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"access_token\":\"test-access-token\"}", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class PendingTokenExchangeHandler : HttpMessageHandler
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WasCancelled { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                throw;
            }

            throw new InvalidOperationException("测试中的 Token 请求不应正常完成");
        }
    }
}
