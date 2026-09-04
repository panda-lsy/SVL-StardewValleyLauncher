using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SVL.Avalonia.Services;
using SVL.Core.Platform.Abstractions;
using SVL.Core.Platform.Modpack;
using SVL.Core.Platform.Services;

namespace SVL.Avalonia.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly IPlatformInfoService _platformInfoService;
    private readonly IGameInstallPathLocator _gameInstallPathLocator;
    private readonly AppUserSettingsStore _settingsStore;
    private readonly LocalizationService _localizationService;
    private readonly ImageResourceService _imageResourceService;
    private readonly DialogService _dialogService;
    private readonly LauncherUpdateService _launcherUpdateService;
    private readonly Stack<(string Page, ObservableObject ViewModel)> _backStack = new();
    private Models.DownloadTaskItem? _currentDownloadTask;

    public LaunchPageViewModel LaunchPage { get; }

    public DownloadPageViewModel DownloadPage { get; }

    public SettingsPageViewModel SettingsPage { get; }

    public InstancesPageViewModel InstancesPage { get; }

    /// <summary>SMAPI 下载服务（持有引用用于 NXM 回调路由：SMAPI 专用 NXM 链接优先交给它处理）。</summary>
    public Services.SmapiDownloadService SmapiDownloadService { get; }

    /// <summary>通用浏览器下载回退服务（非 Premium 用户 NXM 回调等待，支持普通 Mod）。</summary>
    public Services.BrowserDownloadFallbackService BrowserDownloadFallbackService { get; }

    public TaskStatusPageViewModel TaskStatusPage { get; }

    public ModSearchPageViewModel ModSearchPage { get; }

    public ModpackSearchPageViewModel ModpackSearchPage { get; }

    public ModDetailsPageViewModel ModDetailsPage { get; }

    public VersionSettingsPageViewModel VersionSettingsPage { get; }

    public InstanceSettingsPageViewModel InstanceSettingsPage { get; }

    public ExportPageViewModel ExportPage { get; }

    [ObservableProperty]
    private string _currentPage = "启动";

    [ObservableProperty]
    private ObservableObject? _currentPageViewModel;

    [ObservableProperty]
    private string _windowTitle = "Stardew Valley Launcher";

    [ObservableProperty]
    private string _navLaunchText = "启动";

    [ObservableProperty]
    private string _navDownloadText = "下载";

    [ObservableProperty]
    private string _navTasksText = "任务";

    [ObservableProperty]
    private string _navSettingsText = "设置";

    [ObservableProperty]
    private string _navLaunchIconSource = string.Empty;

    [ObservableProperty]
    private string _navDownloadIconSource = string.Empty;

    [ObservableProperty]
    private string _navTasksIconSource = string.Empty;

    [ObservableProperty]
    private string _navSettingsIconSource = string.Empty;

    [ObservableProperty]
    private string _brandJunimoIconSource = string.Empty;

    [ObservableProperty]
    private bool _showTaskNavNotification;

    [ObservableProperty]
    private bool _showTaskNavSoftHint;

    [ObservableProperty]
    private bool _showDownloadFloatingTaskButton;

    [ObservableProperty]
    private int _floatingTaskBadgeCount;

    [ObservableProperty]
    private string _launcherAppNameText = "SVL";

    [ObservableProperty]
    private string _resourceDetailsHeaderTitle = "资源下载";

    [ObservableProperty]
    private string _sidebarCurrentPageText = "当前页面";

    [ObservableProperty]
    private string _sidebarMigrationStatusText = "迁移状态";

    [ObservableProperty]
    private string _sidebarMigrationPoint1 = "- 保留全部导航结构";

    [ObservableProperty]
    private string _sidebarMigrationPoint2 = "- 保留全部功能域（启动/下载/任务/设置）";

    [ObservableProperty]
    private string _sidebarMigrationPoint3 = "- 正在逐页迁移原 WPF 视图";

    [ObservableProperty]
    private string _sidebarPathDetectText = "路径探测";

    public string PlatformText => _platformInfoService.GetPlatformDisplayName();

    public string SteamPathPreview => _gameInstallPathLocator.TryLocateSteamStardewPath() ?? "未探测到（可手动选择）";

    public string GogPathPreview => _gameInstallPathLocator.TryLocateGogStardewPath() ?? "未探测到（可手动选择）";

    public bool IsLaunchPage => string.Equals(CurrentPage, "启动", StringComparison.Ordinal);

    public bool IsDownloadPage => string.Equals(CurrentPage, "下载", StringComparison.Ordinal);

    public bool IsTasksPage => string.Equals(CurrentPage, "任务", StringComparison.Ordinal);

    public bool IsSettingsPage => string.Equals(CurrentPage, "设置", StringComparison.Ordinal);

    public bool ShowWindowControlButtons => OperatingSystem.IsWindows();

    public bool ShowBackButton => IsBackPage(CurrentPage) && _backStack.Count > 0;

    public bool ShowResourceDetailHeaderTitle => IsResourceDetailsPage;

    public bool ShowBrandIdentity => !ShowBackButton && !ShowResourceDetailHeaderTitle;

    private bool IsResourceDetailsPage => string.Equals(CurrentPage, "资源详情", StringComparison.Ordinal);

    public MainWindowViewModel()
    {
        _platformInfoService = new PlatformInfoService();
        _gameInstallPathLocator = new GameInstallPathLocator();
        var externalProcessService = new ExternalProcessService();

        _settingsStore = new AppUserSettingsStore();
        _localizationService = new LocalizationService(_settingsStore);
        _imageResourceService = new ImageResourceService(_localizationService);
        _localizationService.LanguageChanged += ApplyLocalizedTexts;
        _imageResourceService.ResourcesChanged += ApplyImageResources;
        var initialSettings = _settingsStore.Load();
        LauncherAppNameText = string.IsNullOrWhiteSpace(initialSettings.LauncherAppName) ? "SVL" : initialSettings.LauncherAppName;
        ApplyLocalizedTexts();
        ApplyImageResources();

        var dialogService = new DialogService();
        _dialogService = dialogService;
        var nexusAuthService = new NexusAuthService();
        var nexusOAuthService = new NexusOAuthService();
        var httpDownloadService = new HttpDownloadService(_settingsStore);
        var nexusModDownloadResolverService = new NexusModDownloadResolverService();
        var downloadInstallService = new DownloadInstallService(_gameInstallPathLocator);
        var nxmLinkParser = new NxmLinkParser();
        var nxmProtocolRegistrationService = new NxmProtocolRegistrationService();
        var smapiInstallService = new SVL.Avalonia.Services.SmapiInstallService();
        var smapiDownloadService = new SVL.Avalonia.Services.SmapiDownloadService(httpDownloadService, nxmLinkParser);
        SmapiDownloadService = smapiDownloadService;
        var browserDownloadFallbackService = new SVL.Avalonia.Services.BrowserDownloadFallbackService(nxmLinkParser, externalProcessService);
        BrowserDownloadFallbackService = browserDownloadFallbackService;
        var downloadTaskStateStore = new DownloadTaskStateStore();
        var retryDiffReportService = new RetryDiffReportService();
        var instanceRegistryStore = new InstanceRegistryStore();
        var remoteCatalogService = new RemoteCatalogService(_settingsStore);
        var communityLocalizationService = new SVL.Avalonia.Services.CommunityLocalizationService(_settingsStore);
        remoteCatalogService.SetLocalizationService(communityLocalizationService);
        var modpackInstallService = new SVL.Avalonia.Services.ModpackInstallService(
            _gameInstallPathLocator, smapiInstallService, httpDownloadService, remoteCatalogService,
            _settingsStore, nexusModDownloadResolverService, nxmLinkParser);
        var collectionInstallService = new SVL.Avalonia.Services.CollectionInstallService(
            _gameInstallPathLocator, smapiInstallService, httpDownloadService, remoteCatalogService,
            _settingsStore, nexusModDownloadResolverService, nxmLinkParser, browserDownloadFallbackService,
            modpackInstallService);
        var launcherUpdateService = new LauncherUpdateService();
        _launcherUpdateService = launcherUpdateService;
        LaunchPage = new LaunchPageViewModel(_gameInstallPathLocator, externalProcessService, _settingsStore, _localizationService, _imageResourceService);
        DownloadPage = new DownloadPageViewModel(
            _localizationService,
            _imageResourceService,
            nxmLinkParser,
            _gameInstallPathLocator,
            _settingsStore,
            dialogService,
            httpDownloadService,
            nexusModDownloadResolverService,
            downloadInstallService,
            smapiInstallService,
            browserDownloadFallbackService,
            remoteCatalogService,
            downloadTaskStateStore,
            retryDiffReportService,
            modpackInstallService,
            collectionInstallService);
        SettingsPage = new SettingsPageViewModel(_settingsStore, dialogService, nexusAuthService, nexusOAuthService, launcherUpdateService, externalProcessService, nxmProtocolRegistrationService, _localizationService, _imageResourceService);
        InstancesPage = new InstancesPageViewModel(_gameInstallPathLocator, dialogService, instanceRegistryStore, _settingsStore, _imageResourceService, _localizationService);
        TaskStatusPage = new TaskStatusPageViewModel();
        ModSearchPage = new ModSearchPageViewModel(remoteCatalogService);
        ModpackSearchPage = new ModpackSearchPageViewModel(remoteCatalogService);
        ModDetailsPage = new ModDetailsPageViewModel(remoteCatalogService, dialogService);
        ModDetailsPage.QueueDownloadRequested += HandleQueueDownload;
        // 注入 Mods 路径解析器：用于检测 Mod 是否已安装（扫描 Mods 目录 manifest.json）
        ModDetailsPage.CurrentModsPathResolver = () => DownloadPage.GetCurrentModsPath();
        VersionSettingsPage = new VersionSettingsPageViewModel(_settingsStore, _gameInstallPathLocator, _localizationService, _imageResourceService, dialogService, remoteCatalogService, smapiInstallService, smapiDownloadService, communityLocalizationService);
        // 注入路径列表提供者：SMAPI 安装对话框可从版本选择页面的 Base 路径列表中选择安装目标
        // 参考旧架构 GamePathConfirmDialog.LoadGamePaths：只提供 Base 路径，过滤掉版本隔离子目录
        // 路径列表提供者：供 SMAPI 安装对话框和 Collection 安装流程使用
        // 参考旧架构 ModpackDropDialogViewModel.LoadGamePaths：只提供 Base 路径，过滤版本隔离子目录
        // 旧架构通过 Tags.Contains("Base") 过滤，新架构通过 IsBaseInstance 标志 + 子目录过滤双重保障
        Func<IReadOnlyList<string>> basePathsProvider = () =>
        {
            if (!InstancesPage.HasPathEntries)
            {
                InstancesPage.RefreshFromSettingsChange();
            }

            // 参考旧架构：从所有实例中筛选 Base 实例（IsBaseInstance == true，等价于 Tags.Contains("Base")）
            var basePaths = InstancesPage.PathEntries
                .SelectMany(p => p.Instances)
                .Where(i => i.IsBaseInstance)
                .Select(i => i.Path)
                .Where(p => !string.IsNullOrWhiteSpace(p) && System.IO.Directory.Exists(p))
                .Select(p => p.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 补充 PreferredInstancePath（用户在设置中指定的首选路径）
            var preferredPath = ResolveBasePathForInstance(_settingsStore.Load().PreferredInstancePath);
            if (!string.IsNullOrWhiteSpace(preferredPath) && System.IO.Directory.Exists(preferredPath))
            {
                var normalizedPreferred = preferredPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                if (!basePaths.Any(p => string.Equals(p, normalizedPreferred, StringComparison.OrdinalIgnoreCase)))
                {
                    basePaths.Add(normalizedPreferred);
                }
            }

            if (basePaths.Count <= 1)
            {
                return basePaths;
            }

            // 过滤掉是其他路径子目录的路径（版本隔离目录可能也被识别为 Base 实例）
            return basePaths
                .Where(p => !basePaths.Any(other =>
                    !string.Equals(other, p, StringComparison.OrdinalIgnoreCase) &&
                    p.StartsWith(other + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        };

        VersionSettingsPage.AvailableGamePathsProvider = basePathsProvider;
        DownloadPage.AvailableGamePathsProvider = basePathsProvider;
        DownloadPage.AvailableModInstancesProvider = () =>
        {
            if (!InstancesPage.HasPathEntries)
            {
                InstancesPage.RefreshFromSettingsChange();
            }

            return InstancesPage.PathEntries
                .SelectMany(pathEntry => pathEntry.Instances
                    .Where(instance => instance.IsSmapiInstance &&
                                       !string.IsNullOrWhiteSpace(instance.Path) &&
                                       System.IO.Directory.Exists(instance.Path))
                    .Select(instance => new ModInstallTarget(
                        instance.Name,
                        instance.Path,
                        pathEntry.GamePath,
                        instance.IsBaseInstance)))
                .GroupBy(target => target.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        };
        VersionSettingsPage.OpenDetailsRequested += HandleOpenDetailsFromModManage;
        VersionSettingsPage.BatchUpdateModsRequested += HandleBatchUpdateModsRequested;
        InstanceSettingsPage = new InstanceSettingsPageViewModel(_settingsStore);
        ExportPage = new ExportPageViewModel(_gameInstallPathLocator, externalProcessService, _settingsStore);
        LaunchPage.NavigateToInstancesRequested += HandleNavigateToInstances;
        LaunchPage.NavigateToVersionSettingsRequested += HandleNavigateToVersionSettings;
        LaunchPage.NavigateToModManageRequested += HandleNavigateToModManage;
        VersionSettingsPage.InstanceContextChanged += HandleInstanceContextChanged;
        VersionSettingsPage.SmapiInstallTaskCreated += HandleSmapiInstallTaskCreated;
        VersionSettingsPage.RequestReturnToLaunch += () => NavigateToPage("启动", LaunchPage, clearBackStack: true);
        InstancesPage.InstanceActivated += HandleInstanceActivated;
        InstancesPage.InstanceSettingsRequested += HandleInstanceSettingsRequested;
        InstancesPage.ModpackImportRequested += HandleModpackImportRequested;
        DownloadPage.TaskSelected += HandleTaskSelected;
        DownloadPage.TaskStateChanged += HandleTaskStateChanged;
        DownloadPage.TaskLogGenerated += HandleTaskLogGenerated;
        DownloadPage.NavigateToTaskStatusRequested += HandleNavigateToTaskStatus;
        DownloadPage.NavigateToInstancesRequested += HandleNavigateToInstances;
        DownloadPage.NavigateToModSearchRequested += HandleNavigateToModSearch;
        DownloadPage.NavigateToModpackSearchRequested += HandleNavigateToModpackSearch;
        DownloadPage.NavigateToSettingsRequested += HandleNavigateToSettingsForNexusLogin;
        DownloadPage.OpenDetailsRequested += HandleOpenDetails;
        // SMAPI/Modpack/Collection 安装成功后刷新 LaunchPage/InstancesPage 实例图标
        DownloadPage.InstanceContextChanged += HandleInstanceContextChanged;
        // 任务状态页统一视图：任务操作事件转发到 DownloadPage 执行
        TaskStatusPage.RetryFailedItemsRequested += HandleRetryFailedItemsRequested;
        TaskStatusPage.NavigateToDownloadRequested += HandleNavigateToDownload;
        TaskStatusPage.CancelTaskRequested += HandleCancelTaskRequested;
        TaskStatusPage.RetryTaskRequested += HandleRetryTaskRequested;
        TaskStatusPage.RemoveTaskRequested += HandleRemoveTaskRequested;
        TaskStatusPage.OpenDirectoryRequested += HandleOpenDirectoryRequested;
        TaskStatusPage.OpenReportRequested += HandleOpenReportRequested;
        TaskStatusPage.OpenRetryReportRequested += HandleOpenRetryReportRequested;
        TaskStatusPage.ClearCompletedRequested += HandleClearCompletedRequested;
        ModSearchPage.OpenDetailsRequested += HandleOpenDetailsFromSearch;
        ModpackSearchPage.OpenDetailsRequested += HandleOpenDetailsFromSearch;
        SettingsPage.PropertyChanged += HandleSettingsPropertyChanged;
        SettingsPage.TakeoverDownloadRequested += HandleTakeoverDownloadRequested;
        SettingsPage.NexusLoggedOut += HandleSettingsNexusLoggedOut;

        CurrentPageViewModel = LaunchPage;
        OnPropertyChanged(nameof(ShowBackButton));
        OnPropertyChanged(nameof(ShowResourceDetailHeaderTitle));
        OnPropertyChanged(nameof(ShowBrandIdentity));
        RefreshFloatingTaskButtonState();

        // 冷启动后初始同步任务列表，确保历史任务在任务页立即可见
        TaskStatusPage.SyncTasks(DownloadPage.DownloadTasks);

        // 启动时按设置自动检查启动器更新（延迟 2 秒避免与初始化抢资源）
        _ = PerformAutoUpdateCheckAsync();
    }

    /// <summary>启动时自动检查更新：仅当 EnableAutoUpdateCheck 且未跳过该版本时弹窗。</summary>
    private async Task PerformAutoUpdateCheckAsync()
    {
        try
        {
            await Task.Delay(2000);

            var settings = _settingsStore.Load();
            if (!settings.EnableAutoUpdateCheck)
            {
                return;
            }

            var includePrerelease = settings.UpdateChannel?.Contains("pre", StringComparison.OrdinalIgnoreCase) ?? false;
            var preferGitee = string.Equals(settings.PreferredUpdateSource, "Gitee", StringComparison.OrdinalIgnoreCase);

            var result = await _launcherUpdateService.CheckForUpdateAsync(includePrerelease, preferGitee);
            if (!result.Success || !result.HasUpdate || result.ReleaseInfo == null)
            {
                return;
            }

            var releaseTag = result.ReleaseInfo.TagName;
            if (!string.IsNullOrWhiteSpace(settings.SkippedLauncherVersion) &&
                string.Equals(settings.SkippedLauncherVersion, releaseTag, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // 复用 SettingsPage 的弹窗逻辑：通过事件请求 SettingsPage 弹出更新对话框
            await SettingsPage.ShowUpdateDialogFromAutoCheckAsync(result);
        }
        catch
        {
            // 自动检查失败不应影响启动器正常使用
        }
    }

    private void ApplyLocalizedTexts()
    {
        WindowTitle = _localizationService.Get("Window.Title");
        NavLaunchText = _localizationService.Get("Nav.Launch");
        NavDownloadText = _localizationService.Get("Nav.Download");
        NavTasksText = _localizationService.Get("Nav.Tasks");
        NavSettingsText = _localizationService.Get("Nav.Settings");
        SidebarCurrentPageText = _localizationService.Get("Sidebar.CurrentPage");
        SidebarMigrationStatusText = _localizationService.Get("Sidebar.MigrationStatus");
        SidebarMigrationPoint1 = _localizationService.Get("Sidebar.MigrationPoint1");
        SidebarMigrationPoint2 = _localizationService.Get("Sidebar.MigrationPoint2");
        SidebarMigrationPoint3 = _localizationService.Get("Sidebar.MigrationPoint3");
        SidebarPathDetectText = _localizationService.Get("Sidebar.PathDetect");
    }

    private void ApplyImageResources()
    {
        BrandJunimoIconSource = _imageResourceService.Get("header.brand.junimo");
        NavLaunchIconSource = _imageResourceService.Get("nav.launch");
        NavDownloadIconSource = _imageResourceService.Get("nav.download");
        NavTasksIconSource = _imageResourceService.Get("nav.tasks");
        NavSettingsIconSource = _imageResourceService.Get("nav.settings");
    }

    partial void OnCurrentPageChanged(string value)
    {
        OnPropertyChanged(nameof(IsLaunchPage));
        OnPropertyChanged(nameof(IsDownloadPage));
        OnPropertyChanged(nameof(IsTasksPage));
        OnPropertyChanged(nameof(IsSettingsPage));
        OnPropertyChanged(nameof(ShowBackButton));
        OnPropertyChanged(nameof(ShowResourceDetailHeaderTitle));
        OnPropertyChanged(nameof(ShowBrandIdentity));
        RefreshTaskNavNotification();
        RefreshFloatingTaskButtonState();
    }

    private void HandleNavigateToInstances()
    {
        NavigateToPage("实例", InstancesPage, pushCurrentToBackStack: true);
    }

    private void HandleNavigateToVersionSettings()
    {
        VersionSettingsPage.ReloadFromSettings();
        VersionSettingsPage.SwitchToGeneral();
        // 预加载版本选择页面的路径列表，确保 SMAPI 安装对话框能获取所有 Base 路径
        if (!InstancesPage.HasPathEntries)
        {
            InstancesPage.RefreshFromSettingsChange();
        }
        NavigateToPage("版本设置", VersionSettingsPage, pushCurrentToBackStack: true);
    }

    private void HandleNavigateToModManage()
    {
        VersionSettingsPage.ReloadFromSettings(reloadModsWhenActive: true);
        VersionSettingsPage.SwitchToModManage();
        NavigateToPage("版本设置", VersionSettingsPage, pushCurrentToBackStack: true);
    }

    private void HandleInstanceContextChanged()
    {
        LaunchPage.RefreshFromSettingsAndEnvironment();
        InstancesPage.RefreshFromSettingsChange();
    }

    private void HandleSmapiInstallTaskCreated(Models.DownloadTaskItem taskItem)
    {
        DownloadPage.DownloadTasks.Insert(0, taskItem);
        DownloadPage.DownloadTasks[0].StatusIconSource = "avares://SVL.Avalonia/Assets/Icons/Modded.png";
        DownloadPage.Status = $"SMAPI 安装任务已创建: {taskItem.Name}";
        RefreshTaskNavNotification();
        NavigateToPage("任务", TaskStatusPage, pushCurrentToBackStack: true);
        TaskStatusPage.SetCurrentTask(taskItem.Name, taskItem.Status);

        // 监听任务状态变化以更新导航通知
        taskItem.PropertyChanged += (_, args) =>
        {
            if (string.Equals(args.PropertyName, nameof(Models.DownloadTaskItem.Status), StringComparison.Ordinal))
            {
                RefreshTaskNavNotification();
                TaskStatusPage.SetCurrentTask(taskItem.Name, taskItem.Status);
            }
        };
    }

    private void HandleInstanceActivated(InstanceItem _)
    {
        LaunchPage.RefreshFromSettingsAndEnvironment();
        // 实例切换后 Mod 安装状态可能变化（如从原版切到 SMAPI 实例），刷新详情页按钮文本
        ModDetailsPage.RefreshInstallActionTexts();
        NavigateToPage("启动", LaunchPage, clearBackStack: true);
    }

    private void HandleInstanceSettingsRequested(InstanceItem _)
    {
        VersionSettingsPage.ReloadFromSettings();
        VersionSettingsPage.SwitchToOverview();
        NavigateToPage("版本设置", VersionSettingsPage, pushCurrentToBackStack: true);
    }

    private void HandleModpackImportRequested()
    {
        DownloadPage.OpenModpackSearchPageCommand.Execute(null);
    }

    private void HandleTaskSelected(Models.DownloadTaskItem task)
    {
        _currentDownloadTask = task;
        TaskStatusPage.SyncTasks(DownloadPage.DownloadTasks);
        TaskStatusPage.SetCurrentTask(task);
        UpdateTaskStatusOverview();
        RefreshTaskNavNotification();
    }

    private void HandleTaskStateChanged(Models.DownloadTaskItem task)
    {
        _currentDownloadTask = task;

        // 仅当任务列表结构变化（新增/删除/状态类型变化）时才同步列表，
        // 避免下载进度回调频繁 Clear+Add 导致整个列表重绘闪烁。
        var currentState = task.TaskState;
        var lastState = task.PreviousSyncedState;
        var stateChanged = currentState != lastState;
        if (stateChanged)
        {
            task.PreviousSyncedState = currentState;
            TaskStatusPage.SyncTasks(DownloadPage.DownloadTasks);
        }

        if (TaskStatusPage.SelectedTask == task)
        {
            TaskStatusPage.SetCurrentTask(task);
        }

        // 概览/通知仅在状态类型变化时刷新，避免进度回调频闪
        if (stateChanged)
        {
            UpdateTaskStatusOverview();
            RefreshTaskNavNotification();
        }
    }

    private void HandleRetryFailedItemsRequested()
    {
        if (_currentDownloadTask == null)
        {
            TaskStatusPage.AddLog("没有可重试的当前任务");
            return;
        }

        DownloadPage.RetryTaskCommand.Execute(_currentDownloadTask);
    }

    // 任务状态页统一视图：操作事件转发到 DownloadPage 执行
    private void HandleCancelTaskRequested(Models.DownloadTaskItem task)
    {
        DownloadPage.CancelTaskCommand.Execute(task);
        TaskStatusPage.SyncTasks(DownloadPage.DownloadTasks);
    }

    private void HandleRetryTaskRequested(Models.DownloadTaskItem task)
    {
        DownloadPage.RetryTaskCommand.Execute(task);
        TaskStatusPage.SyncTasks(DownloadPage.DownloadTasks);
    }

    private void HandleRemoveTaskRequested(Models.DownloadTaskItem task)
    {
        DownloadPage.RemoveTaskCommand.Execute(task);
        TaskStatusPage.SyncTasks(DownloadPage.DownloadTasks);
        if (_currentDownloadTask == task)
        {
            _currentDownloadTask = null;
        }
        UpdateTaskStatusOverview();
    }

    private void HandleOpenDirectoryRequested(Models.DownloadTaskItem task)
    {
        DownloadPage.OpenTaskDirectoryCommand.Execute(task);
    }

    private void HandleOpenReportRequested(Models.DownloadTaskItem task)
    {
        DownloadPage.OpenTaskReportCommand.Execute(task);
    }

    private void HandleOpenRetryReportRequested(Models.DownloadTaskItem task)
    {
        DownloadPage.OpenTaskRetryReportCommand.Execute(task);
    }

    private void HandleClearCompletedRequested()
    {
        DownloadPage.ClearCompletedTasksCommand.Execute(null);
        TaskStatusPage.SyncTasks(DownloadPage.DownloadTasks);
        UpdateTaskStatusOverview();
    }

    /// <summary>接管下载：创建 Generic 下载任务并入队（用 HttpDownloadService 实际下载）。</summary>
    private void HandleTakeoverDownloadRequested(string url, string targetPath)
    {
        var fileName = System.IO.Path.GetFileName(targetPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "接管下载";
        }

        var taskItem = new Models.DownloadTaskItem
        {
            Name = $"接管下载: {fileName}",
            Status = "已加入队列",
            TaskKind = Models.DownloadTaskKind.Generic,
            TaskAction = Models.DownloadTaskAction.SaveOnly,
            SourceUrl = url,
            OutputFilePath = targetPath,
            StatusIconSource = "avares://SVL.Avalonia/Assets/Icons/Modded.png"
        };

        DownloadPage.DownloadTasks.Insert(0, taskItem);
        DownloadPage.Status = $"接管下载任务已创建: {fileName}";
        RefreshTaskNavNotification();
        NavigateToPage("任务", TaskStatusPage, pushCurrentToBackStack: true);
        TaskStatusPage.SetCurrentTask(taskItem);
    }

    private void HandleTaskLogGenerated(string message)
    {
        TaskStatusPage.AddLog(message);

        const string retryPrefix = "重试对比报告: ";
        if (message.StartsWith(retryPrefix, StringComparison.Ordinal))
        {
            var reportPath = message.Substring(retryPrefix.Length).Trim();
            TaskStatusPage.AddRetryReport(reportPath);
        }

        UpdateTaskStatusOverview();

        RefreshTaskNavNotification();
    }

    private void HandleNavigateToTaskStatus()
    {
        // 进入任务页前同步任务列表，确保历史任务能显示
        TaskStatusPage.SyncTasks(DownloadPage.DownloadTasks);
        UpdateTaskStatusOverview();
        NavigateToPage("任务", TaskStatusPage);
    }

    private void HandleNavigateToDownload()
    {
        NavigateToPage("下载", DownloadPage, clearBackStack: true);
    }

    private void HandleNavigateToSettingsForNexusLogin()
    {
        SettingsPage.ReloadFromSettings();
        SettingsPage.SelectedTabIndex = 1; // 下载设置标签页（Nexus 账户区）
        NavigateToPage("设置", SettingsPage, clearBackStack: true);
    }

    private void HandleNavigateToModSearch()
    {
        NavigateToPage("Mod搜索", ModSearchPage);
        _ = ModSearchPage.InitializeAsync();
    }

    private void HandleNavigateToModpackSearch()
    {
        NavigateToPage("Modpack搜索", ModpackSearchPage);
    }

    private void HandleOpenDetails(string details)
    {
        ModDetailsPage.SetResource(details, "由下载页搜索结果触发的详情上下文");
        // 立即导航到详情页，详情数据在后台异步加载并在页面上显示 loading 动画。
        NavigateToPage("资源详情", ModDetailsPage, pushCurrentToBackStack: true);
        _ = ModDetailsPage.LoadDetailsAsync(details);
    }

    /// <summary>搜索页（ModSearch/ModpackSearch）结构化身份触发的详情跳转。</summary>
    private void HandleOpenDetailsFromSearch(Models.CatalogResourceIdentity identity)
    {
        // SetResource 仍接字符串以填充 header 显示，用 Identity.Name 作为显示名。
        ModDetailsPage.SetResource(identity.Name, "由搜索页触发的详情上下文");
        // 立即导航到详情页，详情数据在后台异步加载并在页面上显示 loading 动画。
        NavigateToPage("资源详情", ModDetailsPage, pushCurrentToBackStack: true);
        _ = ModDetailsPage.LoadDetailsAsync(identity);
    }

    private void HandleOpenDetailsFromModManage(string details)
    {
        ModDetailsPage.SetResource(details, "由 Mod 列表触发的详情上下文");
        // 立即导航到详情页，详情数据在后台异步加载并在页面上显示 loading 动画。
        NavigateToPage("资源详情", ModDetailsPage, pushCurrentToBackStack: true);
        _ = ModDetailsPage.LoadDetailsAsync(details);
    }

    private async void HandleQueueDownload(Models.ExternalDownloadRequest request)
    {
        var queued = await DownloadPage.AddTaskFromExternalAsync(request);
        if (!queued)
        {
            return;
        }

        TaskStatusPage.SetCurrentTask(request.ToTaskDisplayName(), "已加入队列");
        NavigateToPage("任务", TaskStatusPage);
    }

    /// <summary>批量更新路由：把 VersionSettingsPage 收集的可更新 Mod 列表交给 DownloadPage 入队。</summary>
    private async void HandleBatchUpdateModsRequested(IReadOnlyList<ModBatchUpdateEntry> entries)
    {
        await DownloadPage.EnqueueBatchUpdateAsync(entries);
        NavigateToPage("任务", TaskStatusPage);
    }

    [RelayCommand]
    private void NavigateToLaunch()
    {
        LaunchPage.RefreshFromSettingsAndEnvironment();
        NavigateToPage("启动", LaunchPage, clearBackStack: true);
    }

    [RelayCommand]
    private void NavigateToDownload()
    {
        NavigateToPage("下载", DownloadPage, clearBackStack: true);
    }

    /// <summary>当外部 NXM 链接需要把窗口置顶时触发。MainWindow 订阅并调用 Activate()。</summary>
    public event Action? BringToFrontRequested;

    /// <summary>
    /// 处理外部 NXM 链接（来自浏览器协议回调或单实例转发）：导航到下载页后交给 DownloadPage 处理，
    /// 并请求把主窗口置顶。ImportNxmLinkAsync 内部入队后还会跳转到任务页（既有行为）。
    /// </summary>
    public async Task HandleExternalNxmLinkAsync(string link)
    {
        if (string.IsNullOrWhiteSpace(link))
        {
            return;
        }

        // 优先尝试 SMAPI 专用 NXM 回调（匹配 SMAPI mod id 时由 SmapiDownloadService 接管，不走通用入队）。
        if (await SmapiDownloadService.HandleNxmCallbackAsync(link))
        {
            BringToFrontRequested?.Invoke();
            return;
        }

        // 其次尝试通用浏览器下载回退（非 Premium 用户普通 Mod 的 NXM 回调）。
        if (BrowserDownloadFallbackService.HandleNxmCallback(link))
        {
            BringToFrontRequested?.Invoke();
            return;
        }

        // 先导航到下载页，让用户看到导入状态；入队后 DownloadPage 会再跳任务页。
        NavigateToPage("下载", DownloadPage, clearBackStack: true);

        try
        {
            await DownloadPage.HandleExternalNxmLinkAsync(link);
        }
        catch (Exception ex)
        {
            // 外部链接处理失败不应影响启动器运行。
            System.Diagnostics.Debug.WriteLine($"[MainWindowViewModel] 外部 NXM 链接处理失败: {ex.Message}");
        }

        BringToFrontRequested?.Invoke();
    }

    /// <summary>
    /// 处理拖放或按钮选中的本地 Modpack 整合包文件：先验证格式，再弹出元数据预览对话框，
    /// 用户确认导入后将整合包任务入队（安装执行器留后续迁移）。临时解压目录随任务保留。
    /// </summary>
    public async Task HandleModpackDropAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !ModpackTypeDetector.IsSupportedFile(filePath))
        {
            await _dialogService.ShowMessageAsync("不支持的文件", "仅支持 .zip 和 .cfmodpack 整合包格式（.7z 暂不支持）。");
            return;
        }

        // 从版本选择页路径列表构造可选安装目标（DisplayName + GamePath）
        // 预加载 PathEntries（若用户未访问过版本选择页面）
        if (!InstancesPage.HasPathEntries)
        {
            InstancesPage.RefreshFromSettingsChange();
        }
        var pathEntries = InstancesPage.PathEntries
            .Where(p => !string.IsNullOrWhiteSpace(p.GamePath))
            .Select(p => (p.DisplayName, p.GamePath))
            .ToList();

        // 默认选中当前版本所在的 Base 路径
        var preferredPath = ResolveBasePathForInstance(_settingsStore.Load().PreferredInstancePath);
        if (string.IsNullOrWhiteSpace(preferredPath))
        {
            // 回退到版本选择页当前选中的路径
            preferredPath = InstancesPage.SelectedPathEntry?.GamePath;
        }

        var result = await _dialogService.ShowModpackDropDialogAsync(filePath, pathEntries, "导入 Modpack", preferredPath);
        if (result == null)
        {
            // 用户取消；ShowModpackDropDialogAsync 的 CancelCommand 已清理临时解压目录
            return;
        }

        EnqueueModpackImportTask(result);
    }

    private static string? ResolveBasePathForInstance(string? instancePath)
    {
        if (string.IsNullOrWhiteSpace(instancePath))
        {
            return null;
        }

        var fullPath = instancePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = new DirectoryInfo(fullPath);
        var parent = current.Parent;
        if (parent != null && string.Equals(parent.Name, "versions", StringComparison.OrdinalIgnoreCase))
        {
            return parent.Parent?.FullName;
        }

        var grandParent = parent?.Parent;
        if (string.Equals(current.Name, "game", StringComparison.OrdinalIgnoreCase) &&
            grandParent != null &&
            string.Equals(grandParent.Name, "versions", StringComparison.OrdinalIgnoreCase))
        {
            return grandParent.Parent?.FullName;
        }

        return fullPath;
    }

    /// <summary>将整合包导入任务入队（按检测类型设置 TaskKind，ExecuteTaskAsync 据此路由到 ModpackInstallService）。</summary>
    private void EnqueueModpackImportTask(Models.ModpackDropDialogResult result)
    {
        var detection = result.Detection;
        var displayName = detection.ModpackName ?? System.IO.Path.GetFileNameWithoutExtension(result.ModpackFilePath);
        var typeText = detection.Type.ToString();

        var taskKind = detection.Type switch
        {
            SVL.Core.Platform.Modpack.ModpackType.SVL => Models.DownloadTaskKind.SvlModpack,
            SVL.Core.Platform.Modpack.ModpackType.Curseforge => Models.DownloadTaskKind.CurseforgeModpack,
            SVL.Core.Platform.Modpack.ModpackType.NexusCollection => Models.DownloadTaskKind.NexusCollection,
            _ => Models.DownloadTaskKind.Generic
        };

        var taskAction = taskKind switch
        {
            Models.DownloadTaskKind.NexusCollection => Models.DownloadTaskAction.InstallCollection,
            Models.DownloadTaskKind.Generic => Models.DownloadTaskAction.InstallMod,
            _ => Models.DownloadTaskAction.InstallModpack
        };

        var taskItem = new Models.DownloadTaskItem
        {
            Name = $"{displayName} ({typeText})",
            Status = "已加入队列",
            TaskKind = taskKind,
            TaskAction = taskAction,
            SourceUrl = result.ModpackFilePath,
            OutputFilePath = result.ModpackFilePath,
            TargetInstanceName = result.InstanceName,
            TargetGamePath = result.TargetGamePath,
            StatusIconSource = "avares://SVL.Avalonia/Assets/Icons/Modded.png"
        };
        taskItem.SetState(Models.DownloadTaskState.Pending, "已加入队列");

        DownloadPage.DownloadTasks.Insert(0, taskItem);
        DownloadPage.Status = $"整合包导入任务已创建: {displayName}";
        RefreshTaskNavNotification();
        NavigateToPage("任务", TaskStatusPage, pushCurrentToBackStack: true);
        TaskStatusPage.SetCurrentTask(taskItem.Name, taskItem.Status);

        taskItem.PropertyChanged += (_, args) =>
        {
            if (string.Equals(args.PropertyName, nameof(Models.DownloadTaskItem.Status), StringComparison.Ordinal))
            {
                RefreshTaskNavNotification();
                TaskStatusPage.SetCurrentTask(taskItem.Name, taskItem.Status);
            }
        };
    }

    /// <summary>按钮路径：打开文件选择器选取整合包文件后进入导入流程。</summary>
    [RelayCommand]
    private async Task ImportModpackFromFile()
    {
        var path = await _dialogService.PickModpackFileAsync();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await HandleModpackDropAsync(path);
    }

    [RelayCommand]
    private void NavigateToTasks()
    {
        // 进入任务页前同步任务列表，确保历史任务能显示
        TaskStatusPage.SyncTasks(DownloadPage.DownloadTasks);
        NavigateToPage("任务", TaskStatusPage, clearBackStack: true);
    }

    [RelayCommand]
    private void OpenFloatingTaskManager()
    {
        NavigateToTasks();
    }

    [RelayCommand]
    private void NavigateToSettings()
    {
        NavigateToPage("设置", SettingsPage, clearBackStack: true);
    }

    [RelayCommand]
    private void NavigateBack()
    {
        if (_backStack.Count <= 0)
        {
            return;
        }

        var previous = _backStack.Pop();
        NavigateToPage(previous.Page, previous.ViewModel);
    }

    private void NavigateToPage(string page, ObservableObject viewModel, bool pushCurrentToBackStack = false, bool clearBackStack = false)
    {
        if (clearBackStack)
        {
            _backStack.Clear();
        }

        if (pushCurrentToBackStack && CurrentPageViewModel != null)
        {
            _backStack.Push((CurrentPage, CurrentPageViewModel));
        }

        CurrentPage = page;
        CurrentPageViewModel = viewModel;
        RefreshTaskNavNotification();
        OnPropertyChanged(nameof(ShowBackButton));
        OnPropertyChanged(nameof(ShowBrandIdentity));
    }

    private static bool IsBackPage(string page)
    {
        return string.Equals(page, "实例", StringComparison.Ordinal) ||
               string.Equals(page, "版本设置", StringComparison.Ordinal) ||
               string.Equals(page, "资源详情", StringComparison.Ordinal);
    }

    private void RefreshTaskNavNotification()
    {
        var hasFailedTasks = DownloadPage.DownloadTasks.Any(task => task.IsFailed || task.CanRetry);
        var hasRunningTasks = DownloadPage.DownloadTasks.Any(task => task.IsRunning);

        ShowTaskNavNotification = !IsTasksPage && hasFailedTasks;
        ShowTaskNavSoftHint = !IsTasksPage && !hasFailedTasks && hasRunningTasks;
        UpdateTaskStatusOverview();
    }

    private void UpdateTaskStatusOverview()
    {
        TaskStatusPage.UpdateTaskOverview(
            DownloadPage.ActiveTasks.Count,
            DownloadPage.FinishedTasks.Count,
            DownloadPage.SelectedTaskHint);
        RefreshFloatingTaskButtonState();
    }

    private void RefreshFloatingTaskButtonState()
    {
        // 红点只统计"需要关注"的任务：运行中（ActiveTasks）+ 失败/取消（不含成功）
        // 成功任务（Completed）不计入红点，避免用户处理完任务后红点一直不消失
        var activeCount = DownloadPage.ActiveTasks.Count;
        var failedCount = DownloadPage.FinishedTasks.Count(t => t.IsFailed || t.TaskState == Models.DownloadTaskState.Cancelled);
        var badgeCount = activeCount + failedCount;
        FloatingTaskBadgeCount = badgeCount;

        var enabled = SettingsPage.EnableDownloadFloatingTaskButton;
        ShowDownloadFloatingTaskButton = enabled && badgeCount > 0 && !IsTasksPage;
    }

    private void HandleSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(SettingsPageViewModel.LauncherAppName), StringComparison.Ordinal))
        {
            LauncherAppNameText = string.IsNullOrWhiteSpace(SettingsPage.LauncherAppName)
                ? "SVL"
                : SettingsPage.LauncherAppName;
            return;
        }

        if (string.Equals(e.PropertyName, nameof(SettingsPageViewModel.EnableDownloadFloatingTaskButton), StringComparison.Ordinal))
        {
            RefreshFloatingTaskButtonState();
        }
    }

    private void HandleSettingsNexusLoggedOut()
    {
        DownloadPage.ResetNexusAuthNotificationSuppression();
    }
}
