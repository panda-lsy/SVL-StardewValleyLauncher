using Avalonia;
using Avalonia.Controls;

namespace SVL.Avalonia.Controls;

public partial class MyCard : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<MyCard, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string> SubtitleProperty =
        AvaloniaProperty.Register<MyCard, string>(nameof(Subtitle), string.Empty);

    public static readonly StyledProperty<object?> CardContentProperty =
        AvaloniaProperty.Register<MyCard, object?>(nameof(CardContent));

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public object? CardContent
    {
        get => GetValue(CardContentProperty);
        set => SetValue(CardContentProperty, value);
    }

    public MyCard()
    {
        InitializeComponent();
    }
}
