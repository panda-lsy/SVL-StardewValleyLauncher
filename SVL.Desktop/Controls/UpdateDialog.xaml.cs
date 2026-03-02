using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
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
    private System.Threading.CancellationTokenSource? _downloadCts;

    public UpdateDialog(Version currentVersion, ReleaseInfo releaseInfo)
    {
        InitializeComponent();

        _currentVersion = currentVersion;
        _releaseInfo = releaseInfo;

        // 设置版本信息
        CurrentVersionText.Text = $"v{currentVersion}";
        NewVersionText.Text = releaseInfo.TagName;

        // 显示更新日志（只使用 Update.txt 内容）
        RenderChangelog(releaseInfo.UpdateLog);
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        // 设置 Owner 以便居中显示
        if (Owner == null)
            Owner = Application.Current.MainWindow;
    }

    /// <summary>
    /// 简单处理 Markdown 并显示为纯文本
    /// </summary>
    private void RenderChangelog(string markdown)
    {
        // 如果更新日志为空，显示默认消息
        if (string.IsNullOrWhiteSpace(markdown))
        {
            ChangelogText.Text = "暂无更新日志信息。";
            return;
        }

        try
        {
            // 简单处理 Markdown 格式
            var text = markdown;
            
            // 移除 Markdown 标题标记
            text = System.Text.RegularExpressions.Regex.Replace(text, @"^#+\s*", "", System.Text.RegularExpressions.RegexOptions.Multiline);
            
            // 移除粗体和斜体标记
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\*\*(.+?)\*\*", "$1");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\*(.+?)\*", "$1");
            
            // 移除链接，保留文本
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\[(.+?)\]\(.+?\)", "$1");
            
            // 移除代码块标记
            text = System.Text.RegularExpressions.Regex.Replace(text, @"```.+?```", "", System.Text.RegularExpressions.RegexOptions.Singleline);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"`(.+?)`", "$1");
            
            // 清理多余空行
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\n{3,}", "\n\n");
            
            ChangelogText.Text = text.Trim();
            
            Log.Debug($"[UpdateDialog] 更新日志已渲染，长度: {text.Length}");
        }
        catch (Exception ex)
        {
            Log.Error($"[UpdateDialog] 渲染更新日志失败: {ex.Message}");
            ChangelogText.Text = markdown;
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
        _downloadCts = new System.Threading.CancellationTokenSource();

        try
        {
            // 隐藏按钮区域，显示下载进度
            ButtonPanel.Visibility = Visibility.Collapsed;
            DownloadProgressPanel.Visibility = Visibility.Visible;
            ChangelogPanel.MaxHeight = 180;

            // 禁用窗口关闭
            Closing += OnWindowClosing;

            // 只查找 EXE 文件（只发布 EXE）
            var downloadAsset = _releaseInfo.Assets.FirstOrDefault(a => 
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

            // 如果没有 EXE，尝试查找任何可下载的资源
            if (downloadAsset == null && _releaseInfo.Assets.Count > 0)
            {
                downloadAsset = _releaseInfo.Assets[0];
            }

            if (downloadAsset == null)
            {
                throw new InvalidOperationException("该版本没有可下载的更新包。\n请前往 GitHub 发布页面手动下载。");
            }

            Log.Info($"[UpdateDialog] 开始下载更新: {downloadAsset.BrowserDownloadUrl}");

            // 下载更新文件
            _downloadedFilePath = await DownloadUpdateAsync(downloadAsset.BrowserDownloadUrl, _downloadCts.Token);

            // 下载完成，显示重启按钮
            DownloadProgressPanel.Visibility = Visibility.Collapsed;
            RestartPanel.Visibility = Visibility.Visible;
            ChangelogPanel.MaxHeight = 220;

            Log.Info($"[UpdateDialog] 更新下载完成: {_downloadedFilePath}");
        }
        catch (OperationCanceledException)
        {
            Log.Info("[UpdateDialog] 下载已取消");

            // 解除 Closing 事件
            Closing -= OnWindowClosing;
            _isDownloading = false;
            _downloadCts?.Dispose();
            _downloadCts = null;

            // 删除临时文件
            if (!string.IsNullOrEmpty(_downloadedFilePath) && File.Exists(_downloadedFilePath))
            {
                try { File.Delete(_downloadedFilePath); }
                catch { }
                _downloadedFilePath = null;
            }

            // 关闭对话框
            DialogResult = false;
            Close();
        }
        catch (Exception ex)
        {
            Log.Error($"[UpdateDialog] 下载更新失败: {ex.Message}");

            // 先解除 Closing 事件，避免 NullReferenceException
            Closing -= OnWindowClosing;
            _isDownloading = false;
            _downloadCts?.Dispose();
            _downloadCts = null;

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

            // 恢复按钮区域
            ButtonPanel.Visibility = Visibility.Visible;
            DownloadProgressPanel.Visibility = Visibility.Collapsed;
            ChangelogPanel.MaxHeight = 280;

            // 使用 Dispatcher 延迟显示错误，确保 UI 状态完全稳定
            await Dispatcher.BeginInvoke(async () =>
            {
                // 再次等待确保所有 UI 更新完成
                await Task.Delay(100);
                
                try
                {
                    // 使用自定义 ConfirmDialog
                    var result = ConfirmDialog.Show(
                        this,
                        "下载更新失败，是否前往发布页面手动下载？",
                        "下载更新失败",
                        ex.Message,
                        "前往下载",
                        "取消");

                    if (result)
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = _releaseInfo.HtmlUrl,
                            UseShellExecute = true
                        });
                        DialogResult = false;
                        Close();
                    }
                }
                catch (Exception dialogEx)
                {
                    Log.Error($"[UpdateDialog] 显示错误对话框失败: {dialogEx.Message}");
                }
            }, DispatcherPriority.ContextIdle);
        }
    }

    private async Task<string> DownloadUpdateAsync(string downloadUrl, System.Threading.CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "SVL_Update");
        Directory.CreateDirectory(tempPath);

        var fileName = Path.GetFileName(new Uri(downloadUrl).LocalPath);
        if (string.IsNullOrEmpty(fileName))
            fileName = $"SVL_Update_{_releaseInfo.TagName}.exe";

        var filePath = Path.Combine(tempPath, fileName);

        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromMinutes(30);

        // 添加 User-Agent
        httpClient.DefaultRequestHeaders.Add("User-Agent", "SVL-Launcher");

        using var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
            cancellationToken.ThrowIfCancellationRequested();
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

    private void CancelDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadCts != null && !_downloadCts.IsCancellationRequested)
        {
            _downloadCts.Cancel();
            Log.Info("[UpdateDialog] 用户取消下载");
        }
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
            // 标记正在为更新而退出（跳过 Debug 控制台的等待按键）
            App.MarkExitingForUpdate();

            // 判断是否为 Release 版本（非 Debug 构建）
            // Release 版本静默更新，Debug 版本显示控制台
            bool isSilentUpdate = !LauncherUpdateService.IsDebugBuild;

            // 创建更新脚本
            var scriptPath = CreateUpdateScript(_downloadedFilePath, isSilentUpdate);

            // 启动更新脚本
            var processInfo = new ProcessStartInfo
            {
                FileName = scriptPath,
                UseShellExecute = true,
                // Release 版本隐藏窗口，Debug 版本显示窗口
                WindowStyle = isSilentUpdate ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
                // 静默更新时创建新窗口但不显示
                CreateNoWindow = isSilentUpdate
            };

            Process.Start(processInfo);

            Log.Info($"[UpdateDialog] 启动更新脚本: {scriptPath}, 静默模式: {isSilentUpdate}");

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
    /// 创建更新脚本（批处理文件）- EXE 直接复制替换
    /// </summary>
    /// <param name="updateFilePath">更新文件路径</param>
    /// <param name="silent">是否静默模式（不显示控制台窗口）</param>
    private string CreateUpdateScript(string updateFilePath, bool silent = false)
    {
        var currentExePath = Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("无法获取当前程序路径");

        var scriptPath = Path.Combine(Path.GetTempPath(), "SVL_Update.bat");
        var currentDir = Path.GetDirectoryName(currentExePath) ?? "";
        var currentExeName = Path.GetFileName(currentExePath);
        
        // 获取新版本号，用于重命名文件（从 TagName 解析，如 "v1.1.3" -> "1.1.3"）
        var versionStr = _releaseInfo.TagName.TrimStart('v');
        var newExeName = $"SVL.Desktop_v{versionStr}.exe";
        var newExePath = Path.Combine(currentDir, newExeName);

        // 更新日志内容（用于更新完成后显示）
        var updateLogEscaped = (_releaseInfo.UpdateLog ?? _releaseInfo.Body ?? "更新完成")
            .Replace("\"", "'")
            .Replace("\r\n", " ")
            .Replace("\n", " ");

        string scriptContent;
        
        if (silent)
        {
            // 静默模式：不显示任何窗口，出错时弹窗提示
            // 使用 PowerShell 启动新进程，更可靠地处理路径和参数
            scriptContent = $@"@echo off
setlocal EnableDelayedExpansion
chcp 65001 >nul
REM 静默更新脚本

REM 等待进程完全退出（增加到5秒确保完全关闭）
echo 等待程序关闭... >nul
timeout /t 5 /nobreak >nul

REM 强制结束可能残留的进程
taskkill /f /im ""{currentExeName}"" >nul 2>&1

REM 再等待1秒
timeout /t 1 /nobreak >nul

REM 复制新文件到临时位置
echo 正在更新文件... >nul
copy /Y ""{updateFilePath}"" ""{newExePath}"" >nul 2>&1

if !errorlevel! neq 0 (
    REM 复制失败，尝试直接覆盖原文件
    copy /Y ""{updateFilePath}"" ""{currentExePath}"" >nul 2>&1
    if !errorlevel! neq 0 (
        mshta vbscript:Execute(""CreateObject(""WScript.Shell"").Popup(""更新失败：无法复制文件。"" & vbCrLf & ""请手动将以下文件复制到程序目录："" & vbCrLf & ""{updateFilePath}"", 0, ""SVL 更新错误"", 16):close"")
        exit /b 1
    )
    set ""FINAL_EXE={currentExePath}""
) else (
    set ""FINAL_EXE={newExePath}""
)

REM 更新成功，启动程序（带 --updated 参数表示刚完成更新）
REM 使用 PowerShell 启动，更可靠地处理路径和参数
powershell -WindowStyle Hidden -Command ""Start-Process -FilePath '!FINAL_EXE!' -ArgumentList '--updated'""

REM 清理临时文件
del ""{updateFilePath}"" 2>nul
del ""%~f0"" 2>nul

exit
";
        }
        else
        {
            // 调试模式：显示控制台窗口，方便查看更新过程
            scriptContent = $@"@echo off
setlocal EnableDelayedExpansion
chcp 65001 >nul
title SVL 更新程序
echo ========================================
echo   SVL 启动器自动更新程序
echo ========================================
echo.
echo 正在关闭启动器...

REM 等待进程完全退出（增加到5秒确保完全关闭）
timeout /t 5 /nobreak >nul

REM 强制结束可能残留的进程
taskkill /f /im ""{currentExeName}"" >nul 2>&1

REM 再等待1秒
timeout /t 1 /nobreak >nul

echo 正在复制更新文件...
echo 新版本: {versionStr}
echo 目标文件: {newExeName}

REM 复制新文件到带版本号的文件名
copy /Y ""{updateFilePath}"" ""{newExePath}"" >nul

if !errorlevel! neq 0 (
    echo.
    echo [警告] 无法创建版本化文件名，尝试直接覆盖...
    copy /Y ""{updateFilePath}"" ""{currentExePath}"" >nul
    if !errorlevel! neq 0 (
        echo.
        echo [错误] 复制文件失败！
        echo 请手动将下载的文件复制到程序目录。
        echo 下载位置: {updateFilePath}
        pause
        exit /b 1
    )
    set ""FINAL_EXE={currentExePath}""
) else (
    echo 成功创建: {newExeName}
    set ""FINAL_EXE={newExePath}""
)

echo.
echo 更新完成！正在启动启动器...
timeout /t 2 /nobreak >nul

REM 启动程序（带 --updated 参数表示刚完成更新）
echo 启动: !FINAL_EXE!
powershell -Command ""Start-Process -FilePath '!FINAL_EXE!' -ArgumentList '--updated'""

REM 清理临时文件
del ""{updateFilePath}"" 2>nul
del ""%~f0"" 2>nul

exit
";
        }

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
