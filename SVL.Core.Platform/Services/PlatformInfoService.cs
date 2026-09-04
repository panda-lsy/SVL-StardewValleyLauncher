using System.Runtime.InteropServices;

namespace SVL.Core.Platform.Abstractions;

public sealed class PlatformInfoService : IPlatformInfoService
{
    public PlatformKind CurrentPlatform
    {
        get
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return PlatformKind.Windows;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return PlatformKind.MacOS;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return PlatformKind.Linux;
            }

            return PlatformKind.Unknown;
        }
    }

    public bool IsSteamDeck()
    {
        if (CurrentPlatform != PlatformKind.Linux)
        {
            return false;
        }

        var env = Environment.GetEnvironmentVariable("SteamDeck");
        if (!string.IsNullOrWhiteSpace(env) && env == "1")
        {
            return true;
        }

        try
        {
            var markerPath = "/etc/os-release";
            if (!File.Exists(markerPath))
            {
                return false;
            }

            var content = File.ReadAllText(markerPath);
            return content.Contains("steamdeck", StringComparison.OrdinalIgnoreCase)
                || content.Contains("SteamOS", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public string GetPlatformDisplayName()
    {
        return CurrentPlatform switch
        {
            PlatformKind.Windows => "Windows",
            PlatformKind.MacOS => "macOS",
            PlatformKind.Linux when IsSteamDeck() => "Steam Deck (SteamOS)",
            PlatformKind.Linux => "Linux",
            _ => "Unknown"
        };
    }
}
