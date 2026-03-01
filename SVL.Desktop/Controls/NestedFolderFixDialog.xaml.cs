using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SVL.Core.Logging;
using SVL.Core.Stardew.Mod;
using ModIssueType = SVL.Core.Stardew.Mod.ModManager.NestedFolderIssueType;

namespace SVL.Desktop.Controls;

/// <summary>
/// 嵌套文件夹修复对话框
/// </summary>
public partial class NestedFolderFixDialog : Window
{
    private readonly List<ModManager.NestedFolderIssue> _issues;
    private readonly IModManager _modManager;
    private readonly Action _onFixed;
    private int _fixedCount = 0;

    public NestedFolderFixDialog(
        List<ModManager.NestedFolderIssue> issues,
        IModManager modManager,
        Action onFixed)
    {
        InitializeComponent();
        _issues = issues;
        _modManager = modManager;
        _onFixed = onFixed;

        // 更新说明
        SummaryTextBlock.Text = $"检测到 {_issues.Count} 个 MOD 的安装结构存在问题（MOD 文件在嵌套文件夹中）。建议自动修复以正确显示 MOD 信息。";

        // 显示问题列表
        ShowIssues();
    }

    /// <summary>
    /// 显示问题列表
    /// </summary>
    private void ShowIssues()
    {
        IssuesPanel.Children.Clear();

        // 分类统计
        int nestedCount = 0;
        int noManifestCount = 0;
        int parseErrorCount = 0;
        int fixableCount = 0;

        int index = 1;
        foreach (var issue in _issues)
        {
            // 统计类型
            switch ((ModIssueType)issue.IssueType)
            {
                case ModIssueType.NestedManifest:
                    nestedCount++;
                    fixableCount++;
                    break;
                case ModIssueType.NoManifest:
                    noManifestCount++;
                    break;
                case ModIssueType.ManifestParseError:
                    parseErrorCount++;
                    break;
            }

            var border = new Border
            {
                Padding = new Thickness(0, 12, 0, 12),
                Margin = new Thickness(0),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 220, 220)),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };

            var stackPanel = new StackPanel();

