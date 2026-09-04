using Avalonia;
using Avalonia.Controls;
using System.Windows.Input;

namespace SVL.Avalonia.Controls;

public partial class GamePathSelectionDialog : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<GamePathSelectionDialog, string>(nameof(Title), "选择游戏路径");

    public static readonly StyledProperty<string> DescriptionProperty =
        AvaloniaProperty.Register<GamePathSelectionDialog, string>(nameof(Description), "未自动探测到 Stardew Valley，请手动选择游戏目录。\n");

    public static readonly StyledProperty<string> SelectedPathProperty =
        AvaloniaProperty.Register<GamePathSelectionDialog, string>(nameof(SelectedPath), string.Empty);

    public static readonly StyledProperty<ICommand?> BrowseCommandProperty =
        AvaloniaProperty.Register<GamePathSelectionDialog, ICommand?>(nameof(BrowseCommand));

    public static readonly StyledProperty<ICommand?> ConfirmCommandProperty =
        AvaloniaProperty.Register<GamePathSelectionDialog, ICommand?>(nameof(ConfirmCommand));

    public static readonly StyledProperty<ICommand?> CancelCommandProperty =
        AvaloniaProperty.Register<GamePathSelectionDialog, ICommand?>(nameof(CancelCommand));

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string SelectedPath
    {
        get => GetValue(SelectedPathProperty);
        set => SetValue(SelectedPathProperty, value);
    }

    public ICommand? BrowseCommand
    {
        get => GetValue(BrowseCommandProperty);
        set => SetValue(BrowseCommandProperty, value);
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

    public GamePathSelectionDialog()
    {
        InitializeComponent();
    }
}
