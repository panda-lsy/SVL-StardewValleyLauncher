using SVL.Avalonia.Services;
using System.Text.RegularExpressions;

namespace SVL.Avalonia.Models;

public enum ExternalDownloadAction
{
    Install,
    SaveAs
}

public sealed class ExternalDownloadRequest
{
    public ExternalDownloadAction Action { get; init; } = ExternalDownloadAction.Install;

    public string ResourceName { get; init; } = string.Empty;

    public string ResourceSource { get; init; } = string.Empty;

    public string ResourceId { get; init; } = string.Empty;

    public string SourceToken { get; init; } = string.Empty;

    public string SourcePageUrl { get; init; } = string.Empty;

    public bool IsSmapiResource { get; init; }

    public string SelectedDownloadOption { get; init; } = string.Empty;

    /// <summary>是否为 Nexus Collection（整合包）。用于区分普通 Mod 安装和 Collection 安装流程。</summary>
    public bool IsCollection { get; init; }

    /// <summary>是否为 Curseforge/SVL 整合包（非 Nexus Collection）。用于走 Modpack 安装流程（manifest.json）。</summary>
    public bool IsModpack { get; init; }

    /// <summary>
    /// 整合包详情页的主图标 URL。CurseForge 安装时优先缓存该图标，
    /// 下载包内未提供图标或网络获取失败时再回退到包内/现有整合包图标。
    /// </summary>
    public string ModpackIconUrl { get; init; } = string.Empty;

    /// <summary>Nexus Collection 的 slug（仅 IsCollection 为 true 时有效）。</summary>
    public string CollectionSlug { get; init; } = string.Empty;

    /// <summary>Collection 的 revision 号（-1 表示最新）。</summary>
    public int CollectionRevision { get; init; } = -1;

    /// <summary>用户选定的 Base 游戏路径（Collection 安装时由路径选择对话框填充）。</summary>
    public string TargetGamePath { get; init; } = string.Empty;

    /// <summary>用户输入的实例/版本名称（Collection 安装时由版本名输入对话框填充）。</summary>
    public string TargetInstanceName { get; init; } = string.Empty;

    public string ToTaskDisplayName()
    {
        var actionPrefix = Action == ExternalDownloadAction.SaveAs ? "[另存为]" : "[安装]";

        if (string.IsNullOrWhiteSpace(SelectedDownloadOption))
        {
            return string.IsNullOrWhiteSpace(ResourceSource)
                ? $"{actionPrefix} {ResourceName}"
                : $"{actionPrefix} {ResourceName} [{ResourceSource}]";
        }

        return string.IsNullOrWhiteSpace(ResourceSource)
            ? $"{actionPrefix} {ResourceName} | {SelectedDownloadOption}"
            : $"{actionPrefix} {ResourceName} [{ResourceSource}] | {SelectedDownloadOption}";
    }

    public string ResolveSuggestedFileName()
    {
        var option = SelectedDownloadOption?.Trim() ?? string.Empty;
        if (option.Length > 0)
        {
            // 某些来源把“File 7448774_ 文件名.zip”整体 URL 编码后再传入，
            // 先解码非 URL 选项，才能正确去掉 Nexus 的 FileID 前缀。
            if (!IsHttpUrl(option))
            {
                option = Uri.UnescapeDataString(option);
            }

            // 剥离 ~~ 后缀元数据（CurseForge 下载选项可能包含 ~~channel=...;gamever=... 等元数据）
            var tildeIndex = option.IndexOf("~~", StringComparison.Ordinal);
            if (tildeIndex > 0)
            {
                option = option[..tildeIndex].Trim();
            }

            var pipeIndex = option.IndexOf('|');
            if (pipeIndex > 0)
            {
                var leftPart = DownloadOptionIdentityParser.StripGeneratedFilePrefix(option[..pipeIndex]);
                if (leftPart.Length > 0 && !IsHttpUrl(leftPart))
                {
                    return CollapseRepeatedVersionSuffix(Uri.UnescapeDataString(leftPart));
                }
            }

            option = DownloadOptionIdentityParser.StripGeneratedFilePrefix(option);
            if (option.Length > 0)
            {
                if (TryGetUrlFileName(option, out var urlFileName))
                {
                    return urlFileName;
                }

                // 只有 URL、且 URL 没有可读文件名时，回退到 ResourceName，
                // 不要把整条 URL 交给文件选择器作为默认文件名。
                if (IsHttpUrl(option))
                {
                    return string.IsNullOrWhiteSpace(ResourceName)
                        ? "download.zip"
                        : ResourceName.Trim();
                }

                return CollapseRepeatedVersionSuffix(option);
            }
        }

        if (string.IsNullOrWhiteSpace(ResourceName))
        {
            return "download.zip";
        }

        return ResourceName.Trim();
    }

    private static bool IsHttpUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static bool TryGetUrlFileName(string value, out string fileName)
    {
        fileName = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        var pathFileName = Uri.UnescapeDataString(Path.GetFileName(uri.LocalPath));
        if (string.IsNullOrWhiteSpace(pathFileName) ||
            pathFileName is "." or ".." ||
            pathFileName.Equals("download", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        fileName = CollapseRepeatedVersionSuffix(pathFileName);
        return true;
    }

    private static string CollapseRepeatedVersionSuffix(string value)
    {
        var extension = Path.GetExtension(value);
        if (string.IsNullOrWhiteSpace(extension) ||
            !extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".7z", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".rar", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".cfmodpack", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var stem = value[..^extension.Length];
        var match = Regex.Match(
            stem,
            @"^(?<prefix>.+?)\s+(?<version>\d+(?:\.\d+)+)\s+\k<version>$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        return match.Success
            ? $"{match.Groups["prefix"].Value.Trim()} {match.Groups["version"].Value}{extension}"
            : value;
    }
}
