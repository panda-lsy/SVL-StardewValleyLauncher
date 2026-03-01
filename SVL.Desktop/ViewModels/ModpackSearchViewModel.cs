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
using SVL.Desktop.Controls;
using SVL.Desktop.Models;
using SVL.Desktop.Utilities;

namespace SVL.Desktop.ViewModels;

/// <summary>
/// 整合包搜索页面 ViewModel（用于搜索和下载整合包）
/// 复用 ModSearchViewModel 的搜索和分页逻辑
/// </summary>
public partial class ModpackSearchViewModel : ObservableObject
{
    public ModpackSearchViewModel()
    {
        try
        {
            var settings = AppConfig.GetSettings();
            // 使用 Mods 的默认源配置作为整合包的默认源
            var defaultSource = settings.ModDefaultSource ?? "全部";

            // 防御：确保是合法值
            if (!Sources.Contains(defaultSource))
                defaultSource = "全部";

            SelectedSource = defaultSource;
            Log.Info($"[ModpackSearchViewModel] 默认下载源(Modpacks) = {SelectedSource}");
        }
        catch (Exception ex)
        {
            Log.Warn("[ModpackSearchViewModel] 读取默认下载源失败，回退为 全部", ex);
            SelectedSource = "全部";
        }
    }

    // ===== 搜索条件 =====

    [ObservableProperty]
    private string _searchName = "";

    /// <summary>
    /// 搜索词变化时延迟触发搜索（避免频繁搜索）
    /// </summary>
    partial void OnSearchNameChanged(string value)
    {
        // 防抖：延迟500ms后触发搜索
        _debounceTimer?.Stop();
        _debounceTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _debounceTimer.Tick += async (s, e) =>
        {
            _debounceTimer?.Stop();
            await PerformSearchWithConfigCheckAsync();
        };
        _debounceTimer.Start();
    }

    private System.Windows.Threading.DispatcherTimer? _debounceTimer;

    /// <summary>
    /// 是否已经显示过 API 配置警告（防止重复提示）
    /// </summary>
    private bool _hasShownApiConfigWarning = false;

    /// <summary>
    /// 执行搜索（带配置检查）
    /// </summary>
    private async Task PerformSearchWithConfigCheckAsync()
    {
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
        await SearchModpacksAsync();
    }

    /// <summary>
    /// 检查 API 配置是否完整
    /// </summary>
    private List<string> CheckApiConfig()
    {
        var warnings = new List<string>();
        var settings = AppConfig.GetSettings();

        // 检查 Curseforge API（如果选择了 Curseforge 或 全部）
        if (SelectedSource == "全部" || SelectedSource == "Curseforge")
        {
            var curseforgeKey = settings.CurseforgeApiKey;
            if (string.IsNullOrEmpty(curseforgeKey))
            {
                warnings.Add("⚠️ Curseforge API 未配置\n请在设置页面配置 Curseforge API Key 以使用 Curseforge 整合包源。");
            }
        }

        // 检查 NexusMods API（如果选择了 NexusMods 或 全部）
        if (SelectedSource == "全部" || SelectedSource == "NexusMods")
        {
            var nexusKey = settings.NexusModsApiKey;
            var nexusToken = settings.NexusModsOAuthToken;

            if (string.IsNullOrEmpty(nexusKey) && string.IsNullOrEmpty(nexusToken))
            {
                warnings.Add("⚠️ NexusMods 未登录\n请在设置页面登录 NexusMods 账户以使用 Nexus Collections。");
            }
        }

        return warnings;
    }

    private void HandleNexusModsTokenExpired(string scene)
    {
        StatusMessage = "NexusMods 登录已过期，请重新登录";
        NexusAuthStateHelper.HandleTokenExpired(scene, "ModpackSearchViewModel", showNotification: true);
    }

    [ObservableProperty]
    private string _selectedSource = "全部";

    // ===== 数据列表 =====

    [ObservableProperty]
    private ObservableCollection<ModSearchItem> _modpackList = new();

    [ObservableProperty]
    private int _totalCount = 0;

