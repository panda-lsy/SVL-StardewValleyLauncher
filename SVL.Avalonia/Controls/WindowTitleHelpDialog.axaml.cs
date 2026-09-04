using Avalonia;
using Avalonia.Controls;
using System.Windows.Input;

namespace SVL.Avalonia.Controls;

public partial class WindowTitleHelpDialog : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<WindowTitleHelpDialog, string>(nameof(Title), "窗口标题帮助");

    public static readonly StyledProperty<string> HelpTextProperty =
        AvaloniaProperty.Register<WindowTitleHelpDialog, string>(nameof(HelpText), "你可以修改游戏窗口标题用于兼容截图/录制工具。建议保留默认或仅做小范围调整。\n");

    public static readonly StyledProperty<ICommand?> CloseCommandProperty =
        AvaloniaProperty.Register<WindowTitleHelpDialog, ICommand?>(nameof(CloseCommand));

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string HelpText
    {
        get => GetValue(HelpTextProperty);
        set => SetValue(HelpTextProperty, value);
    }

    public ICommand? CloseCommand
    {
        get => GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    public WindowTitleHelpDialog()
    {
        InitializeComponent();
    }
}
