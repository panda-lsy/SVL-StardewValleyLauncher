using System;
using System.Windows;
using System.Windows.Controls;
using SVL.Core.IO;
using SVL.Core.Stardew.Instance;

namespace SVL.Desktop.Controls;

/// <summary>
/// InstanceNameDialog.xaml 的交互逻辑
/// </summary>
public partial class InstanceNameDialog : Window
{
    public string? InstanceName { get; private set; }

    /// <summary>
    /// Debug 模式：安装失败时保留文件
    /// </summary>
    public bool DebugMode { get; private set; }

    /// <summary>
    /// 检查名称是否重复的回调函数
    /// </summary>
    public Func<string, bool>? CheckNameExists { get; set; }

    /// <summary>
    /// 自动转换非法字符（默认启用）
    /// </summary>
    public bool AutoSanitize { get; set; } = true;

    private bool _isUpdatingText = false;

    public InstanceNameDialog()
    {
        InitializeComponent();
        Loaded += (s, e) => InstanceNameTextBox.Focus();
        InstanceNameTextBox.TextChanged += OnTextChanged;
    }

    /// <summary>
    /// 当文本改变时，自动转换非法字符
    /// </summary>
    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!AutoSanitize || _isUpdatingText)
            return;

        var currentText = InstanceNameTextBox.Text;
        if (string.IsNullOrEmpty(currentText))
            return;

        var sanitized = FileNameValidator.SanitizeFolderName(currentText);
        if (sanitized != currentText)
        {
            _isUpdatingText = true;
            var cursorPosition = InstanceNameTextBox.SelectionStart;
            var prefix = cursorPosition > 0 && cursorPosition <= currentText.Length
                ? currentText.Substring(0, cursorPosition)
                : currentText;
            var sanitizedPrefix = FileNameValidator.SanitizeFolderName(prefix);
            InstanceNameTextBox.Text = sanitized;
            InstanceNameTextBox.SelectionStart = Math.Min(sanitizedPrefix.Length, sanitized.Length);
            InstanceNameTextBox.SelectionLength = 0;
            _isUpdatingText = false;
        }

        // 实时验证
        ValidateInput(sanitized);
    }

    /// <summary>
    /// 实时验证输入
    /// </summary>
    private void ValidateInput(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            SetError("文件夹名称不能为空");
            return;
        }

        var (isValid, errorMessage) = FileNameValidator.ValidateFolderName(folderName);
        if (!isValid)
        {
            SetError(errorMessage);
            return;
        }

        // 检查名称是否重复
        if (CheckNameExists != null && CheckNameExists(folderName))
        {
            SetError("此名称已存在，请使用不同的名称");
            return;
        }

        ClearError();
    }

    private void SetError(string message)
    {
        ErrorTextBlock.Text = message;
        ErrorTextBlock.Visibility = Visibility.Visible;
    }

    private void ClearError()
    {
        ErrorTextBlock.Visibility = Visibility.Collapsed;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var folderName = InstanceNameTextBox.Text.Trim();

        if (string.IsNullOrEmpty(folderName))
        {
            SetError("文件夹名称不能为空");
            return;
        }

        // 验证文件夹名称
        var (isValid, errorMessage) = FileNameValidator.ValidateFolderName(folderName);

        if (!isValid)
        {
            SetError(errorMessage);
            return;
        }

        // *** 检查名称是否重复 ***
        if (CheckNameExists != null && CheckNameExists(folderName))
        {
            SetError($"实例名称 '{folderName}' 已存在，请使用不同的名称");
            InstanceNameTextBox.Focus();
            return;
        }

        InstanceName = folderName;
        DebugMode = DebugModeCheckBox.IsChecked ?? false;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// 显示实例名称输入对话框（静态便捷方法）
    /// </summary>
    /// <param name="owner">父窗口</param>
    /// <param name="defaultName">默认名称</param>
    /// <param name="checkNameExists">检查名称是否重复的回调函数</param>
    /// <param name="autoSanitize">是否自动转换非法字符</param>
    /// <returns>实例名称，如果取消则返回 null</returns>
    public static string? Show(
        Window owner,
        string defaultName = "",
        Func<string, bool>? checkNameExists = null,
        bool autoSanitize = true)
    {
        var dialog = new InstanceNameDialog
        {
            Owner = owner,
            CheckNameExists = checkNameExists,
            AutoSanitize = autoSanitize
        };

        // 设置默认值
        dialog.InstanceNameTextBox.Text = defaultName;

        if (dialog.ShowDialog() == true)
        {
            return dialog.InstanceName;
        }

        return null;
    }
}