    [ObservableProperty]
    private bool _isLoading = false;

    [ObservableProperty]
    private string _statusMessage = "加载热门整合包中...";

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

    public List<string> Sources { get; } = new()
    {
        "全部",
        "Curseforge",
        "NexusMods"
    };

    /// <summary>
    /// 初始化时加载热门整合包
    /// </summary>
    public async Task InitializeAsync()
    {
        Log.Info($"[ModpackSearchViewModel] InitializeAsync 被调用，ModpackList.Count = {ModpackList.Count}");
        if (ModpackList.Count == 0)
        {
            await LoadPopularModpacksAsync();
        }
        else
        {
            Log.Info("[ModpackSearchViewModel] ModpackList 已有数据，跳过初始化");
        }
    }

    /// <summary>
    /// 加载热门整合包（支持分页，同时从 Curseforge 和 Nexus 加载）
    /// </summary>
    private async Task LoadPopularModpacksAsync()
    {
        Log.Info($"[ModpackSearchViewModel] 开始加载第 {CurrentPage} 页热门整合包");
        IsLoading = true;
        StatusMessage = $"正在加载第 {CurrentPage} 页...";

        try
        {
            ModpackList.Clear();
            Log.Info("[ModpackSearchViewModel] ModpackList 已清空");

            // 计算分页参数
            int skip = (CurrentPage - 1) * PageSize;

            // 计算需要获取的数据量（双倍数据量，用于跨来源统一排序）
            int fetchCount = PageSize * 2;

            // 并行加载 Curseforge 和 Nexus 的热门整合包
            var curseforgeTask = (SelectedSource == "全部" || SelectedSource == "Curseforge")
                ? LoadModpacksFromCurseforgeAsync(skip, fetchCount)
                : Task.CompletedTask;

            var nexusTask = (SelectedSource == "全部" || SelectedSource == "NexusMods")
                ? LoadCollectionsFromNexusAsync(CurrentPage, fetchCount)
                : Task.CompletedTask;

            await Task.WhenAll(curseforgeTask, nexusTask);

            // 处理 Curseforge 结果
            bool curseforgeHasMore = false;
            var curseforgeItems = new List<ModSearchItem>();
            if (SelectedSource == "全部" || SelectedSource == "Curseforge")
            {
                if (curseforgeTask.Status == TaskStatus.RanToCompletion)
                {
                    curseforgeItems = await ((Task<List<ModSearchItem>>)curseforgeTask);
                    Log.Info($"[ModpackSearchViewModel] 收集了 {curseforgeItems.Count} 个 Curseforge 整合包");
                    curseforgeHasMore = curseforgeItems.Count >= fetchCount;
                }
            }

            // 处理 Nexus 结果
            bool nexusHasMore = false;
            var nexusItems = new List<ModSearchItem>();
            if (SelectedSource == "全部" || SelectedSource == "NexusMods")
            {
                if (nexusTask.Status == TaskStatus.RanToCompletion)
                {
                    nexusItems = await ((Task<List<ModSearchItem>>)nexusTask);
                    Log.Info($"[ModpackSearchViewModel] 收集了 {nexusItems.Count} 个 Nexus Collections");
                    nexusHasMore = nexusItems.Count >= fetchCount;
                }
            }

            // ✅ 分组排序策略：两个来源各自排序，然后交错合并
            var sortedCurseforge = curseforgeItems.OrderByDescending(m => m.DownloadCount).ToList();
            var sortedNexus = nexusItems.OrderByDescending(m => m.DownloadCount).ToList();

            Log.Info($"[ModpackSearchViewModel] 分组排序完成：Curseforge {sortedCurseforge.Count} 个，Nexus {sortedNexus.Count} 个");

            // 交错合并（保证两个来源都能按比例展示）
            var displayItems = new List<ModSearchItem>();
            int maxCount = Math.Max(sortedCurseforge.Count, sortedNexus.Count);

            for (int i = 0; i < maxCount && displayItems.Count < PageSize; i++)
            {
                // 交替添加：Curseforge 第 i 个，Nexus 第 i 个
                if (i < sortedCurseforge.Count && displayItems.Count < PageSize)
                {
                    displayItems.Add(sortedCurseforge[i]);
                }
                if (i < sortedNexus.Count && displayItems.Count < PageSize)
                {
                    displayItems.Add(sortedNexus[i]);
                }
            }

            Log.Info($"[ModpackSearchViewModel] 交错合并后显示 {displayItems.Count} 个整合包（Curseforge: {displayItems.Count(m => m.Source == "Curseforge")}, Nexus: {displayItems.Count(m => m.Source == "NexusMods")}）");

            // 将排序后的整合包添加到显示列表
            foreach (var item in displayItems)
            {
                ModpackList.Add(item);
                _ = item.LoadIconAsync();
            }

            // 更新分页状态（任一来源有更多结果就显示下一页）
            if (displayItems.Count == 0)
            {
                TotalPages = 1;
            }
            else if (curseforgeHasMore || nexusHasMore)
            {
                TotalPages = CurrentPage + 1; // 至少有一个来源还有更多结果
            }
            else
            {
                TotalPages = CurrentPage; // 所有来源都已到最后一页
            }
            UpdatePaginationState();

            TotalCount = ModpackList.Count;
            Log.Info($"[ModpackSearchViewModel] 第 {CurrentPage} 页加载完成，共 {TotalCount} 个整合包");

            if (TotalCount > 0)
            {
                StatusMessage = $"第 {CurrentPage}/{TotalPages} 页（共 {TotalCount} 个整合包）";
            }
            else
            {
                StatusMessage = "未找到整合包";
            }
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "[ModpackSearchViewModel] 加载热门整合包失败");
            StatusMessage = $"加载失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void ViewModpackDetails(ModSearchItem modpack)
    {
        if (modpack == null)
            return;

        if (System.Windows.Application.Current.MainWindow is MainWindow mainWindow &&
            mainWindow.DataContext is MainWindowViewModel mainViewModel)
        {
            mainViewModel.SelectedModSearch = modpack;
            mainViewModel.ModDetailsBackPage = PageType.Download;
            mainViewModel.CurrentPage = PageType.ModDetails;

            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (mainViewModel.LeftPanelContent is ModDetailsViewModel detailsViewModel)
                {
                    await detailsViewModel.LoadModpackAsync(modpack);
                }
            }));

