using Avalonia;
using Avalonia.Controls;
using System.Collections.Generic;
using System.Windows.Input;

namespace SVL.Avalonia.Controls;

public partial class InstanceSelectionDialog : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<InstanceSelectionDialog, string>(nameof(Title), "选择实例");

    public static readonly StyledProperty<IEnumerable<string>> InstancesProperty =
        AvaloniaProperty.Register<InstanceSelectionDialog, IEnumerable<string>>(nameof(Instances), new List<string>());

    public static readonly StyledProperty<string?> SelectedInstanceProperty =
        AvaloniaProperty.Register<InstanceSelectionDialog, string?>(nameof(SelectedInstance));

    public static readonly StyledProperty<ICommand?> ConfirmCommandProperty =
        AvaloniaProperty.Register<InstanceSelectionDialog, ICommand?>(nameof(ConfirmCommand));

    public static readonly StyledProperty<ICommand?> CancelCommandProperty =
        AvaloniaProperty.Register<InstanceSelectionDialog, ICommand?>(nameof(CancelCommand));

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public IEnumerable<string> Instances
    {
        get => GetValue(InstancesProperty);
        set => SetValue(InstancesProperty, value);
    }

    public string? SelectedInstance
    {
        get => GetValue(SelectedInstanceProperty);
        set => SetValue(SelectedInstanceProperty, value);
    }

    public ICommand? ConfirmCommand
    {
        get => GetValue(ConfirmCommandProperty);
        set => SetValue(ConfirmCommandProperty, value);
    }

    public ICommand? CancelCommand
    {
        get => GetValue(CancelCommandProperty);
        set => SetValue(CancelCommandProperty, value);
    }

    public InstanceSelectionDialog()
    {
        InitializeComponent();
    }
}
