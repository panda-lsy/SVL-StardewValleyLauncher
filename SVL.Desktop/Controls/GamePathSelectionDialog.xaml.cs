using System;
using System.IO;
using SVL.Core.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using SVL.Core.Logging;

namespace SVL.Desktop.Controls;

/// <summary>
/// GamePathSelectionDialog.xaml 的交互逻辑
/// </summary>
public partial class GamePathSelectionDialog : Window
{
    public string? SelectedPath { get; private set; }

    public GamePathSelectionDialog()
    {
        InitializeComponent();
        Loaded += (s, e) => PathTextBox.Focus();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new FolderBrowserDialog();
        dialog.Description = "选择 Stardew Valley 游戏安装目录";
        dialog.ShowNewFolderButton = false;

        // 设置默认路径为 Steam 安装目录
        var steamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley";
        if (Directory.Exists(steamPath))
        {
            dialog.SelectedPath = steamPath;
        }

        var result = dialog.ShowDialog();
        if (result == System.Windows.Forms.DialogResult.OK)
        {
            PathTextBox.Text = dialog.SelectedPath;
        }
    }

    private void PathTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ValidatePath();
    }

    private bool ValidatePath()
    {
        var path = PathTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(path))
        {
            HideError();
            ConfirmButton.IsEnabled = false;
            return false;
        }

        if (!Directory.Exists(path))
        {
            ShowError("目录不存在");
            ConfirmButton.IsEnabled = false;
            return false;
        }

        var exePath = Path.Combine(path, "Stardew Valley.exe");
        if (!File.Exists(exePath))
        {
            ShowError("所选目录不包含游戏文件（Stardew Valley.exe）");
            ConfirmButton.IsEnabled = false;
            return false;
        }

        HideError();
        ConfirmButton.IsEnabled = true;
        return true;
    }

    private void ShowError(string message)
    {
        ErrorTextBlock.Text = message;
        ErrorTextBlock.Visibility = Visibility.Visible;
    }

    private void HideError()
    {
        ErrorTextBlock.Visibility = Visibility.Collapsed;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var path = PathTextBox.Text.Trim();

        if (!ValidatePath())
        {
            return;
        }

        SelectedPath = path;
        Log.Info($"[GamePathSelectionDialog] 用户选择游戏路径: {path}");
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void SteamLink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            ProcessEx.OpenUrl(e.Uri.AbsoluteUri);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[GamePathSelectionDialog] 打开 Steam 链接失败");
        }
    }
}
