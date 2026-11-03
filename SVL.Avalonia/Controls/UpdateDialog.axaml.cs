using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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
    private bool _autoDownloadOnOpen;

    public UpdateDialog()
        : this(new Version(0, 0, 0, 0), new LauncherReleaseInfo(), "-")
    {
    }

    public UpdateDialog(Version currentVersion, LauncherReleaseInfo releaseInfo, string source)
    {
        InitializeComponent();
        Opened += UpdateDialog_Opened;

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

        UpdateRuntimeDisplay(LauncherUpdateService.DetectCurrentRuntimeMode());
    }

    /// <summary>注入更新服务以支持应用内下载（由 DialogService 调用）。</summary>
    internal void InitializeForDownload(LauncherUpdateService updateService, bool autoDownload = false)
    {
        _updateService = updateService;
        UpdateRuntimeDisplay(updateService.RuntimeMode);
        _autoDownloadOnOpen = autoDownload && DownloadButton.IsVisible;
    }

    private void UpdateRuntimeDisplay(LauncherRuntimeMode runtimeMode)
    {
        RuntimeModeText.Text = $"当前包体：{LauncherUpdateService.DescribeRuntimeMode(runtimeMode)}";
        var asset = _releaseInfo == null
            ? null
            : LauncherUpdateService.SelectPreferredAsset(_releaseInfo, runtimeMode, OperatingSystem.IsWindows());
        DownloadButton.IsVisible = asset != null;
        RuntimeWarningText.IsVisible = asset == null;
    }

    private async void UpdateDialog_Opened(object? sender, EventArgs e)
    {
        if (!_autoDownloadOnOpen)
        {
            return;
        }

        _autoDownloadOnOpen = false;
        await StartDownloadAsync(automatic: true);
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
        await StartDownloadAsync();
    }

    private async Task StartDownloadAsync(bool automatic = false)
    {
        if (_updateService == null || _releaseInfo == null || _releaseInfo.Assets.Count == 0)
        {
            Close(UpdateDialogAction.OpenRelease);
            return;
        }

        var asset = _updateService.SelectPreferredAsset(_releaseInfo);
        if (asset == null)
        {
            ProgressText.Text = "没有适合当前平台的更新文件";
            return;
        }
        _downloadCts = new CancellationTokenSource();
        DownloadButton.IsEnabled = false;
        DownloadButton.Content = automatic ? "自动下载中..." : "下载中...";
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
            DownloadButton.Content = OperatingSystem.IsWindows() ? "安装并重启" : "前往发布页安装";

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
            DownloadButton.Content = automatic ? "应用内下载" : "重试下载";
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
            if (!OperatingSystem.IsWindows())
            {
                Close(UpdateDialogAction.OpenRelease);
                return;
            }
            _updateService.StartUpdateInstaller(_downloadedFilePath);
            Close(UpdateDialogAction.DownloadAndInstall);
            if (global::Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                if (desktop.MainWindow is MainWindow mainWindow) mainWindow.CloseForExit();
                else desktop.Shutdown();
            }
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
