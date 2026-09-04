using Avalonia.Controls;
using Avalonia.Interactivity;
using SVL.Avalonia.Models;
using SVL.Avalonia.Services;
using System.Threading;

namespace SVL.Avalonia.Controls;

public partial class UpdateDialog : Window
{
    private LauncherReleaseInfo? _releaseInfo;
    private LauncherUpdateService? _updateService;
    private CancellationTokenSource? _downloadCts;
    private string? _downloadedFilePath;

    public UpdateDialog()
        : this(new Version(0, 0, 0, 0), new LauncherReleaseInfo(), "-")
    {
    }

    public UpdateDialog(Version currentVersion, LauncherReleaseInfo releaseInfo, string source)
    {
        InitializeComponent();

        _releaseInfo = releaseInfo;

        VersionText.Text = $"v{currentVersion} -> {releaseInfo.TagName}";
        SourceText.Text = string.IsNullOrWhiteSpace(source) ? "-" : source;
        PublishedAtText.Text = releaseInfo.PublishedAt == DateTime.MinValue
            ? "-"
            : releaseInfo.PublishedAt.ToString("yyyy-MM-dd HH:mm");

        if (!string.IsNullOrWhiteSpace(releaseInfo.UpdateLog))
        {
            ChangelogText.Text = releaseInfo.UpdateLog.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(releaseInfo.Body))
        {
            ChangelogText.Text = releaseInfo.Body.Trim();
        }
        else
        {
            ChangelogText.Text = "暂无更新日志";
        }

        // 无可用资产时隐藏应用内下载按钮
        var hasAsset = releaseInfo.Assets.Count > 0;
        DownloadButton.IsVisible = hasAsset;
    }

    /// <summary>注入更新服务以支持应用内下载（由 DialogService 调用）。</summary>
    internal void InitializeForDownload(LauncherUpdateService updateService)
    {
        _updateService = updateService;
    }

    private void Later_Click(object? sender, RoutedEventArgs e)
    {
        CancelDownloadIfAny();
        Close(UpdateDialogAction.Later);
    }

    private void SkipVersion_Click(object? sender, RoutedEventArgs e)
    {
        CancelDownloadIfAny();
        Close(UpdateDialogAction.SkipVersion);
    }

    private void OpenRelease_Click(object? sender, RoutedEventArgs e)
    {
        CancelDownloadIfAny();
        Close(UpdateDialogAction.OpenRelease);
    }

    private async void DownloadButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_updateService == null || _releaseInfo == null || _releaseInfo.Assets.Count == 0)
        {
            Close(UpdateDialogAction.OpenRelease);
            return;
        }

        var asset = _releaseInfo.Assets[0];
        _downloadCts = new CancellationTokenSource();
        DownloadButton.IsEnabled = false;
        DownloadButton.Content = "下载中...";
        CancelDownloadButton.IsVisible = true;
        ProgressPanel.IsVisible = true;
        DownloadProgress.Value = 0;
        ProgressText.Text = "0%";

        try
        {
            var progress = new Progress<(int Percent, long DownloadedBytes, long TotalBytes)>(p =>
            {
                DownloadProgress.Value = p.Percent;
                ProgressText.Text = $"{p.Percent}%";
                var mbDownloaded = p.DownloadedBytes / 1024.0 / 1024.0;
                var mbTotal = p.TotalBytes / 1024.0 / 1024.0;
                SizeText.Text = p.TotalBytes > 0
                    ? $"{mbDownloaded:F1} MB / {mbTotal:F1} MB"
                    : $"{mbDownloaded:F1} MB";
            });

            _downloadedFilePath = await _updateService.DownloadAssetAsync(asset, progress, _downloadCts.Token);

            ProgressText.Text = "下载完成";
            CancelDownloadButton.IsVisible = false;
            DownloadButton.Content = "安装并重启";

            // 下载完成后点击同一按钮触发安装
            DownloadButton.IsEnabled = true;
            DownloadButton.Click -= DownloadButton_Click;
            DownloadButton.Click += InstallButton_Click;
        }
        catch (OperationCanceledException)
        {
            DownloadButton.IsEnabled = true;
            DownloadButton.Content = "应用内下载";
            CancelDownloadButton.IsVisible = false;
            ProgressPanel.IsVisible = false;
        }
        catch (System.Exception ex)
        {
            DownloadButton.IsEnabled = true;
            DownloadButton.Content = "重试下载";
            CancelDownloadButton.IsVisible = false;
            ProgressText.Text = $"下载失败: {ex.Message}";
        }
    }

    private void InstallButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_updateService == null || string.IsNullOrEmpty(_downloadedFilePath))
        {
            return;
        }

        try
        {
            _updateService.StartUpdateInstaller(_downloadedFilePath);
            // 启动安装程序后关闭启动器（安装程序会接管）
            Close(UpdateDialogAction.DownloadAndInstall);
        }
        catch (System.Exception ex)
        {
            ProgressText.Text = $"启动安装失败: {ex.Message}";
        }
    }

    private void CancelDownloadButton_Click(object? sender, RoutedEventArgs e)
    {
        CancelDownloadIfAny();
        DownloadButton.IsEnabled = true;
        DownloadButton.Content = "应用内下载";
        CancelDownloadButton.IsVisible = false;
        ProgressPanel.IsVisible = false;
    }

    private void CancelDownloadIfAny()
    {
        if (_downloadCts != null && !_downloadCts.IsCancellationRequested)
        {
            _downloadCts.Cancel();
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        CancelDownloadIfAny();
        base.OnClosing(e);
    }
}
