using System.Net.Http;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SVL.Avalonia.Services;

public sealed class NexusOAuthService
{
    private const int DefaultLoopbackPort = 12456;
    private const int MaxPortAttempts = 10;
    private const string ClientId = "vortex_loopback";
    private const string BaseUrl = "https://users.nexusmods.com/oauth";
    private const string Scope = "openid profile email";

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

    public async Task<NexusOAuthTokenResult> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri)
    {
        using var client = new HttpClient();
        var payload = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = redirectUri,
            ["code"] = code,
            ["code_verifier"] = codeVerifier
        };

        var response = await client.PostAsync($"{BaseUrl}/token", new FormUrlEncodedContent(payload));
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            return NexusOAuthTokenResult.Failed($"Token 交换失败: HTTP {(int)response.StatusCode} {body}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        var token = await JsonSerializer.DeserializeAsync<NexusOAuthTokenResponse>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

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

    public async Task<NexusOAuthTokenResult> AuthorizeWithLoopbackAsync(Action<string> openBrowser, TimeSpan? timeout = null)
    {
        var waitTimeout = timeout ?? TimeSpan.FromMinutes(3);
        HttpListener? listener = null;
        NexusOAuthStartResult? start = null;

        for (var i = 0; i < MaxPortAttempts; i++)
        {
            var port = DefaultLoopbackPort + i;
            var redirectUri = $"http://127.0.0.1:{port}";
            var attempt = CreateAuthorizationUrl(redirectUri);
            var listenPrefix = BuildListenerPrefix(attempt.RedirectUri);

            var candidate = new HttpListener();
            try
            {
                candidate.Prefixes.Add(listenPrefix);
                candidate.Start();
                listener = candidate;
                start = attempt;
                break;
            }
            catch
            {
                candidate.Close();
            }
        }

        if (listener == null || start == null)
        {
            return NexusOAuthTokenResult.Failed(
                "无法启动本地回调监听（端口占用），请关闭占用进程后重试或改用手动 code 登录",
                NexusOAuthFailureReason.ListenerStartFailed);
        }

        using (listener)
        {
            try
            {
                openBrowser(start.AuthorizeUrl);
            }
            catch (Exception ex)
            {
                return NexusOAuthTokenResult.Failed(
                    $"打开浏览器失败: {ex.Message}",
                    NexusOAuthFailureReason.BrowserOpenFailed,
                    start.AuthorizeUrl);
            }

            var contextTask = listener.GetContextAsync();
            var completed = await Task.WhenAny(contextTask, Task.Delay(waitTimeout));
            if (completed != contextTask)
            {
                return NexusOAuthTokenResult.Failed(
                    "等待 OAuth 回调超时，可点击“重开授权页”继续或改用手动 code 登录",
                    NexusOAuthFailureReason.Timeout,
                    start.AuthorizeUrl);
            }

            HttpListenerContext context;
            try
            {
                context = await contextTask;
            }
            catch (Exception ex)
            {
                return NexusOAuthTokenResult.Failed(
                    $"接收 OAuth 回调失败: {ex.Message}",
                    NexusOAuthFailureReason.ListenerReceiveFailed,
                    start.AuthorizeUrl);
            }

            var code = context.Request.QueryString["code"];
            var state = context.Request.QueryString["state"];
            var error = context.Request.QueryString["error"];

            var isOk = !string.IsNullOrWhiteSpace(code) &&
                       string.Equals(state, start.State, StringComparison.Ordinal) &&
                       string.IsNullOrWhiteSpace(error);

            await WriteCallbackPageAsync(context.Response, isOk);

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

            var exchange = await ExchangeCodeAsync(code, start.CodeVerifier, start.RedirectUri);
            return exchange.IsSuccess
                ? exchange
                : NexusOAuthTokenResult.Failed(exchange.Message, NexusOAuthFailureReason.TokenExchangeFailed, start.AuthorizeUrl);
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

            return new NexusOAuthProfile
            {
                UserName = userName,
                MembershipType = membership,
                UserId = userId
            };
        }
        catch
        {
            return new NexusOAuthProfile();
        }
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

            var key = kv[..index];
            var value = Uri.UnescapeDataString(kv[(index + 1)..]);
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

    private static string BuildListenerPrefix(string redirectUri)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri))
        {
            return "http://127.0.0.1:12456/";
        }

        var path = uri.AbsolutePath;
        if (!path.EndsWith('/'))
        {
            path += "/";
        }

        return $"{uri.Scheme}://{uri.Host}:{uri.Port}{path}";
    }

    private static async Task WriteCallbackPageAsync(HttpListenerResponse response, bool success)
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
            html.AppendLine("      <h1>认证成功</h1>");
            html.AppendLine("      <p>SVL 已成功连接到您的 Nexus Mods 账户</p>");
            html.AppendLine("      <p>请返回 SVL 继续操作</p>");
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

        var bytes = Encoding.UTF8.GetBytes(html.ToString());
        response.StatusCode = 200;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
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
    Unknown
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