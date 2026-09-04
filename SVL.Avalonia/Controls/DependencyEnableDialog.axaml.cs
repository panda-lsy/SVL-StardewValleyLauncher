using Avalonia;
using Avalonia.Controls;
using System.Collections.Generic;
using System.Windows.Input;

namespace SVL.Avalonia.Controls;

public partial class DependencyEnableDialog : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<DependencyEnableDialog, string>(nameof(Title), "启用依赖");

    public static readonly StyledProperty<string> MessageProperty =
        AvaloniaProperty.Register<DependencyEnableDialog, string>(nameof(Message), "检测到当前资源依赖以下模组，建议一并启用。\n");

    public static readonly StyledProperty<IEnumerable<string>> DependenciesProperty =
        AvaloniaProperty.Register<DependencyEnableDialog, IEnumerable<string>>(nameof(Dependencies), new List<string>());

    public static readonly StyledProperty<ICommand?> EnableCommandProperty =
        AvaloniaProperty.Register<DependencyEnableDialog, ICommand?>(nameof(EnableCommand));

    public static readonly StyledProperty<ICommand?> CancelCommandProperty =
        AvaloniaProperty.Register<DependencyEnableDialog, ICommand?>(nameof(CancelCommand));

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

    public IEnumerable<string> Dependencies
    {
        get => GetValue(DependenciesProperty);
        set => SetValue(DependenciesProperty, value);
    }

    public ICommand? EnableCommand
    {
        get => GetValue(EnableCommandProperty);
        set => SetValue(EnableCommandProperty, value);
    }

    public ICommand? CancelCommand
    {
        get => GetValue(CancelCommandProperty);
        set => SetValue(CancelCommandProperty, value);
    }

    public DependencyEnableDialog()
    {
        InitializeComponent();
    }
}
