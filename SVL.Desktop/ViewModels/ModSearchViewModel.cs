using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SVL.Core.Config;
using SVL.Core.Download;
using SVL.Core.Logging;
using SVL.Core.Utils;
using SVL.Desktop.Controls;
using SVL.Desktop.Models;
using SVL.Desktop.Utilities;

namespace SVL.Desktop.ViewModels;

/// <summary>
/// MOD 搜索页面 ViewModel（用于搜索和下载 MOD）
/// </summary>
public partial class ModSearchViewModel : ObservableObject
{
    private const int CurseforgeRawPagesPerFilteredPage = 3;
    private const int NexusRawPagesPerFilteredPage = 3;

    private bool _suppressSourceChangeNotification = true;
    private bool _suppressModTypeReload;
    private string _activeSearchName = string.Empty;
    private readonly Dictionary<long, List<string>> _nexusSupportedGameVersionsCache = new();
    private bool _nexusGameVersionFetchDisabledByAuth;
    private bool _hasHandledNexusTokenExpired;

    public ModSearchViewModel()
    {
        try
        {
            var settings = AppConfig.GetSettings();
            var defaultSource = settings.ModDefaultSource ?? "全部";

            // 防御：确保是合法值
            if (!Sources.Contains(defaultSource))
                defaultSource = "全部";

            SelectedSource = defaultSource;
            Log.Info($"[ModSearchViewModel] 默认下载源(Mods) = {SelectedSource}");
        }
        catch (Exception ex)
        {
            Log.Warn("[ModSearchViewModel] 读取默认下载源失败，回退为 全部", ex);
            SelectedSource = "全部";
        }

        _suppressSourceChangeNotification = false;
        ShowInitialTypeFilterDisabledNoticeIfNeeded();
        _ = RefreshModTypesForCurrentSourceAsync();
        _ = LoadGameVersionsAsync();
    }

