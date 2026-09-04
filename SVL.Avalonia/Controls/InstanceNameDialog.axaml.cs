using Avalonia;
using Avalonia.Controls;
using System.Windows.Input;

namespace SVL.Avalonia.Controls;

public partial class InstanceNameDialog : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<InstanceNameDialog, string>(nameof(Title), "实例命名");

    public static readonly StyledProperty<string> InstanceNameProperty =
        AvaloniaProperty.Register<InstanceNameDialog, string>(nameof(InstanceName), string.Empty);

    public static readonly StyledProperty<string> ErrorMessageProperty =
        AvaloniaProperty.Register<InstanceNameDialog, string>(nameof(ErrorMessage), string.Empty);

    public static readonly StyledProperty<bool> HasErrorProperty =
        AvaloniaProperty.Register<InstanceNameDialog, bool>(nameof(HasError), false);

    public static readonly StyledProperty<ICommand?> ConfirmCommandProperty =
        AvaloniaProperty.Register<InstanceNameDialog, ICommand?>(nameof(ConfirmCommand));

    public static readonly StyledProperty<ICommand?> CancelCommandProperty =
        AvaloniaProperty.Register<InstanceNameDialog, ICommand?>(nameof(CancelCommand));

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string InstanceName
    {
        get => GetValue(InstanceNameProperty);
        set => SetValue(InstanceNameProperty, value);
    }

    /// <summary>校验错误消息（显示在输入框下方正文中，不污染标题）。</summary>
    public string ErrorMessage
    {
        get => GetValue(ErrorMessageProperty);
        set => SetValue(ErrorMessageProperty, value);
    }

    /// <summary>是否存在校验错误（控制错误消息可见性）。</summary>
    public bool HasError
    {
        get => GetValue(HasErrorProperty);
        set => SetValue(HasErrorProperty, value);
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

    public InstanceNameDialog()
    {
        InitializeComponent();
    }
}
