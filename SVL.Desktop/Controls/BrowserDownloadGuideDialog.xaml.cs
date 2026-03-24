using System;
using SVL.Core.IO;
using System.Windows;
using System.Windows.Threading;
using SVL.Core.Logging;

namespace SVL.Desktop.Controls;

/// <summary>
/// 浏览器下载引导对话框的交互逻辑
/// </summary>
public partial class BrowserDownloadGuideDialog : Window
{
    private readonly long _modId;
    private readonly long _fileId;
    private readonly string _gameId;

    public BrowserDownloadGuideDialog(long modId, long fileId, string gameId)
    {
        InitializeComponent();
        _modId = modId;
        _fileId = fileId;
        _gameId = gameId;
    }

    /// <summary>
    /// 显示对话框（带 Blur 效果）
    /// </summary>
    public bool? ShowWithBlur(Window owner = null)
    {
        if (owner != null)
            Owner = owner;

        // 保留方法名以兼容现有调用，但不再启用主窗口 Blur。
        return ShowDialog();
    }

    /// <summary>
    /// 打开浏览器下载页面
    /// </summary>
    private void OpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 构造 NexusMods 具体文件下载页面 URL（包含 file_id 和 nmm=1 参数）
            var downloadUrl = $"https://www.nexusmods.com/{_gameId}/mods/{_modId}?tab=files&file_id={_fileId}&nmm=1";

            Log.Info($"[BrowserDownloadGuideDialog] 打开浏览器: {downloadUrl}");

            // 使用默认浏览器打开
            ProcessEx.OpenUrl(downloadUrl);

            // 关闭对话框
            DialogResult = true;
            Close();

            // 在对话框关闭并移除 blur 后再显示通知，确保层级正确
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                FloatingNotificationControl.Show(
                    title: "浏览器已打开",
                    message: "请在浏览器中点击「Slow Download」进行下载。\n\nSVL 将自动接收下载。",
                    autoCloseDelay: 5000
                );
            }), DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[BrowserDownloadGuideDialog] 打开浏览器失败");

            DialogResult = false;
            Close();

            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                FloatingNotificationControl.Show(
                    title: "打开浏览器失败",
                    message: $"无法打开浏览器：{ex.Message}\n\n请手动访问 NexusMods 网站下载。",
                    autoCloseDelay: 8000,
                    notificationType: NotificationType.Error
                );
            }), DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// 取消/稍后下载
    /// </summary>
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Log.Info("[BrowserDownloadGuideDialog] 用户选择稍后下载");
        DialogResult = false;
        Close();
    }
}
