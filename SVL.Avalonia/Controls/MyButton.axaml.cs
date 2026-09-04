using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using System.Windows.Input;

namespace SVL.Avalonia.Controls;

public partial class MyButton : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<MyButton, string>(nameof(Text), "按钮");

    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<MyButton, ICommand?>(nameof(Command));

    public static readonly StyledProperty<object?> CommandParameterProperty =
        AvaloniaProperty.Register<MyButton, object?>(nameof(CommandParameter));

    public static readonly StyledProperty<bool> IsButtonEnabledProperty =
        AvaloniaProperty.Register<MyButton, bool>(nameof(IsButtonEnabled), true);

    public static readonly StyledProperty<HorizontalAlignment> HorizontalButtonAlignmentProperty =
        AvaloniaProperty.Register<MyButton, HorizontalAlignment>(nameof(HorizontalButtonAlignment), HorizontalAlignment.Stretch);

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
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

    public MyButton()
    {
        InitializeComponent();
    }
}
