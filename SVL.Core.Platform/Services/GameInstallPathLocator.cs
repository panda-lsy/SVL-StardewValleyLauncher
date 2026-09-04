using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SVL.Core.Platform.Abstractions;

public sealed class GameInstallPathLocator : IGameInstallPathLocator
{
    public string? TryLocateSteamStardewPath()
    {
        foreach (var path in GetSteamCandidatePaths())
        {
            if (IsValidGamePath(path))
            {
                return path;
            }
        }

        return null;
    }

    public string? TryLocateGogStardewPath()
    {
        foreach (var path in GetGogCandidatePaths())
        {
            if (IsValidGamePath(path))
            {
                return path;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetSteamCandidatePaths()
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var roots = new[]
            {
                Path.Combine(home, "Library", "Application Support", "Steam"),
                Path.Combine(home, ".steam", "steam")
            };

            foreach (var candidate in EnumerateSteamGameCandidates(roots))
            {
                if (yielded.Add(candidate))
                {
                    yield return candidate;
                }
            }

            yield break;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var roots = new[]
            {
                Path.Combine(home, ".steam", "steam"),
                Path.Combine(home, ".local", "share", "Steam"),
                Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam")
            };

            foreach (var candidate in EnumerateSteamGameCandidates(roots))
            {
                if (yielded.Add(candidate))
                {
                    yield return candidate;
                }
            }

            yield break;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            var roots = new List<string>
            {
                Path.Combine(programFilesX86, "Steam"),
                Path.Combine(programFiles, "Steam")
            };

            foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.IsReady))
            {
                roots.Add(Path.Combine(drive.RootDirectory.FullName, "Steam"));
                roots.Add(Path.Combine(drive.RootDirectory.FullName, "SteamLibrary"));
            }

            foreach (var candidate in EnumerateSteamGameCandidates(roots))
            {
                if (yielded.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static IEnumerable<string> GetGogCandidatePaths()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            yield return Path.Combine(programFilesX86, "GOG Galaxy", "Games", "Stardew Valley");
            yield return Path.Combine(programFiles, "GOG Galaxy", "Games", "Stardew Valley");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "GOG Games", "Stardew Valley");
            yield break;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return "/Applications/Stardew Valley.app/Contents/MacOS";
            yield return Path.Combine(home, "Applications", "Stardew Valley.app", "Contents", "MacOS");
            yield return Path.Combine(home, "GOG Games", "Stardew Valley");
            yield break;
        }

        var linuxHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(linuxHome, "GOG Games", "Stardew Valley");
        yield return Path.Combine(linuxHome, "Games", "Stardew Valley");
    }

    private static IEnumerable<string> EnumerateSteamGameCandidates(IEnumerable<string> steamRoots)
    {
        foreach (var root in steamRoots.Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)))
        {
            foreach (var steamAppsPath in GetSteamAppsDirectories(root))
            {
                var commonPath = Path.Combine(steamAppsPath, "common", "Stardew Valley");
                yield return commonPath;
                yield return Path.Combine(commonPath, "Stardew Valley.app", "Contents", "MacOS");
                yield return Path.Combine(commonPath, "Contents");

                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && Directory.Exists(commonPath))
                {
                    foreach (var appBundle in Directory.EnumerateDirectories(commonPath, "*.app", SearchOption.TopDirectoryOnly))
                    {
                        yield return Path.Combine(appBundle, "Contents", "MacOS");
                    }
                }
            }
        }
    }

    private static IEnumerable<string> GetSteamAppsDirectories(string steamRoot)
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var defaultSteamApps = Path.Combine(steamRoot, "steamapps");
        if (Directory.Exists(defaultSteamApps) && yielded.Add(defaultSteamApps))
        {
            yield return defaultSteamApps;
        }

        var libraryFoldersVdf = Path.Combine(defaultSteamApps, "libraryfolders.vdf");
        if (!File.Exists(libraryFoldersVdf))
        {
            if (Directory.Exists(steamRoot) && Path.GetFileName(steamRoot).Equals("steamapps", StringComparison.OrdinalIgnoreCase) && yielded.Add(steamRoot))
            {
                yield return steamRoot;
            }

            yield break;
        }

        string content;
        try
        {
            content = File.ReadAllText(libraryFoldersVdf);
        }
        catch
        {
            yield break;
        }

        var matches = Regex.Matches(content, "\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
        foreach (Match match in matches)
        {
            if (!match.Success)
            {
                continue;
            }

            var pathRaw = match.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(pathRaw))
            {
                continue;
            }

            var normalizedRoot = pathRaw.Replace("\\\\", "\\");
            var appsPath = Path.Combine(normalizedRoot, "steamapps");
            if (Directory.Exists(appsPath) && yielded.Add(appsPath))
            {
                yield return appsPath;
            }
        }
    }

    private static bool IsValidGamePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return false;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            if (path.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                var appRootCandidates = new[]
                {
                    Path.Combine(path, "Contents", "MacOS", "StardewValley"),
                    Path.Combine(path, "Contents", "MacOS", "Stardew Valley")
                };

                if (appRootCandidates.Any(File.Exists))
                {
                    return true;
                }
            }

            if (Path.GetFileName(path).Equals("Contents", StringComparison.OrdinalIgnoreCase))
            {
                var contentsCandidates = new[]
                {
                    Path.Combine(path, "MacOS", "StardewValley"),
                    Path.Combine(path, "MacOS", "Stardew Valley")
                };

                if (contentsCandidates.Any(File.Exists))
                {
                    return true;
                }
            }

            var appExecutableCandidates = new[]
            {
                Path.Combine(path, "Stardew Valley.app", "Contents", "MacOS", "StardewValley"),
                Path.Combine(path, "Stardew Valley.app", "Contents", "MacOS", "Stardew Valley")
            };

            if (appExecutableCandidates.Any(File.Exists))
            {
                return true;
            }
        }

        var markers = new[]
        {
            "Stardew Valley.dll",
            "Stardew Valley.deps.json",
            "Stardew Valley.exe",
            "Stardew Valley",
            "StardewValley.exe",
            "StardewValley",
            "StardewModdingAPI.exe",
            "StardewModdingAPI"
        };

        return markers.Any(marker => File.Exists(Path.Combine(path, marker)));
    }
}
