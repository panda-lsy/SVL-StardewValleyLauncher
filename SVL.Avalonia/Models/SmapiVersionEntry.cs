using System;

namespace SVL.Avalonia.Models;

public sealed class SmapiVersionEntry
{
    public string Version { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string DownloadUrl { get; set; } = string.Empty;

    public DateTime PublishedDate { get; set; }

    public bool IsPrerelease { get; set; }

    /// <summary>NexusMods 文件 ID（仅 NexusMods 来源有值，用于 NXM 回调匹配）。其他来源为 null。</summary>
    public long? FileId { get; set; }

    /// <summary>用户选择的目标安装路径（由 SmapiVersionPickerDialog 路径下拉框设置）。</summary>
    public string TargetPath { get; set; } = string.Empty;
}
