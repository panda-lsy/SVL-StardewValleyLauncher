using System;
using System.IO;
using System.Linq;
using ICSharpCode.SharpZipLib.Zip;

namespace SVL.Core.Stardew.Mod;

public enum ModInstallSourceKind
{
    Unknown,
    Mod,
    Smapi
}

public static class ModArchiveDetector
{
    public static ModInstallSourceKind DetectSourceKind(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ModInstallSourceKind.Unknown;

        if (Directory.Exists(path))
        {
            if (LooksLikeSmapiDirectory(path))
                return ModInstallSourceKind.Smapi;

            if (Directory.EnumerateFiles(path, "manifest.json", SearchOption.AllDirectories).Any())
                return ModInstallSourceKind.Mod;

            return ModInstallSourceKind.Unknown;
        }

        if (!File.Exists(path) || !path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return ModInstallSourceKind.Unknown;

        try
        {
            using var zipFile = new ZipFile(path);
            foreach (ZipEntry entry in zipFile)
            {
                if (!entry.IsFile)
                    continue;

                var entryName = entry.Name.Replace('\\', '/');
                if (LooksLikeSmapiEntry(entryName))
                    return ModInstallSourceKind.Smapi;

                if (entryName.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
                    return ModInstallSourceKind.Mod;
            }
        }
        catch
        {
            return ModInstallSourceKind.Unknown;
        }

        return ModInstallSourceKind.Unknown;
    }

    public static bool LooksLikeModInstallSource(string path)
    {
        return DetectSourceKind(path) == ModInstallSourceKind.Mod;
    }

    public static bool LooksLikeSmapiInstallerSource(string path)
    {
        return DetectSourceKind(path) == ModInstallSourceKind.Smapi;
    }

    private static bool LooksLikeSmapiDirectory(string path)
    {
        return File.Exists(Path.Combine(path, "install on Windows.bat"))
               || File.Exists(Path.Combine(path, "StardewModdingAPI.exe"))
               || File.Exists(Path.Combine(path, "internal", "windows", "install.dat"));
    }

    private static bool LooksLikeSmapiEntry(string entryName)
    {
        return entryName.EndsWith("install on Windows.bat", StringComparison.OrdinalIgnoreCase)
               || entryName.EndsWith("StardewModdingAPI.exe", StringComparison.OrdinalIgnoreCase)
               || entryName.EndsWith("internal/windows/install.dat", StringComparison.OrdinalIgnoreCase);
    }
}