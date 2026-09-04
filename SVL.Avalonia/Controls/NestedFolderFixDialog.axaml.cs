using Avalonia;
using Avalonia.Controls;
using System.Windows.Input;

namespace SVL.Avalonia.Controls;

public partial class NestedFolderFixDialog : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<NestedFolderFixDialog, string>(nameof(Title), "嵌套目录修复");

    public static readonly StyledProperty<string> MessageProperty =
        AvaloniaProperty.Register<NestedFolderFixDialog, string>(nameof(Message), "检测到压缩包目录层级异常，可自动整理到正确目录结构。\n");

    public static readonly StyledProperty<string> SourceFolderProperty =
        AvaloniaProperty.Register<NestedFolderFixDialog, string>(nameof(SourceFolder), string.Empty);

    public static readonly StyledProperty<string> TargetFolderProperty =
        AvaloniaProperty.Register<NestedFolderFixDialog, string>(nameof(TargetFolder), string.Empty);

    public static readonly StyledProperty<ICommand?> FixCommandProperty =
        AvaloniaProperty.Register<NestedFolderFixDialog, ICommand?>(nameof(FixCommand));

    public static readonly StyledProperty<ICommand?> SkipCommandProperty =
        AvaloniaProperty.Register<NestedFolderFixDialog, ICommand?>(nameof(SkipCommand));

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

    public string SourceFolder
    {
        get => GetValue(SourceFolderProperty);
        set => SetValue(SourceFolderProperty, value);
    }

    public string TargetFolder
    {
        get => GetValue(TargetFolderProperty);
        set => SetValue(TargetFolderProperty, value);
    }

    public ICommand? FixCommand
    {
        get => GetValue(FixCommandProperty);
        set => SetValue(FixCommandProperty, value);
    }

    public ICommand? SkipCommand
    {
        get => GetValue(SkipCommandProperty);
        set => SetValue(SkipCommandProperty, value);
    }

    public NestedFolderFixDialog()
    {
        InitializeComponent();
    }
}
