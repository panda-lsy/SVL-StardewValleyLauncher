using System.Net.Http;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SVL.Avalonia.Services;

public sealed class NexusOAuthService
{
    private const int DefaultLoopbackPort = 12456;
    private const string ClientId = "vortex_loopback";
    private const string BaseUrl = "https://users.nexusmods.com/oauth";
    private const string Scope = "openid profile email";
    private readonly HttpClient? _tokenHttpClient;

    public NexusOAuthService(HttpClient? tokenHttpClient = null)
    {
        _tokenHttpClient = tokenHttpClient;
    }

    public NexusOAuthStartResult CreateAuthorizationUrl(string? redirectUriOverride = null)
    {
        var state = Guid.NewGuid().ToString("N");
        var verifier = GenerateCodeVerifier();
        var challenge = GenerateCodeChallenge(verifier);
        var redirectUri = string.IsNullOrWhiteSpace(redirectUriOverride)
            ? $"http://127.0.0.1:{DefaultLoopbackPort}"
            : redirectUriOverride;

        var authorizeUrl = BuildAuthorizeUrl(redirectUri, state, challenge);

        return new NexusOAuthStartResult
        {
            AuthorizeUrl = authorizeUrl,
            State = state,
            CodeVerifier = verifier,
            RedirectUri = redirectUri
        };
    }

    public async Task<NexusOAuthTokenResult> ExchangeCodeAsync(
        string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken = default)
    {
        using var ownedClient = _tokenHttpClient == null ? new HttpClient() : null;
        var client = _tokenHttpClient ?? ownedClient!;
        var payload = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = redirectUri,
            ["code"] = code,
            ["code_verifier"] = codeVerifier
        };

        using var response = await client.PostAsync(
            $"{BaseUrl}/token", new FormUrlEncodedContent(payload), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return NexusOAuthTokenResult.Failed($"Token 交换失败: HTTP {(int)response.StatusCode} {body}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var token = await JsonSerializer.DeserializeAsync<NexusOAuthTokenResponse>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }, cancellationToken);

        if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            return NexusOAuthTokenResult.Failed("Token 响应无效");
        }

        var profile = ParseUserFromIdToken(token.IdToken);
        return NexusOAuthTokenResult.Success(token, profile);
    }

    public async Task<NexusOAuthTokenResult> RefreshAccessTokenAsync(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return NexusOAuthTokenResult.Failed("Refresh Token 为空");
        }