            // 根据问题类型选择图标和颜色
            string icon = "⚠";
            var titleColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 140, 0));
            string issueDescription = string.Empty;

            switch ((ModIssueType)issue.IssueType)
            {
                case ModIssueType.NestedManifest:
                    icon = "📁";
                    titleColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 130, 246));
                    issueDescription = $"MOD 文件在嵌套文件夹中（深度: {issue.NestingDepth} 层）";
                    break;
                case ModIssueType.NoManifest:
                    icon = "❓";
                    titleColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(128, 128, 128));
                    issueDescription = "未找到 manifest.json 文件";
                    break;
                case ModIssueType.ManifestParseError:
                    icon = "⚠";
                    titleColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 140, 0));
                    issueDescription = $"manifest.json 解析失败: {issue.ParseErrorMessage}";
                    break;
            }

            // 序号、图标和 MOD 名称
            var titlePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 4)
            };
            var iconText = new TextBlock
            {
                Text = icon,
                FontSize = 16,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var titleBlock = new TextBlock
            {
                Text = $"{index}. {issue.ModName}",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = titleColor,
                VerticalAlignment = VerticalAlignment.Center
            };
            titlePanel.Children.Add(iconText);
            titlePanel.Children.Add(titleBlock);
            stackPanel.Children.Add(titlePanel);

            // 问题描述
            var descBlock = new TextBlock
            {
                Text = issueDescription,
                FontSize = 12,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(128, 128, 128)),
                Margin = new Thickness(0, 0, 0, 4)
            };
            stackPanel.Children.Add(descBlock);

            // 路径信息（仅对嵌套类型）
            if (issue.IssueType == ModIssueType.NestedManifest)
            {
                var pathBlock = new TextBlock
                {
                    Text = $"当前: {issue.ParentFolderName}/{issue.NestedFolderName}",
                    FontSize = 12,
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(128, 128, 128)),
                    Margin = new Thickness(0, 0, 0, 4)
                };
                stackPanel.Children.Add(pathBlock);

                // 修复后路径
                var fixedPathBlock = new TextBlock
                {
                    Text = $"修复后: {issue.ParentFolderName}",
                    FontSize = 12,
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 130, 246)),
                    Margin = new Thickness(0, 0, 0, 4)
                };
                stackPanel.Children.Add(fixedPathBlock);
            }

            // 是否可修复的说明
            string fixStatus = issue.IssueType == ModIssueType.NestedManifest
                ? "✓ 可自动修复"
                : "✗ 需要手动处理";

            var fixStatusBlock = new TextBlock
            {
                Text = fixStatus,
                FontSize = 11,
                Foreground = issue.IssueType == ModIssueType.NestedManifest
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 67, 54)),
                Margin = new Thickness(0, 0, 0, 4)
            };
            stackPanel.Children.Add(fixStatusBlock);

            // 如果有其他文件，显示警告
            if (issue.HasOtherFiles && issue.IssueType == ModIssueType.NestedManifest)
            {
                var warningBlock = new TextBlock
                {
                    Text = "⚠ 源文件夹包含其他文件（如 changelog.txt），修复后需要手动决定是否删除",
                    FontSize = 11,
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 140, 0)),
                    Margin = new Thickness(0, 0, 0, 4)
                };
                stackPanel.Children.Add(warningBlock);
            }

            // 为所有问题添加打开文件夹按钮
            var openButton = new Button
            {
                Content = "📂 打开所在文件夹",
                FontSize = 11,
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 4, 0, 0),
                Background = System.Windows.Media.Brushes.Transparent,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 130, 246)),
                BorderThickness = new Thickness(1),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 130, 246)),
                Cursor = System.Windows.Input.Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left,
                Tag = issue.ParentFolderPath
            };
            openButton.Click += (s, e) =>
            {
                try
                {
                    var path = (string)((Button)s).Tag;
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{path}\"",
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    Log.Error("[NestedFolderFixDialog] 打开文件夹失败", ex);
                }
            };
            stackPanel.Children.Add(openButton);

            border.Child = stackPanel;
            IssuesPanel.Children.Add(border);
            index++;
        }

        // 更新摘要信息
        var summary = $"检测到 {_issues.Count} 个 MOD 安装问题：\n" +
                       $"• 可自动修复: {fixableCount} 个（嵌套文件夹）\n" +
                       $"• 需手动处理: {noManifestCount + parseErrorCount} 个";

        if (noManifestCount > 0)
            summary += $"\n  - 未找到 manifest.json: {noManifestCount} 个";
        if (parseErrorCount > 0)
            summary += $"\n  - manifest.json 解析失败: {parseErrorCount} 个";

        SummaryTextBlock.Text = summary;

        // 只有当有可修复的问题时才显示修复按钮
        FixButton.Visibility = fixableCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        FixButton.Content = fixableCount > 0 ? $"自动修复 ({fixableCount})" : "无可修复项";
        FixButton.IsEnabled = fixableCount > 0;
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void Fix_Click(object sender, RoutedEventArgs e)
    {
        FixButton.IsEnabled = false;
        SkipButton.IsEnabled = false;
        FixButton.Content = "修复中...";

        _fixedCount = 0;

        foreach (var issue in _issues)
        {
            // 只修复可自动修复的问题
            if (issue.IssueType == ModIssueType.NestedManifest)
            {
                var success = await _modManager.FixNestedFolderAsync(issue);
                if (success)
                    _fixedCount++;
            }
        }

        // 显示结果
        FixButton.Content = $"已修复 {_fixedCount}/{_issues.Count} 个可修复项";

        await Dispatcher.DelayAsync(1000);

        // 触发回调重新加载 MOD 列表
        _onFixed?.Invoke();

        DialogResult = true;
        Close();
    }
}

/// <summary>
/// Dispatcher 扩展
/// </summary>
public static class DispatcherExtensions
{
    public static System.Threading.Tasks.Task DelayAsync(this Dispatcher dispatcher, int milliseconds)
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds)
        };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            tcs.SetResult(true);
        };
        timer.Start();
        return tcs.Task;
    }
}
