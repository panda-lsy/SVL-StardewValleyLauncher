namespace SVL.Avalonia.Models;

public sealed class NexusLoginResult
{
    public string ApiKey { get; init; } = string.Empty;

    public string OAuthAccessToken { get; init; } = string.Empty;

    public string OAuthRefreshToken { get; init; } = string.Empty;

    public string OAuthIdToken { get; init; } = string.Empty;

    public bool IsOAuthLogin { get; init; }

    public string UserName { get; init; } = string.Empty;

    public string MembershipType { get; init; } = "Free";

    public int UserId { get; init; }
}