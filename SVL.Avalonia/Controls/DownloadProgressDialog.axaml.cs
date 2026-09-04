using Avalonia;
using Avalonia.Controls;
using System.Windows.Input;

namespace SVL.Avalonia.Controls;

public partial class DownloadProgressDialog : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<DownloadProgressDialog, string>(nameof(Title), "下载进度");

    public static readonly StyledProperty<string> TaskNameProperty =
        AvaloniaProperty.Register<DownloadProgressDialog, string>(nameof(TaskName), string.Empty);

    public static readonly StyledProperty<double> ProgressPercentProperty =
        AvaloniaProperty.Register<DownloadProgressDialog, double>(nameof(ProgressPercent), 0);

    public static readonly StyledProperty<string> StatusTextProperty =
        AvaloniaProperty.Register<DownloadProgressDialog, string>(nameof(StatusText), "等待中");

    public static readonly StyledProperty<ICommand?> CancelCommandProperty =
        AvaloniaProperty.Register<DownloadProgressDialog, ICommand?>(nameof(CancelCommand));

    public static readonly StyledProperty<ICommand?> CloseCommandProperty =
        AvaloniaProperty.Register<DownloadProgressDialog, ICommand?>(nameof(CloseCommand));

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string TaskName
    {
        get => GetValue(TaskNameProperty);
        set => SetValue(TaskNameProperty, value);
    }

    public double ProgressPercent
    {
        get => GetValue(ProgressPercentProperty);
        set => SetValue(ProgressPercentProperty, value);
    }

    public string StatusText
    {
        get => GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public ICommand? CancelCommand
    {
        get => GetValue(CancelCommandProperty);
        set => SetValue(CancelCommandProperty, value);
    }

    public ICommand? CloseCommand
    {
        get => GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    public DownloadProgressDialog()
    {
        InitializeComponent();
    }
}
