using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SVL.Core.Config;
using SVL.Core.Download;
using SVL.Core.IO;
using SVL.Core.Logging;
using SVL.Core.Stardew.Mod;
using SVL.Core.Stardew.ResourceProject.NexusMods;
using SVL.Desktop.Controls;
using SVL.Desktop.Models;
using SVL.Desktop.Utilities;

namespace SVL.Desktop.ViewModels;

/// <summary>
/// 下载项数据模型
/// </summary>
public partial class DownloadItem : ObservableObject
{
    [ObservableProperty]
    private string _id = "";

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _author = "";

    [ObservableProperty]
    private string _version = "";

    [ObservableProperty]
    private string _description = "";

    [ObservableProperty]
    private string _thumbnail = "";

    [ObservableProperty]
    private string _downloadUrl = "";

    [ObservableProperty]
    private string _category = "";

    [ObservableProperty]
    private double _downloadSize;

    [ObservableProperty]
    private string _fileName = "";

    /// <summary>
    /// Curseforge/NexusMods 文件ID（仅 Curseforge/NexusMods 资源使用）
    /// </summary>
    [ObservableProperty]
    private long? _fileId = null;

    /// <summary>
    /// 来源标签（用于 SMAPI 全部显示）
    /// </summary>
    [ObservableProperty]
    private string _source = "GitHub";

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private List<DownloadItem> _versions = new();

    /// <summary>
    /// 图标 URL（用于异步加载）
    /// </summary>
    [ObservableProperty]
    private string _iconUrl = "";

    /// <summary>
    /// 本地缓存图标路径
    /// </summary>
    [ObservableProperty]
    private string _localIconPath = "";

    /// <summary>
    /// 异步加载并缓存图标
    /// </summary>
    public async Task LoadIconAsync()
    {
        if (string.IsNullOrWhiteSpace(IconUrl))
            return;

        // 检查缓存
        var cachedPath = SVL.Core.IO.ImageCacheService.GetCachedImagePath(IconUrl);
        if (cachedPath != null)
        {
            // 在 UI 线程上更新属性（使用高优先级确保立即更新）
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                LocalIconPath = cachedPath;
            }, System.Windows.Threading.DispatcherPriority.Render);
            return;
        }

        // 下载并缓存
        var downloadedPath = await SVL.Core.IO.ImageCacheService.DownloadAndCacheImageAsync(IconUrl);
        if (downloadedPath != null)
        {
            // 在 UI 线程上更新属性（使用高优先级确保立即更新）
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                LocalIconPath = downloadedPath;
            }, System.Windows.Threading.DispatcherPriority.Render);
        }
    }
}

/// <summary>
/// 下载页面右侧ViewModel，显示内容
/// </summary>
public partial class DownloadRightViewModel : ObservableObject
{
    private enum BrowserDownloadKind
    {
        SmapiInstall,
        ModInstall,
        ModSaveOnly,
        CollectionInstall
    }

    // 静态：跟踪等待 NXM 回调的下载任务（Mod）
    private static readonly Dictionary<(long ModId, long FileId), PendingBrowserDownload> _pendingBrowserDownloads = new();

    // 静态：跟踪等待 NXM 回调的 Collection 下载任务
    private static readonly Dictionary<(string Slug, int RevisionNumber), PendingBrowserDownload> _pendingCollectionDownloads = new();

    private MainWindowViewModel _mainViewModel;
    private DownloadCategory _currentCategory;
    private bool _hasLoadedSmapiItems;
    private bool _hasLoadedModItems;
    private bool _hasLoadedModpackItems;

    public bool IsSmapiCategory => _currentCategory == DownloadCategory.SMAPI;

    private static bool _nxmHandlerInitialized = false;

    /// <summary>
    /// 确保 NXM URL 事件处理器已注册。
    /// 必须在应用启动时调用（如 MainWindowViewModel 构造函数），
    /// 以保证在任何 NXM URL 到达前就完成订阅。
    /// </summary>
    public static void InitializeNxmHandler()
    {
        if (_nxmHandlerInitialized) return;
        _nxmHandlerInitialized = true;
        App.NxmUrlReceived += OnNxmUrlReceived;
        Log.Info("[DownloadRightViewModel] NXM URL 事件处理器已注册");
    }

    static DownloadRightViewModel()
    {
        // 确保 NXM URL 事件处理器已注册（兼容旧路径）
        InitializeNxmHandler();
    }

