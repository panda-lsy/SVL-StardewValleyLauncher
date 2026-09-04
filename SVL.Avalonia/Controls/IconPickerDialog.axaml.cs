using Avalonia;
using Avalonia.Controls;
using System.Collections.Generic;
using System.Windows.Input;

namespace SVL.Avalonia.Controls;

public sealed class IconPickerOption
{
    public string Name { get; init; } = string.Empty;

    public string IconPath { get; init; } = string.Empty;

    public bool IsCustom { get; init; }
}

public partial class IconPickerDialog : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<IconPickerDialog, string>(nameof(Title), "选择图标");

    public static readonly StyledProperty<IEnumerable<IconPickerOption>> OptionsProperty =
        AvaloniaProperty.Register<IconPickerDialog, IEnumerable<IconPickerOption>>(nameof(Options), []);

    public static readonly StyledProperty<ICommand?> SelectOptionCommandProperty =
        AvaloniaProperty.Register<IconPickerDialog, ICommand?>(nameof(SelectOptionCommand));

    public static readonly StyledProperty<ICommand?> CancelCommandProperty =
        AvaloniaProperty.Register<IconPickerDialog, ICommand?>(nameof(CancelCommand));

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public IEnumerable<IconPickerOption> Options
    {
        get => GetValue(OptionsProperty);
        set => SetValue(OptionsProperty, value);
    }

    public ICommand? SelectOptionCommand
    {
        get => GetValue(SelectOptionCommandProperty);
        set => SetValue(SelectOptionCommandProperty, value);
    }

    public ICommand? CancelCommand
    {
        get => GetValue(CancelCommandProperty);
        set => SetValue(CancelCommandProperty, value);
    }

    public IconPickerDialog()
    {
        InitializeComponent();
    }
}
