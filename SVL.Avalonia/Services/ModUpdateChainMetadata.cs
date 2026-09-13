namespace SVL.Avalonia.Services;

/// <summary>
/// Mod 覆盖更新的可恢复链路。
///
/// 目录名并不是 Mod 身份：发布者可能把旧目录 A 改成新目录 BCD，
/// 因此备份记录必须同时保存旧目录和本次归档计划写入的目录名。
/// </summary>
public sealed class ModUpdateChainMetadata
{
    public int SchemaVersion { get; set; } = 1;
    public string OperationId { get; set; } = string.Empty;
    public string OriginalFolderName { get; set; } = string.Empty;
    public string OriginalUniqueId { get; set; } = string.Empty;
    public List<string> ReplacementFolderNames { get; set; } = [];
    public string SourceArchiveName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
