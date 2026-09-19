using System.Text;
using SVL.Core.Platform.Abstractions;

namespace SVL.Core.Platform.Services;

/// <summary>使用 freedesktop.org desktop entry 和 mimeapps.list 注册当前用户的 nxm:// 处理器。</summary>
internal static class LinuxNxmProtocolRegistration
{
    internal const string DesktopFileId = "svl-avalonia-nxm.desktop";
    private const string SchemeMimeType = "x-scheme-handler/nxm";
    private const string SchemeMimeEntry = SchemeMimeType + "=" + DesktopFileId + ";";

    internal static string GetDataHome()
        => GetXdgHome("XDG_DATA_HOME", ".local/share");

    internal static string GetConfigHome()
        => GetXdgHome("XDG_CONFIG_HOME", ".config");

    internal static string? GetDesktopEnvironmentName()
        => NormalizeDesktopEnvironmentName(Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"));

    internal static IReadOnlyList<string> GetDesktopEnvironmentNames()
        => NormalizeDesktopEnvironmentNames(Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"));

    internal static string? NormalizeDesktopEnvironmentName(string? currentDesktopValue)
        => NormalizeDesktopEnvironmentNames(currentDesktopValue).FirstOrDefault();

    internal static IReadOnlyList<string> NormalizeDesktopEnvironmentNames(string? currentDesktopValue)
    {
        if (string.IsNullOrWhiteSpace(currentDesktopValue))
        {
            return Array.Empty<string>();
        }

        return currentDesktopValue
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsSafeDesktopEnvironmentName)
            .Select(name => name.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    internal static NxmProtocolRegistrationResult GetStatus(
        string dataHome,
        string configHome,
        string? desktopEnvironmentName)
        => GetStatusForDesktops(
            dataHome,
            configHome,
            desktopEnvironmentName is null ? Array.Empty<string>() : [desktopEnvironmentName]);

    internal static NxmProtocolRegistrationResult GetStatusForDesktops(
        string dataHome,
        string configHome,
        IReadOnlyList<string> desktopEnvironmentNames)
    {
        try
        {
            var desktopPath = Path.Combine(dataHome, "applications", DesktopFileId);
            var mimeAppsPath = Path.Combine(configHome, "mimeapps.list");
            var desktopIsValid = File.Exists(desktopPath) &&
                                 File.ReadAllLines(desktopPath).Any(line =>
                                     line.Trim().Equals($"MimeType={SchemeMimeType};", StringComparison.Ordinal));
            var desktopSpecificMimeAppsPaths = GetDesktopSpecificMimeAppsPaths(configHome, desktopEnvironmentNames);
            var registeredDefault = ReadEffectiveDefaultHandler(
                desktopSpecificMimeAppsPaths,
                mimeAppsPath,
                dataHome);
            var isDefault = string.Equals(registeredDefault, DesktopFileId, StringComparison.Ordinal);
            var registered = desktopIsValid && isDefault;

            return new NxmProtocolRegistrationResult
            {
                IsSuccess = true,
                IsSupported = true,
                IsRegistered = registered,
                Message = registered ? "NXM 协议已注册" : "NXM 协议未注册"
            };
        }
        catch (Exception ex)
        {
            return new NxmProtocolRegistrationResult
            {
                IsSuccess = false,
                IsSupported = true,
                IsRegistered = false,
                Message = $"读取 NXM 协议状态失败: {ex.Message}"
            };
        }
    }

    internal static NxmProtocolRegistrationResult TryRegister(
        string launcherExecutablePath,
        string dataHome,
        string configHome,
        string? desktopEnvironmentName)
        => TryRegisterForDesktops(
            launcherExecutablePath,
            dataHome,
            configHome,
            desktopEnvironmentName is null ? Array.Empty<string>() : [desktopEnvironmentName]);

    internal static NxmProtocolRegistrationResult TryRegisterForDesktops(
        string launcherExecutablePath,
        string dataHome,
        string configHome,
        IReadOnlyList<string> desktopEnvironmentNames)
    {
        if (string.IsNullOrWhiteSpace(launcherExecutablePath) || !File.Exists(launcherExecutablePath))
        {
            return new NxmProtocolRegistrationResult
            {
                IsSuccess = false,
                IsSupported = true,
                IsRegistered = false,
                Message = "未找到启动器可执行文件，无法注册 NXM 协议"
            };
        }

        try
        {
            var executable = Path.GetFullPath(launcherExecutablePath);
            var desktopPath = Path.Combine(dataHome, "applications", DesktopFileId);
            var mimeAppsPath = Path.Combine(configHome, "mimeapps.list");

            WriteUtf8Atomically(desktopPath, BuildDesktopEntry(executable));
            var existingMimeApps = File.Exists(mimeAppsPath) ? File.ReadAllText(mimeAppsPath) : string.Empty;
            WriteUtf8Atomically(mimeAppsPath, UpsertDefaultHandler(existingMimeApps));

            foreach (var desktopSpecificMimeAppsPath in GetDesktopSpecificMimeAppsPaths(configHome, desktopEnvironmentNames))
            {
                var existingDesktopMimeApps = File.Exists(desktopSpecificMimeAppsPath)
                    ? File.ReadAllText(desktopSpecificMimeAppsPath)
                    : string.Empty;
                WriteUtf8Atomically(
                    desktopSpecificMimeAppsPath,
                    UpsertDefaultHandler(existingDesktopMimeApps));
            }

            return GetStatusForDesktops(dataHome, configHome, desktopEnvironmentNames);
        }
        catch (Exception ex)
        {
            return new NxmProtocolRegistrationResult
            {
                IsSuccess = false,
                IsSupported = true,
                IsRegistered = false,
                Message = $"NXM 协议注册失败: {ex.Message}"
            };
        }
    }

    internal static string BuildDesktopEntry(string executablePath)
    {
        if (executablePath.Contains('\r') || executablePath.Contains('\n'))
        {
            throw new ArgumentException("启动器路径不能包含换行符。", nameof(executablePath));
        }

        return string.Join('\n',
            "[Desktop Entry]",
            "Type=Application",
            "Name=SVL",
            "Comment=Handle Nexus Mods download links",
            "NoDisplay=true",
            "Terminal=false",
            $"Exec={QuoteDesktopArgument(executablePath)} %u",
            $"MimeType={SchemeMimeType};",
            string.Empty);
    }

    internal static string UpsertDefaultHandler(string contents)
    {
        var lines = contents.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var sectionStart = lines.FindIndex(line =>
            line.Trim().Equals("[Default Applications]", StringComparison.OrdinalIgnoreCase));
        if (sectionStart < 0)
        {
            if (lines.Count > 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add("[Default Applications]");
            lines.Add(SchemeMimeEntry);
        }
        else
        {
            var sectionEnd = sectionStart + 1 < lines.Count
                ? lines.FindIndex(sectionStart + 1, line => line.TrimStart().StartsWith("[", StringComparison.Ordinal))
                : -1;
            if (sectionEnd < 0)
            {
                sectionEnd = lines.Count;
            }

            var entryIndexes = Enumerable.Range(sectionStart + 1, sectionEnd - sectionStart - 1)
                .Where(index => IsSchemeMimeAssignment(lines[index]))
                .ToArray();
            if (entryIndexes.Length == 0)
            {
                lines.Insert(sectionEnd, SchemeMimeEntry);
            }
            else
            {
                lines[entryIndexes[0]] = SchemeMimeEntry;
                foreach (var duplicateIndex in entryIndexes.Skip(1).Reverse())
                {
                    lines.RemoveAt(duplicateIndex);
                }
            }
        }

        return string.Join('\n', lines) + "\n";
    }

    private static string? ReadEffectiveDefaultHandler(
        IEnumerable<string> desktopSpecificMimeAppsPaths,
        string mimeAppsPath,
        string dataHome)
    {
        // XDG_CURRENT_DESKTOP is an ordered, colon-separated list. Try each
        // desktop-specific override in order before the generic user override.
        foreach (var desktopSpecificMimeAppsPath in desktopSpecificMimeAppsPaths)
        {
            if (TryReadDefaultHandlers(desktopSpecificMimeAppsPath, out var desktopSpecificHandlers))
            {
                var effectiveHandler = desktopSpecificHandlers.FirstOrDefault(
                    handler => IsDesktopEntryAssociatedWithNxm(handler, dataHome));
                if (effectiveHandler is not null)
                {
                    return effectiveHandler;
                }
            }
        }

        if (TryReadDefaultHandlers(mimeAppsPath, out var defaultHandlers))
        {
            return defaultHandlers.FirstOrDefault(handler => IsDesktopEntryAssociatedWithNxm(handler, dataHome));
        }

        return null;
    }

    private static bool TryReadDefaultHandlers(string mimeAppsPath, out IReadOnlyList<string> handlers)
    {
        handlers = Array.Empty<string>();
        if (!File.Exists(mimeAppsPath))
        {
            return false;
        }

        var inDefaultApplications = false;
        foreach (var rawLine in File.ReadLines(mimeAppsPath))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith(']'))
            {
                inDefaultApplications = line.Equals("[Default Applications]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inDefaultApplications || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0 || !line[..separator].Trim().Equals(SchemeMimeType, StringComparison.Ordinal))
            {
                continue;
            }

            handlers = line[(separator + 1)..]
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();
            return true;
        }

        return false;
    }

    private static bool IsDesktopEntryAssociatedWithNxm(string desktopFileId, string dataHome)
    {
        if (string.IsNullOrWhiteSpace(desktopFileId) ||
            desktopFileId is "." or ".." ||
            desktopFileId.Contains('/') ||
            desktopFileId.Contains('\\') ||
            desktopFileId.Any(char.IsControl))
        {
            return false;
        }

        foreach (var applicationsDirectory in GetApplicationsDirectories(dataHome))
        {
            var desktopEntryPath = Path.Combine(applicationsDirectory, desktopFileId);
            try
            {
                if (File.Exists(desktopEntryPath) && DesktopEntryDeclaresNxm(File.ReadLines(desktopEntryPath)))
                {
                    return true;
                }
            }
            catch (IOException)
            {
                // Ignore an unreadable candidate and continue with lower-precedence data dirs.
            }
            catch (UnauthorizedAccessException)
            {
                // Ignore an unreadable candidate and continue with lower-precedence data dirs.
            }
        }

        return false;
    }

    private static bool DesktopEntryDeclaresNxm(IEnumerable<string> lines)
    {
        var inDesktopEntryGroup = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inDesktopEntryGroup = line.Equals("[Desktop Entry]", StringComparison.Ordinal);
                continue;
            }

            if (!inDesktopEntryGroup || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0 || !line[..separator].Equals("MimeType", StringComparison.Ordinal))
            {
                continue;
            }

            return line[(separator + 1)..]
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(SchemeMimeType, StringComparer.Ordinal);
        }

        return false;
    }

    private static IEnumerable<string> GetApplicationsDirectories(string dataHome)
    {
        yield return Path.Combine(dataHome, "applications");

        var configuredDataDirectories = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        var dataDirectories = string.IsNullOrWhiteSpace(configuredDataDirectories)
            ? new[] { "/usr/local/share", "/usr/share" }
            : configuredDataDirectories.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var dataDirectory in dataDirectories)
        {
            if (Path.IsPathFullyQualified(dataDirectory))
            {
                yield return Path.Combine(dataDirectory, "applications");
            }
        }
    }

    private static IEnumerable<string> GetDesktopSpecificMimeAppsPaths(
        string configHome,
        IEnumerable<string> desktopEnvironmentNames)
        => desktopEnvironmentNames
            .Where(IsSafeDesktopEnvironmentName)
            .Select(name => Path.Combine(configHome, $"{name.ToLowerInvariant()}-mimeapps.list"))
            .Distinct(StringComparer.Ordinal);

    private static bool IsSafeDesktopEnvironmentName(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           value.All(character =>
               char.IsAsciiLetterOrDigit(character) && character < 128 || character is '-' or '_');

    private static bool IsSchemeMimeAssignment(string line)
    {
        var separator = line.IndexOf('=');
        return separator > 0 && line[..separator].Trim().Equals(SchemeMimeType, StringComparison.Ordinal);
    }

    private static string QuoteDesktopArgument(string argument)
    {
        var escaped = argument.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("%", "%%", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    private static string GetXdgHome(string variableName, string fallbackSuffix)
    {
        var configured = Environment.GetEnvironmentVariable(variableName);
        if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured))
        {
            return configured;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            throw new InvalidOperationException("无法确定当前用户的配置目录。");
        }

        return Path.Combine(home, fallbackSuffix.Replace('/', Path.DirectorySeparatorChar));
    }

    private static void WriteUtf8Atomically(string targetPath, string contents)
    {
        var fullPath = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(fullPath)
                        ?? throw new InvalidOperationException("无法确定协议配置文件目录。");
        Directory.CreateDirectory(directory);

        var temporaryPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporaryPath, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
