using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SVL.Core.Logging;

namespace SVL.Core.Download.NexusMods;

/// <summary>
/// Nexus Mods OAuth 2.0 PKCE 认证管理器
/// </summary>
public class NexusOAuthManager : IDisposable
{
    private const string ClientId = "vortex_loopback";
    private const string BaseUrl = "https://users.nexusmods.com/oauth";
    private const string Scope = "openid profile email";

    private HttpListener _httpListener;

    /// <summary>
    /// 执行OAuth认证流程
    /// </summary>
    /// <param name="openBrowserCallback">打开浏览器的回调函数</param>
    /// <returns>访问令牌</returns>
    public async Task<NexusTokenResponse> AuthenticateAsync(Action<string> openBrowserCallback)
    {
        HttpListener listener = null;
        try
        {
            Log.Info("[NexusOAuth] 开始OAuth认证流程");

            // 1. 启动本地HTTP服务器接收回调
            // 尝试多个端口直到找到可用的
            listener = new HttpListener();
            int port = 12456;
            bool started = false;
            const int maxAttempts = 10;

            for (int i = 0; i < maxAttempts; i++)
            {
                try
                {
                    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                    listener.Start();
                    started = true;
                    Log.Info($"[NexusOAuth] 本地服务器启动，端口: {port}");
                    break;
                }
                catch (HttpListenerException)
                {
                    // 端口被占用，尝试下一个端口
                    listener.Prefixes.Clear();
                    port++;
                    Log.Debug($"[NexusOAuth] 端口 {port - 1} 被占用，尝试端口 {port}");
                }
            }

            if (!started)
            {
                throw new InvalidOperationException($"无法启动本地HTTP服务器，已尝试 {maxAttempts} 个端口");
            }

            // 2. 生成PKCE验证器和挑战
            var verifier = GenerateCodeVerifier();
            var challenge = GenerateCodeChallenge(verifier);
            var state = Guid.NewGuid().ToString();

            Log.Info($"[NexusOAuth] 生成PKCE参数: state={state}");

            // 3. 构建授权URL
            var authUrl = $"{BaseUrl}/authorize?" +
                $"response_type=code&" +
                $"scope={Uri.EscapeDataString(Scope)}&" +
                $"code_challenge_method=S256&" +
                $"client_id={ClientId}&" +
                $"redirect_uri={Uri.EscapeDataString($"http://127.0.0.1:{port}")}&" +
                $"state={state}&" +
                $"code_challenge={challenge}";

            Log.Info($"[NexusOAuth] 授权URL已生成");

            // 4. 打开浏览器让用户授权
            openBrowserCallback?.Invoke(authUrl);

            // 5. 等待OAuth回调
            Log.Info("[NexusOAuth] 等待OAuth回调...");

            var context = await listener.GetContextAsync();
            var query = context.Request.QueryString;

            var code = query["code"];
            var returnedState = query["state"];

            // 发送响应页面
            var responseHtml = GenerateResultPage(true);
            var buffer = Encoding.UTF8.GetBytes(responseHtml);
            context.Response.ContentLength64 = buffer.Length;
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            context.Response.Close();

            // 验证state
            if (returnedState != state)
            {
                Log.Error("[NexusOAuth] State不匹配，可能存在CSRF攻击");
                throw new SecurityException("OAuth state不匹配");
            }

            Log.Info("[NexusOAuth] 收到授权码，开始交换Token");

            // 6. 交换access token
            var token = await ExchangeCodeForToken(code, verifier, port);

            Log.Info($"[NexusOAuth] OAuth认证成功，Token过期时间: {token.ExpiresIn}秒");

            return token;
        }
        finally
        {
            // 停止HTTP服务器
            if (listener != null)
            {
                try
                {
                    listener.Close();
                    listener = null;
                }
                catch (Exception ex)
                {
                    Log.Warn($"[NexusOAuth] 关闭HTTP服务器时出错: {ex.Message}");
                }
            }

            // 清理成员变量
            _httpListener = null;
        }
    }

