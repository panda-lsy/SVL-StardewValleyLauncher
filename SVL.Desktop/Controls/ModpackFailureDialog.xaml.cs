using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Microsoft.Win32;
using SVL.Core.Download;
using SVL.Core.IO;

namespace SVL.Desktop.Controls;

/// <summary>
/// 整合包安装失败汇总对话框
/// </summary>
public partial class ModpackFailureDialog : Window
{
    private const int PageSize = 5;
    private readonly List<FailedModInfo> _failedMods;
    private int _currentPage = 0;
    private int _totalPages = 1;

    public ModpackFailureDialog(List<FailedModInfo> failedMods, string modpackName)
    {
        InitializeComponent();
        _failedMods = failedMods.OrderBy(m => m.ProjectId).ToList();

        // 截断过长的整合包名称（最多 30 个字符）
        var displayName = modpackName.Length > 30
            ? modpackName.Substring(0, 27) + "..."
            : modpackName;

        // 检测主要平台（取第一个失败的 mod 的平台）
        var primaryPlatform = _failedMods.FirstOrDefault()?.Platform ?? "Curseforge";
        var isNexusMods = primaryPlatform.Equals("NexusMods", StringComparison.OrdinalIgnoreCase) ||
                         primaryPlatform.Equals("Nexus", StringComparison.OrdinalIgnoreCase);

        // 更新标题和说明
        TitleTextBlock.Text = $"⚠ {displayName} - 部分模组安装失败";
        SummaryTextBlock.Text = $"整合包已安装完成，但有 {_failedMods.Count} 个模组下载失败。您可以点击下方链接访问 {(isNexusMods ? "NexusMods" : "Curseforge")} 手动下载这些模组。";

        // 计算总页数
        _totalPages = (int)Math.Ceiling(_failedMods.Count / (double)PageSize);

        // 显示第一页
        ShowPage(0);

        // 如果有多页，显示分页控件
        if (_totalPages > 1)
        {
            PaginationPanel.Visibility = Visibility.Visible;
            UpdatePaginationButtons();
        }
    }

    /// <summary>
    /// 显示指定页的失败模组
    /// </summary>
    private void ShowPage(int pageIndex)
    {
        FailedModsPanel.Children.Clear();

        int startIndex = pageIndex * PageSize;
        int endIndex = Math.Min(startIndex + PageSize, _failedMods.Count);

        for (int i = startIndex; i < endIndex; i++)
        {
            var mod = _failedMods[i];
            var modItem = CreateModItem(mod, i + 1);
            FailedModsPanel.Children.Add(modItem);
        }

        _currentPage = pageIndex;
        UpdatePaginationButtons();
    }

    /// <summary>
    /// 创建单个模组显示项
    /// </summary>
    private Border CreateModItem(FailedModInfo mod, int index)
    {
        // 判断平台
        var isNexusMods = mod.Platform.Equals("NexusMods", StringComparison.OrdinalIgnoreCase) ||
                         mod.Platform.Equals("Nexus", StringComparison.OrdinalIgnoreCase);

        // 模组名称和 ID 标识
        var idBadge = new Border
        {
            Background = FindResource("ColorBrush2") as System.Windows.Media.Brush,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 8)
        };
        var idText = new TextBlock
        {
            Text = $"{(isNexusMods ? "Nexus" : "CF")} #{mod.ProjectId}",
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.White
        };
        idBadge.Child = idText;

