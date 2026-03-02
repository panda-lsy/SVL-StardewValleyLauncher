using System;
using System.Windows;
using System.Windows.Input;
using SVL.Core.Logging;

namespace SVL.Desktop.Controls;

/// <summary>
/// 更新完成对话框
/// </summary>
public partial class UpdateCompleteDialog : Window
{
    public UpdateCompleteDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 设置版本号标题
    /// </summary>
    /// <param name="version">版本号（如 "1.1.3.1" 或 "v1.1.3.1"）</param>
    public void SetVersion(string version)
    {
        // 确保 version 带有 v 前缀
        var displayVersion = version.StartsWith("v", StringComparison.OrdinalIgnoreCase) 
            ? version 
            : $"v{version}";
        
        TitleTextBlock.Text = $"已更新到 {displayVersion} 版本";
        Title = $"更新完成 - {displayVersion}";
    }

    /// <summary>
    /// 设置更新日志内容
    /// </summary>
    /// <param name="updateLog">更新日志文本（支持 Markdown 格式会自动转换）</param>
    public void SetUpdateLog(string updateLog)
    {
        if (string.IsNullOrWhiteSpace(updateLog))
        {
            UpdateLogTextBlock.Text = "暂无更新日志";
            return;
        }

        // 简单处理 Markdown 格式
        var formattedLog = FormatUpdateLog(updateLog);
        UpdateLogTextBlock.Text = formattedLog;
    }

    /// <summary>
    /// 格式化更新日志（简单处理常见格式）
    /// </summary>
    private static string FormatUpdateLog(string rawLog)
    {
        if (string.IsNullOrWhiteSpace(rawLog))
            return "暂无更新日志";

        var lines = rawLog.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        var result = new System.Text.StringBuilder();

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();

            // 跳过空行
            if (string.IsNullOrWhiteSpace(trimmedLine))
            {
                result.AppendLine();
                continue;
            }

            // 处理标题（# ## ###）
            if (trimmedLine.StartsWith("### "))
            {
                result.AppendLine($"【{trimmedLine.Substring(4).Trim()}】");
            }
            else if (trimmedLine.StartsWith("## "))
            {
                result.AppendLine();
                result.AppendLine($"■ {trimmedLine.Substring(3).Trim()}");
                result.AppendLine();
            }
            else if (trimmedLine.StartsWith("# "))
            {
                result.AppendLine();
                result.AppendLine($"▶ {trimmedLine.Substring(2).Trim()}");
                result.AppendLine();
            }
            // 处理列表项（- 或 *）
            else if (trimmedLine.StartsWith("- ") || trimmedLine.StartsWith("* "))
            {
                result.AppendLine($"  • {trimmedLine.Substring(2).Trim()}");
            }
            // 处理数字列表（1. 2. 等）
            else if (System.Text.RegularExpressions.Regex.IsMatch(trimmedLine, @"^\d+\. "))
            {
                var match = System.Text.RegularExpressions.Regex.Match(trimmedLine, @"^(\d+)\. (.*)$");
                if (match.Success)
                {
                    result.AppendLine($"  {match.Groups[1].Value}. {match.Groups[2].Value.Trim()}");
                }
                else
                {
                    result.AppendLine($"  {trimmedLine}");
                }
            }
            // 普通文本
            else
            {
                result.AppendLine(trimmedLine);
            }
        }

        return result.ToString().Trim();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        // 设置焦点到确定按钮
        OkButton.Focus();
        
        // 应用模糊效果到父窗口
        if (Owner is MainWindow mainWindow)
        {
            mainWindow.ApplyBlurEffect();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        
        // 移除父窗口的模糊效果
        if (Owner is MainWindow mainWindow)
        {
            mainWindow.RemoveBlurEffect();
        }
    }

    /// <summary>
    /// 显示更新完成对话框
    /// </summary>
    /// <param name="owner">父窗口</param>
    /// <param name="version">版本号</param>
    /// <param name="updateLog">更新日志</param>
    public static void ShowDialog(Window? owner, string version, string updateLog)
    {
        try
        {
            var dialog = new UpdateCompleteDialog();
            dialog.SetVersion(version);
            dialog.SetUpdateLog(updateLog);

            if (owner != null)
            {
                dialog.Owner = owner;
            }

            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[UpdateCompleteDialog] 显示更新完成对话框失败");
        }
    }
}
