using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SVL.Avalonia.Controls;

public sealed class ConfirmDialogModel
{
    public string Title { get; set; } = "确认";
    public string Message { get; set; } = string.Empty;
}

public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    private void Confirm_Click(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
