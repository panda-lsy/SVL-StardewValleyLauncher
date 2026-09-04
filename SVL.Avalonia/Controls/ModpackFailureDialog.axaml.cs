using Avalonia;
using Avalonia.Controls;
using System.Windows.Input;

namespace SVL.Avalonia.Controls;

public partial class ModpackFailureDialog : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<ModpackFailureDialog, string>(nameof(Title), "Modpack 安装失败");

    public static readonly StyledProperty<string> FailureReasonProperty =
        AvaloniaProperty.Register<ModpackFailureDialog, string>(nameof(FailureReason), string.Empty);

    public static readonly StyledProperty<string> LogPathProperty =
        AvaloniaProperty.Register<ModpackFailureDialog, string>(nameof(LogPath), string.Empty);

    public static readonly StyledProperty<ICommand?> OpenLogCommandProperty =
        AvaloniaProperty.Register<ModpackFailureDialog, ICommand?>(nameof(OpenLogCommand));

    public static readonly StyledProperty<ICommand?> RetryCommandProperty =
        AvaloniaProperty.Register<ModpackFailureDialog, ICommand?>(nameof(RetryCommand));

    public static readonly StyledProperty<ICommand?> CloseCommandProperty =
        AvaloniaProperty.Register<ModpackFailureDialog, ICommand?>(nameof(CloseCommand));

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string FailureReason
    {
        get => GetValue(FailureReasonProperty);
        set => SetValue(FailureReasonProperty, value);
    }

    public string LogPath
    {
        get => GetValue(LogPathProperty);
        set => SetValue(LogPathProperty, value);
    }

    public ICommand? OpenLogCommand
    {
        get => GetValue(OpenLogCommandProperty);
        set => SetValue(OpenLogCommandProperty, value);
    }

    public ICommand? RetryCommand
    {
        get => GetValue(RetryCommandProperty);
        set => SetValue(RetryCommandProperty, value);
    }

    public ICommand? CloseCommand
    {
        get => GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    public ModpackFailureDialog()
    {
        InitializeComponent();
    }
}
