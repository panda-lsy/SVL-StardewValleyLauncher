namespace SVL.Core.Platform.Abstractions;

public interface IPlatformInfoService
{
    PlatformKind CurrentPlatform { get; }

    bool IsSteamDeck();

    string GetPlatformDisplayName();
}