            Log.Info($"[ModpackSearchViewModel] 打开整合包详情页: {modpack.Name}");
        }
    }
    /// <summary>
    /// 从 Curseforge 加载 Modpacks
    /// </summary>
    private async Task<List<ModSearchItem>> LoadModpacksFromCurseforgeAsync(int skip, int pageSize)
    {
        var results = new List<ModSearchItem>();

        if (!CurseforgeApiService.HasApiKey)
        {
            Log.Warn("[ModpackSearchViewModel] Curseforge API 未配置，跳过加载");
            return results;
        }

        try
        {
            Log.Info($"[ModpackSearchViewModel] 从 Curseforge 搜索整合包（跳过{skip}个）");
            var modpacks = await CurseforgeApiService.SearchModpacksAsync(
                searchQuery: "",
                gameId: 669,
                pageSize: pageSize,
                index: skip
            );

            if (modpacks != null && modpacks.Count > 0)
            {
                foreach (var item in modpacks)
                {
                    var logo = item.Logo?.ThumbnailUrl ?? item.Logo?.Url;
                    results.Add(new ModSearchItem
                    {
                        Id = $"cfpack-{item.Id}",
                        Name = item.Name,
                        Summary = item.Summary ?? "Curseforge Modpack",
                        Description = item.Summary ?? "",
                        IconUrl = logo ?? "",
                        Author = "Curseforge",
                        DownloadCount = item.DownloadCount,
                        LastUpdateTime = ParseDateTime(item.DateModified),
                        Source = "Curseforge",
                        Category = "Modpack",
                        SupportedGameVersions = item.LatestFile?.GameVersion ?? new List<string>(),
                        Rating = 0,
                        Url = item.Links?.WebsiteUrl ?? $"https://www.curseforge.com/stardewvalley/modpacks/{item.Slug}"
                    });
                }

                Log.Info($"[ModpackSearchViewModel] Curseforge 返回 {modpacks.Count} 个整合包");
            }
            else
            {
                Log.Warn("[ModpackSearchViewModel] Curseforge API 未返回任何整合包");
            }
        }
        catch (System.Exception ex)
        {
            Log.Warn("[ModpackSearchViewModel] 从 Curseforge 加载整合包失败", ex);
        }

        return results;
    }

    /// <summary>
    /// 从 NexusMods 加载 Collections
    /// </summary>
    private async Task<List<ModSearchItem>> LoadCollectionsFromNexusAsync(int page, int pageSize)
    {
        var results = new List<ModSearchItem>();
        var settings = AppConfig.GetSettings();
        var hasNexusLogin = !string.IsNullOrWhiteSpace(settings.NexusModsOAuthToken);

        if (!hasNexusLogin)
        {
            Log.Warn("[ModpackSearchViewModel] NexusMods 未登录，跳过加载 Collections");
            return results;
        }

        try
        {
            Log.Info($"[ModpackSearchViewModel] 从 NexusMods 搜索 Collections (第{page}页)");
            var collections = await SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsService.SearchCollectionsAsync(
                query: "",
                page: page,
                pageSize: pageSize,
                useCache: settings.EnableNexusModsSearchCache
            );

            if (collections != null && collections.Count > 0)
            {
                foreach (var col in collections)
                {
                    results.Add(new ModSearchItem
                    {
                        Id = $"nexuscol-{col.CollectionId}",
                        Name = col.Name,
                        Summary = col.Summary ?? "NexusMods Collection",
                        Description = col.Summary ?? "",
                        IconUrl = col.PictureUrl ?? "",
                        Author = col.Author ?? "NexusMods",
                        DownloadCount = col.Downloads,
                        LastUpdateTime = col.UpdatedAt.ToString("yyyy-MM-dd"),
                        Source = "NexusMods",
                        Category = "Collection",
                        SupportedGameVersions = new List<string>(),
                        Rating = 0,
                        Url = col.Url ?? $"https://www.nexusmods.com/stardewvalley/collections/{col.CollectionId}"
                    });
                }

                Log.Info($"[ModpackSearchViewModel] NexusMods 返回 {collections.Count} 个 Collections");
            }
            else
            {
                Log.Warn("[ModpackSearchViewModel] NexusMods API 未返回任何 Collections");
            }
        }
        catch (SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsTokenExpiredException)
        {
            Log.Warn("[ModpackSearchViewModel] NexusMods Token 已过期");
            HandleNexusModsTokenExpired("LoadCollectionsFromNexus");
        }
        catch (System.Exception ex)
        {
            Log.Warn("[ModpackSearchViewModel] 从 NexusMods 加载 Collections 失败", ex);
        }

        return results;
    }

    /// <summary>
    /// 搜索整合包
    /// </summary>
    [RelayCommand]
    private async Task SearchModpacksAsync()
    {
        // 如果搜索词为空，加载热门整合包
        if (string.IsNullOrWhiteSpace(SearchName))
        {
            await LoadPopularModpacksAsync();
            return;
        }

        // 重置到第一页
        CurrentPage = 1;

        IsLoading = true;
        StatusMessage = "正在搜索整合包...";

        try
        {
            ModpackList.Clear();

            // 普通关键词搜索 - 收集所有结果后统一排序
            var allSearchResults = new List<ModSearchItem>();
            int fetchCount = PageSize * 2;  // 获取双倍数据量

            if (SelectedSource == "全部" || SelectedSource == "Curseforge")
            {
                var curseforgeResults = await SearchModpacksFromCurseforgeInternal(fetchCount);
                allSearchResults.AddRange(curseforgeResults);
            }

            if (SelectedSource == "全部" || SelectedSource == "NexusMods")
            {
                var nexusResults = await SearchCollectionsInternal(fetchCount);
                allSearchResults.AddRange(nexusResults);
            }

            // ✅ 分组排序策略：两个来源各自排序，然后交错合并
            var sortedCurseforge = allSearchResults.Where(m => m.Source == "Curseforge").OrderByDescending(m => m.DownloadCount).ToList();
            var sortedNexus = allSearchResults.Where(m => m.Source == "NexusMods").OrderByDescending(m => m.DownloadCount).ToList();

            Log.Info($"[ModpackSearchViewModel] 搜索分组排序：Curseforge {sortedCurseforge.Count} 个，Nexus {sortedNexus.Count} 个");

            // 交错合并
            var displayResults = new List<ModSearchItem>();
            int maxCount = Math.Max(sortedCurseforge.Count, sortedNexus.Count);

            for (int i = 0; i < maxCount && displayResults.Count < PageSize; i++)
            {
                if (i < sortedCurseforge.Count && displayResults.Count < PageSize)
                {
                    displayResults.Add(sortedCurseforge[i]);
                }
                if (i < sortedNexus.Count && displayResults.Count < PageSize)
                {
                    displayResults.Add(sortedNexus[i]);
                }
            }

            Log.Info($"[ModpackSearchViewModel] 搜索交错合并：显示 {displayResults.Count} 个（Curseforge: {displayResults.Count(m => m.Source == "Curseforge")}, Nexus: {displayResults.Count(m => m.Source == "NexusMods")}）");

            // 添加到显示列表
            foreach (var item in displayResults)
            {
                ModpackList.Add(item);
                _ = item.LoadIconAsync();
            }

            // 更新分页状态（任一来源还有结果就继续）
            bool hasMore = (sortedCurseforge.Count >= fetchCount) || (sortedNexus.Count >= fetchCount);
            if (hasMore)
            {
                TotalPages = CurrentPage + 1;
            }
            else
            {
                TotalPages = CurrentPage;
            }
            UpdatePaginationState();

            TotalCount = ModpackList.Count;

            if (TotalCount > 0)
            {
                StatusMessage = $"找到 {TotalCount} 个整合包";
            }
            else
            {
                StatusMessage = "未找到匹配的整合包";
            }
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "[ModpackSearchViewModel] 搜索整合包失败");
            StatusMessage = $"搜索失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 从 Curseforge 搜索整合包（内部方法，返回结果列表）
    /// </summary>
    private async Task<List<ModSearchItem>> SearchModpacksFromCurseforgeInternal(int pageSize)
    {
        var results = new List<ModSearchItem>();

        if (!CurseforgeApiService.HasApiKey)
        {
            return results;
        }

        try
        {
            var searchTerm = SearchName ?? "";
            Log.Info($"[ModpackSearchViewModel] [内部] 调用 Curseforge API 搜索整合包: '{searchTerm}'（第{CurrentPage}页，获取{pageSize}个）");

            int skip = (CurrentPage - 1) * PageSize;

            var modpacks = await CurseforgeApiService.SearchModpacksAsync(
                searchQuery: searchTerm,
                gameId: 669,
                pageSize: pageSize,
                index: skip
            );
            Log.Info($"[ModpackSearchViewModel] [内部] Curseforge API 返回 {modpacks?.Count ?? 0} 个结果");

            if (modpacks == null || modpacks.Count == 0)
            {
                Log.Warn("[ModpackSearchViewModel] [内部] Curseforge API 未返回任何整合包");
                return results;
            }

            foreach (var item in modpacks)
            {
                var logo = item.Logo?.ThumbnailUrl ?? item.Logo?.Url;
                results.Add(new ModSearchItem
                {
                    Id = $"cfpack-{item.Id}",
                    Name = item.Name,
                    Summary = item.Summary ?? "Curseforge Modpack",
                    Description = item.Summary ?? "",
                    IconUrl = logo ?? "",
                    Author = "Curseforge",
                    DownloadCount = item.DownloadCount,
                    LastUpdateTime = ParseDateTime(item.DateModified),
                    Source = "Curseforge",
                    Category = "Modpack",
                    SupportedGameVersions = item.LatestFile?.GameVersion ?? new List<string>(),
                    Rating = 0,
                    Url = item.Links?.WebsiteUrl ?? $"https://www.curseforge.com/stardewvalley/modpacks/{item.Slug}"
                });
            }

            Log.Info($"[ModpackSearchViewModel] [内部] 从 Curseforge 获取 {results.Count} 个整合包");
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "[ModpackSearchViewModel] [内部] 从 Curseforge 搜索整合包失败");
        }

        return results;
    }

    /// <summary>
    /// 从 NexusMods 搜索 Collections（内部方法，返回结果列表）
    /// </summary>
    private async Task<List<ModSearchItem>> SearchCollectionsInternal(int pageSize)
    {
        var results = new List<ModSearchItem>();
        var settings = AppConfig.GetSettings();
        var hasNexusLogin = !string.IsNullOrWhiteSpace(settings.NexusModsOAuthToken);

        if (!hasNexusLogin)
        {
            return results;
        }

        try
        {
            var searchTerm = SearchName;
            Log.Info($"[ModpackSearchViewModel] [内部] 从 NexusMods 搜索 Collections: '{searchTerm}' (第{CurrentPage}页，获取{pageSize}个)");

            var collections = await SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsService.SearchCollectionsAsync(
                query: searchTerm,
                page: CurrentPage,
                pageSize: pageSize,
                useCache: settings.EnableNexusModsSearchCache
            );

            if (collections != null && collections.Count > 0)
            {
                foreach (var col in collections)
                {
                    results.Add(new ModSearchItem
                    {
                        Id = $"nexuscol-{col.CollectionId}",
                        Name = col.Name,
                        Summary = col.Summary ?? "NexusMods Collection",
                        Description = col.Summary ?? "",
                        IconUrl = col.PictureUrl ?? "",
                        Author = col.Author ?? "NexusMods",
                        DownloadCount = col.Downloads,
                        LastUpdateTime = col.UpdatedAt.ToString("yyyy-MM-dd"),
                        Source = "NexusMods",
                        Category = "Collection",
                        SupportedGameVersions = new List<string>(),
                        Rating = 0,
                        Url = col.Url ?? $"https://www.nexusmods.com/stardewvalley/collections/{col.CollectionId}"
                    });
                }

                Log.Info($"[ModpackSearchViewModel] [内部] 从 NexusMods 获取 {results.Count} 个 Collections");
            }
            else
            {
                Log.Warn("[ModpackSearchViewModel] [内部] NexusMods 搜索未返回任何 Collections");
            }
        }
        catch (SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsTokenExpiredException)
        {
            Log.Warn("[ModpackSearchViewModel] [内部] NexusMods Token 已过期");
            HandleNexusModsTokenExpired("SearchCollectionsInternal");
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "[ModpackSearchViewModel] [内部] 从 NexusMods 搜索 Collections 失败");
        }

        return results;
    }

    /// <summary>
    /// 重置搜索条件
    /// </summary>
    [RelayCommand]
    private async Task ResetSearchAsync()
    {
        SearchName = "";
        SelectedSource = "全部";
        ModpackList.Clear();
        TotalCount = 0;
        CurrentPage = 1;

        // 重置后重新加载热门整合包
        await LoadPopularModpacksAsync();
    }

    /// <summary>
    /// 下一页
    /// </summary>
    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (HasNextPage && !IsLoading)
        {
            CurrentPage++;
            await LoadPopularModpacksAsync();
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
            await LoadPopularModpacksAsync();
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
            await LoadPopularModpacksAsync();
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

    /// <summary>
    /// 解析日期时间字符串
    /// </summary>
    private string ParseDateTime(string? dateTimeStr)
    {
        if (string.IsNullOrWhiteSpace(dateTimeStr))
            return "未知";
        if (DateTime.TryParse(dateTimeStr, out DateTime dt))
            return dt.ToString("yyyy-MM-dd");
        return "未知";
    }
}
