using System;
using System.Collections.Generic;
using System.Windows;

namespace SVL.Desktop.Controls;

/// <summary>
/// 窗口标题占位符帮助对话框
/// </summary>
public partial class WindowTitleHelpDialog : Window
{
    public WindowTitleHelpDialog()
    {
        InitializeComponent();
        DataContext = this;
    }

    /// <summary>
    /// 占位符列表
    /// </summary>
    public List<PlaceholderItem> Placeholders { get; set; } = new List<PlaceholderItem>();

    /// <summary>
    /// 原版默认模板
    /// </summary>
    public string DefaultTemplate { get; set; } = string.Empty;

    /// <summary>
    /// SMAPI 默认模板
    /// </summary>
    public string SmapTemplate { get; set; } = string.Empty;

    /// <summary>
    /// 加载占位符数据
    /// </summary>
    public void LoadPlaceholders()
    {
        try
        {
            // 从 WindowTitlePlaceholderService 获取占位符
            var placeholders = SVL.Core.Stardew.Launch.WindowTitlePlaceholderService.Placeholders;

            Placeholders.Clear();
            foreach (var placeholder in placeholders.Values)
            {
                Placeholders.Add(new PlaceholderItem
                {
                    Tag = placeholder.Tag,
                    Description = placeholder.Description
                });
            }

            // 获取默认模板
            DefaultTemplate = SVL.Core.Stardew.Launch.WindowTitlePlaceholderService.GetDefaultTitleTemplate(false);
            SmapTemplate = SVL.Core.Stardew.Launch.WindowTitlePlaceholderService.GetDefaultTitleTemplate(true);
        }
        catch (Exception ex)
        {
            SVL.Core.Logging.Log.Error(ex, "[WindowTitleHelpDialog] Failed to load placeholders");
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

/// <summary>
/// 占位符项
/// </summary>
public class PlaceholderItem
{
    public string Tag { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
