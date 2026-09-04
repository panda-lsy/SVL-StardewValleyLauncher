using Avalonia;
using Avalonia.Controls;
using System.Windows.Input;

namespace SVL.Avalonia.Controls;

public partial class UpdateCompleteDialog : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<UpdateCompleteDialog, string>(nameof(Title), "更新完成");

    public static readonly StyledProperty<string> SummaryProperty =
        AvaloniaProperty.Register<UpdateCompleteDialog, string>(nameof(Summary), "已下载并应用最新版本。可立即重启以生效。\n");

    public static readonly StyledProperty<ICommand?> OpenChangelogCommandProperty =
        AvaloniaProperty.Register<UpdateCompleteDialog, ICommand?>(nameof(OpenChangelogCommand));

    public static readonly StyledProperty<ICommand?> LaterCommandProperty =
        AvaloniaProperty.Register<UpdateCompleteDialog, ICommand?>(nameof(LaterCommand));

    public static readonly StyledProperty<ICommand?> RestartNowCommandProperty =
        AvaloniaProperty.Register<UpdateCompleteDialog, ICommand?>(nameof(RestartNowCommand));

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Summary
    {
        get => GetValue(SummaryProperty);
        set => SetValue(SummaryProperty, value);
    }

    public ICommand? OpenChangelogCommand
    {
        get => GetValue(OpenChangelogCommandProperty);
        set => SetValue(OpenChangelogCommandProperty, value);
    }

    public ICommand? LaterCommand
    {
        get => GetValue(LaterCommandProperty);
        set => SetValue(LaterCommandProperty, value);
    }

    public ICommand? RestartNowCommand
    {
        get => GetValue(RestartNowCommandProperty);
        set => SetValue(RestartNowCommandProperty, value);
    }

    public UpdateCompleteDialog()
    {
        InitializeComponent();
    }
}
