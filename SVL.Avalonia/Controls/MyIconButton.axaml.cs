using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using System.Windows.Input;

namespace SVL.Avalonia.Controls;

public partial class MyIconButton : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<MyIconButton, string>(nameof(Text), "按钮");

    public static readonly StyledProperty<string> IconProperty =
        AvaloniaProperty.Register<MyIconButton, string>(nameof(Icon), string.Empty);

    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<MyIconButton, ICommand?>(nameof(Command));

    public static readonly StyledProperty<object?> CommandParameterProperty =
        AvaloniaProperty.Register<MyIconButton, object?>(nameof(CommandParameter));

    public static readonly StyledProperty<bool> IsButtonEnabledProperty =
        AvaloniaProperty.Register<MyIconButton, bool>(nameof(IsButtonEnabled), true);

    public static readonly StyledProperty<HorizontalAlignment> HorizontalButtonAlignmentProperty =
        AvaloniaProperty.Register<MyIconButton, HorizontalAlignment>(nameof(HorizontalButtonAlignment), HorizontalAlignment.Stretch);

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    public bool IsButtonEnabled
    {
        get => GetValue(IsButtonEnabledProperty);
        set => SetValue(IsButtonEnabledProperty, value);
    }

    public HorizontalAlignment HorizontalButtonAlignment
    {
        get => GetValue(HorizontalButtonAlignmentProperty);
        set => SetValue(HorizontalButtonAlignmentProperty, value);
    }

    public MyIconButton()
    {
        InitializeComponent();
    }
}
