namespace SVL.Avalonia.Services;

/// <summary>
/// 解析版本隔离实例的实际运行时目录。
/// 新布局直接使用 versions/&lt;name&gt;，旧布局使用 versions/&lt;name&gt;/game；
/// 只有在 game 目录确实包含游戏文件时才将其视为旧布局。
/// </summary>
public static class InstanceRuntimePathResolver
{
    private static readonly char[] WindowsInvalidFileNameChars =
        ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    public static string Resolve(string versionRoot)
    {
        if (string.IsNullOrWhiteSpace(versionRoot))
        {
            return string.Empty;
        }

        var legacyRuntimePath = Path.Combine(versionRoot, "game");
        return IsValidGamePath(legacyRuntimePath) ? legacyRuntimePath : versionRoot;
    }

    public static string SanitizeFileNameComponent(string? value, string fallback = "unknown")
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var invalidChars = Path.GetInvalidFileNameChars()
            .Concat(WindowsInvalidFileNameChars)
            .Distinct()
            .ToArray();
        var sanitized = string.Concat(candidate.Select(c => invalidChars.Contains(c) ? '_' : c));
        sanitized = sanitized.Trim().Trim('.');

        if (string.IsNullOrWhiteSpace(sanitized) || IsReservedDeviceName(sanitized))
        {
            return fallback;
        }

        return sanitized;
    }

    private static bool IsValidGamePath(string path)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        var markers = new[]
        {
            "Stardew Valley.dll",
            "Stardew Valley.deps.json",
            "Stardew Valley.exe",
            "StardewValley.exe",
            "StardewValley",
            "Stardew Valley",
            "Stardew Valley.app"
        };

        return markers.Any(marker =>
            File.Exists(Path.Combine(path, marker)) ||
            Directory.Exists(Path.Combine(path, marker)));
    }

    private static bool IsReservedDeviceName(string value)
    {
        var name = value.Split('.', 2)[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" ||
               ((name.StartsWith("COM", StringComparison.Ordinal) ||
                 name.StartsWith("LPT", StringComparison.Ordinal)) &&
                name.Length == 4 && int.TryParse(name[3..], out var number) && number is >= 1 and <= 9);
    }
}
