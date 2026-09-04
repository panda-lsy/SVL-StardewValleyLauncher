using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SVL.Avalonia.Controls;

public sealed class InputDialogModel
{
    public string Title { get; set; } = "输入";
    public string Message { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public partial class InputDialog : Window
{
    public InputDialog()
    {
        InitializeComponent();
    }

    private void Confirm_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InputDialogModel model)
        {
            Close(model.Value);
            return;
        }

        Close(string.Empty);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}
