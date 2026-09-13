using System.Windows.Input;
using Avalonia.Controls;

namespace SVL.Avalonia.Controls;

public sealed class ConflictResolutionDialogModel
{
    public string Title { get; set; } = "处理冲突";
    public string Message { get; set; } = string.Empty;
    public string ComparisonSummary { get; set; } = string.Empty;
    public string IncomingPathText { get; set; } = string.Empty;
    public string ExistingPathText { get; set; } = string.Empty;
    public string OpenIncomingText { get; set; } = "打开备份文件夹";
    public string OpenExistingText { get; set; } = "打开原有 Mod 文件夹";
    public string BackupNotice { get; set; } = "确认替换前，系统会先备份原有 Mod。备份失败时不会继续替换。";
    public string ReplaceButtonText { get; set; } = "替换（先备份）";
    public string IncomingPath { get; set; } = string.Empty;
    public string ExistingPath { get; set; } = string.Empty;
    public ICommand? OpenIncomingCommand { get; set; }
    public ICommand? OpenExistingCommand { get; set; }
    public ICommand? CancelCommand { get; set; }
    public ICommand? ReplaceCommand { get; set; }
}

public partial class ConflictResolutionDialog : Window
{
    public ConflictResolutionDialog()
    {
        InitializeComponent();
    }
}
