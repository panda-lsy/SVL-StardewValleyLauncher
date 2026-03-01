using System;
using System.Windows;
using System.Windows.Threading;

namespace SVL.Desktop.Controls;

/// <summary>
/// MOD 更新确认对话框
/// </summary>
public partial class ModUpdateConfirmDialog : Window
{
    public string ModName { get; set; } = string.Empty;
    public string CurrentVersion { get; set; } = string.Empty;
    public string TargetVersion { get; set; } = string.Empty;
    public string OperationType { get; set; } = "更新";
    public bool IsConfirmed { get; private set; }

    public ModUpdateConfirmDialog()
    {
        InitializeComponent();
        DataContext = this;
    }

    public static bool Show(Window owner, string modName, string currentVersion, string targetVersion, bool isUpdate)
    {
        var dialog = new ModUpdateConfirmDialog
        {
            ModName = modName,
            CurrentVersion = currentVersion,
            TargetVersion = targetVersion,
            OperationType = isUpdate ? "更新" : "降级",
            Owner = owner
        };

        dialog.ShowDialog();
        return dialog.IsConfirmed;
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        IsConfirmed = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        IsConfirmed = false;
        Close();
    }
}
