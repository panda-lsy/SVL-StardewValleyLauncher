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
            // 剥离 ~~ 后缀元数据（CurseForge 下载选项可能包含 ~~channel=...;gamever=... 等元数据）
            var tildeIndex = option.IndexOf("~~", StringComparison.Ordinal);
            if (tildeIndex > 0)
            {
                option = option[..tildeIndex].Trim();
            }

            var pipeIndex = option.IndexOf('|');
            if (pipeIndex > 0)
            {
                var leftPart = option[..pipeIndex].Trim();
                if (leftPart.Length > 0)
                {
                    return leftPart;
                }
            }

            if (option.StartsWith("File ", StringComparison.OrdinalIgnoreCase))
            {
                var colonIndex = option.IndexOf(':');
                if (colonIndex > 0 && colonIndex < option.Length - 1)
                {
                    var filePart = option[(colonIndex + 1)..].Trim();
                    if (filePart.Length > 0)
                    {
                        return filePart;
                    }
                }
            }

            if (option.Length > 0)
            {
                return option;
            }
        }

        if (string.IsNullOrWhiteSpace(ResourceName))
        {
            return "download.zip";
        }

        return ResourceName.Trim();
    }
}
