using System;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SVL.Core.Logging;

namespace SVL.Core.Download.NexusMods;

/// <summary>
/// Nexus Mods WebSocket SSO 认证管理器
/// 参考 Mod Organizer 2 实现，使用 API Key 方式
/// </summary>
public class NexusSsoManager : IDisposable
{
    // Nexus SSO 公开端点
    private const string NexusSsoUrl = "wss://sso.nexusmods.com";
    private const string NexusSsoPage = "https://www.nexusmods.com/sso?id={0}&application=svl-launcher";

    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private bool _disposed = false;

    /// <summary>
    /// API Key 接收事件
    /// </summary>
    public event EventHandler<string>? ApiKeyReceived;

    /// <summary>
    /// 状态变更事件
    /// </summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>
    /// 启动 SSO 登录流程
    /// </summary>
    /// <param name="openBrowserCallback">打开浏览器的回调函数</param>
    /// <returns>API Key</returns>
    public async Task<string> StartSsoLoginAsync(Action<string> openBrowserCallback)
    {
        Log.Info("[NexusSSO] 开始 SSO 登录流程");

        // 步骤 1: 生成随机 GUID（会话 ID）
        var guid = Guid.NewGuid().ToString();
        Log.Info($"[NexusSSO] 会话 ID: {guid}");

        OnStatusChanged("连接到 Nexus SSO 服务器...");

        _webSocket = new ClientWebSocket();
        _cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        try
        {
            // 步骤 2: 连接到 Nexus WebSocket 服务器
            Log.Info("[NexusSSO] 连接到 WebSocket 服务器");
            await _webSocket.ConnectAsync(new Uri(NexusSsoUrl), _cts.Token);
            Log.Info("[NexusSSO] WebSocket 连接成功");

            // 步骤 3: 发送握手消息
            var handshakeMessage = new
            {
                id = guid,
                @protocol = 2
            };

            var handshakeJson = JsonSerializer.Serialize(handshakeMessage);
            var handshakeBuffer = Encoding.UTF8.GetBytes(handshakeJson);

            Log.Debug($"[NexusSSO] 发送握手消息: {handshakeJson}");

            await _webSocket.SendAsync(
                new ArraySegment<byte>(handshakeBuffer),
                WebSocketMessageType.Text,
                true,
                _cts.Token);

            OnStatusChanged("等待 Nexus 响应...");

            // 步骤 4: 接收 connection_token（第一次响应）
            var response1 = await ReceiveMessageAsync();
            var response1Doc = JsonDocument.Parse(response1);

            Log.Debug($"[NexusSSO] 第一次响应: {response1}");

            if (!response1Doc.RootElement.GetProperty("success").GetBoolean())
            {
                var error = response1Doc.RootElement.TryGetProperty("error", out var err)
                    ? err.GetString()
                    : "Unknown error";
                throw new Exception($"SSO 初始化失败: {error}");
            }

            // 步骤 5: 打开浏览器让用户登录
            var browserUrl = string.Format(NexusSsoPage, guid);
            Log.Info($"[NexusSSO] 打开浏览器: {browserUrl}");

            OnStatusChanged("正在打开浏览器...");
            openBrowserCallback(browserUrl);
            OnStatusChanged("请在浏览器中完成登录并授权...");

            // 步骤 6: 等待 API Key（第二次响应）
            OnStatusChanged("等待登录...");
            var response2 = await ReceiveMessageAsync();
            var response2Doc = JsonDocument.Parse(response2);

            Log.Debug($"[NexusSSO] 第二次响应: {response2}");

            if (!response2Doc.RootElement.GetProperty("success").GetBoolean())
            {
                // 提取错误信息
                var error = response2Doc.RootElement.TryGetProperty("error", out var err)
                    ? err.GetString()
                    : "Unknown error";
                throw new Exception($"SSO 登录失败: {error}");
            }

            // 步骤 7: 获取 API Key
            var apiKey = response2Doc.RootElement
                .GetProperty("data")
                .GetProperty("api_key")
                .GetString();

            Log.Info("[NexusSSO] ✓ 成功获取 API Key");

            OnStatusChanged("登录成功！");
            OnApiKeyReceived(apiKey);

            return apiKey;
        }
        catch (OperationCanceledException)
        {
            Log.Error("[NexusSSO] 登录超时");
            OnStatusChanged("登录超时，请重试");
            throw new TimeoutException("登录超时（120秒），请重试");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NexusSSO] 登录失败");
            OnStatusChanged($"登录失败: {ex.Message}");
            throw;
        }
        finally
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Closing",
                    CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// 接收 WebSocket 消息
    /// </summary>
    private async Task<string> ReceiveMessageAsync()
    {
        if (_webSocket == null || _cts == null)
            throw new InvalidOperationException("WebSocket 未初始化");

        var buffer = new byte[4096];
        var sb = new StringBuilder();

        while (_webSocket.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
        {
            var result = await _webSocket.ReceiveAsync(
                new ArraySegment<byte>(buffer),
                _cts.Token);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Closing",
                    CancellationToken.None);
                break;
            }

            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

            if (result.EndOfMessage)
            {
                return sb.ToString();
            }
        }

        throw new Exception("WebSocket 连接意外关闭");
    }

    /// <summary>
    /// 触发 API Key 接收事件
    /// </summary>
    protected virtual void OnApiKeyReceived(string apiKey)
    {
        ApiKeyReceived?.Invoke(this, apiKey);
    }

    /// <summary>
    /// 触发状态变更事件
    /// </summary>
    protected virtual void OnStatusChanged(string status)
    {
        StatusChanged?.Invoke(this, status);
        Log.Info($"[NexusSSO] 状态: {status}");
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _webSocket?.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Nexus 配置（用于存储 API Key）
/// </summary>
public class NexusConfig
{
    public string ApiKey { get; set; } = string.Empty;
    public DateTime SavedAt { get; set; } = DateTime.UtcNow;
}
