using SVL.Core.Platform.Modpack;

namespace SVL.Avalonia.Models;

/// <summary>
/// Modpack 拖放导入对话框的结构化返回结果。
/// 由 DialogService.ShowModpackDropDialogAsync 在用户点击"导入"时构造。
/// </summary>
public sealed class ModpackDropDialogResult
{
    /// <summary>整合包类型检测结果（含类型、元数据、临时解压路径）。</summary>
    public required ModpackDetectionResult Detection { get; init; }

    /// <summary>用户输入的版本实例名称（用于版本隔离目录）。</summary>
    public string InstanceName { get; init; } = string.Empty;

    /// <summary>用户选择的目标游戏路径（来自路径列表）。空表示使用自动探测的游戏路径。</summary>
    public string TargetGamePath { get; init; } = string.Empty;

    /// <summary>原始整合包文件路径（即拖放或按钮选中的文件）。</summary>
    public string ModpackFilePath { get; init; } = string.Empty;
}
