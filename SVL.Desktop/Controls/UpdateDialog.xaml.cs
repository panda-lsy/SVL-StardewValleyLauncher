using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Markdig;
using SVL.Core.App;
using SVL.Core.Config;
using SVL.Core.Logging;

namespace SVL.Desktop.Controls;

/// <summary>
/// 启动器更新对话框
/// </summary>
public partial class UpdateDialog : Window
{
    private readonly ReleaseInfo _releaseInfo;
    private readonly Version _currentVersion;
    private string? _downloadedFilePath;
    private bool _isDownloading;

    public UpdateDialog(Version currentVersion, ReleaseInfo releaseInfo)
    {
        InitializeComponent();

        _currentVersion = currentVersion;
        _releaseInfo = releaseInfo;

        // 设置版本信息
        CurrentVersionText.Text = $"v{currentVersion}";
        NewVersionText.Text = releaseInfo.TagName;

        // 渲染 Markdown 更新日志
        RenderChangelog(releaseInfo.Body);
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        // 设置 Owner 以便居中显示
        if (Owner == null)
            Owner = Application.Current.MainWindow;
    }

    /// <summary>
    /// 将 Markdown 渲染为 HTML 并显示在 WebBrowser 中
    /// </summary>
    private void RenderChangelog(string markdown)
    {
        try
        {
            // 使用 Markdig 将 Markdown 转换为 HTML
            var pipeline = new MarkdownPipelineBuilder()
                .UseAutoLinks()
                .UseTaskLists()
                .Build();
            var html = Markdig.Markdown.ToHtml(markdown, pipeline);

            // 创建完整的 HTML 文档，添加样式
            var styledHtml = $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset=""utf-8"">
    <style>
        body {{
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif;
            font-size: 13px;
            line-height: 1.6;
            color: #e0e0e0;
            background-color: #1e1e1e;
            margin: 0;
            padding: 8px;
        }}
        h1, h2, h3, h4, h5, h6 {{
            color: #ffffff;
            margin-top: 16px;
            margin-bottom: 8px;
        }}
        h1 {{ font-size: 18px; border-bottom: 1px solid #404040; padding-bottom: 8px; }}
        h2 {{ font-size: 16px; }}
        h3 {{ font-size: 14px; }}
        code {{
            background-color: #2d2d2d;
            padding: 2px 6px;
            border-radius: 3px;
            font-family: 'Consolas', 'Monaco', monospace;
            font-size: 12px;
        }}
        pre {{
            background-color: #2d2d2d;
            padding: 12px;
            border-radius: 6px;
            overflow-x: auto;
        }}
        pre code {{
            background-color: transparent;
            padding: 0;
        }}
        a {{
            color: #60a5fa;
            text-decoration: none;
        }}
        a:hover {{
            text-decoration: underline;
        }}
        ul, ol {{
            padding-left: 20px;
        }}
        li {{
            margin-bottom: 4px;
        }}
        hr {{
            border: none;
            border-top: 1px solid #404040;
            margin: 16px 0;
        }}
        blockquote {{
            border-left: 3px solid #60a5fa;
            margin: 8px 0;
            padding-left: 12px;
            color: #a0a0a0;
        }}
    </style>
</head>
<body>
{html}
</body>
</html>";

            // 在 WebBrowser 中显示 HTML
            ChangelogBrowser.NavigateToString(styledHtml);
        }
        catch (Exception ex)
        {
            Log.Error($"[UpdateDialog] 渲染更新日志失败: {ex.Message}");
            // 如果渲染失败，显示原始 Markdown
            ChangelogBrowser.NavigateToString($"<html><body><pre>{markdown}</pre></body></html>");
        }
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void SkipVersion_Click(object sender, RoutedEventArgs e)
    {
        // 保存"跳过此版本"设置
        var settings = AppConfig.GetSettings();
        settings.SkippedUpdateVersion = _releaseInfo.TagName;
        AppConfig.SaveSettings(settings);

        Log.Info($"[UpdateDialog] 用户选择跳过版本 {_releaseInfo.TagName} 的更新提醒");

        DialogResult = false;
        Close();
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloading)
            return;

        _isDownloading = true;

        try
        {
            // 隐藏按钮区域，显示下载进度
            ButtonPanel.Visibility = Visibility.Collapsed;
            DownloadProgressPanel.Visibility = Visibility.Visible;
            ChangelogPanel.MaxHeight = 180;

            // 禁用窗口关闭
            Closing += OnWindowClosing;

            // 查找 ZIP 资源
            var zipAsset = _releaseInfo.Assets.FirstOrDefault(a => 
                a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

            if (zipAsset == null)
            {
                throw new InvalidOperationException("未找到可下载的更新包");
            }

            Log.Info($"[UpdateDialog] 开始下载更新: {zipAsset.BrowserDownloadUrl}");

            // 下载更新文件
            _downloadedFilePath = await DownloadUpdateAsync(zipAsset.BrowserDownloadUrl);

            // 下载完成，显示重启按钮
            DownloadProgressPanel.Visibility = Visibility.Collapsed;
            RestartPanel.Visibility = Visibility.Visible;
            ChangelogPanel.MaxHeight = 220;

            Log.Info($"[UpdateDialog] 更新下载完成: {_downloadedFilePath}");
        }
        catch (Exception ex)
        {
            Log.Error($"[UpdateDialog] 下载更新失败: {ex.Message}");

            // 恢复按钮区域
            ButtonPanel.Visibility = Visibility.Visible;
            DownloadProgressPanel.Visibility = Visibility.Collapsed;
            ChangelogPanel.MaxHeight = 280;

            _isDownloading = false;

            // 如果有下载的临时文件，删除它
            if (!string.IsNullOrEmpty(_downloadedFilePath) && File.Exists(_downloadedFilePath))
            {
                try
                {
                    File.Delete(_downloadedFilePath);
                    _downloadedFilePath = null;
                }
                catch { }
            }

            MessageBox.Show($"下载更新失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task<string> DownloadUpdateAsync(string downloadUrl)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "SVL_Update");
        Directory.CreateDirectory(tempPath);

        var fileName = Path.GetFileName(new Uri(downloadUrl).LocalPath);
        if (string.IsNullOrEmpty(fileName) || !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            fileName = $"SVL_Update_{_releaseInfo.TagName}.zip";

        var filePath = Path.Combine(tempPath, fileName);

        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromMinutes(30);

        // 添加 User-Agent
        httpClient.DefaultRequestHeaders.Add("User-Agent", "SVL-Launcher");

        using var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        var totalBytesRead = 0L;

        using var contentStream = await response.Content.ReadAsStreamAsync();
        using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[8192];
        int bytesRead;

        // 更新 UI 的计时器
        var lastUpdate = DateTime.Now;

        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, bytesRead);
            totalBytesRead += bytesRead;

            // 每 100ms 更新一次 UI
            if ((DateTime.Now - lastUpdate).TotalMilliseconds > 100)
            {
                lastUpdate = DateTime.Now;

                await Dispatcher.BeginInvoke(() =>
                {
                    if (totalBytes > 0)
                    {
                        var percentage = (double)totalBytesRead / totalBytes * 100;
                        DownloadProgressBar.Value = percentage;
                        DownloadProgressText.Text = $"{percentage:F0}%";

                        var downloadedMB = totalBytesRead / 1024.0 / 1024.0;
                        var totalMB = totalBytes / 1024.0 / 1024.0;
                        DownloadStatusText.Text = $"正在下载更新... ({downloadedMB:F1} MB / {totalMB:F1} MB)";
                    }
                    else
                    {
                        var downloadedMB = totalBytesRead / 1024.0 / 1024.0;
                        DownloadStatusText.Text = $"正在下载更新... ({downloadedMB:F1} MB)";
                        DownloadProgressText.Text = $"{downloadedMB:F1} MB";
                    }
                }, DispatcherPriority.Background);
            }
        }

        await Dispatcher.BeginInvoke(() =>
        {
            DownloadProgressBar.Value = 100;
            DownloadProgressText.Text = "100%";
            DownloadStatusText.Text = "下载完成";
        });

        return filePath;
    }

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_downloadedFilePath) || !File.Exists(_downloadedFilePath))
        {
            MessageBox.Show("更新文件不存在，请重新下载。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        try
        {
            // 创建更新脚本
            var scriptPath = CreateUpdateScript(_downloadedFilePath);

            // 启动更新脚本
            var processInfo = new ProcessStartInfo
            {
                FileName = scriptPath,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            };

            Process.Start(processInfo);

            Log.Info($"[UpdateDialog] 启动更新脚本: {scriptPath}");

            // 关闭应用程序
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error($"[UpdateDialog] 启动更新失败: {ex.Message}");
            MessageBox.Show($"启动更新失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 创建更新脚本（批处理文件）
    /// </summary>
    private string CreateUpdateScript(string zipFilePath)
    {
        var currentExePath = Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("无法获取当前程序路径");

        var appDirectory = Path.GetDirectoryName(currentExePath)
            ?? throw new InvalidOperationException("无法获取程序目录");

        var scriptPath = Path.Combine(Path.GetTempPath(), "SVL_Update.bat");

        // 批处理脚本内容
        var scriptContent = $@"@echo off
chcp 65001 >nul
title SVL 更新程序
echo ========================================
echo   SVL 启动器自动更新程序
echo ========================================
echo.
echo 正在关闭启动器...

REM 等待进程完全退出
timeout /t 3 /nobreak >nul

echo 正在解压更新文件...
powershell -Command ""Expand-Archive -Path '{zipFilePath}' -DestinationPath '{appDirectory}' -Force""

if %errorlevel% neq 0 (
    echo.
    echo [错误] 解压失败！
    pause
    exit /b 1
)

echo.
echo 更新完成！正在启动启动器...
timeout /t 2 /nobreak >nul

start "" ""{currentExePath}""

REM 清理临时文件
del ""{zipFilePath}"" 2>nul
del ""%~f0"" 2>nul

exit
";

        File.WriteAllText(scriptPath, scriptContent);

        return scriptPath;
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 如果正在下载，阻止关闭
        if (_isDownloading && RestartPanel.Visibility != Visibility.Visible)
        {
            e.Cancel = true;
        }
    }
}
