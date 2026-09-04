using Avalonia;
using Avalonia.Controls;
using System.Collections.Generic;
using System.Windows.Input;

namespace SVL.Avalonia.Controls;

/// <summary>
/// Modpack 拖放导入对话框（元数据预览 + 路径选择 + 版本名输入）。
/// 由 DialogService.ShowModpackDropDialogAsync 创建并填充元数据属性后以模态窗口显示。
/// ImportCommand 触发时对话框宿主窗口关闭并返回 InstanceName；CancelCommand 关闭返回 null。
/// </summary>
public partial class ModpackDropDialog : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<ModpackDropDialog, string>(nameof(Title), "导入 Modpack");

    /// <summary>可选安装路径列表的显示名（来自版本选择页路径列表）。空列表时隐藏路径选择。</summary>
    public static readonly StyledProperty<IEnumerable<string>> PathEntryDisplayNamesProperty =
        AvaloniaProperty.Register<ModpackDropDialog, IEnumerable<string>>(nameof(PathEntryDisplayNames));

    /// <summary>当前选中的路径索引（-1 表示未选中）。</summary>
    public static readonly StyledProperty<int> SelectedPathIndexProperty =
        AvaloniaProperty.Register<ModpackDropDialog, int>(nameof(SelectedPathIndex), -1);

    /// <summary>是否存在可选路径（控制路径选择区显隐）。</summary>
    public static readonly StyledProperty<bool> HasPathEntriesProperty =
        AvaloniaProperty.Register<ModpackDropDialog, bool>(nameof(HasPathEntries));

    public static readonly StyledProperty<string> ModpackNameProperty =
        AvaloniaProperty.Register<ModpackDropDialog, string>(nameof(ModpackName), string.Empty);

    public static readonly StyledProperty<string> ModpackVersionProperty =
        AvaloniaProperty.Register<ModpackDropDialog, string>(nameof(ModpackVersion), "-");

    public static readonly StyledProperty<string> ModpackAuthorProperty =
        AvaloniaProperty.Register<ModpackDropDialog, string>(nameof(ModpackAuthor), "-");

    public static readonly StyledProperty<string> ModpackDescriptionProperty =
        AvaloniaProperty.Register<ModpackDropDialog, string>(nameof(ModpackDescription), "-");

    public static readonly StyledProperty<string> ModCountTextProperty =
        AvaloniaProperty.Register<ModpackDropDialog, string>(nameof(ModCountText), "0");

    public static readonly StyledProperty<string> ModpackTypeTextProperty =
        AvaloniaProperty.Register<ModpackDropDialog, string>(nameof(ModpackTypeText), "-");

    public static readonly StyledProperty<string> ModpackIconPathProperty =
        AvaloniaProperty.Register<ModpackDropDialog, string>(nameof(ModpackIconPath), string.Empty);

    public static readonly StyledProperty<string> InstanceNameProperty =
        AvaloniaProperty.Register<ModpackDropDialog, string>(nameof(InstanceName), string.Empty);

    public static readonly StyledProperty<bool> IsLoadingProperty =
        AvaloniaProperty.Register<ModpackDropDialog, bool>(nameof(IsLoading), true);

    public static readonly StyledProperty<bool> HasErrorProperty =
        AvaloniaProperty.Register<ModpackDropDialog, bool>(nameof(HasError), false);

    public static readonly StyledProperty<string> ErrorMessageProperty =
        AvaloniaProperty.Register<ModpackDropDialog, string>(nameof(ErrorMessage), string.Empty);

    public static readonly StyledProperty<ICommand?> ImportCommandProperty =
        AvaloniaProperty.Register<ModpackDropDialog, ICommand?>(nameof(ImportCommand));

    public static readonly StyledProperty<ICommand?> CancelCommandProperty =
        AvaloniaProperty.Register<ModpackDropDialog, ICommand?>(nameof(CancelCommand));

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>可选安装路径显示名列表（来自版本选择页路径列表）。</summary>
    public IEnumerable<string> PathEntryDisplayNames
    {
        get => GetValue(PathEntryDisplayNamesProperty);
        set => SetValue(PathEntryDisplayNamesProperty, value);
    }

    /// <summary>当前选中的路径索引（-1 表示未选中）。</summary>
    public int SelectedPathIndex
    {
        get => GetValue(SelectedPathIndexProperty);
        set => SetValue(SelectedPathIndexProperty, value);
    }

    /// <summary>是否存在可选路径（控制路径选择区显隐）。</summary>
    public bool HasPathEntries
    {
        get => GetValue(HasPathEntriesProperty);
        set => SetValue(HasPathEntriesProperty, value);
    }

    public string ModpackName
    {
        get => GetValue(ModpackNameProperty);
        set => SetValue(ModpackNameProperty, value);
    }

    public string ModpackVersion
    {
        get => GetValue(ModpackVersionProperty);
        set => SetValue(ModpackVersionProperty, value);
    }

    public string ModpackAuthor
    {
        get => GetValue(ModpackAuthorProperty);
        set => SetValue(ModpackAuthorProperty, value);
    }

    public string ModpackDescription
    {
        get => GetValue(ModpackDescriptionProperty);
        set => SetValue(ModpackDescriptionProperty, value);
    }

    public string ModCountText
    {
        get => GetValue(ModCountTextProperty);
        set => SetValue(ModCountTextProperty, value);
    }

    public string ModpackTypeText
    {
        get => GetValue(ModpackTypeTextProperty);
        set => SetValue(ModpackTypeTextProperty, value);
    }

    public string ModpackIconPath
    {
        get => GetValue(ModpackIconPathProperty);
        set => SetValue(ModpackIconPathProperty, value);
    }

    public string InstanceName
    {
        get => GetValue(InstanceNameProperty);
        set => SetValue(InstanceNameProperty, value);
    }

    public bool IsLoading
    {
        get => GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public bool HasError
    {
        get => GetValue(HasErrorProperty);
        set => SetValue(HasErrorProperty, value);
    }

    public string ErrorMessage
    {
        get => GetValue(ErrorMessageProperty);
        set => SetValue(ErrorMessageProperty, value);
    }

    public ICommand? ImportCommand
    {
        get => GetValue(ImportCommandProperty);
        set => SetValue(ImportCommandProperty, value);
    }

    public ICommand? CancelCommand
    {
        get => GetValue(CancelCommandProperty);
        set => SetValue(CancelCommandProperty, value);
    }

    public ModpackDropDialog()
    {
        InitializeComponent();
    }
}
