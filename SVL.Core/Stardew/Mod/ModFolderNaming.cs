using System;
using System.IO;

namespace SVL.Core.Stardew.Mod;

/// <summary>
/// 模组文件夹命名规则工具。
/// 兼容旧的 .disabled 后缀，并统一使用以点开头的禁用方式。
/// </summary>
public static class ModFolderNaming
{
    private const string LegacyDisabledSuffix = ".disabled";

    public static bool IsLegacyDisabledFolderName(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.EndsWith(LegacyDisabledSuffix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDisabledFolderName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var folderName = GetLeafFolderName(value);
        return folderName.StartsWith(".", StringComparison.Ordinal)
            || folderName.EndsWith(LegacyDisabledSuffix, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeFolderName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var folderName = GetLeafFolderName(value);

        if (folderName.EndsWith(LegacyDisabledSuffix, StringComparison.OrdinalIgnoreCase))
        {
            folderName = folderName.Substring(0, folderName.Length - LegacyDisabledSuffix.Length);
        }

        return folderName.Trim('.');
    }

    public static string GetDisabledFolderName(string? value, bool useTrailingDotFallback = false)
    {
        var baseName = NormalizeFolderName(value);
        if (string.IsNullOrWhiteSpace(baseName))
            return ".";

        return useTrailingDotFallback
            ? $".{baseName}."
            : $".{baseName}";
    }

    public static string GetEnabledFolderName(string? value)
    {
        return NormalizeFolderName(value);
    }

    public static string GetDisabledFolderPath(string folderPath, bool useTrailingDotFallback = false)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return folderPath;

        var directory = Path.GetDirectoryName(folderPath);
        var disabledName = GetDisabledFolderName(Path.GetFileName(folderPath), useTrailingDotFallback);
        return string.IsNullOrWhiteSpace(directory)
            ? disabledName
            : Path.Combine(directory, disabledName);
    }

    public static string GetEnabledFolderPath(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return folderPath;

        var directory = Path.GetDirectoryName(folderPath);
        var enabledName = GetEnabledFolderName(Path.GetFileName(folderPath));
        return string.IsNullOrWhiteSpace(directory)
            ? enabledName
            : Path.Combine(directory, enabledName);
    }

    private static string GetLeafFolderName(string value)
    {
        var trimmed = value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFileName(trimmed) ?? string.Empty;
    }
}