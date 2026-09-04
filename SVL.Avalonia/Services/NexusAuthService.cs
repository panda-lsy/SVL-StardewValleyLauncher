using System.Net.Http;
using System.Text.Json;

namespace SVL.Avalonia.Services;

public sealed class NexusAuthService
{
    private static readonly Uri ValidateEndpoint = new("https://api.nexusmods.com/v1/users/validate.json");

    public async Task<NexusAuthResult> ValidateApiKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return NexusAuthResult.Failed("API Key 不能为空");
        }

        using var httpClient = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, ValidateEndpoint);
        request.Headers.Add("apikey", apiKey.Trim());
        request.Headers.Add("application-name", "SVL.Avalonia");
        request.Headers.Add("accept", "application/json");

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            return NexusAuthResult.Failed($"网络请求失败: {ex.Message}");
        }

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;
            return statusCode == 401
                ? NexusAuthResult.Failed("API Key 无效或已过期")
                : NexusAuthResult.Failed($"鉴权失败: HTTP {statusCode}");
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;

            var userName = root.TryGetProperty("name", out var nameValue)
                ? nameValue.GetString() ?? "Unknown"
                : "Unknown";

            var isPremium = root.TryGetProperty("is_premium", out var premiumValue) &&
                            premiumValue.ValueKind == JsonValueKind.True;

            var userId = root.TryGetProperty("user_id", out var userIdValue) &&
                         userIdValue.TryGetInt32(out var parsedUserId)
                ? parsedUserId
                : 0;

            return NexusAuthResult.Success(userName, isPremium ? "Premium" : "Free", userId);
        }
        catch (Exception ex)
        {
            return NexusAuthResult.Failed($"解析鉴权响应失败: {ex.Message}");
        }
    }
}

public sealed class NexusAuthResult
{
    public bool IsSuccess { get; init; }

    public string Message { get; init; } = string.Empty;

    public string UserName { get; init; } = string.Empty;

    public string MembershipType { get; init; } = "Free";

    public int UserId { get; init; }

    public static NexusAuthResult Success(string userName, string membershipType, int userId)
    {
        return new NexusAuthResult
        {
            IsSuccess = true,
            Message = "验证成功",
            UserName = userName,
            MembershipType = membershipType,
            UserId = userId
        };
    }

    public static NexusAuthResult Failed(string message)
    {
        return new NexusAuthResult
        {
            IsSuccess = false,
            Message = message
        };
    }
}