    /// <summary>
    /// 处理 NXM 协议回调
    /// </summary>
    private static async void OnNxmUrlReceived(object? sender, SVL.Core.Download.NexusMods.NxmUrlReceivedEventArgs e)
    {
        var nxmUrl = e.Url;
        Log.Info($"[NxmDispatch] 收到 NXM URL: Type={nxmUrl.Type}, ModId={nxmUrl.ModId}, FileId={nxmUrl.FileId}");

        // *** 如果需要置顶窗口，处理窗口置顶信号 ***
        if (e.ShouldBringToFront)
        {
            Log.Info("[NxmDispatch] 收到窗口置顶信号");
            BringMainWindowToFront();
        }

        // 处理 Test 类型的 NXM URL（用于 Wiki 测试 NXM 协议联动）
        if (nxmUrl.Type == SVL.Core.Download.NexusMods.NxmUrlType.Test)
        {
            Log.Info("[NxmDispatch] 收到 NXM 测试 URL，协议联动成功");
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                Controls.FloatingNotificationControl.Show(
                    title: "NXM 协议联动测试成功",
                    message: "SVL 已成功接收 NXM 协议回调，协议联动正常工作！",
                    autoCloseDelay: 5000,
                    notificationType: Controls.NotificationType.Success);
            });
            return;
        }

        // 处理 Collection 类型的 NXM URL
        if (nxmUrl.IsCollection)
        {
            await OnCollectionNxmUrlReceivedAsync(nxmUrl);
            return;
        }

        if (!nxmUrl.IsMod)
        {
            Log.Warn($"[NxmDispatch] 收到非 Mod 类型的 NXM URL: {nxmUrl.Type}");
            return;
        }

        // *** 检查是否有 Collection WizardTask 正在等待此 NXM URL ***
        var allTasks = SVL.Core.Download.DownloadManager.Instance.GetAllTasks();
        var wizardTasks = allTasks.OfType<SVL.Core.Download.NexusMods.NexusCollectionWizardTask>().ToList();

        if (wizardTasks.Count > 0)
        {
            Log.Info($"[NxmDispatch] 找到 {wizardTasks.Count} 个 WizardTask，逐个检查匹配...");
            foreach (var wt in wizardTasks)
            {
                Log.Debug($"[NxmDispatch]   WizardTask '{wt.Name}': Status={wt.Status}, CurrentMod={wt.CurrentMod?.Name ?? "(null)"}, " +
                          $"ModId={wt.CurrentMod?.ModId.ToString() ?? "N/A"}, FileId={wt.CurrentMod?.FileId.ToString() ?? "N/A"}");
            }
        }

        var wizardTask = wizardTasks
            .FirstOrDefault(t => t.Status == SVL.Core.Download.DownloadTaskStatus.WaitingConfirmation &&
                           t.CurrentMod?.ModId == nxmUrl.ModId &&
                           t.CurrentMod?.FileId == nxmUrl.FileId);

        if (wizardTask != null)
        {
            Log.Info($"[NxmDispatch] Collection WizardTask 正在等待此 NXM URL: ModId={nxmUrl.ModId}, FileId={nxmUrl.FileId}");
            await wizardTask.HandleNxmUrlAsync(nxmUrl);
            return;
        }

        // *** 检查是否有 ModBatchUpdateTask 正在等待此 NXM URL ***
        var batchUpdateTask = SVL.Core.Download.DownloadManager.Instance.GetAllTasks()
            .OfType<SVL.Core.Download.ModBatchUpdateTask>()
            .FirstOrDefault(t => t.Status == SVL.Core.Download.DownloadTaskStatus.WaitingConfirmation);

        if (batchUpdateTask != null && batchUpdateTask.HandleNxmUrl(nxmUrl))
        {
            Log.Info($"[NxmDispatch] ModBatchUpdateTask 正在等待此 NXM URL: ModId={nxmUrl.ModId}, FileId={nxmUrl.FileId}");
            return;
        }

        // *** 检查是否有 SmapiDownloadTask 正在等待此 NXM URL ***
        var smapiTask = SVL.Core.Download.DownloadManager.Instance.GetAllTasks()
            .OfType<SVL.Core.Download.SmapiDownloadTask>()
            .FirstOrDefault(t => t.Status == SVL.Core.Download.DownloadTaskStatus.WaitingConfirmation);

        if (smapiTask != null && smapiTask.HandleNxmUrl(nxmUrl))
        {
            Log.Info($"[NxmDispatch] SmapiDownloadTask 正在等待此 NXM URL: ModId={nxmUrl.ModId}, FileId={nxmUrl.FileId}");
            return;
        }

        // *** 检查是否有 SvlModpackInstallTask 正在等待此 NXM URL ***
        var svlModpackTask = SVL.Core.Download.DownloadManager.Instance.GetAllTasks()
            .OfType<SVL.Core.Download.SvlModpackInstallTask>()
            .FirstOrDefault(t => t.Status == SVL.Core.Download.DownloadTaskStatus.WaitingConfirmation);

        if (svlModpackTask != null && svlModpackTask.HandleNxmUrl(nxmUrl))
        {
            Log.Info($"[NxmDispatch] SvlModpackInstallTask 正在等待此 NXM URL: ModId={nxmUrl.ModId}, FileId={nxmUrl.FileId}");
            return;
        }

        // *** 检查是否有 NexusModsBrowserDownloadTask 正在等待此 NXM URL ***
        var browserDownloadTask = SVL.Core.Download.DownloadManager.Instance.GetAllTasks()
            .OfType<SVL.Core.Download.NexusMods.NexusModsBrowserDownloadTask>()
            .FirstOrDefault(t => t.Status == SVL.Core.Download.DownloadTaskStatus.WaitingConfirmation
                && t.PendingModId == nxmUrl.ModId
                && t.PendingFileId == nxmUrl.FileId);

        if (browserDownloadTask != null && browserDownloadTask.HandleNxmUrl(nxmUrl))
        {
            Log.Info($"[NxmDispatch] NexusModsBrowserDownloadTask 匹配: ModId={nxmUrl.ModId}, FileId={nxmUrl.FileId}");
            return;
        }

        var key = (nxmUrl.ModId, nxmUrl.FileId);

        if (_pendingBrowserDownloads.TryGetValue(key, out var pendingDownload))
        {
            Log.Info($"[NxmDispatch] 收到 NXM 回调（pendingBrowser）: ModId={nxmUrl.ModId}, FileId={nxmUrl.FileId}");

            // 移除待处理任务
            _pendingBrowserDownloads.Remove(key);

            // 使用 NexusMods 下载器下载文件
            try
            {
                Log.Info($"[DownloadRightViewModel] 开始下载: ModId={nxmUrl.ModId}, FileId={nxmUrl.FileId}, Key={(!string.IsNullOrEmpty(nxmUrl.Key) ? nxmUrl.Key.Substring(0, 8) + "..." : "(无)")}");

                // 创建进度回调，更新占位任务状态
                SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsService.DownloadProgressCallback progressCallback = (progress, statusMessage, bytesRead, totalBytes) =>
                {
                    // 降低日志级别：每秒更新不需要记录日志
                    // Log.Debug($"[DownloadRightViewModel] 下载进度: {progress:F0}% - {statusMessage}");

                    // 在 UI 线程上更新占位任务状态
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        SVL.Core.Download.DownloadManager.Instance.UpdateTaskStatus(
                            pendingDownload.PlaceholderTaskId,
                            status: SVL.Core.Download.DownloadTaskStatus.Downloading,
                            statusMessage: statusMessage,
                            progress: progress
                        );
                    }));
                };

                // 获取占位任务的取消令牌
                var placeholderTask = SVL.Core.Download.DownloadManager.Instance.GetTask(pendingDownload.PlaceholderTaskId);
                var cancellationToken = (placeholderTask as PlaceholderDownloadTask)?.CancellationToken ?? default;

                var success = await SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsService.DownloadModAsync(
                    nxmUrl.ModId,
                    nxmUrl.FileId,
                    pendingDownload.TempDir,
                    nxmUrl.Key ?? string.Empty,
                    nxmUrl.Expires?.ToString() ?? string.Empty,
                    progressCallback,
                    cancellationToken
                );

                if (success)
                {
                    Log.Info($"[DownloadRightViewModel] NXM 回调下载成功");

                    // 查找下载的文件
                    var expectedZipPath = System.IO.Path.Combine(pendingDownload.TempDir, $"mod_{nxmUrl.ModId}_{nxmUrl.FileId}.zip");
                    string? downloadedZipPath = null;

                    if (System.IO.File.Exists(expectedZipPath))
                    {
                        downloadedZipPath = expectedZipPath;
                        Log.Info($"[DownloadRightViewModel] 找到预期文件: {downloadedZipPath}");
                    }
                    else
                    {
                        var zipFiles = System.IO.Directory.GetFiles(pendingDownload.TempDir, "*.zip");
                        if (zipFiles.Length > 0)
                        {
                            downloadedZipPath = zipFiles[0];
                            Log.Info($"[DownloadRightViewModel] 找到 ZIP 文件: {downloadedZipPath}");
                        }
                    }

                    if (!string.IsNullOrEmpty(downloadedZipPath))
                    {
                        // *** 保存到缓存 ***
                        await NexusModsCacheService.SaveAsync(downloadedZipPath, nxmUrl.ModId, nxmUrl.FileId);

                        // 创建真正的安装任务（根据待处理类型）
                        SVL.Core.Download.DownloadTask installTask;
                        switch (pendingDownload.Kind)
                        {
                            case BrowserDownloadKind.SmapiInstall:
                                installTask = new SVL.Core.Download.SmapiDownloadTask(
                                    pendingDownload.GameBasePath,
                                    pendingDownload.InstanceName,
                                    downloadedZipPath,
                                    SVL.Core.Stardew.Mod.SMAPI.SmapiSource.NexusMods,
                                    pendingDownload.DebugMode,
                                    pendingDownload.Version  // 传递版本号（如果有）
                                );
                                break;

                            case BrowserDownloadKind.ModSaveOnly:
                            case BrowserDownloadKind.ModInstall:
                                installTask = new SVL.Core.Download.ModDownloadTask(
                                    modId: pendingDownload.ModId,
                                    modName: pendingDownload.ModName,
                                    fileName: string.IsNullOrWhiteSpace(pendingDownload.FileName)
                                        ? (System.IO.Path.GetFileName(downloadedZipPath) ?? "mod.zip")
                                        : pendingDownload.FileName,
                                    localZipPath: downloadedZipPath,
                                    isLocalFile: true,
                                    gameBasePath: string.IsNullOrWhiteSpace(pendingDownload.GameBasePath)
                                        ? null
                                        : pendingDownload.GameBasePath,
                                    targetModsPath: pendingDownload.TargetModsPath,
                                    saveOnly: pendingDownload.Kind == BrowserDownloadKind.ModSaveOnly,
                                    sourcePlatform: "NexusMods",
                                    sourceProjectId: nxmUrl.ModId.ToString(),
                                    sourceFileId: nxmUrl.FileId.ToString()
                                );
                                break;

                            default:
                                throw new InvalidOperationException("未知的浏览器下载任务类型");
                        }

                        // 移除占位任务并添加真实任务
                        SVL.Core.Download.DownloadManager.Instance.RemoveTask(pendingDownload.PlaceholderTaskId);
                        await SVL.Core.Download.DownloadManager.Instance.AddTaskAsync(installTask);
                    }
                    else
                    {
                        var ex = new Exception("下载完成但未找到文件");
                        Log.Error(ex, "[DownloadRightViewModel] NXM 回调下载失败");
                        FailPendingDownload(pendingDownload.PlaceholderTaskId, "下载完成但未找到文件，请重试。");
                    }
                }
                else
                {
                    // 检查占位任务是否已经被取消
                    var currentTask = SVL.Core.Download.DownloadManager.Instance.GetTask(pendingDownload.PlaceholderTaskId);
                    if (currentTask != null && currentTask.Status == SVL.Core.Download.DownloadTaskStatus.Cancelled)
                    {
                        // 任务已经被用户取消，不显示失败通知
                        Log.Info("[DownloadRightViewModel] 下载已被用户取消，不显示失败通知");
                    }
                    else
                    {
                        Log.Error(new Exception($"DownloadModAsync 返回 false: ModId={nxmUrl.ModId}, FileId={nxmUrl.FileId}"),
                            "[DownloadRightViewModel] NXM 回调下载失败");
                        FailPendingDownload(pendingDownload.PlaceholderTaskId, "下载失败，请检查网络连接或稍后重试。");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 用户主动取消，不显示失败通知
                Log.Info("[DownloadRightViewModel] 下载已被用户取消");

                // 更新占位任务状态为已取消
                SVL.Core.Download.DownloadManager.Instance.UpdateTaskStatus(
                    pendingDownload.PlaceholderTaskId,
                    status: SVL.Core.Download.DownloadTaskStatus.Cancelled,
                    statusMessage: "已取消",
                    progress: 0
                );
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DownloadRightViewModel] NXM 回调下载失败");
                FailPendingDownload(pendingDownload.PlaceholderTaskId, $"下载失败：{ex.Message}");
            }
        }
        else
        {
            Log.Warn($"[NxmDispatch] 收到未匹配的 NXM 回调，无任何任务处理此 URL: ModId={nxmUrl.ModId}, FileId={nxmUrl.FileId}");
        }
    }

    /// <summary>
    /// 标记占位任务为失败并显示错误通知
    /// </summary>
    private static void FailPendingDownload(string placeholderTaskId, string message)
    {
        SVL.Core.Download.DownloadManager.Instance.UpdateTaskStatus(
            placeholderTaskId,
            status: SVL.Core.Download.DownloadTaskStatus.Failed,
            statusMessage: message,
            progress: 0
        );

        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            SVL.Desktop.Controls.FloatingNotificationControl.Show(
                title: "下载失败",
                message: message,
                autoCloseDelay: 5000,
                notificationType: NotificationType.Error
            );
        }));
    }

    /// <summary>
    /// 处理 Collection 类型的 NXM 协议回调
    /// </summary>
    private static async Task OnCollectionNxmUrlReceivedAsync(SVL.Core.Download.NexusMods.NxmUrl nxmUrl)
    {
        var slug = nxmUrl.CollectionSlug;
        var revisionNumber = nxmUrl.RevisionNumber ?? nxmUrl.RevisionId ?? -1;

        Log.Info($"[DownloadRightViewModel] 收到 Collection NXM 回调: Slug={slug}, Revision={revisionNumber}");

        var key = (slug, revisionNumber);
        if (!_pendingCollectionDownloads.TryGetValue(key, out var pendingDownload))
        {
            // 尝试使用 revisionNumber = -1 匹配（latest）
            if (revisionNumber != -1 && _pendingCollectionDownloads.TryGetValue((slug, -1), out pendingDownload))
            {
                Log.Info($"[DownloadRightViewModel] 使用 latest 匹配到待处理 Collection: {slug}");
            }
            else
            {
                Log.Warn($"[DownloadRightViewModel] 未找到匹配的待处理 Collection: Slug={slug}, Revision={revisionNumber}");
                return;
            }
        }

        // 移除待处理任务
        _pendingCollectionDownloads.Remove(key);

        try
        {
            Log.Info($"[DownloadRightViewModel] 开始下载 Collection: {slug} (Revision {revisionNumber})");

            // 更新占位任务状态
            SVL.Core.Download.DownloadManager.Instance.UpdateTaskStatus(
                pendingDownload.PlaceholderTaskId,
                status: SVL.Core.Download.DownloadTaskStatus.Downloading,
                statusMessage: "正在下载 Collection..."
            );

            // 获取 OAuth Token
            var accessToken = SVL.Core.Config.AppConfig.GetSettings().NexusModsOAuthToken;
            if (string.IsNullOrEmpty(accessToken))
            {
                throw new Exception("未找到 NexusMods OAuth Token");
            }

            // 创建 Collection 下载任务
            var collectionTask = new SVL.Core.Download.NexusMods.NexusCollectionDownloadTask(
                gameId: "stardewvalley",
                collectionSlug: slug,
                revisionNumber: revisionNumber,
                downloadDirectory: pendingDownload.TempDir,
                oauthToken: accessToken
            );

            // 移除占位任务并添加真实任务
            SVL.Core.Download.DownloadManager.Instance.RemoveTask(pendingDownload.PlaceholderTaskId);
            await SVL.Core.Download.DownloadManager.Instance.AddTaskAsync(collectionTask);

            Log.Info($"[DownloadRightViewModel] Collection 下载任务已添加: {slug}");

            // 显示成功通知
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                SVL.Desktop.Controls.FloatingNotificationControl.Show(
                    title: "Collection 下载已开始",
                    message: $"正在下载 {pendingDownload.ModName}...",
                    autoCloseDelay: 3000,
                    notificationType: NotificationType.Success
                );
            }));
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[DownloadRightViewModel] Collection 下载失败: {slug}");

            // 更新占位任务状态为失败
            SVL.Core.Download.DownloadManager.Instance.UpdateTaskStatus(
                pendingDownload.PlaceholderTaskId,
                status: SVL.Core.Download.DownloadTaskStatus.Failed,
                statusMessage: $"下载失败: {ex.Message}"
            );

            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                SVL.Desktop.Controls.FloatingNotificationControl.Show(
                    title: "Collection 下载失败",
                    message: ex.Message,
                    autoCloseDelay: 5000,
                    notificationType: NotificationType.Error
                );
            }));
        }
    }

    /// <summary>
    /// 待处理的浏览器下载任务
    /// </summary>
    private class PendingBrowserDownload
    {
        public BrowserDownloadKind Kind { get; set; } = BrowserDownloadKind.SmapiInstall;
        public string PlaceholderTaskId { get; set; } = string.Empty;
        public string TempDir { get; set; } = string.Empty;

        public string ModId { get; set; } = string.Empty;
        public string ModName { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string TargetModsPath { get; set; } = string.Empty;

        public string GameBasePath { get; set; } = string.Empty;
        public string InstanceName { get; set; } = string.Empty;
        public bool DebugMode { get; set; }
        public string Version { get; set; } = string.Empty;  // SMAPI 版本号

        // Collection 相关属性
        public string CollectionSlug { get; set; } = string.Empty;
        public int RevisionNumber { get; set; }
    }

    public static async Task RegisterPendingNexusSmapiDownloadAsync(
        long modId,
        long fileId,
        string gameBasePath,
        string instanceName,
        bool debugMode,
        string version)
    {
        var tempDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SVL",
            "temp"
        );

        var placeholderTask = new SVL.Core.Download.PlaceholderDownloadTask(
            $"SMAPI {version} - {instanceName}",
            SVL.Core.Download.DownloadTaskType.SMAPI,
            "等待浏览器下载（请点击下载按钮）..."
        );

        await SVL.Core.Download.DownloadManager.Instance.AddTaskAsync(placeholderTask);

        _pendingBrowserDownloads[(modId, fileId)] = new PendingBrowserDownload
        {
            Kind = BrowserDownloadKind.SmapiInstall,
            PlaceholderTaskId = placeholderTask.Id,
            TempDir = tempDir,
            GameBasePath = gameBasePath,
            InstanceName = instanceName,
            DebugMode = debugMode,
            Version = version
        };
    }

    public static async Task RegisterPendingNexusModDownloadAsync(
        long modId,
        long fileId,
        string modName,
        string fileName,
        string targetModsPath,
        bool saveOnly,
        string? gameBasePath = null)
    {
        var tempDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SVL",
            "temp",
            "mods"
        );

        var displayName = saveOnly
            ? $"{modName} ({fileName}) [另存为]"
            : $"{modName} ({fileName})";

        var placeholderTask = new SVL.Core.Download.PlaceholderDownloadTask(
            displayName,
            SVL.Core.Download.DownloadTaskType.Mod,
            "等待浏览器下载（请点击下载按钮）..."
        );

        await SVL.Core.Download.DownloadManager.Instance.AddTaskAsync(placeholderTask);

        _pendingBrowserDownloads[(modId, fileId)] = new PendingBrowserDownload
        {
            Kind = saveOnly ? BrowserDownloadKind.ModSaveOnly : BrowserDownloadKind.ModInstall,
            PlaceholderTaskId = placeholderTask.Id,
            TempDir = tempDir,
            ModId = $"nexus-{modId}",
            ModName = modName,
            FileName = fileName,
            TargetModsPath = targetModsPath,
            GameBasePath = gameBasePath ?? string.Empty
        };
    }

    /// <summary>
    /// 注册待处理的 Nexus Collection 下载（等待浏览器 NXM 回调）
    /// </summary>
    public static async Task RegisterPendingNexusCollectionDownloadAsync(
        string collectionSlug,
        int revisionNumber,
        string collectionName,
        string? saveDirectory = null)
    {
        var tempDir = saveDirectory ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SVL",
            "downloads",
            "collections",
            collectionSlug
        );

        var displayName = $"Collection: {collectionName} (r{revisionNumber})";

        var placeholderTask = new SVL.Core.Download.PlaceholderDownloadTask(
            displayName,
            SVL.Core.Download.DownloadTaskType.Modpack,
            "等待浏览器下载（请点击下载按钮）..."
        );

        await SVL.Core.Download.DownloadManager.Instance.AddTaskAsync(placeholderTask);

        var key = (collectionSlug, revisionNumber);
        _pendingCollectionDownloads[key] = new PendingBrowserDownload
        {
            Kind = BrowserDownloadKind.CollectionInstall,
            PlaceholderTaskId = placeholderTask.Id,
            TempDir = tempDir,
            ModName = collectionName,
            CollectionSlug = collectionSlug,
            RevisionNumber = revisionNumber
        };

        Log.Info($"[DownloadRightViewModel] 已注册 Collection 待处理下载: Slug={collectionSlug}, Revision={revisionNumber}, 保存目录={tempDir}");
    }

    public DownloadRightViewModel(MainWindowViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
        // 从设置中读取 SMAPI 默认源（必须在加载列表前设置，否则首次会按“全部”构建）
        var settings = AppConfig.GetSettings();
        SmapiSelectedSource = settings.SmapiDefaultSource ?? "全部";

        // 根据上次停留的下载子页决定默认类别（避免回到下载页时左/右不一致）
        _currentCategory = mainViewModel.CurrentDownloadSubPage switch
        {
            DownloadSubPageType.Modpacks => DownloadCategory.Modpacks,
            _ => DownloadCategory.SMAPI
        };

        LoadItemsForCategory(_currentCategory);
        CheckGamePathStatus();
        _ = LoadGameVersionsAsync();

        // 初始化页码列表
        UpdatePageNumbers();
    }

    /// <summary>
    /// 检查游戏路径状态
    /// </summary>
    private void CheckGamePathStatus()
    {
        var configuredPath = SVL.Core.Config.GamePathConfig.GetGamePath();
        if (string.IsNullOrEmpty(configuredPath) || !System.IO.Directory.Exists(configuredPath))
        {
            GamePathWarningVisibility = System.Windows.Visibility.Visible;
            GamePathStatus = "未设置游戏路径";
        }
        else
        {
            GamePathWarningVisibility = System.Windows.Visibility.Collapsed;
            GamePathStatus = $"游戏路径: {configuredPath}";
        }
    }

    [ObservableProperty]
    private string _title = "SMAPI";

    [ObservableProperty]
    private string _status = "浏览可下载内容";

    [ObservableProperty]
    private List<DownloadItem> _items = [];

    [ObservableProperty]
    private List<DownloadItem> _filteredItems = [];

    [ObservableProperty]
    private DownloadItem? _selectedItem;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private string _downloadStatus = "";

    [ObservableProperty]
    private double _downloadProgress;

    // 游戏路径状态
    [ObservableProperty]
    private System.Windows.Visibility _gamePathWarningVisibility = System.Windows.Visibility.Collapsed;

    public System.Windows.Visibility SmapiGamePathWarningVisibility
        => IsSmapiCategory ? GamePathWarningVisibility : System.Windows.Visibility.Collapsed;

    [ObservableProperty]
    private string _gamePathStatus = "";

    partial void OnGamePathWarningVisibilityChanged(System.Windows.Visibility value)
    {
        OnPropertyChanged(nameof(SmapiGamePathWarningVisibility));
    }

    // SMAPI 搜索相关
    [ObservableProperty]
    private string _smapiSearchText = "";

    /// <summary>
    /// SMAPI Mod 列表（用于"全部"模式，复用 ModSearchItem UI）
    /// </summary>
    [ObservableProperty]
    private List<ModSearchItem> _smapiModList = new();

    public IAsyncRelayCommand OpenSmapiDetailsCommand => new AsyncRelayCommand<ModSearchItem>(OpenSmapiDetailsAsync);

    [ObservableProperty]
    private string _smapiSelectedSource = "全部";

    [ObservableProperty]
    private string _smapiGameVersion = "全部";

    public List<string> SmapiSources { get; } = new List<string> { "全部", "GitHub", "Curseforge", "NexusMods" };

    /// <summary>
    /// 是否已经显示过 SMAPI API 配置警告（防止重复提示）
    /// </summary>
    private static bool _hasShownSmapiConfigWarning = false;

    /// <summary>
    /// 是否已经显示过 Mod API 配置警告（防止重复提示）
    /// </summary>
    private static bool _hasShownModConfigWarning = false;

    /// <summary>
    /// 是否已经显示过 Modpack API 配置警告（防止重复提示）
    /// </summary>
    private static bool _hasShownModpackConfigWarning = false;

    public ObservableCollection<string> GameVersions { get; } = new ObservableCollection<string> { "全部" };

    private async Task LoadGameVersionsAsync()
    {
        try
        {
            var list = await SVL.Core.Stardew.Mod.SMAPI.SmapApiService.GetKnownGameVersionsAsync(maxPages: 5);
            if (list == null || list.Count == 0)
                return;

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                GameVersions.Clear();
                GameVersions.Add("全部");
                foreach (var version in list)
                    GameVersions.Add(version);

                if (!GameVersions.Contains(SmapiGameVersion))
                    SmapiGameVersion = "全部";
            });
        }
        catch (Exception ex)
        {
            Log.Warn("[DownloadRightViewModel] 动态加载游戏版本失败", ex);
        }
    }

    // Mods 搜索相关
    [ObservableProperty]
    private string _modsSearchText = "";

    [ObservableProperty]
    private string _modsSortBy = "热门";

    public List<string> ModsSortOptions { get; } = new List<string> { "热门", "最新更新", "最早上传", "最多下载" };

    // 分页相关
    [ObservableProperty]
    private int _currentPage = 1;

    [ObservableProperty]
    private int _pageSize = 10;

    [ObservableProperty]
    private int _totalPages = 1;

    [ObservableProperty]
    private bool _hasNextPage;

    [ObservableProperty]
    private bool _hasPreviousPage;

    /// <summary>
    /// 页码列表（用于显示页码按钮）
    /// </summary>
    [ObservableProperty]
    private List<int> _pageNumbers = new();

    partial void OnCurrentPageChanged(int value)
    {
        UpdatePaginationButtons();
        UpdatePageNumbers();

        // 根据当前类别重新加载内容
        if (_currentCategory == DownloadCategory.SMAPI && Items.Count > 0)
        {
            // SMAPI 类别：重新加载版本
            _ = LoadSmapiVersionsAsync(Items[0]);
        }
        else if (_currentCategory == DownloadCategory.Mods)
        {
            // Mods 类别：重新搜索/加载
            _ = LoadModItemsFromNexusMods();
        }
        else if (_currentCategory == DownloadCategory.Modpacks)
        {
            _ = LoadModpackItemsFromMultipleSourcesAsync();
        }
    }

    private void UpdatePaginationButtons()
    {
        HasPreviousPage = CurrentPage > 1;
        HasNextPage = CurrentPage < TotalPages;
    }

    /// <summary>
    /// 更新页码列表（智能显示页码）
    /// </summary>
    private void UpdatePageNumbers()
    {
        var pages = new List<int>();

        // 确保 TotalPages 至少为 1
        var totalPages = Math.Max(1, TotalPages);
        Log.Debug($"[DownloadRightViewModel] UpdatePageNumbers: CurrentPage={CurrentPage}, TotalPages={TotalPages}");

        if (totalPages <= 7)
        {
            // 总页数少于7页，显示所有页码
            for (int i = 1; i <= totalPages; i++)
            {
                pages.Add(i);
            }
        }
        else
        {
            // 总页数多于7页，智能显示
            // 始终显示第一页
            pages.Add(1);

            if (CurrentPage <= 3)
            {
                // 当前页在前面：1 2 3 4 5 ... 总页数
                for (int i = 2; i <= 5; i++)
                {
                    pages.Add(i);
                }
                pages.Add(-1); // -1 表示省略号
                pages.Add(totalPages);
            }
            else if (CurrentPage >= totalPages - 2)
            {
                // 当前页在后面：1 ... 总页数-4 总页数-3 总页数-2 总页数-1 总页数
                pages.Add(-1); // 省略号
                for (int i = totalPages - 4; i <= totalPages; i++)
                {
                    pages.Add(i);
                }
            }
            else
            {
                // 当前页在中间：1 ... 当前页-1 当前页 当前页+1 ... 总页数
                pages.Add(-1); // 省略号
                pages.Add(CurrentPage - 1);
                pages.Add(CurrentPage);
                pages.Add(CurrentPage + 1);
                pages.Add(-1); // 省略号
                pages.Add(totalPages);
            }
        }

        Log.Debug($"[DownloadRightViewModel] PageNumbers: [{string.Join(", ", pages)}]");
        PageNumbers = pages;
    }

    /// <summary>
    /// 跳转到指定页
    /// </summary>
    [RelayCommand]
    private void GoToPage(int pageNumber)
    {
        if (pageNumber >= 1 && pageNumber <= TotalPages && pageNumber != CurrentPage)
        {
            CurrentPage = pageNumber;
        }
    }

    [RelayCommand]
    private void SearchSmapi()
    {
        ApplySmapiFilters();
    }

    /// <summary>
    /// 搜索 NexusMods Mods
    /// </summary>
    [RelayCommand]
    private async void SearchMods()
    {
        if (_currentCategory != DownloadCategory.Mods)
        {
            return;
        }

        // 检查 NexusMods 登录状态
        var settings = AppConfig.GetSettings();
        var nexusToken = settings.NexusModsOAuthToken;
        if (string.IsNullOrEmpty(nexusToken) && !_hasShownModConfigWarning)
        {
            _hasShownModConfigWarning = true;
            ApiSettingsNavigationHelper.ShowApiConfigWarningAndNavigate(
                "DownloadRightViewModel",
                "⚠️ NexusMods 未登录\n请在设置页面登录 NexusMods 账户以使用 NexusMods 源。"
            );
            return;
        }

        // 重置到第一页
        CurrentPage = 1;

        // 执行搜索
        await LoadModItemsFromNexusMods();
    }

    /// <summary>
    /// 选择游戏路径
    /// </summary>
    [RelayCommand]
    private void SelectGamePath()
    {
        var owner = System.Windows.Application.Current.MainWindow;

        // 显示路径选择对话框
        var pathDialog = new SVL.Desktop.Controls.GamePathSelectionDialog();
        if (owner != null)
        {
            pathDialog.Owner = owner;
        }

        var pathResult = pathDialog.ShowDialog();
        if (pathResult == true && !string.IsNullOrEmpty(pathDialog.SelectedPath))
        {
            // 保存配置
            SVL.Core.Config.GamePathConfig.SaveGamePath(pathDialog.SelectedPath);
            Log.Info($"[DownloadRightViewModel] ✓ 用户选择了游戏路径: {pathDialog.SelectedPath}");

            // 更新状态
            CheckGamePathStatus();
        }
    }

    [RelayCommand]
    private void NextPage()
    {
        if (HasNextPage)
        {
            CurrentPage++;
        }
    }

    [RelayCommand]
    private void PreviousPage()
    {
        if (HasPreviousPage)
        {
            CurrentPage--;
        }
    }

    [RelayCommand]
    private void ToggleItemExpanded(DownloadItem item)
    {
        if (item == null)
            return;

        // SMAPI 主卡片：点击后跳转到对应的 ModDetails（复用 MOD 详情页显示）
        if (_currentCategory == DownloadCategory.SMAPI && item.Id == "smapi-all")
        {
            OpenSmapiModDetails(item);
            return;
        }

        item.IsExpanded = !item.IsExpanded;
    }

    private void OpenSmapiModDetails(DownloadItem smapiItem)
    {
        try
        {
            const int smapiCurseforgeProjectId = 898372;
            const long smapiNexusModId = 2400;

            var settings = AppConfig.GetSettings();

            string source;
            string id;
            if (SmapiSelectedSource == "NexusMods")
            {
                source = "NexusMods";
                id = $"nexus-{smapiNexusModId}";
            }
            else if (SmapiSelectedSource == "Curseforge")
            {
                source = "Curseforge";
                id = $"curse-{smapiCurseforgeProjectId}";
            }
            else if (!string.IsNullOrWhiteSpace(settings.NexusModsOAuthToken))
            {
                source = "NexusMods";
                id = $"nexus-{smapiNexusModId}";
            }
            else
            {
                // 无可用源时仍允许进入详情页展示基础信息
                source = "GitHub";
                id = "github-smapi";
            }

            var mod = new ModSearchItem
            {
                Id = id,
                Name = smapiItem.Name,
                Summary = smapiItem.Description,
                Description = smapiItem.Description,
                Author = smapiItem.Author,
                Source = source,
                Url = source switch
                {
                    "Curseforge" => "https://www.curseforge.com/stardewvalley/mods/smapi",
                    "NexusMods" => $"https://www.nexusmods.com/stardewvalley/mods/{smapiNexusModId}",
                    _ => "https://smapi.io/"
                }
            };

            // 复用已缓存的缩略图（如果有）
            if (!string.IsNullOrWhiteSpace(smapiItem.Thumbnail))
            {
                if (System.IO.File.Exists(smapiItem.Thumbnail))
                {
                    mod.LocalIconPath = smapiItem.Thumbnail;
                }
                else if (smapiItem.Thumbnail.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                         smapiItem.Thumbnail.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    mod.IconUrl = smapiItem.Thumbnail;
                }
            }

            _mainViewModel.SelectedModSearch = mod;
            _mainViewModel.CurrentPage = PageType.ModDetails;

            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                _ = LoadModDetailsAsync(mod.Id);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DownloadRightViewModel] 打开 SMAPI 详情失败");
        }
    }

    private async Task LoadModDetailsAsync(string modId)
    {
        if (_mainViewModel.LeftPanelContent is ModDetailsViewModel detailsViewModel)
        {
            await detailsViewModel.LoadModAsync(modId);
        }
    }

    private void ApplySmapiFilters()
    {
        if (_currentCategory != DownloadCategory.SMAPI)
        {
            FilteredItems = new List<DownloadItem>(Items);
            Status = $"显示全部 {Items.Count} 个结果";
            return;
        }

        var query = Items.AsEnumerable();

        // 搜索过滤
        if (!string.IsNullOrWhiteSpace(SmapiSearchText))
        {
            query = query.Where(item =>
                item.Name.IndexOf(SmapiSearchText, System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                (item.Author != null && item.Author.IndexOf(SmapiSearchText, System.StringComparison.OrdinalIgnoreCase) >= 0) ||
                (item.Description != null && item.Description.IndexOf(SmapiSearchText, System.StringComparison.OrdinalIgnoreCase) >= 0));
        }

        FilteredItems = query.ToList();
        Status = FilteredItems.Count == Items.Count
            ? $"显示全部 {FilteredItems.Count} 个结果"
            : $"筛选出 {FilteredItems.Count} / {Items.Count} 个结果";
    }

    /// <summary>
    /// 更新显示内容
    /// </summary>
    public void UpdateContent(DownloadCategory category)
    {
        _currentCategory = category;
        OnPropertyChanged(nameof(IsSmapiCategory));
        OnPropertyChanged(nameof(SmapiGamePathWarningVisibility));
        Title = category switch
        {
            DownloadCategory.SMAPI => "SMAPI",
            DownloadCategory.Mods => "社区资源 - Mod",
            DownloadCategory.Modpacks => "社区资源 - 整合包",
            _ => "下载"
        };

        LoadItemsForCategory(category);
    }

    /// <summary>
    /// 搜索项目（旧方法，保留兼容性）
    /// </summary>
    public void SearchItems(string searchText)
    {
        // 简单的名称搜索
        if (string.IsNullOrWhiteSpace(searchText))
        {
            FilteredItems = new List<DownloadItem>(Items);
        }
        else
        {
            FilteredItems = Items
                .Where(item => item.Name.IndexOf(searchText, System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                             (item.Author != null && item.Author.IndexOf(searchText, System.StringComparison.OrdinalIgnoreCase) >= 0) ||
                             (item.Description != null && item.Description.IndexOf(searchText, System.StringComparison.OrdinalIgnoreCase) >= 0))
                .ToList();
        }

        Status = FilteredItems.Count == Items.Count
            ? $"显示全部 {FilteredItems.Count} 个结果"
            : $"筛选出 {FilteredItems.Count} / {Items.Count} 个结果";
    }

    /// <summary>
    /// 根据类别加载项目
    /// </summary>
    private void LoadItemsForCategory(DownloadCategory category)
    {
        switch (category)
        {
            case DownloadCategory.SMAPI:
                if (_hasLoadedSmapiItems && Items.Count > 0)
                {
                    FilteredItems = new List<DownloadItem>(Items);
                    return;
                }
                LoadSMAPIItems();
                break;
            case DownloadCategory.Mods:
                if (_hasLoadedModItems && Items.Count > 0)
                {
                    FilteredItems = new List<DownloadItem>(Items);
                    return;
                }
                LoadModItems();
                break;
            case DownloadCategory.Modpacks:
                if (_hasLoadedModpackItems && Items.Count > 0)
                {
                    FilteredItems = new List<DownloadItem>(Items);
                    return;
                }
                LoadModpackItems();
                break;
        }

        FilteredItems = new List<DownloadItem>(Items);
    }

    /// <summary>
    /// 加载SMAPI项目
    /// </summary>
    private void LoadSMAPIItems()
    {
        _hasLoadedSmapiItems = true;
        // SMAPI 页面改为 ModSearch 风格列表
        Items = new List<DownloadItem>();
        FilteredItems = new List<DownloadItem>();
        _ = BuildSmapiModListAsync();
    }

    partial void OnSmapiSelectedSourceChanged(string value)
    {
        if (_currentCategory == DownloadCategory.SMAPI)
        {
            _ = BuildSmapiModListAsync();
        }
    }

    private async Task BuildSmapiModListAsync()
    {
        try
        {
            Status = "正在加载 SMAPI...";

            const int smapiCurseforgeProjectId = 898372;
            const long smapiNexusModId = 2400;

            var settings = AppConfig.GetSettings();
            var enableSearchCache = settings.EnableNexusModsSearchCache;
            var cacheKey = $"list|source={SmapiSelectedSource}";

            if (enableSearchCache && SVL.Core.IO.SearchCacheService.TryGet<List<ModSearchItem>>("smapi", cacheKey, out var cachedList) && cachedList != null && cachedList.Count > 0)
            {
                SmapiModList = cachedList;
                Status = $"显示 {SmapiModList.Count} 个结果";
                Log.Debug($"[DownloadRightViewModel] SMAPI 列表命中缓存: source={SmapiSelectedSource}, count={SmapiModList.Count}");
                
                // 为缓存的项异步加载汉化和图标
                foreach (var item in cachedList)
                {
                    _ = item.LoadIconAsync();
                }
                LocalizationDisplayHelper.ApplyLocalizationInBackground(cachedList);
                
                return;
            }

            var result = new List<ModSearchItem>();

            IEnumerable<string> sources = SmapiSelectedSource switch
            {
                "GitHub" => new[] { "GitHub" },
                "Curseforge" => new[] { "Curseforge" },
                "NexusMods" => new[] { "NexusMods" },
                _ => new[] { "GitHub", "Curseforge", "NexusMods" }
            };

            foreach (var source in sources)
            {
                // 无法获取某个来源的信息，则不显示该来源
                

                

                var item = new ModSearchItem
                {
                    Name = "SMAPI - Stardew Modding API",
                    Summary = "Stardew Valley 的模组加载 API（必须先安装）",
                    Description = "Stardew Valley 的模组加载 API，安装后可以加载和使用 Mod。这是游戏的 Mod 框架，必须首先安装。",
                    Author = "Pathoschild",
                    Source = source,
                    DownloadCount = 0,
                    LastUpdateTime = "-",
                    SupportedGameVersions = new List<string>()
                };

                item.Id = source switch
                {
                    "Curseforge" => $"curse-{smapiCurseforgeProjectId}",
                    "NexusMods" => $"nexus-{smapiNexusModId}",
                    _ => "github-smapi"
                };

                item.Url = source switch
                {
                    "Curseforge" => "https://www.curseforge.com/stardewvalley/mods/smapi",
                    "NexusMods" => $"https://www.nexusmods.com/stardewvalley/mods/{smapiNexusModId}",
                    _ => "https://github.com/Pathoschild/SMAPI/releases"
                };

                try
                {
                    // 参考 ModSearch 的 icon 获取方式
                    if (source == "Curseforge")
                    {
                        var modInfo = await SVL.Core.Download.CurseforgeApiService.GetModInfoAsync(smapiCurseforgeProjectId);
                        var logoUrl = modInfo?.Logo?.ThumbnailUrl ?? modInfo?.Logo?.Url;
                        if (string.IsNullOrWhiteSpace(logoUrl))
                            continue;

                        item.IconUrl = logoUrl;

                        // 贴近 ModSearch：下载量 / 描述 / 更新时间
                        if (!string.IsNullOrWhiteSpace(modInfo?.Summary))
                        {
                            item.Summary = modInfo.Summary;
                            item.Description = modInfo.Summary;
                        }

                        if (modInfo != null && modInfo.DownloadCount > 0)
                            item.DownloadCount = modInfo.DownloadCount;

                        if (!string.IsNullOrWhiteSpace(modInfo?.DateModified) && DateTime.TryParse(modInfo.DateModified, out var modified))
                            item.LastUpdateTime = modified.ToString("yyyy-MM-dd");

                        try
                        {
                            var versions = await SVL.Core.Stardew.Mod.SMAPI.SmapApiService.GetAllVersionsFromCurseforgeAsync(0, 1);
                            var latest = versions.FirstOrDefault();
                            if (latest != null && latest.PublishedDate != default)
                                item.LastUpdateTime = latest.PublishedDate.ToString("yyyy-MM-dd");
                        }
                        catch
                        {
                            // 更新时间获取失败不影响展示
                        }
                    }
                    else if (source == "NexusMods")
                    {
                        // 列表页只获取基本信息，不加载版本列表（版本列表在Detail页加载）
                        // SMAPI 是固定 Mod ID，直接使用 REST API 获取详情
                        var modInfo = await SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsService
                            .GetModDetailsAsync(smapiNexusModId);

                        if (string.IsNullOrWhiteSpace(modInfo?.PictureUrl))
                            continue;

                        item.IconUrl = modInfo.PictureUrl;

                        // 贴近 ModSearch：下载量 / 描述 / 更新时间
                        if (!string.IsNullOrWhiteSpace(modInfo?.Summary))
                        {
                            item.Summary = modInfo.Summary;
                            item.Description = modInfo.Summary;
                        }

                        if (modInfo != null && modInfo.Downloads > 0)
                            item.DownloadCount = modInfo.Downloads;

                        if (modInfo != null && modInfo.UpdatedAt != default)
                            item.LastUpdateTime = modInfo.UpdatedAt.ToString("yyyy-MM-dd");

                        // 注意：版本列表不在列表页加载，用户点击进入Detail页时才会加载
                    }
                    else
                    {
                        // 避免使用 smapi.io（存在 404），改用 GitHub 稳定图片源
                        item.IconUrl = "https://opengraph.githubassets.com/1/Pathoschild/SMAPI";
                        try
                        {
                            var stars = await SVL.Core.Stardew.Mod.SMAPI.SmapApiService.GetSmapiRepoStarCountAsync();
                            if (stars.HasValue)
                            {
                                item.DownloadCount = stars.Value;
                            }

                            var versions = await SVL.Core.Stardew.Mod.SMAPI.SmapApiService.GetAllVersionsAsync(1, 1);
                            var latest = versions.FirstOrDefault();
                            if (latest != null && latest.PublishedDate != default)
                                item.LastUpdateTime = latest.PublishedDate.ToString("yyyy-MM-dd");
                        }
                        catch
                        {
                            // GitHub 版本拉取失败时仍展示基础信息
                        }
                    }

                    _ = item.LoadIconAsync();
                    result.Add(item);
                    LocalizationDisplayHelper.ApplyLocalizationInBackground(new[] { item });
                }
                catch (Exception ex)
                {
                    

                    // 无法获取该来源信息 => 不显示，但如果用户明确选择该来源（或当前为“全部”且该来源缺失），提示一次并跳转设置
                    Log.Warn($"[DownloadRightViewModel] SMAPI 来源 {source} 信息获取失败", ex);

                    if (!_hasShownSmapiConfigWarning && (SmapiSelectedSource == source || SmapiSelectedSource == "全部"))
                    {
                        _hasShownSmapiConfigWarning = true;
                        ApiSettingsNavigationHelper.ShowApiConfigWarningAndNavigate(
                            "DownloadRightViewModel",
                            $"⚠️ 无法加载 {source} 的 SMAPI 信息\n请检查网络连接与 API/登录配置。"
                        );
                    }
                }
            }

            SmapiModList = result;
            Status = SmapiModList.Count > 0
                ? $"显示 {SmapiModList.Count} 个结果"
                : "无可用来源";

            if (enableSearchCache && SmapiModList.Count > 0)
            {
                await SVL.Core.IO.SearchCacheService.SetAsync("smapi", cacheKey, SmapiModList);
            }

            if (SmapiModList.Count == 0 && !_hasShownSmapiConfigWarning)
            {
                _hasShownSmapiConfigWarning = true;
                ApiSettingsNavigationHelper.ShowApiConfigWarningAndNavigate(
                    "DownloadRightViewModel",
                    "⚠️ 未能加载任何 SMAPI 来源\n请检查网络连接与 NexusMods 登录状态，或稍后重试。"
                );
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DownloadRightViewModel] 构建 SMAPI 列表失败");
            Status = "加载失败";
        }
    }

    private async Task OpenSmapiDetailsAsync(ModSearchItem? item)
    {
        if (item == null)
            return;

        _mainViewModel.SelectedModSearch = item;
        _mainViewModel.CurrentPage = PageType.ModDetails;

        // 异步加载 MOD 详情
        if (_mainViewModel.LeftPanelContent is ModDetailsViewModel detailsViewModel)
        {
            await detailsViewModel.LoadModAsync(item.Id);
        }
    }

    /// <summary>
    /// 异步加载SMAPI版本信息
    /// </summary>
    private async Task LoadSmapiVersionsAsync(DownloadItem smapiItem)
    {
        try
        {
            // 确保在加载前认证信息已初始化（从 AppConfig 重新加载最新配置）
            try
            {
                var settings = SVL.Core.Config.AppConfig.GetSettings();

                // NexusMods 现在使用 OAuth 认证，不需要手动设置 API Key
                if (!string.IsNullOrEmpty(settings.NexusModsOAuthToken))
                {
                    Log.Info("[DownloadRightViewModel] ✓ NexusMods OAuth Token 已配置");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DownloadRightViewModel] 初始化认证信息失败");
            }

            Status = "正在加载版本列表...";

            // 根据当前选择的源设置主项目图标
            SetSmapiItemIcon(smapiItem);

            List<SVL.Core.Stardew.Mod.SMAPI.SmapiVersionInfo> versions;
            string sourceName;

            // 检查 NexusMods 登录状态（如果选择了 NexusMods 或 全部）
            if (SmapiSelectedSource == "NexusMods" || SmapiSelectedSource == "全部")
            {
                var settings = AppConfig.GetSettings();
                var nexusToken = settings.NexusModsOAuthToken;
                if (string.IsNullOrEmpty(nexusToken) && !_hasShownSmapiConfigWarning)
                {
                    _hasShownSmapiConfigWarning = true;
                    ApiSettingsNavigationHelper.ShowApiConfigWarningAndNavigate(
                        "DownloadRightViewModel",
                        "⚠️ NexusMods 未登录\n请在设置页面登录 NexusMods 账户以从 NexusMods 源下载 SMAPI。"
                    );
                    // 如果选择的是 NexusMods 单独源，则不继续加载
                    if (SmapiSelectedSource == "NexusMods")
                    {
                        Status = "⚠️ 需要登录 NexusMods";
                        return;
                    }
                }
            }

            // 处理"全部"选项：同页合并显示多来源版本（保持 DownloadItem 显示代码一致）
            if (SmapiSelectedSource == "全部")
            {
                sourceName = "全部";

                var settings = AppConfig.GetSettings();

                var githubTask = SVL.Core.Stardew.Mod.SMAPI.SmapApiService.GetAllVersionsAsync(CurrentPage, PageSize);

                var curseforgeTask = SVL.Core.Stardew.Mod.SMAPI.SmapApiService.GetAllVersionsFromCurseforgeAsync(CurrentPage - 1, PageSize);

                
                  var nexusTask = string.IsNullOrWhiteSpace(settings.NexusModsOAuthToken) 
                                      ? Task.FromResult<(System.Collections.Generic.List<SVL.Core.Stardew.Mod.SMAPI.SmapiVersionInfo>, string, int)>((new System.Collections.Generic.List<SVL.Core.Stardew.Mod.SMAPI.SmapiVersionInfo>(), string.Empty, 0)) 
                                      : GetSmapiVersionsFromNexusModsAsync();

                await Task.WhenAll(githubTask, curseforgeTask, nexusTask);

                var (nexusVersions, _, nexusTotalPages) = await nexusTask;
                var combined = new List<SVL.Core.Stardew.Mod.SMAPI.SmapiVersionInfo>();
                combined.AddRange(await githubTask);
                combined.AddRange(await curseforgeTask);
                combined.AddRange(nexusVersions);

                // 去重：同一来源+版本只保留一条
                versions = combined
                    .Where(v => v != null)
                    .GroupBy(v => $"{v.Source}|{v.Version}")
                    .Select(g => g.First())
                    .OrderByDescending(v => v.PublishedDate)
                    .ToList();

                // "全部"模式总页数：尽量取 Nexus 的精确页数，其余按是否还有下一页做近似
                var hasMore = (await githubTask).Count >= PageSize || (await curseforgeTask).Count >= PageSize;
                var guessPages = hasMore ? CurrentPage + 1 : CurrentPage;
                TotalPages = Math.Max(guessPages, nexusTotalPages > 0 ? nexusTotalPages : 1);
            }
            else if (SmapiSelectedSource == "NexusMods")
            {
                sourceName = "NexusMods";

                // *** 优先启动图片缓存任务 ***
                // 立即启动图片缓存任务（不等待版本信息）
                _ = Task.Run(async () =>
                {
                    try
                    {
                        const long SMAPI_MOD_ID = 2400; // SMAPI 在 NexusMods 上的 mod ID

                        // 先获取 mod 信息（包含图片 URL）
                        var modInfo = await SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsService.GetModDetailsAsync(SMAPI_MOD_ID);
                        if (modInfo != null && !string.IsNullOrEmpty(modInfo.PictureUrl))
                        {
                            var pictureUrl = modInfo.PictureUrl;
                            Log.Info($"[DownloadRightViewModel] 获取到 SMAPI 图片: {pictureUrl}");

                            // 先检查缓存
                            var cachedPath = ImageCacheService.GetCachedImagePath(pictureUrl);
                            if (!string.IsNullOrEmpty(cachedPath))
                            {
                                // 使用缓存
                                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    smapiItem.Thumbnail = cachedPath;
                                    Log.Info($"[DownloadRightViewModel] 使用缓存图片: {pictureUrl}");
                                    FilteredItems = new List<DownloadItem>(Items);
                                }));
                            }
                            else
                            {
                                // 下载并缓存
                                var downloadedPath = await ImageCacheService.DownloadAndCacheImageAsync(pictureUrl);
                                if (!string.IsNullOrEmpty(downloadedPath))
                                {
                                    System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                                    {
                                        smapiItem.Thumbnail = downloadedPath;
                                        Log.Info($"[DownloadRightViewModel] NexusMods SMAPI 图片已缓存: {pictureUrl}");
                                        FilteredItems = new List<DownloadItem>(Items);
                                    }));
                                }
                                else
                                {
                                    // 下载失败，使用URL
                                    System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                                    {
                                        smapiItem.Thumbnail = pictureUrl;
                                        FilteredItems = new List<DownloadItem>(Items);
                                    }));
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("[DownloadRightViewModel] 获取 NexusMods 图片失败", ex);
                    }
                });

                // 同时获取版本信息
                var (versionsResult, _, nexusTotalPages) = await GetSmapiVersionsFromNexusModsAsync();
                versions = versionsResult;
                TotalPages = nexusTotalPages;
            }
            else if (SmapiSelectedSource == "Curseforge")
            {
                sourceName = "Curseforge";

                // *** 优先启动图片缓存任务 ***
                // 立即启动 logo 缓存任务（不等待版本信息）
                _ = Task.Run(async () =>
                {
                    try
                    {
                        const int smapiModId = 898372; // SMAPI Curseforge 项目 ID
                        var modInfo = await SVL.Core.Download.CurseforgeApiService.GetModInfoAsync(smapiModId);
                        if (modInfo != null && !string.IsNullOrEmpty(modInfo.Logo?.Url))
                        {
                            var logoUrl = modInfo.Logo.Url;

                            // 先检查缓存
                            var cachedPath = ImageCacheService.GetCachedImagePath(logoUrl);
                            if (!string.IsNullOrEmpty(cachedPath))
                            {
                                // 使用缓存
                                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    smapiItem.Thumbnail = cachedPath;
                                    Log.Info($"[DownloadRightViewModel] 使用缓存 Logo: {logoUrl}");
                                    FilteredItems = new List<DownloadItem>(Items);
                                }));
                            }
                            else
                            {
                                // 下载并缓存
                                var downloadedPath = await ImageCacheService.DownloadAndCacheImageAsync(logoUrl);
                                if (!string.IsNullOrEmpty(downloadedPath))
                                {
                                    System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                                    {
                                        smapiItem.Thumbnail = downloadedPath;
                                        Log.Info($"[DownloadRightViewModel] Curseforge SMAPI Logo 已缓存: {logoUrl}");
                                        FilteredItems = new List<DownloadItem>(Items);
                                    }));
                                }
                                else
                                {
                                    // 下载失败，使用URL
                                    System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                                    {
                                        smapiItem.Thumbnail = logoUrl;
                                        FilteredItems = new List<DownloadItem>(Items);
                                    }));
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("[DownloadRightViewModel] 获取 Curseforge Logo 失败", ex);
                    }
                });

                // 同时获取版本信息
                versions = await SVL.Core.Stardew.Mod.SMAPI.SmapApiService.GetAllVersionsFromCurseforgeAsync(CurrentPage - 1, PageSize);

                // 计算总页数（Curseforge 返回的数据是分页的）
                TotalPages = versions.Count == PageSize ? CurrentPage + 1 : CurrentPage;
            }
            else
            {
                versions = await SVL.Core.Stardew.Mod.SMAPI.SmapApiService.GetAllVersionsAsync(CurrentPage, PageSize);
                sourceName = "GitHub";

                // GitHub 的总页数计算（GitHub API 返回的数据也是分页的）
                TotalPages = versions.Count == PageSize ? CurrentPage + 1 : CurrentPage;
            }

            Status = $"{sourceName} - 第 {CurrentPage} 页";

            var versionItems = new List<DownloadItem>();
            foreach (var version in versions)
            {
                var itemSource = string.IsNullOrWhiteSpace(version.Source) ? sourceName : version.Source;
                string thumbnail = itemSource switch
                {
                    "Curseforge" => "📦",
                    "NexusMods" => "🎮",
                    _ => "🔧"
                };

                versionItems.Add(new DownloadItem
                {
                    Id = $"smapi-{itemSource}-{version.Version}",
                    Name = "SMAPI",
                    Version = version.Version,
                    Description = sourceName == "全部" ? $"[{itemSource}] {version.Description}" : version.Description,
                    Author = "Pathoschild",
                    Thumbnail = thumbnail,
                    Category = "SMAPI",
                    FileName = version.Version,
                    DownloadUrl = version.DownloadUrl,
                    FileId = version.FileId,
                    Source = itemSource
                });
            }

            smapiItem.Versions = versionItems;

            UpdatePaginationButtons();
            UpdatePageNumbers();

            // 更新状态
            Status += $"，显示 {versionItems.Count} 个版本";

            // 刷新显示
            FilteredItems = new List<DownloadItem>(Items);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DownloadRightViewModel] 加载SMAPI版本失败");
            Status = "加载版本失败";
        }
    }

    /// <summary>
    /// 从 NexusMods 获取 SMAPI 版本列表
    /// </summary>
    private async Task<(List<SVL.Core.Stardew.Mod.SMAPI.SmapiVersionInfo> Versions, string PictureUrl, int TotalPages)> GetSmapiVersionsFromNexusModsAsync()
    {
        try
        {
            const long SMAPI_MOD_ID = 2400; // SMAPI 在 NexusMods 上的 mod ID

            // 并行启动两个任务：获取文件列表和获取mod信息（包含图片）
            var filesTask = SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsService.GetModFilesAsync(SMAPI_MOD_ID);
            var modInfoTask = SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsService.GetModDetailsAsync(SMAPI_MOD_ID);

            // 等待文件列表完成（需要先处理版本数据）
            var files = await filesTask;

            if (files == null || files.Count == 0)
            {
                Log.Warn("[DownloadRightViewModel] NexusMods 未找到 SMAPI 文件");
                return (new List<SVL.Core.Stardew.Mod.SMAPI.SmapiVersionInfo>(), string.Empty, 0);
            }

            // 获取 SMAPI mod 信息（包含图片）- 已经在后台并行执行
            string pictureUrl = string.Empty;
            try
            {
                var modInfo = await modInfoTask;  // 等待并行任务完成
                if (modInfo != null && !string.IsNullOrEmpty(modInfo.PictureUrl))
                {
                    pictureUrl = modInfo.PictureUrl;
                    Log.Info($"[DownloadRightViewModel] 获取到 SMAPI 图片: {pictureUrl}");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("[DownloadRightViewModel] 获取 SMAPI 图片失败，将使用默认图标", ex);
            }

            // 按上传时间降序排序
            var sortedFiles = files.OrderByDescending(f => f.UploadedTime).ToList();

            // 计算总页数
            var totalCount = sortedFiles.Count;
            var totalPages = (int)Math.Ceiling((double)totalCount / PageSize);

            // 分页：只返回当前页的数据
            var skip = (CurrentPage - 1) * PageSize;
            var pagedFiles = sortedFiles.Skip(skip).Take(PageSize).ToList();

            // 转换为 SmapiVersionInfo（去重：同一版本只保留一个）
            var versions = new List<SVL.Core.Stardew.Mod.SMAPI.SmapiVersionInfo>();
            var seenVersions = new HashSet<string>(); // 用于跟踪已见过的版本号

            foreach (var file in pagedFiles)
            {
                var version = file.Version ?? "未知";

                // 如果这个版本已经处理过，跳过
                if (seenVersions.Contains(version))
                {
                    Log.Debug($"[DownloadRightViewModel] 跳过重复版本: {version}");
                    continue;
                }

                seenVersions.Add(version);
                versions.Add(new SVL.Core.Stardew.Mod.SMAPI.SmapiVersionInfo
                {
                    Version = version,
                    Description = file.Description ?? file.Name,
                    Source = "NexusMods",
                    DownloadUrl = string.Empty, // NexusMods 需要通过特殊方式下载
                    FileId = file.GetFileIdLong(),
                    PublishedDate = DateTime.TryParse(file.UploadedTime, out var uploaded) ? uploaded : DateTime.MinValue
                });
            }

            Log.Info($"[DownloadRightViewModel] 从 NexusMods 加载了 {versions.Count} 个 SMAPI 版本（第 {CurrentPage}/{totalPages} 页，共 {totalCount} 个版本，去重后 {versions.Count} 个）");
            return (versions, pictureUrl, totalPages);
        }
        catch (SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsTokenExpiredException)
        {
            Log.Warn("[DownloadRightViewModel] NexusMods Token 已过期");
            HandleNexusModsTokenExpired();
            return (new List<SVL.Core.Stardew.Mod.SMAPI.SmapiVersionInfo>(), string.Empty, 0);
        }
        catch (System.Net.Http.HttpRequestException httpEx)
        {
            // 检查 HTTP 状态码并显示相应的错误提示
            Log.Error(httpEx, "[DownloadRightViewModel] NexusMods HTTP 请求失败");

            string errorMessage;
            var statusCodeStr = ExtractStatusCode(httpEx);
            if (!string.IsNullOrEmpty(statusCodeStr) && int.TryParse(statusCodeStr, out int statusCode))
            {
                switch (statusCode)
                {
                    case 401:
                        HandleNexusModsTokenExpired();
                        errorMessage = "无法访问 NexusMods：登录已过期。\n\n请前往「设置」→「API 与账户设置」重新登录 NexusMods 账户。";
                        break;
                    case 403:
                        errorMessage = "无法访问 NexusMods：权限被拒绝。\n\n请检查您的 API Key 是否有效，或稍后重试。";
                        break;
                    case 404:
                        errorMessage = "无法访问 NexusMods：资源不存在。\n\n请检查网络连接或稍后重试。";
                        break;
                    default:
                        errorMessage = $"无法访问 NexusMods：HTTP {statusCode} 错误。\n\n请检查网络连接或稍后重试。";
                        break;
                }
            }
            else
            {
                errorMessage = "无法访问 NexusMods：网络连接失败。\n\n请检查网络连接或稍后重试。";
            }

            // 在 UI 线程显示错误提示
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                // 显示浮窗通知（类似 SMAPI 安装完成的提示）
                SVL.Desktop.Controls.FloatingNotificationControl.Show(
                    title: "SMAPI 版本加载失败",
                    message: errorMessage,
                    autoCloseDelay: 8000,  // 8秒后自动关闭
                    notificationType: NotificationType.Error
                );
            }));

            return (new List<SVL.Core.Stardew.Mod.SMAPI.SmapiVersionInfo>(), string.Empty, 0);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DownloadRightViewModel] 从 NexusMods 加载 SMAPI 版本失败");

            // 显示通用错误提示
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                SVL.Desktop.Controls.FloatingNotificationControl.Show(
                    title: "SMAPI 版本加载失败",
                    message: $"加载 SMAPI 版本时发生错误：{ex.Message}\n\n请查看日志获取详细信息。",
                    autoCloseDelay: 8000,
                    notificationType: NotificationType.Error
                );
            }));

            return (new List<SVL.Core.Stardew.Mod.SMAPI.SmapiVersionInfo>(), string.Empty, 0);
        }
    }

    /// <summary>
    /// 从 HttpRequestException 中提取 HTTP 状态码
    /// </summary>
    private string ExtractStatusCode(System.Net.Http.HttpRequestException ex)
    {
        if (ex == null) return null;

        // 尝试从消息中提取状态码
        var message = ex.Message;
        if (string.IsNullOrEmpty(message)) return null;

        // 查找 "404" 或 "Not Found" 等模式
        var match = System.Text.RegularExpressions.Regex.Match(message, @"(\d{3})");
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        // 检查 InnerException
        if (ex.InnerException != null)
        {
            if (ex.InnerException is System.Net.WebException webEx && webEx.Response != null)
            {
                var response = (System.Net.HttpWebResponse)webEx.Response;
                return ((int)response.StatusCode).ToString();
            }
        }

        return null;
    }

    private bool IsNexusUnauthorizedException(Exception ex)
    {
        return NexusAuthStateHelper.IsUnauthorized(ex);
    }

    /// <summary>
    /// 加载Mod项目（同时从 Curseforge 和 NexusMods 获取）
    /// </summary>
    private async void LoadModItems()
    {
        _hasLoadedModItems = true;
        // 如果有搜索词，执行搜索；否则显示热门Mod
        if (!string.IsNullOrWhiteSpace(ModsSearchText))
        {
            await LoadModItemsFromNexusMods();
        }
        else
        {
            await LoadPopularModsFromMultipleSources();
        }
    }

    /// <summary>
    /// 从多个源加载热门 Mod（Curseforge + NexusMods）
    /// </summary>
    private async Task LoadPopularModsFromMultipleSources()
    {
        Items = new List<DownloadItem>();

        // 确保在加载前认证信息已初始化（从 AppConfig 重新加载最新配置）
        try
        {
            var settings = SVL.Core.Config.AppConfig.GetSettings();

            // NexusMods 现在使用 OAuth 认证，不需要手动设置 API Key
            if (!string.IsNullOrEmpty(settings.NexusModsOAuthToken))
            {
                Log.Info("[DownloadRightViewModel] ✓ NexusMods OAuth Token 已配置");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DownloadRightViewModel] 初始化认证信息失败");
        }

        // 并行加载 Curseforge 和 NexusMods 的热门 Mod
        Status = "正在加载热门 Mod（Curseforge + NexusMods）...";

        var curseforgeTask = LoadModsFromCurseforgeAsync();
        var nexusModsTask = LoadModsFromNexusModsAsync();

        await Task.WhenAll(curseforgeTask, nexusModsTask);

        var curseforgeMods = await curseforgeTask;
        var nexusMods = await nexusModsTask;

        var allMods = new List<DownloadItem>();

        if (curseforgeMods != null && curseforgeMods.Count > 0)
        {
            allMods.AddRange(curseforgeMods.Take(10));
            Log.Info($"[DownloadRightViewModel] ✓ 加载了 {curseforgeMods.Count} 个 Curseforge Mods");
        }

        if (nexusMods != null && nexusMods.Count > 0)
        {
            allMods.AddRange(nexusMods.Take(10));
            Log.Info($"[DownloadRightViewModel] ✓ 加载了 {nexusMods.Count} 个 NexusMods Mods");
        }

        if (allMods.Count > 0)
        {
            Items = allMods;
            FilteredItems = new List<DownloadItem>(allMods);
            Status = $"已加载 {allMods.Count} 个热门 Mod（Curseforge + NexusMods）";
        }
        else
        {
            ShowPlaceholderMods();
        }
    }

    /// <summary>
    /// 从 Curseforge 加载 Mods
    /// </summary>
    private async Task<List<DownloadItem>> LoadModsFromCurseforgeAsync()
    {
        try
        {
            const int stardewValleyGameId = 669; // Stardew Valley 在 Curseforge 的游戏 ID

            var featuredMods = await SVL.Core.Download.CurseforgeApiService.GetFeaturedModsAsync(
                gameId: stardewValleyGameId,
                pageSize: 10
            );

            if (featuredMods?.Data != null)
            {
                var modItems = new List<DownloadItem>();

                var modsList = featuredMods.Data.Popular ?? featuredMods.Data.Featured ?? new List<SVL.Core.Download.CurseforgeApiService.CurseforgeModSearchItem>();

                foreach (var mod in modsList.Take(10))
                {
                    modItems.Add(new DownloadItem
                    {
                        Id = $"cf-{mod.Id}",
                        Name = mod.Name,
                        Author = "Curseforge", // Curseforge Mods 没有单独的作者字段
                        Version = "最新版本",
                        Description = mod.Summary ?? "暂无描述",
                        Thumbnail = "📦", // Curseforge Logo
                        Category = "Mods",
                        DownloadUrl = mod.Logo?.Url ?? "",
                        FileName = mod.Id.ToString(),
                        FileId = mod.LatestFile?.Id
                    });
                }

                return modItems;
            }

            return new List<DownloadItem>();
        }
        catch (Exception ex)
        {
            Log.Warn("[DownloadRightViewModel] 从 Curseforge 加载 Mods 失败", ex);
            return new List<DownloadItem>();
        }
    }

    /// <summary>
    /// 从 NexusMods 加载 Mods
    /// </summary>
    private async Task<List<DownloadItem>> LoadModsFromNexusModsAsync()
    {
        try
        {
            var popularMods = await SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsService.SearchModsAsync(
                query: "SMAPI",
                useCache: AppConfig.GetSettings().EnableNexusModsSearchCache
            );

            if (popularMods != null && popularMods.Count > 0)
            {
                var modItems = new List<DownloadItem>();
                foreach (var mod in popularMods.Take(10))
                {
                    if (mod.ModId <= 0)
                    {
                        Log.Warn($"[DownloadRightViewModel] 跳过无效 NexusMods ModId: {mod.Name} (ModId={mod.ModId})");
                        continue;
                    }

                    modItems.Add(new DownloadItem
                    {
                        Id = $"nexus-{mod.ModId}",
                        Name = mod.Name,
                        Author = mod.Author,
                        Version = "最新版本",
                        Description = mod.Summary ?? "暂无描述",
                        Thumbnail = "🎮", // NexusMods Logo
                        Category = "Mods",
                        DownloadUrl = mod.PictureUrl ?? "",
                        FileName = mod.ModId.ToString()
                    });
                }

                return modItems;
            }

            return new List<DownloadItem>();
        }
        catch (SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsTokenExpiredException)
        {
            Log.Warn("[DownloadRightViewModel] NexusMods Token 已过期");
            HandleNexusModsTokenExpired();
            return new List<DownloadItem>();
        }
        catch (Exception ex)
        {
            Log.Warn("[DownloadRightViewModel] 从 NexusMods 加载 Mods 失败", ex);
            return new List<DownloadItem>();
        }
    }

    /// <summary>
    /// 从 NexusMods 搜索并加载 Mod
    /// </summary>
    private async Task LoadModItemsFromNexusMods()
    {
        Items = new List<DownloadItem>();

        var searchText = string.IsNullOrWhiteSpace(ModsSearchText) ? "SMAPI" : ModsSearchText;

        Status = $"正在搜索「{searchText}」...";
        try
        {
            var searchResults = await SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsService.SearchModsAsync(
                query: searchText,
                useCache: AppConfig.GetSettings().EnableNexusModsSearchCache
            );

            if (searchResults != null && searchResults.Count > 0)
            {
                var modItems = new List<DownloadItem>();
                foreach (var mod in searchResults)
                {
                    if (mod.ModId <= 0)
                    {
                        Log.Warn($"[DownloadRightViewModel] 跳过无效 NexusMods ModId: {mod.Name} (ModId={mod.ModId})");
                        continue;
                    }

                    modItems.Add(new DownloadItem
                    {
                        Id = $"nexus-{mod.ModId}",
                        Name = mod.Name,
                        Author = mod.Author,
                        Version = "最新版本",
                        Description = mod.Summary ?? "暂无描述",
                        Thumbnail = "📦",
                        Category = "Mods",
                        DownloadUrl = mod.PictureUrl ?? "",
                        FileName = mod.ModId.ToString()
                    });
                }

                Items = modItems;
                FilteredItems = new List<DownloadItem>(modItems);

                TotalPages = 1;
                HasNextPage = false;
                HasPreviousPage = false;

                Status = $"找到 {modItems.Count} 个结果";
                Log.Info($"[DownloadRightViewModel] 搜索「{searchText}」完成，共 {modItems.Count} 个结果");
            }
            else
            {
                Items = new List<DownloadItem>();
                FilteredItems = new List<DownloadItem>();
                Status = $"未找到匹配「{searchText}」的 Mod";
                Log.Info($"[DownloadRightViewModel] 搜索「{searchText}」无结果");
            }
        }
        catch (SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsTokenExpiredException)
        {
            Log.Warn("[DownloadRightViewModel] NexusMods Token 已过期");
            HandleNexusModsTokenExpired();
            ShowPlaceholderMods();
        }
        catch (System.Net.Http.HttpRequestException httpEx)
        {
            Log.Error(httpEx, "[DownloadRightViewModel] 搜索 NexusMods Mod 失败");

            string errorMessage;
            var statusCodeStr = ExtractStatusCode(httpEx);
            if (!string.IsNullOrEmpty(statusCodeStr) && int.TryParse(statusCodeStr, out int statusCode))
            {
                switch (statusCode)
                {
                    case 401:
                        HandleNexusModsTokenExpired();
                        errorMessage = "无法访问 NexusMods：未登录或登录已过期。\n\n请前往「设置」→「API 与账户设置」登录 NexusMods 账户。";
                        break;
                    case 403:
                        errorMessage = "无法访问 NexusMods：需要 Premium 权限。\n\n请使用 NexusMods Premium 账户登录，或使用浏览器下载。";
                        break;
                    default:
                        errorMessage = $"无法访问 NexusMods：HTTP {statusCode} 错误。\n\n请检查网络连接或稍后重试。";
                        break;
                }
            }
            else
            {
                errorMessage = "无法访问 NexusMods：网络连接失败。\n\n请检查网络连接或稍后重试。";
            }

            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                SVL.Desktop.Controls.FloatingNotificationControl.Show(
                    title: "搜索失败",
                    message: errorMessage,
                    autoCloseDelay: 8000,
                    notificationType: NotificationType.Error
                );
            }));

            ShowPlaceholderMods();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DownloadRightViewModel] 搜索 NexusMods Mod 失败");

            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                SVL.Desktop.Controls.FloatingNotificationControl.Show(
                    title: "搜索失败",
                    message: $"搜索时发生错误：{ex.Message}\n\n请查看日志获取详细信息。",
                    autoCloseDelay: 8000,
                    notificationType: NotificationType.Error
                );
            }));

            ShowPlaceholderMods();
        }
    }

    /// <summary>
    /// 显示占位符Mod（当API未配置或加载失败时）
    /// </summary>
    private void ShowPlaceholderMods()
    {
        Items = new List<DownloadItem>
        {
            new DownloadItem
            {
                Id = "cpv",
                Name = "Content Patcher",
                Author = "Pathoschild",
                Version = "2.1.0",
                Description = "一个用于加载和显示自定义内容的框架，允许 Mod 修改游戏资源而无需替换文件。",
                Thumbnail = "📦",
                Category = "Mods"
            },
            new DownloadItem
            {
                Id = "svi",
                Name = "Stardew Valley Expanded",
                Author = "FlashShifter",
                Version = "1.14.0",
                Description = "一个大型扩展 Mod，添加了新的 NPC、地点、事件、剧情等内容。",
                Thumbnail = "🌾",
                Category = "Mods"
            }
        };
        FilteredItems = new List<DownloadItem>(Items);
        Status = "API 未配置或加载失败，显示占位符";
    }

    /// <summary>
    /// 加载整合包项目
    /// </summary>
    private void LoadModpackItems()
    {
        _hasLoadedModpackItems = true;
        Items = new List<DownloadItem>();
        FilteredItems = new List<DownloadItem>();
        _ = LoadModpackItemsFromMultipleSourcesAsync();
    }

    private async Task LoadModpackItemsFromMultipleSourcesAsync()
    {
        try
        {
            Status = "正在加载整合包（Curseforge + Nexus Collection）...";

            var query = string.IsNullOrWhiteSpace(ModsSearchText) ? string.Empty : ModsSearchText.Trim();
            var settings = AppConfig.GetSettings();

            var curseforgeTask = LoadModpacksFromCurseforgeAsync(query);
            var nexusTask = LoadModpacksFromNexusCollectionsAsync(query, !string.IsNullOrWhiteSpace(settings.NexusModsOAuthToken));

            await Task.WhenAll(curseforgeTask, nexusTask);

            var curseforgeItems = await curseforgeTask;
            var nexusItems = await nexusTask;

            var result = new List<DownloadItem>();
            result.AddRange(curseforgeItems);
            result.AddRange(nexusItems);

            if (result.Count == 0)
            {
                ShowPlaceholderModpacks();
                return;
            }

            Items = result
                .OrderByDescending(i => i.DownloadSize)
                .ThenBy(i => i.Name)
                .ToList();
            FilteredItems = new List<DownloadItem>(Items);

            TotalPages = 1;
            CurrentPage = 1;
            HasNextPage = false;
            HasPreviousPage = false;

            Status = $"已加载 {Items.Count} 个整合包（Curseforge: {curseforgeItems.Count}, Nexus Collection: {nexusItems.Count}）";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DownloadRightViewModel] 加载整合包失败");
            ShowPlaceholderModpacks();
        }
    }

    private async Task<List<DownloadItem>> LoadModpacksFromCurseforgeAsync(string query)
    {
        var result = new List<DownloadItem>();

        try
        {
            var modpacks = await CurseforgeApiService.SearchModpacksAsync(query, gameId: 669, pageSize: 50, index: 0);
            foreach (var item in modpacks.Take(20))
            {
                var logo = item.Logo?.ThumbnailUrl ?? item.Logo?.Url;
                var downloadHint = item.DownloadCount > 0 ? item.DownloadCount : 0;

                result.Add(new DownloadItem
                {
                    Id = $"cfpack-{item.Id}",
                    Name = item.Name,
                    Author = "Curseforge",
                    Version = item.LatestFile?.DisplayName ?? "最新版本",
                    Description = string.IsNullOrWhiteSpace(item.Summary) ? "Curseforge Modpack" : item.Summary,
                    Thumbnail = string.IsNullOrWhiteSpace(logo) ? "🧩" : logo,
                    DownloadUrl = item.Links?.WebsiteUrl ?? $"https://www.curseforge.com/stardewvalley/modpacks/{item.Slug}",
                    Category = "Modpacks",
                    Source = "Curseforge",
                    DownloadSize = downloadHint,
                    FileName = item.Slug ?? item.Id.ToString(),
                    Versions = new List<DownloadItem>
                    {
                        new DownloadItem
                        {
                            Id = $"cfpack-version-{item.Id}",
                            Name = item.Name,
                            Version = item.LatestFile?.DisplayName ?? "最新版本",
                            Description = "打开来源页面下载 Modpack",
                            DownloadUrl = item.Links?.WebsiteUrl ?? $"https://www.curseforge.com/stardewvalley/modpacks/{item.Slug}",
                            Source = "Curseforge"
                        }
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn("[DownloadRightViewModel] 加载 Curseforge Modpack 失败", ex);
        }

        return result;
    }

    private async Task<List<DownloadItem>> LoadModpacksFromNexusCollectionsAsync(string query, bool hasNexusLogin)
    {
        var result = new List<DownloadItem>();

        if (!hasNexusLogin)
        {
            if (!_hasShownModpackConfigWarning)
            {
                _hasShownModpackConfigWarning = true;
                ApiSettingsNavigationHelper.ShowApiConfigWarningAndNavigate(
                    "DownloadRightViewModel",
                    "⚠️ NexusMods 未登录\n请在设置页面登录后加载 Nexus Collection。"
                );
            }

            return result;
        }

        try
        {
            var collections = await SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsService.SearchCollectionsAsync(
                query,
                page: Math.Max(1, CurrentPage),
                pageSize: 20,
                useCache: AppConfig.GetSettings().EnableNexusModsSearchCache);

            foreach (var collection in collections)
            {
                var updated = collection.UpdatedAt != default ? collection.UpdatedAt.ToString("yyyy-MM-dd") : "最新";
                var displayUrl = string.IsNullOrWhiteSpace(collection.Url)
                    ? $"https://next.nexusmods.com/stardewvalley/collections/{collection.CollectionId}"
                    : collection.Url;

                var downloadItem = new DownloadItem
                {
                    Id = $"nexus-collection-{collection.CollectionId}",
                    Name = collection.Name,
                    Author = string.IsNullOrWhiteSpace(collection.Author) ? "NexusMods" : collection.Author,
                    Version = $"更新于 {updated}",
                    Description = string.IsNullOrWhiteSpace(collection.Summary) ? "Nexus Collection" : collection.Summary,
                    Thumbnail = string.IsNullOrWhiteSpace(collection.PictureUrl) ? "🗂️" : collection.PictureUrl,
                    DownloadUrl = displayUrl,
                    Category = "Modpacks",
                    Source = "NexusCollection",
                    DownloadSize = collection.Downloads,
                    FileName = collection.CollectionId.ToString(),
                    Versions = new List<DownloadItem>
                    {
                        new DownloadItem
                        {
                            Id = $"nexus-collection-version-{collection.CollectionId}",
                            Name = collection.Name,
                            Version = $"更新于 {updated}",
                            Description = "打开来源页面下载 Collection",
                            DownloadUrl = displayUrl,
                            Source = "NexusCollection"
                        }
                    }
                };

                // 异步加载 Collection 图标（从 NexusMods 获取）
                if (!string.IsNullOrWhiteSpace(collection.PictureUrl))
                {
                    downloadItem.IconUrl = collection.PictureUrl;
                    _ = downloadItem.LoadIconAsync();
                }

                result.Add(downloadItem);
            }
        }
        catch (SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsTokenExpiredException)
        {
            HandleNexusModsTokenExpired();
        }
        catch (Exception ex)
        {
            Log.Warn("[DownloadRightViewModel] 加载 Nexus Collection 失败", ex);
        }

        return result;
    }

    private void ShowPlaceholderModpacks()
    {
        Items = new List<DownloadItem>
        {
            new DownloadItem
            {
                Id = "modpack-placeholder-cf",
                Name = "Curseforge Modpack（示例）",
                Author = "Curseforge",
                Version = "等待配置",
                Description = "请检查网络连接后重试加载 Curseforge 整合包。",
                Thumbnail = "🧩",
                Category = "Modpacks"
            },
            new DownloadItem
            {
                Id = "modpack-placeholder-nx",
                Name = "Nexus Collection（示例）",
                Author = "NexusMods",
                Version = "等待登录",
                Description = "请在设置中登录 NexusMods 后加载 Collection。",
                Thumbnail = "🗂️",
                Category = "Modpacks"
            }
        };
        FilteredItems = new List<DownloadItem>(Items);
        Status = "整合包来源未就绪，请检查网络或登录状态";
    }

    // Utilities category removed

    [RelayCommand]
    private async Task DownloadItemAsync(DownloadItem item)
    {
        if (item == null) return;

        IsDownloading = true;
        DownloadStatus = $"正在准备下载 {item.Name}...";
        DownloadProgress = 0;

        try
        {
            switch (_currentCategory)
            {
                case DownloadCategory.SMAPI:
                    await DownloadSMAPIAsync(item);
                    break;
                case DownloadCategory.Mods:
                    await DownloadModAsync(item);
                    break;
                case DownloadCategory.Modpacks:
                    await DownloadModpackAsync(item);
                    break;
            }
        }
        catch (System.Exception ex)
        {
            DownloadStatus = $"下载失败: {ex.Message}";
        }
        finally
        {
            IsDownloading = false;
        }
    }

    private Task DownloadModpackAsync(DownloadItem item)
    {
        try
        {
            if (item == null || string.IsNullOrWhiteSpace(item.DownloadUrl))
            {
                DownloadStatus = "整合包下载失败：缺少来源链接";
                return Task.CompletedTask;
            }

            ProcessEx.OpenUrl(item.DownloadUrl);

            DownloadStatus = $"已打开来源页面：{item.Name}";
            FloatingNotificationControl.Show(
                title: "已打开来源页面",
                message: "请在来源页面完成整合包下载，后续将接入自动化安装流程。",
                autoCloseDelay: 5000,
                notificationType: NotificationType.Info
            );
        }
        catch (Exception ex)
        {
            DownloadStatus = $"整合包下载失败: {ex.Message}";
            Log.Error(ex, "[DownloadRightViewModel] 打开整合包来源页面失败");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 下载 SMAPI
    /// </summary>
    private async Task DownloadSMAPIAsync(DownloadItem item)
    {
        // 获取要安装的版本（从FileName字段获取）
        var version = item.FileName ?? "latest";
        var fileId = item.FileId;  // Curseforge 文件ID
        var downloadUrl = item.DownloadUrl;  // Curseforge 下载 URL

        try
        {
            // 1. 查找/确认游戏本体路径（弹出对话框）
            var gameBasePath = FindGameBasePath();
            if (string.IsNullOrEmpty(gameBasePath))
            {
                Log.Info("[DownloadRightViewModel] 未找到游戏本体路径");
                DownloadStatus = "需要选择游戏安装目录";
                return;
            }

            // 2. 显示 BASE 路径确认对话框
            var owner = System.Windows.Application.Current.MainWindow;
            var basePathConfirmDialog = new SVL.Desktop.Controls.GamePathConfirmDialog();
            basePathConfirmDialog.SetGamePath(gameBasePath);
            if (owner != null)
            {
                basePathConfirmDialog.Owner = owner;
            }

            var basePathResult = basePathConfirmDialog.ShowDialog();
            if (basePathResult != true)
            {
                Log.Info("[DownloadRightViewModel] 用户取消了 BASE 路径确认");
                DownloadStatus = "已取消安装";
                return;
            }

            // 获取确认的路径（用户可能通过"重新选择"更改了路径）
            gameBasePath = basePathConfirmDialog.GetSelectedPath() ?? gameBasePath;
            Log.Info($"[DownloadRightViewModel] BASE 路径确认: {gameBasePath}");

            // 3. 显示实例名称输入对话框
            var dialog = new SVL.Desktop.Controls.InstanceNameDialog();

            // *** 设置重复检测回调：仅基于 versions 文件夹 ***
            dialog.CheckNameExists = (name) =>
            {
                // 检查 versions 文件夹中是否存在同名目录
                var versionPath = System.IO.Path.Combine(gameBasePath, "versions", name);
                return System.IO.Directory.Exists(versionPath);
            };

            // owner 已在上面定义，直接使用
            if (owner != null)
            {
                dialog.Owner = owner;
            }

            var result = dialog.ShowDialog();
            if (result != true || string.IsNullOrEmpty(dialog.InstanceName))
            {
                Log.Info("[DownloadRightViewModel] 用户取消了实例创建");
                return;
            }

            var instanceName = dialog.InstanceName;
            var debugMode = dialog.DebugMode;  // 获取 Debug 模式设置

            // 3. 根据来源选择下载方式
            SVL.Core.Download.SmapiDownloadTask? downloadTask = null;

            if (item.Author == "NexusMods" || item.Id.Contains("NexusMods"))
            {
                // NexusMods 下载：使用专门的下载函数（支持浏览器自动下载）
                // 注意：此函数会在成功后自行创建并添加任务到 DownloadManager
                if (fileId.HasValue)
                {
                    await DownloadSmapiFromNexusModsAsync(gameBasePath, instanceName, fileId.Value, debugMode);
                }
                else
                {
                    DownloadStatus = "❌ NexusMods 文件 ID 未找到";
                    Log.Error("[DownloadRightViewModel] NexusMods 文件 ID 为空");
                    return;
                }
            }
            else
            {
                // Curseforge/GitHub 下载：创建标准下载任务
                SVL.Core.Stardew.Mod.SMAPI.SmapiSource source;
                long? nexusModsFileId = null;
                string? nexusModsDownloadUrl = null;

                if (fileId.HasValue)
                {
                    source = SVL.Core.Stardew.Mod.SMAPI.SmapiSource.Curseforge;
                }
                else
                {
                    source = SVL.Core.Stardew.Mod.SMAPI.SmapiSource.GitHub;
                }

                downloadTask = new SVL.Core.Download.SmapiDownloadTask(
                    gameBasePath,
                    instanceName,
                    version,
                    source,
                    nexusModsFileId,
                    nexusModsDownloadUrl,
                    debugMode
                );

                await SVL.Core.Download.DownloadManager.Instance.AddTaskAsync(downloadTask);
            }

            if (debugMode)
            {
                Log.Info($"[DownloadRightViewModel] Debug 模式已启用，安装失败时将保留文件");
            }

            DownloadStatus = $"已添加到下载队列: SMAPI {version} - {instanceName}";
            if (downloadTask != null)
            {
                Log.Info($"[DownloadRightViewModel] 已添加下载任务: {downloadTask.Name}");
            }
            else
            {
                Log.Info($"[DownloadRightViewModel] 已添加下载任务: SMAPI {version} - {instanceName}");
            }
        }
        catch (Exception ex)
        {
            DownloadStatus = $"下载失败: {ex.Message}";
            Log.Error(ex, "[DownloadRightViewModel] SMAPI 下载失败");
        }
    }

    /// <summary>
    /// 从 NexusMods 下载 SMAPI
    /// </summary>
    private async Task DownloadSmapiFromNexusModsAsync(string gameBasePath, string instanceName, long fileId, bool debugMode)
    {
        const long SMAPI_MOD_ID = 2400; // SMAPI 在 NexusMods 上的 mod ID

        SVL.Core.Download.PlaceholderDownloadTask? placeholderTask = null;

        try
        {
            DownloadStatus = $"正在从 NexusMods 下载 SMAPI...";

            // *** 优化：立即创建占位任务（让右下角按钮立即显示） ***
            // 先使用一个通用的名称和状态，等获取到版本信息后再更新
            placeholderTask = new SVL.Core.Download.PlaceholderDownloadTask(
                $"SMAPI - {instanceName}",
                SVL.Core.Download.DownloadTaskType.SMAPI,
                "正在获取版本信息..."
            );

            await SVL.Core.Download.DownloadManager.Instance.AddTaskAsync(placeholderTask);
            Log.Info($"[DownloadRightViewModel] 已创建占位任务: {placeholderTask.Name}");

            // 获取SMAPI的文件列表（异步，不会阻塞UI）
            var files = await SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsService.GetModFilesAsync(SMAPI_MOD_ID);
            if (files == null || files.Count == 0)
            {
                DownloadStatus = "未找到 SMAPI 文件";
                Log.Error("[DownloadRightViewModel] NexusMods 未找到 SMAPI 文件");

                // 更新占位任务为失败状态
                SVL.Core.Download.DownloadManager.Instance.UpdateTaskStatus(
                    placeholderTask.Id,
                    status: SVL.Core.Download.DownloadTaskStatus.Failed,
                    statusMessage: "未找到 SMAPI 文件",
                    progress: 0
                );
                return;
            }

            // 如果没有指定fileId，使用最新的文件
            if (fileId == 0)
            {
                var latestFile = files.OrderByDescending(f => f.UploadedTime).FirstOrDefault();
                if (latestFile == null)
                {
                    DownloadStatus = "未找到最新的 SMAPI 版本";
                    Log.Error("[DownloadRightViewModel] 未找到最新的 SMAPI 版本");

                    // 更新占位任务为失败状态
                    SVL.Core.Download.DownloadManager.Instance.UpdateTaskStatus(
                        placeholderTask.Id,
                        status: SVL.Core.Download.DownloadTaskStatus.Failed,
                        statusMessage: "未找到最新的 SMAPI 版本",
                        progress: 0
                    );
                    return;
                }
                fileId = latestFile.GetFileIdLong();
            }

            // *** 更新占位任务名称（使用实际版本号） ***
            var version = files.FirstOrDefault(f => f.GetFileIdLong() == fileId)?.Version ?? "未知版本";
            placeholderTask.Name = $"SMAPI {version} - {instanceName}";
            SVL.Core.Download.DownloadManager.Instance.UpdateTaskStatus(
                placeholderTask.Id,
                status: SVL.Core.Download.DownloadTaskStatus.Pending,
                statusMessage: "准备下载 SMAPI...",
                progress: 0
            );
            Log.Info($"[DownloadRightViewModel] 已更新占位任务名称: {placeholderTask.Name}");

            // *** 步骤2：尝试 NexusMods API 下载 ***
            var tempDir = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "SVL",
                "temp"
            );

            string? downloadedZipPath = null;

            // 确保目录存在
            if (!System.IO.Directory.Exists(tempDir))
            {
                System.IO.Directory.CreateDirectory(tempDir);
            }

            DownloadStatus = "正在从 NexusMods 下载 SMAPI...";
            Log.Info($"[DownloadRightViewModel] 尝试 NexusMods API 下载: modId={SMAPI_MOD_ID}, fileId={fileId}");

            // *** 检查缓存 ***
            var cachedPath = NexusModsCacheService.Get(SMAPI_MOD_ID, fileId);
            if (!string.IsNullOrEmpty(cachedPath))
            {
                Log.Info($"[DownloadRightViewModel] ✓ 使用缓存文件: {cachedPath}");
                DownloadStatus = "从缓存加载 SMAPI...";
                SVL.Core.Download.DownloadManager.Instance.UpdateTaskStatus(
                    placeholderTask.Id,
                    status: SVL.Core.Download.DownloadTaskStatus.Downloading,
                    statusMessage: "从缓存加载...",
                    progress: 50
                );
                downloadedZipPath = cachedPath;
            }

            // *** 如果没有缓存，从 NexusMods 下载 ***
            bool apiSuccess;
            if (string.IsNullOrEmpty(downloadedZipPath))
            {
                apiSuccess = await NexusModsService.DownloadModAsync(
                SMAPI_MOD_ID,
                fileId,
                tempDir,
                null,  // 不使用 .nxm 链接
                placeholderTask.CancellationToken  // 传递取消令牌
            );

            if (apiSuccess)
            {
                // API 下载成功
                var expectedZipPath = System.IO.Path.Combine(tempDir, $"mod_{SMAPI_MOD_ID}_{fileId}.zip");
                if (System.IO.File.Exists(expectedZipPath))
                {
                    downloadedZipPath = expectedZipPath;
                }
                else
                {
                    // 尝试查找目录中的zip文件
                    var zipFiles = System.IO.Directory.GetFiles(tempDir, "*.zip");
                    if (zipFiles.Length > 0)
                    {
                        downloadedZipPath = zipFiles[0];
                    }
                }

                if (!string.IsNullOrEmpty(downloadedZipPath))
                {
                    Log.Info($"[DownloadRightViewModel] ✓ NexusMods API 下载成功: {downloadedZipPath}");

                    // *** 保存到缓存 ***
                    await NexusModsCacheService.SaveAsync(downloadedZipPath, SMAPI_MOD_ID, fileId);
                }
                else
                {
                    Log.Warn("[DownloadRightViewModel] NexusMods API 下载返回成功，但未找到文件");
                    apiSuccess = false;
                }
            }
            }
            else
            {
                // 使用缓存成功
                apiSuccess = true;
            }

            // *** 步骤3：如果 API 下载失败，检查会员状态并显示引导 ***
            if (!apiSuccess)
            {
                Log.Warn("[DownloadRightViewModel] NexusMods API 下载失败，检查会员状态");

                // *** 检查占位任务是否已被用户取消 ***
                var currentTask = SVL.Core.Download.DownloadManager.Instance.GetTask(placeholderTask.Id);
                if (currentTask != null && currentTask.Status == SVL.Core.Download.DownloadTaskStatus.Cancelled)
                {
                    Log.Info("[DownloadRightViewModel] 占位任务已被用户取消，不显示浏览器下载引导");
                    return;
                }

                // 检查是否为 Premium 会员
                var settings = AppConfig.GetSettings();
                var isPremium = !string.IsNullOrEmpty(settings.NexusModsOAuthMembershipType) &&
                                settings.NexusModsOAuthMembershipType.IndexOf("premium", StringComparison.OrdinalIgnoreCase) >= 0;

                if (isPremium)
                {
                    // Premium 会员下载失败，显示错误
                    Log.Error("[DownloadRightViewModel] Premium 会员下载失败");
                    DownloadStatus = "下载失败";
                    SVL.Core.Download.DownloadManager.Instance.UpdateTaskStatus(
                        placeholderTask.Id,
                        status: SVL.Core.Download.DownloadTaskStatus.Failed,
                        statusMessage: "Premium 会员下载失败，请检查网络连接",
                        progress: 0
                    );
                    // 不删除任务，保留失败状态供用户查看
                    return;
                }
                else
                {
                    // *** 再次检查占位任务是否已被用户取消 ***
                    currentTask = SVL.Core.Download.DownloadManager.Instance.GetTask(placeholderTask.Id);
                    if (currentTask != null && currentTask.Status == SVL.Core.Download.DownloadTaskStatus.Cancelled)
                    {
                        Log.Info("[DownloadRightViewModel] 占位任务已被用户取消，不显示浏览器下载引导");
                        return;
                    }

                    // 非 Premium 用户，显示浏览器下载引导
                    Log.Info("[DownloadRightViewModel] 非 Premium 用户，显示浏览器下载引导");

                    // 注册待处理的浏览器下载任务
                    _pendingBrowserDownloads[(SMAPI_MOD_ID, fileId)] = new PendingBrowserDownload
                    {
                        PlaceholderTaskId = placeholderTask.Id,
                        TempDir = tempDir,
                        GameBasePath = gameBasePath,
                        InstanceName = instanceName,
                        DebugMode = debugMode,
                        Version = version  // 保存版本号
                    };

                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var guideDialog = new SVL.Desktop.Controls.BrowserDownloadGuideDialog(
                            SMAPI_MOD_ID,
                            fileId,
                            "stardewvalley"
                        );
                        guideDialog.Owner = System.Windows.Application.Current.MainWindow;
                        guideDialog.ShowWithBlur(System.Windows.Application.Current.MainWindow);
                    });

                    // 标记任务为等待 NXM 协议回调
                    SVL.Core.Download.DownloadManager.Instance.UpdateTaskStatus(
                        placeholderTask.Id,
                        status: SVL.Core.Download.DownloadTaskStatus.Pending,
                        statusMessage: "等待浏览器下载（请点击下载按钮）...",
                        progress: 5
                    );

                    // 等待下载完成（由 NXM 协议处理器触发）
                    DownloadStatus = "等待浏览器下载...";

                    // 不删除占位任务，等待 NXM 协议回调
                    return;
                }
            }

            // *** 步骤4：下载成功，删除占位任务，创建真正的安装任务 ***
            if (!string.IsNullOrEmpty(downloadedZipPath) && System.IO.File.Exists(downloadedZipPath))
            {
                // *** 检查占位任务是否已被用户取消 ***
                var currentTask = SVL.Core.Download.DownloadManager.Instance.GetTask(placeholderTask.Id);
                if (currentTask != null && currentTask.Status == SVL.Core.Download.DownloadTaskStatus.Cancelled)
                {
                    Log.Info("[DownloadRightViewModel] 占位任务已被用户取消，取消安装流程");

                    // 删除已下载的文件
                    try
                    {
                        if (System.IO.File.Exists(downloadedZipPath))
                        {
                            if (ModBackupService.MovePathToRecycleBin(downloadedZipPath))
                            {
                                Log.Info($"[DownloadRightViewModel] 已将已下载文件移到回收站: {downloadedZipPath}");
                            }
                            else
                            {
                                Log.Warn($"[DownloadRightViewModel] 无法将已下载文件移到回收站: {downloadedZipPath}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("[DownloadRightViewModel] 删除已下载文件失败", ex);
                    }

                    return;
                }

                // 删除占位任务
                SVL.Core.Download.DownloadManager.Instance.RemoveTask(placeholderTask.Id);
                Log.Info($"[DownloadRightViewModel] 已删除占位任务: {placeholderTask.Id}");

                // 创建真正的 SmapiDownloadTask（使用本地文件）
                var installTask = new SVL.Core.Download.SmapiDownloadTask(
                    gameBasePath,
                    instanceName,
                    downloadedZipPath,
                    SVL.Core.Stardew.Mod.SMAPI.SmapiSource.NexusMods,
                    debugMode,
                    version  // 传递版本号，确保任务名称一致
                );

                await SVL.Core.Download.DownloadManager.Instance.AddTaskAsync(installTask);
                Log.Info($"[DownloadRightViewModel] 已创建安装任务: {installTask.Name}");

                // 显示开始安装通知
                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    SVL.Desktop.Controls.FloatingNotificationControl.Show(
                        title: "开始安装",
                        message: $"✓ 已下载 SMAPI {version}\n\n点击右下角「任务状态」查看安装进度。",
                        autoCloseDelay: 3000
                    );
                }));

                return;
            }
            else
            {
                Log.Error("[DownloadRightViewModel] 下载失败：未找到下载的文件");
                DownloadStatus = "下载失败：未找到文件";
                // 更新占位任务状态为失败
                SVL.Core.Download.DownloadManager.Instance.UpdateTaskStatus(
                    placeholderTask.Id,
                    status: SVL.Core.Download.DownloadTaskStatus.Failed,
                    statusMessage: "下载失败：未找到文件",
                    progress: 0
                );
                return;
            }
        }
        catch (SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsTokenExpiredException)
        {
            Log.Warn("[DownloadRightViewModel] NexusMods Token 已过期");
            HandleNexusModsTokenExpired();
            DownloadStatus = "下载失败：登录已过期";
        }
        catch (Exception ex)
        {
            DownloadStatus = $"下载失败: {ex.Message}";
            Log.Error(ex, "[DownloadRightViewModel] 从 NexusMods 下载 SMAPI 失败");
        }
    }

    /// <summary>
    /// 查找游戏本体路径
    /// 1. 首先检查配置文件
    /// 2. 然后从 instances.json 中查找
    /// 3. 最后提示用户手动选择
    /// </summary>
    private string? FindGameBasePath()
    {
        var owner = System.Windows.Application.Current.MainWindow;

        // 步骤1：检查配置文件
        var configuredPath = SVL.Core.Config.GamePathConfig.GetGamePath();
        if (!string.IsNullOrEmpty(configuredPath) && System.IO.Directory.Exists(configuredPath))
        {
            Log.Info($"[DownloadRightViewModel] 从配置文件找到游戏路径: {configuredPath}");
            return configuredPath;
        }

        // 步骤2：从 instances.json 中查找游戏路径
        var instances = SVL.Core.Stardew.Instance.SettingsService.LoadInstances();
        if (instances != null && instances.Count > 0)
        {
            Log.Info($"[DownloadRightViewModel] 从 instances.json 加载了 {instances.Count} 个实例");

            // 使用第一个实例的 GamePath
            foreach (var instance in instances)
            {
                var gamePath = instance.GamePath;
                if (string.IsNullOrEmpty(gamePath))
                {
                    continue;
                }

                Log.Info($"[DownloadRightViewModel] 检查实例: {instance.Name}, GamePath: {gamePath}");

                // 验证路径
                if (System.IO.Directory.Exists(gamePath))
                {
                    var exePath = System.IO.Path.Combine(gamePath, "Stardew Valley.exe");
                    if (System.IO.File.Exists(exePath))
                    {
                        Log.Info($"[DownloadRightViewModel] ✓ 找到游戏文件: {exePath}");

                        // 显示确认对话框
                        var confirmDialog = new SVL.Desktop.Controls.GamePathConfirmDialog();
                        confirmDialog.SetGamePath(gamePath);
                        if (owner != null)
                        {
                            confirmDialog.Owner = owner;
                        }

                        var confirmResult = confirmDialog.ShowDialog();
                        if (confirmResult == true)
                        {
                            // 用户确认使用此路径，保存配置
                            SVL.Core.Config.GamePathConfig.SaveGamePath(gamePath);
                            Log.Info($"[DownloadRightViewModel] ✓ 用户确认使用路径: {gamePath}");
                            return gamePath;
                        }
                        else
                        {
                            // 用户拒绝，继续查找其他实例
                            Log.Info("[DownloadRightViewModel] 用户拒绝了此路径，继续查找其他实例");
                            continue;
                        }
                    }
                    else
                    {
                        Log.Warn($"[DownloadRightViewModel] 游戏文件不存在: {exePath}");
                    }
                }
                else
                {
                    Log.Warn($"[DownloadRightViewModel] 目录不存在: {gamePath}");
                }
            }
        }
        else
        {
            Log.Info("[DownloadRightViewModel] instances.json 为空或未加载");
        }

        // 步骤3：显示路径选择对话框
        Log.Info("[DownloadRightViewModel] 显示游戏路径选择对话框");
        var pathDialog = new SVL.Desktop.Controls.GamePathSelectionDialog();
        if (owner != null)
        {
            pathDialog.Owner = owner;
        }

        var pathResult = pathDialog.ShowDialog();
        if (pathResult == true && !string.IsNullOrEmpty(pathDialog.SelectedPath))
        {
            // 保存配置
            SVL.Core.Config.GamePathConfig.SaveGamePath(pathDialog.SelectedPath);
            return pathDialog.SelectedPath;
        }

        return null;
    }

    /// <summary>
    /// 下载 Mod
    /// </summary>
    private async Task DownloadModAsync(DownloadItem item)
    {
        try
        {
            DownloadStatus = $"正在准备下载 {item.Name}...";

            // 检查是否为NexusMods资源
            if (item.Id.StartsWith("nexus-") && int.TryParse(item.FileName, out int nexusModId))
            {
                // 从 NexusMods 下载
                await DownloadNexusModAsync(item, nexusModId);
            }
            // 检查是否为Curseforge资源
            else if (item.Id.StartsWith("cf-") && int.TryParse(item.FileName, out int cfModId))
            {
                // 从 Curseforge 下载
                await DownloadCurseforgeModAsync(item, cfModId);
            }
            else
            {
                // 占位符Mod，显示提示
                await Task.Delay(1000);
                DownloadProgress = 100;
                DownloadStatus = $"{item.Name} 下载完成！（占位符功能）";
            }
        }
        catch (Exception ex)
        {
            DownloadStatus = $"下载失败: {ex.Message}";
            Log.Error(ex, $"[DownloadRightViewModel] 下载 Mod 失败: {item.Name}");
        }
    }

    /// <summary>
    /// 从 Curseforge 下载 Mod
    /// </summary>
    private async Task DownloadCurseforgeModAsync(DownloadItem item, int modId)
    {
        try
        {
            DownloadStatus = $"正在获取 {item.Name} 的文件列表...";

            // 获取 Curseforge Mod 文件列表
            var modFiles = await SVL.Core.Download.CurseforgeApiService.GetModFilesAsync(modId);

            if (modFiles == null || modFiles.Count == 0)
            {
                DownloadStatus = $"未找到 {item.Name} 的文件";
                Log.Warn($"[DownloadRightViewModel] Curseforge Mod {modId} 没有可用文件");
                return;
            }

            // 选择主文件（最新的文件）
            var mainFile = modFiles.OrderByDescending(f => f.FileDate).FirstOrDefault();

            if (mainFile == null)
            {
                DownloadStatus = $"未找到 {item.Name} 的主文件";
                return;
            }

            DownloadStatus = $"正在下载 {item.Name} ({mainFile.DisplayName})...";

            // 获取默认实例的Mods路径
            var modsPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SVL",
                "instances",
                "default",
                "Mods"
            );

            // 确保目录存在
            if (!System.IO.Directory.Exists(modsPath))
            {
                System.IO.Directory.CreateDirectory(modsPath);
            }

            // 创建下载任务
            var downloadUrl = mainFile.DownloadUrl;
            var fileName = mainFile.FileName;

            // 使用 ModDownloadTask 下载
            var task = new SVL.Core.Download.ModDownloadTask(
                modId: modId.ToString(),
                modName: item.Name,
                fileName: fileName,
                downloadUrl: downloadUrl,
                targetModsPath: modsPath,
                sourcePlatform: "Curseforge",
                sourceProjectId: modId.ToString(),
                sourceFileId: mainFile.Id.ToString()
            );

            // 添加到下载管理器
            var manager = SVL.Core.Download.DownloadManager.Instance;
            _ = await manager.AddTaskAsync(task);

            DownloadProgress = 100;
            DownloadStatus = $"✓ {item.Name} 已添加到下载队列";
            Log.Info($"[DownloadRightViewModel] ✓ 成功添加 Curseforge Mod 到下载队列: {item.Name}");
        }
        catch (Exception ex)
        {
            DownloadStatus = $"下载失败: {ex.Message}";
            Log.Error(ex, $"[DownloadRightViewModel] 从 Curseforge 下载 Mod 失败: {item.Name}");
        }
    }

    /// <summary>
    /// 从 NexusMods 下载 Mod
    /// </summary>
    private async Task DownloadNexusModAsync(DownloadItem item, int modId)
    {
        DownloadStatus = $"正在获取 {item.Name} 的文件列表...";

        // 获取Mod文件列表（将int转换为long）
        var files = await SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsService.GetModFilesAsync((long)modId);

        if (files == null || files.Count == 0)
        {
            DownloadStatus = $"未找到 {item.Name} 的文件";
            Log.Warn($"[DownloadRightViewModel] Mod {modId} 没有可用文件");
            return;
        }

        // 选择主文件（通常是第一个或最新的，避免可选文件）
        var mainFile = files.FirstOrDefault(f =>
            f.Categories == null ||
            !f.Categories.Any(c =>
                !string.IsNullOrEmpty(c.Name) &&
                c.Name.IndexOf("optional", StringComparison.OrdinalIgnoreCase) >= 0))
                       ?? files[0];

        var fileId = mainFile.GetFileIdLong();
        if (fileId <= 0)
        {
            DownloadStatus = $"未找到 {item.Name} 的有效文件 ID";
            Log.Warn($"[DownloadRightViewModel] Mod {modId} 文件 ID 无效");
            return;
        }

        DownloadStatus = $"正在下载 {item.Name} ({mainFile.FileName})...";

        // 获取默认实例的Mods路径
        var modsPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SVL",
            "instances",
            "default",
            "Mods"
        );

        // 确保目录存在
        if (!System.IO.Directory.Exists(modsPath))
        {
            System.IO.Directory.CreateDirectory(modsPath);
        }

        // 复用 SMAPI 的 Nexus 下载工作流：先下载 zip，再交给 ModDownloadTask 安装
        var tempDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SVL",
            "temp",
            "mods"
        );

        var zipPath = await SVL.Core.Download.NexusMods.NexusDownloadWorkflow.DownloadZipAsync(
            gameId: "stardewvalley",
            modId: modId,
            fileId: fileId,
            workingDirectory: tempDir,
            progressCallback: p =>
            {
                DownloadProgress = p.Percentage;
                DownloadStatus = $"正在下载 {item.Name}... {p.Percentage}%";
            },
            useCache: true
        );

        var installTask = new SVL.Core.Download.ModDownloadTask(
            modId: $"nexus-{modId}",
            modName: item.Name,
            fileName: mainFile.FileName ?? mainFile.Name ?? $"mod_{modId}_{fileId}.zip",
            localZipPath: zipPath,
            isLocalFile: true,
            gameBasePath: null,
            targetModsPath: modsPath,
            sourcePlatform: "NexusMods",
            sourceProjectId: modId.ToString(),
            sourceFileId: fileId.ToString()
        );

        await SVL.Core.Download.DownloadManager.Instance.AddTaskAsync(installTask);

        DownloadProgress = 100;
        DownloadStatus = $"✓ {item.Name} 已添加到安装队列";
        Log.Info($"[DownloadRightViewModel] ✓ 已添加 NexusMods 安装任务: {item.Name}");
    }

    /// <summary>
    /// 根据当前选择的源设置 SMAPI 项目的图标
    /// </summary>
    private void SetSmapiItemIcon(DownloadItem smapiItem)
    {
        switch (SmapiSelectedSource)
        {
            case "GitHub":
                // 使用 pack URI 格式，确保能被 Image 控件识别
                smapiItem.Thumbnail = "pack://application:,,,/Images/Modded.png";
                Log.Debug("[DownloadRightViewModel] 设置 SMAPI 图标为 GitHub Modded.png");
                break;
            case "Curseforge":
            case "全部":
                // Curseforge 会异步加载 logo，这里先设置默认图标
                smapiItem.Thumbnail = "📦";
                Log.Debug("[DownloadRightViewModel] 设置 SMAPI 图标为 Curseforge 默认图标");
                break;
            case "NexusMods":
                // NexusMods 会异步加载图片，这里先设置默认图标
                smapiItem.Thumbnail = "🎮";
                Log.Debug("[DownloadRightViewModel] 设置 SMAPI 图标为 NexusMods 默认图标");
                break;
            default:
                smapiItem.Thumbnail = "🔧";
                break;
        }
    }

    /// <summary>
    /// 获取最新 SMAPI 版本的文件 ID（异步）
    /// </summary>
    private async Task<long> GetLatestSmapiFileIdAsync()
    {
        try
        {
            const long SMAPI_MOD_ID = 2400;

            // 异步获取文件列表
            var files = await SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsClient.GetModFilesAsync(SMAPI_MOD_ID);

            if (files != null && files.Count > 0)
            {
                // 返回第一个文件的 ID（通常是最新版本）
                return files[0].GetFileIdLong();
            }

            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DownloadRightViewModel] 获取 SMAPI 文件列表失败");
            return 0;
        }
    }

    /// <summary>
    /// 获取缓存的图片路径（如果存在）
    /// </summary>
    /// <param name="imageUrl">图片URL</param>
    /// <returns>缓存路径，如果不存在返回null</returns>
    private string? GetCachedImagePath(string imageUrl)
    {
        if (string.IsNullOrEmpty(imageUrl))
            return null;

        try
        {
            var cachedPath = ImageCacheService.GetCachedImagePath(imageUrl);
            if (!string.IsNullOrEmpty(cachedPath))
            {
                Log.Debug($"[DownloadRightViewModel] 使用缓存图片: {imageUrl}");
                return cachedPath;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("[DownloadRightViewModel] 检查图片缓存失败", ex);
        }

        return null;
    }

    /// <summary>
    /// 异步下载并缓存图片
    /// </summary>
    /// <param name="imageUrl">图片URL</param>
    /// <param name="onCompleted">完成回调</param>
    private async Task DownloadAndCacheImageAsync(string imageUrl, Action<string?> onCompleted)
    {
        if (string.IsNullOrEmpty(imageUrl))
        {
            onCompleted?.Invoke(null);
            return;
        }

        try
        {
            var cachedPath = await ImageCacheService.DownloadAndCacheImageAsync(imageUrl);
            onCompleted?.Invoke(cachedPath);
        }
        catch (Exception ex)
        {
            Log.Warn($"[DownloadRightViewModel] 下载并缓存图片失败: {imageUrl}", ex);
            onCompleted?.Invoke(null);
        }
    }

    /// <summary>
    /// 处理 NexusMods Token 过期
    /// </summary>
    private void HandleNexusModsTokenExpired(bool showNotification = true)
    {
        Log.Warn("[DownloadRightViewModel] NexusMods Token 已过期");
        NexusAuthStateHelper.HandleTokenExpired("DownloadRightViewModel", "DownloadRightViewModel", showNotification);
    }

    /// <summary>
    /// 将主窗口置顶到桌面最前（视觉反馈）
    /// </summary>
    private static void BringMainWindowToFront()
    {
        try
        {
            // 必须在 UI 线程上访问 MainWindow
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                Log.Warn("[DownloadRightViewModel] Dispatcher 未找到，无法置顶");
                return;
            }

            Log.Info("[DownloadRightViewModel] 将主窗口置顶到桌面最前");

            // 在 UI 线程上执行
            dispatcher.BeginInvoke(new Action(() =>
            {
                var mainWindow = System.Windows.Application.Current?.MainWindow as MainWindow;
                if (mainWindow == null)
                {
                    Log.Warn("[DownloadRightViewModel] MainWindow 未找到，无法置顶");
                    return;
                }

                // 如果窗口被最小化，恢复窗口
                if (mainWindow.WindowState == WindowState.Minimized)
                {
                    mainWindow.WindowState = WindowState.Normal;
                }

                // 临时置顶窗口
                mainWindow.Topmost = true;
                mainWindow.Activate();
                mainWindow.Focus();

                // 短暂延迟后取消置顶
                System.Threading.Tasks.Task.Delay(500).ContinueWith(_ =>
                {
                    mainWindow.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        mainWindow.Topmost = false;
                    }));
                });
            }));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DownloadRightViewModel] 置顶主窗口失败");
        }
    }

}
