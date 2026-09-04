using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SVL.Avalonia.Controls;

public sealed class SvlMessageBoxModel
{
    public string Title { get; set; } = "提示";
    public string Message { get; set; } = string.Empty;
}

public partial class SvlMessageBox : Window
{
    public SvlMessageBox()
    {
        InitializeComponent();
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