        using var client = new HttpClient();
        var payload = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = refreshToken.Trim()
        };

        var response = await client.PostAsync($"{BaseUrl}/token", new FormUrlEncodedContent(payload));
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            return NexusOAuthTokenResult.Failed($"刷新 Token 失败: HTTP {(int)response.StatusCode} {body}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        var token = await JsonSerializer.DeserializeAsync<NexusOAuthTokenResponse>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            return NexusOAuthTokenResult.Failed("刷新 Token 响应无效");
        }

        var profile = ParseUserFromIdToken(token.IdToken);
        return NexusOAuthTokenResult.Success(token, profile);
    }

    public async Task<NexusOAuthTokenResult> AuthorizeWithLoopbackAsync(
        Action<string> openBrowser,
        TimeSpan? timeout = null,
        Action<NexusOAuthStartResult>? onStarted = null,
        Task<string>? manualCallback = null,
        CancellationToken cancellationToken = default)
    {
        var waitTimeout = timeout ?? TimeSpan.FromMinutes(10);
        TcpListener? listener = null;
        NexusOAuthStartResult? start = null;
        try
        {
            // 绑定系统分配的空闲回环端口，避免固定端口冲突和 Windows
            // HttpListener/HTTP.sys URL ACL 导致浏览器回调无法连接。
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            start = CreateAuthorizationUrl($"http://127.0.0.1:{endpoint.Port}");
        }
        catch (Exception exception)
        {
            listener?.Stop();
            return NexusOAuthTokenResult.Failed(
                $"无法启动本地回调监听：{exception.Message}",
                NexusOAuthFailureReason.ListenerStartFailed);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                onStarted?.Invoke(start);
                cancellationToken.ThrowIfCancellationRequested();
                openBrowser(start.AuthorizeUrl);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return NexusOAuthTokenResult.Failed(
                    $"打开浏览器失败: {ex.Message}",
                    NexusOAuthFailureReason.BrowserOpenFailed,
                    start.AuthorizeUrl);
            }

            using var timeoutSource = new CancellationTokenSource(waitTimeout);
            using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token, cancellationToken);
            var query = await WaitForLoopbackCallbackAsync(listener, start, manualCallback, linkedSource.Token);

            var code = query.GetValueOrDefault("code");
            var state = query.GetValueOrDefault("state");
            var error = query.GetValueOrDefault("error");

            if (!string.IsNullOrWhiteSpace(error))
            {
                var reason = error.Equals("access_denied", StringComparison.OrdinalIgnoreCase)
                    ? NexusOAuthFailureReason.UserCancelled
                    : NexusOAuthFailureReason.AuthorizeFailed;

                return NexusOAuthTokenResult.Failed($"OAuth 授权失败: {error}", reason, start.AuthorizeUrl);
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                return NexusOAuthTokenResult.Failed("未获取到授权码", NexusOAuthFailureReason.MissingCode, start.AuthorizeUrl);
            }

            if (!string.Equals(state, start.State, StringComparison.Ordinal))
            {
                return NexusOAuthTokenResult.Failed("OAuth state 校验失败，请重试", NexusOAuthFailureReason.StateMismatch, start.AuthorizeUrl);
            }

            try
            {
                var exchange = await ExchangeCodeAsync(code, start.CodeVerifier, start.RedirectUri, cancellationToken);
                return exchange.IsSuccess
                    ? exchange
                    : NexusOAuthTokenResult.Failed(exchange.Message, NexusOAuthFailureReason.TokenExchangeFailed, start.AuthorizeUrl);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return NexusOAuthTokenResult.Failed(
                    $"Token 交换失败：{exception.Message}", NexusOAuthFailureReason.TokenExchangeFailed, start.AuthorizeUrl);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return NexusOAuthTokenResult.Failed("已取消 Nexus 授权", NexusOAuthFailureReason.Cancelled);
        }
        catch (OperationCanceledException)
        {
            return NexusOAuthTokenResult.Failed(
                "等待自动回调超时；可粘贴本次回调地址，或重新开始登录。",
                NexusOAuthFailureReason.Timeout,
                start.AuthorizeUrl);
        }
        catch (Exception exception)
        {
            return NexusOAuthTokenResult.Failed(
                $"接收 OAuth 回调失败：{exception.Message}",
                NexusOAuthFailureReason.ListenerReceiveFailed,
                start.AuthorizeUrl);
        }
        finally
        {
            listener.Stop();
        }
    }

    public async Task<NexusOAuthValidateResult> ValidateAccessTokenAsync(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return NexusOAuthValidateResult.Failed("Access Token 为空");
        }

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.nexusmods.com/v1/users/validate.json");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken.Trim());
        request.Headers.Add("application-name", "SVL.Avalonia");

        var response = await client.SendAsync(request);
        NexusApiRateLimitService.Record(response.Headers);
        if (!response.IsSuccessStatusCode)
        {
            return NexusOAuthValidateResult.Failed($"验证失败: HTTP {(int)response.StatusCode}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;

        var userName = root.TryGetProperty("name", out var nameProp)
            ? nameProp.GetString() ?? "Unknown"
            : "Unknown";
        var userId = root.TryGetProperty("user_id", out var idProp) && idProp.TryGetInt32(out var parsed)
            ? parsed
            : 0;
        var membership = root.TryGetProperty("is_premium", out var premiumProp) && premiumProp.ValueKind == JsonValueKind.True
            ? "Premium"
            : "Free";

        return NexusOAuthValidateResult.Success(userName, membership, userId);
    }

    public static bool TryExtractCodeFromCallback(string callbackOrCode, out string code, out string? state)
    {
        code = string.Empty;
        state = null;

        if (string.IsNullOrWhiteSpace(callbackOrCode))
        {
            return false;
        }

        var raw = callbackOrCode.Trim();
        if (!raw.Contains("http://", StringComparison.OrdinalIgnoreCase) &&
            !raw.Contains("https://", StringComparison.OrdinalIgnoreCase))
        {
            code = raw;
            return true;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var query = ParseQuery(uri.Query);
        if (!query.TryGetValue("code", out var parsedCode) || string.IsNullOrWhiteSpace(parsedCode))
        {
            return false;
        }

        code = parsedCode;

        state = query.TryGetValue("state", out var stateValue) ? stateValue : null;
        return true;
    }

    public static bool TryExtractCodeForSession(
        string callbackUrl, NexusOAuthStartResult start, out string code, out string error)
    {
        code = string.Empty;
        error = string.Empty;
        if (!Uri.TryCreate(callbackUrl?.Trim(), UriKind.Absolute, out var callbackUri) ||
            !Uri.TryCreate(start.RedirectUri, UriKind.Absolute, out var expectedUri))
        {
            error = "请粘贴浏览器地址栏中的完整回调地址。";
            return false;
        }

        if (!string.Equals(callbackUri.Scheme, expectedUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(callbackUri.Host, expectedUri.Host, StringComparison.OrdinalIgnoreCase) ||
            callbackUri.Port != expectedUri.Port ||
            !string.Equals(callbackUri.AbsolutePath, expectedUri.AbsolutePath, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(callbackUri.UserInfo) || !string.IsNullOrEmpty(callbackUri.Fragment))
        {
            error = "回调地址不属于当前登录会话，请使用最新打开的授权页面。";
            return false;
        }

        var query = ParseQuery(callbackUri.Query);
        if (!query.TryGetValue("code", out var parsedCode) || string.IsNullOrWhiteSpace(parsedCode))
        {
            error = "回调地址中没有授权码，请重新完成 Nexus 授权。";
            return false;
        }

        if (!query.TryGetValue("state", out var state) ||
            !string.Equals(state, start.State, StringComparison.Ordinal))
        {
            error = "回调地址与当前登录会话不匹配，请重新完成授权。";
            return false;
        }

        code = parsedCode;
        return true;
    }

    private static NexusOAuthProfile ParseUserFromIdToken(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken))
        {
            return new NexusOAuthProfile();
        }

        try
        {
            var parts = idToken.Split('.');
            if (parts.Length != 3)
            {
                return new NexusOAuthProfile();
            }

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

            var bytes = Convert.FromBase64String(payload);
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;

            var userName = root.TryGetProperty("name", out var nameProp)
                ? nameProp.GetString() ?? string.Empty
                : string.Empty;

            var userIdRaw = root.TryGetProperty("sub", out var subProp)
                ? subProp.GetString() ?? string.Empty
                : string.Empty;

            var userId = int.TryParse(userIdRaw, out var parsedId) ? parsedId : 0;

            var role = root.TryGetProperty("membership_role", out var roleProp)
                ? roleProp.GetString() ?? string.Empty
                : string.Empty;

            var membership = role.Equals("premium", StringComparison.OrdinalIgnoreCase) ||
                             role.Equals("supporter", StringComparison.OrdinalIgnoreCase)
                ? "Premium"
                : "Free";

            var avatarUrl = TryGetString(root, "picture") ??
                            TryGetString(root, "avatar_url") ??
                            TryGetString(root, "avatar") ??
                            string.Empty;

            return new NexusOAuthProfile
            {
                UserName = userName,
                MembershipType = membership,
                UserId = userId,
                AvatarUrl = avatarUrl
            };
        }
        catch
        {
            return new NexusOAuthProfile();
        }
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
        {
            return map;
        }

        foreach (var kv in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = kv.IndexOf('=');
            if (index < 0)
            {
                continue;
            }

            var key = WebUtility.UrlDecode(kv[..index]);
            var value = WebUtility.UrlDecode(kv[(index + 1)..]);
            map[key] = value;
        }

        return map;
    }

    private static string GenerateCodeVerifier()
    {
        var bytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string GenerateCodeChallenge(string verifier)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(verifier));
        return Base64UrlEncode(hash);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    private static string BuildAuthorizeUrl(string redirectUri, string state, string challenge)
    {
        return
            $"{BaseUrl}/authorize?response_type=code" +
            $"&scope={Uri.EscapeDataString(Scope)}" +
            "&code_challenge_method=S256" +
            $"&client_id={ClientId}" +
            $"&application={Uri.EscapeDataString("Stardew Valley Launcher")}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&state={state}" +
            $"&code_challenge={challenge}";
    }

    private static async Task<Dictionary<string, string>> WaitForLoopbackCallbackAsync(
        TcpListener listener,
        NexusOAuthStartResult start,
        Task<string>? manualCallback,
        CancellationToken cancellationToken)
    {
        var callback = new TaskCompletionSource<Dictionary<string, string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var pendingSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var connections = new List<Task>();
        Task<TcpClient>? pendingAccept = null;

        try
        {
            while (true)
            {
                pendingAccept = listener.AcceptTcpClientAsync(pendingSource.Token).AsTask();
                var completed = manualCallback == null
                    ? await Task.WhenAny(pendingAccept, callback.Task)
                    : await Task.WhenAny(pendingAccept, callback.Task, manualCallback);
                if (completed == callback.Task)
                {
                    return await callback.Task;
                }

                if (completed == manualCallback)
                {
                    var callbackUrl = await manualCallback!;
                    if (!TryExtractCodeForSession(callbackUrl, start, out var code, out _))
                    {
                        throw new InvalidDataException("手动回调地址与当前授权会话不匹配");
                    }

                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["code"] = code,
                        ["state"] = start.State
                    };
                }

                var client = await pendingAccept;
                pendingAccept = null;
                connections.RemoveAll(task => task.IsCompleted);
                if (connections.Count >= 32)
                {
                    client.Dispose();
                    continue;
                }

                connections.Add(HandleLoopbackConnectionAsync(client, start, callback, pendingSource.Token));
            }
        }
        finally
        {
            pendingSource.Cancel();
            if (pendingAccept != null)
            {
                try
                {
                    using var unusedClient = await pendingAccept;
                }
                catch (OperationCanceledException)
                {
                }
                catch (SocketException)
                {
                }
            }

            await Task.WhenAll(connections);
        }
    }

    private static async Task HandleLoopbackConnectionAsync(
        TcpClient client,
        NexusOAuthStartResult start,
        TaskCompletionSource<Dictionary<string, string>> callback,
        CancellationToken cancellationToken)
    {
        using (client)
        using (var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            try
            {
                connectionTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                client.NoDelay = true;
                await using var stream = client.GetStream();
                var callbackUri = await ReadLoopbackRequestAsync(stream, start.RedirectUri, connectionTimeout.Token);
                var expectedUri = new Uri(start.RedirectUri);
                if (!string.Equals(callbackUri.AbsolutePath, expectedUri.AbsolutePath, StringComparison.Ordinal))
                {
                    await WritePlainResponseAsync(stream, "404 Not Found", connectionTimeout.Token);
                    return;
                }

                var query = ParseQuery(callbackUri.Query);
                var code = query.GetValueOrDefault("code");
                var error = query.GetValueOrDefault("error");
                if ((!string.IsNullOrWhiteSpace(code) || !string.IsNullOrWhiteSpace(error)) &&
                    string.Equals(query.GetValueOrDefault("state"), start.State, StringComparison.Ordinal))
                {
                    try
                    {
                        await WriteCallbackPageAsync(stream, string.IsNullOrWhiteSpace(error), connectionTimeout.Token);
                    }
                    finally
                    {
                        // 收到经过 state 校验的授权结果后，即使浏览器提前断开也继续处理。
                        callback.TrySetResult(query);
                    }

                    return;
                }

                await WritePlainResponseAsync(stream, "400 Bad Request", connectionTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                // 关闭超时的预连接，继续等待同一授权会话的其他连接。
            }
            catch (Exception)
            {
                // 浏览器预连接、断开的连接或无效请求都不能结束整个授权会话。
            }
        }
    }

    private static async Task<Uri> ReadLoopbackRequestAsync(
        Stream stream,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            stream,
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);

        var requestLine = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(requestLine) || requestLine.Length > 8192)
        {
            throw new InvalidDataException("本地回调请求行无效");
        }

        var requestParts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestParts.Length != 3 || !string.Equals(requestParts[0], "GET", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("本地回调请求格式无效");
        }

        var headerLength = requestLine.Length;
        while (true)
        {
            var header = await reader.ReadLineAsync(cancellationToken);
            if (header == null)
            {
                throw new InvalidDataException("本地回调请求头不完整");
            }

            headerLength += header.Length;
            if (headerLength > 16 * 1024)
            {
                throw new InvalidDataException("本地回调请求头过大");
            }

            if (header.Length == 0)
            {
                break;
            }
        }

        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var expectedUri) ||
            !requestParts[1].StartsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidDataException("本地回调地址无效");
        }

        var callbackUri = new Uri(expectedUri, requestParts[1]);
        if (!string.Equals(callbackUri.Host, expectedUri.Host, StringComparison.OrdinalIgnoreCase) ||
            callbackUri.Port != expectedUri.Port)
        {
            throw new InvalidDataException("收到的请求不是当前 Nexus OAuth 回调");
        }

        return callbackUri;
    }

    private static Task WritePlainResponseAsync(Stream stream, string status, CancellationToken cancellationToken)
    {
        return WriteHttpResponseAsync(stream, status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(status), cancellationToken);
    }

    private static async Task WriteCallbackPageAsync(Stream stream, bool success, CancellationToken cancellationToken)
    {
        var html = new StringBuilder();
        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine("<html lang=\"zh-CN\">");
        html.AppendLine("<head>");
        html.AppendLine("    <meta charset=\"UTF-8\">");
        html.AppendLine("    <title>SVL - Nexus Mods</title>");
        html.AppendLine("    <meta http-equiv=\"refresh\" content=\"4;url=https://www.nexusmods.com/\" />");
        html.AppendLine("    <style>");
        html.AppendLine("      * { margin: 0; padding: 0; box-sizing: border-box; }");
        html.AppendLine("      body {");
        html.AppendLine("        display: flex; flex-direction: column; height: 100vh;");
        html.AppendLine("        justify-content: center; align-items: center;");
        html.AppendLine("        background: #F5EDE0;");
        html.AppendLine("        font-family: 'Segoe UI', 'Microsoft YaHei', sans-serif; color: #3A2B22;");
        html.AppendLine("      }");
        html.AppendLine("      .card {");
        html.AppendLine("        background: #FAF7EE; border-radius: 14px;");
        html.AppendLine("        padding: 48px 56px; text-align: center;");
        html.AppendLine("        border: 1px solid #B89B7F;");
        html.AppendLine("        box-shadow: 0 4px 24px rgba(58,43,34,0.08);");
        html.AppendLine("        max-width: 400px;");
        html.AppendLine("      }");
        html.AppendLine("      .icon-wrap {");
        html.AppendLine("        width: 64px; height: 64px; border-radius: 50%; margin: 0 auto 20px;");
        html.AppendLine("        display: flex; align-items: center; justify-content: center;");
        html.AppendLine("      }");
        html.AppendLine("      .success .icon-wrap { background: #E8F5E9; }");
        html.AppendLine("      .fail .icon-wrap { background: #FFEBEE; }");
        html.AppendLine("      svg { width: 32px; height: 32px; }");
        html.AppendLine("      .success svg { stroke: #4CAF50; }");
        html.AppendLine("      .fail svg { stroke: #E53935; }");
        html.AppendLine("      h1 { font-size: 20px; font-weight: 600; margin-bottom: 10px; }");
        html.AppendLine("      .success h1 { color: #4CAF50; }");
        html.AppendLine("      .fail h1 { color: #E53935; }");
        html.AppendLine("      p { font-size: 14px; color: #6D5D4E; line-height: 1.7; }");
        html.AppendLine("      .hint { margin-top: 28px; font-size: 12px; color: #A89888; }");
        html.AppendLine("      .brand { margin-top: 36px; padding-top: 16px; border-top: 1px solid #E0D6C8; }");
        html.AppendLine("      .brand span { font-size: 11px; color: #B89B7F; letter-spacing: 1.5px; text-transform: uppercase; }");
        html.AppendLine("    </style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine("  <div class=\"card\">");

        if (success)
        {
            html.AppendLine("    <div class=\"success\">");
            html.AppendLine("      <div class=\"icon-wrap\">");
            html.AppendLine("        <svg viewBox=\"0 0 24 24\" fill=\"none\" stroke-width=\"2.5\" stroke-linecap=\"round\" stroke-linejoin=\"round\">");
            html.AppendLine("          <polyline points=\"20 6 9 17 4 12\"/>");
            html.AppendLine("        </svg>");
            html.AppendLine("      </div>");
            html.AppendLine("      <h1>授权已收到</h1>");
            html.AppendLine("      <p>SVL 正在完成 Nexus Mods 登录</p>");
            html.AppendLine("      <p>请返回 SVL 查看登录结果</p>");
            html.AppendLine("    </div>");
        }
        else
        {
            html.AppendLine("    <div class=\"fail\">");
            html.AppendLine("      <div class=\"icon-wrap\">");
            html.AppendLine("        <svg viewBox=\"0 0 24 24\" fill=\"none\" stroke-width=\"2.5\" stroke-linecap=\"round\" stroke-linejoin=\"round\">");
            html.AppendLine("          <line x1=\"18\" y1=\"6\" x2=\"6\" y2=\"18\"/>");
            html.AppendLine("          <line x1=\"6\" y1=\"6\" x2=\"18\" y2=\"18\"/>");
            html.AppendLine("        </svg>");
            html.AppendLine("      </div>");
            html.AppendLine("      <h1>认证失败</h1>");
            html.AppendLine("      <p>请返回 SVL 查看错误信息并重试</p>");
            html.AppendLine("    </div>");
        }

        html.AppendLine("    <p class=\"hint\">4 秒后自动跳转到 Nexus Mods...</p>");
        html.AppendLine("    <div class=\"brand\"><span>Stardew Valley Launcher</span></div>");
        html.AppendLine("  </div>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");

        await WriteHttpResponseAsync(
            stream, "200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html.ToString()), cancellationToken);
    }

    private static async Task WriteHttpResponseAsync(
        Stream stream, string status, string contentType, byte[] bytes, CancellationToken cancellationToken)
    {
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
        await stream.WriteAsync(headers, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}

public sealed class NexusOAuthStartResult
{
    public string AuthorizeUrl { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string CodeVerifier { get; init; } = string.Empty;
    public string RedirectUri { get; init; } = string.Empty;
}

public sealed class NexusOAuthProfile
{
    public string UserName { get; init; } = string.Empty;
    public string MembershipType { get; init; } = "Free";
    public int UserId { get; init; }
    public string AvatarUrl { get; init; } = string.Empty;
}

public sealed class NexusOAuthTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; init; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; init; } = string.Empty;

    [JsonPropertyName("id_token")]
    public string IdToken { get; init; } = string.Empty;

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }
}

