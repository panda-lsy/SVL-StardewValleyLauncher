namespace SVL.Core.Platform.Abstractions;

public interface IGameInstallPathLocator
{
    string? TryLocateSteamStardewPath();

    string? TryLocateGogStardewPath();
}
