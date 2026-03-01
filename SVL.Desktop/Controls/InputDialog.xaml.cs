using System;
using System.Windows;
using System.Windows.Controls;

namespace SVL.Desktop.Controls;

public partial class InputDialog : Window
{
    public string Result { get; private set; } = string.Empty;

    /// <summary>
    /// 验证函数：返回 (是否有效, 错误消息)
    /// </summary>
    private Func<string, (bool isValid, string errorMessage)>? _validateFunc;

    public InputDialog()
    {
        InitializeComponent();
        Loaded += (s, e) =>
        {
            InputTextBox.Focus();
            // 手动触发一次验证，确保默认值也被验证
            TriggerValidation();
        };
    }

    /// <summary>
    /// 手动触发验证
    /// </summary>
    private void TriggerValidation()
    {
        if (_validateFunc != null)
        {
            var (isValid, errorMessage) = _validateFunc(InputTextBox.Text);

            if (!isValid)
            {
                // 显示错误消息
                ErrorMessage.Text = errorMessage;
                ErrorMessage.Visibility = Visibility.Visible;
                OKButton.IsEnabled = false;
            }
            else
            {
                // 隐藏错误消息
                ErrorMessage.Text = "";
                ErrorMessage.Visibility = Visibility.Collapsed;
                OKButton.IsEnabled = true;
            }
        }
    }

    public InputDialog(string message, string defaultValue = "") : this()
    {
        MessageText.Text = message;
        InputTextBox.Text = defaultValue;
    }

    private void InputTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_validateFunc != null)
        {
            var (isValid, errorMessage) = _validateFunc(InputTextBox.Text);

            if (!isValid)
            {
                // 显示错误消息
                ErrorMessage.Text = errorMessage;
                ErrorMessage.Visibility = Visibility.Visible;
                OKButton.IsEnabled = false;
            }
            else
            {
                // 隐藏错误消息
                ErrorMessage.Text = "";
                ErrorMessage.Visibility = Visibility.Collapsed;
                OKButton.IsEnabled = true;
            }
        }
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        Result = InputTextBox.Text;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// 显示输入对话框并返回用户输入的值
    /// </summary>
    public static string? Show(Window owner, string message, string defaultValue = "")
    {
        var dialog = new InputDialog(message, defaultValue)
        {
            Owner = owner
        };

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

        if (result == true)
        {
            return dialog.Result;
        }

        return null;
    }

    /// <summary>
    /// 显示输入对话框并返回用户输入的值（带验证功能）
    /// </summary>
    /// <param name="owner">父窗口</param>
    /// <param name="message">提示消息</param>
    /// <param name="defaultValue">默认值</param>
    /// <param name="validateFunc">验证函数，返回 (是否有效, 错误消息)</param>
    public static string? Show(Window owner, string message, string defaultValue, Func<string, (bool isValid, string errorMessage)> validateFunc)
    {
        var dialog = new InputDialog(message, defaultValue)
        {
            Owner = owner,
            _validateFunc = validateFunc
        };

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

        if (result == true)
        {
            return dialog.Result;
        }

        return null;
    }
}