public sealed class NexusOAuthTokenResult
{
    public bool IsSuccess { get; init; }
    public string Message { get; init; } = string.Empty;
    public NexusOAuthTokenResponse? Token { get; init; }
    public NexusOAuthProfile Profile { get; init; } = new();
    public NexusOAuthFailureReason FailureReason { get; init; } = NexusOAuthFailureReason.None;
    public string AuthorizeUrl { get; init; } = string.Empty;

    public static NexusOAuthTokenResult Success(NexusOAuthTokenResponse token, NexusOAuthProfile profile)
    {
        return new NexusOAuthTokenResult { IsSuccess = true, Message = "登录成功", Token = token, Profile = profile };
    }

    public static NexusOAuthTokenResult Failed(string message, NexusOAuthFailureReason reason = NexusOAuthFailureReason.Unknown, string authorizeUrl = "")
    {
        return new NexusOAuthTokenResult
        {
            IsSuccess = false,
            Message = message,
            FailureReason = reason,
            AuthorizeUrl = authorizeUrl
        };
    }
}

public enum NexusOAuthFailureReason
{
    None,
    Timeout,
    ListenerStartFailed,
    ListenerReceiveFailed,
    BrowserOpenFailed,
    UserCancelled,
    AuthorizeFailed,
    MissingCode,
    StateMismatch,
    TokenExchangeFailed,
    Unknown,
    Cancelled
}

public sealed class NexusOAuthValidateResult
{
    public bool IsSuccess { get; init; }
    public string Message { get; init; } = string.Empty;
    public string UserName { get; init; } = string.Empty;
    public string MembershipType { get; init; } = "Free";
    public int UserId { get; init; }

    public static NexusOAuthValidateResult Success(string userName, string membershipType, int userId)
    {
        return new NexusOAuthValidateResult
        {
            IsSuccess = true,
            Message = "验证成功",
            UserName = userName,
            MembershipType = membershipType,
            UserId = userId
        };
    }

    public static NexusOAuthValidateResult Failed(string message)
    {
        return new NexusOAuthValidateResult { IsSuccess = false, Message = message };
    }
}
