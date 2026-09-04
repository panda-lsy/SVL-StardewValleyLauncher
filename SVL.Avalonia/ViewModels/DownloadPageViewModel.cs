using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using SVL.Avalonia.Converters;
using SVL.Avalonia.Models;
using SVL.Avalonia.Services;
using SVL.Core.Platform.Abstractions;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SVL.Avalonia.ViewModels;

/// <summary>可用于安装 Mod 的 SMAPI 实例目标。</summary>
public sealed record ModInstallTarget(string Name, string Path, string BasePath, bool IsBaseInstance);

public partial class DownloadPageViewModel : ObservableObject
{
    private const string SmapiDefaultName = "SMAPI - Stardew Modding API";
    private const string SmapiDefaultSummary = "适用于星露谷物语的Mod加载器";
    private const string DescriptionModeLocalized = "社区汉化";
    private const string DescriptionModeSource = "源站英文";
    private static readonly object IconHttpClientLock = new();
    private static HttpClient? _smapiIconHttpClient;
    private static string _smapiIconProxySignature = string.Empty;
    private const int ModPageSize = 10;
    private const int ModpackPageSize = 10;
    private static readonly TimeSpan ModSearchCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NexusAuthReminderCooldown = TimeSpan.FromSeconds(30);

    private readonly LocalizationService _localizationService;
    private readonly ImageResourceService _imageResourceService;
    private readonly INxmLinkParser _nxmLinkParser;
    private readonly IGameInstallPathLocator _gameInstallPathLocator;
    private readonly AppUserSettingsStore _settingsStore;
    private readonly HttpDownloadService _httpDownloadService;
    private readonly NexusModDownloadResolverService _nexusModDownloadResolverService;
    private readonly DownloadInstallService _downloadInstallService;
    private readonly SmapiInstallService _smapiInstallService;
    private readonly BrowserDownloadFallbackService _browserDownloadFallbackService;
    private readonly RemoteCatalogService _remoteCatalogService;
    private readonly DownloadTaskStateStore _taskStateStore;
    private readonly RetryDiffReportService _retryDiffReportService;
    private readonly DialogService _dialogService;
    private readonly ModpackInstallService _modpackInstallService;
    private readonly CollectionInstallService _collectionInstallService;
    private readonly string _downloadRootPath;
    private readonly string _taskStatePath;
    private readonly string _smapiIconCachePath;
    private readonly Dictionary<string, string> _smapiIconDiskCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<DownloadTaskItem, CancellationTokenSource> _runningTaskCancellationSources = [];
    private readonly SemaphoreSlim _concurrencyGate = new(3, 3);
    private readonly HashSet<DownloadTaskItem> _dispatchedTasks = [];
    private int _catalogLoadToken;
    private bool _forceHotModsLoad;
    private readonly List<string> _modAllResults = [];
    private readonly List<string> _modpackAllResults = [];
    private bool _modHasMore;
    private bool _modpackHasMore;
    private bool _modGameVersionsLoaded;
    private bool _isLoadingModGameVersions;
    private DateTimeOffset _lastNexusAuthReminderAt = DateTimeOffset.MinValue;
    private bool _suppressNexusAuthNotificationThisSession;
    private readonly Dictionary<string, (DateTime CreatedAt, List<string> Results, bool HasMore)> _modResultsCache = new(StringComparer.Ordinal);

    public event Action<DownloadTaskItem>? TaskSelected;

    public event Action<DownloadTaskItem>? TaskStateChanged;

    public event Action<string>? TaskLogGenerated;

    public event Action? NavigateToTaskStatusRequested;

    public event Action? NavigateToModSearchRequested;

    public event Action? NavigateToModpackSearchRequested;

    public event Action? NavigateToInstancesRequested;

    public event Action? NavigateToSettingsRequested;

    public event Action<string>? OpenDetailsRequested;

    /// <summary>
    /// 实例上下文变更通知：SMAPI/Modpack/Collection 安装成功后触发，
    /// 由 MainWindowViewModel 订阅以刷新 LaunchPage 和 InstancesPage 的实例图标与状态。
    /// </summary>
    public event Action? InstanceContextChanged;

    /// <summary>路径列表提供者：Collection 安装时从版本选择页面的 Base 路径列表中选择安装目标。</summary>
    public Func<IReadOnlyList<string>>? AvailableGamePathsProvider { get; set; }

    /// <summary>提供已安装 SMAPI 实例，供从在线详情页安装 Mod 时选择目标。</summary>
    public Func<IReadOnlyList<ModInstallTarget>>? AvailableModInstancesProvider { get; set; }

    [ObservableProperty]
    private DownloadCategory _selectedCategory = DownloadCategory.Smapi;

    [ObservableProperty]
    private string _title = "下载中心";

    [ObservableProperty]
    private string _status = "就绪";

    [ObservableProperty]
    private bool _showNexusAuthExpiredNotice;

    [ObservableProperty]
    private string _nexusAuthExpiredNotice = string.Empty;

    [ObservableProperty]
    private string _smapiSearchText = string.Empty;

    [ObservableProperty]
    private string _selectedSmapiSource = "全部";

    [ObservableProperty]
    private string _selectedModSource = "全部";

    [ObservableProperty]
    private string _selectedModpackSource = "全部";

    [ObservableProperty]
    private string _selectedModGameVersion = "全部";

    [ObservableProperty]
    private string _selectedModType = "全部";

    [ObservableProperty]
    private string _selectedModDescriptionMode = DescriptionModeLocalized;

    [ObservableProperty]
    private string _modSearchText = string.Empty;

    [ObservableProperty]
    private string _selectedTaskHint = "未选择任务";

    [ObservableProperty]
    private bool _showGamePathWarning = true;

    [ObservableProperty]
    private string _nxmLinkInput = string.Empty;

    [ObservableProperty]
    private string _nxmImportStatus = "可粘贴 NXM 链接（nxm://...）快速入队";

    [ObservableProperty]
    private string _downloadUrlInput = string.Empty;

    [ObservableProperty]
    private string _downloadFileNameInput = string.Empty;

    [ObservableProperty]
    private string _urlDownloadStatus = "可输入 HTTP/HTTPS 直链进行真实下载";

    [ObservableProperty]
    private string _modpackUrlInput = string.Empty;

    [ObservableProperty]
    private string _modpackFileNameInput = string.Empty;

    [ObservableProperty]
    private string _modpackImportStatus = "支持 URL 导入 Modpack 并进入真实下载队列";

    [ObservableProperty]
    private string _gamePathHint = "未探测到游戏目录";

    [ObservableProperty]
    private bool _hasNoTasks;

    [ObservableProperty]
    private string _downloadCategoryTitleText = "下载类别";

    [ObservableProperty]
    private string _categorySmapiText = "SMAPI";

    [ObservableProperty]
    private string _categorySmapiSubText = "模组启动器";

    [ObservableProperty]
    private string _categoryModsText = "Mod";

    [ObservableProperty]
    private string _categoryModsSubText = "单个模组";

    [ObservableProperty]
    private string _categoryModpacksText = "Modpack";

    [ObservableProperty]
    private string _categoryModpacksSubText = "整合包";

    [ObservableProperty]
    private string _activeTasksTitleText = "进行中的任务";

    [ObservableProperty]
    private string _noActiveTasksText = "当前无进行中任务";

    [ObservableProperty]
    private string _historyTasksTitleText = "历史任务";

    [ObservableProperty]
    private string _noHistoryTasksText = "当前无历史任务";

    [ObservableProperty]
    private string _taskCancelButtonText = "取消";

    [ObservableProperty]
    private string _taskRetryButtonText = "重试";

    [ObservableProperty]
    private string _taskOpenReportButtonText = "打开报告";

    [ObservableProperty]
    private string _taskOpenBackupButtonText = "打开备份";

    [ObservableProperty]
    private string _taskCopyFailedButtonText = "复制失败明细";

    [ObservableProperty]
    private string _taskOpenRetryReportButtonText = "打开重试报告";

    [ObservableProperty]
    private string _statusPrefixText = "状态: ";

    [ObservableProperty]
    private string _nxmCardTitleText = "NXM 链接导入";

    [ObservableProperty]
    private string _nxmInputWatermarkText = "粘贴 nxm://stardewvalley/mods/.../files/...";

    [ObservableProperty]
    private string _nxmImportButtonText = "导入 NXM";

    [ObservableProperty]
    private string _urlCardTitleText = "URL 直链下载（真实网络）";

    [ObservableProperty]
    private string _urlInputWatermarkText = "https://example.com/file.zip";

    [ObservableProperty]
    private string _urlFileNameWatermarkText = "可选：自定义文件名（不填则自动推断）";

    [ObservableProperty]
    private string _urlQueueButtonText = "加入真实下载队列";

    [ObservableProperty]
    private string _gamePathWarningTitleText = "未设置游戏安装目录";

    [ObservableProperty]
    private string _gamePathWarningDescriptionText = "当前需要先配置实例中的游戏目录，下载与安装流程会使用该目录。";

    [ObservableProperty]
    private string _gamePathHintPrefixText = "探测结果: ";

    [ObservableProperty]
    private string _goInstanceButtonText = "前往实例管理";

    [ObservableProperty]
    private string _smapiSearchTitleText = "搜索 SMAPI";

    [ObservableProperty]
    private string _smapiSearchWatermarkText = "输入关键词";

    [ObservableProperty]
    private string _searchButtonText = "搜索";

    [ObservableProperty]
    private string _selectFirstResultButtonText = "选择首条结果";

    [ObservableProperty]
    private string _modSearchTitleText = "搜索 Mod";

    [ObservableProperty]
    private string _modSearchWatermarkText = "输入 Mod 关键词";

    [ObservableProperty]
    private string _openModSearchButtonText = "进入 Mod 搜索页";

    [ObservableProperty]
    private string _modpackImportTitleText = "Modpack 导入";

    [ObservableProperty]
    private string _modpackImportDescriptionText = "可通过搜索页选择整合包，或直接输入 URL 进入真实下载队列。";

    [ObservableProperty]
    private string _modpackUrlWatermarkText = "https://example.com/modpack.zip";

    [ObservableProperty]
    private string _modpackFileNameWatermarkText = "可选：自定义文件名（不填则自动推断）";

    [ObservableProperty]
    private string _modpackImportButtonText = "导入 Modpack URL";

    [ObservableProperty]
    private string _openModpackSearchButtonText = "进入 Modpack 搜索页";

    [ObservableProperty]
    private string _categorySmapiIconSource = "avares://SVL.Avalonia/Assets/Icons/Modded.png";

    [ObservableProperty]
    private string _categoryModsIconSource = "avares://SVL.Avalonia/Assets/Icons/Junimo.png";

    [ObservableProperty]
    private string _categoryGameIconSource = "avares://SVL.Avalonia/Assets/Icons/Vanilla.png";

    [ObservableProperty]
    private string _categoryModpacksIconSource = "avares://SVL.Avalonia/Assets/Icons/icon.png";

    [ObservableProperty]
    private string _modpackSearchText = string.Empty;

    [ObservableProperty]
    private bool _isCatalogLoading;

    [ObservableProperty]
    private bool _isSearchingMods;

    [ObservableProperty]
    private string _catalogListTitleText = "资源列表";

    [ObservableProperty]
    private string _catalogNoItemsText = "暂无资源，可尝试搜索关键词";

    [ObservableProperty]
    private int _currentModPage = 1;

    [ObservableProperty]
    private int _totalModPages = 1;

    [ObservableProperty]
    private int _currentModpackPage = 1;

    [ObservableProperty]
    private int _totalModpackPages = 1;

    public ObservableCollection<string> SmapiSources { get; } = ["全部", "GitHub", "NexusMods", "Curseforge"];

    // ---- 游戏本体下载（SteamCMD） ----

    private readonly SteamCmdService _steamCmdService;

    /// <summary>游戏版本选项（登录后通过 SteamCMD 自动获取，初始为空）。</summary>
    public ObservableCollection<SteamGameVersionOption> GameVersionOptions { get; } = [];

    [ObservableProperty]
    private SteamGameVersionOption? _selectedGameVersion;

    /// <summary>当前选中版本的描述（在版本下拉框下方展示）。</summary>
    public string SelectedGameVersionDescription => SelectedGameVersion?.Description ?? string.Empty;

    public bool HasSelectedGameVersionDescription => !string.IsNullOrWhiteSpace(SelectedGameVersionDescription);

    partial void OnSelectedGameVersionChanged(SteamGameVersionOption? value)
    {
        OnPropertyChanged(nameof(SelectedGameVersionDescription));
        OnPropertyChanged(nameof(HasSelectedGameVersionDescription));
    }

    [ObservableProperty]
    private string _steamUsername = string.Empty;

    [ObservableProperty]
    private string _steamPassword = string.Empty;

    [ObservableProperty]
    private string _steamGuardCode = string.Empty;

    [ObservableProperty]
    private string _customManifestId = string.Empty;

    [ObservableProperty]
    private string _gameTargetPath = string.Empty;

    [ObservableProperty]
    private string _steamCmdInputText = string.Empty;

    [ObservableProperty]
    private string _steamCmdStatusText = "未安装";

    [ObservableProperty]
    private string _steamCmdLogText = string.Empty;

    [ObservableProperty]
    private bool _isSteamCmdBusy;

    [ObservableProperty]
    private bool _isSteamLoggedIn;

    [ObservableProperty]
    private double _gameDownloadProgress;

    public bool IsSteamCmdInstalled => _steamCmdService.IsSteamCmdInstalled;

    public bool CanDownloadGame => !IsSteamCmdBusy && IsSteamCmdInstalled;

    public bool CanSendSteamCmdInput => !IsSteamCmdBusy && IsSteamCmdInstalled;

    /// <summary>SteamCMD 分页流程索引：0=安装，1=登录，2=版本下载。</summary>
    [ObservableProperty]
    private int _steamCmdStepIndex;

    public bool IsSteamCmdStepInstall => SteamCmdStepIndex == 0;

    public bool IsSteamCmdStepLogin => SteamCmdStepIndex == 1;

    public bool IsSteamCmdStepDownload => SteamCmdStepIndex == 2;

    public bool CanGoPrevSteamCmdStep => SteamCmdStepIndex > 0;

    // 登录页：只有检测到登录成功（IsSteamLoggedIn）才能点"下一步"进入版本下载。
    public bool CanGoNextSteamCmdStep => SteamCmdStepIndex switch
    {
        0 => true,
        1 => IsSteamLoggedIn,
        _ => false
    };

    partial void OnIsSteamLoggedInChanged(bool value)
    {
        OnPropertyChanged(nameof(CanGoNextSteamCmdStep));
    }