    /// <summary>
    /// 交换授权码获取Token
    /// </summary>
    private async Task<NexusTokenResponse> ExchangeCodeForToken(string code, string verifier, int port)
    {
        using (var client = new HttpClient())
        {
            client.DefaultRequestHeaders.Add("User-Agent", "SVL-StardewLauncher/1.0");

            var content = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = ClientId,
                ["redirect_uri"] = $"http://127.0.0.1:{port}",
                ["code"] = code,
                ["code_verifier"] = verifier
            };

            Log.Info("[NexusOAuth] 交换Token请求已发送");

            var response = await client.PostAsync($"{BaseUrl}/token", new FormUrlEncodedContent(content));

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Log.Error($"[NexusOAuth] Token交换失败: {response.StatusCode} - {errorContent}");
                throw new HttpRequestException($"Token交换失败: {errorContent}");
            }

            var json = await response.Content.ReadAsStringAsync();
            var tokenResponse = JsonSerializer.Deserialize<NexusTokenResponse>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return tokenResponse;
        }
    }

    /// <summary>
    /// 生成PKCE code verifier
    /// </summary>
    private string GenerateCodeVerifier()
    {
        // 生成32字节随机值，Base64 URL编码
        var bytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
            rng.GetBytes(bytes);

        return Base64UrlEncode(bytes);
    }

    /// <summary>
    /// 生成PKCE code challenge
    /// </summary>
    private string GenerateCodeChallenge(string verifier)
    {
        // SHA256哈希
        using (var sha256 = SHA256.Create())
        {
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(verifier));
            return Base64UrlEncode(hash);
        }
    }

    /// <summary>
    /// Base64 URL编码
    /// </summary>
    private string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    /// <summary>
    /// 从 JWT id_token 解析用户信息
    /// </summary>
    /// <param name="idToken">JWT ID Token</param>
    /// <returns>用户信息</returns>
    public NexusJwtUserInfo ParseUserInfoFromIdToken(string idToken)
    {
        if (string.IsNullOrEmpty(idToken))
        {
            throw new ArgumentException("ID Token 不能为空", nameof(idToken));
        }

        Log.Info("[NexusOAuth] 从 JWT 解析用户信息");

        try
        {
            // JWT 格式: header.payload.signature
            var parts = idToken.Split('.');
            if (parts.Length != 3)
            {
                throw new InvalidOperationException("无效的 JWT 格式");
            }

            // 解析 payload（Base64 URL 编码）
            var payload = parts[1];
            // 添加填充（如果需要）
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            // 将 Base64 URL 转换为 Base64
            payload = payload.Replace('-', '+').Replace('_', '/');

            var payloadBytes = Convert.FromBase64String(payload);
            var json = System.Text.Encoding.UTF8.GetString(payloadBytes);

            Log.Debug($"[NexusOAuth] JWT Payload: {json.Substring(0, Math.Min(200, json.Length))}...");

            var userInfo = JsonSerializer.Deserialize<NexusJwtUserInfo>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (userInfo == null)
            {
                throw new InvalidOperationException("JWT 反序列化失败");
            }

            Log.Info($"[NexusOAuth] ✓ 解析用户信息成功: {userInfo.Name} (Role: {userInfo.MembershipRole})");
            return userInfo;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NexusOAuth] 解析 JWT 失败");
            throw;
        }
    }

    /// <summary>
    /// 获取用户信息（从 JWT id_token）
    /// </summary>
    /// <param name="idToken">JWT ID Token</param>
    /// <returns>用户信息</returns>
    public NexusUserInfo GetUserInfo(string idToken)
    {
        var jwtInfo = ParseUserInfoFromIdToken(idToken);

        // 将 JWT 用户信息转换为 NexusUserInfo
        var isPremium = jwtInfo.MembershipRole?.ToLower() == "premium" ||
                        jwtInfo.MembershipRole?.ToLower() == "supporter";

        // 检查 avatar 字段
        var avatarUrl = jwtInfo.Avatar;
        if (string.IsNullOrEmpty(avatarUrl))
        {
            Log.Info($"[NexusOAuth] JWT 中没有 avatar 字段，将使用默认头像");
            avatarUrl = null;
        }
        else
        {
            Log.Info($"[NexusOAuth] ✓ 获取到头像 URL: {avatarUrl}");
        }

        return new NexusUserInfo
        {
            UserId = long.Parse(jwtInfo.Sub),
            Name = jwtInfo.Name,
            Email = jwtInfo.Email,
            Avatar = avatarUrl,
            IsPremium = isPremium,
            IsSupporter = isPremium,
            ProfileUrl = $"https://nexusmods.com/users/{jwtInfo.Name}"
        };
    }

    /// <summary>
    /// 使用 Refresh Token 刷新 Access Token
    /// </summary>
    /// <param name="refreshToken">刷新令牌</param>
    /// <returns>新的Token响应</returns>
    public async Task<NexusTokenResponse> RefreshAccessTokenAsync(string refreshToken)
    {
        if (string.IsNullOrEmpty(refreshToken))
        {
            throw new ArgumentException("Refresh token 不能为空", nameof(refreshToken));
        }

        Log.Info("[NexusOAuth] 开始刷新 Access Token");

        using (var client = new HttpClient())
        {
            client.DefaultRequestHeaders.Add("User-Agent", "SVL-StardewLauncher/1.0");

            // 注意：Nexus OAuth 使用 vortex_loopback 客户端，不需要 redirect_uri
            var content = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = ClientId,
                ["refresh_token"] = refreshToken
            };

            Log.Debug("[NexusOAuth] 刷新Token请求已发送");

            var response = await client.PostAsync($"{BaseUrl}/token", new FormUrlEncodedContent(content));

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Log.Error($"[NexusOAuth] Token刷新失败: {response.StatusCode} - {errorContent}");
                throw new HttpRequestException($"Token刷新失败: {errorContent}");
            }

            var json = await response.Content.ReadAsStringAsync();
            var tokenResponse = JsonSerializer.Deserialize<NexusTokenResponse>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
            {
                Log.Error("[NexusOAuth] Token刷新响应无效");
                throw new InvalidOperationException("Token刷新响应无效");
            }

            Log.Info($"[NexusOAuth] Token刷新成功，新Token过期时间: {tokenResponse.ExpiresIn}秒");
            return tokenResponse;
        }
    }

    /// <summary>
    /// 生成OAuth结果页面HTML
    /// </summary>
    private string GenerateResultPage(bool success)
    {
        var html = new StringBuilder();
        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine("<html lang=\"zh-CN\">");
        html.AppendLine("<head>");
        html.AppendLine("    <meta charset=\"UTF-8\">");
        html.AppendLine("    <title>认证状态</title>");
        html.AppendLine("    <meta http-equiv=\"refresh\" content=\"3;url=https://www.nexusmods.com/\" />");
        html.AppendLine("</head>");
        html.AppendLine("<body style=\"display: flex; flex-direction: column; height: 100vh; justify-content: center; align-items: center; background-color: #1a1a1a; font-family: 'Segoe UI', sans-serif; color: white;\">");
        html.AppendLine("    <div style=\"text-align: center;\">");

        if (success)
        {
            html.AppendLine("        <h1 style=\"color: #4CAF50;\">✓ 认证成功！</h1>");
            html.AppendLine("        <p style=\"font-size: 1.2em;\">SVL已成功连接到您的Nexus Mods账户</p>");
        }
        else
        {
            html.AppendLine("        <h1 style=\"color: #f44336;\">✕ 认证失败</h1>");
            html.AppendLine("        <p style=\"font-size: 1.2em;\">请检查SVL获取更多信息的提示</p>");
        }

        html.AppendLine("        <p style=\"font-size: 1.1em; margin-top: 20px;\">正在跳转到Nexus Mods...</p>");
        html.AppendLine("    </div>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");

        return html.ToString();
    }

    public void Dispose()
    {
        _httpListener?.Close();
        _httpListener = null;
    }
}
