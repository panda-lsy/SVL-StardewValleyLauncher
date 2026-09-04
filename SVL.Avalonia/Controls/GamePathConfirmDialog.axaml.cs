using Avalonia;
using Avalonia.Controls;
using System.Windows.Input;

namespace SVL.Avalonia.Controls;

public partial class GamePathConfirmDialog : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<GamePathConfirmDialog, string>(nameof(Title), "确认游戏路径");

    public static readonly StyledProperty<string> MessageProperty =
        AvaloniaProperty.Register<GamePathConfirmDialog, string>(nameof(Message), "请确认此目录为 Stardew Valley 游戏安装目录。\n");

    public static readonly StyledProperty<string> PathToConfirmProperty =
        AvaloniaProperty.Register<GamePathConfirmDialog, string>(nameof(PathToConfirm), string.Empty);

    public static readonly StyledProperty<ICommand?> ConfirmCommandProperty =
        AvaloniaProperty.Register<GamePathConfirmDialog, ICommand?>(nameof(ConfirmCommand));

    public static readonly StyledProperty<ICommand?> CancelCommandProperty =
        AvaloniaProperty.Register<GamePathConfirmDialog, ICommand?>(nameof(CancelCommand));

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public string PathToConfirm
    {
        get => GetValue(PathToConfirmProperty);
        set => SetValue(PathToConfirmProperty, value);
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

    public GamePathConfirmDialog()
    {
        InitializeComponent();
    }
}
