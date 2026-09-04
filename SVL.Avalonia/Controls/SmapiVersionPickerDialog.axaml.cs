using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using SVL.Avalonia.ViewModels;

namespace SVL.Avalonia.Controls;

public partial class SmapiVersionPickerDialog : UserControl
{
    public SmapiVersionPickerDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