    private async Task LoadGameVersionsAsync()
    {
        try
        {
            var versionSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var curseforgeVersions = await CurseforgeApiService.GetGameVersionsAsync();
            foreach (var version in curseforgeVersions)
            {
                if (!string.IsNullOrWhiteSpace(version))
                    versionSet.Add(version);
            }

            var smapiVersions = await SVL.Core.Stardew.Mod.SMAPI.SmapApiService.GetKnownGameVersionsAsync(maxPages: 5);
            foreach (var version in smapiVersions)
            {
                if (!string.IsNullOrWhiteSpace(version))
                    versionSet.Add(version);
            }

            var list = versionSet
                .OrderByDescending(version => version, SemanticVersionComparer.Instance)
                .ToList();

            // UI：保留“全部”为首项
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                GameVersions.Clear();
                GameVersions.Add("全部");
                foreach (var v in list)
                    GameVersions.Add(v);
            });
        }
        catch (Exception ex)
        {
            Log.Warn("[ModSearchViewModel] 动态加载游戏版本失败", ex);
        }
    }

    // ===== 搜索条件 =====

    [ObservableProperty]
    private string _searchName = "";

    partial void OnSearchNameChanged(string value)
    {
        // 输入框只更新待提交条件，真正搜索在点击“搜索”按钮后触发。
    }

    /// <summary>
    /// 是否已经显示过 API 配置警告（防止重复提示）
    /// </summary>
    private bool _hasShownApiConfigWarning = false;

    /// <summary>
    /// 执行搜索（带配置检查）
    /// </summary>
    private async Task PerformSearchWithConfigCheckAsync()
    {
        _activeSearchName = NormalizeSearchText(SearchName);

        // 检查 API 配置
        var configWarnings = CheckApiConfig();
        var message = string.Join("\n\n", configWarnings);
        if (configWarnings.Count > 0 && !_hasShownApiConfigWarning)
        {
            _hasShownApiConfigWarning = true;

            // 第一次显示：弹出可交互对话框，点击确定后跳转到设置
            if (SvlMessageBox.Confirm(
                message + "\n\n点击确定跳转到设置页面配置 API。",
                "API 配置提示",
                okText: "确定",
                cancelText: "取消"))
            {
                // 跳转到设置页面（并切换到 API 与账户选项卡）
                if (System.Windows.Application.Current.MainWindow is MainWindow mainWindow &&
                    mainWindow.DataContext is MainWindowViewModel mainViewModel)
                {
                    mainViewModel.CurrentPage = PageType.Settings;
                    _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (mainViewModel.LeftPanelContent is SettingsViewModel settingsViewModel)
                        {
                            settingsViewModel.RefreshNexusLoginStatus();
                            settingsViewModel.SwitchToApiTab();
                        }
                    }), System.Windows.Threading.DispatcherPriority.Loaded);
                }
            }
        }
        else if (configWarnings.Count > 0)
        {
            // 后续：仅显示提示，不跳转
            FloatingNotificationControl.Show(
                title: "API 配置提示",
                message: message,
                autoCloseDelay: 8000,
                notificationType: NotificationType.Warning
            );
        }

        // 执行搜索
        await SearchModsCoreAsync(resetPage: true);
    }

    /// <summary>
    /// 检查 API 配置是否完整
    /// </summary>
    private List<string> CheckApiConfig()
    {
        var warnings = new List<string>();
        var settings = AppConfig.GetSettings();

        // 检查 NexusMods API（如果选择了 NexusMods 或 全部）
        if (SelectedSource == "全部" || SelectedSource == "NexusMods")
        {
            var nexusKey = settings.NexusModsApiKey;
            var nexusToken = settings.NexusModsOAuthToken;

            if (string.IsNullOrEmpty(nexusKey) && string.IsNullOrEmpty(nexusToken))
            {
                warnings.Add("⚠️ NexusMods 未登录\n请在设置页面登录 NexusMods 账户以使用 NexusMods 源。");
            }
        }

        return warnings;
    }

    private void HandleNexusModsTokenExpired(string scene)
    {
        if (_hasHandledNexusTokenExpired)
            return;

        _hasHandledNexusTokenExpired = true;
        _nexusGameVersionFetchDisabledByAuth = true;
        StatusMessage = "NexusMods 登录已过期，请重新登录";
        NexusAuthStateHelper.HandleTokenExpired(scene, "ModSearchViewModel", showNotification: true);
    }

    [ObservableProperty]
    private string _selectedGameVersion = "全部";

    private string _searchGameVersion = "全部";  // 用于可编辑的 ComboBox

    /// <summary>
    /// 游戏版本搜索文本（与 SelectedGameVersion 同步）
    /// </summary>
    public string SearchGameVersion
    {
        get => _searchGameVersion;
        set
        {
            if (SetProperty(ref _searchGameVersion, value))
            {
                // 同步到 SelectedGameVersion
                if (string.IsNullOrWhiteSpace(value) || GameVersions.Contains(value))
                {
                    SelectedGameVersion = value ?? "全部";
                }
            }
        }
    }

    [ObservableProperty]
    private string _selectedSource = "全部";

    [ObservableProperty]
    private string _selectedCategory = "全部";

    [ObservableProperty]
    private string _selectedModType = "全部";

    // ===== 数据列表 =====

    [ObservableProperty]
    private ObservableCollection<ModSearchItem> _modList = new();

    [ObservableProperty]
    private int _totalCount = 0;

    [ObservableProperty]
    private bool _isLoading = false;

    [ObservableProperty]
    private string _statusMessage = "加载热门模组中...";

    // ===== 分页相关 =====

    [ObservableProperty]
    private int _currentPage = 1;

    [ObservableProperty]
    private int _pageSize = 20; // 每页显示数量

    [ObservableProperty]
    private int _totalPages = 1;

    [ObservableProperty]
    private bool _hasNextPage = false;

    [ObservableProperty]
    private bool _hasPreviousPage = false;

    /// <summary>
    /// 是否显示分页控件（当总页数大于1时显示）
    /// </summary>
    [ObservableProperty]
    private bool _isPaginationVisible = false;

    /// <summary>
    /// 可显示的页码列表（-1 表示省略号）
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<int> _pageNumbers = new();


    // ===== 下拉选项 =====

    public ObservableCollection<string> GameVersions { get; } = new() { "全部" };

    public List<string> Sources { get; } = new()
    {
        "全部",
        "Curseforge",
        "NexusMods"
    };

    public List<string> Categories { get; } = new()
    {
        "全部",
        "功能扩展",
        "界面美化",
        "游戏内容",
        "工具类",
        "音效材质",
        "作弊类"
    };

    [ObservableProperty]
    private ObservableCollection<string> _modTypes = new(new[] { "全部" });

    public bool IsModTypeFilterEnabled => !string.Equals(SelectedSource, "全部", StringComparison.OrdinalIgnoreCase);

    public string ModTypeFilterHint => IsModTypeFilterEnabled
        ? "按当前来源的类型筛选"
        : "请先选择具体来源，再按类型筛选";

    partial void OnSelectedSourceChanged(string value)
    {
        OnPropertyChanged(nameof(IsModTypeFilterEnabled));
        OnPropertyChanged(nameof(ModTypeFilterHint));

        if (SelectedModType != "全部")
            SelectedModType = "全部";

        if (string.Equals(value, "全部", StringComparison.OrdinalIgnoreCase))
        {
            ModTypes.Clear();
            ModTypes.Add("全部");

            if (!_suppressSourceChangeNotification && AppConfig.GetSettings().ShowModTypeFilterDisabledNotice)
            {
                FloatingNotificationControl.Show(
                    title: "类型筛选已关闭",
                    message: "来源为“全部”时无法按类型筛选，请先切换到 Curseforge 或 NexusMods。",
                    autoCloseDelay: 3500,
                    notificationType: NotificationType.Info);
            }

            return;
        }

        _ = RefreshModTypesForCurrentSourceAsync();
    }

    private void ShowInitialTypeFilterDisabledNoticeIfNeeded()
    {
        if (string.Equals(SelectedSource, "全部", StringComparison.OrdinalIgnoreCase) &&
            AppConfig.GetSettings().ShowModTypeFilterDisabledNotice)
        {
            FloatingNotificationControl.Show(
                title: "类型筛选已关闭",
                message: "当前来源为“全部”，请先切换到 Curseforge 或 NexusMods 后再按类型筛选。",
                autoCloseDelay: 3500,
                notificationType: NotificationType.Info);
        }
    }

    partial void OnSelectedModTypeChanged(string value)
    {
        Log.Info($"[ModSearchViewModel] 类型选择变更: {value}");

        if (_suppressModTypeReload)
            return;

        _ = ReloadCurrentResultsAsync(resetPage: true);
    }

    private void UpdateModTypes(IEnumerable<string> categories)
    {
        if (!IsModTypeFilterEnabled)
        {
            if (ModTypes.Count != 1 || ModTypes[0] != "全部")
            {
                ModTypes.Clear();
                ModTypes.Add("全部");
            }
            return;
        }

        var categoryList = categories?
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(category => category)
            .ToList() ?? new List<string>();

        var previousSelection = SelectedModType;
        _suppressModTypeReload = true;
        ModTypes.Clear();
        ModTypes.Add("全部");

        foreach (var category in categoryList)
        {
            ModTypes.Add(category);
        }

        if (ModTypes.Contains(previousSelection))
            SelectedModType = previousSelection;
        else
            SelectedModType = "全部";
        _suppressModTypeReload = false;
    }

    private async Task ReloadCurrentResultsAsync(bool resetPage)
    {
        if (IsLoading)
            return;

        if (resetPage)
            CurrentPage = 1;

        if (string.IsNullOrWhiteSpace(_activeSearchName))
            await LoadPopularModsAsync();
        else
            await SearchModsCoreAsync(resetPage: false);
    }

    private static string NormalizeSearchText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private string? GetSelectedGameVersionFilter()
    {
        var value = NormalizeSearchText(SelectedGameVersion);
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, "全部", StringComparison.OrdinalIgnoreCase)
            ? null
            : value;
    }

    private static string NormalizeGameVersionToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var text = value.Trim();
        var match = System.Text.RegularExpressions.Regex.Match(
            text,
            @"(?i)(\d+(?:\.\d+){0,3})(\+|\s*(?:or|and)\s*(?:later|newer))?");

        if (!match.Success)
            return text;

        var version = match.Groups[1].Value;
        var hasPlus = !string.IsNullOrWhiteSpace(match.Groups[2].Value);
        return hasPlus ? version + "+" : version;
    }

    private static bool IsVersionPrefixMatch(string value, string prefix)
    {
        return value.Equals(prefix, StringComparison.OrdinalIgnoreCase)
               || value.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesSelectedGameVersion(IEnumerable<string>? versions, string? selectedGameVersion)
    {
        if (string.IsNullOrWhiteSpace(selectedGameVersion))
            return true;

        var requested = NormalizeGameVersionToken(selectedGameVersion);
        if (string.IsNullOrWhiteSpace(requested))
            return true;

        foreach (var rawVersion in versions ?? Enumerable.Empty<string>())
        {
            var candidate = NormalizeGameVersionToken(rawVersion);
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            var candidateBase = candidate.TrimEnd('+');
            var requestedBase = requested.TrimEnd('+');

            if (candidateBase.Equals(requestedBase, StringComparison.OrdinalIgnoreCase))
                return true;

            if (candidate.EndsWith("+", StringComparison.Ordinal) && IsVersionPrefixMatch(requestedBase, candidateBase))
                return true;

            if (requested.EndsWith("+", StringComparison.Ordinal) && IsVersionPrefixMatch(candidateBase, requestedBase))
                return true;

            if (IsVersionPrefixMatch(candidateBase, requestedBase) || IsVersionPrefixMatch(requestedBase, candidateBase))
                return true;
        }

        return false;
    }

    private static string ExtractStardewGameVersionKeyFromNexusFileDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return "未知";

        var match = System.Text.RegularExpressions.Regex.Match(
            description,
            @"(?i)\bfor\s+stardew(?:\s+valley)?\s+(\d+(?:\.\d+){1,3})\b(?:\s*(?:or|and)\s*(?:later|newer)\b)?");

        if (!match.Success)
        {
            match = System.Text.RegularExpressions.Regex.Match(
                description,
                @"(?i)\bstardew(?:\s+valley)?\s+(\d+(?:\.\d+){1,3})\b(?:\s*(?:or|and)\s*(?:later|newer)\b)?");
        }

        if (!match.Success)
            return "未知";

        var version = match.Groups[1].Value;
        if (string.IsNullOrWhiteSpace(version))
            return "未知";

        var hasPlus = System.Text.RegularExpressions.Regex.IsMatch(match.Value, @"(?i)\b(?:or|and)\s*(?:later|newer)\b")
                      || match.Value.Contains('+');

        return hasPlus ? (version.EndsWith("+", StringComparison.Ordinal) ? version : version + "+") : version;
    }

    private async Task<List<string>> GetNexusSupportedGameVersionsAsync(long modId)
    {
        if (modId <= 0)
            return new List<string>();

        if (_nexusGameVersionFetchDisabledByAuth)
            return new List<string>();

        if (_nexusSupportedGameVersionsCache.TryGetValue(modId, out var cached))
            return cached;

        try
        {
            var files = await SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsService.GetModFilesAsync(modId);
            var versions = (files ?? new List<SVL.Core.Stardew.ResourceProject.NexusMods.NexusModFile>())
                .Select(file => ExtractStardewGameVersionKeyFromNexusFileDescription(file.Description))
                .Where(version => !string.IsNullOrWhiteSpace(version)
                                  && !string.Equals(version, "未知", StringComparison.OrdinalIgnoreCase)
                                  && !string.Equals(version, "全部", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(version => version, SemanticVersionComparer.Instance)
                .ToList();

            _nexusSupportedGameVersionsCache[modId] = versions;
            return versions;
        }
        catch (SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsTokenExpiredException)
        {
            // Token 过期后停止逐条请求版本信息，避免后台日志刷屏。
            _nexusGameVersionFetchDisabledByAuth = true;
            _nexusSupportedGameVersionsCache[modId] = new List<string>();
            HandleNexusModsTokenExpired("GetNexusSupportedGameVersionsAsync");
            return _nexusSupportedGameVersionsCache[modId];
        }
        catch (Exception ex)
        {
            Log.Warn($"[ModSearchViewModel] 获取 NexusMods 游戏版本失败: modId={modId}", ex);
            var empty = new List<string>();
            _nexusSupportedGameVersionsCache[modId] = empty;
            return empty;
        }
    }

    private static bool IsNameExactOrPartialMatch(string searchText, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(searchText) || string.IsNullOrWhiteSpace(candidate))
            return false;

        var normalizedSearch = NormalizeSearchText(searchText);
        var normalizedCandidate = NormalizeSearchText(candidate);

        return normalizedCandidate.Equals(normalizedSearch, StringComparison.OrdinalIgnoreCase)
               || normalizedCandidate.IndexOf(normalizedSearch, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsDescriptionMatch(string searchText, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(searchText) || string.IsNullOrWhiteSpace(candidate))
            return false;

        return candidate.IndexOf(NormalizeSearchText(searchText), StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static List<CurseforgeApiService.CurseforgeModSearchItem> RankCurseforgeSearchResults(IEnumerable<CurseforgeApiService.CurseforgeModSearchItem> mods, string searchText)
    {
        var candidates = (mods ?? Enumerable.Empty<CurseforgeApiService.CurseforgeModSearchItem>())
            .Where(mod => mod != null)
            .GroupBy(mod => mod.Id)
            .Select(group => group.First())
            .ToList();

        if (string.IsNullOrWhiteSpace(searchText))
            return candidates.OrderByDescending(mod => mod.DownloadCount).ToList();

        var exactMatches = candidates
            .Where(mod => string.Equals(NormalizeSearchText(mod.Name), NormalizeSearchText(searchText), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(mod => mod.DownloadCount);

        var partialMatches = candidates
            .Where(mod => !string.Equals(NormalizeSearchText(mod.Name), NormalizeSearchText(searchText), StringComparison.OrdinalIgnoreCase))
            .Where(mod => IsNameExactOrPartialMatch(searchText, mod.Name))
            .OrderByDescending(mod => mod.DownloadCount);

        var descriptionMatches = candidates
            .Where(mod => !IsNameExactOrPartialMatch(searchText, mod.Name))
            .Where(mod => IsDescriptionMatch(searchText, mod.Summary))
            .OrderByDescending(mod => mod.DownloadCount);

        return exactMatches
            .Concat(partialMatches)
            .Concat(descriptionMatches)
            .GroupBy(mod => mod.Id)
            .Select(group => group.First())
            .ToList();
    }

    private async Task<(List<CurseforgeApiService.CurseforgeModSearchItem> Items, HashSet<string> Categories, bool HasMore)> FetchCurseforgeModsForCurrentTypeAsync(string searchTerm, int filteredPage, int filteredPageSize)
    {
        var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectedType = string.IsNullOrWhiteSpace(SelectedModType) ? "全部" : SelectedModType;
        var selectedGameVersion = GetSelectedGameVersionFilter();
        var safeFilteredPageSize = Math.Max(1, Math.Min(50, filteredPageSize));
        var normalizedSearchTerm = NormalizeSearchText(searchTerm);
        var needsExpandedFetch = selectedType != "全部" || !string.IsNullOrWhiteSpace(normalizedSearchTerm) || !string.IsNullOrWhiteSpace(selectedGameVersion);

        if (!needsExpandedFetch)
        {
            var skip = (filteredPage - 1) * safeFilteredPageSize;
            var batch = await CurseforgeApiService.SearchModsAsync(searchTerm, pageSize: safeFilteredPageSize, index: skip, gameVersion: selectedGameVersion) ?? new List<CurseforgeApiService.CurseforgeModSearchItem>();
            foreach (var mod in batch)
            {
                foreach (var category in mod.Categories ?? new List<CurseforgeApiService.CurseforgeCategory>())
                {
                    if (!string.IsNullOrWhiteSpace(category.Name))
                        categories.Add(category.Name);
                }
            }

            return (batch, categories, batch.Count >= safeFilteredPageSize);
        }

        var collected = new List<CurseforgeApiService.CurseforgeModSearchItem>();
        const int rawPageSize = 50;
        var startRawPage = Math.Max(1, ((filteredPage - 1) * CurseforgeRawPagesPerFilteredPage) + 1);
        var endRawPage = startRawPage + CurseforgeRawPagesPerFilteredPage - 1;
        var sawFullBatch = false;

        for (var rawPage = startRawPage; rawPage <= endRawPage; rawPage++)
        {
            var rawIndex = (rawPage - 1) * rawPageSize;
            var batch = await CurseforgeApiService.SearchModsAsync(searchTerm, pageSize: rawPageSize, index: rawIndex, gameVersion: selectedGameVersion);
            if (batch == null || batch.Count == 0)
                break;

            sawFullBatch = batch.Count >= rawPageSize;

            foreach (var mod in batch)
            {
                foreach (var category in mod.Categories ?? new List<CurseforgeApiService.CurseforgeCategory>())
                {
                    if (!string.IsNullOrWhiteSpace(category.Name))
                        categories.Add(category.Name);
                }

                var typeMatched = selectedType == "全部" || (mod.Categories != null && mod.Categories.Any(category => string.Equals(category.Name, selectedType, StringComparison.OrdinalIgnoreCase)));
                var versionMatched = MatchesSelectedGameVersion(mod.LatestFile?.GameVersion, selectedGameVersion);

                if (typeMatched && versionMatched)
                {
                    collected.Add(mod);
                }
            }

            if (batch.Count < rawPageSize)
                break;
        }

        var ranked = RankCurseforgeSearchResults(collected, normalizedSearchTerm);
        var pageItems = ranked.Take(safeFilteredPageSize).ToList();
        var hasMore = ranked.Count > safeFilteredPageSize || sawFullBatch;
        Log.Info($"[ModSearchViewModel] Curseforge 类型直取: type={selectedType}, filteredPage={filteredPage}, rawWindow={startRawPage}-{endRawPage}, rawCollected={collected.Count}, pageItems={pageItems.Count}, hasMore={hasMore}");
        return (pageItems, categories, hasMore);
    }

    private async Task<(List<SVL.Core.Stardew.ResourceProject.NexusMods.NexusMod> Items, HashSet<string> Categories, bool HasMore)> FetchNexusModsForCurrentTypeAsync(string searchTerm, int filteredPage, int filteredPageSize, bool useCache)
    {
        var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectedType = string.IsNullOrWhiteSpace(SelectedModType) ? "全部" : SelectedModType;
        var selectedGameVersion = GetSelectedGameVersionFilter();
        var safeFilteredPageSize = Math.Max(1, Math.Min(50, filteredPageSize));
        var needsExpandedFetch = selectedType != "全部" || !string.IsNullOrWhiteSpace(selectedGameVersion);

        if (!needsExpandedFetch)
        {
            var batch = await SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsService.SearchModsAsync(
                query: searchTerm,
                page: filteredPage,
                pageSize: safeFilteredPageSize,
                useCache: useCache) ?? new List<SVL.Core.Stardew.ResourceProject.NexusMods.NexusMod>();

            foreach (var mod in batch)
            {
                if (!string.IsNullOrWhiteSpace(mod.Category))
                    categories.Add(mod.Category);
            }

            return (batch, categories, batch.Count >= safeFilteredPageSize);
        }

        var collected = new List<SVL.Core.Stardew.ResourceProject.NexusMods.NexusMod>();
        const int rawPageSize = 50;
        var startRawPage = Math.Max(1, ((filteredPage - 1) * NexusRawPagesPerFilteredPage) + 1);
        var endRawPage = startRawPage + NexusRawPagesPerFilteredPage - 1;
        var sawFullBatch = false;

        for (var rawPage = startRawPage; rawPage <= endRawPage; rawPage++)
        {
            var batch = await SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsService.SearchModsAsync(
                query: searchTerm,
                page: rawPage,
                pageSize: rawPageSize,
                useCache: false);

            if (batch == null || batch.Count == 0)
                break;

            sawFullBatch = batch.Count >= rawPageSize;

            foreach (var mod in batch)
            {
                var category = string.IsNullOrWhiteSpace(mod.Category) ? "未分类" : mod.Category;
                categories.Add(category);

                if (selectedType != "全部" && !string.Equals(category, selectedType, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrWhiteSpace(selectedGameVersion))
                {
                    var supportedVersions = await GetNexusSupportedGameVersionsAsync(mod.ModId);
                    if (!MatchesSelectedGameVersion(supportedVersions, selectedGameVersion))
                        continue;
                }

                collected.Add(mod);
            }

            if (batch.Count < rawPageSize)
                break;
        }

        var deduped = collected
            .GroupBy(mod => mod.ModId)
            .Select(group => group.First())
            .ToList();

        var pageItems = deduped.Take(safeFilteredPageSize).ToList();
        var hasMore = deduped.Count > safeFilteredPageSize || sawFullBatch;
        Log.Info($"[ModSearchViewModel] NexusMods 类型直取: type={selectedType}, filteredPage={filteredPage}, rawWindow={startRawPage}-{endRawPage}, rawCollected={collected.Count}, pageItems={pageItems.Count}, hasMore={hasMore}");
        return (pageItems, categories, hasMore);
    }

    private async Task RefreshModTypesForCurrentSourceAsync()
    {
        try
        {
            if (!IsModTypeFilterEnabled)
            {
                UpdateModTypes(Array.Empty<string>());
                return;
            }

            var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (string.Equals(SelectedSource, "Curseforge", StringComparison.OrdinalIgnoreCase))
            {
                var mods = await CurseforgeApiService.SearchModsAsync(string.Empty, pageSize: 50, index: 0);
                foreach (var mod in mods ?? new List<CurseforgeApiService.CurseforgeModSearchItem>())
                {
                    foreach (var category in mod.Categories ?? new List<CurseforgeApiService.CurseforgeCategory>())
                    {
                        if (!string.IsNullOrWhiteSpace(category.Name))
                            categories.Add(category.Name);
                    }
                }

                Log.Info($"[ModSearchViewModel] 来源切换为 Curseforge，预加载类型 {categories.Count} 个");
            }
            else if (string.Equals(SelectedSource, "NexusMods", StringComparison.OrdinalIgnoreCase))
            {
                var mods = await SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsService.SearchModsAsync(
                    query: string.Empty,
                    page: 1,
                    pageSize: 50,
                    useCache: false);

                foreach (var mod in mods)
                {
                    if (!string.IsNullOrWhiteSpace(mod.Category))
                        categories.Add(mod.Category);
                }

                Log.Info($"[ModSearchViewModel] 来源切换为 NexusMods，预加载类型 {categories.Count} 个");
            }

            UpdateModTypes(categories);
        }
        catch (SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsTokenExpiredException)
        {
            HandleNexusModsTokenExpired("RefreshModTypesForCurrentSourceAsync");
        }
        catch (Exception ex)
        {
            Log.Warn("[ModSearchViewModel] 预加载类型列表失败", ex);
        }
    }

    /// <summary>
    /// 初始化时加载热门模组
    /// </summary>
    public async Task InitializeAsync()
    {
        Log.Info($"[ModSearchViewModel] InitializeAsync 被调用，ModList.Count = {ModList.Count}");
        if (ModList.Count == 0)
        {
            await LoadPopularModsAsync();
        }
        else
        {
            Log.Info("[ModSearchViewModel] ModList 已有数据，跳过初始化");
        }
    }

    /// <summary>
    /// 加载热门模组（支持分页，同时从 Curseforge 和 NexusMods 加载）
    /// </summary>
    private async Task LoadPopularModsAsync()
    {
        Log.Info($"[ModSearchViewModel] 开始加载第 {CurrentPage} 页热门模组");
        IsLoading = true;
        StatusMessage = $"正在加载第 {CurrentPage} 页...";

        try
        {
            ModList.Clear();
            Log.Info("[ModSearchViewModel] ModList 已清空");

            // 计算分页参数
            int skip = (CurrentPage - 1) * PageSize;

            // 收集所有类型
            var allCategories = new HashSet<string>();
            var addedCount = 0;

            // 收集所有模组的临时列表（用于统一排序）
            var allMods = new List<ModSearchItem>();

            // 计算需要获取的数据量（双倍数据量，用于跨来源统一排序）
            int fetchCount = SelectedSource == "全部" ? PageSize * 2 : PageSize;

            // 并行加载 Curseforge 和 NexusMods 的热门模组
            var curseforgeTask = (SelectedSource == "全部" || SelectedSource == "Curseforge")
                ? LoadModsFromCurseforgeAsync(skip, fetchCount)
                : Task.CompletedTask;

            var nexusModsTask = (SelectedSource == "全部" || SelectedSource == "NexusMods")
                ? LoadModsFromNexusModsAsync(CurrentPage, fetchCount)
                : Task.CompletedTask;

            await Task.WhenAll(curseforgeTask, nexusModsTask);

            // 处理 Curseforge 结果
            bool curseforgeHasMore = false;
            if (SelectedSource == "全部" || SelectedSource == "Curseforge")
            {
                if (curseforgeTask.Status == TaskStatus.RanToCompletion)
                {
                    var curseforgeResult = await ((Task<LoadModsResult>)curseforgeTask);
                    if (curseforgeResult != null && curseforgeResult.ModItems.Count > 0)
                    {
                        foreach (var category in curseforgeResult.Categories)
                        {
                            allCategories.Add(category);
                        }

                        // 先添加到临时列表
                        allMods.AddRange(curseforgeResult.ModItems);

                        Log.Info($"[ModSearchViewModel] 收集了 {curseforgeResult.ModItems.Count} 个 Curseforge 模组");

                        // Curseforge 返回了满页（fetchCount），可能还有更多结果
                        curseforgeHasMore = curseforgeResult.HasMore || curseforgeResult.ModItems.Count >= fetchCount;
                    }
                }
            }

            // 处理 NexusMods 结果
            bool nexusModsHasMore = false;
            if (SelectedSource == "全部" || SelectedSource == "NexusMods")
            {
                if (nexusModsTask.Status == TaskStatus.RanToCompletion)
                {
                    var nexusModsResult = await ((Task<LoadModsResult>)nexusModsTask);
                    if (nexusModsResult != null && nexusModsResult.ModItems.Count > 0)
                    {
                        foreach (var category in nexusModsResult.Categories)
                        {
                            allCategories.Add(category);
                        }

                        // 先添加到临时列表
                        allMods.AddRange(nexusModsResult.ModItems);

                        Log.Info($"[ModSearchViewModel] 收集了 {nexusModsResult.ModItems.Count} 个 NexusMods 模组");

                        // NexusMods 返回了满页（fetchCount），可能还有更多结果
                        nexusModsHasMore = nexusModsResult.HasMore || nexusModsResult.ModItems.Count >= fetchCount;
                    }
                }
            }

            // ✅ 分组排序策略：两个来源各自排序，然后交错合并
            // 先按来源分组并各自排序
            var curseforgeMods = allMods.Where(m => m.Source == "Curseforge").OrderByDescending(m => m.DownloadCount).ToList();
            var nexusMods = allMods.Where(m => m.Source == "NexusMods").OrderByDescending(m => m.DownloadCount).ToList();

            Log.Info($"[ModSearchViewModel] 分组排序完成：Curseforge {curseforgeMods.Count} 个，NexusMods {nexusMods.Count} 个");

            // 交错合并（保证两个来源都能按比例展示）
            var displayMods = new List<ModSearchItem>();
            int maxCount = Math.Max(curseforgeMods.Count, nexusMods.Count);

            for (int i = 0; i < maxCount && displayMods.Count < PageSize; i++)
            {
                // 交替添加：Curseforge 第 i 个，NexusMods 第 i 个
                if (i < curseforgeMods.Count && displayMods.Count < PageSize)
                {
                    displayMods.Add(curseforgeMods[i]);
                }
                if (i < nexusMods.Count && displayMods.Count < PageSize)
                {
                    displayMods.Add(nexusMods[i]);
                }
            }

            Log.Info($"[ModSearchViewModel] 交错合并后显示 {displayMods.Count} 个模组（Curseforge: {displayMods.Count(m => m.Source == "Curseforge")}, NexusMods: {displayMods.Count(m => m.Source == "NexusMods")}）");

            // 将排序后的模组添加到显示列表
            foreach (var item in displayMods)
            {
                ModList.Add(item);
                _ = item.LoadIconAsync();
                addedCount++;
            }

            LocalizationDisplayHelper.ApplyLocalizationInBackground(displayMods);

            // 更新类型列表
            UpdateModTypes(allCategories);

            // 更新分页状态（任一来源有更多结果就显示下一页）
            if (addedCount == 0)
            {
                TotalPages = 1;
            }
            else if (curseforgeHasMore || nexusModsHasMore)
            {
                TotalPages = CurrentPage + 1; // 至少有一个来源还有更多结果
            }
            else
            {
                TotalPages = CurrentPage; // 所有来源都已到最后一页
            }
            UpdatePaginationState();

            TotalCount = addedCount;
            Log.Info($"[ModSearchViewModel] 第 {CurrentPage} 页加载完成，共 {TotalCount} 个模组");

            if (TotalCount > 0)
            {
                StatusMessage = $"第 {CurrentPage}/{TotalPages} 页（共 {TotalCount} 个模组）";
            }
            else
            {
                StatusMessage = "未找到模组";
            }
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "[ModSearchViewModel] 加载热门模组失败");
            StatusMessage = $"加载失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 加载模组的结果
    /// </summary>
    private class LoadModsResult
    {
        public List<ModSearchItem> ModItems { get; set; } = new();
        public HashSet<string> Categories { get; set; } = new();
        public bool HasMore { get; set; }
    }

    /// <summary>
    /// 从 Curseforge 加载 Mods
    /// </summary>
    private async Task<LoadModsResult> LoadModsFromCurseforgeAsync(int skip, int pageSize)
    {
        var result = new LoadModsResult();

        try
        {
            Log.Info($"[ModSearchViewModel] 从 Curseforge 搜索热门模组（跳过{skip}个）");
            var filteredPage = Math.Max(1, (skip / Math.Max(pageSize, 1)) + 1);
            var fetchResult = await FetchCurseforgeModsForCurrentTypeAsync(string.Empty, filteredPage, pageSize);
            var curseforgeMods = fetchResult.Items;
            result.HasMore = fetchResult.HasMore;
            result.Categories = fetchResult.Categories;

            if (curseforgeMods != null && curseforgeMods.Count > 0)
            {
                foreach (var mod in curseforgeMods)
                {
                    // 解析更新时间
                    string lastUpdateTime = "未知";
                    if (!string.IsNullOrEmpty(mod.DateModified))
                    {
                        if (DateTime.TryParse(mod.DateModified, out DateTime dateModified))
                        {
                            lastUpdateTime = dateModified.ToString("yyyy-MM-dd");
                        }
                    }

                    // 转换为 ModSearchItem
                    var searchItem = new ModSearchItem
                    {
                        Id = $"curse-{mod.Id}",
                        Name = mod.Name,
                        Summary = mod.Summary ?? "暂无描述",
                        Description = mod.Summary ?? "暂无描述",
                        IconUrl = mod.Logo?.ThumbnailUrl ?? mod.Logo?.Url ?? "",
                        Author = "Unknown", // Curseforge API 搜索结果中没有作者信息
                        DownloadCount = mod.DownloadCount,
                        LastUpdateTime = lastUpdateTime,
                        Source = "Curseforge",
                        Category = mod.Categories?.FirstOrDefault()?.Name ?? "未分类",
                        SupportedGameVersions = mod.LatestFile?.GameVersion ?? new List<string>(),
                        Rating = 0,
                        Url = mod.Links?.WebsiteUrl ?? ""
                    };

                    result.ModItems.Add(searchItem);
                }

                Log.Info($"[ModSearchViewModel] Curseforge 返回 {curseforgeMods.Count} 个模组");
            }
            else
            {
                Log.Warn("[ModSearchViewModel] Curseforge API 未返回任何模组");
            }
        }
        catch (System.Exception ex)
        {
            Log.Warn("[ModSearchViewModel] 从 Curseforge 加载 Mods 失败", ex);
        }

        return result;
    }

    /// <summary>
    /// 从 NexusMods 加载 Mods（支持分页）
    /// </summary>
    /// <param name="page">页码（从 1 开始）</param>
    /// <param name="pageSize">每页数量</param>
    /// <param name="searchTerm">搜索关键词（空字符串表示加载热门模组）</param>
    private async Task<LoadModsResult> LoadModsFromNexusModsAsync(int page = 1, int pageSize = 20, string searchTerm = "")
    {
        var result = new LoadModsResult();

        try
        {
            // 使用空字符串获取热门模组（按下载量降序），或使用指定关键词搜索
            var query = searchTerm ?? "";
            Log.Info($"[ModSearchViewModel] 从 NexusMods 搜索: '{query}' (第{page}页, 每页{pageSize}个)");
            var fetchResult = await FetchNexusModsForCurrentTypeAsync(query, page, pageSize, AppConfig.GetSettings().EnableNexusModsSearchCache);
            var nexusMods = fetchResult.Items;
            result.HasMore = fetchResult.HasMore;
            result.Categories = fetchResult.Categories;

            if (nexusMods != null && nexusMods.Count > 0)
            {
                foreach (var mod in nexusMods)
                {
                    if (mod.ModId <= 0)
                    {
                        Log.Warn($"[ModSearchViewModel] 跳过无效 NexusMods ModId: {mod.Name} (ModId={mod.ModId})");
                        continue;
                    }

                    var category = string.IsNullOrWhiteSpace(mod.Category) ? "未分类" : mod.Category;
                    var supportedGameVersions = await GetNexusSupportedGameVersionsAsync(mod.ModId);

                    // 解析更新时间（从 updatedAt ISO 8601 日期时间）
                    string lastUpdateTime = "未知";
                    if (mod.UpdatedAt != default)
                    {
                        try
                        {
                            lastUpdateTime = mod.UpdatedAt.ToString("yyyy-MM-dd");
                        }
                        catch
                        {
                            lastUpdateTime = "未知";
                        }
                    }

                    // 转换为 ModSearchItem
                    var searchItem = new ModSearchItem
                    {
                        Id = $"nexus-{mod.ModId}",
                        Name = mod.Name,
                        Summary = mod.Summary ?? "暂无描述",
                        Description = mod.Summary ?? "暂无描述",
                        IconUrl = mod.PictureUrl ?? "",
                        Author = mod.Author ?? "未知",
                        DownloadCount = mod.Downloads,  // 使用 GraphQL 返回的下载量
                        LastUpdateTime = lastUpdateTime,
                        Source = "NexusMods",
                        Category = category,
                        SupportedGameVersions = supportedGameVersions,
                        Rating = mod.Endorsements,  // 使用推荐数作为评分
                        Url = $"https://www.nexusmods.com/stardewvalley/mods/{mod.ModId}"
                    };

                    result.ModItems.Add(searchItem);
                }

                Log.Info($"[ModSearchViewModel] NexusMods 返回 {nexusMods.Count} 个模组");
            }
            else
            {
                Log.Warn("[ModSearchViewModel] NexusMods API 未返回任何模组");
            }
        }
        catch (SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsTokenExpiredException)
        {
            Log.Warn("[ModSearchViewModel] NexusMods Token 已过期");
            HandleNexusModsTokenExpired("LoadPopularModsFromNexusMods");
        }
        catch (System.Exception ex)
        {
            Log.Warn("[ModSearchViewModel] 从 NexusMods 加载 Mods 失败", ex);
        }

        return result;
    }

    /// <summary>
    /// 搜索 MOD
    /// </summary>
    [RelayCommand]
    private async Task SearchModsAsync()
    {
        await PerformSearchWithConfigCheckAsync();
    }

    private async Task SearchModsCoreAsync(bool resetPage)
    {
        // 如果搜索词为空，加载热门模组
        if (string.IsNullOrWhiteSpace(_activeSearchName))
        {
            if (resetPage)
                CurrentPage = 1;
            await LoadPopularModsAsync();
            return;
        }

        if (resetPage)
            CurrentPage = 1;

        IsLoading = true;
        StatusMessage = "正在搜索 MOD...";

        try
        {
            ModList.Clear();

            // 检测是否为ModID搜索（纯数字）
            bool isModIdSearch = _activeSearchName.All(char.IsDigit);

            if (isModIdSearch)
            {
                Log.Info($"[ModSearchViewModel] 检测到ModID搜索: {_activeSearchName}");
                StatusMessage = $"正在搜索 ModID: {_activeSearchName}...";

                // ModID搜索：直接获取指定Mod
                if (SelectedSource == "全部" || SelectedSource == "Curseforge")
                {
                    await SearchModByIdAsync(int.Parse(_activeSearchName));
                }

                if (SelectedSource == "全部" || SelectedSource == "NexusMods")
                {
                    await SearchNexusModByIdAsync(_activeSearchName);
                }
            }
            else
            {
                // 普通关键词搜索 - 收集所有结果后统一排序
                var allSearchResults = new List<ModSearchItem>();
                int fetchCount = SelectedSource == "全部" ? PageSize * 2 : PageSize;  // 全部来源时保留交错排序的冗余抓取

                if (SelectedSource == "全部" || SelectedSource == "Curseforge")
                {
                    var curseforgeResults = await SearchFromCurseforgeAsyncInternal(false, fetchCount);
                    allSearchResults.AddRange(curseforgeResults);
                }

                if (SelectedSource == "全部" || SelectedSource == "NexusMods")
                {
                    var nexusResults = await SearchFromNexusModsAsyncInternal(fetchCount);
                    allSearchResults.AddRange(nexusResults);
                }

                UpdateModTypes(allSearchResults.Select(item => item.Category));

                // ✅ 分组排序策略：两个来源各自排序，然后交错合并
                var sortedCurseforge = allSearchResults.Where(m => m.Source == "Curseforge").OrderByDescending(m => m.DownloadCount).ToList();
                var sortedNexusMods = allSearchResults.Where(m => m.Source == "NexusMods").OrderByDescending(m => m.DownloadCount).ToList();

                Log.Info($"[ModSearchViewModel] 搜索分组排序：Curseforge {sortedCurseforge.Count} 个，NexusMods {sortedNexusMods.Count} 个");

                // 交错合并
                var displayResults = new List<ModSearchItem>();
                int maxCount = Math.Max(sortedCurseforge.Count, sortedNexusMods.Count);

                for (int i = 0; i < maxCount && displayResults.Count < PageSize; i++)
                {
                    if (i < sortedCurseforge.Count && displayResults.Count < PageSize)
                    {
                        displayResults.Add(sortedCurseforge[i]);
                    }
                    if (i < sortedNexusMods.Count && displayResults.Count < PageSize)
                    {
                        displayResults.Add(sortedNexusMods[i]);
                    }
                }

                Log.Info($"[ModSearchViewModel] 搜索交错合并：显示 {displayResults.Count} 个（Curseforge: {displayResults.Count(m => m.Source == "Curseforge")}, NexusMods: {displayResults.Count(m => m.Source == "NexusMods")}）");

                // 添加到显示列表
                foreach (var item in displayResults)
                {
                    ModList.Add(item);
                    _ = item.LoadIconAsync();
                }

                LocalizationDisplayHelper.ApplyLocalizationInBackground(displayResults);

                // 更新分页状态（任一来源还有结果就继续）
                bool hasMore = (sortedCurseforge.Count >= fetchCount) || (sortedNexusMods.Count >= fetchCount);
                if (hasMore)
                {
                    TotalPages = CurrentPage + 1;
                }
                else
                {
                    TotalPages = CurrentPage;
                }
                UpdatePaginationState();
            }

            TotalCount = ModList.Count;

            if (TotalCount > 0)
            {
                StatusMessage = $"找到 {TotalCount} 个 MOD";
            }
            else
            {
                StatusMessage = "未找到匹配的 MOD";
            }
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "[ModSearchViewModel] 搜索 MOD 失败");
            StatusMessage = $"搜索失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 从 Curseforge 搜索 MOD（支持模糊搜索和类型筛选）
    /// </summary>
    /// <param name="isPopular">是否加载热门模组（使用空搜索）</param>
    private async Task SearchFromCurseforgeAsync(bool isPopular = false)
    {
        try
        {
            // 如果是热门模组，使用空字符串搜索
            var searchTerm = isPopular ? "" : SearchName;
            searchTerm = isPopular ? "" : _activeSearchName;
            Log.Info($"[ModSearchViewModel] 调用 Curseforge API 搜索: '{searchTerm}'（第{CurrentPage}页）");

            // 计算分页参数
            int skip = (CurrentPage - 1) * PageSize;

            var curseforgeMods = await CurseforgeApiService.SearchModsAsync(searchTerm, pageSize: PageSize, index: skip);
            Log.Info($"[ModSearchViewModel] Curseforge API 返回 {curseforgeMods?.Count ?? 0} 个结果");

            if (curseforgeMods == null || curseforgeMods.Count == 0)
            {
                Log.Warn("[ModSearchViewModel] Curseforge API 未返回任何模组");

                // 如果是第一页且没有结果，设置总页数为 1
                if (CurrentPage == 1)
                {
                    TotalPages = 1;
                    UpdatePaginationState();
                }
                return;
            }

            // 收集所有类型（用于类型筛选）
            var allCategories = new HashSet<string>();
            var addedCount = 0;

            foreach (var mod in curseforgeMods)
            {
                // 收集类型
                if (mod.Categories != null)
                {
                    foreach (var category in mod.Categories)
                    {
                        allCategories.Add(category.Name);
                    }
                }

                // 类型筛选
                if (SelectedModType != "全部" &&
                    (mod.Categories == null || !mod.Categories.Any(c => c.Name == SelectedModType)))
                {
                    continue;
                }

                // 解析更新时间
                string lastUpdateTime = "未知";
                if (!string.IsNullOrEmpty(mod.DateModified))
                {
                    if (DateTime.TryParse(mod.DateModified, out DateTime dateModified))
                    {
                        lastUpdateTime = dateModified.ToString("yyyy-MM-dd");
                    }
                }

                // 转换为 ModSearchItem
                var searchItem = new ModSearchItem
                {
                    Id = $"curse-{mod.Id}",
                    Name = mod.Name,
                    Summary = mod.Summary ?? "暂无描述",
                    Description = mod.Summary ?? "暂无描述",
                    IconUrl = mod.Logo?.ThumbnailUrl ?? mod.Logo?.Url ?? "",
                    Author = "Unknown", // Curseforge API 搜索结果中没有作者信息
                    DownloadCount = mod.DownloadCount,
                    LastUpdateTime = lastUpdateTime,
                    Source = "Curseforge",
                    Category = mod.Categories?.FirstOrDefault()?.Name ?? "未分类",
                    SupportedGameVersions = mod.LatestFile?.GameVersion ?? new List<string>(),
                    Rating = 0,
                    Url = mod.Links?.WebsiteUrl ?? ""
                };

                ModList.Add(searchItem);

                // 异步加载图标（不阻塞UI）
                _ = searchItem.LoadIconAsync();

                addedCount++;
            }

            Log.Info($"[ModSearchViewModel] 添加了 {addedCount} 个模组到列表");

            // 更新总页数（Curseforge 限制最多 10000 个结果）
            // 如果返回的结果少于 PageSize，说明是最后一页
            if (curseforgeMods.Count < PageSize)
            {
                TotalPages = CurrentPage;
            }
            else
            {
                // 估算总页数（假设最多 10000 个结果）
                int estimatedTotal = Math.Min(10000, CurrentPage * PageSize + PageSize);
                TotalPages = (int)Math.Ceiling(estimatedTotal / (double)PageSize);
            }
            UpdatePaginationState();

            // 调试：输出第一个模组的 DateModified 信息
            if (curseforgeMods.Count > 0)
            {
                var firstMod = curseforgeMods[0];
                Log.Info($"[ModSearchViewModel] 第一个模组调试信息:");
                Log.Info($"  - Name: {firstMod.Name}");
                Log.Info($"  - DateModified: {firstMod.DateModified ?? "null"}");
                Log.Info($"  - Logo ThumbnailUrl: {firstMod.Logo?.ThumbnailUrl ?? "null"}");
                Log.Info($"  - Logo Url: {firstMod.Logo?.Url ?? "null"}");
                Log.Info($"  - GameVersion: {string.Join(", ", firstMod.LatestFile?.GameVersion ?? new List<string>())}");
            }

            // 更新类型列表（如果首次搜索或类型列表为空）
            if (ModTypes.Count <= 1 && allCategories.Count > 0)
            {
                // 保存当前选择的类型
                var previousSelection = SelectedModType;

                ModTypes.Clear();
                ModTypes.Add("全部");
                foreach (var category in allCategories.OrderBy(c => c))
                {
                    ModTypes.Add(category);
                }

                // 恢复之前的选择（如果仍在列表中），否则默认为"全部"
                if (ModTypes.Contains(previousSelection))
                {
                    SelectedModType = previousSelection;
                }
                else
                {
                    SelectedModType = "全部";
                }

                Log.Info($"[ModSearchViewModel] 更新类型列表，共 {allCategories.Count} 个类型");
            }

            var searchType = isPopular ? "热门模组" : $"搜索 '{SearchName}'";
            Log.Info($"[ModSearchViewModel] 从 Curseforge {searchType}获取 {curseforgeMods.Count} 个 MOD，筛选后 {ModList.Count} 个，共 {TotalPages} 页");
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "[ModSearchViewModel] 从 Curseforge 搜索失败");
        }
    }

    /// <summary>
    /// 从 Curseforge 搜索 MOD（内部方法，返回结果列表而不是直接添加到 ModList）
    /// </summary>
    /// <param name="isPopular">是否加载热门模组（使用空搜索）</param>
    /// <param name="pageSize">要获取的数据量（默认 PageSize，搜索时使用 PageSize * 2）</param>
    private async Task<List<ModSearchItem>> SearchFromCurseforgeAsyncInternal(bool isPopular = false, int? pageSize = null)
    {
        var results = new List<ModSearchItem>();
        var fetchCount = pageSize ?? PageSize;

        try
        {
            // 如果是热门模组，使用空字符串搜索
            var searchTerm = isPopular ? "" : SearchName;
            Log.Info($"[ModSearchViewModel] [内部] 调用 Curseforge API 搜索: '{searchTerm}'（第{CurrentPage}页，获取{fetchCount}个）");

            var fetchResult = await FetchCurseforgeModsForCurrentTypeAsync(searchTerm, CurrentPage, fetchCount);
            var curseforgeMods = fetchResult.Items;
            Log.Info($"[ModSearchViewModel] [内部] Curseforge API 返回 {curseforgeMods?.Count ?? 0} 个结果");

            if (curseforgeMods == null || curseforgeMods.Count == 0)
            {
                Log.Warn("[ModSearchViewModel] [内部] Curseforge API 未返回任何模组");
                return results;
            }

            foreach (var mod in curseforgeMods)
            {
                // 解析更新时间
                string lastUpdateTime = "未知";
                if (!string.IsNullOrEmpty(mod.DateModified))
                {
                    if (DateTime.TryParse(mod.DateModified, out DateTime dateModified))
                    {
                        lastUpdateTime = dateModified.ToString("yyyy-MM-dd");
                    }
                }

                // 转换为 ModSearchItem
                var searchItem = new ModSearchItem
                {
                    Id = $"curse-{mod.Id}",
                    Name = mod.Name,
                    Summary = mod.Summary ?? "暂无描述",
                    Description = mod.Summary ?? "暂无描述",
                    IconUrl = mod.Logo?.ThumbnailUrl ?? mod.Logo?.Url ?? "",
                    Author = "Unknown",
                    DownloadCount = mod.DownloadCount,
                    LastUpdateTime = lastUpdateTime,
                    Source = "Curseforge",
                    Category = mod.Categories?.FirstOrDefault()?.Name ?? "未分类",
                    SupportedGameVersions = mod.LatestFile?.GameVersion ?? new List<string>(),
                    Rating = 0,
                    Url = mod.Links?.WebsiteUrl ?? ""
                };

                results.Add(searchItem);
            }

            Log.Info($"[ModSearchViewModel] [内部] 从 Curseforge 获取 {results.Count} 个模组");
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "[ModSearchViewModel] [内部] 从 Curseforge 搜索失败");
        }

        return results;
    }

    /// <summary>
    /// 从 NexusMods 搜索 MOD（支持分页）
    /// </summary>
    private async Task SearchFromNexusModsAsync()
    {
        try
        {
            var searchTerm = _activeSearchName;
            Log.Info($"[ModSearchViewModel] 从 NexusMods 搜索: '{searchTerm}' (第{CurrentPage}页)");

            var nexusMods = await SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsService.SearchModsAsync(
                query: searchTerm,
                page: CurrentPage,
                pageSize: PageSize,
                useCache: false
            );

            if (nexusMods != null && nexusMods.Count > 0)
            {
                var addedCount = 0;
                var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var mod in nexusMods)
                {
                    var category = string.IsNullOrWhiteSpace(mod.Category) ? "未分类" : mod.Category;
                    categories.Add(category);

                    if (SelectedModType != "全部" && !string.Equals(category, SelectedModType, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // 解析更新时间（从 updatedAt ISO 8601 日期时间）
                    string lastUpdateTime = "未知";
                    if (mod.UpdatedAt != default)
                    {
                        try
                        {
                            lastUpdateTime = mod.UpdatedAt.ToString("yyyy-MM-dd");
                        }
                        catch
                        {
                            lastUpdateTime = "未知";
                        }
                    }

                    // 转换为 ModSearchItem
                    var searchItem = new ModSearchItem
                    {
                        Id = $"nexus-{mod.ModId}",
                        Name = mod.Name,
                        Summary = mod.Summary ?? "暂无描述",
                        Description = mod.Summary ?? "暂无描述",
                        IconUrl = mod.PictureUrl ?? "",
                        Author = mod.Author ?? "未知",
                        DownloadCount = mod.Downloads,  // 使用 GraphQL 返回的下载量
                        LastUpdateTime = lastUpdateTime,
                        Source = "NexusMods",
                        Category = category,
                        SupportedGameVersions = new List<string>(),
                        Rating = mod.Endorsements,  // 使用推荐数作为评分
                        Url = $"https://www.nexusmods.com/stardewvalley/mods/{mod.ModId}"
                    };

                    ModList.Add(searchItem);

                    // 异步加载图标
                    _ = searchItem.LoadIconAsync();

                    addedCount++;
                }

                UpdateModTypes(categories);

                Log.Info($"[ModSearchViewModel] 从 NexusMods 搜索添加了 {addedCount} 个模组");

                // 更新分页状态
                if (nexusMods.Count < PageSize)
                {
                    TotalPages = CurrentPage;
                }
                else
                {
                    TotalPages = CurrentPage + 1; // 可能有下一页
                }
                UpdatePaginationState();
            }
            else
            {
                Log.Warn("[ModSearchViewModel] NexusMods 搜索未返回任何模组");

                // 如果是第一页且没有结果，设置总页数为 1
                if (CurrentPage == 1)
                {
                    TotalPages = 1;
                    UpdatePaginationState();
                }
            }
        }
        catch (SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsTokenExpiredException)
        {
            Log.Warn("[ModSearchViewModel] NexusMods Token 已过期");
            HandleNexusModsTokenExpired("SearchFromNexusMods");
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "[ModSearchViewModel] 从 NexusMods 搜索失败");
        }
    }

    /// <summary>
    /// 从 NexusMods 搜索 MOD（内部方法，返回结果列表而不是直接添加到 ModList）
    /// </summary>
    /// <param name="pageSize">要获取的数据量（默认 PageSize，搜索时使用 PageSize * 2）</param>
    private async Task<List<ModSearchItem>> SearchFromNexusModsAsyncInternal(int? pageSize = null)
    {
        var results = new List<ModSearchItem>();
        var fetchCount = pageSize ?? PageSize;

        try
        {
            var searchTerm = _activeSearchName;
            Log.Info($"[ModSearchViewModel] [内部] 从 NexusMods 搜索: '{searchTerm}' (第{CurrentPage}页，获取{fetchCount}个)");

            var fetchResult = await FetchNexusModsForCurrentTypeAsync(searchTerm, CurrentPage, fetchCount, false);
            var nexusMods = fetchResult.Items;

            if (nexusMods != null && nexusMods.Count > 0)
            {
                var categories = fetchResult.Categories;

                foreach (var mod in nexusMods)
                {
                    var category = string.IsNullOrWhiteSpace(mod.Category) ? "未分类" : mod.Category;
                    var supportedGameVersions = await GetNexusSupportedGameVersionsAsync(mod.ModId);

                    // 解析更新时间（从 updatedAt ISO 8601 日期时间）
                    string lastUpdateTime = "未知";
                    if (mod.UpdatedAt != default)
                    {
                        try
                        {
                            lastUpdateTime = mod.UpdatedAt.ToString("yyyy-MM-dd");
                        }
                        catch
                        {
                            lastUpdateTime = "未知";
                        }
                    }

                    // 转换为 ModSearchItem
                    var searchItem = new ModSearchItem
                    {
                        Id = $"nexus-{mod.ModId}",
                        Name = mod.Name,
                        Summary = mod.Summary ?? "暂无描述",
                        Description = mod.Summary ?? "暂无描述",
                        IconUrl = mod.PictureUrl ?? "",
                        Author = mod.Author ?? "未知",
                        DownloadCount = mod.Downloads,
                        LastUpdateTime = lastUpdateTime,
                        Source = "NexusMods",
                        Category = category,
                        SupportedGameVersions = supportedGameVersions,
                        Rating = mod.Endorsements,
                        Url = $"https://www.nexusmods.com/stardewvalley/mods/{mod.ModId}"
                    };

                    results.Add(searchItem);
                }

                UpdateModTypes(categories);

                Log.Info($"[ModSearchViewModel] [内部] 从 NexusMods 获取 {results.Count} 个模组");
            }
            else
            {
                Log.Warn("[ModSearchViewModel] [内部] NexusMods 搜索未返回任何模组");
            }
        }
        catch (SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsTokenExpiredException)
        {
            Log.Warn("[ModSearchViewModel] [内部] NexusMods Token 已过期");
            HandleNexusModsTokenExpired("SearchFromNexusModsInternal");
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "[ModSearchViewModel] [内部] 从 NexusMods 搜索失败");
        }

        return results;
    }

    /// <summary>
    /// 从 Curseforge 加载特色模组
    /// </summary>
    private async Task LoadFeaturedModsFromCurseforgeAsync()
    {
        try
        {
            Log.Info("[ModSearchViewModel] 开始调用 GetFeaturedModsAsync");
            var featuredResponse = await CurseforgeApiService.GetFeaturedModsAsync();
            Log.Info($"[ModSearchViewModel] GetFeaturedModsAsync 返回，结果: {featuredResponse != null}");

            var allMods = new List<CurseforgeApiService.CurseforgeModSearchItem>();

            // 尝试使用 Featured API
            if (featuredResponse?.Data != null)
            {
                if (featuredResponse.Data.Featured != null)
                {
                    Log.Info($"[ModSearchViewModel] Featured 模组数量: {featuredResponse.Data.Featured.Count}");
                    allMods.AddRange(featuredResponse.Data.Featured);
                }
                if (featuredResponse.Data.Popular != null)
                {
                    Log.Info($"[ModSearchViewModel] Popular 模组数量: {featuredResponse.Data.Popular.Count}");
                    allMods.AddRange(featuredResponse.Data.Popular);
                }
                if (featuredResponse.Data.RecentlyUpdated != null)
                {
                    Log.Info($"[ModSearchViewModel] RecentlyUpdated 模组数量: {featuredResponse.Data.RecentlyUpdated.Count}");
                    allMods.AddRange(featuredResponse.Data.RecentlyUpdated);
                }
            }

            Log.Info($"[ModSearchViewModel] Curseforge 特色模组 API 返回 {allMods.Count} 个模组");

            // 如果 Featured API 返回空数据，使用搜索 API 作为备选方案
            if (allMods.Count == 0)
            {
                Log.Warn("[ModSearchViewModel] Featured API 返回空数据，使用搜索 API 获取热门模组（按下载量排序）");
                allMods = await CurseforgeApiService.SearchModsAsync("", pageSize: 50);
                Log.Info($"[ModSearchViewModel] 搜索 API 返回 {allMods.Count} 个模组");
            }

            if (allMods.Count == 0)
            {
                Log.Warn("[ModSearchViewModel] 所有模组列表都为空，没有可显示的模组");
                return;
            }

            // 收集所有类型（用于类型筛选）
            var allCategories = new HashSet<string>();
            var addedCount = 0;

            foreach (var mod in allMods)
            {
                // 收集类型
                if (mod.Categories != null)
                {
                    foreach (var category in mod.Categories)
                    {
                        allCategories.Add(category.Name);
                    }
                }

                // 解析更新时间
                string lastUpdateTime = "未知";
                if (!string.IsNullOrEmpty(mod.DateModified))
                {
                    if (DateTime.TryParse(mod.DateModified, out DateTime dateModified))
                    {
                        lastUpdateTime = dateModified.ToString("yyyy-MM-dd");
                    }
                }

                // 转换为 ModSearchItem
                var searchItem = new ModSearchItem
                {
                    Id = $"curse-{mod.Id}",
                    Name = mod.Name,
                    Summary = mod.Summary ?? "暂无描述",
                    Description = mod.Summary ?? "暂无描述",
                    IconUrl = mod.Logo?.ThumbnailUrl ?? mod.Logo?.Url ?? "",
                    Author = "Unknown", // Curseforge API 搜索结果中没有作者信息
                    DownloadCount = mod.DownloadCount,
                    LastUpdateTime = lastUpdateTime,
                    Source = "Curseforge",
                    Category = mod.Categories?.FirstOrDefault()?.Name ?? "未分类",
                    SupportedGameVersions = mod.LatestFile?.GameVersion ?? new List<string>(),
                    Rating = 0,
                    Url = mod.Links?.WebsiteUrl ?? ""
                };

                ModList.Add(searchItem);

                // 异步加载图标（不阻塞UI）
                _ = searchItem.LoadIconAsync();

                addedCount++;
            }

            Log.Info($"[ModSearchViewModel] 添加了 {addedCount} 个特色模组到列表，ModList.Count = {ModList.Count}");

            // 更新类型列表
            if (ModTypes.Count <= 1 && allCategories.Count > 0)
            {
                // 保存当前选择的类型
                var previousSelection = SelectedModType;

                ModTypes.Clear();
                ModTypes.Add("全部");
                foreach (var category in allCategories.OrderBy(c => c))
                {
                    ModTypes.Add(category);
                }

                // 恢复之前的选择（如果仍在列表中），否则默认为"全部"
                if (ModTypes.Contains(previousSelection))
                {
                    SelectedModType = previousSelection;
                }
                else
                {
                    SelectedModType = "全部";
                }

                Log.Info($"[ModSearchViewModel] 更新类型列表，共 {allCategories.Count} 个类型");
            }
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "[ModSearchViewModel] 从 Curseforge 加载特色模组失败");
        }
    }

    /// <summary>
    /// 重置搜索条件
    /// </summary>
    [RelayCommand]
    private async Task ResetSearchAsync()
    {
        SearchName = "";
        _activeSearchName = string.Empty;
        SelectedGameVersion = "全部";
        SearchGameVersion = "全部";
        SelectedSource = "全部";
        SelectedCategory = "全部";
        SelectedModType = "全部";
        ModList.Clear();
        TotalCount = 0;

        // 重置后重新加载热门模组
        await LoadPopularModsAsync();
    }

    /// <summary>
    /// 通过ModID搜索并获取单个MOD详情
    /// </summary>
    private async Task SearchModByIdAsync(int modId)
    {
        try
        {
            Log.Info($"[ModSearchViewModel] 通过ModID搜索: {modId}");

            // 调用GetModInfoAsync获取单个Mod详情
            var modInfo = await CurseforgeApiService.GetModInfoAsync(modId);

            if (modInfo == null)
            {
                Log.Warn($"[ModSearchViewModel] 未找到ModID为 {modId} 的模组");
                StatusMessage = $"未找到 ModID 为 {modId} 的模组";
                return;
            }

            // 输出调试信息
            Log.Info($"[ModSearchViewModel] ModID搜索结果:");
            Log.Info($"  - Name: {modInfo.Name}");
            Log.Info($"  - Summary: {modInfo.Summary ?? "null"}");
            Log.Info($"  - DownloadCount: {modInfo.DownloadCount}");
            Log.Info($"  - DateModified: {modInfo.DateModified ?? "null"}");
            Log.Info($"  - Logo ThumbnailUrl: {modInfo.Logo?.ThumbnailUrl ?? "null"}");
            Log.Info($"  - Logo Url: {modInfo.Logo?.Url ?? "null"}");
            Log.Info($"  - Categories: {modInfo.Categories?.Count ?? 0} 个");
            Log.Info($"  - LatestFile: {(modInfo.LatestFile != null ? "存在" : "null")}");

            // 解析更新时间
            string lastUpdateTime = "未知";
            if (!string.IsNullOrEmpty(modInfo.DateModified))
            {
                if (DateTime.TryParse(modInfo.DateModified, out DateTime dateModified))
                {
                    lastUpdateTime = dateModified.ToString("yyyy-MM-dd");
                }
            }

            // 转换为ModSearchItem
            var searchItem = new ModSearchItem
            {
                Id = $"curse-{modInfo.Id}",
                Name = modInfo.Name,
                Summary = modInfo.Summary ?? "暂无描述",
                Description = modInfo.Description ?? modInfo.Summary ?? "暂无描述",
                IconUrl = modInfo.Logo?.ThumbnailUrl ?? modInfo.Logo?.Url ?? "",
                Author = modInfo.Authors?.FirstOrDefault()?.Name ?? "Unknown",
                DownloadCount = modInfo.DownloadCount,
                LastUpdateTime = lastUpdateTime,
                Source = "Curseforge",
                Category = modInfo.Categories?.FirstOrDefault()?.Name ?? "未分类",
                SupportedGameVersions = modInfo.LatestFile?.GameVersion ?? new List<string>(),
                Rating = 0,
                Url = modInfo.Links?.WebsiteUrl ?? ""
            };

            await LocalizationDisplayHelper.ApplyLocalizationAsync(searchItem);

            ModList.Add(searchItem);

            // 异步加载图标
            _ = searchItem.LoadIconAsync();

            Log.Info($"[ModSearchViewModel] 通过ModID找到模组: {modInfo.Name}");
            StatusMessage = $"找到模组: {modInfo.Name}";
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, $"[ModSearchViewModel] 通过ModID {modId} 搜索失败");
            StatusMessage = $"搜索失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 通过 NexusMods ModID 搜索并获取单个 MOD 详情
    /// </summary>
    private async Task SearchNexusModByIdAsync(string modIdStr)
    {
        try
        {
            Log.Info($"[ModSearchViewModel] 通过 NexusMods ModID 搜索: {modIdStr}");

            if (!int.TryParse(modIdStr, out int modId))
            {
                Log.Warn($"[ModSearchViewModel] 无效的 NexusMods ModID: {modIdStr}");
                return;
            }

            var mod = await SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsService.GetModDetailsWithSearchFallbackAsync(modId);

            if (mod != null)
            {
                var lastUpdateTime = mod.UpdatedAt != default
                    ? mod.UpdatedAt.ToString("yyyy-MM-dd")
                    : "未知";

                UpdateModTypes(new[] { string.IsNullOrWhiteSpace(mod.Category) ? "未分类" : mod.Category });

                var searchItem = new ModSearchItem
                {
                    Id = $"nexus-{mod.ModId}",
                    Name = mod.Name,
                    Summary = mod.Summary ?? "暂无描述",
                    Description = mod.Description ?? mod.Summary ?? "暂无描述",
                    IconUrl = mod.PictureUrl ?? mod.PictureUrlLegacy ?? string.Empty,
                    Author = string.IsNullOrWhiteSpace(mod.Author) ? "未知" : mod.Author,
                    DownloadCount = mod.Downloads,
                    LastUpdateTime = lastUpdateTime,
                    Source = "NexusMods",
                    Category = string.IsNullOrWhiteSpace(mod.Category) ? "未分类" : mod.Category,
                    SupportedGameVersions = await GetNexusSupportedGameVersionsAsync(mod.ModId),
                    Rating = mod.Endorsements,
                    Url = $"https://www.nexusmods.com/stardewvalley/mods/{mod.ModId}"
                };

                await LocalizationDisplayHelper.ApplyLocalizationAsync(searchItem);

                ModList.Add(searchItem);

                // 异步加载图标
                _ = searchItem.LoadIconAsync();

                Log.Info($"[ModSearchViewModel] 通过 NexusMods ModID 找到模组: {mod.Name}");
                StatusMessage = $"找到模组: {mod.Name}";
            }
            else
            {
                Log.Warn($"[ModSearchViewModel] NexusMods 未找到 ModID 为 {modId} 的模组");
                StatusMessage = $"未找到 ModID 为 {modId} 的模组";
            }
        }
        catch (SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsTokenExpiredException)
        {
            Log.Warn("[ModSearchViewModel] NexusMods Token 已过期");
            HandleNexusModsTokenExpired("SearchNexusModById");
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, $"[ModSearchViewModel] 通过 NexusMods ModID {modIdStr} 搜索失败");
            StatusMessage = $"搜索失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 查看 MOD 详情
    /// </summary>
    [RelayCommand]
    private async void ViewModDetails(ModSearchItem mod)
    {
        Log.Info($"[ModSearchViewModel] 查看 MOD 详情: {mod.Name}");

        // 获取 MainWindowViewModel 并设置选中的 MOD
        if (System.Windows.Application.Current.MainWindow is MainWindow mainWindow &&
            mainWindow.DataContext is MainWindowViewModel mainViewModel)
        {
            // 如果是 Curseforge MOD，获取完整信息（包括作者）
            if (mod.Source == "Curseforge" && mod.Id.StartsWith("curse-"))
            {
                try
                {
                    // 从 mod.Id 中提取 modId（格式：curse-{modId}）
                    string modIdStr = mod.Id.Substring(6); // 去掉 "curse-" 前缀
                    if (int.TryParse(modIdStr, out int modId))
                    {
                        Log.Info($"[ModSearchViewModel] 获取 Curseforge MOD 完整信息: modId={modId}");

                        // 获取完整 MOD 信息
                        var modInfo = await CurseforgeApiService.GetModInfoAsync(modId);
                        if (modInfo != null && modInfo.Authors != null && modInfo.Authors.Count > 0)
                        {
                            // 更新作者信息
                            mod.Author = modInfo.Authors.FirstOrDefault()?.Name ?? "Unknown";
                            Log.Info($"[ModSearchViewModel] 已更新作者信息: {mod.Author}");
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    Log.Error(ex, "[ModSearchViewModel] 获取 MOD 完整信息失败，将使用默认作者信息");
                }
            }

            if (mainWindow.RightContentScrollViewer != null)
            {
                mainViewModel.DownloadModsScrollOffset = mainWindow.RightContentScrollViewer.VerticalOffset;
            }

            // 设置选中的 MOD
            mainViewModel.SelectedModSearch = mod;
            mainViewModel.ModDetailsBackPage = PageType.Download;

            // 切换到 MOD 详情页面（整页显示）
            mainViewModel.CurrentPage = PageType.ModDetails;

            // 异步加载 MOD 详情
            if (mainViewModel.LeftPanelContent is ModDetailsViewModel detailsViewModel)
            {
                await detailsViewModel.LoadModAsync(mod.Id);
            }
        }
    }

    /// <summary>
    /// 生成模拟数据（临时）
    /// </summary>
    /// <summary>
    /// 下一页
    /// </summary>
    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (HasNextPage && !IsLoading)
        {
            CurrentPage++;
            await ReloadCurrentResultsAsync(resetPage: false);
        }
    }

    /// <summary>
    /// 上一页
    /// </summary>
    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (HasPreviousPage && !IsLoading)
        {
            CurrentPage--;
            await ReloadCurrentResultsAsync(resetPage: false);
        }
    }

    /// <summary>
    /// 跳转到指定页
    /// </summary>
    [RelayCommand]
    private async Task GoToPageAsync(int pageNumber)
    {
        if (pageNumber >= 1 && pageNumber <= TotalPages && pageNumber != CurrentPage && !IsLoading)
        {
            CurrentPage = pageNumber;
            await ReloadCurrentResultsAsync(resetPage: false);
        }
    }

    /// <summary>
    /// 更新分页状态
    /// </summary>
    private void UpdatePaginationState()
    {
        HasPreviousPage = CurrentPage > 1;
        HasNextPage = CurrentPage < TotalPages;

        // 如果总页数为0，至少有1页
        if (TotalPages == 0)
            TotalPages = 1;

        // 当总页数大于1时显示分页控件
        IsPaginationVisible = TotalPages > 1;

        // 生成页码列表（显示最多7个页码，当前页在中间，使用省略号）
        PageNumbers.Clear();
        if (TotalPages <= 7)
        {
            // 总页数小于等于7，显示所有页码
            for (int i = 1; i <= TotalPages; i++)
            {
                PageNumbers.Add(i);
            }
        }
        else
        {
            // 总页数大于7，需要省略号
            if (CurrentPage <= 4)
            {
                // 当前页在前面：1 2 3 4 5 6 ... 50
                for (int i = 1; i <= 6; i++)
                {
                    PageNumbers.Add(i);
                }
                PageNumbers.Add(-1); // 省略号
                PageNumbers.Add(TotalPages);
            }
            else if (CurrentPage >= TotalPages - 3)
            {
                // 当前页在后面：1 ... 45 46 47 48 49 50
                PageNumbers.Add(1);
                PageNumbers.Add(-1); // 省略号
                for (int i = TotalPages - 5; i <= TotalPages; i++)
                {
                    PageNumbers.Add(i);
                }
            }
            else
            {
                // 当前页在中间：1 ... 10 11 12 13 14 ... 50
                PageNumbers.Add(1);
                PageNumbers.Add(-1); // 省略号
                for (int i = CurrentPage - 2; i <= CurrentPage + 2; i++)
                {
                    PageNumbers.Add(i);
                }
                PageNumbers.Add(-1); // 省略号
                PageNumbers.Add(TotalPages);
            }
        }
    }
}