        // 模组名称（如果有）
        TextBlock? nameBlock = null;
        if (!string.IsNullOrEmpty(mod.ModName))
        {
            nameBlock = new TextBlock
            {
                Text = mod.ModName,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(60, 60, 60)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4)
            };
        }

        // 错误信息
        var errorBlock = new TextBlock
        {
            Text = mod.Error ?? "未知错误",
            FontSize = 13,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(198, 65, 12)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };

        // 链接按钮样式
        var link = new Hyperlink
        {
            NavigateUri = new Uri(mod.ModUrl)
        };
        link.Inlines.Add(new Run(isNexusMods ? "在 NexusMods 中查看此模组" : "在 Curseforge 中查看此模组")
        {
            FontWeight = FontWeights.SemiBold
        });
        link.RequestNavigate += (s, e) =>
        {
            try { ProcessEx.OpenUrl(e.Uri.AbsoluteUri); } catch { }
            e.Handled = true;
        };

        var linkBlock = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 4)
        };
        linkBlock.Inlines.Add(link);

        // 容器
        var stackPanel = new StackPanel
        {
            Margin = new Thickness(0)
        };
        stackPanel.Children.Add(idBadge);
        if (nameBlock != null)
            stackPanel.Children.Add(nameBlock);
        stackPanel.Children.Add(errorBlock);
        stackPanel.Children.Add(linkBlock);

        // 如果有 ZIP 文件路径，添加手动解压按钮
        if (!string.IsNullOrEmpty(mod.ZipFilePath) && File.Exists(mod.ZipFilePath))
        {
            var zipButton = new Button
            {
                Content = "📁 打开文件夹尝试手动解压",
                FontSize = 12,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 4, 0, 0),
                Background = System.Windows.Media.Brushes.Transparent,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 130, 246)),
                BorderThickness = new Thickness(1),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 130, 246)),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            zipButton.Click += (s, e) =>
            {
                try
                {
                    // 选中 ZIP 文件并打开文件夹
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{mod.ZipFilePath}\"",
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    SVL.Core.Logging.Log.Error(ex, "[ModpackFailureDialog] 打开文件夹失败");
                    SvlMessageBox.Error($"无法打开文件夹: {ex.Message}");
                }
            };
            stackPanel.Children.Add(zipButton);

            // 添加 ZIP 文件路径提示
            var zipPathBlock = new TextBlock
            {
                Text = $"ZIP 文件: {Path.GetFileName(mod.ZipFilePath)}",
                FontSize = 11,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(128, 128, 128)),
                Margin = new Thickness(0, 2, 0, 0)
            };
            stackPanel.Children.Add(zipPathBlock);
        }

        // 分隔线（最后一项不显示）
        return new Border
        {
            Child = stackPanel,
            Padding = new Thickness(0, 12, 0, 12),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 220, 220)),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
    }

    /// <summary>
    /// 更新分页按钮状态
    /// </summary>
    private void UpdatePaginationButtons()
    {
        PageInfoTextBlock.Text = $"{_currentPage + 1} / {_totalPages}";
        PrevPageButton.IsEnabled = _currentPage > 0;
        NextPageButton.IsEnabled = _currentPage < _totalPages - 1;

        // 更新按钮透明度
        PrevPageButton.Opacity = PrevPageButton.IsEnabled ? 1.0 : 0.5;
        NextPageButton.Opacity = NextPageButton.IsEnabled ? 1.0 : 0.5;
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage > 0)
        {
            ShowPage(_currentPage - 1);
        }
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage < _totalPages - 1)
        {
            ShowPage(_currentPage + 1);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    /// <summary>
    /// 导出失败模组列表
    /// </summary>
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 创建保存文件对话框
            var saveFileDialog = new SaveFileDialog
            {
                Filter = "文本文件 (*.txt)|*.txt|CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
                DefaultExt = "txt",
                FileName = $"failed_mods_{DateTime.Now:yyyyMMdd_HHmmss}"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                var filePath = saveFileDialog.FileName;
                var extension = Path.GetExtension(filePath).ToLower();

                if (extension == ".csv")
                {
                    ExportToCsv(filePath);
                }
                else
                {
                    ExportToText(filePath);
                }

                SVL.Core.Logging.Log.Info($"[ModpackFailureDialog] 已导出失败模组列表到: {filePath}");
            }
        }
        catch (Exception ex)
        {
            SVL.Core.Logging.Log.Error(ex, "[ModpackFailureDialog] 导出失败模组列表失败");
            SvlMessageBox.Error($"导出失败: {ex.Message}", "导出错误");
        }
    }

    /// <summary>
    /// 导出为文本格式
    /// </summary>
    private void ExportToText(string filePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("整合包安装失败模组列表");
        sb.AppendLine($"导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"共 {_failedMods.Count} 个模组下载失败");
        sb.AppendLine();
        sb.AppendLine(new string('=', 80));

        foreach (var mod in _failedMods.OrderBy(m => m.ProjectId))
        {
            sb.AppendLine($"平台: {mod.Platform}");
            sb.AppendLine($"模组名称: {mod.ModName ?? "未知"}");
            sb.AppendLine($"Project ID: {mod.ProjectId}");
            sb.AppendLine($"File ID: {mod.FileId}");
            sb.AppendLine($"错误信息: {mod.Error ?? "未知"}");
            sb.AppendLine($"链接: {mod.ModUrl}");
            sb.AppendLine(new string('-', 80));
        }

        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
    }

    /// <summary>
    /// 导出为 CSV 格式
    /// </summary>
    private void ExportToCsv(string filePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Platform,ModName,ProjectID,FileID,ErrorMessage,ModUrl");

        foreach (var mod in _failedMods.OrderBy(m => m.ProjectId))
        {
            var modName = mod.ModName?.Replace("\"", "\"\"") ?? "";
            var error = mod.Error?.Replace("\"", "\"\"") ?? "";
            var url = mod.ModUrl.Replace("\"", "\"\"");
            sb.AppendLine($"\"{mod.Platform}\",\"{modName}\",{mod.ProjectId},{mod.FileId},\"{error}\",\"{url}\"");
        }

        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
    }
}
