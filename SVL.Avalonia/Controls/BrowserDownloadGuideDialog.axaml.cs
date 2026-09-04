using Avalonia;
using Avalonia.Controls;
using System.Windows.Input;

namespace SVL.Avalonia.Controls;

public partial class BrowserDownloadGuideDialog : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<BrowserDownloadGuideDialog, string>(nameof(Title), "浏览器下载指引");

    public static readonly StyledProperty<string> GuideMessageProperty =
        AvaloniaProperty.Register<BrowserDownloadGuideDialog, string>(nameof(GuideMessage), "该资源需要在浏览器完成授权或手动下载。请打开链接并完成下载后返回。\n");

    public static readonly StyledProperty<string> DownloadUrlProperty =
        AvaloniaProperty.Register<BrowserDownloadGuideDialog, string>(nameof(DownloadUrl), string.Empty);

    public static readonly StyledProperty<ICommand?> OpenInBrowserCommandProperty =
        AvaloniaProperty.Register<BrowserDownloadGuideDialog, ICommand?>(nameof(OpenInBrowserCommand));

    public static readonly StyledProperty<ICommand?> CopyLinkCommandProperty =
        AvaloniaProperty.Register<BrowserDownloadGuideDialog, ICommand?>(nameof(CopyLinkCommand));

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string GuideMessage
    {
        get => GetValue(GuideMessageProperty);
        set => SetValue(GuideMessageProperty, value);
    }

    public string DownloadUrl
    {
        get => GetValue(DownloadUrlProperty);
        set => SetValue(DownloadUrlProperty, value);
    }

    public ICommand? OpenInBrowserCommand
    {
        get => GetValue(OpenInBrowserCommandProperty);
        set => SetValue(OpenInBrowserCommandProperty, value);
    }

    public ICommand? CopyLinkCommand
    {
        get => GetValue(CopyLinkCommandProperty);
        set => SetValue(CopyLinkCommandProperty, value);
    }

    public BrowserDownloadGuideDialog()
    {
        InitializeComponent();
    }
}
