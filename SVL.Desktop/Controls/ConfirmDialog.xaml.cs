using System.Windows;

namespace SVL.Desktop.Controls;

/// <summary>
/// 自定义确认对话框（符合启动器 UI 风格）
/// </summary>
public partial class ConfirmDialog : Window
{
    /// <summary>
    /// 用户是否选择了"是"
    /// </summary>
    public bool IsConfirmed { get; private set; } = false;

    public ConfirmDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 设置对话框标题
    /// </summary>
    public void SetTitle(string title)
    {
        TitleTextBlock.Text = title;
        Title = title;
    }

    /// <summary>
    /// 设置主要消息内容
    /// </summary>
    public void SetMessage(string message)
    {
        MessageTextBlock.Text = message;
    }

    /// <summary>
    /// 设置详细信息（可选）
    /// </summary>
    public void SetDetail(string detail)
    {
        if (string.IsNullOrEmpty(detail))
        {
            DetailTextBlock.Visibility = Visibility.Collapsed;
        }
        else
        {
            DetailTextBlock.Text = detail;
            DetailTextBlock.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// 自定义按钮文本
    /// </summary>
    public void SetButtonText(string yesText = "是", string noText = "否")
    {
        YesButton.Content = yesText;
        NoButton.Content = noText;
    }

    /// <summary>
    /// 显示确认对话框（静态方法，快捷使用）
    /// </summary>
    /// <param name="owner">父窗口</param>
    /// <param name="message">主要消息</param>
    /// <param name="title">标题（默认"确认"）</param>
    /// <param name="detail">详细信息（可选）</param>
    /// <param name="yesText">"是"按钮文本</param>
    /// <param name="noText">"否"按钮文本</param>
    /// <returns>用户是否选择了"是"</returns>
    public static bool Show(
        Window? owner,
        string message,
        string title = "确认",
        string? detail = null,
        string yesText = "是",
        string noText = "否")
    {
        var dialog = new ConfirmDialog();
        dialog.SetTitle(title);
        dialog.SetMessage(message);
        dialog.SetDetail(detail ?? string.Empty);
        dialog.SetButtonText(yesText, noText);

        if (owner != null)
        {
            dialog.Owner = owner;
        }

        // 应用模糊效果
        if (dialog.Owner is MainWindow mainWindow)
        {
            mainWindow.ApplyBlurEffect();
        }

        var result = dialog.ShowDialog();

        // 移除模糊效果
        if (dialog.Owner is MainWindow main)
        {
            main.RemoveBlurEffect();
        }

        return result == true && dialog.IsConfirmed;
    }

    /// <summary>
    /// "是"按钮点击事件
    /// </summary>
    private void Yes_Click(object sender, RoutedEventArgs e)
    {
        IsConfirmed = true;
        DialogResult = true;
        Close();
    }

    /// <summary>
    /// "否"按钮点击事件
    /// </summary>
    private void No_Click(object sender, RoutedEventArgs e)
    {
        IsConfirmed = false;
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// 窗口加载时将焦点设置到"否"按钮（默认选项，防止误操作）
    /// </summary>
    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        NoButton.Focus();
    }
}
