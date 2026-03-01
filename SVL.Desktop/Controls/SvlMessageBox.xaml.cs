using System.Windows;

namespace SVL.Desktop.Controls;

/// <summary>
/// SVL 自定义消息对话框，替代 System.Windows.MessageBox。
/// 支持三种模式：Info（信息提示）、Error（错误提示）、Confirm（确认对话框）。
/// </summary>
public partial class SvlMessageBox : Window
{
    /// <summary>
    /// 消息框类型
    /// </summary>
    public enum MessageType
    {
        Info,
        Success,
        Warning,
        Error,
        Confirm
    }

    /// <summary>
    /// 用户是否选择了确认
    /// </summary>
    public bool IsConfirmed { get; private set; } = false;

    public SvlMessageBox()
    {
        InitializeComponent();
    }

    #region 配置方法

    private void Configure(string message, string title, MessageType type, string? detail, string okText, string? cancelText)
    {
        TitleTextBlock.Text = title;
        Title = title;
        MessageTextBlock.Text = message;

        if (!string.IsNullOrEmpty(detail))
        {
            DetailTextBlock.Text = detail;
            DetailTextBlock.Visibility = Visibility.Visible;
        }

        OkButton.Content = okText;

        if (!string.IsNullOrEmpty(cancelText))
        {
            CancelButton.Content = cancelText;
            CancelButton.Visibility = Visibility.Visible;
        }

        // 设置图标和颜色
        switch (type)
        {
            case MessageType.Info:
                IconTextBlock.Text = "ℹ";
                IconTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("ColorBrush2");
                break;
            case MessageType.Success:
                IconTextBlock.Text = "✓";
                IconTextBlock.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(76, 175, 80)); // Green
                break;
            case MessageType.Warning:
                IconTextBlock.Text = "⚠";
                IconTextBlock.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(255, 152, 0)); // Orange
                break;
            case MessageType.Error:
                IconTextBlock.Text = "✗";
                IconTextBlock.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(244, 67, 54)); // Red
                OkButton.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(244, 67, 54));
                break;
            case MessageType.Confirm:
                IconTextBlock.Text = "?";
                IconTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("ColorBrush2");
                break;
        }
    }

    #endregion

    #region 静态工厂方法

    /// <summary>
    /// 获取当前活动的主窗口作为 Owner
    /// </summary>
    private static Window? GetMainWindow()
    {
        return Application.Current?.MainWindow is MainWindow mw && mw.IsLoaded ? mw : null;
    }

    /// <summary>
    /// 应用/移除模糊效果
    /// </summary>
    private static void ApplyBlur(Window? owner)
    {
        if (owner is MainWindow mainWindow)
            mainWindow.ApplyBlurEffect();
    }

    private static void RemoveBlur(Window? owner)
    {
        if (owner is MainWindow mainWindow)
            mainWindow.RemoveBlurEffect();
    }

    /// <summary>
    /// 显示信息提示框（仅 OK 按钮）
    /// </summary>
    public static void Info(string message, string title = "提示", string? detail = null)
    {
        var owner = GetMainWindow();
        var dialog = new SvlMessageBox();
        dialog.Configure(message, title, MessageType.Info, detail, "确定", null);
        if (owner != null) dialog.Owner = owner;
        ApplyBlur(owner);
        dialog.ShowDialog();
        RemoveBlur(owner);
    }

    /// <summary>
    /// 显示成功提示框（仅 OK 按钮）
    /// </summary>
    public static void Success(string message, string title = "成功", string? detail = null)
    {
        var owner = GetMainWindow();
        var dialog = new SvlMessageBox();
        dialog.Configure(message, title, MessageType.Success, detail, "确定", null);
        if (owner != null) dialog.Owner = owner;
        ApplyBlur(owner);
        dialog.ShowDialog();
        RemoveBlur(owner);
    }

    /// <summary>
    /// 显示警告提示框（仅 OK 按钮）
    /// </summary>
    public static void Warning(string message, string title = "警告", string? detail = null)
    {
        var owner = GetMainWindow();
        var dialog = new SvlMessageBox();
        dialog.Configure(message, title, MessageType.Warning, detail, "确定", null);
        if (owner != null) dialog.Owner = owner;
        ApplyBlur(owner);
        dialog.ShowDialog();
        RemoveBlur(owner);
    }

    /// <summary>
    /// 显示错误提示框（仅 OK 按钮）
    /// </summary>
    public static void Error(string message, string title = "错误", string? detail = null)
    {
        var owner = GetMainWindow();
        var dialog = new SvlMessageBox();
        dialog.Configure(message, title, MessageType.Error, detail, "确定", null);
        if (owner != null) dialog.Owner = owner;
        ApplyBlur(owner);
        dialog.ShowDialog();
        RemoveBlur(owner);
    }

    /// <summary>
    /// 显示确认对话框（确认 + 取消）
    /// </summary>
    /// <returns>用户是否选择了确认</returns>
    public static bool Confirm(string message, string title = "确认", string? detail = null,
        string okText = "确认", string cancelText = "取消")
    {
        var owner = GetMainWindow();
        var dialog = new SvlMessageBox();
        dialog.Configure(message, title, MessageType.Confirm, detail, okText, cancelText);
        if (owner != null) dialog.Owner = owner;
        ApplyBlur(owner);
        dialog.ShowDialog();
        RemoveBlur(owner);
        return dialog.IsConfirmed;
    }

    #endregion

    #region 事件处理

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        IsConfirmed = true;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        IsConfirmed = false;
        DialogResult = false;
        Close();
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        // 确认对话框默认焦点在取消按钮，防止误操作
        if (CancelButton.Visibility == Visibility.Visible)
            CancelButton.Focus();
        else
            OkButton.Focus();
    }

    #endregion
}
