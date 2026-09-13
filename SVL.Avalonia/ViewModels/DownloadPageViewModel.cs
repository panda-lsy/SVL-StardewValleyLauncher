using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using SVL.Avalonia.Converters;
using SVL.Avalonia.Models;
using SVL.Avalonia.Services;
using SVL.Core.Platform.Abstractions;
using SVL.Core.Platform.Modpack;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
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
public sealed record ModInstallTarget(
    string Name,
    string Path,
    string BasePath,
    bool IsBaseInstance,
    string SmapiVersion = "");

/// <summary>
/// 在线 Mod 安装目标的显示项构造器。
/// 将实例列表转换为弹窗需要的“名称 + 完整路径”文本，并按路径去重，
/// 避免同一个 SMAPI 实例从多个探测来源出现多次。
/// </summary>
public static class ModInstallTargetOptions
{
    public static IReadOnlyList<(string DisplayName, string TargetPath)> Build(
        IEnumerable<ModInstallTarget>? targets)
    {
        return (targets ?? [])
            .Where(target => !string.IsNullOrWhiteSpace(target.Path))
            .Select(target =>
            {
                var displayName = string.IsNullOrWhiteSpace(target.Name)
                    ? "SMAPI 版本"
                    : target.Name.Trim();
                var smapiVersion = target.SmapiVersion?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(smapiVersion) &&
                    !displayName.Contains(smapiVersion, StringComparison.OrdinalIgnoreCase))
                {
                    displayName = $"{displayName} · SMAPI {smapiVersion}";
                }

                return (
                    DisplayName: displayName,
                    TargetPath: target.Path.Trim());
            })
            .GroupBy(target => target.TargetPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }
}

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
    // Nexus 协议回调可能在浏览器/系统协议层重复投递。保留一个很短的已处理窗口，
    // 让专用 SMAPI 流程结束后的迟到回调仍被吞掉，而不会再次进入通用导入并弹实例名。
    private static readonly TimeSpan SmapiExternalCallbackDedupTtl = TimeSpan.FromMinutes(2);

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
    // 取消按钮运行在 UI 线程，任务完成/失败清理运行在后台线程；使用线程安全字典
    // 避免取消与 finally 清理交错时对普通 Dictionary 的并发读写。
    private readonly ConcurrentDictionary<DownloadTaskItem, CancellationTokenSource> _runningTaskCancellationSources = new();
    // 下载回调来自分片线程，并通过 Dispatcher 排队；下载完成后进入安装阶段时，
    // 旧回调仍可能晚到。为每次下载阶段分配单调递增的代号，防止旧进度把安装/完成状态覆盖。
    private readonly ConcurrentDictionary<DownloadTaskItem, long> _downloadProgressEpochs = new();
    private long _downloadProgressEpochSeed;
    private readonly Dictionary<DownloadTaskItem, PropertyChangedEventHandler> _externalTaskPersistenceHandlers = [];
    // 详情页触发的 SMAPI 浏览器回退可能同时收到协议回调和单实例管道回调。
    // 回调被专用等待器消费后，重复事件不能再落入通用 NXM 导入流程，否则会
    // 创建第二个任务并再次询问实例名称。
    private readonly ConcurrentDictionary<string, byte> _activeSmapiExternalWorkflows = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentSmapiExternalCallbacks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _dispatchLock = new();
    private readonly object _taskStateSaveLock = new();
    private long _taskStateSaveSequence;
    private long _lastSavedTaskStateSequence;
    private readonly HashSet<DownloadTaskItem> _dispatchedTasks = [];
    private bool _pendingTasksResumeStarted;
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

    /// <summary>目录卡片使用结构化身份打开详情，避免展示文本变化后丢失来源 ID。</summary>
    public event Action<CatalogResourceIdentity>? OpenStructuredDetailsRequested;

    /// <summary>
    /// 实例上下文变更通知：SMAPI/Modpack/Collection 安装成功后触发，
    /// 由 MainWindowViewModel 订阅以刷新 LaunchPage 和 InstancesPage 的实例图标与状态。
    /// </summary>
    public event Action? InstanceContextChanged;

    /// <summary>普通 Mod 安装完成后通知版本设置页重新读取 manifest 和来源状态。</summary>
    public event Action? ModInstallationCompleted;

    /// <summary>
    /// 在线更新覆盖前创建备份后触发。由主窗口转发给 Mod 管理页，
    /// 让“备份”标签在批量更新创建备份后立即显示新条目。
    /// </summary>
    public event Action? ModBackupCreated;

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
                    UntrackExternalTask(oldItem);
                    _downloadProgressEpochs.TryRemove(oldItem, out var removedEpoch);
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

        // Pending 任务由 App 在主窗口 Show 完成后恢复。恢复流程可能需要
        // Nexus/文件选择等交互，必须等真实主窗口成为对话框宿主后再启动。
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

        // 安装流程可能在队列后台线程结束并修改任务状态；任务桶和状态图标
        // 会触碰 Avalonia 绑定集合，必须统一回到 UI 线程，避免跨线程重绘。
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnTaskPropertyChanged(sender, e));
            return;
        }

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

        if (HasUsableCatalogIdentity(item.Identity))
        {
            OpenStructuredDetailsRequested?.Invoke(item.Identity);
        }
        else
        {
            OpenDetailsRequested?.Invoke(item.DisplayText);
        }
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

        var parsedItem = ParseCatalogItem(item);
        if (HasUsableCatalogIdentity(parsedItem.Identity))
        {
            OpenStructuredDetailsRequested?.Invoke(parsedItem.Identity);
        }
        else
        {
            OpenDetailsRequested?.Invoke(item);
        }
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
            var details = HasUsableCatalogIdentity(item.Identity)
                ? await _remoteCatalogService.GetResourceDetailsAsync(item.Identity)
                : await _remoteCatalogService.GetResourceDetailsAsync(item.DisplayText);
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
            TaskAction = DownloadTaskAction.InstallModpack,
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
        var parsedItem = ParseCatalogItem(selected);
        if (HasUsableCatalogIdentity(parsedItem.Identity))
        {
            OpenStructuredDetailsRequested?.Invoke(parsedItem.Identity);
        }
        else
        {
            OpenDetailsRequested?.Invoke(selected);
        }
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

        if (!_nxmLinkParser.TryParse(NxmLinkInput, out var parsed, out var errorMessage))
        {
            NxmImportStatus = errorMessage;
            Status = "导入失败：链接格式不正确";
            return;
        }

        var settings = _settingsStore.Load();
        string cachedNexusPath = string.Empty;
        var hasCachedNexus = parsed.ResourceType == NxmResourceType.ModFile &&
                             NexusDownloadCache.TryGet(
                                 parsed.ModId,
                                 parsed.FileId,
                                 out cachedNexusPath,
                                 ModpackInstallService.IsValidModArchiveFile);
        if (parsed.ResourceType == NxmResourceType.ModFile &&
            !hasCachedNexus &&
            string.IsNullOrWhiteSpace(settings.NexusApiKey) &&
            string.IsNullOrWhiteSpace(settings.NexusOAuthAccessToken) &&
            string.IsNullOrWhiteSpace(parsed.Key))
        {
            NxmImportStatus = "请先在设置页完成 Nexus 登录，或使用带 key 的 NXM 链接";
            Status = "导入失败：Nexus 未登录且 NXM 链接缺少 key";
            return;
        }

        // SMAPI 的 NXM 回调必须沿用本次安装已经确认的目标实例。
        // 否则回调会落入通用 NXM 导入流程，任务下载完成后又会失去实例名并再次弹窗。
        var isSmapiNxm = IsSmapiNxmResource(parsed);
        string? smapiTargetGamePath = null;
        string? smapiTargetInstanceName = null;
        if (isSmapiNxm)
        {
            (smapiTargetGamePath, smapiTargetInstanceName) = await SelectSmapiInstallTargetAsync("SMAPI");
            if (string.IsNullOrWhiteSpace(smapiTargetGamePath) ||
                string.IsNullOrWhiteSpace(smapiTargetInstanceName))
            {
                Status = "已取消 SMAPI 安装";
                NxmImportStatus = "已取消 SMAPI 安装";
                return;
            }
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

        if (parsed.ResourceType == NxmResourceType.ModFile && hasCachedNexus)
        {
            var cachedFileName = CreateSafeFileName(Path.GetFileName(cachedNexusPath));
            if (string.IsNullOrWhiteSpace(cachedFileName))
            {
                cachedFileName = $"nexus-{parsed.ModId}_{parsed.FileId}.zip";
            }

            sourceUrl = BuildNexusCacheSourceUrl(parsed.ModId, parsed.FileId);
            outputFilePath = Path.Combine(_downloadRootPath, cachedFileName);
            taskName = cachedFileName;
            taskStatus = "已命中 Nexus 缓存（跳过浏览器与下载）";
            NxmImportStatus = $"已命中 Nexus 缓存：{cachedFileName}";
            EmitLog($"NXM Mod 命中 Nexus 缓存，跳过 API/浏览器解析: {cachedNexusPath}");
        }
        else if (parsed.ResourceType == NxmResourceType.ModFile)
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
            if (NexusCollectionDownloadCache.TryGet(
                    "stardewvalley",
                    parsed.CollectionSlug,
                    parsed.RevisionNumber,
                    out var cachedCollectionPath,
                    IsValidCollectionArchive))
            {
                sourceUrl = string.Empty;
                outputFilePath = cachedCollectionPath;
                taskName = CreateSafeFileName(Path.GetFileName(cachedCollectionPath));
                taskStatus = "已命中 Collection 缓存（跳过浏览器与下载）";
                NxmImportStatus = $"已命中 Collection 缓存：{taskName}";
                EmitLog($"NXM Collection 命中稳定缓存，跳过 API/浏览器解析: {cachedCollectionPath}");
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
        }

        if (isSmapiNxm)
        {
            taskName = $"SMAPI 安装 - {smapiTargetInstanceName}";
            taskStatus = "已加入队列（SMAPI 安装）";
        }

        DownloadTasks.Insert(0, new DownloadTaskItem
        {
            Name = taskName,
            Status = taskStatus,
            Progress = 0,
            TaskKind = parsed.ResourceType == NxmResourceType.Collection
                ? DownloadTaskKind.NxmCollection
                : DownloadTaskKind.NxmMod,
            TaskAction = isSmapiNxm
                ? DownloadTaskAction.InstallSmapi
                : parsed.ResourceType == NxmResourceType.Collection
                    ? DownloadTaskAction.InstallCollection
                    : DownloadTaskAction.InstallMod,
            SourceUrl = sourceUrl,
            OutputFilePath = outputFilePath,
            SourceModId = parsed.ResourceType == NxmResourceType.ModFile ? parsed.ModId : null,
            SourceFileId = parsed.ResourceType == NxmResourceType.ModFile ? parsed.FileId : null,
            SourcePlatform = parsed.ResourceType == NxmResourceType.ModFile ? "NexusMods" : string.Empty,
            CollectionSlug = parsed.ResourceType == NxmResourceType.Collection
                ? parsed.CollectionSlug
                : string.Empty,
            CollectionRevision = parsed.ResourceType == NxmResourceType.Collection
                ? parsed.RevisionNumber
                : -1,
            TargetGamePath = smapiTargetGamePath ?? string.Empty,
            TargetInstanceName = smapiTargetInstanceName ?? string.Empty,
            DependencyUrls = dependencyUrls,
            CanCancel = false,
            CanRetry = false
        });
        DownloadTasks[0].StatusIconSource = ResolveTaskStatusIcon(DownloadTasks[0]);

        NxmImportStatus = hasCachedNexus
            ? $"已从缓存入队：{parsed}"
            : $"已解析并入队：{parsed}";
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

        if (HasFailedCollectionDownloads(task))
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

    /// <summary>从任务详情重新打开 Nexus 来源页，供等待浏览器回调或失败重试使用。</summary>
    public void OpenTaskBrowser(DownloadTaskItem? task)
    {
        if (task == null || !task.CanOpenBrowserPage)
        {
            return;
        }

        string url;
        if ((task.TaskKind is DownloadTaskKind.NxmCollection or DownloadTaskKind.NexusCollection) &&
            !string.IsNullOrWhiteSpace(task.CollectionSlug))
        {
            url = $"https://next.nexusmods.com/stardewvalley/collections/{Uri.EscapeDataString(task.CollectionSlug.Trim())}";
        }
        else if (task.SourceModId is long modId && modId > 0)
        {
            var fileQuery = task.SourceFileId is long fileId && fileId > 0
                ? $"&file_id={fileId}"
                : string.Empty;
            url = $"https://www.nexusmods.com/stardewvalley/mods/{modId}?tab=files{fileQuery}&nmm=1";
        }
        else
        {
            return;
        }

        TryOpenPath(url);
        EmitLog($"重新打开 Nexus 来源页面: {url}");
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
            _runningTaskCancellationSources.TryRemove(task, out _);
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

                    string sourceUrl;
                    string resolvedFileName;
                    if (NexusDownloadCache.TryGet(
                            parsed.ModId,
                            parsed.FileId,
                            out var cachedPath,
                            ModpackInstallService.IsValidModArchiveFile))
                    {
                        sourceUrl = BuildNexusCacheSourceUrl(parsed.ModId, parsed.FileId);
                        resolvedFileName = CreateSafeFileName(Path.GetFileName(cachedPath));
                        if (string.IsNullOrWhiteSpace(resolvedFileName))
                        {
                            resolvedFileName = $"nexus-{parsed.ModId}_{parsed.FileId}.zip";
                        }

                        EmitLog($"批量更新命中 Nexus 缓存，跳过 API/浏览器解析: {cachedPath}");
                    }
                    else
                    {
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

                        sourceUrl = resolved.DownloadUrl;
                        resolvedFileName = ResolveDownloadFileName(new Uri(resolved.DownloadUrl), resolved.FileName);
                    }

                    var outputPath = Path.Combine(_downloadRootPath, resolvedFileName);
                    var task = new DownloadTaskItem
                    {
                        Name = resolvedFileName,
                        TaskKind = DownloadTaskKind.NxmMod,
                        TaskAction = DownloadTaskAction.InstallMod,
                        SourceUrl = sourceUrl,
                        OutputFilePath = outputPath,
                        SourceModId = parsed.ModId,
                        SourceFileId = parsed.FileId,
                        SkipConflictPrompt = entry.IsBatchUpdate,
                        CanCancel = false,
                        CanRetry = false
                    };
                    task.SetState(DownloadTaskState.Pending, "已加入队列（批量更新）");
                    DownloadTasks.Insert(0, task);
                    DownloadTasks[0].StatusIconSource = ResolveTaskStatusIcon(DownloadTasks[0]);
                    EmitLog($"批量更新入队: {entry.DisplayName} -> {resolvedFileName}");
                    queued++;
                }
                else if (string.Equals(entry.UpdateSource, "Curseforge", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(entry.UpdateSource, "Curse", StringComparison.OrdinalIgnoreCase))
                {
                    var updateUrl = entry.UpdateUrl;
                    var hasCurseforgeProjectId = TryParsePositiveLong(entry.ProjectId, out var curseforgeProjectId);
                    var hasCurseforgeFileId = TryParsePositiveLong(entry.FileId, out var curseforgeFileId);
                    var isDirectCurseforgeUrl = IsLikelyCurseforgeDirectDownloadUrl(updateUrl);
                    if (hasCurseforgeProjectId && !isDirectCurseforgeUrl)
                    {
                        updateUrl = await _remoteCatalogService.ResolveCurseforgeFileDownloadUrlAsync(
                            curseforgeProjectId,
                            hasCurseforgeFileId ? curseforgeFileId : 0,
                            updateUrl,
                            CancellationToken.None);
                    }

                    if (!Uri.TryCreate(updateUrl, UriKind.Absolute, out var curseUri) ||
                        !IsLikelyCurseforgeDirectDownloadUrl(updateUrl))
                    {
                        EmitLog($"批量更新跳过 {entry.DisplayName}: CurseForge 下载地址解析失败");
                        skipped++;
                        continue;
                    }

                    // CurseForge 更新任务必须保留 projectId/fileId，否则安装完成后
                    // DownloadInstallService 无法写回 svl-source.json，导出会丢失来源。
                    var fileName = ResolveDownloadFileName(curseUri, $"{entry.DisplayName}.zip");
                    var outputPath = Path.Combine(_downloadRootPath, fileName);
                    var task = new DownloadTaskItem
                    {
                        Name = fileName,
                        TaskKind = DownloadTaskKind.Generic,
                        TaskAction = DownloadTaskAction.InstallMod,
                        SourceUrl = curseUri.ToString(),
                        OutputFilePath = outputPath,
                        SourcePlatform = "Curseforge",
                        SourceModId = TryParsePositiveLong(entry.ProjectId, out var projectId) ? projectId : null,
                        SourceFileId = TryParsePositiveLong(entry.FileId, out var fileId) ? fileId : null,
                        SkipConflictPrompt = entry.IsBatchUpdate,
                        CanCancel = false,
                        CanRetry = false
                    };
                    task.SetState(DownloadTaskState.Pending, "已加入队列（批量更新）");
                    DownloadTasks.Insert(0, task);
                    DownloadTasks[0].StatusIconSource = ResolveTaskStatusIcon(DownloadTasks[0]);
                    EmitLog($"批量更新入队: {entry.DisplayName} -> {fileName}");
                    queued++;
                }
                else if (Uri.TryCreate(entry.UpdateUrl, UriKind.Absolute, out var uri) &&
                         (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    // 无平台标记的普通 HTTP 更新仍保留原有安装行为。
                    var fileName = ResolveDownloadFileName(uri, $"{entry.DisplayName}.zip");
                    var outputPath = Path.Combine(_downloadRootPath, fileName);
                    var task = new DownloadTaskItem
                    {
                        Name = fileName,
                        TaskKind = DownloadTaskKind.Generic,
                        TaskAction = DownloadTaskAction.InstallMod,
                        SourceUrl = uri.ToString(),
                        OutputFilePath = outputPath,
                        SourcePlatform = string.Equals(entry.UpdateSource, "GitHub", StringComparison.OrdinalIgnoreCase)
                            ? "GitHub"
                            : string.Empty,
                        SourceRepository = string.Equals(entry.UpdateSource, "GitHub", StringComparison.OrdinalIgnoreCase)
                            ? entry.Repository
                            : string.Empty,
                        SkipConflictPrompt = entry.IsBatchUpdate,
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
        // 强制路由到 SMAPI 安装流程，避免被当成普通 Mod 装进 Mods 文件夹。
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
        // GamePathHint 只是自动探测到的 Base 路径，不代表主页已经选中了一个版本。
        // 没有首选实例时使用专用三按钮弹窗，允许用户直接选择任意已安装 SMAPI
        // 版本，或把当前资源另存为文件。
        var settings = _settingsStore.Load();
        var hasSelectedGameVersion = !string.IsNullOrWhiteSpace(settings.PreferredInstancePath) &&
                                     Directory.Exists(settings.PreferredInstancePath);
        var currentModsPath = ResolveCurrentInstanceModsPath();
        var needInstanceSelection = string.IsNullOrWhiteSpace(currentModsPath) || IsCurrentInstanceVanilla();

        if (!hasSelectedGameVersion)
        {
            var availableTargets = AvailableModInstancesProvider?.Invoke() ?? [];
            var targetOptions = ModInstallTargetOptions.Build(availableTargets);

            var targetChoice = await _dialogService.ShowModInstallTargetDialogAsync(targetOptions);
            if (targetChoice.Action == ModInstallTargetDialogAction.SaveAs)
            {
                return await QueueSaveOnlyTaskFromExternalAsync(request);
            }

            if (targetChoice.Action != ModInstallTargetDialogAction.Confirm ||
                string.IsNullOrWhiteSpace(targetChoice.SelectedPath))
            {
                Status = "已取消 Mod 安装（未选择游戏版本）";
                return false;
            }

            var selectedTarget = availableTargets.FirstOrDefault(target =>
                string.Equals(target.Path, targetChoice.SelectedPath, StringComparison.OrdinalIgnoreCase));
            if (selectedTarget == null)
            {
                Status = "所选 SMAPI 版本已失效，请刷新实例列表后重试";
                return false;
            }

            settings.PreferredInstancePath = selectedTarget.Path;
            settings.InstanceName = selectedTarget.IsBaseInstance ? string.Empty : selectedTarget.Name;
            settings.PreferredLaunchMode = "SMAPI";
            _settingsStore.Save(settings);
        }
        else if (needInstanceSelection)
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
            settings = _settingsStore.Load();
            settings.PreferredInstancePath = selectedTarget.Path;
            settings.InstanceName = selectedTarget.IsBaseInstance ? string.Empty : selectedTarget.Name;
            settings.PreferredLaunchMode = "SMAPI";
            _settingsStore.Save(settings);
        }

        // 普通 Mod 也必须先解析真实下载地址。没有 URL 的 Nexus 文件不能直接入队，
        // 否则 ExecuteTaskAsync 会把它误判为无源任务并直接失败。
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
        var hasTrackedSource = sourceToken == "nexusmods" || sourceToken == "curseforge";
        var isGitHubSource = sourceToken == "github";
        long? sourceModId = hasTrackedSource &&
                            TryExtractPositiveLong(request.ResourceId, out var smodId)
            ? smodId
            : (long?)null;
        long? sourceFileId = hasTrackedSource &&
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
            SourcePlatform = hasTrackedSource
                ? (sourceToken == "curseforge" ? "Curseforge" : "NexusMods")
                : isGitHubSource ? "GitHub" : string.Empty,
            SourceRepository = isGitHubSource
                ? request.ResourceId?.Trim() ?? string.Empty
                : string.Empty,
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
        var suggestedFileName = NormalizeSaveAsFileName(
            CreateSafeFileName(request.ResolveSuggestedFileName()));
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

        var isNexusResource = TryGetNexusResourceIds(request, out var sourceModId, out var sourceFileId);
        var task = new DownloadTaskItem
        {
            Name = Path.GetFileName(savePath),
            Status = "已加入队列（另存为）",
            Progress = 0,
            TaskKind = isNexusResource ? DownloadTaskKind.NxmMod : DownloadTaskKind.Generic,
            TaskAction = DownloadTaskAction.SaveOnly,
            SourceUrl = resolved.DownloadUrl,
            OutputFilePath = savePath,
            SourceModId = isNexusResource ? sourceModId : null,
            SourceFileId = isNexusResource ? sourceFileId : null,
            CanCancel = false,
            CanRetry = false
        };

        EnqueueExternalTask(task, $"已加入另存为队列: {task.Name}");
        return true;
    }

    private async Task<(string? BasePath, string? InstanceName)> SelectSmapiInstallTargetAsync(string defaultName)
    {
        // 获取可用的 Base 路径列表（与整合包/Collection 安装流程一致）
        var availablePaths = AvailableGamePathsProvider?.Invoke();
        if (availablePaths == null || availablePaths.Count == 0)
        {
            var currentPath = ResolveCurrentGamePath();
            currentPath = ResolveCurrentBasePath(currentPath);
            if (string.IsNullOrWhiteSpace(currentPath))
            {
                Status = "SMAPI 安装失败：未检测到可用 Base 路径，请先在实例页面添加游戏路径";
                return (null, null);
            }
            availablePaths = new List<string> { currentPath };
        }

        // 默认选中当前首选路径（主页选中实例对应的 Base 路径）
        var defaultPath = ResolveCurrentBasePath(ResolveCurrentGamePath());
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
            return (null, null);
        }

        var existingNames = GetExistingInstanceNames(gameBasePath);
        var rawInstanceName = await _dialogService.ShowInstanceNameDialogAsync("输入 SMAPI 实例名称", defaultName, existingNames);
        if (string.IsNullOrWhiteSpace(rawInstanceName))
        {
            Status = "已取消 SMAPI 安装";
            return (null, null);
        }

        var instanceName = CreateSafeFileName(rawInstanceName);
        if (string.IsNullOrWhiteSpace(instanceName))
        {
            Status = "实例名称无效";
            return (null, null);
        }

        var versionRoot = Path.Combine(gameBasePath, "versions", instanceName);
        if (Directory.Exists(versionRoot))
        {
            Status = $"实例名称已存在: {instanceName}";
            return (null, null);
        }

        return (gameBasePath, instanceName);
    }

    private async Task<bool> QueueSmapiInstallTaskFromExternalAsync(ExternalDownloadRequest request)
    {
        var workflowKey = TryGetNexusResourceIds(request, out var requestedModId, out var requestedFileId)
            ? BuildSmapiWorkflowKey(requestedModId, requestedFileId)
            : string.Empty;

        if (!string.IsNullOrWhiteSpace(workflowKey) &&
            !_activeSmapiExternalWorkflows.TryAdd(workflowKey, 0))
        {
            Status = "该 SMAPI 下载已在处理中，请勿重复点击安装";
            return false;
        }

        try
        {
            var (gameBasePath, instanceName) = await SelectSmapiInstallTargetAsync(
                BuildSmapiDefaultInstanceName(request));
            if (string.IsNullOrWhiteSpace(gameBasePath) || string.IsNullOrWhiteSpace(instanceName))
            {
                return false;
            }

            var resolved = await ResolveSmapiExternalDownloadTargetAsync(request);
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
            if (!string.IsNullOrWhiteSpace(workflowKey))
            {
                RememberSmapiExternalCallback(workflowKey);
            }

            return true;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(workflowKey))
            {
                _activeSmapiExternalWorkflows.TryRemove(workflowKey, out _);
            }
        }
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
            currentPath = ResolveCurrentBasePath(currentPath);
            if (string.IsNullOrWhiteSpace(currentPath))
            {
                Status = "Collection 安装失败：未检测到可用 Base 路径，请先在实例页面添加游戏路径";
                return false;
            }
            availablePaths = new List<string> { currentPath };
        }

        // 默认选中当前首选路径
        var defaultPath = ResolveCurrentBasePath(ResolveCurrentGamePath());
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

        // Collection 下载地址通常是带签名的短期 URL。稳定缓存命中时不再
        // 解析 API 或打开浏览器，直接把已校验的 Collection 归档交给安装队列。
        var collectionSlug = request.CollectionSlug.Trim();
        var collectionRevision = request.CollectionRevision;
        if (!string.IsNullOrWhiteSpace(collectionSlug) &&
            NexusCollectionDownloadCache.TryGet(
                "stardewvalley",
                collectionSlug,
                collectionRevision,
                out var cachedCollectionPath,
                IsValidCollectionArchive))
        {
            var cachedTask = new DownloadTaskItem
            {
                Name = $"Collection 安装 - {instanceName}",
                Status = "已加入队列（命中 Collection 缓存）",
                Progress = 0,
                TaskKind = DownloadTaskKind.NexusCollection,
                TaskAction = DownloadTaskAction.InstallCollection,
                SourceUrl = string.Empty,
                OutputFilePath = cachedCollectionPath,
                CollectionSlug = collectionSlug,
                CollectionRevision = collectionRevision,
                TargetGamePath = selectedPath,
                TargetInstanceName = instanceName,
                CanCancel = false,
                CanRetry = false
            };

            EnqueueExternalTask(cachedTask, $"已从 Collection 缓存入队: {instanceName}");
            EmitLog($"Collection 命中稳定缓存，跳过 API/浏览器下载: {cachedCollectionPath}");
            return true;
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
            // Collection 下载完成后必须进入 CollectionInstallService，不能保留
            // Generic，否则会绕过安装分支并把 .7z 当成普通 Mod 处理。
            TaskKind = DownloadTaskKind.NexusCollection,
            TaskAction = DownloadTaskAction.InstallCollection,
            SourceUrl = resolved.DownloadUrl,
            OutputFilePath = outputPath,
            CollectionSlug = collectionSlug,
            CollectionRevision = collectionRevision,
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
            currentPath = ResolveCurrentBasePath(currentPath);
            if (string.IsNullOrWhiteSpace(currentPath))
            {
                Status = "整合包安装失败：未检测到可用 Base 路径，请先在实例页面添加游戏路径";
                return false;
            }
            availablePaths = new List<string> { currentPath };
        }

        var defaultPath = ResolveCurrentBasePath(ResolveCurrentGamePath());
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

        // 根据来源判断整合包类型
        var isSvlModpack = request.SourceToken?.Contains("SVL", StringComparison.OrdinalIgnoreCase) ?? false;
        var taskKind = isSvlModpack ? DownloadTaskKind.SvlModpack : DownloadTaskKind.CurseforgeModpack;
        var sourceToken = NormalizeSourceToken(request);
        var hasCurseforgeIdentity = !isSvlModpack && sourceToken == "curseforge";
        long? sourceProjectId = hasCurseforgeIdentity &&
                                TryExtractPositiveLong(request.ResourceId, out var parsedProjectId)
            ? parsedProjectId
            : null;
        long? sourceFileId = hasCurseforgeIdentity &&
                             TryExtractFileIdFromOption(request.SelectedDownloadOption, out var parsedFileId)
            ? parsedFileId
            : null;

        // CurseForge 整合包本体也使用稳定的 ProjectID/FileID 缓存。缓存命中时
        // 不应先请求可能已经失效的 CDN 地址；任务仍保留稳定 ID，若缓存后来被
        // 清理，执行阶段会按 ID 刷新地址。
        ResolvedExternalDownloadTarget resolved;
        if (sourceProjectId is long cachedProjectId && cachedProjectId > 0 &&
            sourceFileId is long cachedFileId && cachedFileId > 0 &&
            CurseforgeDownloadCache.TryGet(
                cachedProjectId,
                cachedFileId,
                out var cachedPackagePath,
                path => IsValidLocalPackageArchive(path, taskKind)))
        {
            resolved = ResolvedExternalDownloadTarget.Success(
                BuildCurseforgeCacheSourceUrl(cachedProjectId, cachedFileId),
                Path.GetFileName(cachedPackagePath));
            EmitLog($"CurseForge 整合包命中稳定缓存，跳过 CDN 解析: {cachedPackagePath}");
        }
        else
        {
            // 解析下载地址
            resolved = await ResolveExternalDownloadTargetAsync(request);
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
        }

        var safeResolvedFileName = CreateSafeFileName(resolved.FileName);
        var outputPath = Path.Combine(_downloadRootPath, safeResolvedFileName);
        var customIconPath = !isSvlModpack && request.IsModpack
            ? await ResolveModpackIconToLocalPathAsync(request.ModpackIconUrl)
            : string.Empty;

        var task = new DownloadTaskItem
        {
            Name = $"整合包安装 - {instanceName}",
            Status = "已加入队列（整合包安装）",
            Progress = 0,
            TaskKind = taskKind,
            TaskAction = DownloadTaskAction.InstallModpack,
            SourceUrl = resolved.DownloadUrl,
            OutputFilePath = outputPath,
            SourceModId = sourceProjectId,
            SourceFileId = sourceFileId,
            SourcePlatform = hasCurseforgeIdentity ? "Curseforge" : string.Empty,
            TargetGamePath = selectedPath,
            TargetInstanceName = instanceName,
            CustomIconPath = customIconPath,
            CanCancel = false,
            CanRetry = false
        };

        EnqueueExternalTask(task, $"已加入整合包安装队列: {instanceName}");
        return true;
    }

    /// <summary>
    /// 将外部创建的任务统一加入下载队列并启动调度器。
    /// 本地整合包/Collection 也必须经过这里，否则只会出现在列表中而不会执行。
    /// </summary>
    public void EnqueueTask(DownloadTaskItem task, string statusText)
    {
        DownloadTasks.Insert(0, task);
        DownloadTasks[0].StatusIconSource = ResolveTaskStatusIcon(DownloadTasks[0]);
        Status = statusText;
        SaveTaskState();
        _ = ProcessQueueAsync();
        NavigateToTaskStatusRequested?.Invoke();
    }

    /// <summary>
    /// 注册一个已经由其他 ViewModel 执行的外部任务。
    ///
    /// SMAPI 版本设置流程需要在版本选择对话框返回后立即执行安装，不能再次交给
    /// DownloadPage 队列，否则会重复下载/安装；但任务仍必须进入统一持久化状态，
    /// 这样应用重启时至少能恢复到可重试的失败记录，而不是直接从任务页消失。
    /// </summary>
    public void RegisterExternalTask(DownloadTaskItem task, string statusText)
    {
        if (task == null)
        {
            return;
        }

        if (!DownloadTasks.Contains(task))
        {
            DownloadTasks.Insert(0, task);
        }

        task.StatusIconSource = ResolveTaskStatusIcon(task);
        TrackExternalTask(task);
        Status = statusText;
        SaveTaskState();
    }

    private void TrackExternalTask(DownloadTaskItem task)
    {
        if (_externalTaskPersistenceHandlers.ContainsKey(task))
        {
            return;
        }

        PropertyChangedEventHandler handler = (_, args) =>
        {
            // 外部 SMAPI 流程的进度回调可能非常频繁，避免每个 Progress 都触发
            // 同步磁盘写入；状态/文案变化已覆盖排队、下载、安装、完成和失败节点。
            if (args.PropertyName == nameof(DownloadTaskItem.TaskState) ||
                args.PropertyName == nameof(DownloadTaskItem.Status) ||
                args.PropertyName == nameof(DownloadTaskItem.CanRetry) ||
                args.PropertyName == nameof(DownloadTaskItem.CanCancel) ||
                args.PropertyName == nameof(DownloadTaskItem.FailedDetails) ||
                args.PropertyName == nameof(DownloadTaskItem.InstalledPath))
            {
                SaveTaskState();
            }
        };

        _externalTaskPersistenceHandlers[task] = handler;
        task.PropertyChanged += handler;
    }

    private void UntrackExternalTask(DownloadTaskItem task)
    {
        if (_externalTaskPersistenceHandlers.Remove(task, out var handler))
        {
            task.PropertyChanged -= handler;
        }
    }

    private void EnqueueExternalTask(DownloadTaskItem task, string statusText)
    {
        EnqueueTask(task, statusText);
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

        if (TryGetNexusResourceIds(request, out var modId, out var fileId))
        {
            // Nexus 的 CDN 地址通常是短期签名 URL，不能作为稳定缓存键。
            // 先按 Mod/File ID 查稳定缓存，命中时无需登录、API 解析或打开浏览器。
            if (NexusDownloadCache.TryGet(
                    modId,
                    fileId,
                    out var cachedPath,
                    ModpackInstallService.IsValidModArchiveFile))
            {
                var cachedFileName = CreateSafeFileName(Path.GetFileName(cachedPath));
                if (string.IsNullOrWhiteSpace(cachedFileName))
                {
                    cachedFileName = $"nexus-{modId}_{fileId}.zip";
                }

                EmitLog($"Nexus 资源命中缓存，跳过 API/浏览器解析: {cachedPath}");
                return ResolvedExternalDownloadTarget.Success(
                    BuildNexusCacheSourceUrl(modId, fileId),
                    cachedFileName);
            }

            // 详情页有时已经给出短期 CDN/归档直链。此时即使没有本地 Nexus
            // 凭据也可以直接入队，不应先打开网页并等待另一个 NXM 回调。
            // 这里必须拒绝 nexusmods.com 的文件页，避免把 HTML 页面当压缩包。
            if (Uri.TryCreate(directUrl, UriKind.Absolute, out var knownDirectUri) &&
                IsLikelyNexusDirectDownloadUrl(knownDirectUri))
            {
                var directFileName = ResolveDownloadFileName(knownDirectUri, request.ResolveSuggestedFileName());
                return ResolvedExternalDownloadTarget.Success(directUrl, directFileName);
            }

            var settings = _settingsStore.Load();
            var hasNexusCredentials = !string.IsNullOrWhiteSpace(settings.NexusApiKey) ||
                                      !string.IsNullOrWhiteSpace(settings.NexusOAuthAccessToken);
            if (!hasNexusCredentials)
            {
                // 在线详情页的 Nexus 下载项只有 Mod/File ID 时，不能只弹一个
                // “请手动下载”的说明就结束：非 Premium 用户需要在浏览器点击
                // Manual Download，随后由 NXM 回调携带一次性 key 完成解析。
                return await ResolveNexusFileViaBrowserAsync(
                    modId,
                    fileId,
                    fallbackGuideUrl,
                    "Nexus 未登录，已切换到浏览器下载回退");
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

            // API/令牌解析失败时同样走浏览器回退。此前这里返回失败后只显示
            // 指引窗口，却没有注册 NXM 等待器，用户点击 Manual Download 后
            // 回调会被丢弃，表现为“下载页一直无法安装”。
            var browserResolved = await ResolveNexusFileViaBrowserAsync(
                modId,
                fileId,
                fallbackGuideUrl,
                "Nexus API 解析失败，已切换到浏览器下载回退");
            if (browserResolved.IsSuccess)
            {
                return browserResolved;
            }

            return browserResolved;
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
                IsHttpUri(curseUri) &&
                IsLikelyCurseforgeDirectDownloadUrl(resolvedUrl))
            {
                var fileName = ResolveDownloadFileName(curseUri, request.ResolveSuggestedFileName());
                return ResolvedExternalDownloadTarget.Success(resolvedUrl, fileName);
            }
        }

        if (!string.IsNullOrWhiteSpace(directUrl) &&
            Uri.TryCreate(directUrl, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
            (sourceToken != "curseforge" || IsLikelyCurseforgeDirectDownloadUrl(directUrl)))
        {
            var fileName = ResolveDownloadFileName(uri, request.ResolveSuggestedFileName());
            return ResolvedExternalDownloadTarget.Success(directUrl, fileName);
        }

        var message = sourceToken == "curseforge"
            ? "CurseForge 文件地址不是可下载压缩包，无法安全安装"
            : "未解析到可用下载地址";
        return ResolvedExternalDownloadTarget.Fail(message, fallbackGuideUrl);
    }

    /// <summary>
    /// 解析 SMAPI 外部下载地址。Nexus API 失败时由当前 SMAPI 流程等待浏览器 NXM 回调，
    /// 确保回调不会落入通用 NXM 导入流程而丢失已确认的实例名。
    /// </summary>
    private async Task<ResolvedExternalDownloadTarget> ResolveSmapiExternalDownloadTargetAsync(
        ExternalDownloadRequest request)
    {
        var resolved = await ResolveExternalDownloadTargetAsync(request);
        if (resolved.IsSuccess ||
            !TryGetNexusResourceIds(request, out var modId, out var fileId) ||
            string.IsNullOrWhiteSpace(resolved.BrowserGuideUrl))
        {
            return resolved;
        }

        var fallbackNxmLink = await TryBrowserDownloadFallbackAsync(
            modId,
            fileId,
            resolved.BrowserGuideUrl);
        if (string.IsNullOrWhiteSpace(fallbackNxmLink) ||
            !_nxmLinkParser.TryParse(fallbackNxmLink, out var fallbackInfo, out _))
        {
            return ResolvedExternalDownloadTarget.Fail(
                "SMAPI 浏览器下载回退超时或取消",
                string.Empty);
        }

        var settings = _settingsStore.Load();
        var fallbackResolved = await _nexusModDownloadResolverService.ResolveDownloadUrlAsync(
            fallbackInfo,
            settings.NexusApiKey,
            settings.NexusOAuthAccessToken);
        if (fallbackResolved.IsSuccess &&
            Uri.TryCreate(fallbackResolved.DownloadUrl, UriKind.Absolute, out var fallbackUri) &&
            IsHttpUri(fallbackUri))
        {
            var fileName = ResolveDownloadFileName(fallbackUri, fallbackResolved.FileName);
            EmitLog($"SMAPI 浏览器回退解析成功: {fileName}");
            return ResolvedExternalDownloadTarget.Success(fallbackResolved.DownloadUrl, fileName);
        }

        return ResolvedExternalDownloadTarget.Fail(
            $"SMAPI 浏览器回退解析失败: {fallbackResolved.Message}",
            string.Empty);
    }

    /// <summary>
    /// 详情页 Nexus 文件没有可用 API 凭据时的浏览器回退。
    /// 浏览器服务负责打开带 file_id/nmm=1 的页面并等待匹配 NXM 回调；
    /// 回调中的 key 交给同一解析器换取短期 CDN 地址。
    /// </summary>
    private async Task<ResolvedExternalDownloadTarget> ResolveNexusFileViaBrowserAsync(
        long modId,
        long fileId,
        string browserUrl,
        string statusText)
    {
        if (modId <= 0 || fileId <= 0)
        {
            return ResolvedExternalDownloadTarget.Fail(
                "Nexus 下载项缺少有效的 Mod/File ID",
                string.Empty);
        }

        EmitLog(statusText);
        var fallbackNxmLink = await TryBrowserDownloadFallbackAsync(modId, fileId, browserUrl);
        var parseError = string.Empty;
        if (string.IsNullOrWhiteSpace(fallbackNxmLink) ||
            !_nxmLinkParser.TryParse(fallbackNxmLink, out var fallbackInfo, out parseError))
        {
            return ResolvedExternalDownloadTarget.Fail(
                string.IsNullOrWhiteSpace(parseError)
                    ? "浏览器下载回退超时或未收到 NXM 回调"
                    : $"浏览器回退链接无效: {parseError}",
                string.Empty);
        }

        var settings = _settingsStore.Load();
        var fallbackResolved = await _nexusModDownloadResolverService.ResolveDownloadUrlAsync(
            fallbackInfo,
            settings.NexusApiKey,
            settings.NexusOAuthAccessToken);
        if (!fallbackResolved.IsSuccess ||
            !Uri.TryCreate(fallbackResolved.DownloadUrl, UriKind.Absolute, out var fallbackUri) ||
            !IsHttpUri(fallbackUri))
        {
            return ResolvedExternalDownloadTarget.Fail(
                $"浏览器回退解析失败: {fallbackResolved.Message}",
                string.Empty);
        }

        var fileName = ResolveDownloadFileName(fallbackUri, fallbackResolved.FileName);
        EmitLog($"Nexus 浏览器回退解析成功: {fileName}");
        return ResolvedExternalDownloadTarget.Success(fallbackResolved.DownloadUrl, fileName);
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
    /// 将当前选中实例归一化为所属 Base 路径。
    /// 下载页的 SMAPI/Collection/整合包选择框只展示 Base；主页若选中
    /// versions/&lt;实例&gt; 或旧布局 versions/&lt;实例&gt;/game，不能把运行目录直接
    /// 当成 Base，否则后续安装会出现嵌套 versions 目录。
    /// </summary>
    private static string ResolveCurrentBasePath(string? currentPath)
    {
        if (string.IsNullOrWhiteSpace(currentPath) || !Directory.Exists(currentPath))
        {
            return string.Empty;
        }

        var basePath = InstanceRuntimePathResolver.ResolveBasePath(currentPath);
        return Directory.Exists(basePath) ? basePath : string.Empty;
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
               text.Contains("898372", StringComparison.Ordinal) ||
               task.SourceModId == SmapiDownloadService.SmapiModId ||
               task.SourceFileId == 898372;
    }

    private static string BuildSmapiWorkflowKey(long modId, long fileId)
    {
        return $"{modId}:{fileId}";
    }

    /// <summary>
    /// 判断外部 SMAPI 回调是否属于已经打开或刚刚完成的专用安装流程。
    /// 专用等待器和通用浏览器回退都未消费时，主窗口仍不能把重复回调导入为
    /// 新的普通 NXM 任务，否则会再次弹出实例名称对话框。协议层可能在专用流程
    /// 刚结束后追加投递一次相同回调，因此还要检查短时已处理记录。
    /// </summary>
    public bool IsActiveSmapiExternalCallback(string nxmLink)
    {
        if (!_nxmLinkParser.TryParse(nxmLink, out var info, out _) ||
            !IsSmapiNxmResource(info))
        {
            return false;
        }

        var workflowKey = BuildSmapiWorkflowKey(info.ModId, info.FileId);
        if (_activeSmapiExternalWorkflows.ContainsKey(workflowKey))
        {
            return true;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _recentSmapiExternalCallbacks)
        {
            if (now - entry.Value > SmapiExternalCallbackDedupTtl)
            {
                _recentSmapiExternalCallbacks.TryRemove(entry.Key, out _);
            }
        }

        return _recentSmapiExternalCallbacks.TryGetValue(workflowKey, out var handledAt) &&
               now - handledAt <= SmapiExternalCallbackDedupTtl;
    }

    private void RememberSmapiExternalCallback(string workflowKey)
    {
        _recentSmapiExternalCallbacks[workflowKey] = DateTimeOffset.UtcNow;
    }

    private static bool IsSmapiNxmResource(NxmLinkInfo link)
    {
        return link.ResourceType == NxmResourceType.ModFile &&
               (link.ModId == SmapiDownloadService.SmapiModId || link.FileId == 898372);
    }

    /// <summary>
    /// 在 SMAPI 任务开始前恢复并固定安装目标。
    ///
    /// 旧版任务可能只保存了任务名、版本目录或普通 Mod 动作；如果把动作先
    /// 归一化为 InstallSmapi 后才解析目标，任务就会跳过原先的补问逻辑，最终
    /// 以空实例名进入安装器。目标解析前置后，已保存的实例名只会被复用一次，
    /// 真正缺失上下文时才会弹出一次对话框。
    /// </summary>
    private async Task<bool> EnsureSmapiTaskTargetAsync(DownloadTaskItem task)
    {
        var basePath = InstanceRuntimePathResolver.ResolveBasePath(task.TargetGamePath);
        var instanceName = ResolveExistingSmapiInstanceName(task);

        var hasUsableBase = !string.IsNullOrWhiteSpace(basePath) && Directory.Exists(basePath);
        if (!hasUsableBase || string.IsNullOrWhiteSpace(instanceName))
        {
            (basePath, instanceName) = await AskSmapiBasePathAndInstanceName(task);
        }

        if (string.IsNullOrWhiteSpace(basePath) ||
            !Directory.Exists(basePath) ||
            string.IsNullOrWhiteSpace(instanceName))
        {
            return false;
        }

        task.TargetGamePath = basePath;
        task.TargetInstanceName = CreateSafeFileName(instanceName);
        if (string.IsNullOrWhiteSpace(task.TargetInstanceName))
        {
            return false;
        }

        // 旧任务恢复出的目标也要立即落盘。若后续网络下载或进程退出，重启时
        // 可以直接复用这个实例名，不会再次弹出同一个对话框。
        SaveTaskState();
        EmitLog($"[SMAPI路由] 已固定安装目标: Base={task.TargetGamePath}, 实例={task.TargetInstanceName}");
        return true;
    }

    /// <summary>解析 SMAPI 安装的 Base 路径与实例名：优先复用任务中已保存的实例名，避免下载完成后重复弹窗。</summary>
    private async Task<(string? BasePath, string? InstanceName)> AskSmapiBasePathAndInstanceName(DownloadTaskItem task)
    {
        var settings = _settingsStore.Load();
        var taskTargetBase = InstanceRuntimePathResolver.ResolveBasePath(task.TargetGamePath);
        var settingsPreferredBase = InstanceRuntimePathResolver.ResolveBasePath(
            settings.PreferredInstancePath);
        var preferredBase = !string.IsNullOrWhiteSpace(taskTargetBase) &&
                            Directory.Exists(taskTargetBase)
            ? taskTargetBase
            : settingsPreferredBase;
        var existingInstanceName = ResolveExistingSmapiInstanceName(task);
        var defaultName = BuildSmapiInstanceNameFromTask(task);

        // 复用已选 Base；如果任务创建阶段已经收集过实例名，这里直接复用。
        if (!string.IsNullOrWhiteSpace(preferredBase) && Directory.Exists(preferredBase))
        {
            if (!string.IsNullOrWhiteSpace(existingInstanceName))
            {
                return (preferredBase, existingInstanceName);
            }

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

        if (!string.IsNullOrWhiteSpace(existingInstanceName))
        {
            return (basePath, existingInstanceName);
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

    private static string ResolveExistingSmapiInstanceName(DownloadTaskItem task)
    {
        var rawTargetName = task.TargetInstanceName?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(rawTargetName))
        {
            return CreateSafeFileName(rawTargetName);
        }

        // 兼容早期任务记录：旧记录可能只保留了“SMAPI 安装 - xxx”任务名。
        const string taskPrefix = "SMAPI 安装 - ";
        if (task.Name.StartsWith(taskPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var nameFromTask = task.Name[taskPrefix.Length..].Trim();
            if (!string.IsNullOrWhiteSpace(nameFromTask))
            {
                return CreateSafeFileName(nameFromTask);
            }
        }

        // 版本设置页早期创建的任务名称是“SMAPI <版本> - <实例名>”，
        // 但当时没有持久化 TargetInstanceName。优先取分隔符后的实例名，
        // 这样下载结束进入安装阶段不会再次弹出相同的输入框。
        if (task.Name.StartsWith("SMAPI ", StringComparison.OrdinalIgnoreCase))
        {
            var separatorIndex = task.Name.IndexOf(" - ", StringComparison.Ordinal);
            if (separatorIndex >= 0 && separatorIndex + 3 < task.Name.Length)
            {
                var nameFromTask = task.Name[(separatorIndex + 3)..].Trim();
                if (!string.IsNullOrWhiteSpace(nameFromTask))
                {
                    return CreateSafeFileName(nameFromTask);
                }
            }
        }

        // 更早的状态文件可能只保存了版本隔离目录/旧布局运行目录。
        // TargetGamePath 的语义曾经在 Base、versions/<name> 和
        // versions/<name>/game 之间变化，读取时从路径恢复实例名即可避免再次询问。
        var pathInstanceName = TryGetInstanceNameFromVersionPath(task.TargetGamePath);
        if (!string.IsNullOrWhiteSpace(pathInstanceName))
        {
            return CreateSafeFileName(pathInstanceName);
        }

        return string.Empty;
    }

    private static string TryGetInstanceNameFromVersionPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            var current = new DirectoryInfo(path.Trim().Trim('"'));
            if (string.Equals(current.Name, "game", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(current.Name, "Mods", StringComparison.OrdinalIgnoreCase))
            {
                current = current.Parent ?? current;
                if (string.Equals(current.Name, "game", StringComparison.OrdinalIgnoreCase))
                {
                    current = current.Parent ?? current;
                }
            }

            return current.Parent != null &&
                   string.Equals(current.Parent.Name, "versions", StringComparison.OrdinalIgnoreCase)
                ? current.Name
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
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
        return DownloadOptionIdentityParser.TryExtractFileId(option, out fileId);
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

    private static bool TryGetNexusResourceIds(
        ExternalDownloadRequest request,
        out long modId,
        out long fileId)
    {
        modId = 0;
        fileId = 0;
        return NormalizeSourceToken(request) == "nexusmods" &&
               TryExtractPositiveLong(request.ResourceId, out modId) &&
               TryExtractFileIdFromOption(request.SelectedDownloadOption, out fileId);
    }

    private static string BuildNexusCacheSourceUrl(long modId, long fileId)
    {
        return $"svl-nexus-cache://{modId}/{fileId}";
    }

    private static bool IsNexusCacheSource(string? sourceUrl)
    {
        return Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) &&
               string.Equals(uri.Scheme, "svl-nexus-cache", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildCurseforgeCacheSourceUrl(long projectId, long fileId)
    {
        return $"svl-curseforge-cache://{projectId}/{fileId}";
    }

    private static bool IsCurseforgeCacheSource(string? sourceUrl)
    {
        return Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) &&
               string.Equals(uri.Scheme, "svl-curseforge-cache", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCurseforgeTask(DownloadTaskItem task)
    {
        return string.Equals(task.SourcePlatform, "Curseforge", StringComparison.OrdinalIgnoreCase);
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

    private static string NormalizeSaveAsFileName(string fileName)
    {
        var normalized = string.IsNullOrWhiteSpace(fileName)
            ? "download.zip"
            : fileName.Trim();
        var extension = Path.GetExtension(normalized);
        if (!IsKnownArchiveExtension(extension))
        {
            // 版本号中的最后一个“.2”“.0”不是文件扩展名。在线 Mod/SMAPI
            // 另存为始终保存归档，补成 .zip 后 Windows 文件选择器才能
            // 显示正确的“ZIP 压缩包 (*.zip)”类型。
            normalized += ".zip";
        }

        return CollapseRepeatedVersionSuffix(normalized);
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
            Identity = new CatalogResourceIdentity(
                long.TryParse(sourceId, out var parsedSourceId) ? parsedSourceId : 0,
                SmapiDefaultName,
                ResolveCatalogSource(sourceKey),
                false,
                string.Empty),
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

    /// <summary>
    /// 缓存 CurseForge 整合包详情页的主图标。安装器随后把该文件写入实例目录；
    /// 如果 URL 不可用或下载失败，返回空值并让安装器继续扫描包内图标作为回退。
    /// </summary>
    private async Task<string> ResolveModpackIconToLocalPathAsync(string? iconUrl)
    {
        if (!Uri.TryCreate(iconUrl?.Trim(), UriKind.Absolute, out var iconUri) ||
            (iconUri.Scheme != Uri.UriSchemeHttp && iconUri.Scheme != Uri.UriSchemeHttps))
        {
            return string.Empty;
        }

        var remoteUrl = iconUri.ToString();
        var cachePath = AssetImageConverter.GetIconCachePath(remoteUrl);
        if (string.IsNullOrWhiteSpace(cachePath))
        {
            return string.Empty;
        }

        try
        {
            if (File.Exists(cachePath) && new FileInfo(cachePath).Length > 0)
            {
                return cachePath;
            }

            var cacheDirectory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(cacheDirectory))
            {
                Directory.CreateDirectory(cacheDirectory);
            }

            using var response = await GetIconHttpClient().GetAsync(
                iconUri,
                HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                EmitLog($"整合包图标下载失败，回退到包内图标: {(int)response.StatusCode} {remoteUrl}");
                return string.Empty;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0)
            {
                return string.Empty;
            }

            await File.WriteAllBytesAsync(cachePath, bytes);
            EmitLog($"已缓存 CurseForge 整合包图标: {cachePath}");
            return cachePath;
        }
        catch (Exception ex)
        {
            EmitLog($"整合包图标缓存失败，回退到包内图标: {ex.Message}");
            return string.Empty;
        }
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
        var sourceId = 0L;
        if (sourceSplitIndex > 0)
        {
            long.TryParse(sourceTag[(sourceSplitIndex + 1)..].Trim(), out sourceId);
        }

        var collectionSlug = parts
            .Skip(1)
            .Select(part => part.Trim())
            .Where(part => part.StartsWith("slug=", StringComparison.OrdinalIgnoreCase))
            .Select(part => part[5..].Trim())
            .FirstOrDefault() ?? string.Empty;

        return new DownloadCatalogItem
        {
            Identity = new CatalogResourceIdentity(
                sourceId,
                string.IsNullOrWhiteSpace(name) ? result : name,
                ResolveCatalogSource(sourceKey),
                sourceHead.Contains("pack", StringComparison.OrdinalIgnoreCase) ||
                sourceHead.Contains("collection", StringComparison.OrdinalIgnoreCase),
                collectionSlug),
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

    private static bool HasUsableCatalogIdentity(CatalogResourceIdentity identity)
    {
        return identity.ResourceId > 0 && identity.Source != CatalogSource.Unknown;
    }

    private static CatalogSource ResolveCatalogSource(string sourceKey)
    {
        return sourceKey switch
        {
            "github" => CatalogSource.GitHub,
            "nexusmods" => CatalogSource.NexusMods,
            "curseforge" => CatalogSource.Curseforge,
            _ => CatalogSource.Unknown
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
        // 并发模型：只把当前可用槽位的任务派发出去；任务完成后会再次泵队列。
        // 这样设置页的并发上限能覆盖所有下载/安装任务，而不是固定为 3。
        // ProcessQueueAsync 也会从后台任务的 finally 回调触发，不能直接在后台线程
        // 枚举 Avalonia ObservableCollection；先取得 UI 线程快照，再在锁内计算槽位。
        var taskSnapshot = Dispatcher.UIThread.CheckAccess()
            ? DownloadTasks.ToList()
            : await Dispatcher.UIThread.InvokeAsync(() => DownloadTasks.ToList());

        List<DownloadTaskItem> toDispatch;
        lock (_dispatchLock)
        {
            var parallelism = GetConfiguredQueueParallelism();
            var slots = Math.Max(0, parallelism - _dispatchedTasks.Count);
            toDispatch = taskSnapshot
                .Where(t => IsPendingTask(t) && !_dispatchedTasks.Contains(t))
                .Take(slots)
                .ToList();

            foreach (var task in toDispatch)
            {
                _dispatchedTasks.Add(task);
            }
        }

        foreach (var task in toDispatch)
        {
            _ = ExecuteTaskWithConcurrencyAsync(task);
        }
    }

    private async Task ExecuteTaskWithConcurrencyAsync(DownloadTaskItem task)
    {
        try
        {
            await ExecuteTaskAsync(task);
        }
        catch (Exception ex)
        {
            // 防止某个未预料的异常让队列槽位永久占用，且让任务保持可重试。
            var errorMessage = ex.Message;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                task.SetState(DownloadTaskState.Failed, $"任务异常（可重试）: {errorMessage}");
                task.CanRetry = true;
                task.CanCancel = false;
                Status = $"任务失败: {task.Name}";
                TaskStateChanged?.Invoke(task);
                EmitLog($"任务异常，可重试: {task.Name}, 错误: {errorMessage}");
                SaveTaskState();
            });
        }
        finally
        {
            lock (_dispatchLock)
            {
                _dispatchedTasks.Remove(task);
            }

            // 当前任务结束后继续填充空出的并发槽位。
            _ = ProcessQueueAsync();
        }
    }

    private int GetConfiguredQueueParallelism()
    {
        try
        {
            return Math.Clamp(_settingsStore.Load().CollectionDownloadParallelism, 1, 8);
        }
        catch
        {
            return 3;
        }
    }

    /// <summary>
    /// 下载任务遇到 Nexus CDN 临时地址失效时，使用稳定的 Mod/File ID 重新解析一次。
    /// Nexus 返回的 CDN 地址带有短期签名，应用重启或任务长时间排队后再直接复用该
    /// 地址很容易得到 403；恢复任务必须回到 API/NXM 浏览器回退，而不是永久失败。
    /// </summary>
    private async Task DownloadTaskArtifactWithNexusRefreshAsync(
        DownloadTaskItem task,
        Func<string, bool>? cacheValidator,
        Action<DownloadProgressSnapshot>? onProgress,
        CancellationToken cancellationToken,
        Action<string>? log)
    {
        // 稳定缓存来源不是 HTTP 地址。它通常只在入队时作为“已命中缓存”的
        // 任务来源写入；如果缓存随后被清理，仍应跳过一次必然失败的伪 URL 请求，
        // 直接按 ProjectID/FileID 刷新真实地址。
        var requiresTrackedSourceRefresh = IsCurseforgeCacheSource(task.SourceUrl);
        if (!requiresTrackedSourceRefresh)
        {
            task.SourceUrl = NormalizeHttpDownloadUrl(task.SourceUrl);
        }
        Exception? firstError = null;
        try
        {
            if (!requiresTrackedSourceRefresh)
            {
                await _httpDownloadService.DownloadAsync(
                    task.SourceUrl,
                    task.OutputFilePath,
                    onProgress,
                    cancellationToken,
                    log,
                    cacheValidator);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception initialError) when (CanRefreshTrackedTaskSource(task))
        {
            firstError = initialError;
            log?.Invoke($"平台临时下载地址失效，先复用直链重试: {initialError.Message}");
            // Edge CDN 偶发会在分片响应中途断流。先复用当前地址重试一次；
            // CurseForge 的重试强制使用单连接，避免同一 CDN 对并发 Range 请求
            // 不稳定时不断重复相同的分片失败。
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var retryThreadCount = IsCurseforgeTrackedTask(task) ? 1 : 0;
                await _httpDownloadService.DownloadAsync(
                    task.SourceUrl,
                    task.OutputFilePath,
                    retryThreadCount,
                    onProgress,
                    cancellationToken,
                    log,
                    cacheValidator);
                log?.Invoke("平台直链重试成功");
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception retryError)
            {
                firstError = retryError;
                log?.Invoke($"平台直链重试失败，继续刷新稳定地址: {retryError.Message}");
            }
        }

        if (requiresTrackedSourceRefresh && !CanRefreshTrackedTaskSource(task))
        {
            throw new InvalidDataException("缓存来源缺少可恢复的项目/文件 ID");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var refreshed = await ResolveTrackedTaskSourceAsync(task, cancellationToken);
        if (!refreshed.IsSuccess ||
            !Uri.TryCreate(refreshed.DownloadUrl, UriKind.Absolute, out var refreshedUri) ||
            !IsHttpUri(refreshedUri))
        {
            if (firstError != null && IsCurseforgeTrackedTask(task))
            {
                throw new InvalidDataException(
                    $"CurseForge 下载失败，且无法刷新下载地址: {firstError.Message}",
                    firstError);
            }

            throw new InvalidDataException(
                string.IsNullOrWhiteSpace(refreshed.Message)
                    ? "Nexus 下载地址刷新失败"
                    : refreshed.Message);
        }

        task.SourceUrl = NormalizeHttpDownloadUrl(refreshed.DownloadUrl);
        log?.Invoke($"{(IsCurseforgeTrackedTask(task) ? "CurseForge" : "Nexus")} 下载地址已刷新，继续下载: {refreshed.FileName}");
        var refreshedThreadCount = IsCurseforgeTrackedTask(task) ? 1 : 0;
        await _httpDownloadService.DownloadAsync(
            task.SourceUrl,
            task.OutputFilePath,
            refreshedThreadCount,
            onProgress,
            cancellationToken,
            log,
            cacheValidator);
    }

    private static bool CanRefreshTrackedTaskSource(DownloadTaskItem task)
    {
        return task.SourceModId is > 0 &&
               task.SourceFileId is > 0 &&
               (task.TaskKind == DownloadTaskKind.NxmMod ||
                string.Equals(task.SourcePlatform, "NexusMods", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(task.SourcePlatform, "Curseforge", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsNexusTrackedTask(DownloadTaskItem task)
    {
        return task.TaskKind == DownloadTaskKind.NxmMod ||
               string.Equals(task.SourcePlatform, "NexusMods", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCurseforgeTrackedTask(DownloadTaskItem task)
    {
        return string.Equals(task.SourcePlatform, "Curseforge", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ResolvedExternalDownloadTarget> ResolveTrackedTaskSourceAsync(
        DownloadTaskItem task,
        CancellationToken cancellationToken)
    {
        if (IsNexusTrackedTask(task))
        {
            var nexusResolved = await ResolveNexusTaskSourceAsync(task, cancellationToken);
            return nexusResolved.IsSuccess &&
                   Uri.TryCreate(nexusResolved.DownloadUrl, UriKind.Absolute, out var nexusUri) &&
                   IsHttpUri(nexusUri)
                ? ResolvedExternalDownloadTarget.Success(
                    nexusResolved.DownloadUrl,
                    ResolveDownloadFileName(nexusUri, nexusResolved.FileName))
                : ResolvedExternalDownloadTarget.Fail(nexusResolved.Message, string.Empty);
        }

        if (IsCurseforgeTrackedTask(task) &&
            task.SourceModId is long projectId && projectId > 0 &&
            task.SourceFileId is long fileId && fileId > 0)
        {
            var resolvedUrl = await _remoteCatalogService.ResolveCurseforgeFileDownloadUrlAsync(
                projectId,
                fileId,
                string.Empty,
                cancellationToken);
            if (Uri.TryCreate(resolvedUrl, UriKind.Absolute, out var curseUri) &&
                IsHttpUri(curseUri) &&
                IsLikelyCurseforgeDirectDownloadUrl(resolvedUrl))
            {
                return ResolvedExternalDownloadTarget.Success(
                    curseUri.AbsoluteUri,
                    ResolveDownloadFileName(curseUri, task.Name));
            }

            return ResolvedExternalDownloadTarget.Fail(
                "CurseForge 下载地址刷新失败",
                string.Empty);
        }

        return ResolvedExternalDownloadTarget.Fail("下载来源缺少可恢复的项目/文件 ID", string.Empty);
    }

    private async Task<NexusResolveResult> ResolveNexusTaskSourceAsync(
        DownloadTaskItem task,
        CancellationToken cancellationToken)
    {
        if (task.SourceModId is not long modId || modId <= 0 ||
            task.SourceFileId is not long fileId || fileId <= 0)
        {
            return NexusResolveResult.Failed("Nexus 下载任务缺少有效的 Mod/File ID");
        }

        var settings = _settingsStore.Load();
        var info = new NxmLinkInfo
        {
            ResourceType = NxmResourceType.ModFile,
            GameDomain = "stardewvalley",
            ModId = modId,
            FileId = fileId
        };

        NexusResolveResult resolved;
        if (!string.IsNullOrWhiteSpace(settings.NexusApiKey) ||
            !string.IsNullOrWhiteSpace(settings.NexusOAuthAccessToken))
        {
            resolved = await _nexusModDownloadResolverService.ResolveDownloadUrlAsync(
                info,
                settings.NexusApiKey,
                settings.NexusOAuthAccessToken,
                cancellationToken);
            if (resolved.IsSuccess)
            {
                return resolved;
            }
        }

        var browserUrl = BuildNexusWebUrl(info);
        var fallbackNxmLink = await TryBrowserDownloadFallbackAsync(
            modId,
            fileId,
            browserUrl,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(fallbackNxmLink))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return NexusResolveResult.Failed("Nexus 下载地址刷新失败，未收到浏览器 NXM 回调");
        }

        if (!_nxmLinkParser.TryParse(fallbackNxmLink, out var fallbackInfo, out var parseError))
        {
            return NexusResolveResult.Failed(
                string.IsNullOrWhiteSpace(parseError)
                    ? "Nexus 下载地址刷新失败，浏览器回退链接无效"
                    : $"Nexus 浏览器回退链接无效: {parseError}");
        }

        return await _nexusModDownloadResolverService.ResolveDownloadUrlAsync(
            fallbackInfo,
            settings.NexusApiKey,
            settings.NexusOAuthAccessToken,
            cancellationToken);
    }

    private static bool IsPendingTask(DownloadTaskItem task)
    {
        return task.TaskState == DownloadTaskState.Pending;
    }

    private long BeginDownloadProgressEpoch(DownloadTaskItem task)
    {
        var epoch = Interlocked.Increment(ref _downloadProgressEpochSeed);
        _downloadProgressEpochs[task] = epoch;
        return epoch;
    }

    private void EndDownloadProgressEpoch(DownloadTaskItem task, long epoch)
    {
        // 不删除条目，避免旧回调与重试新阶段出现 ABA；下一个阶段会写入新的
        // 全局唯一 epoch。任务从列表移除时再清理字典。
        _downloadProgressEpochs.TryUpdate(task, Interlocked.Increment(ref _downloadProgressEpochSeed), epoch);
    }

    private bool IsCurrentDownloadProgressEpoch(DownloadTaskItem task, long epoch)
    {
        return _downloadProgressEpochs.TryGetValue(task, out var current) && current == epoch;
    }

    private static async Task ClearTaskSegmentProgressAsync(DownloadTaskItem task)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            task.ClearSegmentProgress();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(task.ClearSegmentProgress);
    }

    /// <summary>
    /// 应用重启后恢复持久化的 Pending 任务。
    /// 上次运行中已经进入 Resolving/Downloading/Installing 的任务会在读取时
    /// 标记为可重试失败；只有明确保持 Pending 的任务才在这里重新进入队列。
    /// </summary>
    public void ResumePendingTasks()
    {
        if (_pendingTasksResumeStarted)
        {
            return;
        }

        _pendingTasksResumeStarted = true;
        var pendingCount = DownloadTasks.Count(IsPendingTask);
        if (pendingCount == 0)
        {
            return;
        }

        Status = $"正在恢复 {pendingCount} 个待处理下载任务";
        EmitLog($"应用启动：恢复 {pendingCount} 个 Pending 下载任务");
        _ = ProcessQueueAsync();
    }

    private async Task ExecuteTaskAsync(DownloadTaskItem task)
    {
        // 兼容早期状态/外部入队记录：SMAPI 曾经可能以普通 Mod 动作落盘。
        // 必须在下载前纠正动作，避免下载完成后才进入 SMAPI 分支并临时询问
        // 实例名称；已有 TargetGamePath/TargetInstanceName 会被完整复用。
        NormalizeSmapiTaskAction(task);

        // SMAPI 的安装目标是任务上下文的一部分，必须在进入下载/安装队列前
        // 完整恢复。旧状态文件可能没有 TaskAction、TargetGamePath 或
        // TargetInstanceName；此时只允许在这里补问一次，不能等下载完成后再
        // 进入另一条安全网重复弹出实例名对话框。
        if (task.TaskAction == DownloadTaskAction.InstallSmapi &&
            !await EnsureSmapiTaskTargetAsync(task))
        {
            task.SetState(DownloadTaskState.Cancelled, "已取消 SMAPI 安装（未选择实例）");
            task.CanRetry = false;
            task.CanCancel = false;
            Status = $"已取消任务: {task.Name}";
            TaskStateChanged?.Invoke(task);
            SaveTaskState();
            return;
        }

        // 手动 URL 导入在下载前使用 Generic 任务。若应用在归档已经落盘、
        // 但尚未完成类型识别时退出，恢复任务时必须重新识别本地包，
        // 否则它会错过本地安装路由并被当成缺少来源的普通任务。
        TryResolveLocalPackageTaskKind(task);

        // 兼容旧版已经保存 CurseForge ProjectID/FileID、但没有保存 CDN 地址的
        // 整合包任务。稳定 ID 本身就是可恢复来源，先转成内部缓存来源标识，
        // 让后续执行分支优先命中缓存，缓存不存在时再刷新 CDN。
        if (task.TaskAction == DownloadTaskAction.InstallModpack &&
            task.TaskKind == DownloadTaskKind.CurseforgeModpack &&
            !HasRealDownloadSource(task) &&
            IsCurseforgeTask(task) &&
            task.SourceModId is long recoverableProjectId && recoverableProjectId > 0 &&
            task.SourceFileId is long recoverableFileId && recoverableFileId > 0)
        {
            task.SourceUrl = BuildCurseforgeCacheSourceUrl(
                recoverableProjectId,
                recoverableFileId);
        }

        // 整合包/Collection 的下载和安装是两个阶段。若归档已经完整落盘、
        // 但安装阶段失败或应用重启后中断，重试应直接复用本地归档；SourceUrl
        // 仍保留远程地址，只有本地归档不存在时才重新进入下载流程。
        if (ShouldReuseLocalPackageArchive(task))
        {
            if (task.TaskAction == DownloadTaskAction.InstallCollection)
            {
                await ExecuteCollectionInstallTaskAsync(task, task.OutputFilePath);
            }
            else
            {
                await ExecuteModpackInstallTaskAsync(task, task.OutputFilePath);
            }

            return;
        }

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

        if (task.TaskAction == DownloadTaskAction.InstallCollection &&
            task.TaskKind is DownloadTaskKind.NxmCollection or DownloadTaskKind.NexusCollection &&
            HasRealDownloadSource(task))
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

        // 所有可执行任务都必须有真实的下载或安装路由；缺少来源时明确失败，
        // 避免把未执行的任务误报为成功。
        task.CanRetry = false;
        task.CanCancel = false;
        task.SetState(DownloadTaskState.Failed, "缺少真实下载地址，无法执行");
        Status = $"任务失败: {task.Name}";
        TaskStateChanged?.Invoke(task);
        SaveTaskState();
        EmitLog($"任务失败，缺少真实下载地址: {task.Name}");
    }

    /// <summary>整合包安装任务执行：按 TaskKind 路由到 ModpackInstallService 的 SVL 或 Curseforge 流程。</summary>
    private Task ExecuteModpackInstallTaskAsync(DownloadTaskItem task)
    {
        return ExecuteModpackInstallTaskAsync(task, task.SourceUrl);
    }

    /// <summary>
    /// 从指定本地归档安装整合包。在线整合包在下载完成后，SourceUrl 仍是远端地址，
    /// 因此必须显式传入 OutputFilePath，不能让安装器再次把远端 URL 当成本地文件。
    /// </summary>
    private async Task ExecuteModpackInstallTaskAsync(
        DownloadTaskItem task,
        string archivePath)
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
            var zipPath = archivePath;
            if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
            {
                throw new FileNotFoundException("整合包归档不存在", zipPath);
            }

            var instanceName = string.IsNullOrWhiteSpace(task.TargetInstanceName)
                ? Path.GetFileNameWithoutExtension(zipPath)
                : task.TargetInstanceName;
            // 部分安装会保留已成功的 Mods，并把运行目录写入任务状态；重试时
            // 必须让 SMAPI 安装器走更新分支，否则 versions/<实例> 已存在会被拒绝。
            var updateExisting = HasPreviouslyInstalledModpackRuntime(task);

            ModpackInstallResult result;
            if (task.TaskKind == DownloadTaskKind.SvlModpack)
            {
                result = await _modpackInstallService.InstallSvlModpackAsync(
                    zipPath, instanceName, task.TargetGamePath,
                    progress => ApplyModpackInstallProgress(task, progress),
                    cts.Token,
                    customIconPath: task.CustomIconPath,
                    updateExisting: updateExisting);
            }
            else
            {
                result = await _modpackInstallService.InstallCurseforgeModpackAsync(
                    zipPath, instanceName, task.TargetGamePath,
                    progress => ApplyModpackInstallProgress(task, progress),
                    cts.Token,
                    customIconPath: task.CustomIconPath,
                    updateExisting: updateExisting);
            }

            if (result.IsSuccess)
            {
                task.InstalledPath = result.RuntimePath;
                task.InstalledDirectory = result.VersionRootPath;
                var hasFailedMods = result.FailedMods.Count > 0;
                // 部分完成仍有失败项，不能把任务条渲染成满格；只有所有 Mod
                // 都安装成功时才显示 100%。
                task.Progress = hasFailedMods ? 99 : 100;
                var failText = hasFailedMods
                    ? $"（{result.FailedMods.Count} 个 Mod 下载失败，可重试）"
                    : string.Empty;
                task.SetState(
                    hasFailedMods ? DownloadTaskState.Failed : DownloadTaskState.Completed,
                    hasFailedMods ? $"部分完成{failText}" : "已完成");
                task.CanRetry = hasFailedMods;
                Status = hasFailedMods
                    ? $"整合包安装部分完成: {task.Name}"
                    : $"整合包安装完成: {task.Name}";
                EmitLog($"整合包安装{(hasFailedMods ? "部分完成" : "完成")}: {task.Name}, 运行目录: {result.RuntimePath}, 安装 {result.InstalledMods.Count} 个, 失败 {result.FailedMods.Count} 个");
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
                task.FailedDetails = result.Message;
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
            task.FailedDetails = ex.Message;
            task.SetState(DownloadTaskState.Failed, $"安装失败（可重试）: {ex.Message}");
            task.CanRetry = true;
            Status = $"整合包安装失败: {task.Name}";
            EmitLog($"整合包安装异常: {task.Name}, 错误: {ex.Message}");
        }
        finally
        {
            cts.Dispose();
            _runningTaskCancellationSources.TryRemove(task, out _);
            task.CanCancel = false;
            TaskStateChanged?.Invoke(task);
            SaveTaskState();
        }
    }

    /// <summary>Collection 安装任务执行：调用 CollectionInstallService 从本地 7z 文件按 Phase 分阶段安装。</summary>
    private Task ExecuteCollectionInstallTaskAsync(DownloadTaskItem task)
    {
        return ExecuteCollectionInstallTaskAsync(task, task.SourceUrl);
    }

    /// <summary>
    /// 从指定本地归档安装 Collection。在线 URL 任务下载完成后，SourceUrl 仍保留
    /// 远端地址，因此安装阶段必须使用已落盘的 OutputFilePath。
    /// </summary>
    private async Task ExecuteCollectionInstallTaskAsync(
        DownloadTaskItem task,
        string archivePath)
    {
        task.ResetCollectionModProgress();
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
            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            {
                throw new FileNotFoundException("Collection 归档不存在", archivePath);
            }

            var instanceName = string.IsNullOrWhiteSpace(task.TargetInstanceName)
                ? Path.GetFileNameWithoutExtension(archivePath)
                : task.TargetInstanceName;
            var updateExisting = HasPreviouslyInstalledPackageRuntime(task);

            var result = await _collectionInstallService.InstallCollectionFromArchiveAsync(
                archivePath, instanceName,
                progress => ApplyCollectionInstallProgress(task, progress),
                cts.Token,
                gameBasePath: task.TargetGamePath,
                customIconPath: task.CustomIconPath,
                updateExisting: updateExisting);

            if (result.IsSuccess)
            {
                task.InstalledPath = result.RuntimePath;
                task.InstalledDirectory = result.VersionRootPath;
                var hasFailedMods = result.FailedMods.Count > 0;
                task.Progress = hasFailedMods ? 99 : 100;
                var failText = hasFailedMods
                    ? $"（{result.FailedMods.Count} 个 Mod 下载失败，可重试）"
                    : string.Empty;
                task.SetState(
                    hasFailedMods ? DownloadTaskState.Failed : DownloadTaskState.Completed,
                    hasFailedMods ? $"部分完成{failText}" : "已完成");
                task.CanRetry = hasFailedMods;
                Status = hasFailedMods
                    ? $"Collection 安装部分完成: {task.Name}"
                    : $"Collection 安装完成: {task.Name}";
                EmitLog($"Collection 安装{(hasFailedMods ? "部分完成" : "完成")}: {task.Name}, 运行目录: {result.RuntimePath}, 安装 {result.InstalledMods.Count} 个, 失败 {result.FailedMods.Count} 个");
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
                task.FailedDetails = result.Message;
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
            task.FailedDetails = ex.Message;
            task.SetState(DownloadTaskState.Failed, $"安装失败（可重试）: {ex.Message}");
            task.CanRetry = true;
            Status = $"Collection 安装失败: {task.Name}";
            EmitLog($"Collection 安装异常: {task.Name}, 错误: {ex.Message}");
        }
        finally
        {
            cts.Dispose();
            _runningTaskCancellationSources.TryRemove(task, out _);
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
        var downloadProgressEpoch = BeginDownloadProgressEpoch(task);

        try
        {
            // Nexus 下载缓存命中：直接用缓存文件，免重复下载/浏览器指引
            var fromCache = false;
            Func<string, bool>? sourceCacheValidator = task.TaskAction switch
            {
                DownloadTaskAction.InstallSmapi => ModpackInstallService.TryNormalizeSmapiArchive,
                DownloadTaskAction.InstallModpack => path => IsValidLocalPackageArchive(path, task.TaskKind),
                DownloadTaskAction.InstallCollection => IsValidCollectionArchive,
                // 另存为允许保存任意远程资源，不能按 Mod manifest 校验。
                // 之前这里复用了 InstallMod 的校验器，导致另存为 SMAPI
                // 通过下载后又被判定为“未通过文件校验”。
                DownloadTaskAction.SaveOnly => null,
                _ => ModpackInstallService.IsValidModArchiveFile
            };

            if (TryReuseExistingDownloadedArtifact(task))
            {
                task.Progress = 100;
                try
                {
                    var localSize = new FileInfo(task.OutputFilePath).Length;
                    task.TotalSizeText = FormatSize(localSize);
                    task.DownloadedSizeText = FormatSize(localSize);
                    task.SpeedText = "本地归档";
                    task.EtaText = string.Empty;
                }
                catch
                {
                    // 展示字段是增强信息，不能影响本地归档复用。
                }

                EmitLog($"命中任务已有完整归档，跳过网络下载: {task.OutputFilePath}");
                fromCache = true;
            }

            if (task.TaskKind == DownloadTaskKind.NxmMod &&
                task.SourceModId is long modId && task.SourceFileId is long fileId &&
                NexusDownloadCache.TryGet(
                    modId,
                    fileId,
                    out var cachedNexus,
                    sourceCacheValidator))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(task.OutputFilePath) ?? string.Empty);
                    File.Copy(cachedNexus, task.OutputFilePath, true);
                    task.Progress = 100;
                    EmitLog($"命中 Nexus 缓存，直接复制: {cachedNexus}");
                    fromCache = true;
                }
                catch (Exception) when (!IsNexusCacheSource(task.SourceUrl))
                {
                    fromCache = false;
                }
            }

            // CurseForge CDN 是短期/可变化的地址，稳定的 ProjectID/FileID
            // 缓存优先级高于重新请求 CDN。Generic 任务也要走这里，因为下载页
            // 与批量更新为了保留来源凭证并不使用 NxmMod TaskKind。
            if (!fromCache &&
                IsCurseforgeTask(task) &&
                task.SourceModId is long curseforgeProjectId &&
                task.SourceFileId is long curseforgeFileId &&
                CurseforgeDownloadCache.TryGet(
                    curseforgeProjectId,
                    curseforgeFileId,
                    out var cachedCurseforge,
                    sourceCacheValidator))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(task.OutputFilePath) ?? string.Empty);
                File.Copy(cachedCurseforge, task.OutputFilePath, true);
                task.Progress = 100;
                EmitLog($"命中 CurseForge 缓存，直接复制: {cachedCurseforge}");
                fromCache = true;
            }

            if (!fromCache)
            {
                if (IsNexusCacheSource(task.SourceUrl))
                {
                    throw new FileNotFoundException("Nexus 缓存文件已失效，无法完成下载", task.SourceUrl);
                }

                // 通用 URL 缓存默认只按文件存在判断，旧版本可能已经把 HTML
                // 错误页写进缓存。已知任务类型在下载层直接提供归档校验，
                // 这样坏缓存会被淘汰，新的错误响应也不会再次污染缓存。
                await DownloadTaskArtifactWithNexusRefreshAsync(
                    task,
                    sourceCacheValidator,
                    snapshot =>
                    {
                        var displayPercent = DownloadProgressCalculator.ToDisplayPercent(snapshot);
                        var downloadedMb = snapshot.DownloadedBytes / 1024d / 1024d;
                        var totalMb = snapshot.TotalBytes / 1024d / 1024d;
                        var speedMb = snapshot.BytesPerSecond / 1024d / 1024d;
                        // Percent 是观测值，不代表 DownloadAsync 已经完成；多线程分片
                        // 可能先把累计字节写满，再等待其它响应释放。状态文案也必须
                        // 与进度条遵守同一规则，不能出现“下载中 100.0%”。
                        // 状态文本必须复用进度条的整数值；否则会出现进度条为
                        // 99%，文案却显示 99.9%/100.0% 的视觉不一致。
                        var statusPercent = displayPercent;
                        var statusText = snapshot.TotalBytes > 0
                            ? $"下载中 {statusPercent}% ({downloadedMb:F1}/{totalMb:F1} MB, {speedMb:F1} MB/s)"
                            : $"下载中 ({downloadedMb:F1} MB, {speedMb:F1} MB/s)";
                        var segmentPercents = snapshot.SegmentPercents is { Length: > 1 } percents
                            ? percents.ToArray()
                            : null;

                        // HttpDownloadService 的回调来自分片线程；所有绑定属性、分片集合和
                        // 状态事件统一排到 UI 线程，避免进度条与列表同时重绘时发生竞争。
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (!IsCurrentDownloadProgressEpoch(task, downloadProgressEpoch))
                            {
                                return;
                            }

                            task.Progress = displayPercent;
                            task.SetState(DownloadTaskState.Downloading, statusText);
                            if (segmentPercents is { Length: > 1 })
                            {
                                task.SyncSegmentProgress(segmentPercents);
                            }

                            TaskStateChanged?.Invoke(task);
                        });
                    },
                    cts.Token,
                    log: msg => Dispatcher.UIThread.Post(() => EmitLog(msg)));

                // 下载完成写入 Nexus 缓存
                if (task.TaskKind == DownloadTaskKind.NxmMod &&
                    task.SourceModId is long saveModId && task.SourceFileId is long saveFileId)
                {
                    NexusDownloadCache.Save(
                        saveModId,
                        saveFileId,
                        task.OutputFilePath,
                        sourceCacheValidator);
                }

                if (IsCurseforgeTask(task) &&
                    task.SourceModId is long saveCurseforgeProjectId &&
                    task.SourceFileId is long saveCurseforgeFileId)
                {
                    CurseforgeDownloadCache.Save(
                        saveCurseforgeProjectId,
                        saveCurseforgeFileId,
                        task.OutputFilePath,
                        sourceCacheValidator);
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
            EndDownloadProgressEpoch(task, downloadProgressEpoch);
            await ClearTaskSegmentProgressAsync(task);
            cts.Dispose();
            _runningTaskCancellationSources.TryRemove(task, out _);
        }

        task.Progress = 100;
        task.CanCancel = false;

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

        // 手动 URL 导入在下载前不知道归档类型。下载完成后用清单识别真实类型，
        // 再转入对应安装器；否则 Generic 任务会错误地按普通 Mod 解压。
        if (task.TaskAction == DownloadTaskAction.InstallModpack &&
            task.TaskKind == DownloadTaskKind.Generic)
        {
            var detectedType = ModpackType.Unknown;
            string? detectionError = null;
            try
            {
                var detection = ModpackTypeDetector.Detect(task.OutputFilePath);
                detectedType = detection.Type;
                detectionError = detection.ErrorMessage;
                if (!string.IsNullOrWhiteSpace(detection.TempExtractPath))
                {
                    ModpackTypeDetector.CleanupTempDirectory(detection.TempExtractPath);
                }
            }
            catch (Exception ex)
            {
                detectionError = ex.Message;
            }

            if (detectedType == ModpackType.SVL || detectedType == ModpackType.Curseforge)
            {
                task.TaskKind = detectedType == ModpackType.SVL
                    ? DownloadTaskKind.SvlModpack
                    : DownloadTaskKind.CurseforgeModpack;
            }
            else if (detectedType == ModpackType.NexusCollection)
            {
                task.TaskKind = DownloadTaskKind.NexusCollection;
                task.TaskAction = DownloadTaskAction.InstallCollection;
                await ExecuteCollectionInstallTaskAsync(task, task.OutputFilePath);
                return;
            }
            else
            {
                var reason = string.IsNullOrWhiteSpace(detectionError)
                    ? "归档中未找到可识别的 modpack.json、manifest.json 或 collection.json"
                    : detectionError;
                task.SetState(DownloadTaskState.Failed, "无法识别整合包（可重试）");
                task.FailedDetails = reason;
                task.CanRetry = true;
                task.CanCancel = false;
                Status = $"整合包任务失败: {task.Name}";
                TaskStateChanged?.Invoke(task);
                SaveTaskState();
                EmitLog($"整合包识别失败: {task.Name}, 错误: {reason}");
                return;
            }
        }

        // 在线整合包已经下载到 OutputFilePath；此处不能继续落入普通 Mod 安装，
        // 否则会把 modpack.json/manifest.json 当成单个 Mod，表现为整合包安装失败。
        if (task.TaskAction == DownloadTaskAction.InstallModpack &&
            (task.TaskKind == DownloadTaskKind.SvlModpack ||
             task.TaskKind == DownloadTaskKind.CurseforgeModpack))
        {
            await ExecuteModpackInstallTaskAsync(task, task.OutputFilePath);
            return;
        }

        if (task.TaskAction == DownloadTaskAction.InstallSmapi)
        {
            // 下载阶段的 100% 只代表安装包已经落盘；SMAPI 安装仍未完成，
            // 不能让任务页在安装开始前继续显示满格。
            task.Progress = 0;
            task.SetState(DownloadTaskState.Installing, "安装中（SMAPI）");
            TaskStateChanged?.Invoke(task);
            EmitLog($"下载完成，开始安装 SMAPI: {task.OutputFilePath}");

            // 安装阶段使用新的 CTS（下载阶段的 cts 已在 finally 中 Dispose）
            var smapiCts = new CancellationTokenSource();
            _runningTaskCancellationSources[task] = smapiCts;
            SmapiInstallResult smapiResult;
            try
            {
                // 兼容旧任务状态：TargetGamePath 可能保存的是
                // versions/<实例> 或 versions/<实例>/game。SMAPI 安装器接收的
                // 必须是 Base 路径，否则重启后会再次创建嵌套的 versions 目录。
                var smapiBasePath = InstanceRuntimePathResolver.ResolveBasePath(task.TargetGamePath);
                if (string.IsNullOrWhiteSpace(smapiBasePath))
                {
                    smapiBasePath = task.TargetGamePath;
                }

                task.TargetGamePath = smapiBasePath;
                smapiResult = await _smapiInstallService.InstallFromZipAsync(
                    task.OutputFilePath,
                    smapiBasePath,
                    task.TargetInstanceName,
                    cancellationToken: smapiCts.Token,
                    logger: msg => EmitLog($"[SMAPI] {msg}"),
                    updateExisting: HasPreviouslyInstalledSmapiRuntime(task));
            }
            catch (Exception ex)
            {
                smapiResult = SmapiInstallResult.Failed($"SMAPI 安装异常: {ex.Message}");
            }
            finally
            {
                smapiCts.Dispose();
                _runningTaskCancellationSources.TryRemove(task, out _);
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

            // 写入 SMAPI 预设图标（Modded.png），与 VersionSettingsPageViewModel.ChangeSmapiVersionAsync 一致。
            // SMAPI 图标使用独立命名空间，不能复用 Base 原版的通用图标。
            var iconWritten = Services.InstanceIconResolver.TryWriteDefaultSmapiIcon(smapiResult.RuntimePath);
            var iconStorageDir = Services.InstanceIconResolver.ResolveStorageDirectory(smapiResult.RuntimePath);
            var iconFilePath = string.IsNullOrWhiteSpace(iconStorageDir)
                ? string.Empty
                : System.IO.Path.Combine(iconStorageDir, ".svl-instance-icon-smapi.png");
            var resolvedIconPath = Services.InstanceIconResolver.ResolveIconPath(smapiResult.RuntimePath, isSmapiInstance: true);
            var iconFileExists = !string.IsNullOrWhiteSpace(resolvedIconPath) && System.IO.File.Exists(resolvedIconPath);
            EmitLog($"SMAPI 图标写入: {(iconWritten ? "成功" : "失败/已存在")}, 路径={smapiResult.RuntimePath}, 文件存在={iconFileExists}, 实际位置={resolvedIconPath}, 预期位置={iconFilePath}");

            var settings = _settingsStore.Load();
            settings.PreferredInstancePath = smapiResult.RuntimePath;
            settings.InstanceName = task.TargetInstanceName;
            settings.PreferredLaunchMode = "SMAPI";
            _settingsStore.Save(settings);
            RefreshGamePathState();

            SaveTaskState();
            EmitLog($"SMAPI 安装完成: 实例={task.TargetInstanceName}, 路径={task.InstalledPath}");

            // 通知 MainWindowViewModel 刷新 LaunchPage/InstancesPage 的实例图标与状态
            // 解决：SMAPI 安装后图标仍显示 Vanilla.png 的问题（页面未刷新读取新写入的 .svl-instance-icon-smapi.png）
            // 必须 Dispatcher.UIThread.Post：安装流程经 SemaphoreSlim+Task.Run 后延续在线程池线程，
            // 非 UI 线程触发 PropertyChanged 不会传播到控件
            Dispatcher.UIThread.Post(() => InstanceContextChanged?.Invoke());
            return;
        }

        // Collection 安装：下载完成后，使用 CollectionInstallService 从本地压缩包按 Phase 分阶段安装
        if (task.TaskAction == DownloadTaskAction.InstallCollection)
        {
            // Collection 的下载与多阶段安装是两个阶段；进入安装时重新从 0
            // 计算安装进度，避免下载完成的 100% 覆盖后续安装状态。
            task.Progress = 0;
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
                var updateExisting = HasPreviouslyInstalledPackageRuntime(task);

                var collectionResult = await _collectionInstallService.InstallCollectionFromArchiveAsync(
                    task.OutputFilePath,
                    collectionInstanceName,
                    progress => ApplyCollectionInstallProgress(task, progress),
                    collectionCts.Token,
                    gameBasePath: task.TargetGamePath,
                    customIconPath: task.CustomIconPath,
                    updateExisting: updateExisting);

                if (collectionResult.IsSuccess)
                {
                    task.InstalledPath = collectionResult.RuntimePath;
                    task.InstalledDirectory = collectionResult.VersionRootPath;
                    var hasFailedMods = collectionResult.FailedMods.Count > 0;
                    task.Progress = hasFailedMods ? 99 : 100;
                    var failText = hasFailedMods
                        ? $"（{collectionResult.FailedMods.Count} 个 Mod 下载失败，可重试）"
                        : string.Empty;
                    task.SetState(
                        hasFailedMods ? DownloadTaskState.Failed : DownloadTaskState.Completed,
                        hasFailedMods ? $"部分完成{failText}" : "已完成");
                    task.CanRetry = hasFailedMods;
                    Status = hasFailedMods
                        ? $"Collection 安装部分完成: {task.Name}"
                        : $"Collection 安装完成: {task.Name}";
                    EmitLog($"Collection 安装{(hasFailedMods ? "部分完成" : "完成")}: {task.Name}, 运行目录: {collectionResult.RuntimePath}, 安装 {collectionResult.InstalledMods.Count} 个, 失败 {collectionResult.FailedMods.Count} 个");
                    if (collectionResult.FailedMods.Count > 0)
                    {
                        task.FailedDetails = string.Join("\n", collectionResult.FailedMods);
                        EmitLog($"失败 Mod 列表:\n{task.FailedDetails}");
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
                    task.FailedDetails = collectionResult.Message;
                    task.CanRetry = true;
                    Status = $"Collection 安装失败: {task.Name}";
                    EmitLog($"Collection 安装失败: {task.Name}, 错误: {collectionResult.Message}");
                }
            }
            catch (Exception ex)
            {
                task.FailedDetails = ex.Message;
                task.SetState(DownloadTaskState.Failed, $"安装异常: {ex.Message}");
                task.CanRetry = true;
                Status = $"Collection 安装失败: {task.Name}";
                EmitLog($"Collection 安装异常: {task.Name}, 错误: {ex.Message}");
            }
            finally
            {
                collectionCts.Dispose();
                _runningTaskCancellationSources.TryRemove(task, out _);
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
            // 同上：这里只是下载包落盘，整合包清单、SMAPI、Mod 和 overrides
            // 仍需安装，安装回调会从阶段进度重新驱动进度条。
            task.Progress = 0;
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
                // 下载阶段完成后若安装只部分成功，OutputFilePath 仍可复用，且
                // InstalledPath 标记了已有版本；重试必须更新该实例而非新建同名实例。
                var updateExisting = HasPreviouslyInstalledModpackRuntime(task);

                ModpackInstallResult modpackResult;
                if (task.TaskKind == DownloadTaskKind.SvlModpack)
                {
                    modpackResult = await _modpackInstallService.InstallSvlModpackAsync(
                        task.OutputFilePath, modpackInstanceName, task.TargetGamePath,
                        progress => ApplyModpackInstallProgress(task, progress),
                    modpackCts.Token,
                    customIconPath: task.CustomIconPath,
                    updateExisting: updateExisting);
                }
                else
                {
                    modpackResult = await _modpackInstallService.InstallCurseforgeModpackAsync(
                        task.OutputFilePath, modpackInstanceName, task.TargetGamePath,
                        progress => ApplyModpackInstallProgress(task, progress),
                        modpackCts.Token,
                        customIconPath: task.CustomIconPath,
                        updateExisting: updateExisting);
                }

                if (modpackResult.IsSuccess)
                {
                    task.InstalledPath = modpackResult.RuntimePath;
                    task.InstalledDirectory = modpackResult.VersionRootPath;
                    var hasFailedMods = modpackResult.FailedMods.Count > 0;
                    task.Progress = hasFailedMods ? 99 : 100;
                    var failText = hasFailedMods
                        ? $"（{modpackResult.FailedMods.Count} 个 Mod 下载失败，可重试）"
                        : string.Empty;
                    task.SetState(
                        hasFailedMods ? DownloadTaskState.Failed : DownloadTaskState.Completed,
                        hasFailedMods ? $"部分完成{failText}" : "已完成");
                    task.CanRetry = hasFailedMods;
                    Status = hasFailedMods
                        ? $"整合包安装部分完成: {task.Name}"
                        : $"整合包安装完成: {task.Name}";
                    EmitLog($"整合包安装{(hasFailedMods ? "部分完成" : "完成")}: {task.Name}, 运行目录: {modpackResult.RuntimePath}, 安装 {modpackResult.InstalledMods.Count} 个, 失败 {modpackResult.FailedMods.Count} 个");
                    if (modpackResult.FailedMods.Count > 0)
                    {
                        task.FailedDetails = string.Join("\n", modpackResult.FailedMods);
                        EmitLog($"失败 Mod 列表:\n{task.FailedDetails}");
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
                    task.FailedDetails = modpackResult.Message;
                    task.CanRetry = true;
                    Status = $"整合包安装失败: {task.Name}";
                    EmitLog($"整合包安装失败: {task.Name}, 错误: {modpackResult.Message}");
                }
            }
            catch (Exception ex)
            {
                task.FailedDetails = ex.Message;
                task.SetState(DownloadTaskState.Failed, $"安装异常: {ex.Message}");
                task.CanRetry = true;
                Status = $"整合包安装失败: {task.Name}";
                EmitLog($"整合包安装异常: {task.Name}, 错误: {ex.Message}");
            }
            finally
            {
                modpackCts.Dispose();
                _runningTaskCancellationSources.TryRemove(task, out _);
            }

            TaskStateChanged?.Invoke(task);
            SaveTaskState();
            return;
        }

        // 普通 Mod 同样要区分“下载完成”和“安装完成”。
        if (task.SkipConflictPrompt)
        {
            var backupResult = await BackupExistingModsBeforeInstallAsync(task);
            if (!backupResult.Success)
            {
                task.FailedDetails = backupResult.ErrorMessage;
                task.SetState(DownloadTaskState.Failed, "覆盖前备份失败（可重试）");
                task.CanRetry = true;
                task.CanCancel = false;
                Status = $"任务失败: {task.Name}";
                TaskStateChanged?.Invoke(task);
                SaveTaskState();
                EmitLog($"批量更新未覆盖 {task.Name}: {backupResult.ErrorMessage}");
                return;
            }
        }
        else if (!await ConfirmModOverwriteBeforeInstallAsync(task))
        {
            task.SetState(DownloadTaskState.Cancelled, "已取消覆盖安装");
            task.CanRetry = false;
            task.CanCancel = false;
            Status = $"已取消 Mod 安装: {task.Name}";
            TaskStateChanged?.Invoke(task);
            SaveTaskState();
            EmitLog($"已取消 Mod 覆盖安装: {task.Name}");
            return;
        }

        task.Progress = 0;
        task.SetState(DownloadTaskState.Installing, "安装中");
        TaskStateChanged?.Invoke(task);
        EmitLog($"下载完成，进入安装阶段: {task.OutputFilePath}");

        var installResult = await _downloadInstallService.InstallAsync(
            task.OutputFilePath,
            task.Name,
            sourcePlatform: task.SourcePlatform,
            sourceProjectId: task.SourceModId,
            sourceFileId: task.SourceFileId,
            sourceDownloadUrl: task.SourceUrl,
            sourceFileName: Path.GetFileName(task.OutputFilePath),
            sourceRepository: task.SourceRepository);
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
        // 更新任务可能替换了一个包含多个子 Mod 的复合目录。通知外层刷新，
        // 让列表从磁盘重新读取新的 manifest/source，而不是继续显示旧对象状态。
        Dispatcher.UIThread.Post(() => ModInstallationCompleted?.Invoke());
    }

    private async Task ExecuteCollectionTaskAsync(DownloadTaskItem task)
    {
        // 兼容早期版本落盘的 NxmCollection 任务：当时只保存了 slug/revision，
        // 没有保存短期 CDN 地址。不能把这种任务直接终止，应先复用稳定缓存，
        // 再通过 API 重新换取地址，最后才使用浏览器回退。
        if (string.IsNullOrWhiteSpace(task.CollectionSlug))
        {
            task.CanRetry = true;
            task.CanCancel = false;
            task.FailedDetails = "历史 Collection 任务缺少 Collection slug，请重新导入";
            task.SetState(DownloadTaskState.Failed, "Collection 缺少必要信息（可重试）");
            Status = $"Collection 任务失败: {task.Name}";
            TaskStateChanged?.Invoke(task);
            SaveTaskState();
            EmitLog($"Collection 任务失败，缺少 slug: {task.Name}");
            return;
        }

        if (NexusCollectionDownloadCache.TryGet(
                "stardewvalley",
                task.CollectionSlug,
                task.CollectionRevision,
                out var cachedPath,
                IsValidCollectionArchive))
        {
            task.OutputFilePath = cachedPath;
            task.SetState(DownloadTaskState.Installing, "命中 Collection 缓存，准备安装");
            task.Progress = 0;
            task.CanRetry = false;
            task.CanCancel = false;
            TaskStateChanged?.Invoke(task);
            EmitLog($"历史 Collection 任务命中稳定缓存，跳过地址解析: {cachedPath}");
            await ExecuteCollectionInstallTaskAsync(task, cachedPath);
            return;
        }

        task.CanRetry = false;
        // 地址解析与浏览器回退阶段尚未创建下载 CTS，不能向 UI 暴露一个
        // 实际无法取消的按钮；进入真实下载/安装方法后再开启取消。
        task.CanCancel = false;
        task.SetState(DownloadTaskState.Resolving, "重新解析 Collection 下载地址");
        task.Progress = 5;
        Status = $"正在恢复 Collection 任务: {task.Name}";
        TaskStateChanged?.Invoke(task);
        EmitLog($"恢复历史 Collection 任务，重新解析地址: {task.CollectionSlug} rev {task.CollectionRevision}");

        var settings = _settingsStore.Load();
        var collectionInfo = new NxmLinkInfo
        {
            GameDomain = "stardewvalley",
            ResourceType = NxmResourceType.Collection,
            CollectionSlug = task.CollectionSlug.Trim(),
            RevisionNumber = task.CollectionRevision
        };
        var resolved = await _nexusModDownloadResolverService.ResolveCollectionDownloadUrlAsync(
            collectionInfo,
            settings.NexusApiKey,
            settings.NexusOAuthAccessToken);

        if (!resolved.IsSuccess)
        {
            // 没有账号凭据或 API 失败时，复用正常导入流程的浏览器回退。
            // 回退成功后得到的 NXM key 会在 resolver 中作为一次性凭据使用。
            EmitLog($"历史 Collection API 解析失败，尝试浏览器回退: {resolved.Message}");
            var browserUrl = $"https://next.nexusmods.com/stardewvalley/collections/{task.CollectionSlug}";
            var callback = await TryCollectionBrowserDownloadFallbackAsync(
                task.CollectionSlug,
                task.CollectionRevision,
                browserUrl,
                "恢复 Collection 任务");

            if (!string.IsNullOrWhiteSpace(callback) &&
                _nxmLinkParser.TryParse(callback, out var callbackInfo, out _))
            {
                resolved = await _nexusModDownloadResolverService.ResolveCollectionDownloadUrlAsync(
                    callbackInfo,
                    settings.NexusApiKey,
                    settings.NexusOAuthAccessToken);
            }
        }

        if (!resolved.IsSuccess ||
            !Uri.TryCreate(resolved.DownloadUrl, UriKind.Absolute, out var resolvedUri) ||
            !IsHttpUri(resolvedUri))
        {
            task.CanRetry = true;
            task.CanCancel = false;
            task.FailedDetails = string.IsNullOrWhiteSpace(resolved.Message)
                ? "未解析到有效 Collection 下载地址"
                : resolved.Message;
            task.SetState(DownloadTaskState.Failed, "Collection 地址解析失败（可重试）");
            Status = $"Collection 任务失败: {task.Name}";
            TaskStateChanged?.Invoke(task);
            SaveTaskState();
            EmitLog($"历史 Collection 任务地址解析失败: {task.FailedDetails}");
            return;
        }

        task.SourceUrl = resolved.DownloadUrl;
        task.DependencyUrls = resolved.DownloadUrls
            .Where(url => !string.Equals(url, task.SourceUrl, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        task.OutputFilePath = Path.Combine(
            _downloadRootPath,
            CreateSafeFileName(ResolveDownloadFileName(resolvedUri, resolved.FileName)));
        task.Name = string.IsNullOrWhiteSpace(task.Name)
            ? $"Nexus Collection {task.CollectionSlug}"
            : task.Name;
        EmitLog($"历史 Collection 任务已恢复真实地址: {task.OutputFilePath}");

        await ExecuteCollectionRealDownloadTaskAsync(task);
    }

    private async Task ExecuteCollectionRealDownloadTaskAsync(DownloadTaskItem task)
    {
        task.CanRetry = false;
        task.CanCancel = false;
        task.Progress = 0;
        await ClearTaskSegmentProgressAsync(task);
        var retryFailedBefore = task.FailedDownloadUrls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var candidateUrls = BuildCollectionDownloadUrls(task);
        var stableCollectionPath = string.Empty;
        var hasStableCollectionCache =
            !string.IsNullOrWhiteSpace(task.CollectionSlug) &&
            NexusCollectionDownloadCache.TryGet(
                "stardewvalley",
                task.CollectionSlug,
                task.CollectionRevision,
                out stableCollectionPath,
                IsValidCollectionArchive);
        task.FailedDownloadUrls.Clear();
        task.FailedDetails = string.Empty;

        task.SetState(DownloadTaskState.Resolving, "准备 Collection 清单");
        Status = $"正在准备 Collection 清单: {task.Name}";
        TaskStateChanged?.Invoke(task);
        EmitLog($"Collection 准备清单: {task.Name}");
        task.Progress = 10;
        TaskStateChanged?.Invoke(task);

        if (candidateUrls.Count == 0 && !hasStableCollectionCache)
        {
            task.SetState(DownloadTaskState.Failed, "缺少 Collection 下载地址（可重试）");
            task.FailedDetails = "未找到 Collection 压缩包下载地址";
            task.CanRetry = true;
            Status = $"Collection 任务失败: {task.Name}";
            TaskStateChanged?.Invoke(task);
            SaveTaskState();
            EmitLog($"Collection 任务失败，缺少下载地址: {task.Name}");
            return;
        }

        var cts = new CancellationTokenSource();
        _runningTaskCancellationSources[task] = cts;
        var downloadProgressEpoch = BeginDownloadProgressEpoch(task);
        try
        {
            task.CanCancel = true;
            task.SetState(DownloadTaskState.Downloading, "下载 Collection 压缩包");
            Status = $"正在下载 Collection: {task.Name}";
            task.SubProgress = 0;
            task.SubProgressText = $"准备下载 Collection 包（{candidateUrls.Count} 个镜像）";
            TaskStateChanged?.Invoke(task);
            EmitLog($"Collection 开始真实下载，共 {candidateUrls.Count} 个候选镜像（仅下载一个 Collection 包）");

            string? archivePath = null;
            var failures = new List<string>();
            var failedUrls = new List<string>();

            // 同一个 Collection 任务可能因安装阶段失败、应用重启或重复导入
            // 再次执行。只要 OutputFilePath 已经是有效 Collection 归档，
            // 就跳过浏览器/CDN 下载，直接进入安装阶段。
            if (!string.IsNullOrWhiteSpace(task.OutputFilePath) &&
                IsValidCollectionArchive(task.OutputFilePath))
            {
                archivePath = task.OutputFilePath;
                task.Progress = 85;
                task.SubProgress = 100;
                task.SubProgressText = "命中已下载 Collection 归档，跳过下载";
                TaskStateChanged?.Invoke(task);
                EmitLog($"Collection 命中本地归档，跳过下载: {archivePath}");
            }

            if (archivePath == null && hasStableCollectionCache)
            {
                archivePath = stableCollectionPath;
                task.OutputFilePath = stableCollectionPath;
                task.Progress = 85;
                task.SubProgress = 100;
                task.SubProgressText = "命中稳定 Collection 缓存，跳过下载";
                TaskStateChanged?.Invoke(task);
                EmitLog($"Collection 命中稳定缓存，跳过下载: {archivePath}");
            }

            for (var index = 0; archivePath == null && index < candidateUrls.Count; index++)
            {
                cts.Token.ThrowIfCancellationRequested();
                var url = candidateUrls[index];
                var target = ResolveCollectionPartPath(task, url, index + 1);
                task.SetState(DownloadTaskState.Downloading, $"下载 Collection 压缩包（镜像 {index + 1}/{candidateUrls.Count}）");
                task.SubProgressText = $"镜像 {index + 1}/{candidateUrls.Count}";
                TaskStateChanged?.Invoke(task);

                try
                {
                    await _httpDownloadService.DownloadAsync(
                        url,
                        target,
                        snapshot =>
                        {
                            var percent = Math.Clamp(snapshot.Percent, 0, 100);
                            var displayProgress = (int)Math.Round(Math.Min(85, 20 + percent * 0.65));
                            // DownloadAsync 返回前，最后一次回调仍然只是观测值；
                            // 因此下载阶段最多显示 99%，真正完成由 await 返回确认。
                            var subProgress = DownloadProgressCalculator.ToDisplayPercent(snapshot);
                            var downloadedMb = snapshot.DownloadedBytes / 1024d / 1024d;
                            var totalMb = snapshot.TotalBytes / 1024d / 1024d;
                            var speedMb = snapshot.BytesPerSecond / 1024d / 1024d;
                            var statusPercent = subProgress;
                            var statusText = snapshot.TotalBytes > 0
                                ? $"下载 Collection {statusPercent}% ({downloadedMb:F1}/{totalMb:F1} MB, {speedMb:F1} MB/s)"
                                : $"下载 Collection ({downloadedMb:F1} MB, {speedMb:F1} MB/s)";

                            Dispatcher.UIThread.Post(() =>
                            {
                                if (!IsCurrentDownloadProgressEpoch(task, downloadProgressEpoch))
                                {
                                    return;
                                }

                                task.Progress = displayProgress;
                                task.SubProgress = subProgress;
                                task.SubProgressText = $"镜像 {index + 1}/{candidateUrls.Count}: {statusPercent}%";
                                task.SetState(DownloadTaskState.Downloading, statusText);
                                TaskStateChanged?.Invoke(task);
                            });
                        },
                        cts.Token,
                        cacheValidator: IsValidCollectionArchive);

                    // 只有 DownloadAsync 正常返回，才确认压缩包完整可用。
                    archivePath = target;
                    task.OutputFilePath = target;
                    if (!string.IsNullOrWhiteSpace(task.CollectionSlug))
                    {
                        NexusCollectionDownloadCache.Save(
                            "stardewvalley",
                            task.CollectionSlug,
                            task.CollectionRevision,
                            target,
                            IsValidCollectionArchive);
                    }
                    task.Progress = 85;
                    task.SubProgress = 100;
                    task.SubProgressText = "Collection 压缩包下载完成";
                    TaskStateChanged?.Invoke(task);
                    EmitLog($"Collection 压缩包下载完成，使用镜像 {index + 1}/{candidateUrls.Count}: {target}");
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failures.Add($"镜像 {index + 1}: {ex.Message}");
                    failedUrls.Add(url);
                    try
                    {
                        if (File.Exists(target))
                        {
                            File.Delete(target);
                        }
                    }
                    catch
                    {
                        // 下载失败时清理临时文件失败不应覆盖原始网络错误。
                    }

                    EmitLog($"Collection 镜像 {index + 1}/{candidateUrls.Count} 下载失败: {ex.Message}");
                }
            }

            if (string.IsNullOrWhiteSpace(archivePath))
            {
                task.FailedDownloadUrls = failedUrls
                    .Where(url => !string.IsNullOrWhiteSpace(url))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var failedPreview = task.FailedDownloadUrls
                    .Take(5)
                    .Select((url, idx) => $"{idx + 1}. {url}");
                task.FailedDetails = $"Collection 压缩包下载失败:\n{string.Join("\n", failedPreview)}\n{string.Join("; ", failures.Take(3))}";
                throw new InvalidOperationException($"Collection 压缩包下载失败: {string.Join("; ", failures.Take(3))}");
            }

            // 下载阶段结束后立刻失效所有排队中的分片回调，避免它们在安装阶段
            // 把任务状态改回“下载中”。
            EndDownloadProgressEpoch(task, downloadProgressEpoch);
            await ClearTaskSegmentProgressAsync(task);
            task.CanCancel = true;
            task.SetState(DownloadTaskState.Installing, "解析并安装 Collection");
            task.Progress = 90;
            task.SubProgressText = "读取 collection.json 并下载安装 Mod";
            TaskStateChanged?.Invoke(task);
            EmitLog($"Collection 安装开始: {archivePath}");

            var collectionInstanceName = string.IsNullOrWhiteSpace(task.TargetInstanceName)
                ? Path.GetFileNameWithoutExtension(archivePath)
                : task.TargetInstanceName;
            var updateExisting = HasPreviouslyInstalledPackageRuntime(task);
            task.ResetCollectionModProgress();
            var collectionResult = await _collectionInstallService.InstallCollectionFromArchiveAsync(
                archivePath,
                collectionInstanceName,
                progress => ApplyCollectionInstallProgress(task, progress),
                cts.Token,
                gameBasePath: task.TargetGamePath,
                customIconPath: task.CustomIconPath,
                updateExisting: updateExisting);

            if (collectionResult.IsSuccess)
            {
                task.InstalledPath = collectionResult.RuntimePath;
                task.InstalledDirectory = collectionResult.VersionRootPath;
                var hasFailedMods = collectionResult.FailedMods.Count > 0;
                task.Progress = hasFailedMods ? 99 : 100;
                task.FailedDetails = hasFailedMods
                    ? string.Join("\n", collectionResult.FailedMods)
                    : string.Empty;
                task.FailedDownloadUrls.Clear();
                task.SetState(
                    hasFailedMods ? DownloadTaskState.Failed : DownloadTaskState.Completed,
                    hasFailedMods
                        ? $"部分完成（{collectionResult.FailedMods.Count} 个 Mod 下载失败，可重试）"
                        : "已完成");
                task.CanRetry = hasFailedMods;
                Status = hasFailedMods
                    ? $"Collection 安装部分完成: {task.Name}"
                    : $"Collection 安装完成: {task.Name}";
                EmitLog($"Collection 安装{(hasFailedMods ? "部分完成" : "完成")}: {task.Name}, 运行目录: {collectionResult.RuntimePath}, 安装 {collectionResult.InstalledMods.Count} 个, 失败 {collectionResult.FailedMods.Count} 个");
                if (hasFailedMods)
                {
                    EmitLog($"失败 Mod 列表:\n{task.FailedDetails}");
                }

                Dispatcher.UIThread.Post(() => InstanceContextChanged?.Invoke());
            }
            else if (collectionResult.IsCancelled)
            {
                task.SetState(DownloadTaskState.Cancelled, "Collection 安装已取消");
                task.CanRetry = true;
                Status = $"Collection 安装已取消: {task.Name}";
                EmitLog($"Collection 安装取消: {task.Name}");
            }
            else
            {
                task.SetState(DownloadTaskState.Failed, $"安装失败（可重试）: {collectionResult.Message}");
                task.FailedDetails = collectionResult.Message;
                task.CanRetry = true;
                Status = $"Collection 安装失败: {task.Name}";
                EmitLog($"Collection 安装失败: {task.Name}, 错误: {collectionResult.Message}");
            }

            var retrySuccessReport = _retryDiffReportService.Write(_downloadRootPath, task.Name, retryFailedBefore, task.FailedDownloadUrls);
            if (!string.IsNullOrWhiteSpace(retrySuccessReport))
            {
                task.RetryReportPath = retrySuccessReport;
                EmitLog($"重试对比报告: {retrySuccessReport}");
            }
        }
        catch (OperationCanceledException)
        {
            task.SetState(DownloadTaskState.Cancelled, "已取消");
            task.CanRetry = true;
            task.CanCancel = false;
            Status = $"Collection 任务已取消: {task.Name}";
            TaskStateChanged?.Invoke(task);
            SaveTaskState();
            EmitLog($"Collection 任务取消: {task.Name}");
        }
        catch (Exception ex)
        {
            var retryReport = _retryDiffReportService.Write(_downloadRootPath, task.Name, retryFailedBefore, task.FailedDownloadUrls);
            if (!string.IsNullOrWhiteSpace(retryReport))
            {
                task.RetryReportPath = retryReport;
                EmitLog($"重试对比报告: {retryReport}");
            }

            task.SetState(DownloadTaskState.Failed, "Collection 下载/安装失败（可重试）");
            task.CanRetry = true;
            task.CanCancel = false;
            if (string.IsNullOrWhiteSpace(task.FailedDetails))
            {
                task.FailedDetails = ex.Message;
            }
            Status = $"Collection 任务失败: {task.Name}";
            TaskStateChanged?.Invoke(task);
            SaveTaskState();
            EmitLog($"Collection 任务失败: {task.Name}, 错误: {ex.Message}");
        }
        finally
        {
            EndDownloadProgressEpoch(task, downloadProgressEpoch);
            await ClearTaskSegmentProgressAsync(task);
            task.CanCancel = false;
            TaskStateChanged?.Invoke(task);
            SaveTaskState();
            cts.Dispose();
            _runningTaskCancellationSources.TryRemove(task, out _);
        }
    }

    private static int CalculateCollectionFileCompletionPercent(int completedFiles, int totalFiles)
    {
        if (totalFiles <= 0)
        {
            return -1;
        }

        if (completedFiles >= totalFiles)
        {
            return 100;
        }

        // 下载中的文件即使字节进度接近 100%，也不能让“文件完成数”提前显示满格。
        return Math.Clamp((int)Math.Floor(Math.Max(0, completedFiles) * 100d / totalFiles), 0, 99);
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

    /// <summary>校验本地文件是否确实是可识别的 Nexus Collection 归档。</summary>
    private static bool IsValidCollectionArchive(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            return false;
        }

        try
        {
            var detection = ModpackTypeDetector.Detect(archivePath);
            if (!string.IsNullOrWhiteSpace(detection.TempExtractPath))
            {
                ModpackTypeDetector.CleanupTempDirectory(detection.TempExtractPath);
            }

            return detection.Type == ModpackType.NexusCollection;
        }
        catch
        {
            return false;
        }
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

    private static string NormalizeHttpDownloadUrl(string value)
    {
        if (Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && IsHttpUri(uri))
        {
            // AbsoluteUri 会把文件名中的空格等字符正确编码，避免日志里看似
            // 完整的 Edge CDN 地址在 HttpClient 解析时被截断或解释成非法 URI。
            return uri.AbsoluteUri;
        }

        return value?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// 判断 Nexus 来源的 URL 是否足够像真实归档直链。
    /// Nexus 文件页也会被保存为 downloadUrl，因此不能仅凭 HTTP scheme 放行；
    /// 已知 CDN、归档扩展名或非 Nexus 平台的 /download 地址才允许跳过 API/浏览器回退。
    /// </summary>
    private static bool IsLikelyNexusDirectDownloadUrl(Uri uri)
    {
        if (!IsHttpUri(uri))
        {
            return false;
        }

        var host = uri.Host.Trim().TrimEnd('.');
        var path = uri.AbsolutePath;
        var looksLikeArchive = path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".rar", StringComparison.OrdinalIgnoreCase);
        if (looksLikeArchive)
        {
            return !IsNexusPageHost(host);
        }

        if (host.Equals("file-metadata.nexusmods.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("staticdelivery.nexusmods.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("cdn.nexusmods.com", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !IsNexusHost(host) &&
               path.Contains("/download", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNexusPageHost(string host)
    {
        return host.Equals("nexusmods.com", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("www.nexusmods.com", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("next.nexusmods.com", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("api.nexusmods.com", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("users.nexusmods.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNexusHost(string host)
    {
        return host.Equals("nexusmods.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".nexusmods.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断 CurseForge 来源是否已经是可下载地址。
    ///
    /// CurseForge 的文件页、/download 页面以及 curse.tools API 都是网页/API，
    /// 不能直接交给下载器，否则很容易把 HTML 当成 Mod 压缩包保存。优先使用
    /// projectId/fileId 重新解析 CDN 地址；只有 CDN 或明确归档扩展名才放行。
    /// </summary>
    private static bool IsLikelyCurseforgeDirectDownloadUrl(string? value)
    {
        return DownloadUrlPolicy.IsLikelyCurseforgeDirectDownloadUrl(value);
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

    /// <summary>
    /// 安装服务会在并发下载线程中汇报进度。Avalonia 的 ObservableProperty 和
    /// ObservableCollection 只能在 UI 线程安全更新，因此统一在这里切回 UI 线程。
    /// 同时安装阶段最多显示 99%；最终的 100% 由安装结果确认后设置，避免服务的
    /// 最后一条进度回调排在 Completed 状态之后执行而把进度条重新填满/回退。
    /// </summary>
    private void ApplyModpackInstallProgress(DownloadTaskItem task, ModpackInstallProgress progress)
    {
        void Apply()
        {
            // 若最终结果已经到达，丢弃排队中晚到的进度回调，避免 Completed/Failed
            // 被旧回调覆盖。重试会重新进入 Installing 后接收新回调。
            if (task.TaskState != DownloadTaskState.Installing)
            {
                return;
            }

            task.Progress = Math.Clamp(progress.Percent, 0, 99);
            task.SetState(DownloadTaskState.Installing, progress.StepText);
            task.SubProgressText = progress.SubProgressText;
            task.SubProgress = progress.SubProgress;
            EmitModpackProgress(task.Name, progress.StepText, progress.SubProgressText);
            TaskStateChanged?.Invoke(task);
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Apply();
        }
        else
        {
            Dispatcher.UIThread.Post(Apply);
        }
    }

    private void ApplyCollectionInstallProgress(DownloadTaskItem task, CollectionInstallProgress progress)
    {
        void Apply()
        {
            if (task.TaskState != DownloadTaskState.Installing)
            {
                return;
            }

            if (progress.ModState is CollectionModTaskState modState &&
                !string.IsNullOrWhiteSpace(progress.ModName))
            {
                task.SyncCollectionModProgress(
                    progress.ModName,
                    progress.ModPhase,
                    progress.ModOptional,
                    modState,
                    progress.ModMessage,
                    progress.ModSourceUrl,
                    progress.ModRequiresManualAction);
                // 单个 Mod 状态变化是低频事件；立即落盘，进程中断后任务页仍能
                // 恢复到最后一个已处理条目，而不是只保留整合包总进度。
                SaveTaskState();
            }

            if (!string.IsNullOrWhiteSpace(progress.StepText))
            {
                task.Progress = Math.Clamp(progress.Percent, 0, 99);
                task.SetState(DownloadTaskState.Installing, progress.StepText);
                task.SubProgressText = progress.SubProgressText;
                task.SubProgress = progress.SubProgress;
                EmitModpackProgress(task.Name, progress.StepText, progress.SubProgressText);
            }
            TaskStateChanged?.Invoke(task);
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Apply();
        }
        else
        {
            Dispatcher.UIThread.Post(Apply);
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

    private async Task<bool> ConfirmModOverwriteBeforeInstallAsync(DownloadTaskItem task)
    {
        var modsPath = GetCurrentModsPath();
        var existingPaths = await GetExistingModPathsForInstallAsync(task, modsPath);
        if (existingPaths.Count == 0)
        {
            return true;
        }

        var summary = $"检测到 {existingPaths.Count} 个现有 Mod 将被覆盖：\n" +
                      string.Join("\n", existingPaths.Select(path => $"• {Path.GetFileName(path)}"));
        var resolution = await ShowConflictResolutionOnUiThreadAsync(
            "安装 Mod 时发现更新/冲突",
            "当前下载包会替换已有 Mod。是否覆盖？系统会在覆盖前自动备份原有 Mod。",
            task.OutputFilePath,
            existingPaths[0],
            summary,
            "打开待安装文件",
            "打开原有 Mod 文件夹",
            "覆盖（先备份原有 Mod）");
        if (resolution != ConflictResolutionDialogAction.Replace)
        {
            return false;
        }

        var replacementFolderNames = await GetReplacementFolderNamesForUpdateAsync(task, existingPaths);
        var backupResult = await BackupExistingModPathsAsync(
            modsPath,
            existingPaths,
            replacementFolderNames,
            task.Name);
        if (!backupResult.Success)
        {
            var failedName = backupResult.FailedPath == null
                ? "未知 Mod"
                : Path.GetFileName(backupResult.FailedPath);
            await ShowMessageOnUiThreadAsync(
                "无法覆盖 Mod",
                $"原有 Mod“{failedName}”备份失败，已取消本次安装。请检查目录权限后重试。");
            return false;
        }

        task.BackupPath = backupResult.BackupPath ?? string.Empty;
        return true;
    }

    private async Task<List<string>> GetExistingModPathsForInstallAsync(
        DownloadTaskItem task,
        string? modsPath = null)
    {
        modsPath ??= GetCurrentModsPath();
        if (string.IsNullOrWhiteSpace(modsPath) || !Directory.Exists(modsPath))
        {
            return [];
        }

        var targetNames = await Task.Run(() =>
            ModpackInstallService.GetInstallTargetNames(task.OutputFilePath, task.Name, modsPath));
        return targetNames
            .Select(name => Path.Combine(modsPath, name))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<(bool Success, string ErrorMessage, string? FailedPath, string? BackupPath)> BackupExistingModsBeforeInstallAsync(
        DownloadTaskItem task)
    {
        var modsPath = GetCurrentModsPath();
        var existingPaths = await GetExistingModPathsForInstallAsync(task, modsPath);
        if (existingPaths.Count == 0)
        {
            return (true, string.Empty, null, null);
        }

        var replacementFolderNames = await GetReplacementFolderNamesForUpdateAsync(task, existingPaths);
        var result = await BackupExistingModPathsAsync(
            modsPath,
            existingPaths,
            replacementFolderNames,
            task.Name);
        if (result.Success)
        {
            task.BackupPath = result.BackupPath ?? string.Empty;
        }

        return result;
    }

    private async Task<(bool Success, string ErrorMessage, string? FailedPath, string? BackupPath)> BackupExistingModPathsAsync(
        string? modsPath,
        IReadOnlyList<string> existingPaths,
        IReadOnlyList<string>? replacementFolderNames = null,
        string? sourceArchiveName = null)
    {
        if (string.IsNullOrWhiteSpace(modsPath))
        {
            return (false, "未找到有效的 Mods 目录，无法创建覆盖前备份", existingPaths.FirstOrDefault(), null);
        }

        string? lastBackupPath = null;
        foreach (var existingPath in existingPaths)
        {
            var backupResult = await Task.Run(() =>
            {
                var success = _downloadInstallService.TryBackupExistingModDirectory(
                    modsPath,
                    existingPath,
                    replacementFolderNames,
                    sourceArchiveName,
                    out var backupPath);
                return (success, backupPath);
            });
            if (!backupResult.success)
            {
                return (
                    false,
                    $"原有 Mod“{Path.GetFileName(existingPath)}”备份失败",
                    existingPath,
                    lastBackupPath);
            }

            lastBackupPath = backupResult.backupPath;
            EmitLog($"覆盖前已备份 Mod: {existingPath} -> {backupResult.backupPath}");
        }

        if (!string.IsNullOrWhiteSpace(lastBackupPath))
        {
            ModBackupCreated?.Invoke();
        }

        return (true, string.Empty, null, lastBackupPath);
    }

    private static async Task<IReadOnlyList<string>> GetReplacementFolderNamesForUpdateAsync(
        DownloadTaskItem task,
        IReadOnlyList<string> existingPaths)
    {
        var archiveNames = await Task.Run(() =>
            ModpackInstallService.GetInstallTargetNames(task.OutputFilePath, task.Name));
        if (archiveNames.Count > 0)
        {
            return archiveNames;
        }

        // 非标准归档无法解析时仍记录当前目标名，保证恢复链不会丢失。
        return existingPaths
            .Select(path => Path.GetFileName(path))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<ConflictResolutionDialogAction> ShowConflictResolutionOnUiThreadAsync(
        string title,
        string message,
        string incomingPath,
        string existingPath,
        string comparisonSummary,
        string openIncomingText,
        string openExistingText,
        string replaceButtonText)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return await _dialogService.ShowConflictResolutionDialogAsync(
                title,
                message,
                incomingPath,
                existingPath,
                comparisonSummary,
                openIncomingText,
                openExistingText,
                replaceButtonText);
        }

        var operation = Dispatcher.UIThread.InvokeAsync(() =>
            _dialogService.ShowConflictResolutionDialogAsync(
                title,
                message,
                incomingPath,
                existingPath,
                comparisonSummary,
                openIncomingText,
                openExistingText,
                replaceButtonText));
        return await operation;
    }

    private async Task ShowMessageOnUiThreadAsync(string title, string message)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            await _dialogService.ShowMessageAsync(title, message);
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() => _dialogService.ShowMessageAsync(title, message));
    }

    private static bool HasRealDownloadSource(DownloadTaskItem task)
    {
        // 本地拖拽/文件选择任务把压缩包路径放在 SourceUrl 和 OutputFilePath 中，
        // 但这不是待下载 URL，必须直接进入安装执行器。
        if (task.TaskAction is DownloadTaskAction.InstallModpack or DownloadTaskAction.InstallCollection &&
            (!Uri.TryCreate(task.SourceUrl, UriKind.Absolute, out var sourceUri) ||
             (!IsHttpUri(sourceUri) && !IsCurseforgeCacheSource(task.SourceUrl))))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(task.SourceUrl) &&
               !string.IsNullOrWhiteSpace(task.OutputFilePath);
    }

    private static bool HasPreviouslyInstalledModpackRuntime(DownloadTaskItem task)
    {
        return task.TaskAction == DownloadTaskAction.InstallModpack &&
               HasPreviouslyInstalledPackageRuntime(task);
    }

    /// <summary>
    /// 判断 SMAPI 任务是否已经创建过目标实例。
    /// 应用重启时，外部 SMAPI 安装任务无法恢复原来的取消回调，会被标记为
    /// 可重试失败；重试必须允许安装器更新现有的半成品目录，否则只要上次已
    /// 创建 versions/&lt;实例&gt; 就会再次得到“实例已存在”。
    /// </summary>
    private static bool HasPreviouslyInstalledSmapiRuntime(DownloadTaskItem task)
    {
        if (task.TaskAction != DownloadTaskAction.InstallSmapi ||
            string.IsNullOrWhiteSpace(task.TargetGamePath) ||
            string.IsNullOrWhiteSpace(task.TargetInstanceName))
        {
            return false;
        }

        var gameBasePath = InstanceRuntimePathResolver.ResolveBasePath(task.TargetGamePath);
        if (string.IsNullOrWhiteSpace(gameBasePath))
        {
            return false;
        }

        var instanceName = InstanceRuntimePathResolver.SanitizeFileNameComponent(
            task.TargetInstanceName,
            string.Empty);
        if (string.IsNullOrWhiteSpace(instanceName))
        {
            return false;
        }

        return Directory.Exists(Path.Combine(gameBasePath, "versions", instanceName));
    }

    private static bool HasFailedCollectionDownloads(DownloadTaskItem task)
    {
        return (task.TaskKind is DownloadTaskKind.NxmCollection or DownloadTaskKind.NexusCollection) &&
               task.FailedDownloadUrls.Any(url => !string.IsNullOrWhiteSpace(url));
    }

    private static bool HasPreviouslyInstalledPackageRuntime(DownloadTaskItem task)
    {
        var isPackageTask = task.TaskAction is DownloadTaskAction.InstallModpack or DownloadTaskAction.InstallCollection ||
                            task.TaskKind is DownloadTaskKind.NxmCollection or DownloadTaskKind.NexusCollection;
        if (!isPackageTask)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(task.InstalledPath) ||
            !string.IsNullOrWhiteSpace(task.InstalledDirectory))
        {
            return true;
        }

        // 安装器可能已经创建 versions/<实例>，但进程恰好在返回结果前退出，
        // 此时任务状态中还没有 InstalledPath；重试仍应进入更新模式。
        if (string.IsNullOrWhiteSpace(task.TargetGamePath) ||
            string.IsNullOrWhiteSpace(task.TargetInstanceName))
        {
            return false;
        }

        var gameBasePath = InstanceRuntimePathResolver.ResolveBasePath(task.TargetGamePath);
        var instanceName = InstanceRuntimePathResolver.SanitizeFileNameComponent(
            task.TargetInstanceName,
            string.Empty);
        return !string.IsNullOrWhiteSpace(gameBasePath) &&
               !string.IsNullOrWhiteSpace(instanceName) &&
               Directory.Exists(Path.Combine(gameBasePath, "versions", instanceName));
    }

    private static bool ShouldReuseLocalPackageArchive(DownloadTaskItem task)
    {
        var isPackageTask = task.TaskAction is DownloadTaskAction.InstallModpack or DownloadTaskAction.InstallCollection;
        var isSupportedKind = task.TaskKind is
            DownloadTaskKind.SvlModpack or
            DownloadTaskKind.CurseforgeModpack or
            DownloadTaskKind.NxmCollection or
            DownloadTaskKind.NexusCollection;
        return isPackageTask &&
               isSupportedKind &&
               !string.IsNullOrWhiteSpace(task.OutputFilePath) &&
               IsValidLocalPackageArchive(task.OutputFilePath, task.TaskKind);
    }

    /// <summary>
    /// 判断任务输出路径是否已经有可复用的完整下载归档。
    /// 下载状态会先写入 .part，只有最终归档通过校验才允许跳过网络；
    /// SMAPI 还必须包含 install.dat，避免把普通 Mod/错误响应当成安装包。
    /// </summary>
    private static bool TryReuseExistingDownloadedArtifact(DownloadTaskItem task)
    {
        if (task == null ||
            string.IsNullOrWhiteSpace(task.OutputFilePath) ||
            !File.Exists(task.OutputFilePath))
        {
            return false;
        }

        // DownloadAsync 先写入 .part，成功后才原子改名到 OutputFilePath。
        // 如果仍有断点文件，说明最终文件可能只是旧文件或上一次未完成的
        // 残留，不能因为它恰好能被解压/读取就跳过本次下载。
        if (File.Exists(task.OutputFilePath + ".part") ||
            File.Exists(task.OutputFilePath + ".part.json"))
        {
            return false;
        }

        return task.TaskAction switch
        {
            DownloadTaskAction.InstallSmapi =>
                ModpackInstallService.TryNormalizeSmapiArchive(task.OutputFilePath),
            DownloadTaskAction.InstallMod =>
                ModpackInstallService.IsValidModArchiveFile(task.OutputFilePath),
            // 另存为不要求目标是 Mod 压缩包：可以是游戏文件、说明包或
            // 其他用户指定的资源。最终文件存在且没有 .part 即表示上一次
            // 下载已完成，可直接进入“已完成（另存为）”。
            DownloadTaskAction.SaveOnly => IsCompleteSavedFile(task.OutputFilePath),
            _ => false
        };
    }

    private static bool IsCompleteSavedFile(string path)
    {
        try
        {
            return File.Exists(path) &&
                   !File.Exists(path + ".part") &&
                   !File.Exists(path + ".part.json");
        }
        catch
        {
            return false;
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        var units = new[] { "KB", "MB", "GB", "TB" };
        var unitIndex = -1;
        double display = bytes;
        while (display >= 1024 && unitIndex < units.Length - 1)
        {
            display /= 1024;
            unitIndex++;
        }

        return $"{display:0.0} {units[Math.Max(0, unitIndex)]}";
    }

    /// <summary>
    /// 只有归档真实包含对应类型的清单时才允许重试复用。
    /// 下载阶段可能把 HTML、错误响应或截断文件写入 OutputFilePath；
    /// 仅判断 File.Exists 会让后续重试反复跳过远程下载，必须让坏归档回到真实下载流程。
    /// </summary>
    private static bool IsValidLocalPackageArchive(string archivePath, DownloadTaskKind taskKind)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            return false;
        }

        try
        {
            var detection = ModpackTypeDetector.Detect(archivePath);
            if (!string.IsNullOrWhiteSpace(detection.TempExtractPath))
            {
                ModpackTypeDetector.CleanupTempDirectory(detection.TempExtractPath);
            }

            return taskKind switch
            {
                DownloadTaskKind.SvlModpack => detection.Type == ModpackType.SVL,
                DownloadTaskKind.CurseforgeModpack => detection.Type == ModpackType.Curseforge,
                DownloadTaskKind.NxmCollection or DownloadTaskKind.NexusCollection =>
                    detection.Type == ModpackType.NexusCollection,
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 为恢复/重试的本地整合包任务补回真实类型。
    /// URL 导入在下载前无法知道是 SVL、CurseForge 还是 Collection，
    /// 因此初始 TaskKind 为 Generic；只要本地归档已经存在，就可以重新检测。
    /// </summary>
    private static bool TryResolveLocalPackageTaskKind(DownloadTaskItem task)
    {
        if (task.TaskAction != DownloadTaskAction.InstallModpack ||
            task.TaskKind != DownloadTaskKind.Generic ||
            string.IsNullOrWhiteSpace(task.OutputFilePath) ||
            !File.Exists(task.OutputFilePath))
        {
            return task.TaskKind is DownloadTaskKind.SvlModpack or
                DownloadTaskKind.CurseforgeModpack or
                DownloadTaskKind.NexusCollection;
        }

        try
        {
            var detection = ModpackTypeDetector.Detect(task.OutputFilePath);
            if (!string.IsNullOrWhiteSpace(detection.TempExtractPath))
            {
                ModpackTypeDetector.CleanupTempDirectory(detection.TempExtractPath);
            }

            switch (detection.Type)
            {
                case ModpackType.SVL:
                    task.TaskKind = DownloadTaskKind.SvlModpack;
                    return true;
                case ModpackType.Curseforge:
                    task.TaskKind = DownloadTaskKind.CurseforgeModpack;
                    return true;
                case ModpackType.NexusCollection:
                    task.TaskKind = DownloadTaskKind.NexusCollection;
                    task.TaskAction = DownloadTaskAction.InstallCollection;
                    return true;
                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private void RefreshGamePathState()
    {
        var settings = _settingsStore.Load();
        var preferred = settings.PreferredInstancePath?.Trim();

        var gamePath = !string.IsNullOrWhiteSpace(preferred) && Directory.Exists(preferred)
            ? preferred
            : _gameInstallPathLocator.TryLocateSteamStardewPath()
              ?? _gameInstallPathLocator.TryLocateGogStardewPath()
              ?? _gameInstallPathLocator.TryLocateXboxStardewPath();

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
    private async Task<string?> TryBrowserDownloadFallbackAsync(
        long modId,
        long fileId,
        string browserUrl,
        CancellationToken cancellationToken = default)
    {
        NxmImportStatus = "浏览器下载回退：请在浏览器中点击 Manual Download";
        var result = await _browserDownloadFallbackService.WaitForNxmCallbackAsync(
            modId,
            fileId,
            browserUrl,
            hint => NxmImportStatus = hint,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
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
            var decodedManualName = Uri.UnescapeDataString(manualName.Trim());
            var cleaned = InstanceRuntimePathResolver.SanitizeFileNameComponent(decodedManualName, string.Empty);
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                cleaned = CollapseRepeatedVersionSuffix(cleaned);
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
            return InstanceRuntimePathResolver.SanitizeFileNameComponent(
                CollapseRepeatedVersionSuffix(Uri.UnescapeDataString(urlFileName)),
                "download.bin");
        }

        return $"download-{DateTime.Now:yyyyMMddHHmmss}.bin";
    }

    private static string CollapseRepeatedVersionSuffix(string value)
    {
        var extension = Path.GetExtension(value);
        if (string.IsNullOrWhiteSpace(extension) ||
            !IsKnownArchiveExtension(extension))
        {
            return value;
        }

        var stem = value[..^extension.Length];
        var match = Regex.Match(
            stem,
            @"^(?<prefix>.+?)\s+(?<version>\d+(?:\.\d+)+)\s+\k<version>$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        return match.Success
            ? $"{match.Groups["prefix"].Value.Trim()} {match.Groups["version"].Value}{extension}"
            : value;
    }

    private static bool TryParsePositiveLong(string? value, out long result)
    {
        return long.TryParse(value, out result) && result > 0;
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
        // 多个 Collection/Mod 任务会从不同线程同时汇报进度；状态文件使用同一个
        // .tmp 路径，若并发写入会出现互相覆盖或 Move 失败，导致重启后丢任务。
        // 先取得 UI 线程快照，再串行化文件写入。不能在持有写入锁时同步等待
        // UI 线程：若 UI 回调同时触发 SaveTaskState，会形成后台线程等待 UI、UI
        // 等待写入锁的死锁。
        var saveSequence = Interlocked.Increment(ref _taskStateSaveSequence);
        List<DownloadTaskItem> snapshot;
        try
        {
            // DownloadTasks 由 UI 线程维护；安装/下载回调可能从线程池触发保存。
            // 先在 UI 线程取得稳定快照，避免 ObservableCollection 在增删任务时
            // 被后台枚举导致 InvalidOperationException，进而丢失重启恢复记录。
            snapshot = Dispatcher.UIThread.CheckAccess()
                ? DownloadTasks.ToList()
                : Dispatcher.UIThread.InvokeAsync(() => DownloadTasks.ToList())
                    .GetAwaiter()
                    .GetResult();
        }
        catch
        {
            // Keep persistence as best-effort to avoid breaking download workflow.
            return;
        }

        lock (_taskStateSaveLock)
        {
            // 快照在锁外取得，较早请求可能晚于较新请求完成快照；跳过旧序号，
            // 避免新状态已经落盘后又被旧状态覆盖。
            if (saveSequence < _lastSavedTaskStateSequence)
            {
                return;
            }

            try
            {
                _taskStateStore.Save(_taskStatePath, snapshot);
                _lastSavedTaskStateSequence = saveSequence;
            }
            catch
            {
                // Keep persistence as best-effort to avoid breaking download workflow.
            }
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

            DownloadTasks.Clear();
            foreach (var record in records)
            {
                var recoveredTask = new DownloadTaskItem
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
                    SourcePlatform = record.SourcePlatform,
                    SourceRepository = record.SourceRepository,
                    CollectionSlug = record.CollectionSlug,
                    CollectionRevision = record.CollectionRevision,
                    SourceUrl = record.SourceUrl,
                    OutputFilePath = record.OutputFilePath,
                    InstalledPath = record.InstalledPath,
                    InstalledDirectory = record.InstalledDirectory,
                    ReportPath = record.ReportPath,
                    BackupPath = record.BackupPath,
                    FailedDetails = record.FailedDetails,
                    RetryReportPath = record.RetryReportPath,
                    TargetGamePath = record.TargetGamePath,
                    TargetInstanceName = record.TargetInstanceName,
                    CustomIconPath = record.CustomIconPath,
                    SkipConflictPrompt = record.SkipConflictPrompt,
                    SpeedText = record.SpeedText,
                    EtaText = record.EtaText,
                    TotalSizeText = record.TotalSizeText,
                    DownloadedSizeText = record.DownloadedSizeText,
                    SubProgressText = record.SubProgressText,
                    SubProgress = record.SubProgress,
                    StatusIconSource = string.Empty,
                    DependencyUrls = record.DependencyUrls ?? [],
                    FailedDownloadUrls = record.FailedDownloadUrls ?? [],
                    ConflictPreviewItems = record.ConflictPreviewItems ?? []
                };
                foreach (var modItem in record.CollectionModItems ?? [])
                {
                    recoveredTask.SyncCollectionModProgress(
                        modItem.Name,
                        modItem.Phase,
                        modItem.Optional,
                        modItem.State,
                        modItem.Message,
                        modItem.SourceUrl,
                        modItem.RequiresManualAction);
                }

                DownloadTasks.Add(recoveredTask);
                NormalizeRecoveredTaskState(recoveredTask);
                recoveredTask.StatusIconSource = ResolveTaskStatusIcon(recoveredTask);
            }
        }
        catch
        {
            // Ignore broken persisted state and keep in-memory defaults.
        }
    }

    private static void NormalizeRecoveredTaskState(DownloadTaskItem task)
    {
        // 早期状态文件新增 TaskKind 后没有同步写入 TaskAction，
        // 反序列化会使用 InstallMod 默认值。按更具体的任务类型补回动作，
        // 否则重启后本地 Collection/整合包会误走普通 Mod 安装分支。
        if (task.TaskAction == DownloadTaskAction.InstallMod)
        {
            task.TaskAction = LooksLikeSmapiTask(task)
                ? DownloadTaskAction.InstallSmapi
                : task.TaskKind switch
            {
                DownloadTaskKind.NxmCollection or DownloadTaskKind.NexusCollection
                    => DownloadTaskAction.InstallCollection,
                DownloadTaskKind.SvlModpack or DownloadTaskKind.CurseforgeModpack
                    => DownloadTaskAction.InstallModpack,
                _ => task.TaskAction
            };
        }

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

    private static void NormalizeSmapiTaskAction(DownloadTaskItem task)
    {
        if (task.TaskAction == DownloadTaskAction.InstallMod && LooksLikeSmapiTask(task))
        {
            task.TaskAction = DownloadTaskAction.InstallSmapi;
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