    partial void OnSteamCmdStepIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsSteamCmdStepInstall));
        OnPropertyChanged(nameof(IsSteamCmdStepLogin));
        OnPropertyChanged(nameof(IsSteamCmdStepDownload));
        OnPropertyChanged(nameof(CanGoPrevSteamCmdStep));
        OnPropertyChanged(nameof(CanGoNextSteamCmdStep));
    }

    [RelayCommand]
    private void PrevSteamCmdStep()
    {
        if (SteamCmdStepIndex > 0)
        {
            SteamCmdStepIndex--;
        }
    }

    [RelayCommand]
    private void NextSteamCmdStep()
    {
        if (SteamCmdStepIndex < 2)
        {
            SteamCmdStepIndex++;
        }
    }



    public ObservableCollection<string> ModSources { get; } = ["全部", "Curseforge", "NexusMods"];

    public ObservableCollection<string> ModpackSources { get; } = ["全部", "Curseforge", "NexusMods"];

    public ObservableCollection<string> ModGameVersions { get; } = ["全部", "1.6", "1.5", "1.4"];

    public ObservableCollection<string> ModTypes { get; } = ["全部", "功能扩展", "界面美化", "游戏内容", "工具类", "音效材质", "作弊类"];

    public ObservableCollection<string> ModDescriptionModes { get; } = [DescriptionModeLocalized, DescriptionModeSource];

    public ObservableCollection<DownloadTaskItem> DownloadTasks { get; } = [];

    public ObservableCollection<DownloadTaskItem> ActiveTasks { get; } = [];

    public ObservableCollection<DownloadTaskItem> FinishedTasks { get; } = [];

    public ObservableCollection<string> SearchResults { get; } = [];

    public ObservableCollection<DownloadCatalogItem> CategoryItems { get; } = [];

    public ObservableCollection<DownloadCatalogItem> SmapiGithubItems { get; } = [];

    public ObservableCollection<DownloadCatalogItem> SmapiNexusModsItems { get; } = [];

    public ObservableCollection<DownloadCatalogItem> SmapiCurseforgeItems { get; } = [];

    public bool IsSmapiCategory => SelectedCategory == DownloadCategory.Smapi;

    public bool IsModsCategory => SelectedCategory == DownloadCategory.Mods;

    public bool IsModpacksCategory => SelectedCategory == DownloadCategory.Modpacks;

    public bool IsGameCategory => SelectedCategory == DownloadCategory.Game;

    public bool IsNonSmapiCategory => !IsSmapiCategory && !IsGameCategory;

    public bool HasNexusAuthExpiredNotice =>
        ShowNexusAuthExpiredNotice && !string.IsNullOrWhiteSpace(NexusAuthExpiredNotice);

    public bool HasActiveTasks => ActiveTasks.Count > 0;

    public bool HasFinishedTasks => FinishedTasks.Count > 0;

    public bool HasNoActiveTasks => !HasActiveTasks;

    public bool HasNoFinishedTasks => !HasFinishedTasks;

    public bool HasNoCategoryItems => !IsCatalogLoading && CategoryItems.Count == 0;

    public bool HasCategoryItems => !HasNoCategoryItems;

    // Hides the results list while a fresh (non-cached) Mods/Modpacks search is in flight so the
    // loading card is the only thing shown. For SMAPI, IsSearchingMods is always false, so this
    // mirrors HasCategoryItems exactly (no behaviour change).
    public bool IsCategoryListVisible => HasCategoryItems && !IsSearchingMods;

    public bool HasSmapiGithubItems => SmapiGithubItems.Count > 0;

    public bool HasSmapiNexusModsItems => SmapiNexusModsItems.Count > 0;

    public bool HasSmapiCurseforgeItems => SmapiCurseforgeItems.Count > 0;

    public bool HasNoSmapiItems =>
        !IsCatalogLoading &&
        !HasSmapiGithubItems &&
        !HasSmapiNexusModsItems &&
        !HasSmapiCurseforgeItems;

    public bool UseLocalizedModDescription =>
        string.Equals(SelectedModDescriptionMode, DescriptionModeLocalized, StringComparison.Ordinal);

    public bool IsModsPageable => IsModsCategory;

    public bool CanGoToPreviousModPage => CurrentModPage > 1;

    public bool CanGoToNextModPage => CurrentModPage < TotalModPages;

    public string ModPageInfoText => $"第 {CurrentModPage}/{TotalModPages} 页";

    public bool IsModpacksPageable => IsModpacksCategory;

    public bool CanGoToPreviousModpackPage => CurrentModpackPage > 1;

    public bool CanGoToNextModpackPage => CurrentModpackPage < TotalModpackPages;

    public string ModpackPageInfoText => $"第 {CurrentModpackPage}/{TotalModpackPages} 页";

    public string SelectedModSourceDescription => SelectedModSource switch
    {
        "NexusMods" => "来源：仅 NexusMods",
        "Curseforge" => "来源：仅 Curseforge",
        _ => "来源：全部（每个源最多展示 10 条）"
    };

    public string SelectedModGameVersionDescription => SelectedModGameVersion switch
    {
        "全部" => "版本：不过滤",
        _ => $"版本：兼容 {SelectedModGameVersion}"
    };

    public string SelectedModTypeDescription => SelectedModType switch
    {
        "全部" => "类型：不过滤",
        _ => $"类型：{SelectedModType}"
    };

    public DownloadPageViewModel(
        LocalizationService localizationService,
        ImageResourceService imageResourceService,
        INxmLinkParser nxmLinkParser,
        IGameInstallPathLocator gameInstallPathLocator,
        AppUserSettingsStore settingsStore,
        DialogService dialogService,
        HttpDownloadService httpDownloadService,
        NexusModDownloadResolverService nexusModDownloadResolverService,
        DownloadInstallService downloadInstallService,
        SmapiInstallService smapiInstallService,
        BrowserDownloadFallbackService browserDownloadFallbackService,
        RemoteCatalogService remoteCatalogService,
        DownloadTaskStateStore taskStateStore,
        RetryDiffReportService retryDiffReportService,
        ModpackInstallService modpackInstallService,
        CollectionInstallService collectionInstallService)
    {
        _localizationService = localizationService;
        _imageResourceService = imageResourceService;
        _nxmLinkParser = nxmLinkParser;
        _gameInstallPathLocator = gameInstallPathLocator;
        _settingsStore = settingsStore;
        _dialogService = dialogService;
        _httpDownloadService = httpDownloadService;
        _nexusModDownloadResolverService = nexusModDownloadResolverService;
        _downloadInstallService = downloadInstallService;
        // 设置当前 Mods 路径解析器：优先使用用户选中实例的 Mods 路径，
        // 而非自动探测的 Steam/GOG 路径（参考旧架构 GetCurrentSelectedInstance）
        _downloadInstallService.CurrentModsPathResolver = ResolveCurrentInstanceModsPath;
        _smapiInstallService = smapiInstallService;
        _steamCmdService = new SteamCmdService(httpDownloadService);
        _browserDownloadFallbackService = browserDownloadFallbackService;
        _remoteCatalogService = remoteCatalogService;
        _remoteCatalogService.DebugLogger = message => EmitLog($"[Catalog] {message}");
        _remoteCatalogService.NexusAuthExpired += HandleRemoteCatalogNexusAuthExpired;
        _taskStateStore = taskStateStore;
        _retryDiffReportService = retryDiffReportService;
        _modpackInstallService = modpackInstallService;
        _collectionInstallService = collectionInstallService;
        _localizationService.LanguageChanged += ApplyLocalizedTexts;
        _imageResourceService.ResourcesChanged += ApplyImageResources;
        ApplyLocalizedTexts();
        ApplyImageResources();

        _downloadRootPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SVL",
            "Avalonia",
            "Downloads");
        _taskStatePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SVL",
            "Avalonia",
            "download-tasks-state.json");
        _smapiIconCachePath = AssetImageConverter.IconCacheDirectory;

        DownloadTasks.CollectionChanged += (_, args) =>
        {
            HasNoTasks = DownloadTasks.Count == 0;
            if (args.OldItems != null)
            {
                foreach (var oldItem in args.OldItems.OfType<DownloadTaskItem>())
                {
                    oldItem.PropertyChanged -= OnTaskPropertyChanged;
                }
            }

            if (args.NewItems != null)
            {
                foreach (var newItem in args.NewItems.OfType<DownloadTaskItem>())
                {
                    newItem.PropertyChanged += OnTaskPropertyChanged;
                }
            }

            RefreshTaskBuckets();
        };

        CategoryItems.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasNoCategoryItems));
            OnPropertyChanged(nameof(HasCategoryItems));
            OnPropertyChanged(nameof(IsCategoryListVisible));
        };

        SmapiGithubItems.CollectionChanged += (_, _) => RaiseSmapiSourceState();
        SmapiNexusModsItems.CollectionChanged += (_, _) => RaiseSmapiSourceState();
        SmapiCurseforgeItems.CollectionChanged += (_, _) => RaiseSmapiSourceState();

        Directory.CreateDirectory(_downloadRootPath);
        Directory.CreateDirectory(_smapiIconCachePath);
        TryLoadTaskState();
        RefreshGamePathState();

        HasNoTasks = DownloadTasks.Count == 0;
        foreach (var task in DownloadTasks)
        {
            task.PropertyChanged += OnTaskPropertyChanged;
        }

        RefreshTaskBuckets();
        RefreshTaskStatusIcons();
        if (HasNoTasks)
        {
            Status = "暂无下载任务，可通过搜索或链接导入添加";
        }

        _ = EnsureModGameVersionsLoadedAsync();
        _ = LoadCategoryItemsForCurrentCategoryAsync(initialLoad: true);

        SelectedGameVersion = GameVersionOptions.FirstOrDefault();
        RefreshSteamCmdState();
    }

    /// <summary>刷新 SteamCMD 安装状态文案。</summary>
    private void RefreshSteamCmdState()
    {
        SteamCmdStatusText = _steamCmdService.IsSteamCmdInstalled ? "已安装" : "未安装";
        OnPropertyChanged(nameof(IsSteamCmdInstalled));
        OnPropertyChanged(nameof(CanDownloadGame));
        OnPropertyChanged(nameof(CanSendSteamCmdInput));
    }

    private static void TraceSteamLog(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SVL", "Avalonia", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "steam-trace.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] [VM] {message}{Environment.NewLine}");
        }
        catch
        {
            // best-effort
        }
    }

    private bool _steamCmdVersionFetched;

    /// <summary>
    /// 通过 SteamCMD 自动获取当前分支 Manifest，替换"最新版"为真实获取到的版本，避免死代码。
    /// 只在 SteamCMD 已安装且尚未获取过时执行一次。
    /// </summary>
    private async Task RefreshSteamCmdVersionAsync()
    {
        if (_steamCmdVersionFetched || !_steamCmdService.IsSteamCmdInstalled)
        {
            return;
        }

        try
        {
            var versions = await _steamCmdService.FetchAvailableVersionsAsync(
                SteamUsername?.Trim(),
                log: msg => Dispatcher.UIThread.Post(() => AppendSteamCmdLog(msg)));

            if (versions.Count > 0)
            {
                _steamCmdVersionFetched = true;
                Dispatcher.UIThread.Post(() =>
                {
                    var wasFirst = GameVersionOptions.Count > 0 && SelectedGameVersion == GameVersionOptions[0];
                    var wasDefault = SelectedGameVersion == null;

                    GameVersionOptions.Clear();
                    foreach (var v in versions)
                    {
                        GameVersionOptions.Add(v);
                    }

                    if (wasDefault || wasFirst)
                    {
                        SelectedGameVersion = GameVersionOptions.FirstOrDefault();
                    }
                });
            }
        }
        catch (Exception ex)
        {
            AppendSteamCmdLog($"自动获取版本失败: {ex.Message}");
        }
    }

    partial void OnIsSteamCmdBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanDownloadGame));
        OnPropertyChanged(nameof(CanSendSteamCmdInput));
    }

    /// <summary>追加 SteamCMD 日志（保留最近 300 行）。</summary>
    private void AppendSteamCmdLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        var lines = string.IsNullOrEmpty(SteamCmdLogText)
            ? [line]
            : SteamCmdLogText.Split('\n').Append(line).TakeLast(300).ToArray();
        SteamCmdLogText = string.Join('\n', lines);
    }

    [RelayCommand]
    private async Task EnsureSteamCmdAsync()
    {
        if (IsSteamCmdBusy)
        {
            return;
        }

        IsSteamCmdBusy = true;
        try
        {
            await _steamCmdService.EnsureSteamCmdAsync(
                log: msg => Dispatcher.UIThread.Post(() => AppendSteamCmdLog(msg)));
            Status = "SteamCMD 安装完成";
            SteamCmdStepIndex = 1; // 安装完成 → 进入登录页
        }
        catch (Exception ex)
        {
            AppendSteamCmdLog($"安装失败: {ex.Message}");
            Status = $"SteamCMD 安装失败: {ex.Message}";
        }
        finally
        {
            IsSteamCmdBusy = false;
            RefreshSteamCmdState();
        }
    }

    [RelayCommand]
    private async Task SteamCmdLoginAsync()
    {
        if (IsSteamCmdBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SteamUsername) || string.IsNullOrWhiteSpace(SteamPassword))
        {
            Status = "请输入 Steam 账号与密码";
            return;
        }

        if (!_steamCmdService.IsSteamCmdInstalled)
        {
            Status = "请先下载 SteamCMD";
            return;
        }

        IsSteamCmdBusy = true;
        try
        {
            TraceSteamLog("SteamCmdLoginAsync: 开始登录");
            Status = "正在登录 Steam...若开启手机令牌，请在 Steam 手机 APP 中批准登录；若为邮箱验证码，请填入验证码。等待可能耗时（SteamCMD 日志缓冲，非实时）。";
            using var loginTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));

            var result = await _steamCmdService.LoginAsync(
                SteamUsername.Trim(),
                SteamPassword,
                string.IsNullOrWhiteSpace(SteamGuardCode) ? null : SteamGuardCode.Trim(),
                log: msg => Dispatcher.UIThread.Post(() => AppendSteamCmdLog(msg)),
                cancellationToken: loginTimeout.Token);
            TraceSteamLog($"SteamCmd.LoginAsync 返回 status={result.Status}");

            switch (result.Status)
            {
                case SteamCmdLoginStatus.Success:
                    IsSteamLoggedIn = true;
                    SteamGuardCode = string.Empty;
                    Status = "Steam 账号登录成功";
                    SteamCmdStepIndex = 2; // 登录成功 → 进入版本下载页
                    _ = RefreshSteamCmdVersionAsync(); // 登录后自动获取版本
                    break;
                case SteamCmdLoginStatus.NeedsGuardCode:
                    Status = "需要 Steam Guard 验证码，请填写后重新登录";
                    break;
                case SteamCmdLoginStatus.InvalidCredentials:
                    Status = "Steam 账号或密码错误";
                    break;
                default:
                    Status = result.Message;
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            Status = "Steam 登录超时（180 秒），请重试或在手机 App 中及时确认";
        }
        catch (Exception ex)
        {
            Status = $"Steam 登录异常: {ex.Message}";
            EmitLog($"[SteamCMD] 登录异常（堆栈）：{ex}");
        }
        finally
        {
            IsSteamCmdBusy = false;
        }
    }

    [RelayCommand]
    private async Task BrowseGameTargetPathAsync()
    {
        var path = await _dialogService.BrowseFolderPathAsync("选择游戏文件保存目录");
        if (!string.IsNullOrWhiteSpace(path))
        {
            GameTargetPath = path;
        }
    }

    [RelayCommand]
    private async Task DownloadGameAsync()
    {
        if (IsSteamCmdBusy)
        {
            return;
        }

        var manifestId = !string.IsNullOrWhiteSpace(CustomManifestId)
            ? CustomManifestId.Trim()
            : SelectedGameVersion?.ManifestId;
        var versionLabel = manifestId == null ? "最新版" : manifestId;

        IsSteamCmdBusy = true;
        GameDownloadProgress = 0;
        AppendSteamCmdLog($"开始下载游戏文件（{versionLabel}）...");
        try
        {
            var result = await _steamCmdService.DownloadGameDepotAsync(
                SteamUsername.Trim(),
                manifestId,
                GameTargetPath.Trim(),
                log: msg => Dispatcher.UIThread.Post(() => AppendSteamCmdLog(msg)),
                onProgress: percent => Dispatcher.UIThread.Post(() => GameDownloadProgress = percent));

            if (result.Success)
            {
                GameDownloadProgress = 100;
                Status = "游戏文件下载完成";
                EmitLog($"[SteamCMD] 游戏文件下载完成: {result.ContentPath}");

                var savePath = string.IsNullOrWhiteSpace(result.ContentPath)
                    ? GameTargetPath.Trim()
                    : result.ContentPath;
                var addAsBase = await _dialogService.ShowConfirmAsync(
                    "添加到游戏列表",
                    $"游戏文件已下载完成。是否将“{savePath}”添加到游戏列表作为 Base 路径？");
                if (addAsBase)
                {
                    TryAddDownloadedGameAsBase(savePath);
                }
            }
            else
            {
                Status = $"游戏文件下载失败: {result.Message}";
            }
        }
        catch (Exception ex)
        {
            Status = $"游戏文件下载异常: {ex.Message}";
        }
        finally
        {
            IsSteamCmdBusy = false;
            OnPropertyChanged(nameof(CanDownloadGame));
        }
    }

    /// <summary>把下载完成的游戏目录添加到游戏列表作为 Base 路径（vanilla）。</summary>
    private void TryAddDownloadedGameAsBase(string path)
    {
        try
        {
            var store = new InstanceRegistryStore();
            var records = store.LoadManualInstances();
            if (records.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                Status = "该路径已在游戏列表中";
                return;
            }

            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "Stardew Valley";
            }

            records.Add(new ManualInstanceRecord { Name = name, Path = path });
            store.SaveManualInstances(records);
            Status = $"已将“{path}”添加到游戏列表（Base 路径）";
        }
        catch (Exception ex)
        {
            Status = $"添加 Base 路径失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SendSteamCmdInputAsync()
    {
        if (IsSteamCmdBusy)
        {
            return;
        }

        var command = SteamCmdInputText?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        if (!_steamCmdService.IsSteamCmdInstalled)
        {
            Status = "请先下载 SteamCMD";
            return;
        }

        IsSteamCmdBusy = true;
        try
        {
            await _steamCmdService.RunCustomCommandAsync(
                command,
                log: msg => Dispatcher.UIThread.Post(() => AppendSteamCmdLog(msg)));
            SteamCmdInputText = string.Empty;
        }
        catch (Exception ex)
        {
            AppendSteamCmdLog($"执行自定义指令异常: {ex.Message}");
            Status = $"执行自定义指令异常: {ex.Message}";
        }
        finally
        {
            IsSteamCmdBusy = false;
        }
    }

    private void ApplyImageResources()
    {
        CategorySmapiIconSource = _imageResourceService.Get("download.category.smapi");
        CategoryModsIconSource = _imageResourceService.Get("download.category.mods");
        CategoryModpacksIconSource = _imageResourceService.Get("download.category.modpacks");
        RefreshTaskStatusIcons();
    }

    private void RefreshTaskStatusIcons()
    {
        foreach (var task in DownloadTasks)
        {
            task.StatusIconSource = ResolveTaskStatusIcon(task);
        }
    }

    private string ResolveTaskStatusIcon(DownloadTaskItem task)
    {
        if (task.IsFailed || task.IsCancelled)
        {
            return _imageResourceService.Get("download.task.failed");
        }

        if (task.IsCompleted)
        {
            return _imageResourceService.Get("download.task.completed");
        }

        if (task.IsRunning)
        {
            return _imageResourceService.Get("download.task.running");
        }

        return _imageResourceService.Get("download.task.pending");
    }

    private void ApplyLocalizedTexts()
    {
        DownloadCategoryTitleText = _localizationService.Get("Download.CategoryTitle");
        CategorySmapiText = _localizationService.Get("Download.Category.Smapi");
        CategorySmapiSubText = _localizationService.Get("Download.Category.SmapiSub");
        CategoryModsText = _localizationService.Get("Download.Category.Mods");
        CategoryModsSubText = _localizationService.Get("Download.Category.ModsSub");
        CategoryModpacksText = _localizationService.Get("Download.Category.Modpacks");
        CategoryModpacksSubText = _localizationService.Get("Download.Category.ModpacksSub");
        ActiveTasksTitleText = _localizationService.Get("Download.ActiveTasks");
        NoActiveTasksText = _localizationService.Get("Download.NoActiveTasks");
        HistoryTasksTitleText = _localizationService.Get("Download.HistoryTasks");
        NoHistoryTasksText = _localizationService.Get("Download.NoHistoryTasks");
        TaskCancelButtonText = _localizationService.Get("Download.Task.Cancel");
        TaskRetryButtonText = _localizationService.Get("Download.Task.Retry");
        TaskOpenReportButtonText = _localizationService.Get("Download.Task.OpenReport");
        TaskOpenBackupButtonText = _localizationService.Get("Download.Task.OpenBackup");
        TaskCopyFailedButtonText = _localizationService.Get("Download.Task.CopyFailed");
        TaskOpenRetryReportButtonText = _localizationService.Get("Download.Task.OpenRetryReport");
        StatusPrefixText = _localizationService.Get("Download.StatusPrefix");
        NxmCardTitleText = _localizationService.Get("Download.Nxm.Title");
        NxmInputWatermarkText = _localizationService.Get("Download.Nxm.Watermark");
        NxmImportButtonText = _localizationService.Get("Download.Nxm.Import");
        UrlCardTitleText = _localizationService.Get("Download.Url.Title");
        UrlInputWatermarkText = _localizationService.Get("Download.Url.Watermark");
        UrlFileNameWatermarkText = _localizationService.Get("Download.Url.FileNameWatermark");
        UrlQueueButtonText = _localizationService.Get("Download.Url.Queue");
        GamePathWarningTitleText = _localizationService.Get("Download.Path.WarningTitle");
        GamePathWarningDescriptionText = _localizationService.Get("Download.Path.WarningDescription");
        GamePathHintPrefixText = _localizationService.Get("Download.Path.HintPrefix");
        GoInstanceButtonText = _localizationService.Get("Download.Path.GoInstance");
        SmapiSearchTitleText = _localizationService.Get("Download.Search.SmapiTitle");
        SmapiSearchWatermarkText = _localizationService.Get("Download.Search.SmapiWatermark");
        SearchButtonText = _localizationService.Get("Download.Search.Button");
        SelectFirstResultButtonText = _localizationService.Get("Download.Search.SelectFirst");
        ModSearchTitleText = _localizationService.Get("Download.Search.ModTitle");
        ModSearchWatermarkText = _localizationService.Get("Download.Search.ModWatermark");
        OpenModSearchButtonText = _localizationService.Get("Download.Search.OpenMod");
        ModpackImportTitleText = _localizationService.Get("Download.Modpack.Title");
        ModpackImportDescriptionText = _localizationService.Get("Download.Modpack.Description");
        ModpackUrlWatermarkText = _localizationService.Get("Download.Modpack.UrlWatermark");
        ModpackFileNameWatermarkText = _localizationService.Get("Download.Modpack.FileNameWatermark");
        ModpackImportButtonText = _localizationService.Get("Download.Modpack.Import");
        OpenModpackSearchButtonText = _localizationService.Get("Download.Modpack.OpenSearch");
    }

    private void OnTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not DownloadTaskItem task) return;

        // 进度变化只需刷新图标，不需要重建列表（Progress 是 ObservableProperty，UI 自动更新进度条）
        if (e.PropertyName == nameof(DownloadTaskItem.Progress))
        {
            return;
        }

        if (e.PropertyName == nameof(DownloadTaskItem.TaskState) ||
            e.PropertyName == nameof(DownloadTaskItem.Status) ||
            e.PropertyName == nameof(DownloadTaskItem.CanRetry) ||
            e.PropertyName == nameof(DownloadTaskItem.CanCancel))
        {
            task.StatusIconSource = ResolveTaskStatusIcon(task);

            // 状态/可重试/可取消变化会改变 active/finished 分组归属或卡片按钮，
            // 需要重建列表让"重试/取消"按钮立即刷新（否则要切换卡片才生效）。
            if (e.PropertyName == nameof(DownloadTaskItem.TaskState) ||
                e.PropertyName == nameof(DownloadTaskItem.CanRetry) ||
                e.PropertyName == nameof(DownloadTaskItem.CanCancel))
            {
                RefreshTaskBuckets();
            }

            RetryTaskCommand.NotifyCanExecuteChanged();
            CancelTaskCommand.NotifyCanExecuteChanged();
        }
    }

    private void RefreshTaskBuckets()
    {
        var active = DownloadTasks.Where(task => !task.IsFinished).ToList();
        var finished = DownloadTasks.Where(task => task.IsFinished).ToList();

        ActiveTasks.Clear();
        foreach (var task in active)
        {
            ActiveTasks.Add(task);
        }

        FinishedTasks.Clear();
        foreach (var task in finished)
        {
            FinishedTasks.Add(task);
        }

        OnPropertyChanged(nameof(HasActiveTasks));
        OnPropertyChanged(nameof(HasFinishedTasks));
        OnPropertyChanged(nameof(HasNoActiveTasks));
        OnPropertyChanged(nameof(HasNoFinishedTasks));
    }

    partial void OnSelectedCategoryChanged(DownloadCategory value)
    {
        // 使切换分类时已经发出的网络请求失效，避免旧结果回写到新分类。
        Interlocked.Increment(ref _catalogLoadToken);

        Title = value switch
        {
            DownloadCategory.Smapi => "SMAPI 下载",
            DownloadCategory.Mods => "Mod 下载",
            DownloadCategory.Modpacks => "Modpack 下载",
            DownloadCategory.Game => "游戏本体下载",
            _ => "下载中心"
        };

        Status = value switch
        {
            DownloadCategory.Smapi => "可搜索并安装 SMAPI",
            DownloadCategory.Mods => "可搜索并安装 Mod",
            DownloadCategory.Modpacks => "可导入或下载 Modpack",
            DownloadCategory.Game => "通过 SteamCMD 登录并下载游戏本体（支持历史版本）",
            _ => "就绪"
        };

        CatalogListTitleText = value switch
        {
            DownloadCategory.Smapi => "SMAPI 资源列表",
            DownloadCategory.Mods => "Mod 资源列表",
            DownloadCategory.Modpacks => "Modpack 资源列表",
            _ => "资源列表"
        };

        if (value == DownloadCategory.Mods)
        {
            _ = EnsureModGameVersionsLoadedAsync();
        }

        if (value != DownloadCategory.Game)
        {
            _ = LoadCategoryItemsForCurrentCategoryAsync(initialLoad: true);
        }
        else
        {
            IsCatalogLoading = false;
            IsSearchingMods = false;
            OnPropertyChanged(nameof(HasNoCategoryItems));
            OnPropertyChanged(nameof(HasCategoryItems));
        }

        OnPropertyChanged(nameof(IsSmapiCategory));
        OnPropertyChanged(nameof(IsModsCategory));
        OnPropertyChanged(nameof(IsModpacksCategory));
        OnPropertyChanged(nameof(IsGameCategory));
        OnPropertyChanged(nameof(IsNonSmapiCategory));
        OnPropertyChanged(nameof(IsModsPageable));
        OnPropertyChanged(nameof(IsModpacksPageable));
        OnPropertyChanged(nameof(CanGoToPreviousModPage));
        OnPropertyChanged(nameof(CanGoToNextModPage));
        OnPropertyChanged(nameof(CanGoToPreviousModpackPage));
        OnPropertyChanged(nameof(CanGoToNextModpackPage));
        OnPropertyChanged(nameof(ModPageInfoText));
        OnPropertyChanged(nameof(ModpackPageInfoText));
        RaiseSmapiSourceState();
    }

    partial void OnSelectedModDescriptionModeChanged(string value)
    {
        OnPropertyChanged(nameof(UseLocalizedModDescription));
        ApplyModLocalizationPreferenceToCategoryItems();

        if (IsModsCategory)
        {
            _ = LoadCategoryItemsForCurrentCategoryAsync(initialLoad: false);
        }
    }

    partial void OnSelectedModSourceChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedModSourceDescription));
    }

    partial void OnSelectedModGameVersionChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedModGameVersionDescription));
    }

    partial void OnSelectedModTypeChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedModTypeDescription));
    }

    partial void OnIsSearchingModsChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCategoryListVisible));
    }

    partial void OnShowNexusAuthExpiredNoticeChanged(bool value)
    {
        OnPropertyChanged(nameof(HasNexusAuthExpiredNotice));
    }

    partial void OnNexusAuthExpiredNoticeChanged(string value)
    {
        OnPropertyChanged(nameof(HasNexusAuthExpiredNotice));
    }

    private void HandleRemoteCatalogNexusAuthExpired(string message)
    {
        var notice = string.IsNullOrWhiteSpace(message)
            ? "NexusMods 登录已失效，请在设置页重新登录。"
            : message.Trim();

        Dispatcher.UIThread.Post(() =>
        {
            Status = notice;
            EmitLog($"[Catalog] {notice}");

            var persistentSuppressed = false;
            try
            {
                persistentSuppressed = _settingsStore.Load().SuppressNexusAuthNotification;
            }
            catch
            {
                persistentSuppressed = false;
            }

            if (persistentSuppressed || _suppressNexusAuthNotificationThisSession)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (now - _lastNexusAuthReminderAt < NexusAuthReminderCooldown && HasNexusAuthExpiredNotice)
            {
                return;
            }

            _lastNexusAuthReminderAt = now;
            NexusAuthExpiredNotice = notice;
            ShowNexusAuthExpiredNotice = true;
        });
    }

    [RelayCommand]
    private void DismissNexusAuthExpiredNotice()
    {
        ShowNexusAuthExpiredNotice = false;
    }

    /// <summary>重置本会话内的 Nexus 登录失效通知抑制状态（用于用户退出/重新登录后恢复提醒）。</summary>
    public void ResetNexusAuthNotificationSuppression()
    {
        _suppressNexusAuthNotificationThisSession = false;
    }

    [RelayCommand]
    private void DismissNexusAuthNotificationPermanently()
    {
        try
        {
            var settings = _settingsStore.Load();
            settings.SuppressNexusAuthNotification = true;
            _settingsStore.Save(settings);
        }
        catch
        {
            // 忽略持久化失败，仍隐藏当前提示
        }

        ShowNexusAuthExpiredNotice = false;
    }

    [RelayCommand]
    private void DismissNexusAuthNotificationThisSession()
    {
        _suppressNexusAuthNotificationThisSession = true;
        ShowNexusAuthExpiredNotice = false;
    }

    [RelayCommand]
    private void GoToNexusLogin()
    {
        ShowNexusAuthExpiredNotice = false;
        NavigateToSettingsRequested?.Invoke();
    }

    private async Task EnsureModGameVersionsLoadedAsync(bool forceRefresh = false)
    {
        if (_isLoadingModGameVersions || (_modGameVersionsLoaded && !forceRefresh))
        {
            return;
        }

        _isLoadingModGameVersions = true;
        try
        {
            var versions = await _remoteCatalogService.GetModGameVersionsAsync();
            if (versions.Count == 0)
            {
                return;
            }

            var selectedBefore = string.IsNullOrWhiteSpace(SelectedModGameVersion)
                ? "全部"
                : SelectedModGameVersion.Trim();

            ModGameVersions.Clear();
            ModGameVersions.Add("全部");

            foreach (var version in versions)
            {
                if (string.IsNullOrWhiteSpace(version) ||
                    string.Equals(version, "全部", StringComparison.OrdinalIgnoreCase) ||
                    ModGameVersions.Any(existing => string.Equals(existing, version, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                ModGameVersions.Add(version);
            }

            var hasPreviousSelection = ModGameVersions.Any(item =>
                string.Equals(item, selectedBefore, StringComparison.OrdinalIgnoreCase));
            SelectedModGameVersion = hasPreviousSelection ? selectedBefore : "全部";

            _modGameVersionsLoaded = ModGameVersions.Count > 1;
            EmitLog($"[Catalog] Mod 游戏版本已加载: {Math.Max(ModGameVersions.Count - 1, 0)} 项");
        }
        catch (Exception ex)
        {
            EmitLog($"[Catalog] 加载 Mod 游戏版本失败: {ex.Message}");
        }
        finally
        {
            _isLoadingModGameVersions = false;
        }
    }

    partial void OnCurrentModPageChanged(int value)
    {
        OnPropertyChanged(nameof(CanGoToPreviousModPage));
        OnPropertyChanged(nameof(CanGoToNextModPage));
        OnPropertyChanged(nameof(ModPageInfoText));
        GoToNextModPageCommand.NotifyCanExecuteChanged();
        GoToPreviousModPageCommand.NotifyCanExecuteChanged();
    }

    partial void OnTotalModPagesChanged(int value)
    {
        OnPropertyChanged(nameof(IsModsPageable));
        OnPropertyChanged(nameof(CanGoToPreviousModPage));
        OnPropertyChanged(nameof(CanGoToNextModPage));
        OnPropertyChanged(nameof(ModPageInfoText));
        GoToNextModPageCommand.NotifyCanExecuteChanged();
        GoToPreviousModPageCommand.NotifyCanExecuteChanged();
    }

    partial void OnCurrentModpackPageChanged(int value)
    {
        OnPropertyChanged(nameof(CanGoToPreviousModpackPage));
        OnPropertyChanged(nameof(CanGoToNextModpackPage));
        OnPropertyChanged(nameof(ModpackPageInfoText));
        GoToNextModpackPageCommand.NotifyCanExecuteChanged();
        GoToPreviousModpackPageCommand.NotifyCanExecuteChanged();
    }

    partial void OnTotalModpackPagesChanged(int value)
    {
        OnPropertyChanged(nameof(IsModpacksPageable));
        OnPropertyChanged(nameof(CanGoToPreviousModpackPage));
        OnPropertyChanged(nameof(CanGoToNextModpackPage));
        OnPropertyChanged(nameof(ModpackPageInfoText));
        GoToNextModpackPageCommand.NotifyCanExecuteChanged();
        GoToPreviousModpackPageCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ShowLocalizedName(DownloadCatalogItem? item)
    {
        if (item == null)
        {
            return;
        }

        item.UseLocalizedName = true;
    }

    [RelayCommand]
    private void ShowSourceName(DownloadCatalogItem? item)
    {
        if (item == null)
        {
            return;
        }

        item.UseLocalizedName = false;
    }

    [RelayCommand]
    private void ShowLocalizedSummary(DownloadCatalogItem? item)
    {
        if (item == null)
        {
            return;
        }

        item.UseLocalizedSummary = true;
    }

    [RelayCommand]
    private void ShowSourceSummary(DownloadCatalogItem? item)
    {
        if (item == null)
        {
            return;
        }

        item.UseLocalizedSummary = false;
    }

    [RelayCommand]
    private void ToggleLocalizedDisplay(DownloadCatalogItem? item)
    {
        if (item == null)
        {
            return;
        }

        var useLocalized = !(item.UseLocalizedName && item.UseLocalizedSummary);
        item.UseLocalizedName = useLocalized;
        item.UseLocalizedSummary = useLocalized;
    }

    [RelayCommand]
    private void SelectCategory(DownloadCategory category)
    {
        SelectedCategory = category;
    }

    [RelayCommand]
    private async Task SearchSmapi()
    {
        await LoadCategoryItemsForCurrentCategoryAsync(initialLoad: false);
    }

    [RelayCommand]
    private async Task SearchMods()
    {
        ClearSearchCache();
        CurrentModPage = 1;
        await LoadCategoryItemsForCurrentCategoryAsync(initialLoad: false);
    }

    [RelayCommand]
    private async Task SearchModpacks()
    {
        CurrentModpackPage = 1;
        await LoadCategoryItemsForCurrentCategoryAsync(initialLoad: false);
    }

    [RelayCommand]
    private async Task LoadHotModsAsync()
    {
        ModSearchText = string.Empty;
        CurrentModPage = 1;
        _forceHotModsLoad = true;
        await LoadCategoryItemsForCurrentCategoryAsync(initialLoad: false);
    }

    [RelayCommand]
    private async Task ResetModFiltersAsync()
    {
        ModSearchText = string.Empty;
        SelectedModSource = "全部";
        SelectedModGameVersion = "全部";
        SelectedModType = "全部";
        SelectedModDescriptionMode = DescriptionModeLocalized;
        CurrentModPage = 1;
        _forceHotModsLoad = true;
        ClearSearchCache();
        await LoadCategoryItemsForCurrentCategoryAsync(initialLoad: false);
    }

    [RelayCommand(CanExecute = nameof(CanGoToPreviousModPage))]
    private async Task GoToPreviousModPage()
    {
        if (!CanGoToPreviousModPage)
        {
            return;
        }

        CurrentModPage--;
        await LoadCategoryItemsForCurrentCategoryAsync(initialLoad: false);
    }

    [RelayCommand(CanExecute = nameof(CanGoToNextModPage))]
    private async Task GoToNextModPage()
    {
        System.Diagnostics.Debug.WriteLine($"[Download] GoToNextModPage: CanGo={CanGoToNextModPage}, Page={CurrentModPage}, Total={TotalModPages}");
        if (!CanGoToNextModPage)
        {
            return;
        }

        CurrentModPage++;
        await LoadCategoryItemsForCurrentCategoryAsync(initialLoad: false);
    }

    [RelayCommand(CanExecute = nameof(CanGoToPreviousModpackPage))]
    private async Task GoToPreviousModpackPage()
    {
        if (!CanGoToPreviousModpackPage)
        {
            return;
        }

        CurrentModpackPage--;
        await LoadCategoryItemsForCurrentCategoryAsync(initialLoad: false);
    }

    [RelayCommand(CanExecute = nameof(CanGoToNextModpackPage))]
    private async Task GoToNextModpackPage()
    {
        if (!CanGoToNextModpackPage)
        {
            return;
        }

        CurrentModpackPage++;
        await LoadCategoryItemsForCurrentCategoryAsync(initialLoad: false);
    }

    [RelayCommand]
    private async Task OpenCatalogItemDetails(DownloadCatalogItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.DisplayText))
        {
            return;
        }

        await PromoteCatalogItemIconToFullAsync(item);

        OpenDetailsRequested?.Invoke(item.DisplayText);
    }

    private async Task PromoteCatalogItemIconToFullAsync(DownloadCatalogItem item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.FullIconSource))
        {
            return;
        }

        if (string.Equals(item.IconSource, item.FullIconSource, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        item.IconSource = item.FullIconSource;
        await ResolveRemoteIconToLocalAsync(item, Volatile.Read(ref _catalogLoadToken), ResolveCategoryFallbackIcon(item.SourceKey));
    }

    [RelayCommand]
    private void OpenSearchResultDetails(string? item)
    {
        if (string.IsNullOrWhiteSpace(item))
        {
            return;
        }

        OpenDetailsRequested?.Invoke(item);
    }

    [RelayCommand]
    private async Task ToggleCatalogItemExpandedAsync(DownloadCatalogItem? item)
    {
        if (item == null)
        {
            return;
        }

        item.IsExpanded = !item.IsExpanded;
        if (!item.IsExpanded || item.HasLoadedDetails || item.IsLoadingDetails)
        {
            return;
        }

        item.IsLoadingDetails = true;
        try
        {
            var details = await _remoteCatalogService.GetResourceDetailsAsync(item.DisplayText);
            if (!string.IsNullOrWhiteSpace(details.Source))
            {
                item.SourceTag = details.Source;
            }

            if (!string.IsNullOrWhiteSpace(details.Summary))
            {
                item.Summary = details.Summary;
            }

            ReplaceStringCollection(item.VersionOptions, details.VersionOptions);
            ReplaceStringCollection(item.DependencyOptions, details.Dependencies);
            ReplaceStringCollection(item.DownloadOptions, details.DownloadOptions);
            item.HasLoadedDetails = true;
        }
        catch (Exception ex)
        {
            item.Summary = $"加载详情失败: {ex.Message}";
        }
        finally
        {
            item.IsLoadingDetails = false;
        }
    }

    [RelayCommand]
    private void SelectTask(DownloadTaskItem? task)
    {
        if (task == null)
        {
            return;
        }

        SelectedTaskHint = $"已选择任务: {task.Name} ({task.Status})";
        TaskSelected?.Invoke(task);
        NavigateToTaskStatusRequested?.Invoke();
    }

    [RelayCommand]
    private async Task SelectGamePath()
    {
        RefreshGamePathState();
        if (ShowGamePathWarning)
        {
            var configured = await EnsureGamePathConfiguredAsync();
            if (configured)
            {
                Status = $"已配置游戏目录: {GamePathHint}";
                return;
            }

            Status = "未探测到游戏目录，已跳转到实例页进行配置";
            NavigateToInstancesRequested?.Invoke();
            return;
        }

        Status = $"已探测到游戏目录: {GamePathHint}";
    }

    [RelayCommand]
    private async Task QueueModpackUrlDownload()
    {
        if (!await EnsureGamePathConfiguredAsync())
        {
            ModpackImportStatus = "请先配置有效的游戏目录";
            Status = "入队失败：未配置游戏目录";
            return;
        }

        var rawUrl = ModpackUrlInput?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ModpackImportStatus = "Modpack 地址无效，请输入 HTTP/HTTPS 链接";
            Status = "入队失败：Modpack URL 无效";
            return;
        }

        var fileName = ResolveDownloadFileName(uri, ModpackFileNameInput);
        var targetFilePath = Path.Combine(_downloadRootPath, fileName);

        DownloadTasks.Insert(0, new DownloadTaskItem
        {
            Name = fileName,
            Status = "已加入队列（Modpack URL）",
            Progress = 0,
            TaskKind = DownloadTaskKind.Generic,
            SourceUrl = uri.ToString(),
            OutputFilePath = targetFilePath,
            CanCancel = false,
            CanRetry = false
        });
        DownloadTasks[0].StatusIconSource = ResolveTaskStatusIcon(DownloadTasks[0]);

        ModpackImportStatus = $"已入队：{fileName}";
        Status = "Modpack 下载任务已加入队列";
        EmitLog($"Modpack URL 入队: {fileName} -> {targetFilePath}");
        SaveTaskState();
        _ = ProcessQueueAsync();
        NavigateToTaskStatusRequested?.Invoke();
    }

    [RelayCommand]
    private void SelectFirstSearchResult()
    {
        if (!SearchResults.Any())
        {
            Status = "暂无可选搜索结果";
            return;
        }

        var selected = SearchResults[0];
        Status = $"已选择: {selected}";
        OpenDetailsRequested?.Invoke(selected);
    }

    [RelayCommand]
    private void OpenModSearchPage()
    {
        NavigateToModSearchRequested?.Invoke();
    }

    [RelayCommand]
    private void OpenModpackSearchPage()
    {
        NavigateToModpackSearchRequested?.Invoke();
    }

    [RelayCommand]
    private async Task ImportNxmLinkAsync()
    {
        if (!await EnsureGamePathConfiguredAsync())
        {
            NxmImportStatus = "请先配置有效的游戏目录";
            Status = "导入失败：未配置游戏目录";
            return;
        }

        var settings = _settingsStore.Load();
        if (string.IsNullOrWhiteSpace(settings.NexusApiKey) && string.IsNullOrWhiteSpace(settings.NexusOAuthAccessToken))
        {
            NxmImportStatus = "请先在设置页完成 Nexus 登录，再导入 NXM 链接";
            Status = "导入失败：Nexus 未登录";
            return;
        }

        if (!_nxmLinkParser.TryParse(NxmLinkInput, out var parsed, out var errorMessage))
        {
            NxmImportStatus = errorMessage;
            Status = "导入失败：链接格式不正确";
            return;
        }

        var taskName = parsed.ResourceType == NxmResourceType.Collection
            ? $"Nexus Collection {parsed.CollectionSlug} Rev {(parsed.RevisionNumber < 0 ? "latest" : parsed.RevisionNumber.ToString())}"
            : $"Nexus Mod {parsed.ModId} File {parsed.FileId}";

        var taskStatus = parsed.ResourceType == NxmResourceType.Collection
            ? "已加入队列（NXM Collection）"
            : "已加入队列（NXM Mod）";

        string sourceUrl = string.Empty;
        string outputFilePath = string.Empty;
        List<string> dependencyUrls = [];

        if (parsed.ResourceType == NxmResourceType.ModFile)
        {
            NxmImportStatus = "正在通过 Nexus API 解析真实下载地址...";
            var resolved = await _nexusModDownloadResolverService.ResolveDownloadUrlAsync(
                parsed,
                settings.NexusApiKey,
                settings.NexusOAuthAccessToken);

            if (!resolved.IsSuccess)
            {
                // 解析失败：尝试浏览器下载回退（非 Premium 用户路径）
                EmitLog($"NXM Mod 地址解析失败，尝试浏览器回退: {resolved.Message}");
                var fallbackNxmLink = await TryBrowserDownloadFallbackAsync(
                    parsed.ModId,
                    parsed.FileId,
                    BuildNexusWebUrl(parsed));

                if (fallbackNxmLink != null &&
                    _nxmLinkParser.TryParse(fallbackNxmLink, out var fallbackInfo, out _))
                {
                    // 用浏览器回传的 NXM key 重新解析
                    var fallbackResolved = await _nexusModDownloadResolverService.ResolveDownloadUrlAsync(
                        fallbackInfo,
                        settings.NexusApiKey,
                        settings.NexusOAuthAccessToken);

                    if (fallbackResolved.IsSuccess)
                    {
                        resolved = fallbackResolved;
                        EmitLog($"浏览器回退解析成功: {fallbackResolved.FileName}");
                    }
                    else
                    {
                        NxmImportStatus = fallbackResolved.Message;
                        Status = "导入失败：浏览器回退解析仍失败";
                        await _dialogService.ShowBrowserDownloadGuideDialogAsync(
                            BuildNexusWebUrl(parsed),
                            "浏览器下载指引",
                            "请在打开的文件页面点击『Slow Download』完成下载（非 Premium 账号使用慢速下载），下载完成后返回。");
                        return;
                    }
                }
                else
                {
                    NxmImportStatus = resolved.Message;
                    Status = "导入失败：无法解析真实下载地址";
                    EmitLog($"浏览器回退未获得有效 NXM 回调: {resolved.Message}");
                    await _dialogService.ShowBrowserDownloadGuideDialogAsync(
                        BuildNexusWebUrl(parsed),
                        "浏览器下载指引",
                        "请在打开的文件页面点击『Slow Download』完成下载（非 Premium 账号使用慢速下载），下载完成后返回。");
                    return;
                }
            }

            var resolvedFileName = ResolveDownloadFileName(new Uri(resolved.DownloadUrl), resolved.FileName);
            sourceUrl = resolved.DownloadUrl;
            outputFilePath = Path.Combine(_downloadRootPath, resolvedFileName);
            taskName = resolvedFileName;
            taskStatus = "已加入队列（NXM Mod 实下载）";
        }
        else
        {
            NxmImportStatus = "正在通过 Nexus API 解析 Collection 下载地址...";
            var resolved = await _nexusModDownloadResolverService.ResolveCollectionDownloadUrlAsync(
                parsed,
                settings.NexusApiKey,
                settings.NexusOAuthAccessToken);

            if (!resolved.IsSuccess)
            {
                // Collection 解析失败：尝试浏览器回退（等待 Add collection 回调，不重复弹指引）
                EmitLog($"NXM Collection 地址解析失败，尝试浏览器回退: {resolved.Message}");
                var fallbackNxmLink = await TryCollectionBrowserDownloadFallbackAsync(
                    parsed.CollectionSlug,
                    parsed.RevisionNumber,
                    BuildNexusWebUrl(parsed),
                    "NXM Collection 导入");

                if (fallbackNxmLink != null &&
                    _nxmLinkParser.TryParse(fallbackNxmLink, out var fallbackInfo, out _))
                {
                    // 用浏览器回传的 NXM key 重新解析
                    var fallbackResolved = await _nexusModDownloadResolverService.ResolveCollectionDownloadUrlAsync(
                        fallbackInfo,
                        settings.NexusApiKey,
                        settings.NexusOAuthAccessToken);

                    if (fallbackResolved.IsSuccess)
                    {
                        resolved = fallbackResolved;
                        EmitLog($"Collection 浏览器回退解析成功: {fallbackResolved.FileName}");
                    }
                    else
                    {
                        NxmImportStatus = fallbackResolved.Message;
                        Status = "导入失败：Collection 浏览器回退解析仍失败";
                        return;
                    }
                }
                else
                {
                    NxmImportStatus = resolved.Message;
                    Status = "导入失败：浏览器回退超时或取消";
                    return;
                }
            }

            var resolvedFileName = ResolveDownloadFileName(new Uri(resolved.DownloadUrl), resolved.FileName);
            sourceUrl = resolved.DownloadUrl;
            outputFilePath = Path.Combine(_downloadRootPath, resolvedFileName);
            taskName = resolvedFileName;
            taskStatus = "已加入队列（NXM Collection 实下载）";
            dependencyUrls = resolved.DownloadUrls
                .Where(url => !string.Equals(url, sourceUrl, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        DownloadTasks.Insert(0, new DownloadTaskItem
        {
            Name = taskName,
            Status = taskStatus,
            Progress = 0,
            TaskKind = parsed.ResourceType == NxmResourceType.Collection
                ? DownloadTaskKind.NxmCollection
                : DownloadTaskKind.NxmMod,
            SourceUrl = sourceUrl,
            OutputFilePath = outputFilePath,
            DependencyUrls = dependencyUrls,
            CanCancel = false,
            CanRetry = false
        });
        DownloadTasks[0].StatusIconSource = ResolveTaskStatusIcon(DownloadTasks[0]);

        NxmImportStatus = $"已解析并入队：{parsed}";
        Status = "NXM 链接已加入下载队列";
        SaveTaskState();
        _ = ProcessQueueAsync();
        NavigateToTaskStatusRequested?.Invoke();
    }

    /// <summary>
    /// 处理外部传入的 NXM 链接（来自浏览器协议回调或单实例转发）。
    /// 设置 NxmLinkInput 后复用 ImportNxmLinkAsync 的解析与入队逻辑。
    /// </summary>
    public async Task HandleExternalNxmLinkAsync(string link)
    {
        if (string.IsNullOrWhiteSpace(link))
        {
            return;
        }

        NxmLinkInput = link;
        await ImportNxmLinkAsync();
    }

    [RelayCommand]
    private async Task QueueUrlDownload()
    {
        if (!await EnsureGamePathConfiguredAsync())
        {
            UrlDownloadStatus = "请先配置有效的游戏目录";
            Status = "入队失败：未配置游戏目录";
            return;
        }

        var rawUrl = DownloadUrlInput?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            UrlDownloadStatus = "下载地址无效，请输入 HTTP/HTTPS 链接";
            Status = "入队失败：URL 无效";
            return;
        }

        var fileName = ResolveDownloadFileName(uri, DownloadFileNameInput);
        var targetFilePath = Path.Combine(_downloadRootPath, fileName);

        DownloadTasks.Insert(0, new DownloadTaskItem
        {
            Name = fileName,
            Status = "已加入队列（URL 下载）",
            Progress = 0,
            TaskKind = DownloadTaskKind.Generic,
            SourceUrl = uri.ToString(),
            OutputFilePath = targetFilePath,
            CanCancel = false,
            CanRetry = false
        });
        DownloadTasks[0].StatusIconSource = ResolveTaskStatusIcon(DownloadTasks[0]);

        UrlDownloadStatus = $"已入队：{fileName}";
        Status = "URL 下载任务已加入队列";
        EmitLog($"URL 下载入队: {fileName} -> {targetFilePath}");
        SaveTaskState();
        _ = ProcessQueueAsync();
        NavigateToTaskStatusRequested?.Invoke();
    }

    [RelayCommand]
    private void RetryTask(DownloadTaskItem? task)
    {
        if (task == null || !task.CanRetry)
        {
            return;
        }

        if (task.TaskKind == DownloadTaskKind.NxmCollection && task.FailedDownloadUrls.Count > 0)
        {
            var retryUrls = task.FailedDownloadUrls
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (retryUrls.Count > 0)
            {
                task.SourceUrl = retryUrls[0];
                task.DependencyUrls = retryUrls.Skip(1).ToList();
                EmitLog($"Collection 失败项重试: 本次仅重试 {retryUrls.Count} 个文件");
            }
        }

        task.FailedDetails = string.Empty;
        task.CanRetry = false;
        task.CanCancel = false;
        task.Progress = 0;
        task.SetState(DownloadTaskState.Pending, "已加入队列（重试）");
        Status = $"任务已重新入队: {task.Name}";
        EmitLog($"任务重试入队: {task.Name}");
        SaveTaskState();
        _ = ProcessQueueAsync();
    }

    [RelayCommand]
    private void CancelTask(DownloadTaskItem? task)
    {
        if (task == null || !task.CanCancel)
        {
            return;
        }

        // 外部流程创建的任务（如版本设置页的 SMAPI 更新）通过任务项自带回调取消
        if (task.CancelRequested is { } externalCancel)
        {
            externalCancel();
            return;
        }

        if (_runningTaskCancellationSources.TryGetValue(task, out var cts))
        {
            cts.Cancel();
            return;
        }

        task.CanCancel = false;
        task.SetState(DownloadTaskState.Cancelled, "已取消");
        TaskStateChanged?.Invoke(task);
        EmitLog($"任务取消: {task.Name}");
        SaveTaskState();
    }

    [RelayCommand]
    private void OpenTaskReport(DownloadTaskItem? task)
    {
        if (task == null || string.IsNullOrWhiteSpace(task.ReportPath))
        {
            Status = "未找到可打开的安装报告";
            return;
        }

        if (!File.Exists(task.ReportPath) && !Directory.Exists(task.ReportPath))
        {
            Status = "安装报告路径不存在";
            return;
        }

        TryOpenPath(task.ReportPath);
        EmitLog($"打开安装报告: {task.ReportPath}");
    }

    [RelayCommand]
    private void OpenTaskBackup(DownloadTaskItem? task)
    {
        if (task == null || string.IsNullOrWhiteSpace(task.BackupPath))
        {
            Status = "未找到可打开的备份目录";
            return;
        }

        if (!Directory.Exists(task.BackupPath))
        {
            Status = "备份目录不存在";
            return;
        }

        TryOpenPath(task.BackupPath);
        EmitLog($"打开备份目录: {task.BackupPath}");
    }

    [RelayCommand]
    private void OpenTaskRetryReport(DownloadTaskItem? task)
    {
        if (task == null || string.IsNullOrWhiteSpace(task.RetryReportPath))
        {
            Status = "未找到可打开的重试报告";
            return;
        }

        if (!File.Exists(task.RetryReportPath))
        {
            Status = "重试报告路径不存在";
            return;
        }

        TryOpenPath(task.RetryReportPath);
        EmitLog($"打开重试报告: {task.RetryReportPath}");
    }

    [RelayCommand]
    private async Task CopyTaskFailedDetailsAsync(DownloadTaskItem? task)
    {
        if (task == null || string.IsNullOrWhiteSpace(task.FailedDetails))
        {
            Status = "当前任务没有可复制的失败明细";
            return;
        }

        var clipboard = GetClipboard();
        if (clipboard == null)
        {
            Status = "当前环境不支持剪贴板";
            return;
        }

        await clipboard.SetTextAsync(task.FailedDetails);
        Status = "失败明细已复制到剪贴板";
    }

    [RelayCommand]
    private void OpenTaskDirectory(DownloadTaskItem? task)
    {
        if (task == null || string.IsNullOrWhiteSpace(task.InstalledDirectory))
        {
            Status = "当前任务没有可打开的安装目录";
            return;
        }

        if (!Directory.Exists(task.InstalledDirectory))
        {
            Status = "安装目录不存在";
            return;
        }

        TryOpenPath(task.InstalledDirectory);
        EmitLog($"打开安装目录: {task.InstalledDirectory}");
    }

    [RelayCommand]
    private void RemoveTask(DownloadTaskItem? task)
    {
        if (task == null)
        {
            return;
        }

        // 运行中任务需先取消再移除，避免悬挂的 CTS
        if (task.CanCancel && _runningTaskCancellationSources.TryGetValue(task, out var cts))
        {
            cts.Cancel();
            _runningTaskCancellationSources.Remove(task);
        }

        DownloadTasks.Remove(task);
        Status = $"已移除任务: {task.Name}";
        EmitLog($"任务已移除: {task.Name}");
        SaveTaskState();
    }

    [RelayCommand]
    private void ClearCompletedTasks()
    {
        var finished = DownloadTasks.Where(t => t.IsFinished).ToList();
        if (finished.Count == 0)
        {
            Status = "没有可清理的已完成任务";
            return;
        }

        foreach (var task in finished)
        {
            DownloadTasks.Remove(task);
        }

        Status = $"已清理 {finished.Count} 个已完成任务";
        EmitLog($"清理已完成任务: {finished.Count} 个");
        SaveTaskState();
    }

    public void AddTaskFromExternal(ExternalDownloadRequest request)
    {
        _ = AddTaskFromExternalAsync(request);
    }

    public async Task<bool> AddTaskFromExternalAsync(ExternalDownloadRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.ResourceName))
        {
            return false;
        }

        try
        {
            if (request.Action == ExternalDownloadAction.SaveAs)
            {
                return await QueueSaveOnlyTaskFromExternalAsync(request);
            }

            if (IsSmapiExternalRequest(request))
            {
                return await QueueSmapiInstallTaskFromExternalAsync(request);
            }

            // 诊断：若请求看起来像 SMAPI 但未被识别，记录字段便于定位
            var diag = string.Join('|',
                request.ResourceName ?? string.Empty,
                request.ResourceId ?? string.Empty,
                request.ResourceSource ?? string.Empty,
                request.SourceToken ?? string.Empty,
                request.SourcePageUrl ?? string.Empty);
            if (diag.Contains("smapi", StringComparison.OrdinalIgnoreCase) ||
                diag.Contains("2400", StringComparison.Ordinal) ||
                diag.Contains("898372", StringComparison.Ordinal))
            {
                EmitLog($"[SMAPI路由] 疑似 SMAPI 但未识别: name={request.ResourceName}, id={request.ResourceId}, src={request.ResourceSource}, token={request.SourceToken}, isSmapi={request.IsSmapiResource}");
            }

            // Nexus Collection 安装：先选择 Base 路径和输入版本名，再入队
            if (request.IsCollection)
            {
                return await QueueCollectionInstallTaskFromExternalAsync(request);
            }

            // Curseforge/SVL 整合包安装：先选择 Base 路径和输入版本名，再入队
            if (request.IsModpack)
            {
                return await QueueModpackInstallTaskFromExternalAsync(request);
            }

            return await QueueGenericInstallTaskFromExternalAsync(request);
        }
        catch (OperationCanceledException)
        {
            Status = "已取消外部下载任务";
            return false;
        }
        catch (Exception ex)
        {
            Status = $"外部下载任务创建失败: {ex.Message}";
            EmitLog($"外部下载任务创建异常: {ex}");
            return false;
        }
    }

    /// <summary>
    /// 批量更新入队：遍历可更新 Mod 列表，NXM 链接走 API 解析（失败则跳过并记录日志），
    /// HTTP 直链直接入队。批量模式不触发浏览器回退（需逐个用户交互，不适合批量场景）。
    /// </summary>
    public async Task EnqueueBatchUpdateAsync(IReadOnlyList<ModBatchUpdateEntry> entries)
    {
        if (entries == null || entries.Count == 0)
        {
            return;
        }

        if (!await EnsureGamePathConfiguredAsync())
        {
            Status = "批量更新失败：未配置游戏目录";
            EmitLog("批量更新失败：未配置游戏目录");
            return;
        }

        var settings = _settingsStore.Load();
        var queued = 0;
        var skipped = 0;

        foreach (var entry in entries)
        {
            try
            {
                if (entry.UpdateUrl.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
                {
                    // NexusMods NXM 链接：解析真实下载地址后入队
                    if (!_nxmLinkParser.TryParse(entry.UpdateUrl, out var parsed, out var parseError))
                    {
                        EmitLog($"批量更新跳过 {entry.DisplayName}: NXM 链接解析失败 - {parseError}");
                        skipped++;
                        continue;
                    }

                    if (parsed.ResourceType != NxmResourceType.ModFile)
                    {
                        EmitLog($"批量更新跳过 {entry.DisplayName}: 仅支持 Mod 文件 NXM 链接");
                        skipped++;
                        continue;
                    }

                    var resolved = await _nexusModDownloadResolverService.ResolveDownloadUrlAsync(
                        parsed,
                        settings.NexusApiKey,
                        settings.NexusOAuthAccessToken);

                    if (!resolved.IsSuccess)
                    {
                        EmitLog($"批量更新跳过 {entry.DisplayName}: NXM 地址解析失败 - {resolved.Message}（可手动单个更新以走浏览器回退）");
                        skipped++;
                        continue;
                    }

                    var resolvedFileName = ResolveDownloadFileName(new Uri(resolved.DownloadUrl), resolved.FileName);
                    var outputPath = Path.Combine(_downloadRootPath, resolvedFileName);
                    var task = new DownloadTaskItem
                    {
                        Name = resolvedFileName,
                        TaskKind = DownloadTaskKind.NxmMod,
                        TaskAction = DownloadTaskAction.InstallMod,
                        SourceUrl = resolved.DownloadUrl,
                        OutputFilePath = outputPath,
                        SourceModId = parsed.ModId,
                        SourceFileId = parsed.FileId,
                        CanCancel = false,
                        CanRetry = false
                    };
                    task.SetState(DownloadTaskState.Pending, "已加入队列（批量更新）");
                    DownloadTasks.Insert(0, task);
                    DownloadTasks[0].StatusIconSource = ResolveTaskStatusIcon(DownloadTasks[0]);
                    EmitLog($"批量更新入队: {entry.DisplayName} -> {resolvedFileName}");
                    queued++;
                }
                else if (Uri.TryCreate(entry.UpdateUrl, UriKind.Absolute, out var uri) &&
                         (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    // Curseforge HTTP 直链：直接入队
                    var fileName = ResolveDownloadFileName(uri, $"{entry.DisplayName}.zip");
                    var outputPath = Path.Combine(_downloadRootPath, fileName);
                    var task = new DownloadTaskItem
                    {
                        Name = fileName,
                        TaskKind = DownloadTaskKind.Generic,
                        TaskAction = DownloadTaskAction.InstallMod,
                        SourceUrl = uri.ToString(),
                        OutputFilePath = outputPath,
                        CanCancel = false,
                        CanRetry = false
                    };
                    task.SetState(DownloadTaskState.Pending, "已加入队列（批量更新）");
                    DownloadTasks.Insert(0, task);
                    DownloadTasks[0].StatusIconSource = ResolveTaskStatusIcon(DownloadTasks[0]);
                    EmitLog($"批量更新入队: {entry.DisplayName} -> {fileName}");
                    queued++;
                }
                else
                {
                    EmitLog($"批量更新跳过 {entry.DisplayName}: 无法识别的下载链接 {entry.UpdateUrl}");
                    skipped++;
                }
            }
            catch (Exception ex)
            {
                EmitLog($"批量更新异常 {entry.DisplayName}: {ex.Message}");
                skipped++;
            }
        }

        Status = queued > 0
            ? $"批量更新：已入队 {queued} 个，跳过 {skipped} 个"
            : $"批量更新：全部跳过（{skipped} 个无可用链接）";
        SaveTaskState();
        if (queued > 0)
        {
            _ = ProcessQueueAsync();
            NavigateToTaskStatusRequested?.Invoke();
        }
    }

    private async Task<bool> QueueGenericInstallTaskFromExternalAsync(ExternalDownloadRequest request)
    {
        // 安全网：若请求实为 SMAPI（名称/ID/下载选项含 smapi 或 2400/898372），
        // 强制路由到 SMAPI 安装流程，避免被当成普通 MOD 装进 Mods 文件夹。
        // 用直接字段检查（不依赖 IsSmapiExternalRequest，因为调用方已判定过）。
        var looksSmapi =
            (request.ResourceName?.Contains("smapi", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (request.ResourceId?.Trim() == "2400") ||
            (request.ResourceId?.Trim() == "898372") ||
            (request.SelectedDownloadOption?.Contains("smapi", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (request.SelectedDownloadOption?.Contains("/2400/", StringComparison.Ordinal) ?? false);
        if (looksSmapi)
        {
            return await QueueSmapiInstallTaskFromExternalAsync(request);
        }

        // 检查当前实例是否已选择（参考旧架构 GetCurrentSelectedInstance）。
        // 如果没有当前实例，或者当前实例是原版（无 SMAPI），只能从 SMAPI 实例中选择，
        // 不能复用 Base 路径列表，否则会把 Mod 错装到原版目录。
        var currentModsPath = ResolveCurrentInstanceModsPath();
        var needInstanceSelection = string.IsNullOrWhiteSpace(currentModsPath) || IsCurrentInstanceVanilla();

        if (needInstanceSelection)
        {
            var availableTargets = AvailableModInstancesProvider?.Invoke() ?? [];
            if (availableTargets.Count == 0)
            {
                Status = "当前没有可用的 SMAPI 实例，请先在实例页面安装 SMAPI";
                return false;
            }

            var availablePaths = availableTargets
                .Select(target => target.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var currentPath = ResolveCurrentGamePath();
            var defaultPath = availablePaths.FirstOrDefault(path =>
                !string.IsNullOrWhiteSpace(currentPath) &&
                string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase));

            var selectedPath = await _dialogService.ShowInstanceSelectionDialogAsync(
                availablePaths,
                "选择要安装 Mod 的 SMAPI 实例",
                selectedInstance: defaultPath);

            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                Status = "已取消 Mod 安装（未选择实例）";
                return false;
            }

            var selectedTarget = availableTargets.FirstOrDefault(target =>
                string.Equals(target.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
            if (selectedTarget == null)
            {
                Status = "所选实例已失效，请刷新实例列表后重试";
                return false;
            }

            // 临时切换到用户选择的 SMAPI 实例，并同步实例名，保证隔离实例的 Mods 路径正确。
            var settings = _settingsStore.Load();
            settings.PreferredInstancePath = selectedTarget.Path;
            settings.InstanceName = selectedTarget.IsBaseInstance ? string.Empty : selectedTarget.Name;
            settings.PreferredLaunchMode = "SMAPI";
            _settingsStore.Save(settings);
        }

        // 普通 Mod 也必须先解析真实下载地址。没有 URL 的 Nexus 文件不能直接入队，
        // 否则 ExecuteTaskAsync 会把它误判为无源任务并走“模拟完成”分支。
        var resolved = await ResolveExternalDownloadTargetAsync(request);
        if (!resolved.IsSuccess)
        {
            Status = resolved.Message;
            if (!string.IsNullOrWhiteSpace(resolved.BrowserGuideUrl))
            {
                await _dialogService.ShowBrowserDownloadGuideDialogAsync(
                    resolved.BrowserGuideUrl,
                    "浏览器下载指引",
                    "该资源当前无法直接解析下载地址，请在浏览器完成下载后再返回重试。"
                );
            }

            return false;
        }

        var sourceUrl = resolved.DownloadUrl;
        var sourceUri = new Uri(sourceUrl);
        var fileName = CreateSafeFileName(
            ResolveDownloadFileName(sourceUri, resolved.FileName));
        var outputPath = Path.Combine(_downloadRootPath, fileName);
        var taskName = fileName;

        var sourceToken = NormalizeSourceToken(request);
        long? sourceModId = sourceToken == "nexusmods" &&
                            TryExtractPositiveLong(request.ResourceId, out var smodId)
            ? smodId
            : (long?)null;
        long? sourceFileId = sourceToken == "nexusmods" &&
                             TryExtractFileIdFromOption(request.SelectedDownloadOption, out var sfileId)
            ? sfileId
            : (long?)null;

        var task = new DownloadTaskItem
        {
            Name = taskName,
            Status = "已加入队列（真实下载）",
            Progress = 0,
            TaskKind = sourceToken == "nexusmods" ? DownloadTaskKind.NxmMod : DownloadTaskKind.Generic,
            TaskAction = DownloadTaskAction.InstallMod,
            SourceUrl = sourceUrl,
            OutputFilePath = outputPath,
            SourceModId = sourceModId,
            SourceFileId = sourceFileId,
            CanCancel = false,
            CanRetry = false
        };

        EnqueueExternalTask(task, $"已加入下载队列: {taskName}");
        return true;
    }

    private async Task<bool> QueueSaveOnlyTaskFromExternalAsync(ExternalDownloadRequest request)
    {
        // 先弹出另存为对话框让用户输入文件名，再解析下载地址
        // 这样即使 Nexus 非 Premium 需要浏览器回调，用户也能先确定保存路径
        var suggestedFileName = CreateSafeFileName(request.ResolveSuggestedFileName());
        var savePath = await _dialogService.SaveFilePathAsync(
            "另存为",
            suggestedFileName,
            BuildSaveFileTypes(suggestedFileName));
        if (string.IsNullOrWhiteSpace(savePath))
        {
            Status = "已取消另存为";
            return false;
        }

        // 解析下载地址
        var resolved = await ResolveExternalDownloadTargetAsync(request);
        if (!resolved.IsSuccess)
        {
            Status = resolved.Message;
            if (!string.IsNullOrWhiteSpace(resolved.BrowserGuideUrl))
            {
                await _dialogService.ShowBrowserDownloadGuideDialogAsync(
                    resolved.BrowserGuideUrl,
                    "浏览器下载指引",
                    "该资源当前无法直接解析下载地址，请在浏览器完成下载后再返回。"
                );
            }

            return false;
        }

        // 另存为后缀名自动更正：如果用户输入的文件名缺少有效的压缩包扩展名，
        // 从下载 URL 中提取扩展名并附加
        if (Uri.TryCreate(resolved.DownloadUrl, UriKind.Absolute, out var resolvedUri))
        {
            var correctedFileName = ResolveDownloadFileName(resolvedUri, Path.GetFileName(savePath));
            var currentDir = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrWhiteSpace(currentDir))
            {
                savePath = Path.Combine(currentDir, correctedFileName);
            }
        }

        var task = new DownloadTaskItem
        {
            Name = Path.GetFileName(savePath),
            Status = "已加入队列（另存为）",
            Progress = 0,
            TaskKind = DownloadTaskKind.Generic,
            TaskAction = DownloadTaskAction.SaveOnly,
            SourceUrl = resolved.DownloadUrl,
            OutputFilePath = savePath,
            CanCancel = false,
            CanRetry = false
        };

        EnqueueExternalTask(task, $"已加入另存为队列: {task.Name}");
        return true;
    }

    private async Task<bool> QueueSmapiInstallTaskFromExternalAsync(ExternalDownloadRequest request)
    {
        // 获取可用的 Base 路径列表（与整合包/Collection 安装流程一致）
        var availablePaths = AvailableGamePathsProvider?.Invoke();
        if (availablePaths == null || availablePaths.Count == 0)
        {
            var currentPath = ResolveCurrentGamePath();
            if (string.IsNullOrWhiteSpace(currentPath))
            {
                Status = "SMAPI 安装失败：未检测到可用 Base 路径，请先在实例页面添加游戏路径";
                return false;
            }
            availablePaths = new List<string> { currentPath };
        }

        // 默认选中当前首选路径（主页选中实例对应的 Base 路径）
        var defaultPath = ResolveCurrentGamePath();
        if (string.IsNullOrWhiteSpace(defaultPath) && availablePaths.Count > 0)
        {
            defaultPath = availablePaths[0];
        }

        // 弹出 Base 路径选择对话框（下拉栏，可切换）
        var gameBasePath = await _dialogService.ShowInstanceSelectionDialogAsync(
            availablePaths,
            "选择 SMAPI 基础路径",
            selectedInstance: defaultPath);

        if (string.IsNullOrWhiteSpace(gameBasePath))
        {
            Status = "已取消 SMAPI 安装";
            return false;
        }

        var defaultName = BuildSmapiDefaultInstanceName(request);
        var existingNames = GetExistingInstanceNames(gameBasePath);
        var rawInstanceName = await _dialogService.ShowInstanceNameDialogAsync("输入 SMAPI 实例名称", defaultName, existingNames);
        if (string.IsNullOrWhiteSpace(rawInstanceName))
        {
            Status = "已取消 SMAPI 安装";
            return false;
        }

        var instanceName = CreateSafeFileName(rawInstanceName);
        if (string.IsNullOrWhiteSpace(instanceName))
        {
            Status = "实例名称无效";
            return false;
        }

        var versionRoot = Path.Combine(gameBasePath, "versions", instanceName);
        if (Directory.Exists(versionRoot))
        {
            Status = $"实例名称已存在: {instanceName}";
            return false;
        }

        var resolved = await ResolveExternalDownloadTargetAsync(request);
        if (!resolved.IsSuccess)
        {
            Status = resolved.Message;
            if (!string.IsNullOrWhiteSpace(resolved.BrowserGuideUrl))
            {
                await _dialogService.ShowBrowserDownloadGuideDialogAsync(
                    resolved.BrowserGuideUrl,
                    "浏览器下载指引",
                    "请在打开的文件页面点击『Slow Download』完成下载（非 Premium 账号使用慢速下载），下载完成后返回。"
                );
            }

            return false;
        }

        var safeResolvedFileName = CreateSafeFileName(resolved.FileName);
        var outputPath = Path.Combine(_downloadRootPath, safeResolvedFileName);
        var sourceToken = NormalizeSourceToken(request);
        var smapiModId = sourceToken == "nexusmods" &&
                         TryExtractPositiveLong(request.ResourceId, out var smapiModIdV)
            ? smapiModIdV
            : (long?)null;
        var smapiFileId = sourceToken == "nexusmods" &&
                          TryExtractFileIdFromOption(request.SelectedDownloadOption, out var smapiFileIdV)
            ? smapiFileIdV
            : (long?)null;
        var task = new DownloadTaskItem
        {
            Name = $"SMAPI 安装 - {instanceName}",
            Status = "已加入队列（SMAPI 安装）",
            Progress = 0,
            TaskKind = sourceToken == "nexusmods" ? DownloadTaskKind.NxmMod : DownloadTaskKind.Generic,
            TaskAction = DownloadTaskAction.InstallSmapi,
            SourceUrl = resolved.DownloadUrl,
            OutputFilePath = outputPath,
            SourceModId = smapiModId,
            SourceFileId = smapiFileId,
            TargetGamePath = gameBasePath,
            TargetInstanceName = instanceName,
            CanCancel = false,
            CanRetry = false
        };

        EnqueueExternalTask(task, $"已加入 SMAPI 安装队列: {instanceName}");
        return true;
    }

    /// <summary>
    /// Nexus Collection 安装入队：参考旧架构 ModpackDropDialog 流程，
    /// 先弹出 Base 路径选择对话框（仅 Base 路径），再弹出实例名输入对话框，
    /// 最后将选定的路径和实例名附加到下载任务中入队。
    /// </summary>
    private async Task<bool> QueueCollectionInstallTaskFromExternalAsync(ExternalDownloadRequest request)
    {
        // 获取可用的 Base 路径列表
        var availablePaths = AvailableGamePathsProvider?.Invoke();
        if (availablePaths == null || availablePaths.Count == 0)
        {
            // 后备：使用当前游戏路径
            var currentPath = ResolveCurrentGamePath();
            if (string.IsNullOrWhiteSpace(currentPath))
            {
                Status = "Collection 安装失败：未检测到可用 Base 路径，请先在实例页面添加游戏路径";
                return false;
            }
            availablePaths = new List<string> { currentPath };
        }

        // 默认选中当前首选路径
        var defaultPath = ResolveCurrentGamePath();
        if (string.IsNullOrWhiteSpace(defaultPath) && availablePaths.Count > 0)
        {
            defaultPath = availablePaths[0];
        }

        // 步骤 1：弹出 Base 路径选择对话框
        var selectedPath = await _dialogService.ShowInstanceSelectionDialogAsync(
            availablePaths,
            "选择 Base 路径",
            selectedInstance: defaultPath);

        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            Status = "已取消 Collection 安装";
            return false;
        }

        // 步骤 2：弹出实例名输入对话框
        var defaultInstanceName = !string.IsNullOrWhiteSpace(request.TargetInstanceName)
            ? request.TargetInstanceName
            : GenerateCollectionDefaultInstanceName(request);

        var rawInstanceName = await _dialogService.ShowInstanceNameDialogAsync(
            "输入版本名称",
            defaultInstanceName,
            GetExistingInstanceNames(selectedPath));

        if (string.IsNullOrWhiteSpace(rawInstanceName))
        {
            Status = "已取消 Collection 安装";
            return false;
        }

        var instanceName = CreateSafeFileName(rawInstanceName);
        if (string.IsNullOrWhiteSpace(instanceName))
        {
            Status = "版本名称无效";
            return false;
        }

        // 检查版本目录是否已存在
        var versionRoot = Path.Combine(selectedPath, "versions", instanceName);
        if (Directory.Exists(versionRoot))
        {
            Status = $"版本名称已存在: {instanceName}";
            return false;
        }

        // 解析 Collection 下载地址：直接使用 Nexus Collection API，不走通用的 ResolveExternalDownloadTargetAsync
        // （通用方法仅处理 mod/file id，Collection 没有 mod/file id，会直接失败）
        var settings = _settingsStore.Load();
        var nxmInfo = new NxmLinkInfo
        {
            GameDomain = "stardewvalley",
            ResourceType = NxmResourceType.Collection,
            CollectionSlug = request.CollectionSlug ?? string.Empty,
            RevisionNumber = request.CollectionRevision
        };

        var resolved = await ResolveCollectionDownloadAsync(nxmInfo, settings);
        if (!resolved.IsSuccess)
        {
            // Collection API 解析失败：尝试浏览器回退（等待 Add collection 回调，不重复弹指引）
            if (!string.IsNullOrWhiteSpace(request.CollectionSlug))
            {
                var collectionBrowserUrl = $"https://next.nexusmods.com/stardewvalley/collections/{request.CollectionSlug}";

                var fallbackNxmLink = await TryCollectionBrowserDownloadFallbackAsync(
                    request.CollectionSlug,
                    request.CollectionRevision,
                    collectionBrowserUrl,
                    "Collection 安装");

                if (!string.IsNullOrWhiteSpace(fallbackNxmLink) &&
                    _nxmLinkParser.TryParse(fallbackNxmLink, out var fallbackInfo, out _))
                {
                    // 用浏览器回传的 NXM key 重新解析 Collection 下载地址
                    var fallbackResolved = await ResolveCollectionDownloadAsync(fallbackInfo, settings);

                    if (fallbackResolved.IsSuccess)
                    {
                        var fallbackFileName = ResolveDownloadFileName(
                            new Uri(fallbackResolved.DownloadUrl), fallbackResolved.FileName);
                        resolved = ResolvedExternalDownloadTarget.Success(
                            fallbackResolved.DownloadUrl, fallbackFileName);
                        EmitLog($"Collection 浏览器回退解析成功: {fallbackResolved.FileName}");
                    }
                    else
                    {
                        Status = $"Collection 浏览器回退解析仍失败: {fallbackResolved.Message}";
                        return false;
                    }
                }
                else
                {
                    Status = "已取消 Collection 安装（浏览器回退超时或取消）";
                    return false;
                }
            }
            else
            {
                Status = resolved.Message;
                return false;
            }
        }

        var safeResolvedFileName = CreateSafeFileName(resolved.FileName);
        var outputPath = Path.Combine(_downloadRootPath, safeResolvedFileName);
        var task = new DownloadTaskItem
        {
            Name = $"Collection 安装 - {instanceName}",
            Status = "已加入队列（Collection 安装）",
            Progress = 0,
            TaskKind = DownloadTaskKind.Generic,
            TaskAction = DownloadTaskAction.InstallCollection,
            SourceUrl = resolved.DownloadUrl,
            OutputFilePath = outputPath,
            TargetGamePath = selectedPath,
            TargetInstanceName = instanceName,
            CanCancel = false,
            CanRetry = false
        };

        EnqueueExternalTask(task, $"已加入 Collection 安装队列: {instanceName}");
        return true;
    }

    /// <summary>根据 Collection 资源信息生成默认实例名。</summary>
    private static string GenerateCollectionDefaultInstanceName(ExternalDownloadRequest request)
    {
        var name = request.ResourceName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Collection";
        }

        // 移除文件名中常见的非法字符
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var c in invalid)
        {
            name = name.Replace(c, '-');
        }

        // 截断到 30 字符
        return name.Length > 30 ? name[..30] : name;
    }

    /// <summary>
    /// Curseforge/SVL 整合包安装入队：与 Collection 安装流程一致，
    /// 先弹出 Base 路径选择对话框，再弹出实例名输入对话框，最后入队下载+安装。
    /// </summary>
    private async Task<bool> QueueModpackInstallTaskFromExternalAsync(ExternalDownloadRequest request)
    {
        // 获取可用的 Base 路径列表
        var availablePaths = AvailableGamePathsProvider?.Invoke();
        if (availablePaths == null || availablePaths.Count == 0)
        {
            var currentPath = ResolveCurrentGamePath();
            if (string.IsNullOrWhiteSpace(currentPath))
            {
                Status = "整合包安装失败：未检测到可用 Base 路径，请先在实例页面添加游戏路径";
                return false;
            }
            availablePaths = new List<string> { currentPath };
        }

        var defaultPath = ResolveCurrentGamePath();
        if (string.IsNullOrWhiteSpace(defaultPath) && availablePaths.Count > 0)
        {
            defaultPath = availablePaths[0];
        }

        // 步骤 1：弹出 Base 路径选择对话框
        var selectedPath = await _dialogService.ShowInstanceSelectionDialogAsync(
            availablePaths,
            "选择 Base 路径",
            selectedInstance: defaultPath);

        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            Status = "已取消整合包安装";
            return false;
        }

        // 步骤 2：弹出实例名输入对话框
        var defaultInstanceName = GenerateCollectionDefaultInstanceName(request);
        var rawInstanceName = await _dialogService.ShowInstanceNameDialogAsync(
            "输入版本名称",
            defaultInstanceName,
            GetExistingInstanceNames(selectedPath));

        if (string.IsNullOrWhiteSpace(rawInstanceName))
        {
            Status = "已取消整合包安装";
            return false;
        }

        var instanceName = CreateSafeFileName(rawInstanceName);
        if (string.IsNullOrWhiteSpace(instanceName))
        {
            Status = "版本名称无效";
            return false;
        }

        // 检查版本目录是否已存在
        var versionRoot = Path.Combine(selectedPath, "versions", instanceName);
        if (Directory.Exists(versionRoot))
        {
            Status = $"版本名称已存在: {instanceName}";
            return false;
        }

        // 解析下载地址
        var resolved = await ResolveExternalDownloadTargetAsync(request);
        if (!resolved.IsSuccess)
        {
            Status = resolved.Message;
            if (!string.IsNullOrWhiteSpace(resolved.BrowserGuideUrl))
            {
                await _dialogService.ShowBrowserDownloadGuideDialogAsync(
                    resolved.BrowserGuideUrl,
                    "浏览器下载指引",
                    "该整合包资源需要在浏览器完成下载授权，请完成后重试。"
                );
            }
            return false;
        }

        var safeResolvedFileName = CreateSafeFileName(resolved.FileName);
        var outputPath = Path.Combine(_downloadRootPath, safeResolvedFileName);

        // 根据来源判断整合包类型
        var isSvlModpack = request.SourceToken?.Contains("SVL", StringComparison.OrdinalIgnoreCase) ?? false;
        var taskKind = isSvlModpack ? DownloadTaskKind.SvlModpack : DownloadTaskKind.CurseforgeModpack;

        var task = new DownloadTaskItem
        {
            Name = $"整合包安装 - {instanceName}",
            Status = "已加入队列（整合包安装）",
            Progress = 0,
            TaskKind = taskKind,
            TaskAction = DownloadTaskAction.InstallModpack,
            SourceUrl = resolved.DownloadUrl,
            OutputFilePath = outputPath,
            TargetGamePath = selectedPath,
            TargetInstanceName = instanceName,
            CanCancel = false,
            CanRetry = false
        };

        EnqueueExternalTask(task, $"已加入整合包安装队列: {instanceName}");
        return true;
    }

    private void EnqueueExternalTask(DownloadTaskItem task, string statusText)
    {
        DownloadTasks.Insert(0, task);
        DownloadTasks[0].StatusIconSource = ResolveTaskStatusIcon(DownloadTasks[0]);
        Status = statusText;
        SaveTaskState();
        _ = ProcessQueueAsync();
        NavigateToTaskStatusRequested?.Invoke();
    }

    /// <summary>
    /// 使用 Nexus Collection API 解析 Collection 下载地址。
    /// 与 ImportNxmLinkAsync 中 Collection 分支的解析逻辑一致。
    /// </summary>
    private async Task<ResolvedExternalDownloadTarget> ResolveCollectionDownloadAsync(
        NxmLinkInfo nxmInfo, AppUserSettings settings)
    {
        EmitLog($"正在通过 Nexus API 解析 Collection 下载地址: {nxmInfo.CollectionSlug} rev {nxmInfo.RevisionNumber}");
        var resolved = await _nexusModDownloadResolverService.ResolveCollectionDownloadUrlAsync(
            nxmInfo, settings.NexusApiKey, settings.NexusOAuthAccessToken);

        if (resolved.IsSuccess)
        {
            var fileName = ResolveDownloadFileName(new Uri(resolved.DownloadUrl), resolved.FileName);
            return ResolvedExternalDownloadTarget.Success(resolved.DownloadUrl, fileName);
        }

        return ResolvedExternalDownloadTarget.Fail(resolved.Message, string.Empty);
    }

    private async Task<ResolvedExternalDownloadTarget> ResolveExternalDownloadTargetAsync(ExternalDownloadRequest request)
    {
        var sourceToken = NormalizeSourceToken(request);
        var directUrl = TryResolveDirectDownloadUrl(request.SelectedDownloadOption);
        var fallbackGuideUrl = BuildFallbackGuideUrl(request);

        if (sourceToken == "nexusmods" &&
            TryExtractPositiveLong(request.ResourceId, out var modId) &&
            TryExtractFileIdFromOption(request.SelectedDownloadOption, out var fileId))
        {
            var settings = _settingsStore.Load();
            if (string.IsNullOrWhiteSpace(settings.NexusApiKey) && string.IsNullOrWhiteSpace(settings.NexusOAuthAccessToken))
            {
                return ResolvedExternalDownloadTarget.Fail("请先在设置页完成 Nexus 登录后再下载", fallbackGuideUrl);
            }

            var info = new NxmLinkInfo
            {
                ResourceType = NxmResourceType.ModFile,
                GameDomain = "stardewvalley",
                ModId = modId,
                FileId = fileId
            };

            var resolved = await _nexusModDownloadResolverService.ResolveDownloadUrlAsync(
                info,
                settings.NexusApiKey,
                settings.NexusOAuthAccessToken);
            if (resolved.IsSuccess &&
                Uri.TryCreate(resolved.DownloadUrl, UriKind.Absolute, out var resolvedUri) &&
                IsHttpUri(resolvedUri))
            {
                var fileName = ResolveDownloadFileName(resolvedUri, resolved.FileName);
                return ResolvedExternalDownloadTarget.Success(resolved.DownloadUrl, fileName);
            }

            if (!string.IsNullOrWhiteSpace(directUrl) &&
                Uri.TryCreate(directUrl, UriKind.Absolute, out var directUri) &&
                IsHttpUri(directUri))
            {
                var fallbackName = ResolveDownloadFileName(directUri, request.ResolveSuggestedFileName());
                return ResolvedExternalDownloadTarget.Success(directUrl, fallbackName);
            }

            return ResolvedExternalDownloadTarget.Fail(resolved.Message, fallbackGuideUrl);
        }

        if (sourceToken == "curseforge" &&
            TryExtractPositiveLong(request.ResourceId, out var curseforgeModId) &&
            TryExtractFileIdFromOption(request.SelectedDownloadOption, out var curseforgeFileId))
        {
            var resolvedUrl = await _remoteCatalogService.ResolveCurseforgeFileDownloadUrlAsync(
                curseforgeModId,
                curseforgeFileId,
                directUrl);
            if (!string.IsNullOrWhiteSpace(resolvedUrl) &&
                Uri.TryCreate(resolvedUrl, UriKind.Absolute, out var curseUri) &&
                IsHttpUri(curseUri))
            {
                var fileName = ResolveDownloadFileName(curseUri, request.ResolveSuggestedFileName());
                return ResolvedExternalDownloadTarget.Success(resolvedUrl, fileName);
            }
        }

        if (!string.IsNullOrWhiteSpace(directUrl) &&
            Uri.TryCreate(directUrl, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var fileName = ResolveDownloadFileName(uri, request.ResolveSuggestedFileName());
            return ResolvedExternalDownloadTarget.Success(directUrl, fileName);
        }

        return ResolvedExternalDownloadTarget.Fail("未解析到可用下载地址", fallbackGuideUrl);
    }

    private string ResolveCurrentGamePath()
    {
        var settings = _settingsStore.Load();
        if (!string.IsNullOrWhiteSpace(settings.PreferredInstancePath) && Directory.Exists(settings.PreferredInstancePath))
        {
            return settings.PreferredInstancePath;
        }

        if (!string.IsNullOrWhiteSpace(GamePathHint) && Directory.Exists(GamePathHint))
        {
            return GamePathHint;
        }

        return string.Empty;
    }

    /// <summary>
    /// 解析当前选中实例的 Mods 安装路径。
    /// 参考旧架构 GetCurrentSelectedInstance + GetCurrentModsPath：
    /// 1. 优先使用 PreferredInstancePath（用户在启动页选中的实例）
    /// 2. 直接使用实例注册表选出的运行时目录，兼容新布局 versions/{name} 和旧布局
    ///    versions/{name}/game（而不是根据 PreferredInstancePath 再次拼接路径）。
    /// </summary>
    public string? GetCurrentModsPath() => ResolveCurrentInstanceModsPath();

    private string? ResolveCurrentInstanceModsPath()
    {
        var runtimePath = ResolveCurrentInstanceRuntimePath();
        if (string.IsNullOrWhiteSpace(runtimePath) || !Directory.Exists(runtimePath))
        {
            return null;
        }

        var modsPath = Path.Combine(runtimePath, "Mods");
        if (Directory.Exists(modsPath))
        {
            return modsPath;
        }

        // 实例路径存在但 Mods 目录不存在，创建它
        Directory.CreateDirectory(modsPath);
        return modsPath;
    }

    private string? ResolveCurrentInstanceRuntimePath()
    {
        var instancePath = ResolveCurrentGamePath();
        if (string.IsNullOrWhiteSpace(instancePath) || !Directory.Exists(instancePath))
        {
            return null;
        }

        var settings = _settingsStore.Load();
        var instanceName = settings.InstanceName?.Trim();
        if (string.IsNullOrWhiteSpace(instanceName))
        {
            return instancePath;
        }

        // PreferredInstancePath 通常已经是 InstancesPage 选出的运行时目录；兼容新/旧两种布局。
        if (IsVersionRuntimePath(instancePath, instanceName))
        {
            return instancePath;
        }

        // 兼容旧设置：PreferredInstancePath 保存的是 Base 路径。
        var versionRoot = Path.Combine(instancePath, "versions", instanceName);
        return Directory.Exists(versionRoot)
            ? InstanceRuntimePathResolver.Resolve(versionRoot)
            : instancePath;
    }

    private static bool IsVersionRuntimePath(string path, string instanceName)
    {
        var current = new DirectoryInfo(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(current.Name, instanceName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.Parent?.Name, "versions", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(current.Name, "game", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(current.Parent?.Name, instanceName, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(current.Parent?.Parent?.Name, "versions", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 检查当前选中实例是否为原版（无 SMAPI）。
    /// 原版实例无法使用 Mod，安装 Mod 前应提示用户选择 SMAPI 实例。
    /// </summary>
    private bool IsCurrentInstanceVanilla()
    {
        var runtimePath = ResolveCurrentInstanceRuntimePath();
        if (string.IsNullOrWhiteSpace(runtimePath) || !Directory.Exists(runtimePath))
        {
            return true;
        }

        return !File.Exists(Path.Combine(runtimePath, "StardewModdingAPI.exe")) &&
               !File.Exists(Path.Combine(runtimePath, "StardewModdingAPI")) &&
               !File.Exists(Path.Combine(runtimePath, "StardewModdingAPI.dll"));
    }

    private static bool IsSmapiExternalRequest(ExternalDownloadRequest request)
    {
        if (request.IsSmapiResource)
        {
            return true;
        }

        var combined = string.Join('|',
            request.ResourceName ?? string.Empty,
            request.ResourceSource ?? string.Empty,
            request.SourceToken ?? string.Empty,
            request.SourcePageUrl ?? string.Empty,
            request.ResourceId ?? string.Empty,
            request.SelectedDownloadOption ?? string.Empty);

        if (combined.Contains("smapi", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals((request.ResourceId ?? string.Empty).Trim(), "2400", StringComparison.OrdinalIgnoreCase) ||
               string.Equals((request.ResourceId ?? string.Empty).Trim(), "898372", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>判断已下载任务是否实为 SMAPI（文件名/下载 URL 含 smapi 或 2400/898372）。</summary>
    private static bool LooksLikeSmapiTask(DownloadTaskItem task)
    {
        var text = string.Join('|', task.Name ?? string.Empty, task.SourceUrl ?? string.Empty);
        return text.Contains("smapi", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("mods/2400", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("/2400/", StringComparison.Ordinal) ||
               text.Contains("898372", StringComparison.Ordinal);
    }

    /// <summary>解析 SMAPI 安装的 Base 路径与实例名：优先复用设置里已选的 Base（不再次弹 Base 框），
    /// 但实例名始终弹"输入SMAPI实例名称"对话框（默认取 SMAPI 版本）；仅当没有可用 Base 时才连 Base 一起弹。</summary>
    private async Task<(string? BasePath, string? InstanceName)> AskSmapiBasePathAndInstanceName(DownloadTaskItem task)
    {
        var settings = _settingsStore.Load();
        var preferredBase = settings.PreferredInstancePath;
        var defaultName = BuildSmapiInstanceNameFromTask(task);

        // 复用已选 Base：不再次弹 Base 选择框，但仍弹"输入SMAPI实例名称"
        if (!string.IsNullOrWhiteSpace(preferredBase) && Directory.Exists(preferredBase))
        {
            var rawName = await _dialogService.ShowInstanceNameDialogAsync(
                "输入SMAPI实例名称",
                defaultName,
                GetExistingInstanceNames(preferredBase));
            if (string.IsNullOrWhiteSpace(rawName))
            {
                return (null, null);
            }
            var instanceName = CreateSafeFileName(rawName);
            return (string.IsNullOrWhiteSpace(instanceName) ? null : preferredBase,
                    string.IsNullOrWhiteSpace(instanceName) ? null : instanceName);
        }

        // 无可用 Base：连 Base 一起弹（SMAPI 基础路径 + 输入SMAPI实例名称）
        var availablePaths = AvailableGamePathsProvider?.Invoke();
        if (availablePaths == null || availablePaths.Count == 0)
        {
            var current = ResolveCurrentGamePath();
            if (string.IsNullOrWhiteSpace(current))
            {
                Status = "SMAPI 安装失败：未检测到可用 Base 路径，请先在实例页面添加游戏路径";
                return (null, null);
            }
            availablePaths = new List<string> { current };
        }

        var defaultPath = ResolveCurrentGamePath();
        if (string.IsNullOrWhiteSpace(defaultPath) && availablePaths.Count > 0)
        {
            defaultPath = availablePaths[0];
        }

        var basePath = await _dialogService.ShowInstanceSelectionDialogAsync(
            availablePaths,
            "选择 SMAPI 基础路径",
            selectedInstance: defaultPath);
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return (null, null);
        }

        var rawInstanceName = await _dialogService.ShowInstanceNameDialogAsync(
            "输入SMAPI实例名称",
            defaultName,
            GetExistingInstanceNames(basePath));
        if (string.IsNullOrWhiteSpace(rawInstanceName))
        {
            return (null, null);
        }

        var name = CreateSafeFileName(rawInstanceName);
        return (string.IsNullOrWhiteSpace(name) ? null : basePath,
                string.IsNullOrWhiteSpace(name) ? null : name);
    }

    /// <summary>从任务名（如 "SMAPI 4.5.2-2400-..."）提取 SMAPI 版本，生成默认实例名。</summary>
    private static string BuildSmapiInstanceNameFromTask(DownloadTaskItem task)
    {
        var text = string.Join(' ', task.Name ?? string.Empty, task.SourceUrl ?? string.Empty);
        var match = Regex.Match(text, "(?<version>\\d+\\.\\d+(?:\\.\\d+)*)", RegexOptions.CultureInvariant);
        return match.Success ? $"SMAPI {match.Groups["version"].Value}" : "SMAPI";
    }

    private static string NormalizeSourceToken(ExternalDownloadRequest request)
    {
        var raw = string.Join('|',
            request.SourceToken ?? string.Empty,
            request.ResourceSource ?? string.Empty)
            .ToLowerInvariant();

        if (raw.Contains("nexus"))
        {
            return "nexusmods";
        }

        if (raw.Contains("curse"))
        {
            return "curseforge";
        }

        if (raw.Contains("github"))
        {
            return "github";
        }

        return string.Empty;
    }

    private static string TryResolveDirectDownloadUrl(string? option)
    {
        if (string.IsNullOrWhiteSpace(option))
        {
            return string.Empty;
        }

        var trimmed = option.Trim();

        // 剥离 ~~ 后缀元数据（channel=...;gamever=...;displayname=... 等），
        // 这些元数据附加在 URL 末尾会导致 403（服务器无法识别带元数据的路径）。
        var tildeIndex = trimmed.IndexOf("~~", StringComparison.Ordinal);
        if (tildeIndex >= 0)
        {
            trimmed = trimmed[..tildeIndex].Trim();
        }

        var markerIndex = trimmed.IndexOf("http://", StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            markerIndex = trimmed.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
        }

        if (markerIndex >= 0)
        {
            var candidate = trimmed[markerIndex..].Trim();
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var byMarker) &&
                (byMarker.Scheme == Uri.UriSchemeHttp || byMarker.Scheme == Uri.UriSchemeHttps))
            {
                return byMarker.ToString();
            }
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) &&
            (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            return parsed.ToString();
        }

        return string.Empty;
    }

    private static bool TryExtractFileIdFromOption(string? option, out long fileId)
    {
        fileId = 0;
        if (string.IsNullOrWhiteSpace(option))
        {
            return false;
        }

        // URL 形式先匹配 /files/{id}，避免把 /mods/{modId} 误当成 File ID。
        var match = Regex.Match(
            option,
            @"(?:[/\\]files[/\\]|\bfile(?:\s+id)?\s*[:#]?\s*)(?<id>\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        return long.TryParse(match.Groups["id"].Value, out fileId) && fileId > 0;
    }

    private static bool TryExtractPositiveLong(string? raw, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (long.TryParse(raw.Trim(), out var parsed) && parsed > 0)
        {
            value = parsed;
            return true;
        }

        var match = Regex.Match(raw, "(?<id>\\d+)", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        return long.TryParse(match.Groups["id"].Value, out value) && value > 0;
    }

    private static string BuildFallbackGuideUrl(ExternalDownloadRequest request)
    {
        // Nexus 优先构造带 file_id + nmm=1 的文件下载页（定位到具体文件并触发 NXM 回调），
        // 而不是回退到无参的 SourcePageUrl。
        var sourceToken = NormalizeSourceToken(request);
        if (sourceToken == "nexusmods")
        {
            if (TryExtractPositiveLong(request.ResourceId, out var nexusModId))
            {
                // 如果有 fileId，使用 nmm=1 格式直接触发 NXM 协议回调
                if (TryExtractFileIdFromOption(request.SelectedDownloadOption, out var nexusFileId))
                {
                    return $"https://www.nexusmods.com/stardewvalley/mods/{nexusModId}?tab=files&file_id={nexusFileId}&nmm=1";
                }
                return $"https://www.nexusmods.com/stardewvalley/mods/{nexusModId}";
            }

            return "https://www.nexusmods.com/stardewvalley/mods";
        }

        if (sourceToken == "curseforge")
        {
            if (TryExtractPositiveLong(request.ResourceId, out var curseId))
            {
                return $"https://www.curseforge.com/projects/{curseId}";
            }

            return "https://www.curseforge.com/stardewvalley/mods";
        }

        // 其余来源回退到 SourcePageUrl，再回退到 SMAPI 发布页
        if (!string.IsNullOrWhiteSpace(request.SourcePageUrl))
        {
            return request.SourcePageUrl;
        }

        return "https://github.com/Pathoschild/SMAPI/releases";
    }

    private static string BuildSmapiDefaultInstanceName(ExternalDownloadRequest request)
    {
        var text = string.Join(' ', request.ResourceName, request.SelectedDownloadOption);
        var match = Regex.Match(text, "(?<version>\\d+\\.\\d+(?:\\.\\d+)*)", RegexOptions.CultureInvariant);
        if (match.Success)
        {
            return $"SMAPI {match.Groups["version"].Value}";
        }

        return "SMAPI";
    }

    private static IReadOnlyList<global::Avalonia.Platform.Storage.FilePickerFileType> BuildSaveFileTypes(string fileName)
    {
        var ext = Path.GetExtension(fileName)?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(ext))
        {
            return [new global::Avalonia.Platform.Storage.FilePickerFileType("所有文件") { Patterns = ["*.*"] }];
        }

        var normalized = ext.StartsWith('.') ? ext : "." + ext;
        var label = normalized.ToLowerInvariant() switch
        {
            ".zip" => "ZIP 压缩包",
            ".7z" => "7z 压缩包",
            ".rar" => "RAR 压缩包",
            _ => $"{normalized.ToUpperInvariant()} 文件"
        };

        return
        [
            new global::Avalonia.Platform.Storage.FilePickerFileType(label)
            {
                Patterns = [$"*{normalized}"]
            },
            new global::Avalonia.Platform.Storage.FilePickerFileType("所有文件")
            {
                Patterns = ["*.*"]
            }
        ];
    }

    private sealed class ResolvedExternalDownloadTarget
    {
        public bool IsSuccess { get; init; }

        public string DownloadUrl { get; init; } = string.Empty;

        public string FileName { get; init; } = string.Empty;

        public string Message { get; init; } = string.Empty;

        public string BrowserGuideUrl { get; init; } = string.Empty;

        public static ResolvedExternalDownloadTarget Success(string downloadUrl, string fileName)
        {
            return new ResolvedExternalDownloadTarget
            {
                IsSuccess = true,
                DownloadUrl = downloadUrl,
                FileName = fileName,
                Message = "下载地址解析成功"
            };
        }

        public static ResolvedExternalDownloadTarget Fail(string message, string browserGuideUrl)
        {
            return new ResolvedExternalDownloadTarget
            {
                IsSuccess = false,
                Message = string.IsNullOrWhiteSpace(message) ? "未解析到可用下载地址" : message,
                BrowserGuideUrl = browserGuideUrl
            };
        }
    }

    private async Task LoadCategoryItemsForCurrentCategoryAsync(bool initialLoad)
    {
        if (SelectedCategory == DownloadCategory.Game)
        {
            return; // 游戏本体分类不加载目录列表（SteamCMD 面板独立展示）
        }

        var loadToken = Interlocked.Increment(ref _catalogLoadToken);
        var isHotModsLoad = false;
        var isAutoHotCollectionsLoad = false;

        // Mods: serve from the in-memory cache first so repeated loads (paging back, returning to
        // the Mods category) skip the network round-trip and avoid flickering the loading card.
        if (SelectedCategory == DownloadCategory.Mods)
        {
            isHotModsLoad = _forceHotModsLoad || initialLoad || string.IsNullOrWhiteSpace(ModSearchText);
            var modQueryHint = isHotModsLoad ? string.Empty : ModSearchText.Trim();
            var cacheKey = BuildModCacheKey(modQueryHint, isHotModsLoad, CurrentModPage);
            if (TryGetCachedModResults(cacheKey, out var cachedResults, out var cachedHasMore))
            {
                _forceHotModsLoad = false;
                _modHasMore = cachedHasMore;
                TotalModPages = cachedHasMore ? CurrentModPage + 1 : CurrentModPage;
                ReplaceStringCollection(SearchResults, cachedResults);
                SyncModCategoryItems(cachedResults);
                Status = cachedResults.Count == 0
                    ? (isHotModsLoad ? "未获取到热门 Mod，请调整来源后重试" : "未找到匹配 Mod")
                    : (isHotModsLoad ? $"已加载 {cachedResults.Count} 条热门 Mod" : $"已筛选得到 {cachedResults.Count} 条 Mod");
                EmitLog($"[Catalog] Served mods from cache key='{cacheKey}', count={cachedResults.Count}");
                // The in-memory result cache short-circuits the network path, which also
                // skips icon resolution. Trigger it here so cached mod cards still resolve
                // their remote icons to the on-disk cache (cache hits are instant; misses
                // download asynchronously and update IconSource once ready).
                _ = ResolveCategoryCardIconsAsync(loadToken);
                return;
            }
        }

        IsCatalogLoading = true;
        IsSearchingMods = SelectedCategory == DownloadCategory.Mods || SelectedCategory == DownloadCategory.Modpacks;
        OnPropertyChanged(nameof(HasNoCategoryItems));
        OnPropertyChanged(nameof(HasCategoryItems));
        // Yield to the UI thread so the loading spinner renders before the heavy synchronous
        // work (clearing/populating collections) runs. This keeps page/category switching responsive.
        await Task.Yield();

        // If a newer load superseded this one while we were yielding, bail out early.
        if (loadToken != Volatile.Read(ref _catalogLoadToken))
        {
            return;
        }

        // Clear previous category list now that the loading card is visible.
        CategoryItems.Clear();
        ClearSmapiSourceItems();
        EmitLog($"[Catalog] Begin load token={loadToken}, category={SelectedCategory}, initialLoad={initialLoad}");
        OnPropertyChanged(nameof(HasNoCategoryItems));
        OnPropertyChanged(nameof(HasCategoryItems));

        try
        {
            List<string> results;
            string queryHint;

            switch (SelectedCategory)
            {
                case DownloadCategory.Smapi:
                    queryHint = string.IsNullOrWhiteSpace(SmapiSearchText) ? "SMAPI" : $"SMAPI {SmapiSearchText.Trim()}";
                    EmitLog($"[Catalog] SMAPI query='{queryHint}', source='{SelectedSmapiSource}'");
                    results = (await _remoteCatalogService.SearchSmapiAsync(queryHint, SelectedSmapiSource))
                        .Select(ToCatalogDisplayText).ToList();
                    break;

                case DownloadCategory.Mods:
                    isHotModsLoad = _forceHotModsLoad || initialLoad || string.IsNullOrWhiteSpace(ModSearchText);
                    _forceHotModsLoad = false;
                    queryHint = isHotModsLoad ? string.Empty : ModSearchText.Trim();
                    var modSource = SelectedModSource;
                    EmitLog($"[Catalog] MOD query='{queryHint}', hotOnly={isHotModsLoad}, source='{modSource}', version='{SelectedModGameVersion}', type='{SelectedModType}', mode='{SelectedModDescriptionMode}'");
                    var paged = await _remoteCatalogService.SearchModsAdvancedPagedAsync(
                        queryHint,
                        modSource,
                        SelectedModGameVersion,
                        SelectedModType,
                        UseLocalizedModDescription,
                        isHotModsLoad,
                        CurrentModPage,
                        ModPageSize);
                    _modHasMore = paged.HasMore;
                    results = paged.Items.Select(ToCatalogDisplayText).ToList();

                    TotalModPages = _modHasMore ? CurrentModPage + 1 : CurrentModPage;
                    SetCachedModResults(BuildModCacheKey(queryHint, isHotModsLoad, CurrentModPage), results, _modHasMore);
                    System.Diagnostics.Debug.WriteLine($"[Download] Mods loaded: page={CurrentModPage}, items={results.Count}, hasMore={_modHasMore}, totalModPages={TotalModPages}, canGoNext={CurrentModPage < TotalModPages}");
                    break;

                case DownloadCategory.Modpacks:
                    isAutoHotCollectionsLoad = initialLoad && string.IsNullOrWhiteSpace(ModpackSearchText);
                    queryHint = string.IsNullOrWhiteSpace(ModpackSearchText) ? string.Empty : ModpackSearchText.Trim();
                    var modpackSource = SelectedModpackSource;
                    EmitLog($"[Catalog] Modpack query='{queryHint}', source='{modpackSource}', autoHot={isAutoHotCollectionsLoad}");
                    var modpackPaged = await _remoteCatalogService.SearchModpacksPagedAsync(queryHint, modpackSource, CurrentModpackPage, ModpackPageSize);
                    _modpackHasMore = modpackPaged.HasMore;
                    results = modpackPaged.Items.Select(ToCatalogDisplayText).ToList();
                    TotalModpackPages = _modpackHasMore ? CurrentModpackPage + 1 : CurrentModpackPage;
                    break;

                default:
                    results = [];
                    break;
            }

            if (loadToken != Volatile.Read(ref _catalogLoadToken))
            {
                EmitLog($"[Catalog] Skip outdated token={loadToken}");
                return;
            }

            EmitLog($"[Catalog] Loaded raw results={results.Count}, category={SelectedCategory}");

            ReplaceStringCollection(SearchResults, results);
            if (SelectedCategory == DownloadCategory.Smapi)
            {
                CategoryItems.Clear();
                SyncSmapiSourceItems(results);
                _ = ResolveSmapiCardIconsAsync(loadToken);
            }
            else
            {
                ClearSmapiSourceItems();
                if (SelectedCategory == DownloadCategory.Mods)
                {
                    SyncModCategoryItems(results);
                }
                else if (SelectedCategory == DownloadCategory.Modpacks)
                {
                    SyncModpackCategoryItems(results);
                }
                else
                {
                    SyncCategoryItems(results);
                }
                _ = ResolveCategoryCardIconsAsync(loadToken);
            }

            if (SelectedCategory == DownloadCategory.Smapi)
            {
                var cardCount = SmapiGithubItems.Count + SmapiNexusModsItems.Count + SmapiCurseforgeItems.Count;
                Status = cardCount > 0
                    ? $"已准备 {cardCount} 个来源卡片"
                    : "SMAPI 来源卡片加载失败";
            }
            else if (SelectedCategory == DownloadCategory.Mods)
            {
                if (results.Count == 0)
                {
                    Status = isHotModsLoad ? "未获取到热门 Mod，请调整来源后重试" : "未找到匹配 Mod";
                    EmitLog("[Catalog] Mod list is empty after search/filter.");
                }
                else
                {
                    Status = isHotModsLoad
                        ? $"已加载 {results.Count} 条热门 Mod"
                        : $"已筛选得到 {results.Count} 条 Mod";
                    EmitLog($"[Catalog] Mod cards ready count={results.Count}");
                }
            }
            else if (SelectedCategory == DownloadCategory.Modpacks)
            {
                if (results.Count == 0)
                {
                    Status = isAutoHotCollectionsLoad
                        ? "未获取到 Nexus 热门 Collection，请稍后重试"
                        : (initialLoad ? "已加载，暂无可展示资源" : "未找到匹配资源");
                }
                else
                {
                    Status = isAutoHotCollectionsLoad
                        ? $"已加载 {results.Count} 条 Nexus 热门 Collection"
                        : $"第 {CurrentModpackPage}/{TotalModpackPages} 页，共 {results.Count} 条资源";
                }
            }
            else if (results.Count == 0)
            {
                Status = initialLoad ? "已加载，暂无可展示资源" : "未找到匹配资源";
            }
            else
            {
                Status = SelectedCategory == DownloadCategory.Modpacks
                    ? $"第 {CurrentModpackPage}/{TotalModpackPages} 页，共 {results.Count} 条资源"
                    : (initialLoad ? $"已加载 {results.Count} 条资源" : $"筛选得到 {results.Count} 条资源");
            }
        }
        catch (Exception ex)
        {
            if (loadToken != Volatile.Read(ref _catalogLoadToken))
            {
                return;
            }

            var message = ex.Message;
            if (message.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("TLS", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("handshake", StringComparison.OrdinalIgnoreCase))
            {
                Status = "资源加载失败：SSL/TLS 连接异常。请到“设置 -> 下载设置”启用代理并填写代理地址后重试。";
            }
            else
            {
                Status = $"资源加载失败: {message}";
            }
            EmitLog($"[Catalog] Load failed token={loadToken}, error={ex.Message}");
            CategoryItems.Clear();
            SearchResults.Clear();
            ClearSmapiSourceItems();
        }
        finally
        {
            if (loadToken == Volatile.Read(ref _catalogLoadToken))
            {
                IsCatalogLoading = false;
                IsSearchingMods = false;
                OnPropertyChanged(nameof(HasNoCategoryItems));
                OnPropertyChanged(nameof(HasCategoryItems));
                EmitLog($"[Catalog] End load token={loadToken}, hasItems={CategoryItems.Count > 0 || SmapiGithubItems.Count + SmapiNexusModsItems.Count + SmapiCurseforgeItems.Count > 0}");
            }
        }
    }

    private void SyncCategoryItems(IEnumerable<string> results)
    {
        CategoryItems.Clear();
        foreach (var result in results)
        {
            var item = ParseCatalogItem(result);
            if (SelectedCategory == DownloadCategory.Mods)
            {
                ApplyModLocalizationPreferenceToItem(item);
            }

            CategoryItems.Add(item);
        }

        ApplyModLocalizationPreferenceToCategoryItems();

        OnPropertyChanged(nameof(HasNoCategoryItems));
        OnPropertyChanged(nameof(HasCategoryItems));
    }

    private void SyncModCategoryItems(IEnumerable<string> results)
    {
        CategoryItems.Clear();
        foreach (var result in results)
        {
            var item = ParseCatalogItem(result);
            ApplyModLocalizationPreferenceToItem(item);
            CategoryItems.Add(item);
        }

        ApplyModLocalizationPreferenceToCategoryItems();
        OnPropertyChanged(nameof(HasNoCategoryItems));
        OnPropertyChanged(nameof(HasCategoryItems));
        OnPropertyChanged(nameof(ModPageInfoText));
        OnPropertyChanged(nameof(IsModsPageable));
    }

    private void SyncModpackCategoryItems(IEnumerable<string> results)
    {
        CategoryItems.Clear();
        foreach (var result in results)
        {
            CategoryItems.Add(ParseCatalogItem(result));
        }

        OnPropertyChanged(nameof(HasNoCategoryItems));
        OnPropertyChanged(nameof(HasCategoryItems));
        OnPropertyChanged(nameof(ModpackPageInfoText));
        OnPropertyChanged(nameof(IsModpacksPageable));
    }

    private void ApplyCurrentModPageItems()
    {
        CategoryItems.Clear();
        if (_modAllResults.Count == 0)
        {
            return;
        }

        var skip = (CurrentModPage - 1) * ModPageSize;
        var pageItems = _modAllResults.Skip(skip).Take(ModPageSize);
        foreach (var result in pageItems)
        {
            var item = ParseCatalogItem(result);
            ApplyModLocalizationPreferenceToItem(item);
            CategoryItems.Add(item);
        }

        OnPropertyChanged(nameof(HasNoCategoryItems));
        OnPropertyChanged(nameof(HasCategoryItems));
        OnPropertyChanged(nameof(ModPageInfoText));
    }

    private void ApplyCurrentModpackPageItems()
    {
        CategoryItems.Clear();
        if (_modpackAllResults.Count == 0)
        {
            return;
        }

        var skip = (CurrentModpackPage - 1) * ModpackPageSize;
        var pageItems = _modpackAllResults.Skip(skip).Take(ModpackPageSize);
        foreach (var result in pageItems)
        {
            CategoryItems.Add(ParseCatalogItem(result));
        }

        OnPropertyChanged(nameof(HasNoCategoryItems));
        OnPropertyChanged(nameof(HasCategoryItems));
        OnPropertyChanged(nameof(ModpackPageInfoText));
    }

    private string BuildModCacheKey(string queryHint, bool isHotModsLoad, int page)
    {
        return string.Join("|", [
            "mods",
            SelectedModSource,
            SelectedModGameVersion,
            SelectedModType,
            SelectedModDescriptionMode,
            isHotModsLoad ? "hot" : "search",
            queryHint,
            $"p{page}"
        ]);
    }

    private bool TryGetCachedModResults(string key, out List<string> results, out bool hasMore)
    {
        results = [];
        hasMore = false;
        if (!_modResultsCache.TryGetValue(key, out var entry))
        {
            return false;
        }

        if (DateTime.Now - entry.CreatedAt > ModSearchCacheTtl)
        {
            _modResultsCache.Remove(key);
            return false;
        }

        results = [..entry.Results];
        hasMore = entry.HasMore;
        return true;
    }

    private void SetCachedModResults(string key, IEnumerable<string> results, bool hasMore)
    {
        _modResultsCache[key] = (DateTime.Now, [..results], hasMore);
    }

    private void ClearSearchCache() => _modResultsCache.Clear();

    private void ApplyModLocalizationPreferenceToCategoryItems()
    {
        if (SelectedCategory != DownloadCategory.Mods)
        {
            return;
        }

        foreach (var item in CategoryItems)
        {
            ApplyModLocalizationPreferenceToItem(item);
        }
    }

    private void ApplyModLocalizationPreferenceToItem(DownloadCatalogItem item)
    {
        if (item == null)
        {
            return;
        }

        var useLocalized = UseLocalizedModDescription;
        item.UseLocalizedText = useLocalized;
        item.UseLocalizedName = useLocalized;
        item.UseLocalizedSummary = useLocalized;
    }

    private void SyncSmapiSourceItems(IEnumerable<string> results)
    {
        ClearSmapiSourceItems();

        var parsedItems = results
            .Select(ParseCatalogItem)
            .Where(item => !string.IsNullOrWhiteSpace(item.SourceKey))
            .ToList();

        var sourceKeys = GetRequestedSmapiSourceKeys();
        foreach (var sourceKey in sourceKeys)
        {
            var bestItem = SelectBestSmapiSourceItem(parsedItems, sourceKey) ?? BuildSmapiPlaceholderItem(sourceKey);
            bestItem = NormalizeSmapiCardPresentation(bestItem, sourceKey);
            switch (sourceKey)
            {
                case "github":
                    SmapiGithubItems.Add(bestItem);
                    break;
                case "nexusmods":
                    SmapiNexusModsItems.Add(bestItem);
                    break;
                case "curseforge":
                    SmapiCurseforgeItems.Add(bestItem);
                    break;
                default:
                    break;
            }
        }

        RaiseSmapiSourceState();
    }

    private List<string> GetRequestedSmapiSourceKeys()
    {
        return SelectedSmapiSource switch
        {
            "GitHub" => ["github"],
            "NexusMods" => ["nexusmods"],
            "Curseforge" => ["curseforge"],
            _ => ["github", "nexusmods", "curseforge"]
        };
    }

    private static DownloadCatalogItem? SelectBestSmapiSourceItem(IEnumerable<DownloadCatalogItem> items, string sourceKey)
    {
        return items
            .Where(item => string.Equals(item.SourceKey, sourceKey, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => ComputeSmapiItemScore(item))
            .FirstOrDefault();
    }

    private static int ComputeSmapiItemScore(DownloadCatalogItem item)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(item.Name) &&
            item.Name.Contains("smapi", StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }

        if (!string.IsNullOrWhiteSpace(item.Summary) &&
            item.Summary.Contains("smapi", StringComparison.OrdinalIgnoreCase))
        {
            score += 6;
        }

        if (!string.IsNullOrWhiteSpace(item.Stat))
        {
            score += 2;
        }

        return score;
    }

    private static DownloadCatalogItem BuildSmapiPlaceholderItem(string sourceKey)
    {
        var sourceLabel = ResolveSourceLabel(sourceKey, sourceKey);
        var sourceId = sourceKey switch
        {
            "nexusmods" => "2400",
            "curseforge" => "898372",
            _ => "0"
        };

        var displayText = $"[{sourceLabel}#{sourceId}] {SmapiDefaultName} | metric= | time= | icon=avares://SVL.Avalonia/Assets/Icons/Modded.png | {SmapiDefaultSummary}";
        return new DownloadCatalogItem
        {
            DisplayText = displayText,
            Name = SmapiDefaultName,
            SourceTag = sourceLabel,
            SourceKey = sourceKey,
            Stat = string.Empty,
            MetricTag = string.Empty,
            TimeTag = string.Empty,
            Summary = SmapiDefaultSummary,
            IconSource = "avares://SVL.Avalonia/Assets/Icons/Modded.png"
        };
    }

    private static DownloadCatalogItem NormalizeSmapiCardPresentation(DownloadCatalogItem item, string sourceKey)
    {
        item.SourceKey = sourceKey;
        item.SourceTag = ResolveSourceLabel(sourceKey, item.SourceTag);
        item.Name = SmapiDefaultName;
        item.Summary = SmapiDefaultSummary;
        if (string.IsNullOrWhiteSpace(item.MetricTag) && !string.IsNullOrWhiteSpace(item.Stat))
        {
            item.MetricTag = item.Stat;
        }

        if (string.IsNullOrWhiteSpace(item.IconSource))
        {
            item.IconSource = ResolveSmapiIconSource(sourceKey);
        }

        return item;
    }

    private HttpClient GetIconHttpClient()
    {
        var settings = _settingsStore.Load();
        var signature = BuildIconProxySignature(settings);

        lock (IconHttpClientLock)
        {
            if (_smapiIconHttpClient != null && string.Equals(signature, _smapiIconProxySignature, StringComparison.Ordinal))
            {
                return _smapiIconHttpClient;
            }

            _smapiIconHttpClient?.Dispose();
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            if (settings.EnableDownloadProxy &&
                TryResolveIconProxyUri(settings.DownloadProxyUrl, out var proxyUri))
            {
                var proxy = new WebProxy(proxyUri);
                if (!string.IsNullOrWhiteSpace(settings.DownloadProxyUserName))
                {
                    proxy.Credentials = new NetworkCredential(
                        settings.DownloadProxyUserName.Trim(),
                        settings.DownloadProxyPassword ?? string.Empty);
                }

                handler.UseProxy = true;
                handler.Proxy = proxy;
            }

            _smapiIconHttpClient = new HttpClient(handler, disposeHandler: true);
            _smapiIconHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SVL-Avalonia-IconFetcher");
            _smapiIconProxySignature = signature;
            return _smapiIconHttpClient;
        }
    }

    private static string BuildIconProxySignature(AppUserSettings settings)
    {
        if (!settings.EnableDownloadProxy)
        {
            return "disabled";
        }

        return string.Join('|',
            "enabled",
            settings.DownloadProxyUrl?.Trim() ?? string.Empty,
            settings.DownloadProxyUserName?.Trim() ?? string.Empty,
            string.IsNullOrWhiteSpace(settings.DownloadProxyUserName)
                ? "anonymous"
                : (string.IsNullOrEmpty(settings.DownloadProxyPassword) ? "user-np" : "user-p"));
    }

    private static bool TryResolveIconProxyUri(string? rawProxyUrl, out Uri proxyUri)
    {
        proxyUri = default!;
        if (string.IsNullOrWhiteSpace(rawProxyUrl))
        {
            return false;
        }

        var trimmed = rawProxyUrl.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var parsedProxyUri) && parsedProxyUri != null)
        {
            proxyUri = parsedProxyUri;
            return true;
        }

        if (!trimmed.Contains("://", StringComparison.Ordinal) &&
            Uri.TryCreate($"http://{trimmed}", UriKind.Absolute, out parsedProxyUri) &&
            parsedProxyUri != null)
        {
            proxyUri = parsedProxyUri;
            return true;
        }

        return false;
    }

    private static string ResolveSmapiIconSource(string sourceKey)
    {
        return sourceKey switch
        {
            "github" => "avares://SVL.Avalonia/Assets/Icons/Modded.png",
            "nexusmods" => "avares://SVL.Avalonia/Assets/Icons/Junimo.png",
            "curseforge" => "avares://SVL.Avalonia/Assets/Icons/Junimo.png",
            _ => "avares://SVL.Avalonia/Assets/Icons/Modded.png"
        };
    }

    private async Task ResolveSmapiCardIconsAsync(int loadToken)
    {
        var items = SmapiGithubItems
            .Concat(SmapiNexusModsItems)
            .Concat(SmapiCurseforgeItems)
            .ToList();

        foreach (var item in items)
        {
            await ResolveSmapiCardIconAsync(item, loadToken);
        }
    }

    private async Task ResolveSmapiCardIconAsync(DownloadCatalogItem item, int loadToken)
    {
        if (item == null || loadToken != Volatile.Read(ref _catalogLoadToken))
        {
            return;
        }

        var iconSource = item.IconSource?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(iconSource, UriKind.Absolute, out var iconUri) ||
            (iconUri.Scheme != Uri.UriSchemeHttp && iconUri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        var remoteUrl = iconUri.ToString();
        var fallback = ResolveSmapiIconSource(item.SourceKey);
        item.IconSource = fallback;

        if (_smapiIconDiskCache.TryGetValue(remoteUrl, out var cachedPath) && File.Exists(cachedPath))
        {
            if (loadToken == Volatile.Read(ref _catalogLoadToken))
            {
                item.IconSource = cachedPath;
            }

            return;
        }

        var iconPath = BuildSmapiIconCachePath(remoteUrl);
        if (File.Exists(iconPath))
        {
            _smapiIconDiskCache[remoteUrl] = iconPath;
            if (loadToken == Volatile.Read(ref _catalogLoadToken))
            {
                item.IconSource = iconPath;
            }

            return;
        }

        try
        {
            using var response = await GetIconHttpClient().GetAsync(iconUri, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0)
            {
                return;
            }

            await File.WriteAllBytesAsync(iconPath, bytes);
            _smapiIconDiskCache[remoteUrl] = iconPath;
            if (loadToken == Volatile.Read(ref _catalogLoadToken))
            {
                item.IconSource = iconPath;
            }
        }
        catch
        {
            // Keep fallback icon when remote icon download fails.
        }
    }

    private async Task ResolveCategoryCardIconsAsync(int loadToken)
    {
        var items = CategoryItems.ToList();
        foreach (var item in items)
        {
            await ResolveRemoteIconToLocalAsync(item, loadToken, ResolveCategoryFallbackIcon(item.SourceKey));
        }
    }

    private static string ResolveCategoryFallbackIcon(string sourceKey)
    {
        return sourceKey switch
        {
            "curseforge" => "avares://SVL.Avalonia/Assets/Icons/Junimo.png",
            "nexusmods" => "avares://SVL.Avalonia/Assets/Icons/Junimo.png",
            _ => "avares://SVL.Avalonia/Assets/Icons/Modded.png"
        };
    }

    private async Task ResolveRemoteIconToLocalAsync(DownloadCatalogItem item, int loadToken, string fallback)
    {
        if (item == null || loadToken != Volatile.Read(ref _catalogLoadToken))
        {
            return;
        }

        var iconSource = item.IconSource?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(iconSource, UriKind.Absolute, out var iconUri) ||
            (iconUri.Scheme != Uri.UriSchemeHttp && iconUri.Scheme != Uri.UriSchemeHttps))
        {
            if (string.IsNullOrWhiteSpace(item.IconSource))
            {
                item.IconSource = fallback;
            }

            return;
        }

        var remoteUrl = iconUri.ToString();
        item.IconSource = fallback;

        if (_smapiIconDiskCache.TryGetValue(remoteUrl, out var cachedPath) && File.Exists(cachedPath))
        {
            if (loadToken == Volatile.Read(ref _catalogLoadToken))
            {
                item.IconSource = cachedPath;
            }

            return;
        }

        var iconPath = BuildSmapiIconCachePath(remoteUrl);
        if (File.Exists(iconPath))
        {
            _smapiIconDiskCache[remoteUrl] = iconPath;
            if (loadToken == Volatile.Read(ref _catalogLoadToken))
            {
                item.IconSource = iconPath;
            }

            return;
        }

        try
        {
            using var response = await GetIconHttpClient().GetAsync(iconUri, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0)
            {
                return;
            }

            await File.WriteAllBytesAsync(iconPath, bytes);
            _smapiIconDiskCache[remoteUrl] = iconPath;
            if (loadToken == Volatile.Read(ref _catalogLoadToken))
            {
                item.IconSource = iconPath;
            }
        }
        catch
        {
            // Keep fallback icon when remote icon download fails.
        }
    }

    // Delegates to the shared cache-path helper so the converter and the ViewModel always
    // agree on the on-disk location of a given remote icon.
    private static string BuildSmapiIconCachePath(string remoteUrl)
    {
        return AssetImageConverter.GetIconCachePath(remoteUrl);
    }

    private void ClearSmapiSourceItems()
    {
        SmapiGithubItems.Clear();
        SmapiNexusModsItems.Clear();
        SmapiCurseforgeItems.Clear();
        RaiseSmapiSourceState();
    }

    private void RaiseSmapiSourceState()
    {
        OnPropertyChanged(nameof(HasSmapiGithubItems));
        OnPropertyChanged(nameof(HasSmapiNexusModsItems));
        OnPropertyChanged(nameof(HasSmapiCurseforgeItems));
        OnPropertyChanged(nameof(HasNoSmapiItems));
    }

    /// <summary>
    /// 把结构化搜索结果项转换回 DownloadPage 目录卡片使用的 displayText 字符串。
    /// DownloadPage 目录路径未迁移到结构化模型，依赖 displayText 字符串经 ParseCatalogItem 解析，
    /// 故在此边界做一次结构化→字符串转换，保持 DownloadPage 内部表示不变。
    /// </summary>
    private static string ToCatalogDisplayText(Models.ModSearchResultItem item)
    {
        var sb = new StringBuilder();
        // Identity.Name 是原始名，item.Name 是汉化优先名
        var originalName = !string.IsNullOrWhiteSpace(item.Identity.Name) ? item.Identity.Name : item.Name;
        var hasLocalization = !string.Equals(originalName, item.Name, StringComparison.Ordinal);
        sb.Append('[').Append(item.SourceTag).Append("] ").Append(originalName);
        AppendDisplaySegment(sb, "metric", item.Stat);
        AppendDisplaySegment(sb, "time", item.TimeTag);
        AppendDisplaySegment(sb, "icon", item.IconUrl);
        AppendDisplaySegment(sb, "fullIcon", item.FullIconUrl);
        AppendDisplaySegment(sb, "type", item.ModType);
        AppendDisplaySegment(sb, "compat", item.GameVersionTag);
        // Collection slug 透传：DownloadPage 仍以 displayText 字符串携带身份，详情页据此拉取 revisions。
        AppendDisplaySegment(sb, "slug", item.CollectionSlug);
        // 显式传递汉化字段，让 ParseCatalogItem 能正确填充 LocalizedName/LocalizedSummary
        if (hasLocalization)
        {
            AppendDisplaySegment(sb, "zhName", item.Name);
        }
        if (!string.IsNullOrWhiteSpace(item.Summary))
        {
            sb.Append(" | ").Append(item.Summary);
        }

        return sb.ToString();
    }

    private static void AppendDisplaySegment(StringBuilder sb, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        sb.Append(" | ").Append(key).Append('=').Append(value);
    }

    private static DownloadCatalogItem ParseCatalogItem(string result)
    {
        var parts = result.Split('|', StringSplitOptions.TrimEntries);
        var header = parts.Length > 0 ? parts[0] : result;
        var stat = string.Empty;
        var metricTag = string.Empty;
        var timeTag = string.Empty;
        var iconSource = string.Empty;
        var fullIconSource = string.Empty;
        var summary = string.Empty;
        var sourceName = string.Empty;
        var sourceSummary = string.Empty;
        var localizedName = string.Empty;
        var localizedSummary = string.Empty;
        var modTypeTag = string.Empty;
        var gameVersionTag = string.Empty;

        for (var index = 1; index < parts.Length; index++)
        {
            var segment = parts[index].Trim();
            if (string.IsNullOrWhiteSpace(segment))
            {
                continue;
            }

            if (segment.StartsWith("metric=", StringComparison.OrdinalIgnoreCase))
            {
                metricTag = segment[7..].Trim();
                if (string.IsNullOrWhiteSpace(stat))
                {
                    stat = metricTag;
                }

                continue;
            }

            if (segment.StartsWith("time=", StringComparison.OrdinalIgnoreCase))
            {
                timeTag = segment[5..].Trim();
                continue;
            }

            if (segment.StartsWith("icon=", StringComparison.OrdinalIgnoreCase))
            {
                iconSource = segment[5..].Trim();
                continue;
            }

            if (segment.StartsWith("fullIcon=", StringComparison.OrdinalIgnoreCase))
            {
                fullIconSource = segment[9..].Trim();
                continue;
            }

            if (segment.StartsWith("type=", StringComparison.OrdinalIgnoreCase))
            {
                modTypeTag = segment[5..].Trim();
                continue;
            }

            if (segment.StartsWith("compat=", StringComparison.OrdinalIgnoreCase))
            {
                gameVersionTag = segment[7..].Trim();
                continue;
            }

            if (segment.StartsWith("srcName=", StringComparison.OrdinalIgnoreCase))
            {
                sourceName = segment[8..].Trim();
                continue;
            }

            if (segment.StartsWith("srcSummary=", StringComparison.OrdinalIgnoreCase))
            {
                sourceSummary = segment[11..].Trim();
                continue;
            }

            if (segment.StartsWith("zhName=", StringComparison.OrdinalIgnoreCase))
            {
                localizedName = segment[7..].Trim();
                continue;
            }

            if (segment.StartsWith("zhSummary=", StringComparison.OrdinalIgnoreCase))
            {
                localizedSummary = segment[10..].Trim();
                continue;
            }

            // slug= 段由 DisplayText 携带供详情页使用，这里仅消费以避免被误判为 summary。
            if (segment.StartsWith("slug=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(stat))
            {
                stat = segment;
                metricTag = segment;
            }
            else if (string.IsNullOrWhiteSpace(summary))
            {
                summary = segment;
            }
            else
            {
                summary = string.Concat(summary, " | ", segment);
            }
        }

        var sourceTag = string.Empty;
        var name = header;

        if (header.StartsWith("[", StringComparison.Ordinal))
        {
            var index = header.IndexOf(']');
            if (index > 1)
            {
                sourceTag = header[1..index].Trim();
                name = header[(index + 1)..].Trim();
            }
        }

        var sourceHead = sourceTag;
        var sourceSplitIndex = sourceTag.IndexOf('#');
        if (sourceSplitIndex > 0)
        {
            sourceHead = sourceTag[..sourceSplitIndex];
        }

        var sourceKey = ResolveSourceKey(sourceHead);
        var sourceLabel = ResolveSourceLabel(sourceKey, sourceHead);

        return new DownloadCatalogItem
        {
            DisplayText = result,
            Name = string.IsNullOrWhiteSpace(name) ? result : name,
            SourceTag = sourceLabel,
            SourceKey = sourceKey,
            Stat = stat,
            MetricTag = metricTag,
            TimeTag = timeTag,
            IconSource = iconSource,
            FullIconSource = fullIconSource,
            Summary = string.IsNullOrWhiteSpace(sourceSummary) ? summary : sourceSummary,
            SourceName = string.IsNullOrWhiteSpace(sourceName) ? name : sourceName,
            SourceSummary = string.IsNullOrWhiteSpace(sourceSummary) ? summary : sourceSummary,
            LocalizedName = localizedName,
            LocalizedSummary = localizedSummary,
            ModTypeTag = modTypeTag,
            GameVersionTag = gameVersionTag
        };
    }

    private static string ResolveSourceKey(string sourceText)
    {
        if (sourceText.Contains("github", StringComparison.OrdinalIgnoreCase))
        {
            return "github";
        }

        if (sourceText.Contains("nexus", StringComparison.OrdinalIgnoreCase))
        {
            return "nexusmods";
        }

        if (sourceText.Contains("curse", StringComparison.OrdinalIgnoreCase))
        {
            return "curseforge";
        }

        return "unknown";
    }

    private static string ResolveSourceLabel(string sourceKey, string fallback)
    {
        return sourceKey switch
        {
            "github" => "GitHub",
            "nexusmods" => "NexusMods",
            "curseforge" => "Curseforge",
            _ => string.IsNullOrWhiteSpace(fallback) ? "未知来源" : fallback
        };
    }

    private static void ReplaceStringCollection(ObservableCollection<string> target, IEnumerable<string> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            if (string.IsNullOrWhiteSpace(item))
            {
                continue;
            }

            target.Add(item.Trim());
        }
    }

    private async Task ProcessQueueAsync()
    {
        // 并发模型：收集所有待调度任务，受 _concurrencyGate(3) 限流并行执行。
        // _dispatchedTasks 防止同一任务被重复调度。
        var pending = DownloadTasks
            .Where(t => IsPendingTask(t) && !_dispatchedTasks.Contains(t))
            .ToList();

        foreach (var task in pending)
        {
            _dispatchedTasks.Add(task);
            _ = ExecuteTaskWithConcurrencyAsync(task);
        }

        await Task.CompletedTask;
    }

    private async Task ExecuteTaskWithConcurrencyAsync(DownloadTaskItem task)
    {
        await _concurrencyGate.WaitAsync();
        try
        {
            await ExecuteTaskAsync(task);
        }
        finally
        {
            _concurrencyGate.Release();
            _dispatchedTasks.Remove(task);
        }
    }

    private static bool IsPendingTask(DownloadTaskItem task)
    {
        return task.TaskState == DownloadTaskState.Pending;
    }

    private async Task ExecuteTaskAsync(DownloadTaskItem task)
    {
        // 整合包安装任务：本地文件路径（无 HTTP 下载源），直接交给 ModpackInstallService
        if (task.TaskAction == DownloadTaskAction.InstallModpack &&
            (task.TaskKind == DownloadTaskKind.SvlModpack ||
             task.TaskKind == DownloadTaskKind.CurseforgeModpack) &&
            !HasRealDownloadSource(task))
        {
            await ExecuteModpackInstallTaskAsync(task);
            return;
        }

        // Collection 安装任务：本地 7z 文件（无 HTTP 下载源），直接交给 CollectionInstallService
        if (task.TaskAction == DownloadTaskAction.InstallCollection &&
            task.TaskKind == DownloadTaskKind.NexusCollection &&
            !HasRealDownloadSource(task))
        {
            await ExecuteCollectionInstallTaskAsync(task);
            return;
        }

        if (task.TaskKind == DownloadTaskKind.NxmCollection && HasRealDownloadSource(task))
        {
            await ExecuteCollectionRealDownloadTaskAsync(task);
            return;
        }

        if (task.TaskKind == DownloadTaskKind.NxmCollection)
        {
            await ExecuteCollectionTaskAsync(task);
            return;
        }

        // 有 HTTP 下载源的任务（含需要先下载的整合包/Collection）：先下载再安装
        if (HasRealDownloadSource(task))
        {
            await ExecuteRealDownloadTaskAsync(task);
            return;
        }

        task.CanRetry = false;
        task.CanCancel = false;
        task.SetState(DownloadTaskState.Downloading, "下载中");
        task.Progress = 0;
        Status = $"正在执行任务: {task.Name}";
        TaskStateChanged?.Invoke(task);
        EmitLog($"开始执行任务: {task.Name}");

        for (var progress = 0; progress <= 100; progress += 20)
        {
            task.Progress = progress;
            TaskStateChanged?.Invoke(task);
            await Task.Delay(250);
        }

        if (task.Name.Contains("fail", StringComparison.OrdinalIgnoreCase))
        {
            task.SetState(DownloadTaskState.Failed, "下载失败（可重试）");
            task.CanRetry = true;
            Status = $"任务失败: {task.Name}";
            TaskStateChanged?.Invoke(task);
            SaveTaskState();
            EmitLog($"任务失败，可重试: {task.Name}");
            return;
        }

        task.SetState(DownloadTaskState.Installing, "安装中");
        TaskStateChanged?.Invoke(task);
        EmitLog($"开始安装任务: {task.Name}");
        await Task.Delay(300);
        task.SetState(DownloadTaskState.Completed, "已完成");
        task.Progress = 100;
        Status = $"任务完成: {task.Name}";
        TaskStateChanged?.Invoke(task);
        SaveTaskState();
        EmitLog($"任务完成: {task.Name}");
    }

    /// <summary>整合包安装任务执行：按 TaskKind 路由到 ModpackInstallService 的 SVL 或 Curseforge 流程。</summary>
    private async Task ExecuteModpackInstallTaskAsync(DownloadTaskItem task)
    {
        task.CanRetry = false;
        task.CanCancel = true;
        task.SetState(DownloadTaskState.Installing, "整合包安装中");
        task.Progress = 0;
        Status = $"正在安装整合包: {task.Name}";
        TaskStateChanged?.Invoke(task);
        EmitLog($"开始安装整合包: {task.Name}（类型: {task.TaskKind}）");

        var cts = new CancellationTokenSource();
        _runningTaskCancellationSources[task] = cts;

        try
        {
            var zipPath = task.SourceUrl;
            var instanceName = string.IsNullOrWhiteSpace(task.TargetInstanceName)
                ? Path.GetFileNameWithoutExtension(zipPath)
                : task.TargetInstanceName;

            ModpackInstallResult result;
            if (task.TaskKind == DownloadTaskKind.SvlModpack)
            {
                result = await _modpackInstallService.InstallSvlModpackAsync(
                    zipPath, instanceName, task.TargetGamePath,
                    progress =>
                    {
                        task.Progress = progress.Percent;
                        task.SetState(DownloadTaskState.Installing, progress.StepText);
                        task.SubProgressText = progress.SubProgressText;
                        task.SubProgress = progress.SubProgress;
                        EmitModpackProgress(task.Name, progress.StepText, progress.SubProgressText);
                        TaskStateChanged?.Invoke(task);
                    },
                    cts.Token);
            }
            else
            {
                result = await _modpackInstallService.InstallCurseforgeModpackAsync(
                    zipPath, instanceName, task.TargetGamePath,
                    progress =>
                    {
                        task.Progress = progress.Percent;
                        task.SetState(DownloadTaskState.Installing, progress.StepText);
                        task.SubProgressText = progress.SubProgressText;
                        task.SubProgress = progress.SubProgress;
                        EmitModpackProgress(task.Name, progress.StepText, progress.SubProgressText);
                        TaskStateChanged?.Invoke(task);
                    },
                    cts.Token);
            }

            if (result.IsSuccess)
            {
                task.Progress = 100;
                task.InstalledPath = result.RuntimePath;
                task.InstalledDirectory = result.VersionRootPath;
                var failText = result.FailedMods.Count > 0
                    ? $"（{result.FailedMods.Count} 个 Mod 下载失败）"
                    : string.Empty;
                task.SetState(DownloadTaskState.Completed, $"已完成{failText}");
                Status = $"整合包安装完成: {task.Name}";
                EmitLog($"整合包安装完成: {task.Name}, 运行目录: {result.RuntimePath}, 安装 {result.InstalledMods.Count} 个, 失败 {result.FailedMods.Count} 个");
                if (result.FailedMods.Count > 0)
                {
                    task.FailedDetails = string.Join("\n", result.FailedMods);
                    EmitLog($"失败 Mod 列表:\n{task.FailedDetails}");
                }
                // 通知 MainWindowViewModel 刷新 LaunchPage/InstancesPage 实例列表
                Dispatcher.UIThread.Post(() => InstanceContextChanged?.Invoke());
            }
            else if (result.IsCancelled)
            {
                task.SetState(DownloadTaskState.Cancelled, "整合包安装已取消");
                Status = $"整合包安装已取消: {task.Name}";
                EmitLog($"整合包安装已取消: {task.Name}");
            }
            else
            {
                task.SetState(DownloadTaskState.Failed, $"安装失败（可重试）: {result.Message}");
                task.CanRetry = true;
                Status = $"整合包安装失败: {task.Name}";
                EmitLog($"整合包安装失败: {task.Name}, 错误: {result.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            task.SetState(DownloadTaskState.Cancelled, "已取消");
            Status = $"任务已取消: {task.Name}";
            EmitLog($"整合包安装取消: {task.Name}");
        }
        catch (Exception ex)
        {
            task.SetState(DownloadTaskState.Failed, $"安装失败（可重试）: {ex.Message}");
            task.CanRetry = true;
            Status = $"整合包安装失败: {task.Name}";
            EmitLog($"整合包安装异常: {task.Name}, 错误: {ex.Message}");
        }
        finally
        {
            cts.Dispose();
            _runningTaskCancellationSources.Remove(task);
            task.CanCancel = false;
            TaskStateChanged?.Invoke(task);
            SaveTaskState();
        }
    }

    /// <summary>Collection 安装任务执行：调用 CollectionInstallService 从本地 7z 文件按 Phase 分阶段安装。</summary>
    private async Task ExecuteCollectionInstallTaskAsync(DownloadTaskItem task)
    {
        task.CanRetry = false;
        task.CanCancel = true;
        task.SetState(DownloadTaskState.Installing, "Collection 安装中");
        task.Progress = 0;
        Status = $"正在安装 Collection: {task.Name}";
        TaskStateChanged?.Invoke(task);
        EmitLog($"开始安装 Collection: {task.Name}");

        var cts = new CancellationTokenSource();
        _runningTaskCancellationSources[task] = cts;

        try
        {
            var archivePath = task.SourceUrl;
            var instanceName = string.IsNullOrWhiteSpace(task.TargetInstanceName)
                ? Path.GetFileNameWithoutExtension(archivePath)
                : task.TargetInstanceName;

            var result = await _collectionInstallService.InstallCollectionFromArchiveAsync(
                archivePath, instanceName,
                progress =>
                {
                    task.Progress = progress.Percent;
                    task.SetState(DownloadTaskState.Installing, progress.StepText);
                    task.SubProgressText = progress.SubProgressText;
                    task.SubProgress = progress.SubProgress;
                    EmitModpackProgress(task.Name, progress.StepText, progress.SubProgressText);
                    TaskStateChanged?.Invoke(task);
                },
                cts.Token,
                gameBasePath: task.TargetGamePath);

            if (result.IsSuccess)
            {
                task.Progress = 100;
                task.InstalledPath = result.RuntimePath;
                task.InstalledDirectory = result.VersionRootPath;
                var failText = result.FailedMods.Count > 0
                    ? $"（{result.FailedMods.Count} 个 Mod 下载失败）"
                    : string.Empty;
                task.SetState(DownloadTaskState.Completed, $"已完成{failText}");
                Status = $"Collection 安装完成: {task.Name}";
                EmitLog($"Collection 安装完成: {task.Name}, 运行目录: {result.RuntimePath}, 安装 {result.InstalledMods.Count} 个, 失败 {result.FailedMods.Count} 个");
                if (result.FailedMods.Count > 0)
                {
                    task.FailedDetails = string.Join("\n", result.FailedMods);
                    EmitLog($"失败 Mod 列表:\n{task.FailedDetails}");
                }
                // 通知 MainWindowViewModel 刷新 LaunchPage/InstancesPage 实例列表
                Dispatcher.UIThread.Post(() => InstanceContextChanged?.Invoke());
            }
            else if (result.IsCancelled)
            {
                task.SetState(DownloadTaskState.Cancelled, "Collection 安装已取消");
                Status = $"Collection 安装已取消: {task.Name}";
                EmitLog($"Collection 安装已取消: {task.Name}");
            }
            else
            {
                task.SetState(DownloadTaskState.Failed, $"安装失败（可重试）: {result.Message}");
                task.CanRetry = true;
                Status = $"Collection 安装失败: {task.Name}";
                EmitLog($"Collection 安装失败: {task.Name}, 错误: {result.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            task.SetState(DownloadTaskState.Cancelled, "已取消");
            Status = $"任务已取消: {task.Name}";
            EmitLog($"Collection 安装取消: {task.Name}");
        }
        catch (Exception ex)
        {
            task.SetState(DownloadTaskState.Failed, $"安装失败（可重试）: {ex.Message}");
            task.CanRetry = true;
            Status = $"Collection 安装失败: {task.Name}";
            EmitLog($"Collection 安装异常: {task.Name}, 错误: {ex.Message}");
        }
        finally
        {
            cts.Dispose();
            _runningTaskCancellationSources.Remove(task);
            task.CanCancel = false;
            TaskStateChanged?.Invoke(task);
            SaveTaskState();
        }
    }

    private async Task ExecuteRealDownloadTaskAsync(DownloadTaskItem task)
    {
        task.CanRetry = false;
        task.CanCancel = true;
        task.SetState(DownloadTaskState.Downloading, "下载中");
        task.Progress = 0;
        Dispatcher.UIThread.Post(task.ClearSegmentProgress);
        Status = $"正在下载: {task.Name}";
        TaskStateChanged?.Invoke(task);
        EmitLog($"开始真实下载: {task.SourceUrl}");

        var cts = new CancellationTokenSource();
        _runningTaskCancellationSources[task] = cts;

        try
        {
            // Nexus 下载缓存命中：直接用缓存文件，免重复下载/浏览器指引
            var fromCache = false;
            if (task.TaskKind == DownloadTaskKind.NxmMod &&
                task.SourceModId is long modId && task.SourceFileId is long fileId &&
                NexusDownloadCache.TryGet(modId, fileId, out var cachedNexus))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(task.OutputFilePath) ?? string.Empty);
                    File.Copy(cachedNexus, task.OutputFilePath, true);
                    task.Progress = 100;
                    EmitLog($"命中 Nexus 缓存，直接复制: {cachedNexus}");
                    fromCache = true;
                }
                catch
                {
                    fromCache = false;
                }
            }

            if (!fromCache)
            {
                await _httpDownloadService.DownloadAsync(
                    task.SourceUrl,
                    task.OutputFilePath,
                    snapshot =>
                    {
                        task.Progress = (int)Math.Round(snapshot.Percent);
                        var downloadedMb = snapshot.DownloadedBytes / 1024d / 1024d;
                        var totalMb = snapshot.TotalBytes / 1024d / 1024d;
                        var speedMb = snapshot.BytesPerSecond / 1024d / 1024d;

                        task.SetState(DownloadTaskState.Downloading,
                            snapshot.TotalBytes > 0
                                ? $"下载中 {snapshot.Percent:F1}% ({downloadedMb:F1}/{totalMb:F1} MB, {speedMb:F1} MB/s)"
                                : $"下载中 ({downloadedMb:F1} MB, {speedMb:F1} MB/s)");

                        // 多线程分片进度（UI 线程同步集合变更）
                        if (snapshot.SegmentPercents is { Length: > 1 } percents)
                        {
                            Dispatcher.UIThread.Post(() => task.SyncSegmentProgress(percents));
                        }

                        TaskStateChanged?.Invoke(task);
                    },
                    cts.Token,
                    log: msg => Dispatcher.UIThread.Post(() => EmitLog(msg)));

                // 下载完成写入 Nexus 缓存
                if (task.TaskKind == DownloadTaskKind.NxmMod &&
                    task.SourceModId is long saveModId && task.SourceFileId is long saveFileId)
                {
                    NexusDownloadCache.Save(saveModId, saveFileId, task.OutputFilePath);
                }
            }
        }
        catch (OperationCanceledException)
        {
            task.SetState(DownloadTaskState.Cancelled, "已取消");
            task.CanCancel = false;
            task.CanRetry = true;
            Status = $"任务已取消: {task.Name}";
            TaskStateChanged?.Invoke(task);
            SaveTaskState();
            EmitLog($"任务取消: {task.Name}");
            return;
        }
        catch (Exception ex)
        {
            task.SetState(DownloadTaskState.Failed, "下载失败（可重试）");
            task.CanRetry = true;
            task.CanCancel = false;
            Status = $"任务失败: {task.Name}";
            TaskStateChanged?.Invoke(task);
            SaveTaskState();
            EmitLog($"真实下载失败: {task.Name}, 错误: {ex.Message}");
            return;
        }
        finally
        {
            cts.Dispose();
            _runningTaskCancellationSources.Remove(task);
        }

        task.Progress = 100;
        task.CanCancel = false;
        Dispatcher.UIThread.Post(task.ClearSegmentProgress);

        if (task.TaskAction == DownloadTaskAction.SaveOnly)
        {
            task.InstalledPath = task.OutputFilePath;
            task.SetState(DownloadTaskState.Completed, "已完成（另存为）");
            TaskStateChanged?.Invoke(task);
            Status = $"另存为完成: {task.Name}";
            SaveTaskState();
            EmitLog($"另存为完成: {task.OutputFilePath}");
            return;
        }

        // 最终安全网：通用 MOD 任务实为 SMAPI（文件名/来源含 smapi 或 2400）时，
        // 转成 SMAPI 安装（需弹 Base 路径 + 实例名）。兜住未走 AddTaskFromExternalAsync 的路由遗漏。
        if (task.TaskAction == DownloadTaskAction.InstallMod &&
            LooksLikeSmapiTask(task) &&
            string.IsNullOrWhiteSpace(task.TargetGamePath))
        {
            var (basePath, instanceName) = await AskSmapiBasePathAndInstanceName(task);
            if (!string.IsNullOrWhiteSpace(basePath) && !string.IsNullOrWhiteSpace(instanceName))
            {
                task.TargetGamePath = basePath;
                task.TargetInstanceName = instanceName;
                task.TaskAction = DownloadTaskAction.InstallSmapi;
                task.Name = $"SMAPI 安装 - {instanceName}";
                EmitLog($"[SMAPI路由] 通用任务识别为 SMAPI，已转 SMAPI 安装，Base={basePath}, 实例={instanceName}");
            }
        }

        if (task.TaskAction == DownloadTaskAction.InstallSmapi)
        {
            task.SetState(DownloadTaskState.Installing, "安装中（SMAPI）");
            TaskStateChanged?.Invoke(task);
            EmitLog($"下载完成，开始安装 SMAPI: {task.OutputFilePath}");

            // 安装阶段使用新的 CTS（下载阶段的 cts 已在 finally 中 Dispose）
            var smapiCts = new CancellationTokenSource();
            _runningTaskCancellationSources[task] = smapiCts;
            SmapiInstallResult smapiResult;
            try
            {
                smapiResult = await _smapiInstallService.InstallFromZipAsync(
                    task.OutputFilePath,
                    task.TargetGamePath,
                    task.TargetInstanceName,
                    cancellationToken: smapiCts.Token,
                    logger: msg => EmitLog($"[SMAPI] {msg}"));
            }
            catch (Exception ex)
            {
                smapiResult = SmapiInstallResult.Failed($"SMAPI 安装异常: {ex.Message}");
            }
            finally
            {
                smapiCts.Dispose();
                _runningTaskCancellationSources.Remove(task);
            }

            if (!smapiResult.IsSuccess)
            {
                // 取消态不可重试（避免重试撞残留目录）；仅失败态可重试
                task.SetState(smapiResult.IsCancelled ? DownloadTaskState.Cancelled : DownloadTaskState.Failed, smapiResult.IsCancelled ? "安装已取消" : "安装失败（可重试）");
                task.CanRetry = !smapiResult.IsCancelled;
                Status = $"任务失败: {task.Name}";
                TaskStateChanged?.Invoke(task);
                SaveTaskState();
                EmitLog($"SMAPI 安装失败: {task.Name}, 错误: {smapiResult.Message}");
                return;
            }

            task.InstalledPath = smapiResult.RuntimePath;
            task.SetState(DownloadTaskState.Completed, "已完成（SMAPI）");
            TaskStateChanged?.Invoke(task);
            Status = $"SMAPI 安装完成: {task.TargetInstanceName}";

            // 写入 SMAPI 预设图标（Modded.png），与 VersionSettingsPageViewModel.ChangeSmapiVersionAsync 一致
            var iconWritten = Services.InstanceIconResolver.TryWriteDefaultSmapiIcon(smapiResult.RuntimePath);
            var iconFilePath = System.IO.Path.Combine(smapiResult.RuntimePath, ".svl-instance-icon.png");
            var iconFileExists = System.IO.File.Exists(iconFilePath);
            EmitLog($"SMAPI 图标写入: {(iconWritten ? "成功" : "失败/已存在")}, 路径={smapiResult.RuntimePath}, 文件存在={iconFileExists}, 预期位置={iconFilePath}");

            var settings = _settingsStore.Load();
            settings.PreferredInstancePath = smapiResult.RuntimePath;
            settings.InstanceName = task.TargetInstanceName;
            settings.PreferredLaunchMode = "SMAPI";
            _settingsStore.Save(settings);
            RefreshGamePathState();

            SaveTaskState();
            EmitLog($"SMAPI 安装完成: 实例={task.TargetInstanceName}, 路径={task.InstalledPath}");

            // 通知 MainWindowViewModel 刷新 LaunchPage/InstancesPage 的实例图标与状态
            // 解决：SMAPI 安装后图标仍显示 Vanilla.png 的问题（页面未刷新读取新写入的 .svl-instance-icon.png）
            // 必须 Dispatcher.UIThread.Post：安装流程经 SemaphoreSlim+Task.Run 后延续在线程池线程，
            // 非 UI 线程触发 PropertyChanged 不会传播到控件
            Dispatcher.UIThread.Post(() => InstanceContextChanged?.Invoke());
            return;
        }

        // Collection 安装：下载完成后，使用 CollectionInstallService 从本地压缩包按 Phase 分阶段安装
        if (task.TaskAction == DownloadTaskAction.InstallCollection)
        {
            task.SetState(DownloadTaskState.Installing, "Collection 安装中");
            TaskStateChanged?.Invoke(task);
            EmitLog($"下载完成，开始安装 Collection: {task.OutputFilePath}");

            var collectionCts = new CancellationTokenSource();
            _runningTaskCancellationSources[task] = collectionCts;

            try
            {
                var collectionInstanceName = string.IsNullOrWhiteSpace(task.TargetInstanceName)
                    ? Path.GetFileNameWithoutExtension(task.OutputFilePath)
                    : task.TargetInstanceName;

                var collectionResult = await _collectionInstallService.InstallCollectionFromArchiveAsync(
                    task.OutputFilePath,
                    collectionInstanceName,
                    progress =>
                    {
                        task.Progress = progress.Percent;
                        task.SetState(DownloadTaskState.Installing, progress.StepText);
                        task.SubProgressText = progress.SubProgressText;
                        task.SubProgress = progress.SubProgress;
                        EmitModpackProgress(task.Name, progress.StepText, progress.SubProgressText);
                        TaskStateChanged?.Invoke(task);
                    },
                    collectionCts.Token,
                    gameBasePath: task.TargetGamePath);

                if (collectionResult.IsSuccess)
                {
                    task.Progress = 100;
                    task.InstalledPath = collectionResult.RuntimePath;
                    task.InstalledDirectory = collectionResult.VersionRootPath;
                    var failText = collectionResult.FailedMods.Count > 0
                        ? $"（{collectionResult.FailedMods.Count} 个 Mod 下载失败）"
                        : string.Empty;
                    task.SetState(DownloadTaskState.Completed, $"已完成{failText}");
                    Status = $"Collection 安装完成: {task.Name}";
                    EmitLog($"Collection 安装完成: {task.Name}, 运行目录: {collectionResult.RuntimePath}");
                    if (collectionResult.FailedMods.Count > 0)
                    {
                        task.FailedDetails = string.Join("\n", collectionResult.FailedMods);
                    }
                    // 通知 MainWindowViewModel 刷新 LaunchPage/InstancesPage 实例列表
                    Dispatcher.UIThread.Post(() => InstanceContextChanged?.Invoke());
                }
                else if (collectionResult.IsCancelled)
                {
                    task.SetState(DownloadTaskState.Cancelled, "Collection 安装已取消");
                    Status = $"Collection 安装已取消: {task.Name}";
                }
                else
                {
                    task.SetState(DownloadTaskState.Failed, $"安装失败: {collectionResult.Message}");
                    task.CanRetry = true;
                    Status = $"Collection 安装失败: {task.Name}";
                    EmitLog($"Collection 安装失败: {task.Name}, 错误: {collectionResult.Message}");
                }
            }
            catch (Exception ex)
            {
                task.SetState(DownloadTaskState.Failed, $"安装异常: {ex.Message}");
                task.CanRetry = true;
                Status = $"Collection 安装失败: {task.Name}";
                EmitLog($"Collection 安装异常: {task.Name}, 错误: {ex.Message}");
            }
            finally
            {
                collectionCts.Dispose();
                _runningTaskCancellationSources.Remove(task);
            }

            TaskStateChanged?.Invoke(task);
            SaveTaskState();
            return;
        }

        // 整合包安装：下载完成后，使用 ModpackInstallService 从本地压缩包安装（manifest.json / modpack.json）
        if (task.TaskAction == DownloadTaskAction.InstallModpack &&
            (task.TaskKind == DownloadTaskKind.SvlModpack ||
             task.TaskKind == DownloadTaskKind.CurseforgeModpack))
        {
            task.SetState(DownloadTaskState.Installing, "整合包安装中");
            TaskStateChanged?.Invoke(task);
            EmitLog($"下载完成，开始安装整合包: {task.OutputFilePath}");

            var modpackCts = new CancellationTokenSource();
            _runningTaskCancellationSources[task] = modpackCts;

            try
            {
                var modpackInstanceName = string.IsNullOrWhiteSpace(task.TargetInstanceName)
                    ? Path.GetFileNameWithoutExtension(task.OutputFilePath)
                    : task.TargetInstanceName;

                ModpackInstallResult modpackResult;
                if (task.TaskKind == DownloadTaskKind.SvlModpack)
                {
                    modpackResult = await _modpackInstallService.InstallSvlModpackAsync(
                        task.OutputFilePath, modpackInstanceName, task.TargetGamePath,
                        progress =>
                        {
                            task.Progress = progress.Percent;
                            task.SetState(DownloadTaskState.Installing, progress.StepText);
                            task.SubProgressText = progress.SubProgressText;
                            task.SubProgress = progress.SubProgress;
                            EmitModpackProgress(task.Name, progress.StepText, progress.SubProgressText);
                            TaskStateChanged?.Invoke(task);
                        },
                        modpackCts.Token);
                }
                else
                {
                    modpackResult = await _modpackInstallService.InstallCurseforgeModpackAsync(
                        task.OutputFilePath, modpackInstanceName, task.TargetGamePath,
                        progress =>
                        {
                            task.Progress = progress.Percent;
                            task.SetState(DownloadTaskState.Installing, progress.StepText);
                            task.SubProgressText = progress.SubProgressText;
                            task.SubProgress = progress.SubProgress;
                            EmitModpackProgress(task.Name, progress.StepText, progress.SubProgressText);
                            TaskStateChanged?.Invoke(task);
                        },
                        modpackCts.Token);
                }

                if (modpackResult.IsSuccess)
                {
                    task.Progress = 100;
                    task.InstalledPath = modpackResult.RuntimePath;
                    task.InstalledDirectory = modpackResult.VersionRootPath;
                    var failText = modpackResult.FailedMods.Count > 0
                        ? $"（{modpackResult.FailedMods.Count} 个 Mod 下载失败）"
                        : string.Empty;
                    task.SetState(DownloadTaskState.Completed, $"已完成{failText}");
                    Status = $"整合包安装完成: {task.Name}";
                    EmitLog($"整合包安装完成: {task.Name}, 运行目录: {modpackResult.RuntimePath}");
                    if (modpackResult.FailedMods.Count > 0)
                    {
                        task.FailedDetails = string.Join("\n", modpackResult.FailedMods);
                    }
                    // 通知 MainWindowViewModel 刷新 LaunchPage/InstancesPage 实例列表
                    Dispatcher.UIThread.Post(() => InstanceContextChanged?.Invoke());
                }
                else if (modpackResult.IsCancelled)
                {
                    task.SetState(DownloadTaskState.Cancelled, "整合包安装已取消");
                    Status = $"整合包安装已取消: {task.Name}";
                }
                else
                {
                    task.SetState(DownloadTaskState.Failed, $"安装失败: {modpackResult.Message}");
                    task.CanRetry = true;
                    Status = $"整合包安装失败: {task.Name}";
                    EmitLog($"整合包安装失败: {task.Name}, 错误: {modpackResult.Message}");
                }
            }
            catch (Exception ex)
            {
                task.SetState(DownloadTaskState.Failed, $"安装异常: {ex.Message}");
                task.CanRetry = true;
                Status = $"整合包安装失败: {task.Name}";
                EmitLog($"整合包安装异常: {task.Name}, 错误: {ex.Message}");
            }
            finally
            {
                modpackCts.Dispose();
                _runningTaskCancellationSources.Remove(task);
            }

            TaskStateChanged?.Invoke(task);
            SaveTaskState();
            return;
        }

        task.SetState(DownloadTaskState.Installing, "安装中");
        TaskStateChanged?.Invoke(task);
        EmitLog($"下载完成，进入安装阶段: {task.OutputFilePath}");

        var installResult = await _downloadInstallService.InstallAsync(task.OutputFilePath, task.Name);
        if (!installResult.IsSuccess)
        {
            task.SetState(installResult.IsCancelled ? DownloadTaskState.Cancelled : DownloadTaskState.Failed, installResult.IsCancelled ? "安装已取消" : "安装失败（可重试）");
            task.CanRetry = true;
            Status = $"任务失败: {task.Name}";
            TaskStateChanged?.Invoke(task);
            SaveTaskState();
            EmitLog($"安装失败: {task.Name}, 错误: {installResult.Message}");
            return;
        }

        task.InstalledPath = installResult.InstalledPath;
        task.SetState(DownloadTaskState.Completed, "已完成");
        TaskStateChanged?.Invoke(task);
        Status = $"任务完成: {task.Name}";
        SaveTaskState();
        EmitLog($"任务完成: {task.Name}，安装目录: {task.InstalledPath}");
    }

    private async Task ExecuteCollectionTaskAsync(DownloadTaskItem task)
    {
        task.CanRetry = false;
        task.CanCancel = false;
        task.Progress = 0;
        task.FailedDetails = string.Empty;

        task.SetState(DownloadTaskState.Resolving, "获取 Collection 清单");
        Status = $"正在获取清单: {task.Name}";
        TaskStateChanged?.Invoke(task);
        EmitLog($"Collection 获取清单: {task.Name}");
        await Task.Delay(300);
        task.Progress = 15;
        TaskStateChanged?.Invoke(task);

        task.SetState(DownloadTaskState.Resolving, "解析 Collection 依赖");
        TaskStateChanged?.Invoke(task);
        EmitLog($"Collection 解析依赖: {task.Name}");
        await Task.Delay(350);
        task.Progress = 35;
        TaskStateChanged?.Invoke(task);

        task.SetState(DownloadTaskState.Downloading, "下载 Collection 资源包");
        TaskStateChanged?.Invoke(task);
        EmitLog($"Collection 下载资源: {task.Name}");
        for (var progress = 35; progress <= 80; progress += 15)
        {
            task.Progress = progress;
            TaskStateChanged?.Invoke(task);
            await Task.Delay(250);
        }

        task.SetState(DownloadTaskState.Installing, "安装 Collection 条目");
        TaskStateChanged?.Invoke(task);
        EmitLog($"Collection 安装条目: {task.Name}");
        await Task.Delay(350);
        task.Progress = 95;
        TaskStateChanged?.Invoke(task);

        task.SetState(DownloadTaskState.Completed, "Collection 安装完成");
        task.Progress = 100;
        Status = $"Collection 任务完成: {task.Name}";
        TaskStateChanged?.Invoke(task);
        EmitLog($"Collection 任务完成: {task.Name}");
    }

    private async Task ExecuteCollectionRealDownloadTaskAsync(DownloadTaskItem task)
    {
        task.CanRetry = false;
        task.CanCancel = false;
        task.Progress = 0;

        task.SetState(DownloadTaskState.Resolving, "获取 Collection 清单");
        Status = $"正在获取 Collection 清单: {task.Name}";
        TaskStateChanged?.Invoke(task);
        EmitLog($"Collection 获取清单: {task.Name}");
        await Task.Delay(200);
        task.Progress = 10;
        TaskStateChanged?.Invoke(task);

        task.SetState(DownloadTaskState.Resolving, "解析 Collection 资源");
        TaskStateChanged?.Invoke(task);
        EmitLog($"Collection 解析资源: {task.Name}");
        await Task.Delay(200);
        task.Progress = 20;
        TaskStateChanged?.Invoke(task);

        task.CanCancel = true;
        task.SetState(DownloadTaskState.Downloading, "下载 Collection 资源包");
        Status = $"正在下载 Collection: {task.Name}";
        TaskStateChanged?.Invoke(task);
        var allUrls = BuildCollectionDownloadUrls(task);
        var retryFailedBefore = task.FailedDownloadUrls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        task.FailedDownloadUrls.Clear();
        EmitLog($"Collection 开始真实下载，共 {allUrls.Count} 个文件");

        var cts = new CancellationTokenSource();
        _runningTaskCancellationSources[task] = cts;
        var downloadedFiles = new List<string>();
        try
        {
            var settings = _settingsStore.Load();
            var configuredParallel = Math.Clamp(settings.CollectionDownloadParallelism, 1, 8);
            var targetParallel = Math.Min(configuredParallel, allUrls.Count);
            var currentParallel = retryFailedBefore.Count > 0
                ? Math.Max(1, targetParallel / 2)
                : targetParallel;

            EmitLog($"Collection 下载并发上限: {targetParallel}");
            if (currentParallel < targetParallel)
            {
                EmitLog($"检测到失败重试场景，初始并发自动降级为: {currentParallel}");
            }

            var fileProgress = new double[allUrls.Count];
            var progressLock = new object();
            var failures = new List<string>();
            var failedUrls = new List<string>();

            var cursor = 0;
            while (cursor < allUrls.Count)
            {
                var batch = allUrls
                    .Select((url, index) => (url, index))
                    .Skip(cursor)
                    .Take(currentParallel)
                    .ToList();

                var failedInBatch = 0;
                var downloadTasks = batch.Select(item => Task.Run(async () =>
                {
                    var url = item.url;
                    var index = item.index;

                    try
                    {
                        var target = ResolveCollectionPartPath(task, url, index + 1);
                        lock (downloadedFiles)
                        {
                            downloadedFiles.Add(target);
                        }

                        await _httpDownloadService.DownloadAsync(
                            url,
                            target,
                            snapshot =>
                            {
                                lock (progressLock)
                                {
                                    fileProgress[index] = Math.Clamp(snapshot.Percent, 0, 100);
                                    var overallPercent = fileProgress.Average();
                                    var mapped = 20 + overallPercent * 0.65;
                                    task.Progress = (int)Math.Round(Math.Min(85, mapped));

                                    var downloadedMb = snapshot.DownloadedBytes / 1024d / 1024d;
                                    var totalMb = snapshot.TotalBytes / 1024d / 1024d;
                                    var speedMb = snapshot.BytesPerSecond / 1024d / 1024d;

                                    task.SetState(DownloadTaskState.Downloading,
                                        snapshot.TotalBytes > 0
                                            ? $"并发下载 {index + 1}/{allUrls.Count} {snapshot.Percent:F1}% ({downloadedMb:F1}/{totalMb:F1} MB, {speedMb:F1} MB/s)"
                                            : $"并发下载 {index + 1}/{allUrls.Count} ({downloadedMb:F1} MB, {speedMb:F1} MB/s)");

                                    TaskStateChanged?.Invoke(task);
                                }
                            },
                            cts.Token);

                        EmitLog($"Collection 文件下载完成: {index + 1}/{allUrls.Count}");
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lock (failures)
                        {
                            failures.Add($"{index + 1}/{allUrls.Count}: {ex.Message}");
                            failedUrls.Add(url);
                            failedInBatch++;
                        }
                    }
                }, cts.Token)).ToList();

                await Task.WhenAll(downloadTasks);

                var batchFailureRate = batch.Count == 0
                    ? 0
                    : failedInBatch / (double)batch.Count;

                if (batchFailureRate >= 0.34 && currentParallel > 1)
                {
                    currentParallel = Math.Max(1, currentParallel - 1);
                    EmitLog($"失败率 {batchFailureRate:P0}，自动降低并发至 {currentParallel}");
                }
                else if (failedInBatch == 0 && currentParallel < targetParallel)
                {
                    currentParallel = Math.Min(targetParallel, currentParallel + 1);
                    EmitLog($"批次稳定，自动恢复并发至 {currentParallel}");
                }

                cursor += batch.Count;
            }

            if (failures.Count > 0)
            {
                task.FailedDownloadUrls = failedUrls
                    .Where(url => !string.IsNullOrWhiteSpace(url))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var failedPreview = task.FailedDownloadUrls
                    .Take(5)
                    .Select((url, idx) => $"{idx + 1}. {url}");
                var omittedText = task.FailedDownloadUrls.Count > 5
                    ? $"\n... 其余 {task.FailedDownloadUrls.Count - 5} 项已省略"
                    : string.Empty;
                task.FailedDetails = $"失败资源 {task.FailedDownloadUrls.Count} 项:\n{string.Join("\n", failedPreview)}{omittedText}";

                var reason = string.Join("; ", failures.Take(3));
                throw new Exception($"部分文件下载失败: {reason}");
            }
        }
        catch (OperationCanceledException)
        {
            task.SetState(DownloadTaskState.Cancelled, "已取消");
            task.CanCancel = false;
            task.CanRetry = true;
            Status = $"任务已取消: {task.Name}";
            TaskStateChanged?.Invoke(task);
            SaveTaskState();
            EmitLog($"Collection 任务取消: {task.Name}");
            return;
        }
        catch (Exception ex)
        {
            var retryReport = _retryDiffReportService.Write(_downloadRootPath, task.Name, retryFailedBefore, task.FailedDownloadUrls);
            if (!string.IsNullOrWhiteSpace(retryReport))
            {
                task.RetryReportPath = retryReport;
                EmitLog($"重试对比报告: {retryReport}");
            }

            task.SetState(DownloadTaskState.Failed, "下载失败（可重试）");
            task.CanRetry = true;
            task.CanCancel = false;
            Status = $"任务失败: {task.Name}";
            TaskStateChanged?.Invoke(task);
            SaveTaskState();
            EmitLog($"Collection 下载失败: {task.Name}, 错误: {ex.Message}");

            var action = await _dialogService.ShowModpackFailureDialogAsync(
                ex.Message,
                task.RetryReportPath ?? string.Empty,
                "Collection 下载失败");
            if (action == ModpackFailureDialogAction.Retry)
            {
                RetryTask(task);
            }

            return;
        }
        finally
        {
            cts.Dispose();
            _runningTaskCancellationSources.Remove(task);
        }

        task.CanCancel = false;
        task.SetState(DownloadTaskState.Installing, "安装 Collection 条目");
        task.Progress = 90;
        TaskStateChanged?.Invoke(task);
        EmitLog($"Collection 安装开始，文件数: {downloadedFiles.Count}");

        var settingsForConflict = _settingsStore.Load();
        var conflictStrategy = CollectionInstallConflictStrategyExtensions.Parse(settingsForConflict.CollectionInstallConflictStrategy);

        var strategyOverride = await _dialogService.ShowInputAsync(
            "安装策略（仅本次）",
            "可输入覆盖/跳过/仅备份以临时覆盖本次策略；留空则沿用当前设置",
            conflictStrategy.ToDisplayName());
        if (!string.IsNullOrWhiteSpace(strategyOverride))
        {
            var parsedOverride = CollectionInstallConflictStrategyExtensions.Parse(strategyOverride.Trim());
            if (parsedOverride != conflictStrategy)
            {
                conflictStrategy = parsedOverride;
                EmitLog($"本次安装策略已临时覆盖为: {conflictStrategy.ToDisplayName()}");
            }
        }

        EmitLog($"Collection 冲突策略: {conflictStrategy.ToDisplayName()}");

        var previewItems = await _downloadInstallService.PreviewCollectionConflictsAsync(downloadedFiles, conflictStrategy);
        task.ConflictPreviewItems = previewItems
            .Select(item => $"{item.ModName} => {item.PlannedAction}")
            .ToList();
        TaskStateChanged?.Invoke(task);

        if (previewItems.Count == 0)
        {
            EmitLog("Collection 冲突预览: 未识别到可安装 Mod 条目");
        }
        else
        {
            EmitLog($"Collection 冲突预览: 共 {previewItems.Count} 个条目");
            foreach (var item in previewItems.Take(12))
            {
                EmitLog($"冲突预览: {item.ModName} => {item.PlannedAction}");
            }

            if (previewItems.Count > 12)
            {
                EmitLog($"冲突预览: 其余 {previewItems.Count - 12} 个条目已省略");
            }
        }

        var previewSummary = BuildInstallPreviewSummary(previewItems, conflictStrategy);
        var userConfirmed = await _dialogService.ShowConfirmAsync(
            "安装前确认",
            $"检测到 {previewItems.Count} 个安装条目，是否继续？\n\n{previewSummary}");
        if (!userConfirmed)
        {
            task.SetState(DownloadTaskState.Cancelled, "安装已取消");
            task.CanRetry = true;
            task.CanCancel = false;
            Status = $"任务已取消: {task.Name}";
            TaskStateChanged?.Invoke(task);
            EmitLog("用户取消了 Collection 安装");
            SaveTaskState();
            return;
        }

        var installResult = await _downloadInstallService.InstallCollectionAsync(
            downloadedFiles,
            task.Name,
            conflictStrategy,
            previewItems);
        if (!installResult.IsSuccess)
        {
            task.SetState(installResult.IsCancelled ? DownloadTaskState.Cancelled : DownloadTaskState.Failed, installResult.IsCancelled ? "安装已取消" : "安装失败（可重试）");
            task.CanRetry = true;
            Status = $"任务失败: {task.Name}";
            TaskStateChanged?.Invoke(task);
            SaveTaskState();
            EmitLog($"Collection 安装失败: {task.Name}, 错误: {installResult.Message}");

            var action = await _dialogService.ShowModpackFailureDialogAsync(
                installResult.Message,
                installResult.ReportPath ?? task.ReportPath ?? task.RetryReportPath ?? string.Empty,
                "Collection 安装失败");
            if (action == ModpackFailureDialogAction.Retry)
            {
                RetryTask(task);
            }

            return;
        }

        task.InstalledPath = installResult.InstalledPath;
        task.FailedDownloadUrls.Clear();
        task.FailedDetails = string.Empty;
        task.ReportPath = installResult.ReportPath;
        task.BackupPath = installResult.BackupPath;
        task.SetState(DownloadTaskState.Completed, "Collection 安装完成");
        task.Progress = 100;
        Status = $"Collection 任务完成: {task.Name}";
        TaskStateChanged?.Invoke(task);
        SaveTaskState();
        var installedListText = installResult.InstalledItems.Count == 0
            ? "无可识别 Mod 条目"
            : string.Join(", ", installResult.InstalledItems.Take(8));

        EmitLog($"Collection 任务完成: {task.Name}，安装目录: {task.InstalledPath}");
        EmitLog($"Collection 安装条目: {installedListText}");
        if (!string.IsNullOrWhiteSpace(installResult.BackupPath))
        {
            EmitLog($"Collection 冲突备份目录: {installResult.BackupPath}");
        }

        EmitLog($"Collection 安装校验: {(installResult.ValidationPassed ? "通过" : "存在问题")}");
        if (!installResult.ValidationPassed)
        {
            foreach (var error in installResult.ValidationErrors.Take(10))
            {
                EmitLog($"Collection 校验问题: {error}");
            }
        }

        if (!string.IsNullOrWhiteSpace(installResult.ReportPath))
        {
            EmitLog($"Collection 安装报告: {installResult.ReportPath}");
        }

        var retrySuccessReport = _retryDiffReportService.Write(_downloadRootPath, task.Name, retryFailedBefore, task.FailedDownloadUrls);
        if (!string.IsNullOrWhiteSpace(retrySuccessReport))
        {
            task.RetryReportPath = retrySuccessReport;
            EmitLog($"重试对比报告: {retrySuccessReport}");
        }
    }

    private static List<string> BuildCollectionDownloadUrls(DownloadTaskItem task)
    {
        var urls = new List<string>();
        if (!string.IsNullOrWhiteSpace(task.SourceUrl))
        {
            urls.Add(task.SourceUrl);
        }

        foreach (var url in task.DependencyUrls)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            if (urls.Any(existing => string.Equals(existing, url, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            urls.Add(url);
        }

        return urls;
    }

    private string ResolveCollectionPartPath(DownloadTaskItem task, string url, int index)
    {
        var fallbackName = $"collection-part-{index}.bin";
        var fileName = fallbackName;

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            fileName = ResolveDownloadFileName(uri, string.Empty);
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = fallbackName;
        }

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var finalName = $"{CreateSafeFileName(task.Name)}-part-{index}-{baseName}{ext}";
        return Path.Combine(_downloadRootPath, finalName);
    }

    private static string CreateSafeFileName(string name)
    {
        return InstanceRuntimePathResolver.SanitizeFileNameComponent(name, "collection");
    }

    private static bool IsHttpUri(Uri uri)
    {
        return uri.IsAbsoluteUri &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    /// <summary>扫描指定 Base 路径下 versions 子目录，返回现有实例名称列表用于重名检测。</summary>
    private static List<string> GetExistingInstanceNames(string gameBasePath)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(gameBasePath))
        {
            return names;
        }
        var versionsPath = Path.Combine(gameBasePath, "versions");
        if (!Directory.Exists(versionsPath))
        {
            return names;
        }
        try
        {
            foreach (var dir in Directory.GetDirectories(versionsPath))
            {
                var name = Path.GetFileName(dir);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
        }
        catch
        {
            // 忽略扫描异常
        }
        return names;
    }

    private void EmitLog(string message)
    {
        TaskLogGenerated?.Invoke(message);
        // 同步转发到 Debug 控制台，便于调试安装流程
        // DebugConsoleService.Append 内部已用 Dispatcher.UIThread.Post 派发，线程安全
        Services.DebugConsoleService.Instance.Append(message);
    }

    /// <summary>
    /// 整合包安装进度日志：仅在 StepText 变化时输出，避免高频进度刷新刷屏。
    /// SubProgressText 非空时附加子进度信息。
    /// </summary>
    private string? _lastModpackStepText;
    private void EmitModpackProgress(string taskName, string stepText, string? subProgressText)
    {
        if (string.IsNullOrWhiteSpace(stepText))
        {
            return;
        }

        // 仅在步骤文本变化时输出主步骤日志
        if (!string.Equals(_lastModpackStepText, stepText, StringComparison.Ordinal))
        {
            _lastModpackStepText = stepText;
            EmitLog($"[Modpack] {taskName}: {stepText}");
        }

        // 子进度文本（如"下载 mod X/N"）独立输出，仅在非空且变化时
        if (!string.IsNullOrWhiteSpace(subProgressText))
        {
            EmitLog($"[Modpack] {subProgressText}");
        }
    }

    private void TryOpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Status = $"打开路径失败: {ex.Message}";
        }
    }

    private static bool HasRealDownloadSource(DownloadTaskItem task)
    {
        return !string.IsNullOrWhiteSpace(task.SourceUrl) &&
               !string.IsNullOrWhiteSpace(task.OutputFilePath);
    }

    private void RefreshGamePathState()
    {
        var settings = _settingsStore.Load();
        var preferred = settings.PreferredInstancePath?.Trim();

        var gamePath = !string.IsNullOrWhiteSpace(preferred) && Directory.Exists(preferred)
            ? preferred
            : _gameInstallPathLocator.TryLocateSteamStardewPath() ?? _gameInstallPathLocator.TryLocateGogStardewPath();

        var hasValidPath = !string.IsNullOrWhiteSpace(gamePath) && Directory.Exists(gamePath);
        ShowGamePathWarning = !hasValidPath;
        GamePathHint = hasValidPath ? gamePath! : "未探测到游戏目录";
    }

    private async Task<bool> EnsureGamePathConfiguredAsync()
    {
        RefreshGamePathState();
        if (!ShowGamePathWarning)
        {
            return true;
        }

        var selectedPath = await _dialogService.ShowGamePathSelectionDialogAsync(
            string.Empty,
            "选择游戏路径",
            "下载与安装需要有效的 Stardew Valley 目录。请先选择游戏目录。");

        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return false;
        }

        var normalized = selectedPath.Trim();
        if (!TryNormalizeGameRootPath(normalized, out var gameRootPath))
        {
            Status = "选择的目录无效：未检测到游戏核心文件";
            return false;
        }

        var confirmed = await _dialogService.ShowGamePathConfirmDialogAsync(
            gameRootPath,
            "确认游戏路径",
            "请确认此目录为 Stardew Valley 安装目录。确认后将作为下载与安装目标路径。");

        if (!confirmed)
        {
            return false;
        }

        var settings = _settingsStore.Load();
        settings.PreferredInstancePath = gameRootPath;
        if (string.IsNullOrWhiteSpace(settings.InstanceName))
        {
            settings.InstanceName = Path.GetFileName(gameRootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        _settingsStore.Save(settings);
        RefreshGamePathState();
        return !ShowGamePathWarning;
    }

    private static string BuildNexusWebUrl(NxmLinkInfo parsed)
    {
        if (parsed.ResourceType == NxmResourceType.Collection)
        {
            return $"https://next.nexusmods.com/stardewvalley/collections/{parsed.CollectionSlug}";
        }

        // 定位到具体文件下载页并用 nmm=1 触发 NXM 回调
        var gameDomain = string.IsNullOrWhiteSpace(parsed.GameDomain) ? "stardewvalley" : parsed.GameDomain;
        return $"https://www.nexusmods.com/{gameDomain}/mods/{parsed.ModId}?tab=files&file_id={parsed.FileId}&nmm=1";
    }

    /// <summary>
    /// 浏览器下载回退：打开 Nexus 页面，等待用户在浏览器点击 Manual Download 后回传的 NXM 链接。
    /// 返回 NXM 原始链接字符串；超时或取消返回 null。
    /// </summary>
    private async Task<string?> TryBrowserDownloadFallbackAsync(long modId, long fileId, string browserUrl)
    {
        NxmImportStatus = "浏览器下载回退：请在浏览器中点击 Manual Download";
        return await _browserDownloadFallbackService.WaitForNxmCallbackAsync(
            modId,
            fileId,
            browserUrl,
            hint => NxmImportStatus = hint);
    }

    /// <summary>
    /// Collection 浏览器下载回退：打开 Nexus Collection 页面，等待用户在浏览器点击 Add collection 后回传的 NXM 链接。
    /// 返回 NXM 原始链接字符串（含 key/expires）；超时或取消返回 null。
    /// </summary>
    private async Task<string?> TryCollectionBrowserDownloadFallbackAsync(
        string collectionSlug, int revision, string browserUrl, string statusPrefix)
    {
        Status = $"{statusPrefix}：浏览器下载回退中，请在浏览器点击 Add collection";
        return await _browserDownloadFallbackService.WaitForCollectionNxmCallbackAsync(
            collectionSlug,
            revision,
            browserUrl,
            hint => Status = hint);
    }

    private static bool TryNormalizeGameRootPath(string inputPath, out string gameRoot)
    {
        gameRoot = string.Empty;
        if (string.IsNullOrWhiteSpace(inputPath) || !Directory.Exists(inputPath))
        {
            return false;
        }

        var candidates = new[]
        {
            inputPath,
            Path.Combine(inputPath, "Stardew Valley.app", "Contents", "MacOS"),
            Path.Combine(inputPath, "Stardew Valley")
        };

        foreach (var candidate in candidates.Where(Directory.Exists))
        {
            var markers = new[]
            {
                "Stardew Valley.dll",
                "Stardew Valley.deps.json",
                "Stardew Valley.exe",
                "StardewValley.exe",
                "StardewValley",
                "StardewModdingAPI.exe",
                "StardewModdingAPI"
            };

            if (markers.Any(marker => File.Exists(Path.Combine(candidate, marker))))
            {
                gameRoot = candidate;
                return true;
            }
        }

        return false;
    }

    private static string BuildInstallPreviewSummary(
        IReadOnlyList<CollectionConflictPreviewItem> previewItems,
        CollectionInstallConflictStrategy conflictStrategy)
    {
        if (previewItems.Count == 0)
        {
            return "未识别到可安装条目，仍将继续安装原始内容。";
        }

        var visible = previewItems.Take(12).ToList();
        var lines = new List<string>
        {
            $"当前策略: {conflictStrategy.ToDisplayName()}",
            $"预览条目: {previewItems.Count}（展示前 {visible.Count} 项）",
            string.Empty
        };

        lines.AddRange(visible.Select((item, idx) => $"{idx + 1}. {item.ModName} -> {item.PlannedAction}"));
        if (previewItems.Count > visible.Count)
        {
            lines.Add($"... 其余 {previewItems.Count - visible.Count} 项已省略");
        }

        return string.Join("\n", lines);
    }

    private static string ResolveDownloadFileName(Uri uri, string manualName)
    {
        // 从 URL 提取真实文件名（含扩展名），作为扩展名缺失时的回退来源
        // 剥离 ~~ 后缀元数据（CurseForge URL 可能包含 ~~channel=...;gamever=... 等元数据）
        var urlFileName = Path.GetFileName(uri.LocalPath);
        var tildeIndex = urlFileName.IndexOf("~~", StringComparison.Ordinal);
        if (tildeIndex > 0)
        {
            urlFileName = urlFileName[..tildeIndex].Trim();
        }

        var urlExtension = !string.IsNullOrWhiteSpace(urlFileName)
            ? Path.GetExtension(urlFileName)
            : string.Empty;

        // manualName 也剥离 ~~ 后缀元数据
        if (!string.IsNullOrWhiteSpace(manualName))
        {
            var nameTildeIndex = manualName.IndexOf("~~", StringComparison.Ordinal);
            if (nameTildeIndex > 0)
            {
                manualName = manualName[..nameTildeIndex].Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(manualName))
        {
            var cleaned = InstanceRuntimePathResolver.SanitizeFileNameComponent(manualName.Trim(), string.Empty);
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                // 若手动指定文件名缺少有效的压缩包扩展名，且 URL 中包含扩展名，则附加 URL 的扩展名
                // 避免 CurseForge 整合包 displayName（如 "1.9.10"）被 Path.GetExtension 误判为有扩展名 ".10"
                // 只有已知压缩包扩展名才视为有效扩展名
                var manualExtension = Path.GetExtension(cleaned);
                if (!IsKnownArchiveExtension(manualExtension) && !string.IsNullOrWhiteSpace(urlExtension) && IsKnownArchiveExtension(urlExtension))
                {
                    return cleaned + urlExtension;
                }

                return cleaned;
            }
        }

        if (!string.IsNullOrWhiteSpace(urlFileName))
        {
            return InstanceRuntimePathResolver.SanitizeFileNameComponent(urlFileName, "download.bin");
        }

        return $"download-{DateTime.Now:yyyyMMddHHmmss}.bin";
    }

    /// <summary>判断扩展名是否为已知的压缩包格式。</summary>
    private static bool IsKnownArchiveExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return false;

        return extension.ToLowerInvariant() switch
        {
            ".zip" or ".7z" or ".cfmodpack" or ".rar" or ".tar" or ".gz" or ".bz2" => true,
            _ => false
        };
    }

    private static global::Avalonia.Input.Platform.IClipboard? GetClipboard()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow?.Clipboard;
        }

        return null;
    }

    private void SaveTaskState()
    {
        try
        {
            _taskStateStore.Save(_taskStatePath, DownloadTasks.ToList());
        }
        catch
        {
            // Keep persistence as best-effort to avoid breaking download workflow.
        }
    }

    private void TryLoadTaskState()
    {
        try
        {
            var records = _taskStateStore.Load(_taskStatePath, out var brokenPath);
            if (!string.IsNullOrWhiteSpace(brokenPath))
            {
                EmitLog($"检测到损坏任务状态文件，已备份到: {brokenPath}");
            }

            if (records == null || records.Count == 0)
            {
                return;
            }

            var filteredRecords = records
                .Where(record => !IsSmokeTestTaskRecord(record))
                .ToList();

            if (filteredRecords.Count != records.Count)
            {
                EmitLog($"已过滤 {records.Count - filteredRecords.Count} 条测试任务记录");
            }

            if (filteredRecords.Count == 0)
            {
                return;
            }

            DownloadTasks.Clear();
            foreach (var record in filteredRecords)
            {
                DownloadTasks.Add(new DownloadTaskItem
                {
                    Name = record.Name,
                    Status = record.Status,
                    Progress = record.Progress,
                    TaskState = record.TaskState ?? InferTaskStateFromStatus(record.Status),
                    CanRetry = record.CanRetry,
                    CanCancel = record.CanCancel,
                    TaskKind = record.TaskKind,
                    TaskAction = record.TaskAction,
                    SourceModId = record.SourceModId,
                    SourceFileId = record.SourceFileId,
                    SourceUrl = record.SourceUrl,
                    OutputFilePath = record.OutputFilePath,
                    InstalledPath = record.InstalledPath,
                    ReportPath = record.ReportPath,
                    BackupPath = record.BackupPath,
                    FailedDetails = record.FailedDetails,
                    RetryReportPath = record.RetryReportPath,
                    TargetGamePath = record.TargetGamePath,
                    TargetInstanceName = record.TargetInstanceName,
                    StatusIconSource = string.Empty,
                    DependencyUrls = record.DependencyUrls,
                    FailedDownloadUrls = record.FailedDownloadUrls,
                    ConflictPreviewItems = record.ConflictPreviewItems
                });

                NormalizeRecoveredTaskState(DownloadTasks[^1]);
                DownloadTasks[^1].StatusIconSource = ResolveTaskStatusIcon(DownloadTasks[^1]);
            }
        }
        catch
        {
            // Ignore broken persisted state and keep in-memory defaults.
        }
    }

    private static bool IsSmokeTestTaskRecord(DownloadTaskStateRecord record)
    {
        if (record == null)
        {
            return false;
        }

        var hasSmokeName = !string.IsNullOrWhiteSpace(record.Name) &&
                           record.Name.Contains("smoke", StringComparison.OrdinalIgnoreCase);
        var hasSmokeSource = !string.IsNullOrWhiteSpace(record.SourceUrl) &&
                             record.SourceUrl.Contains("smoke", StringComparison.OrdinalIgnoreCase);
        var hasSmokeOutputPath = !string.IsNullOrWhiteSpace(record.OutputFilePath) &&
                                 record.OutputFilePath.Contains("svl-smoke-instance", StringComparison.OrdinalIgnoreCase);

        return hasSmokeName || hasSmokeSource || hasSmokeOutputPath;
    }

    private static void NormalizeRecoveredTaskState(DownloadTaskItem task)
    {
        var inferred = task.TaskState;

        if (inferred is DownloadTaskState.Resolving or DownloadTaskState.Downloading or DownloadTaskState.Installing)
        {
            // 上次运行中断 → 标记为可重试失败
            task.SetState(DownloadTaskState.Failed, "上次运行中断（可重试）");
            task.CanRetry = true;
            task.CanCancel = false;
            return;
        }

        task.TaskState = inferred;
        if (inferred == DownloadTaskState.Pending)
        {
            if (string.IsNullOrWhiteSpace(task.Status))
            {
                task.Status = "等待下载";
            }
            task.CanRetry = false;
            task.CanCancel = false;
        }
    }

    /// <summary>兼容旧版状态文件：从持久化的 Status 显示文本反推状态机。</summary>
    private static DownloadTaskState InferTaskStateFromStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return DownloadTaskState.Pending;
        }

        if (status.Contains("失败", StringComparison.Ordinal) || status.Contains("中断", StringComparison.Ordinal))
        {
            return DownloadTaskState.Failed;
        }

        if (status.Contains("已取消", StringComparison.Ordinal) || status.Contains("安装已取消", StringComparison.Ordinal))
        {
            return DownloadTaskState.Cancelled;
        }

        if (status.Contains("完成", StringComparison.Ordinal) || status.Contains("另存为", StringComparison.Ordinal))
        {
            return DownloadTaskState.Completed;
        }

        if (status.Contains("安装中", StringComparison.Ordinal) || status.Contains("安装 Collection", StringComparison.Ordinal))
        {
            return DownloadTaskState.Installing;
        }

        if (status.Contains("下载中", StringComparison.Ordinal) ||
            status.Contains("下载 Collection", StringComparison.Ordinal) ||
            status.Contains("并发下载", StringComparison.Ordinal))
        {
            return DownloadTaskState.Downloading;
        }

        if (status.Contains("获取 Collection", StringComparison.Ordinal) || status.Contains("解析 Collection", StringComparison.Ordinal))
        {
            return DownloadTaskState.Resolving;
        }

        return DownloadTaskState.Pending;
    }

}

