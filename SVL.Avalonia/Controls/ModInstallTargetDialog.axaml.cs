using Avalonia;
using Avalonia.Controls;
using System.Collections.Generic;
using System.Windows.Input;

namespace SVL.Avalonia.Controls;

/// <summary>
/// 在线 Mod 安装前的实例选择对话框。
/// 提供 SMAPI/Base 实例列表、安装、另存为和取消操作。
/// </summary>
public partial class ModInstallTargetDialog : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<ModInstallTargetDialog, string>(nameof(Title), "没有选择游戏版本，请选择");

    public static readonly StyledProperty<string> MessageProperty =
        AvaloniaProperty.Register<ModInstallTargetDialog, string>(nameof(Message), string.Empty);

    public static readonly StyledProperty<IEnumerable<string>> TargetDisplayNamesProperty =
        AvaloniaProperty.Register<ModInstallTargetDialog, IEnumerable<string>>(nameof(TargetDisplayNames), new List<string>());

    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<ModInstallTargetDialog, int>(nameof(SelectedIndex), -1);

    public static readonly StyledProperty<string> EmptyHintProperty =
        AvaloniaProperty.Register<ModInstallTargetDialog, string>(nameof(EmptyHint), "当前没有可用的 SMAPI 版本，请先安装 SMAPI。");

    public static readonly StyledProperty<bool> HasTargetsProperty =
        AvaloniaProperty.Register<ModInstallTargetDialog, bool>(nameof(HasTargets));

    public static readonly StyledProperty<bool> HasNoTargetsProperty =
        AvaloniaProperty.Register<ModInstallTargetDialog, bool>(nameof(HasNoTargets), true);

    public static readonly StyledProperty<ICommand?> ConfirmCommandProperty =
        AvaloniaProperty.Register<ModInstallTargetDialog, ICommand?>(nameof(ConfirmCommand));

    public static readonly StyledProperty<ICommand?> SaveAsCommandProperty =
        AvaloniaProperty.Register<ModInstallTargetDialog, ICommand?>(nameof(SaveAsCommand));

    public static readonly StyledProperty<ICommand?> CancelCommandProperty =
        AvaloniaProperty.Register<ModInstallTargetDialog, ICommand?>(nameof(CancelCommand));

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

    public IEnumerable<string> TargetDisplayNames
    {
        get => GetValue(TargetDisplayNamesProperty);
        set => SetValue(TargetDisplayNamesProperty, value);
    }

    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    public string EmptyHint
    {
        get => GetValue(EmptyHintProperty);
        set => SetValue(EmptyHintProperty, value);
    }

    public bool HasTargets
    {
        get => GetValue(HasTargetsProperty);
        set => SetValue(HasTargetsProperty, value);
    }

    public bool HasNoTargets
    {
        get => GetValue(HasNoTargetsProperty);
        set => SetValue(HasNoTargetsProperty, value);
    }

    public ICommand? ConfirmCommand
    {
        get => GetValue(ConfirmCommandProperty);
        set => SetValue(ConfirmCommandProperty, value);
    }

    public ICommand? SaveAsCommand
    {
        get => GetValue(SaveAsCommandProperty);
        set => SetValue(SaveAsCommandProperty, value);
    }

    public ICommand? CancelCommand
    {
        get => GetValue(CancelCommandProperty);
        set => SetValue(CancelCommandProperty, value);
    }

    public ModInstallTargetDialog()
    {
        InitializeComponent();
    }
}
