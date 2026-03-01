using System;
using System.Windows;
using System.Windows.Input;

namespace SVL.Desktop.Controls;

/// <summary>
/// 下载进度对话框
/// </summary>
public partial class DownloadProgressDialog : Window
{
    private readonly Action<double> _updateProgress;
    private readonly Action<long, long, double> _updateProgressWithBytes;
    private readonly Action<string> _updateStatus;
    private readonly Action<string> _updateDetail;
    private System.Threading.CancellationTokenSource? _cancellationTokenSource;
    private bool _isClosed = false;

    public DownloadProgressDialog()
    {
        InitializeComponent();
        _updateProgress = UpdateProgress;
        _updateProgressWithBytes = UpdateProgressWithBytes;
        _updateStatus = SetStatus;
        _updateDetail = SetDetail;
    }

    /// <summary>
    /// 取消按钮点击事件
    /// </summary>
    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // 标记为已关闭
            _isClosed = true;

            // 立即取消操作
            _cancellationTokenSource?.Cancel();

            // 立即更新状态并关闭对话框
            StatusText.Text = "已取消";
            CancelButton.IsEnabled = false;

            // 立即关闭对话框，不等待后台任务
            Close();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DownloadProgressDialog] 取消失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 边框鼠标左键按下事件 - 用于拖动窗口
    /// </summary>
    private void OnBorderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DownloadProgressDialog] 拖动失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 更新进度（百分比）
    /// </summary>
    /// <param name="progress">进度百分比 (0-100)</param>
    public void UpdateProgress(double progress)
    {
        if (_isClosed) return;  // 对话框已关闭，不更新 UI

        if (progress < 0) progress = 0;
        if (progress > 100) progress = 100;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isClosed) return;  // 再次检查，防止竞态条件

            var width = ActualWidth - 44; // 减去 padding
            var progressWidth = width * progress / 100;

            ProgressBar.Width = progressWidth;
            ProgressText.Text = $"{Math.Round(progress)}%";
        }));
    }

    /// <summary>
    /// 更新进度（带字节数）
    /// </summary>
    /// <param name="current">当前字节数</param>
    /// <param name="total">总字节数</param>
    /// <param name="progress">进度百分比 (0-100)</param>
    public void UpdateProgressWithBytes(long current, long total, double progress)
    {
        if (_isClosed) return;  // 对话框已关闭，不更新 UI

        if (progress < 0) progress = 0;
        if (progress > 100) progress = 100;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isClosed) return;  // 再次检查，防止竞态条件

            var width = ActualWidth - 44; // 减去 padding
            var progressWidth = width * progress / 100;

            ProgressBar.Width = progressWidth;

            // 显示百分比和字节数
            var currentMB = current / 1024.0 / 1024.0;
            var totalMB = total / 1024.0 / 1024.0;
            ProgressText.Text = $"{Math.Round(progress)}% [{FormatBytes(current)} / {FormatBytes(total)}]";
        }));
    }

    /// <summary>
    /// 格式化字节数
    /// </summary>
    private string FormatBytes(long bytes)
    {
        const double KB = 1024;
        const double MB = 1024 * 1024;
        const double GB = 1024 * 1024 * 1024;

        if (bytes >= GB)
            return $"{bytes / GB:F2}GB";
        else if (bytes >= MB)
            return $"{bytes / MB:F1}MB";
        else if (bytes >= KB)
            return $"{bytes / KB:F0}KB";
        else
            return $"{bytes}B";
    }

    /// <summary>
    /// 设置状态文本
    /// </summary>
    public void SetStatus(string status)
    {
        if (_isClosed) return;  // 对话框已关闭，不更新 UI

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isClosed) return;  // 再次检查，防止竞态条件
            StatusText.Text = status;
        }));
    }

    /// <summary>
    /// 设置详细信息
    /// </summary>
    public void SetDetail(string detail)
    {
        if (_isClosed) return;  // 对话框已关闭，不更新 UI

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isClosed) return;  // 再次检查，防止竞态条件
            DetailText.Text = detail;
        }));
    }

    /// <summary>
    /// 显示下载进度对话框并执行下载操作
    /// </summary>
    /// <param name="downloadAction">下载操作</param>
    public static void ShowAndDownload(Action<Action<double>, Action<string>, Action<string>> downloadAction)
    {
        var dialog = new DownloadProgressDialog();
        dialog.Owner = Application.Current.MainWindow;

        // 应用模糊效果
        if (dialog.Owner is MainWindow mainWindow)
        {
            mainWindow.ApplyBlurEffect();
        }

        dialog.Show();

        // 在后台线程执行下载
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                downloadAction(dialog._updateProgress, dialog._updateStatus, dialog._updateDetail);
            }
            finally
            {
                // 下载完成后关闭对话框
                dialog.Dispatcher.BeginInvoke(new Action(() =>
                {
                    dialog.Close();
                }));
            }
        });
    }

    /// <summary>
    /// 显示下载进度对话框并执行异步下载操作
    /// </summary>
    /// <param name="downloadAction">异步下载操作（最后一个参数为 CancellationToken）</param>
    public static async System.Threading.Tasks.Task ShowAndDownloadAsync(
        Func<Action<long, long, double>, Action<string>, Action<string>, System.Threading.CancellationToken, System.Threading.Tasks.Task> downloadAction)
    {
        var dialog = new DownloadProgressDialog();
        dialog.Owner = Application.Current.MainWindow;

        // 应用模糊效果
        if (dialog.Owner is MainWindow mainWindow)
        {
            mainWindow.ApplyBlurEffect();
        }

        dialog.Show();

        // 创建取消令牌
        dialog._cancellationTokenSource = new System.Threading.CancellationTokenSource();
        var cancellationToken = dialog._cancellationTokenSource.Token;

        try
        {
            // 传递字节级进度回调和取消令牌
            await downloadAction(
                (current, total, progress) => dialog._updateProgressWithBytes(current, total, progress),
                dialog._updateStatus,
                dialog._updateDetail,
                cancellationToken
            );

            // 只有成功完成才关闭对话框
            if (!dialog.IsLoaded)
                return;

            dialog.Dispatcher.BeginInvoke(new Action(() =>
            {
                dialog.Close();
            }));
        }
        catch (System.OperationCanceledException)
        {
            // 用户取消操作 - 对话框已经在 OnCancelClick 中关闭
        }
        catch (Exception)
        {
            // 其他错误 - 也需要关闭对话框
            if (dialog.IsLoaded)
            {
                try
                {
                    dialog.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        dialog.Close();
                    }));
                }
                catch
                {
                    // 忽略关闭错误
                }
            }
        }
        finally
        {
            // 确保资源被释放和移除模糊效果
            dialog._cancellationTokenSource?.Dispose();
            if (dialog.Owner is MainWindow main)
            {
                main.RemoveBlurEffect();
            }
        }
    }

    /// <summary>
    /// 窗口关闭事件 - 移除模糊效果
    /// </summary>
    private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _isClosed = true;

        // 移除模糊效果
        if (Owner is MainWindow main)
        {
            main.RemoveBlurEffect();
        }
    }
}