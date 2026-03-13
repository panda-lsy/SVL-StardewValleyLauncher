using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SVL.Core.Download;
using SVL.Core.Download.NexusMods;
using SVL.Core.IO;
using SVL.Core.Logging;
using SVL.Core.Stardew.Localization;
using SVL.Core.Stardew.Instance;
using SVL.Core.Stardew.Mod;
using SVL.Core.Stardew.Mod.SMAPI;
using SVL.Core.Stardew.ResourceProject.NexusMods;
using SVL.Core.Utils;
using SVL.Desktop.Models;
using SVL.Desktop.Utilities;
using SVL.Desktop.Controls;

namespace SVL.Desktop.ViewModels;

/// <summary>
/// GamePathInfo 到 IStardewInstance 的适配器
/// </summary>
internal class GamePathInfoAdapter : IStardewInstance
{
    private readonly GamePathInfo _gamePathInfo;

    public GamePathInfoAdapter(GamePathInfo gamePathInfo)
    {
        _gamePathInfo = gamePathInfo;
        Description = gamePathInfo.DisplayVersion;
        Logo = gamePathInfo.CustomIcon ?? "";
        InstanceInfo = new StardewInstanceInfo
        {
            GameVersion = gamePathInfo.Version,
            SmapVersion = gamePathInfo.SMAPIVersion,
            Platform = "Windows"
        };
    }

    public string Path => _gamePathInfo.GamePath;
    public string Name => _gamePathInfo.Name;
    public StardewInstanceCardType CardType { get; set; }
    public string Description { get; set; }
    public string Logo { get; set; }
    public bool IsStarred => _gamePathInfo.IsFavorite;
    public bool EnableIsolation => _gamePathInfo.EnableIsolation;
    public bool IsSMAPIInstance => _gamePathInfo.IsSMAPIInstance;
    public StardewInstanceInfo InstanceInfo { get; set; }

    public void Load()
    {
        // GamePathInfo 已经加载完毕，无需额外操作
    }
}

/// <summary>
/// MOD 详情页面 ViewModel
/// </summary>
public partial class ModDetailsViewModel : ObservableObject
{
    private const string LocalizationContributionUrl = "https://svl-website.89b52195.er.aliyun-esa.net/contribute";
    private static readonly Dictionary<string, List<ModDependencyLink>> s_requiredModsCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly MainWindowViewModel? _mainViewModel;
    private static bool s_requiredModsExpandedPreference = true;
    private bool _hasHandledNexusTokenExpired;
    private bool _hasShownApiConfigWarning;
    private bool _isCurseforgeModpackDownloadStarting;
    private bool _isNexusCollectionInstalling;
    private CommunityLocalizationEntry? _communityLocalizationEntry;
    private NexusMod? _loadedNexusDetails;

    public ModDetailsViewModel(MainWindowViewModel? mainViewModel = null)
    {
        _mainViewModel = mainViewModel;
        Log.Info("[ModDetailsViewModel] 构造函数调用完成");
    }

    /// <summary>
    /// 下载命令（手动创建以确保兼容性）
    /// </summary>
    public IAsyncRelayCommand DownloadModCommand => new AsyncRelayCommand<object>(async (parameter) =>
    {
        Log.Info($"[ModDetailsViewModel] DownloadModCommand 被调用（手动命令），parameter type: {parameter?.GetType().Name}");

        if (parameter is ModVersionItem version)
        {
            await DownloadModAsync(version);
        }
        else
        {
            Log.Warn($"[ModDetailsViewModel] 参数类型错误，期望 ModVersionItem，实际: {parameter?.GetType().Name}");
        }
    });

    /// <summary>
    /// 另存为命令（只保存 ZIP 文件，不安装）
    /// </summary>
    public IAsyncRelayCommand SaveModAsCommand => new AsyncRelayCommand<object>(async (parameter) =>
    {
        Log.Info($"[ModDetailsViewModel] SaveModAsCommand 被调用，parameter type: {parameter?.GetType().Name}");

        if (parameter is ModVersionItem version)
        {
            await SaveModAsAsync(version);
        }
        else
        {
            Log.Warn($"[ModDetailsViewModel] 参数类型错误，期望 ModVersionItem，实际: {parameter?.GetType().Name}");
        }
    });

    /// <summary>
    /// 安装命令（用于 Nexus Collection 本地安装）
    /// </summary>
    public IAsyncRelayCommand InstallModCommand => new AsyncRelayCommand<object>(async (parameter) =>
    {
        Log.Info($"[ModDetailsViewModel] InstallModCommand 被调用，parameter type: {parameter?.GetType().Name}");

        if (parameter is ModVersionItem version)
        {
            await InstallModAsync(version);
        }
        else
        {
            Log.Warn($"[ModDetailsViewModel] 参数类型错误，期望 ModVersionItem，实际: {parameter?.GetType().Name}");
        }
    });

    [ObservableProperty]
    private ModSearchItem _mod = new();

    [ObservableProperty]
    private ObservableCollection<ModDependencyLink> _requiredMods = new();

    [ObservableProperty]
    private bool _isRequiredModsExpanded;

    [ObservableProperty]
    private ObservableCollection<ModDependencyLink> _hardConflictMods = new();

    [ObservableProperty]
    private ObservableCollection<ModDependencyLink> _functionalOverlapMods = new();

    [ObservableProperty]
    private ObservableCollection<string> _supportedGameVersions = new();

    [ObservableProperty]
    private ObservableCollection<GameVersionFilesGroup> _versionsByGameVersion = new();

    [ObservableProperty]
    private bool _isLoading = false;

    [ObservableProperty]
    private bool _isModpackMode = false;

    [ObservableProperty]
    private string _selectedGameVersion = "全部";

    [ObservableProperty]
    private ObservableCollection<string> _displayedGameVersions = new();

    // GitHub 版本列表：用于“兼容筛选 + 分页渲染”（避免选择高版本时遗漏低版本+，并减少一次性渲染量）
    private readonly List<ModVersionItem> _githubAllVersionItems = new();

    private const int GithubCompatiblePageSize = 5;

    [ObservableProperty]
    private int _githubCompatiblePage = 1;

    [ObservableProperty]
    private int _githubCompatibleTotalPages = 1;

    [ObservableProperty]
    private bool _githubCompatibleHasNextPage;

    [ObservableProperty]
    private bool _githubCompatibleHasPreviousPage;

    [ObservableProperty]
    private ObservableCollection<int> _githubCompatiblePageNumbers = new();

    public bool ShowGithubCompatiblePaging => IsGithubSource && SelectedGameVersion != "全部" && GithubCompatibleTotalPages > 1;

    public string GithubCompatiblePageText => $"{GithubCompatiblePage}/{GithubCompatibleTotalPages}";

    public bool IsGithubSource => string.Equals(Mod?.Source, "GitHub", StringComparison.OrdinalIgnoreCase) || string.Equals(Mod?.Id, "github-smapi", StringComparison.OrdinalIgnoreCase);

    public bool IsNexusSource => string.Equals(Mod?.Source, "NexusMods", StringComparison.OrdinalIgnoreCase);

    public bool IsCurseforgeSource => string.Equals(Mod?.Source, "Curseforge", StringComparison.OrdinalIgnoreCase);

    public bool IsGithubOrNexusSource => IsGithubSource || IsNexusSource;

    public bool IsNexusSmapiMod => IsNexusSource &&
                                   ((Mod?.Id?.IndexOf("2400", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                                    string.Equals(Mod?.Name, "SMAPI - Stardew Modding API", StringComparison.OrdinalIgnoreCase));

    public IRelayCommand GithubCompatibleNextPageCommand => new RelayCommand(() =>
    {
        if (!GithubCompatibleHasNextPage)
            return;

        GithubCompatiblePage++;
        UpdateGithubCompatiblePagination();
        OnPropertyChanged(nameof(DisplayedVersionsByGameVersion));
        OnPropertyChanged(nameof(GithubCompatiblePageText));
    });

    public IRelayCommand GithubCompatiblePrevPageCommand => new RelayCommand(() =>
    {
        if (!GithubCompatibleHasPreviousPage)
            return;

        GithubCompatiblePage--;
        UpdateGithubCompatiblePagination();
        OnPropertyChanged(nameof(DisplayedVersionsByGameVersion));
        OnPropertyChanged(nameof(GithubCompatiblePageText));
    });

    public IRelayCommand GithubCompatibleGoToPageCommand => new RelayCommand<int>((page) =>
    {
        if (page <= 0)
            return;

        if (page == GithubCompatiblePage)
            return;

        GithubCompatiblePage = page;
        UpdateGithubCompatiblePagination();
        OnPropertyChanged(nameof(DisplayedVersionsByGameVersion));
        OnPropertyChanged(nameof(GithubCompatiblePageText));
    });

    /// <summary>
    /// 是否有完整描述（用于控制 UI 显示）
    /// </summary>
    public bool HasFullDescription => !string.IsNullOrEmpty(Mod?.Description) && Mod.Description != Mod.Summary;

    /// <summary>
    /// 用于基本信息卡片显示的一段描述（优先摘要，否则使用完整描述）
    /// </summary>
    public string DisplayDescription
    {
        get
        {
            if (Mod == null)
                return string.Empty;

            if (!string.IsNullOrWhiteSpace(Mod.DisplaySummary))
                return Mod.DisplaySummary;

            if (!string.IsNullOrWhiteSpace(Mod.DisplayDescription))
                return Mod.DisplayDescription;

            return string.Empty;
        }
    }

    public bool HasAnyDescription => !string.IsNullOrWhiteSpace(DisplayDescription);
    public bool HasRequiredMods => RequiredMods.Count > 0;
    public int RequiredModsCount => RequiredMods.Count;
    public bool HasHardConflictMods => HardConflictMods.Count > 0;
    public int HardConflictModsCount => HardConflictMods.Count;
    public bool HasFunctionalOverlapMods => FunctionalOverlapMods.Count > 0;
    public int FunctionalOverlapModsCount => FunctionalOverlapMods.Count;
    public bool IsCollectionDetails => !string.IsNullOrWhiteSpace(Mod?.Id) && Mod.Id.StartsWith("nexuscol-", StringComparison.OrdinalIgnoreCase);
    public string CopyIdButtonText => IsCollectionDetails ? "尾链" : "ID";
    public string CopyIdNotificationLabel => IsCollectionDetails ? "尾链" : "ID";
    public string LocalizationContributor => Mod?.LocalizationContributor ?? string.Empty;

    [RelayCommand]
    private void ToggleRequiredModsExpanded()
    {
        if (!HasRequiredMods)
            return;

        IsRequiredModsExpanded = !IsRequiredModsExpanded;
        s_requiredModsExpandedPreference = IsRequiredModsExpanded;
    }

    private long TryGetNexusModId()
    {
        try
        {
            // 首选：Id = nexus-{modId}
            if (!string.IsNullOrWhiteSpace(Mod?.Id) && Mod.Id.StartsWith("nexus-", StringComparison.OrdinalIgnoreCase))
            {
                var modIdStr = Mod.Id.Substring(6);
                if (long.TryParse(modIdStr, out var id) && id > 0)
                    return id;
            }

            // 兜底：从 URL 提取 /mods/{id}
            if (!string.IsNullOrWhiteSpace(Mod?.Url))
            {
                var marker = "/mods/";
                var idx = Mod.Url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var start = idx + marker.Length;
                    var end = Mod.Url.IndexOfAny(new[] { '/', '?', '#' }, start);
                    var idText = end >= 0 ? Mod.Url.Substring(start, end - start) : Mod.Url.Substring(start);
                    if (long.TryParse(idText, out var id) && id > 0)
                        return id;
                }
            }

            // 兼容：Id 可能是纯数字
            if (!string.IsNullOrWhiteSpace(Mod?.Id) && long.TryParse(Mod.Id, out var pureId) && pureId > 0)
            {
                return pureId;
            }

            // 兼容：Id 可能是其它前缀（如 nexusmods-2400 / mod-2400）
            if (!string.IsNullOrWhiteSpace(Mod?.Id))
            {
                var m = Regex.Match(Mod.Id, @"(\d+)");
                if (m.Success && long.TryParse(m.Groups[1].Value, out var anyId) && anyId > 0)
                    return anyId;
            }
        }
        catch
        {
            // ignore
        }

        return 0;
    }

    private async Task<long> EnsureNexusModIdAsync()
    {
        var parsed = TryGetNexusModId();
        if (parsed > 0)
            return parsed;

        try
        {
            var query = !string.IsNullOrWhiteSpace(Mod?.Name) ? Mod.Name : "SMAPI";
            var results = await SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsService.SearchModsAsync(
                query,
                page: 1,
                pageSize: 50,
                useCache: SVL.Core.Config.AppConfig.GetSettings().EnableNexusModsSearchCache
            );

            var matched = results
                ?.FirstOrDefault(m => !string.IsNullOrWhiteSpace(Mod?.Url)
                    && !string.IsNullOrWhiteSpace(m?.ModId.ToString())
                    && Mod.Url.IndexOf($"/mods/{m.ModId}", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? results?.FirstOrDefault(m => string.Equals(m.Name, Mod?.Name, StringComparison.OrdinalIgnoreCase));

            if (matched != null && matched.ModId > 0)
            {
                Mod.Id = $"nexus-{matched.ModId}";
                if (string.IsNullOrWhiteSpace(Mod.Url))
                    Mod.Url = $"https://www.nexusmods.com/stardewvalley/mods/{matched.ModId}";

                return matched.ModId;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("[ModDetailsViewModel] 通过搜索解析 Nexus modId 失败", ex);
        }

        return 0;
    }

    private bool IsNexusUnauthorizedException(Exception ex)
    {
        return NexusAuthStateHelper.IsUnauthorized(ex);
    }

    private void HandleNexusModsTokenExpired(string scene, bool showNotification)
    {
        if (_hasHandledNexusTokenExpired)
            return;

        _hasHandledNexusTokenExpired = true;

        NexusAuthStateHelper.HandleTokenExpired(scene, "ModDetailsViewModel", showNotification);
    }

    private static System.Version TryParseGameVersionForSort(string gameVersionKey)
    {
        if (string.IsNullOrWhiteSpace(gameVersionKey))
            return new System.Version(0, 0);

        var normalized = gameVersionKey.Trim();
        var hasPlus = normalized.EndsWith("+", StringComparison.Ordinal);
        normalized = normalized.TrimEnd('+');

        var parts = normalized
            .Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p, out var value) ? value : 0)
            .ToList();

        if (parts.Count == 0)
            return new System.Version(0, 0);

        while (parts.Count < 4)
            parts.Add(0);

        if (hasPlus)
            parts[3] = parts[3] + 1;

        return new System.Version(parts[0], parts[1], parts[2], parts[3]);
    }

    private static string NormalizeGitHubGameVersionKey(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "未知";

        var text = raw.Trim();

        // GitHub SMAPI 的字段可能包含 "or later"，展示时统一为 x.y.z+
        var idx = text.IndexOf("or later", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
            text = text.Substring(0, idx).Trim();

        idx = text.IndexOf("or newer", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
            text = text.Substring(0, idx).Trim();

        // 统一追加 +（代表该版本及以上）
        if (!text.EndsWith("+", StringComparison.Ordinal))
            text += "+";

        return text;
    }

    private static string ExtractStardewGameVersionKeyFromNexusFileDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return "未知";

        // 常见格式："For Stardew 1.6.14" / "For Stardew Valley 1.6.14 or later"
        // 仅提取一个版本号，并在出现 or/and later/newer 时追加 +。
        var match = System.Text.RegularExpressions.Regex.Match(
            description,
            @"(?i)\bfor\s+stardew(?:\s+valley)?\s+(\d+(?:\.\d+){1,3})\b(?:\s*(?:or|and)\s*(?:later|newer)\b)?"
        );

        if (!match.Success)
        {
            // 兜底：不带 for 的描述也可能包含 "Stardew 1.x.x"
            match = System.Text.RegularExpressions.Regex.Match(
                description,
                @"(?i)\bstardew(?:\s+valley)?\s+(\d+(?:\.\d+){1,3})\b(?:\s*(?:or|and)\s*(?:later|newer)\b)?"
            );
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

    private static System.Version TryParseSmapiVersionForSort(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return new System.Version(0, 0);

        var normalized = version.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            normalized = normalized.Substring(1);

        return System.Version.TryParse(normalized, out var v) ? v : new System.Version(0, 0);
    }

    private void UpdateGithubCompatiblePagination()
    {
        if (!IsGithubSource || SelectedGameVersion == "全部")
        {
            GithubCompatibleTotalPages = 1;
            GithubCompatibleHasNextPage = false;
            GithubCompatibleHasPreviousPage = false;
            GithubCompatiblePageNumbers.Clear();
            OnPropertyChanged(nameof(ShowGithubCompatiblePaging));
            return;
        }

        var selectedMin = TryParseGameVersionForSort(SelectedGameVersion);
        var compatibleCount = _githubAllVersionItems.Count(v => TryParseGameVersionForSort(v.GameVersion) <= selectedMin);

        GithubCompatibleTotalPages = Math.Max(1, (int)Math.Ceiling(compatibleCount / (double)GithubCompatiblePageSize));
        GithubCompatiblePage = Math.Min(Math.Max(1, GithubCompatiblePage), GithubCompatibleTotalPages);
        GithubCompatibleHasPreviousPage = GithubCompatiblePage > 1;
        GithubCompatibleHasNextPage = GithubCompatiblePage < GithubCompatibleTotalPages;

        UpdateGithubCompatiblePageNumbers();

        OnPropertyChanged(nameof(ShowGithubCompatiblePaging));
        OnPropertyChanged(nameof(GithubCompatiblePageText));
    }

    private void UpdateGithubCompatiblePageNumbers()
    {
        GithubCompatiblePageNumbers.Clear();

        var totalPages = Math.Max(1, GithubCompatibleTotalPages);

        if (totalPages <= 7)
        {
            for (int i = 1; i <= totalPages; i++)
                GithubCompatiblePageNumbers.Add(i);
            return;
        }

        // 参照 ModSearch：最多显示 7 个按钮，中间用 -1 表示省略号
        GithubCompatiblePageNumbers.Add(1);

        var current = Math.Min(Math.Max(1, GithubCompatiblePage), totalPages);
        var start = Math.Max(2, current - 1);
        var end = Math.Min(totalPages - 1, current + 1);

        if (start > 2)
            GithubCompatiblePageNumbers.Add(-1);

        for (int i = start; i <= end; i++)
            GithubCompatiblePageNumbers.Add(i);

        if (end < totalPages - 1)
            GithubCompatiblePageNumbers.Add(-1);

        GithubCompatiblePageNumbers.Add(totalPages);
    }

    /// <summary>
    /// 当 Mod 属性变化时
    /// </summary>
    partial void OnModChanged(ModSearchItem value)
    {
        // 通知 HasFullDescription 属性已更改
        OnPropertyChanged(nameof(HasFullDescription));
        OnPropertyChanged(nameof(IsGithubSource));
        OnPropertyChanged(nameof(IsNexusSource));
        OnPropertyChanged(nameof(IsCurseforgeSource));
        OnPropertyChanged(nameof(IsGithubOrNexusSource));
        OnPropertyChanged(nameof(IsNexusSmapiMod));
        OnPropertyChanged(nameof(ShowGithubCompatiblePaging));

        // 描述相关
        OnPropertyChanged(nameof(DisplayDescription));
        OnPropertyChanged(nameof(HasAnyDescription));
        OnPropertyChanged(nameof(HasRequiredMods));
        OnPropertyChanged(nameof(HasHardConflictMods));
        OnPropertyChanged(nameof(HasFunctionalOverlapMods));
        OnPropertyChanged(nameof(IsCollectionDetails));
        OnPropertyChanged(nameof(CopyIdButtonText));
        OnPropertyChanged(nameof(CopyIdNotificationLabel));
        OnPropertyChanged(nameof(LocalizationContributor));
    }

    /// <summary>
    /// 初始化 MOD 详情
    /// </summary>
    public async Task LoadModAsync(string modId)
    {
        IsModpackMode = false;
        IsLoading = true;
        _communityLocalizationEntry = null;
        _loadedNexusDetails = null;
        RequiredMods.Clear();
        OnPropertyChanged(nameof(HasRequiredMods));
        OnPropertyChanged(nameof(RequiredModsCount));
        ClearCommunityRelationCollections();
        CurseforgeApiService.CurseforgeModInfo? curseforgeModInfo = null;

        try
        {
            // 从 MainWindowViewModel 获取选中的 MOD
            ModSearchItem selectedMod = null;
            if (System.Windows.Application.Current.MainWindow is MainWindow mainWindow &&
                mainWindow.DataContext is MainWindowViewModel mainViewModel)
            {
                selectedMod = mainViewModel.SelectedModSearch;
            }

            if (selectedMod != null)
            {
                Mod = selectedMod;
                await LocalizationDisplayHelper.ApplyLocalizationAsync(Mod);
                await LoadCommunityLocalizationRelationsAsync();
                if (string.Equals(Mod.LastUpdateTime, "0001-01-01", StringComparison.OrdinalIgnoreCase))
                    Mod.LastUpdateTime = string.Empty;

                RestoreOrInitializeRequirements();

                Log.Info($"[ModDetailsViewModel] 开始加载 MOD 详情: {Mod.Name}, Source: {Mod.Source}, Id: {Mod.Id}");

                // 异步加载 MOD 图标（不阻塞主流程）
                _ = Mod.LoadIconAsync();

                // 先按 ID 回填来源元数据（下载量/更新时间/Icon）
                if (Mod.Source == "Curseforge" && Mod.Id.StartsWith("curse-", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var modIdStr = Mod.Id.Substring(6);
                        if (int.TryParse(modIdStr, out var curseforgeModId) && curseforgeModId > 0)
                        {
                            var modInfo = await CurseforgeApiService.GetModInfoAsync(curseforgeModId);
                            if (modInfo != null)
                            {
                                curseforgeModInfo = modInfo;
                                if (!string.IsNullOrWhiteSpace(modInfo.Name))
                                    Mod.Name = modInfo.Name;

                                if (!string.IsNullOrWhiteSpace(modInfo.Summary))
                                    Mod.Summary = modInfo.Summary;

                                if (!string.IsNullOrWhiteSpace(modInfo.Description))
                                    Mod.Description = modInfo.Description;
                                else if (!string.IsNullOrWhiteSpace(modInfo.Summary))
                                    Mod.Description = modInfo.Summary;

                                var authorName = modInfo.Authors?.FirstOrDefault()?.Name;
                                if (!string.IsNullOrWhiteSpace(authorName))
                                    Mod.Author = authorName;

                                if (!string.IsNullOrWhiteSpace(modInfo.Logo?.ThumbnailUrl))
                                {
                                    Mod.IconUrl = modInfo.Logo.ThumbnailUrl;
                                    _ = Mod.LoadIconAsync();
                                }

                                if (modInfo.DownloadCount > 0)
                                    Mod.DownloadCount = modInfo.DownloadCount;

                                if (!string.IsNullOrWhiteSpace(modInfo.DateModified))
                                    Mod.LastUpdateTime = modInfo.DateModified;

                                if (!string.IsNullOrWhiteSpace(modInfo.Links?.WebsiteUrl))
                                    Mod.Url = modInfo.Links.WebsiteUrl;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug($"[ModDetailsViewModel] 回填 Curseforge 元数据失败: {ex.Message}");
                    }
                }

                if (Mod.Source == "NexusMods")
                {
                    try
                    {
                        var nexusModId = await EnsureNexusModIdAsync();
                        if (nexusModId > 0)
                        {
                            var detail = await EnsureNexusDetailsAsync(nexusModId);
                            if (detail != null)
                            {
                                if (detail.Downloads > 0)
                                    Mod.DownloadCount = detail.Downloads;

                                if (detail.UpdatedAt != default)
                                    Mod.LastUpdateTime = detail.UpdatedAt.ToString("yyyy-MM-dd");

                                if (!string.IsNullOrWhiteSpace(detail.Summary))
                                    Mod.Summary = detail.Summary;

                                if (!string.IsNullOrWhiteSpace(detail.Description))
                                    Mod.Description = detail.Description;
                                else if (!string.IsNullOrWhiteSpace(detail.Summary))
                                    Mod.Description = detail.Summary;

                                if (!string.IsNullOrWhiteSpace(detail.PictureUrl))
                                {
                                    Mod.IconUrl = detail.PictureUrl;
                                    _ = Mod.LoadIconAsync();
                                }

                                if (!string.IsNullOrWhiteSpace(detail.Name))
                                    Mod.Name = detail.Name;

                                if (!string.IsNullOrWhiteSpace(detail.Author))
                                    Mod.Author = detail.Author;
                            }
                        }
                    }
                    catch (SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsTokenExpiredException ex)
                    {
                        Log.Warn("[ModDetailsViewModel] 回填 Nexus 元数据失败：登录已过期", ex);
                        HandleNexusModsTokenExpired("LoadModAsync-BackfillMetadata", showNotification: true);
                    }
                    catch (Exception ex)
                    {
                        if (IsNexusUnauthorizedException(ex))
                        {
                            Log.Warn("[ModDetailsViewModel] 回填 Nexus 元数据失败：登录已过期", ex);
                            HandleNexusModsTokenExpired("LoadModAsync-BackfillMetadata", showNotification: true);
                            return;
                        }

                        Log.Debug($"[ModDetailsViewModel] 回填 Nexus 元数据失败: {ex.Message}");
                    }
                }

                // 如果是 Curseforge MOD，获取文件列表
                if (Mod.Source == "Curseforge" && Mod.Id.StartsWith("curse-"))
                {
                    try
                    {
                        // 从 mod.Id 中提取 modId（格式：curse-{modId}）
                        string modIdStr = Mod.Id.Substring(6); // 去掉 "curse-" 前缀
                        if (int.TryParse(modIdStr, out int curseforgeModId))
                        {
                            Log.Info($"[ModDetailsViewModel] 获取 MOD 文件列表: modId={curseforgeModId}");

                            // 获取文件列表
                            var files = await CurseforgeApiService.GetModFilesAsync(curseforgeModId);
                            if (files != null && files.Count > 0)
                            {
                                Log.Info($"[ModDetailsViewModel] 获取到 {files.Count} 个文件");

                                // 清空现有数据
                                SupportedGameVersions.Clear();
                                VersionsByGameVersion.Clear();

                                // 收集所有游戏版本
                                var allGameVersions = new HashSet<string>();

                                // 按游戏版本组织文件
                                var filesByVersion = new Dictionary<string, List<ModVersionItem>>();

                                foreach (var file in files)
                                {
                                    // 获取该文件支持的游戏版本
                                    var gameVersions = file.GameVersions ?? new List<string>();
                                    if (gameVersions.Count == 0)
                                    {
                                        gameVersions = new List<string> { "未知版本" };
                                    }

                                    foreach (var gameVersion in gameVersions)
                                    {
                                        allGameVersions.Add(gameVersion);

                                        if (!filesByVersion.ContainsKey(gameVersion))
                                        {
                                            filesByVersion[gameVersion] = new List<ModVersionItem>();
                                        }

                                        // 转换为 ModVersionItem
                                        // 如果是 SMAPI，清理版本名（去除重复前缀）
                                        var curseVersionText = file.DisplayName;
                                        if ((file.DisplayName?.IndexOf("SMAPI", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                                            (file.FileName?.IndexOf("SMAPI", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
                                        {
                                            curseVersionText = CurseforgeHelper.ParseSmapiDisplayName(file.DisplayName ?? "", file.FileName ?? "");
                                            Log.Info($"[ModDetailsViewModel] CurseForge MOD SMAPI 版本名清理：{file.DisplayName} → {curseVersionText}");
                                        }
                                        var modVersion = new ModVersionItem
                                        {
                                            FileId = $"curse-file-{file.Id}",
                                            Version = curseVersionText,
                                            FileName = file.FileName,
                                            GameVersion = gameVersion,
                                            FileSize = file.FileLength,
                                            UploadTime = file.FileDate.ToString("yyyy-MM-dd"),
                                            DownloadUrl = file.DownloadUrl,
                                            IsPrimary = !file.IsAlternate,
                                            ReleaseType = GetReleaseTypeString(file.ReleaseType),
                                            DownloadCount = file.DownloadCount > 0 ? file.DownloadCount : Mod.DownloadCount  // 优先使用文件下载量，如果没有则使用MOD总下载量
                                        };

                                        filesByVersion[gameVersion].Add(modVersion);
                                    }
                                }

                                // 按版本号排序（从新到旧），只展开第一个（最新）游戏版本
                                var isFirstVersion = true;
                                foreach (var gameVersion in allGameVersions)
                                {
                                    SupportedGameVersions.Add(gameVersion);

                                    if (filesByVersion.ContainsKey(gameVersion))
                                    {
                                        // 按上传时间排序（从新到旧）
                                        var sortedFiles = filesByVersion[gameVersion]
                                            .OrderByDescending(f => f.UploadTime)
                                            .ToList();

                                        VersionsByGameVersion.Add(new GameVersionFilesGroup
                                        {
                                            GameVersion = gameVersion,
                                            Files = new ObservableCollection<ModVersionItem>(sortedFiles),
                                            IsExpanded = isFirstVersion  // 只展开最新版本
                                        });
                                        isFirstVersion = false;
                                    }
                                }

                                // 更新显示的游戏版本列表（添加"全部"选项）
                                DisplayedGameVersions.Clear();
                                DisplayedGameVersions.Add("全部");
                                foreach (var gameVersion in allGameVersions.OrderByDescending(v => v, SemanticVersionComparer.Instance))
                                {
                                    DisplayedGameVersions.Add(gameVersion);
                                }

                                Log.Info($"[ModDetailsViewModel] 已加载 {SupportedGameVersions.Count} 个游戏版本，{VersionsByGameVersion.Count} 个版本文件组");
                                foreach (var group in VersionsByGameVersion)
                                {
                                    Log.Info($"[ModDetailsViewModel]   - {group.GameVersion}: {group.Files.Count} 个文件");
                                }
                                await LoadCurseforgeRequirements(files, curseforgeModInfo);
                                return; // 成功加载，直接返回
                            }
                            else
                            {
                                Log.Warn("[ModDetailsViewModel] 未获取到文件列表");
                            }
                        }
                    }
                    catch (System.Exception ex)
                    {
                        Log.Error(ex, "[ModDetailsViewModel] 获取文件列表失败");
                    }
                }

                // 如果是 NexusMods MOD，获取文件列表
                if (Mod.Source == "NexusMods")
                {
                    try
                    {
                        var nexusModId = await EnsureNexusModIdAsync();
                        if (nexusModId > 0)
                        {
                            if (Mod.DownloadCount <= 0)
                            {
                                try
                                {
                                    var detail = await EnsureNexusDetailsAsync(nexusModId);
                                    if (detail != null && detail.Downloads > 0)
                                    {
                                        Mod.DownloadCount = detail.Downloads;
                                        if (detail.UpdatedAt != default)
                                            Mod.LastUpdateTime = detail.UpdatedAt.ToString("yyyy-MM-dd");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    if (IsNexusUnauthorizedException(ex))
                                    {
                                        Log.Warn("[ModDetailsViewModel] 补拉 Nexus MOD 下载量失败：登录已过期", ex);
                                        HandleNexusModsTokenExpired("LoadModAsync-BackfillDownloadCount", showNotification: false);
                                    }

                                    Log.Debug($"[ModDetailsViewModel] 补拉 Nexus MOD 下载量失败: {ex.Message}");
                                }
                            }

                            Log.Info($"[ModDetailsViewModel] 获取 NexusMods MOD 文件列表: modId={nexusModId}");

                            // 获取文件列表
                            var files = await SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsService.GetModFilesAsync(nexusModId);
                            if (files != null && files.Count > 0)
                            {
                                Log.Info($"[ModDetailsViewModel] 获取到 {files.Count} 个 NexusMods 文件");

                                // 清空现有数据
                                SupportedGameVersions.Clear();
                                VersionsByGameVersion.Clear();

                                // NexusMods：
                                // - SMAPI（Nexus 2400）按游戏版本分组
                                // - 其它 Mod 不按游戏版本分组，统一归入“全部”并按时间倒序展示
                                var isNexusSmapi = string.Equals(Mod?.Source, "NexusMods", StringComparison.OrdinalIgnoreCase)
                                                  && (
                                                      (Mod?.Id?.IndexOf("2400", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                                                      || string.Equals(Mod?.Name, "SMAPI - Stardew Modding API", StringComparison.OrdinalIgnoreCase)
                                                  );
                                var allItems = new List<(string GameVersionKey, ModVersionItem Item)>();
                                var parsedGameVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                var itemsNeedDownloadCountEnrich = new List<(long FileId, ModVersionItem Item)>();

                                foreach (var file in files)
                                {
                                    // 跳过没有有效文件 ID 的文件
                                    var fileId = file.GetFileIdLong();
                                    if (fileId == 0)
                                    {
                                        Log.Warn($"[ModDetailsViewModel] 跳过无效文件 ID: {file.Name}");
                                        continue;
                                    }

                                    var parsedKey = ExtractStardewGameVersionKeyFromNexusFileDescription(file.Description);
                                    if (string.IsNullOrWhiteSpace(parsedKey))
                                        parsedKey = "未知";

                                    if (!string.Equals(parsedKey, "未知", StringComparison.OrdinalIgnoreCase) && !string.Equals(parsedKey, "全部", StringComparison.OrdinalIgnoreCase))
                                        parsedGameVersions.Add(parsedKey);

                                    var modVersion = new ModVersionItem
                                    {
                                        FileId = $"nexus-file-{fileId}",
                                        Version = file.Version ?? "未知",
                                        FileName = file.FileName ?? file.Name ?? "未知",
                                        GameVersion = parsedKey,
                                        FileSize = file.Size,
                                        UploadTime = !string.IsNullOrEmpty(file.UploadedTime)
                                            ? DateTime.Parse(file.UploadedTime).ToString("yyyy-MM-dd")
                                            : "未知",
                                        DownloadUrl = string.Empty,  // 需要后续获取下载链接
                                        IsPrimary = true,
                                        ReleaseType = "Release",  // NexusMods 没有明确的发布类型
                                        DownloadCount = file.GetEffectiveDownloadCount()
                                    };

                                    if (modVersion.DownloadCount <= 0)
                                        itemsNeedDownloadCountEnrich.Add((fileId, modVersion));

                                    allItems.Add((parsedKey, modVersion));
                                }

                                var enableVersionGrouping = isNexusSmapi;

                                var filesByVersion = new Dictionary<string, List<ModVersionItem>>(StringComparer.OrdinalIgnoreCase);
                                var allGameVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                                if (!enableVersionGrouping)
                                {
                                    allGameVersions.Add("全部");
                                    filesByVersion["全部"] = allItems
                                        .OrderByDescending(x =>
                                        {
                                            if (DateTime.TryParse(x.Item.UploadTime, out var dt))
                                                return dt;
                                            return DateTime.MinValue;
                                        })
                                        .Select(x =>
                                    {
                                        x.Item.GameVersion = "全部";
                                        return x.Item;
                                    })
                                    .ToList();
                                }
                                else
                                {
                                    foreach (var (key, item) in allItems)
                                    {
                                        var gameKey = string.IsNullOrWhiteSpace(key) ? "未知" : key;
                                        allGameVersions.Add(gameKey);
                                        if (!filesByVersion.TryGetValue(gameKey, out var list))
                                        {
                                            list = new List<ModVersionItem>();
                                            filesByVersion[gameKey] = list;
                                        }
                                        list.Add(item);
                                    }
                                }

                                if (itemsNeedDownloadCountEnrich.Count > 0)
                                {
                                    _ = EnrichNexusFileDownloadCountsAsync(nexusModId, itemsNeedDownloadCountEnrich);
                                }

                                // 按游戏版本排序（从新到旧），使用语义化版本比较器
                                var orderedGameVersions = allGameVersions
                                    .OrderByDescending(v => v, SemanticVersionComparer.Instance)
                                    .ToList();

                                // 只展开第一个（最新）游戏版本
                                var isFirstVersion = true;
                                foreach (var gameVersion in orderedGameVersions)
                                {
                                    SupportedGameVersions.Add(gameVersion);

                                    if (filesByVersion.ContainsKey(gameVersion))
                                    {
                                        // 按上传时间排序（从新到旧）
                                        var sortedFiles = filesByVersion[gameVersion]
                                            .OrderByDescending(f =>
                                            {
                                                if (DateTime.TryParse(f.UploadTime, out var dt))
                                                    return dt;
                                                return DateTime.MinValue;
                                            })
                                            .ToList();

                                        VersionsByGameVersion.Add(new GameVersionFilesGroup
                                        {
                                            GameVersion = gameVersion,
                                            Files = new ObservableCollection<ModVersionItem>(sortedFiles),
                                            IsExpanded = isFirstVersion  // 只展开最新版本
                                        });
                                        isFirstVersion = false;
                                    }
                                }

                                // 更新显示的游戏版本列表（始终包含"全部"）
                                DisplayedGameVersions.Clear();
                                DisplayedGameVersions.Add("全部");
                                foreach (var gameVersion in orderedGameVersions
                                             .Where(v => !string.Equals(v, "全部", StringComparison.OrdinalIgnoreCase))
                                             .Where(v => !string.Equals(v, "未知", StringComparison.OrdinalIgnoreCase))
                                             .OrderByDescending(v => v, SemanticVersionComparer.Instance))
                                {
                                    DisplayedGameVersions.Add(gameVersion);
                                }

                                // 未知置底
                                if (orderedGameVersions.Any(v => string.Equals(v, "未知", StringComparison.OrdinalIgnoreCase)))
                                    DisplayedGameVersions.Add("未知");

                                // 确保默认筛选为“全部”
                                if (DisplayedGameVersions.Contains("全部"))
                                    SelectedGameVersion = "全部";

                                Log.Info($"[ModDetailsViewModel] 已加载 {SupportedGameVersions.Count} 个游戏版本，{VersionsByGameVersion.Count} 个版本文件组（NexusMods）");
                                foreach (var group in VersionsByGameVersion)
                                {
                                    Log.Info($"[ModDetailsViewModel]   - {group.GameVersion}: {group.Files.Count} 个文件");
                                }
                                await LoadNexusRequirementsAsync(nexusModId, VersionsByGameVersion);
                                return; // 成功加载，直接返回
                            }
                            else
                            {
                                Log.Warn("[ModDetailsViewModel] 未获取到 NexusMods 文件列表");
                            }
                        }
                        else
                        {
                            Log.Warn("[ModDetailsViewModel] 解析 NexusMods modId 失败（modId=0）");
                        }
                    }
                    catch (SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsTokenExpiredException ex)
                    {
                        Log.Warn("[ModDetailsViewModel] 获取 NexusMods 文件列表失败：登录已过期", ex);
                        HandleNexusModsTokenExpired("LoadModAsync-GetModFiles", showNotification: true);
                    }
                    catch (System.Exception ex)
                    {
                        if (IsNexusUnauthorizedException(ex))
                        {
                            Log.Warn("[ModDetailsViewModel] 获取 NexusMods 文件列表失败：登录已过期", ex);
                            HandleNexusModsTokenExpired("LoadModAsync-GetModFiles", showNotification: true);
                            return;
                        }

                        Log.Error(ex, "[ModDetailsViewModel] 获取 NexusMods 文件列表失败");
                    }
                }

                // 如果是 GitHub（SMAPI）来源，加载 GitHub Release 版本列表
                if (Mod.Source == "GitHub" || Mod.Id == "github-smapi")
                {
                    try
                    {
                        Log.Info("[ModDetailsViewModel] 获取 GitHub SMAPI 版本列表");

                        // GitHub：下载量字段改为 Star 数
                        try
                        {
                            var stars = await SVL.Core.Stardew.Mod.SMAPI.SmapApiService.GetSmapiRepoStarCountAsync();
                            if (stars.HasValue)
                            {
                                Mod.DownloadCount = stars.Value;
                            }
                        }
                        catch
                        {
                            // 忽略 star 获取失败
                        }

                        SupportedGameVersions.Clear();
                        VersionsByGameVersion.Clear();
                        DisplayedGameVersions.Clear();
                        _githubAllVersionItems.Clear();

                        GithubCompatiblePage = 1;
                        UpdateGithubCompatiblePagination();

                        // 分页加载：避免一次拉取过多
                        const int perPage = 100;
                        const int maxPages = 20; // 安全阈值
                        var allVersions = new List<SVL.Core.Stardew.Mod.SMAPI.SmapiVersionInfo>();

                        for (int page = 1; page <= maxPages; page++)
                        {
                            var pageVersions = await SVL.Core.Stardew.Mod.SMAPI.SmapApiService.GetAllVersionsAsync(page, perPage);
                            if (pageVersions == null || pageVersions.Count == 0)
                                break;

                            allVersions.AddRange(pageVersions);

                            if (pageVersions.Count < perPage)
                                break;
                        }

                        if (allVersions.Count == 0)
                        {
                            Log.Warn("[ModDetailsViewModel] 未获取到 GitHub SMAPI 版本列表");
                            return;
                        }

                        // 按游戏版本分组（显示为 x.y.z+），使用语义化版本排序，只展开第一个（最新）游戏版本
                        var groups = allVersions
                            .GroupBy(v => NormalizeGitHubGameVersionKey(v.GameVersion))
                            .OrderByDescending(g => g.Key, SemanticVersionComparer.Instance)
                            .ToList();

                        // 默认包含“全部”选项
                        DisplayedGameVersions.Add("全部");

                        var isFirstVersion = true;
                        foreach (var group in groups)
                        {
                            SupportedGameVersions.Add(group.Key);
                            DisplayedGameVersions.Add(group.Key);

                            var versionFileItems = group
                                .OrderByDescending(v => v.PublishedDate)
                                .Select(v => new ModVersionItem
                                {
                                    FileId = $"github-{v.Version}",
                                    Version = v.Version,
                                    FileName = !string.IsNullOrWhiteSpace(v.DownloadUrl)
                                        ? System.IO.Path.GetFileName(v.DownloadUrl)
                                        : $"SMAPI-{v.Version}",
                                    GameVersion = group.Key,
                                    FileSize = 0,
                                    UploadTime = v.PublishedDate != default
                                        ? v.PublishedDate.ToString("yyyy-MM-dd")
                                        : "未知",
                                    DownloadUrl = v.DownloadUrl ?? string.Empty,
                                    IsPrimary = true,
                                    ReleaseType = "Release",
                                    DownloadCount = 0
                                })
                                .ToList();

                            // 记录所有条目，用于“选择高版本时也包含低版本+”的兼容筛选
                            _githubAllVersionItems.AddRange(versionFileItems);

                            VersionsByGameVersion.Add(new GameVersionFilesGroup
                            {
                                GameVersion = group.Key,
                                Files = new ObservableCollection<ModVersionItem>(versionFileItems),
                                IsExpanded = isFirstVersion  // 只展开最新版本
                            });
                            isFirstVersion = false;
                        }

                        Log.Info($"[ModDetailsViewModel] 已加载 GitHub SMAPI 版本: {allVersions.Count} 条，分组 {VersionsByGameVersion.Count} 个");

                        // 修正"全部"下版本选择器排序：按语义化版本从新到旧
                        var ordered = DisplayedGameVersions
                            .Where(v => !string.Equals(v, "全部", StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(v => v, SemanticVersionComparer.Instance)
                            .ToList();
                        DisplayedGameVersions.Clear();
                        DisplayedGameVersions.Add("全部");
                        foreach (var v in ordered)
                            DisplayedGameVersions.Add(v);

                        // 确保默认筛选为“全部”
                        SelectedGameVersion = "全部";

                        UpdateGithubCompatiblePagination();
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[ModDetailsViewModel] 获取 GitHub SMAPI 版本列表失败");
                        return;
                    }
                }

                // 如果获取文件列表失败，清空版本列表
                SupportedGameVersions.Clear();
                DisplayedGameVersions.Clear();
                VersionsByGameVersion.Clear();

                Log.Warn($"[ModDetailsViewModel] 无法加载 {Mod.Source} MOD 的文件列表，将显示为空");
            }
            else
            {
                Log.Warn("[ModDetailsViewModel] 未找到选中的 MOD");
            }
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "[ModDetailsViewModel] 加载 MOD 详情失败");
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task LoadModpackAsync(ModSearchItem modpack)
    {
        if (modpack == null)
            return;

        IsLoading = true;

        try
        {
            IsModpackMode = true;
            Mod = modpack;
            _communityLocalizationEntry = null;
            ClearCommunityRelationCollections();
            await LocalizationDisplayHelper.ApplyLocalizationAsync(Mod);

            if (string.IsNullOrWhiteSpace(Mod.Source))
                Mod.Source = "Modpack";

            if (string.IsNullOrWhiteSpace(Mod.Description))
                Mod.Description = Mod.Summary;

            _ = Mod.LoadIconAsync();

            SupportedGameVersions.Clear();
            VersionsByGameVersion.Clear();
            DisplayedGameVersions.Clear();

            DisplayedGameVersions.Add("全部");
            DisplayedGameVersions.Add("整合包");
            SupportedGameVersions.Add("整合包");
            SelectedGameVersion = "全部";

            var versionItems = new List<ModVersionItem>();
            GameVersionFilesGroup? group = null;

            // 检查是否是 NexusMods Collection
            if (Mod.Id.StartsWith("nexuscol-") || Mod.Source == "NexusMods")
            {
                group = await CreateNexusCollectionGroupAsync();
                if (group == null)
                {
                    AddDefaultModpackVersion(versionItems);
                }
            }
            else if (Mod.Source == "Curseforge" || TryParseCurseforgeModpackId(out _))
            {
                // Curseforge 整合包：获取文件列表
                await LoadCurseforgeModpackFilesAsync(versionItems);
            }
            else
            {
                // 其它来源：使用默认版本
                AddDefaultModpackVersion(versionItems);
            }

            if (group == null)
            {
                group = new GameVersionFilesGroup
                {
                    GameVersion = "整合包",
                    Files = new ObservableCollection<ModVersionItem>(versionItems),
                    IsExpanded = true
                };
            }

            VersionsByGameVersion.Add(group);

            OnPropertyChanged(nameof(DisplayedVersionsByGameVersion));
        }
        finally
        {
            IsLoading = false;
        }

        await Task.CompletedTask;
    }

    private Task<GameVersionFilesGroup?> CreateNexusCollectionGroupAsync()
    {
        var collectionSlug = ExtractCollectionSlug(Mod.Url);

        if (string.IsNullOrWhiteSpace(collectionSlug))
        {
            Log.Warn($"[ModDetailsViewModel] 无法从 URL 提取 Collection Slug: {Mod.Url}");
            return Task.FromResult<GameVersionFilesGroup?>(null);
        }

        Log.Info($"[ModDetailsViewModel] 渐进式加载 Nexus Collection Revisions: slug={collectionSlug}");

        var group = new GameVersionFilesGroup
        {
            GameVersion = "整合包",
            Files = new ObservableCollection<ModVersionItem>
            {
                new ModVersionItem
                {
                    FileId = "loading",
                    Version = "加载中",
                    FileName = "加载中，请稍等...",
                    GameVersion = "整合包",
                    UploadTime = "请稍候",
                    ReleaseType = "Release",
                    IsLoadingPlaceholder = true
                }
            },
            IsExpanded = true
        };

        _ = LoadNexusCollectionRevisionsProgressivelyAsync(collectionSlug, group);
        return Task.FromResult<GameVersionFilesGroup?>(group);
    }

    private async Task LoadNexusCollectionRevisionsProgressivelyAsync(string collectionSlug, GameVersionFilesGroup group)
    {
        try
        {
            // 使用客户端分页（已通过缓存加速，首次加载后后续访问很快）
            // NexusMods API 的 revisions 字段不支持服务端分页
            var revisions = await SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsService.GetAllCollectionRevisionsAsync(collectionSlug, useCache: true);

            if (revisions == null || revisions.Count == 0)
            {
                Log.Warn($"[ModDetailsViewModel] Nexus Collection {collectionSlug} 没有 Revisions");
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (group.Files.Count == 0)
                    {
                        group.Files.Add(new ModVersionItem
                        {
                            FileId = Mod.Id,
                            Version = "在线资源",
                            FileName = string.IsNullOrWhiteSpace(Mod.Name) ? "modpack.collection" : $"{Mod.Name}.collection",
                            GameVersion = "整合包",
                            FileSize = 0,
                            UploadTime = string.IsNullOrWhiteSpace(Mod.LastUpdateTime) ? "未知" : Mod.LastUpdateTime,
                            DownloadUrl = Mod.Url,
                            IsPrimary = true,
                            ReleaseType = "Release",
                            DownloadCount = Mod.DownloadCount
                        });
                    }
                });
                return;
            }

            // 按版本号降序排序
            var sortedRevisions = revisions.OrderByDescending(r => r.RevisionNumber).ToList();

            // 转换为 ModVersionItem 并全部添加到 Files 集合
            // GameVersionFilesGroup 会自动处理客户端分页（每页 5 条）
            var allItems = sortedRevisions
                .Select(rev => CreateNexusCollectionVersionItem(collectionSlug, rev))
                .ToList();

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                group.Files.Clear();
                foreach (var item in allItems)
                {
                    group.Files.Add(item);
                }
            });

            Log.Info($"[ModDetailsViewModel] Nexus Collection Revisions 加载完成: {collectionSlug}, count={allItems.Count}");
        }
        catch (SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsTokenExpiredException)
        {
            Log.Warn("[ModDetailsViewModel] NexusMods Token 已过期");
        }
        catch (Exception ex)
        {
            Log.Warn($"[ModDetailsViewModel] 加载 Nexus Collection Revisions 失败: {ex.Message}");
        }
    }

    private ModVersionItem CreateNexusCollectionVersionItem(string collectionSlug, SVL.Core.Stardew.ResourceProject.NexusMods.NexusCollectionRevision rev)
    {
        var nxmUrl = $"nxm://stardewvalley/collections/{collectionSlug}/revisions/{rev.RevisionNumber}";

        return new ModVersionItem
        {
            FileId = $"nexuscol-{collectionSlug}-rev{rev.RevisionNumber}",
            Version = rev.Name ?? $"Revision {rev.RevisionNumber}",
            FileName = $"{Mod.Name}_r{rev.RevisionNumber}.collection",
            GameVersion = "整合包",
            FileSize = rev.FileSize,
            UploadTime = rev.UpdatedAt.ToString("yyyy-MM-dd HH:mm"),
            DownloadUrl = nxmUrl,
            IsPrimary = rev.IsLatest,
            ReleaseType = rev.IsLatest ? "Release" : "Old",
            DownloadCount = rev.TotalDownloads
        };
    }

    /// <summary>
    /// 加载 Curseforge 整合包的文件列表
    /// </summary>
    private async Task LoadCurseforgeModpackFilesAsync(List<ModVersionItem> versionItems)
    {
        if (!TryParseCurseforgeModpackId(out var modId))
        {
            Log.Warn($"[ModDetailsViewModel] 无法解析 Curseforge Modpack ID: {Mod.Id}");
            AddDefaultModpackVersion(versionItems);
            return;
        }

        Log.Info($"[ModDetailsViewModel] 获取 Curseforge Modpack 文件列表: modId={modId}");

        try
        {
            var files = await CurseforgeApiService.GetModFilesAsync(modId, index: 0, pageSize: 50);

            if (files != null && files.Count > 0)
            {
                Log.Info($"[ModDetailsViewModel] 获取到 {files.Count} 个原始文件，开始处理");

                var sortedFiles = files.OrderByDescending(f => f.FileDate).ToList();

                for (int i = 0; i < sortedFiles.Count; i++)
                {
                    var file = sortedFiles[i];

                    Log.Debug($"[ModDetailsViewModel] 处理文件 [{i+1}/{sortedFiles.Count}]: fileId={file.Id}, fileName={file.FileName}, displayName={file.DisplayName}, hasDownloadUrl={!string.IsNullOrEmpty(file.DownloadUrl)}");

                    // 延迟获取下载地址：仅在安装/另存为时调用 API
                    // 如果有内置 downloadUrl 则使用，否则留空（后续通过 ResolveCurseforgeModpackDownloadUrlAsync 获取）
                    var downloadUrl = file.DownloadUrl ?? string.Empty;

                    // 如果是 CurseForge 的 SMAPI，清理版本名（去除重复前缀）
                    var versionText = file.DisplayName ?? file.FileName ?? $"v{file.Id}";
                    var containsSmapiInDisplay = file.DisplayName?.IndexOf("SMAPI", StringComparison.OrdinalIgnoreCase) ?? -1;
                    var containsSmapiInFile = file.FileName?.IndexOf("SMAPI", StringComparison.OrdinalIgnoreCase) ?? -1;
                    
                    Log.Info($"[ModDetailsViewModel] 检查文件：{file.FileName}, DisplayName={file.DisplayName}, Mod.Source={Mod.Source}, containsSmapiInDisplay={containsSmapiInDisplay}, containsSmapiInFile={containsSmapiInFile}");
                    
                    if (Mod.Source == "Curseforge" && (containsSmapiInDisplay >= 0 || containsSmapiInFile >= 0))
                    {
                        Log.Info($"[ModDetailsViewModel] 触发 SMAPI 版本名清理，原始：{versionText}");
                        versionText = CurseforgeHelper.ParseSmapiDisplayName(file.DisplayName ?? "", file.FileName ?? "");
                        Log.Info($"[ModDetailsViewModel] 清理后的 SMAPI 版本名：{versionText}");
                    }

                    versionItems.Add(new ModVersionItem
                    {
                        FileId = file.Id.ToString(),
                        Version = versionText,
                        FileName = file.FileName ?? $"{Mod.Name}_{file.Id}.zip",
                        GameVersion = "整合包",
                        FileSize = file.FileLength,
                        UploadTime = file.FileDate.ToString("yyyy-MM-dd HH:mm"),
                        DownloadUrl = downloadUrl,
                        IsPrimary = i == 0, // 第一个（最新的）作为主版本
                        ReleaseType = file.ReleaseType switch
                        {
                            1 => "Release",
                            2 => "Beta",
                            3 => "Alpha",
                            _ => "Release"
                        },
                        DownloadCount = file.DownloadCount
                    });
                }

                Log.Info($"[ModDetailsViewModel] 成功处理 {versionItems.Count} 个 Curseforge Modpack 文件");
            }
            else
            {
                Log.Warn($"[ModDetailsViewModel] Curseforge Modpack {modId} 没有文件");
                AddDefaultModpackVersion(versionItems);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[ModDetailsViewModel] 获取 Curseforge Modpack 文件失败: {ex.Message}", ex);
            AddDefaultModpackVersion(versionItems);
        }
    }

    private bool TryParseCurseforgeModpackId(out int modId)
    {
        modId = 0;

        if (int.TryParse(Mod.Id, out var directId) && directId > 0)
        {
            modId = directId;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(Mod.Id))
        {
            var idMatch = Regex.Match(Mod.Id, @"(\d+)$");
            if (idMatch.Success && int.TryParse(idMatch.Groups[1].Value, out var extractedId) && extractedId > 0)
            {
                modId = extractedId;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 添加默认的整合包版本（用于 Curseforge 或 Nexus 获取失败时）
    /// </summary>
    private void AddDefaultModpackVersion(List<ModVersionItem> versionItems)
    {
        versionItems.Add(new ModVersionItem
        {
            FileId = Mod.Id,
            Version = "在线资源",
            FileName = string.IsNullOrWhiteSpace(Mod.Name) ? "modpack.cfmodpack" : $"{Mod.Name}.cfmodpack",
            GameVersion = "整合包",
            FileSize = 0,
            UploadTime = string.IsNullOrWhiteSpace(Mod.LastUpdateTime) ? "未知" : Mod.LastUpdateTime,
            DownloadUrl = Mod.Url,
            IsPrimary = true,
            ReleaseType = "Release",
            DownloadCount = Mod.DownloadCount
        });
    }

    /// <summary>
    /// 从 URL 中提取 Collection Slug
    /// </summary>
    private string ExtractCollectionSlug(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        // URL 格式: https://next.nexusmods.com/stardewvalley/collections/{slug}
        // 或: https://www.nexusmods.com/stardewvalley/collections/{slug}
        try
        {
            var uri = new Uri(url);
            var segments = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            // 查找 "collections" 后面的 segment
            for (int i = 0; i < segments.Length - 1; i++)
            {
                if (segments[i].Equals("collections", StringComparison.OrdinalIgnoreCase))
                {
                    return segments[i + 1];
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[ModDetailsViewModel] 解析 URL 失败: {url}, {ex.Message}");
        }

        return string.Empty;
    }

    /// <summary>
    /// 当选中的游戏版本变化时
    /// </summary>
    partial void OnSelectedGameVersionChanged(string value)
    {
        Log.Info($"[ModDetailsViewModel] 选择游戏版本: {value}");

        if (IsGithubSource)
        {
            // 选择版本切换时重置分页（避免用户看到空页）
            GithubCompatiblePage = 1;
            UpdateGithubCompatiblePagination();
        }

        // 通知 DisplayedVersionsByGameVersion 属性已更改
        OnPropertyChanged(nameof(DisplayedVersionsByGameVersion));
        OnPropertyChanged(nameof(ShowGithubCompatiblePaging));
        OnPropertyChanged(nameof(GithubCompatiblePageText));
    }

    /// <summary>
    /// 获取应该显示的文件列表（根据选择的游戏版本筛选）
    /// </summary>
    public ObservableCollection<GameVersionFilesGroup> DisplayedVersionsByGameVersion
    {
        get
        {
            if (SelectedGameVersion == "全部")
            {
                // 显示所有版本
                return VersionsByGameVersion;
            }
            else
            {
                // GitHub：选择 x.y.z+ 时，应该同时包含更低要求的 x.y.*+ 版本（并向后排序），再进行分页渲染
                if (IsGithubSource)
                {
                    var selectedMin = TryParseGameVersionForSort(SelectedGameVersion);

                    var compatible = _githubAllVersionItems
                        .Where(v => TryParseGameVersionForSort(v.GameVersion) <= selectedMin)
                        .OrderByDescending(v => TryParseGameVersionForSort(v.GameVersion))
                        .ThenByDescending(v => TryParseSmapiVersionForSort(v.Version))
                        .ThenByDescending(v => v.UploadTime)
                        .ToList();

                    var pageItems = compatible
                        .Skip((GithubCompatiblePage - 1) * GithubCompatiblePageSize)
                        .Take(GithubCompatiblePageSize)
                        .ToList();

                    var groupTitle = $"{SelectedGameVersion}（含更低要求）";
                    return new ObservableCollection<GameVersionFilesGroup>
                    {
                        new GameVersionFilesGroup
                        {
                            GameVersion = groupTitle,
                            Files = new ObservableCollection<ModVersionItem>(pageItems),
                            IsExpanded = true
                        }
                    };
                }

                // 其它来源：只显示选中的版本
                var selected = VersionsByGameVersion.FirstOrDefault(v => v.GameVersion == SelectedGameVersion);
                return selected != null
                    ? new ObservableCollection<GameVersionFilesGroup> { selected }
                    : new ObservableCollection<GameVersionFilesGroup>();
            }
        }
    }

    /// <summary>
    /// 选择游戏版本
    /// </summary>
    [RelayCommand]
    private void SelectGameVersion(string gameVersion)
    {
        SelectedGameVersion = gameVersion;
        Log.Info($"[ModDetailsViewModel] 选择游戏版本: {gameVersion}");
    }

    /// <summary>
    /// 切换游戏版本组的展开/折叠状态
    /// </summary>
    [RelayCommand]
    private void ToggleGameVersionExpanded(GameVersionFilesGroup group)
    {
        if (group != null)
        {
            group.IsExpanded = !group.IsExpanded;
            Log.Info($"[ModDetailsViewModel] 切换游戏版本 {group.GameVersion} 展开状态: {group.IsExpanded}");
        }
    }

    private async Task EnrichNexusFileDownloadCountsAsync(long modId, List<(long FileId, ModVersionItem Item)> targets)
    {
        if (targets == null || targets.Count == 0)
            return;

        foreach (var (fileId, item) in targets)
        {
            try
            {
                var metadata = await SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsService.GetModFileMetadataAsync(modId, fileId);
                if (metadata.DownloadCount <= 0)
                    continue;

                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    item.DownloadCount = metadata.DownloadCount;
                });
            }
            catch (Exception ex)
            {
                if (IsNexusUnauthorizedException(ex))
                {
                    Log.Warn("[ModDetailsViewModel] 补充 Nexus 文件下载量失败：登录已过期", ex);
                    HandleNexusModsTokenExpired("EnrichNexusFileDownloadCountsAsync", showNotification: false);
                    break;
                }

                Log.Debug($"[ModDetailsViewModel] 补充 Nexus 文件下载量失败: modId={modId}, fileId={fileId}, error={ex.Message}");
            }
        }
    }

    [RelayCommand]
    private async Task OpenRequiredModAsync(ModDependencyLink dependency)
    {
        if (dependency == null)
            return;

        var searchItem = await ResolveDependencySearchItemAsync(dependency);
        if (searchItem == null)
        {
            SvlMessageBox.Warning($"未找到 {dependency.DisplayName} 的详情页。", "前置 Mod 未找到");
            return;
        }

        if (_mainViewModel != null)
        {
            await _mainViewModel.OpenModDetailsAsync(searchItem, _mainViewModel.ModDetailsBackPage, pushCurrentMod: true);
        }
    }

    private async Task LoadNexusRequirementsAsync(long modId, IEnumerable<GameVersionFilesGroup> groups)
    {
        try
        {
            var fileIds = groups
                .SelectMany(group => group.Files)
                .Select(file => file.FileId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(TryExtractNumericId)
                .Where(id => id > 0)
                .Distinct()
                .Take(24)
                .ToList();

            var aggregatedRequirements = new List<ModDependencyLink>();
            if (fileIds.Count > 0)
            {
                var metadataTasks = fileIds.Select(fileId => NexusModsService.GetModFileMetadataAsync(modId, fileId));
                var metadataList = await Task.WhenAll(metadataTasks);
                aggregatedRequirements.AddRange(metadataList
                    .Where(metadata => metadata != null)
                    .SelectMany(metadata => metadata.Requirements)
                    .Select(requirement => new ModDependencyLink
                    {
                        DisplayName = string.IsNullOrWhiteSpace(requirement.Name) ? $"Nexus Mod {requirement.ModId}" : requirement.Name,
                        ProjectId = requirement.ModId > 0 ? requirement.ModId.ToString() : string.Empty,
                        Source = "NexusMods",
                        Url = requirement.Url ?? string.Empty,
                        MinimumVersion = requirement.Version ?? string.Empty,
                        IsRequired = requirement.IsRequired,
                        Note = "文件前置"
                    }));
            }

            var descriptionHints = (await DetectNexusRequirementsFromDescriptionAsync(modId)).ToList();
            if (descriptionHints.Count > 0)
            {
                foreach (var requirement in aggregatedRequirements)
                {
                    var optionalHint = descriptionHints.FirstOrDefault(hint =>
                        !hint.IsRequired &&
                        (( !string.IsNullOrWhiteSpace(hint.ProjectId) && string.Equals(hint.ProjectId, requirement.ProjectId, StringComparison.OrdinalIgnoreCase)) ||
                         ( !string.IsNullOrWhiteSpace(hint.Url) && string.Equals(hint.Url, requirement.Url, StringComparison.OrdinalIgnoreCase))));

                    if (optionalHint != null)
                    {
                        requirement.IsRequired = false;
                        if (string.IsNullOrWhiteSpace(requirement.Note) || requirement.Note.IndexOf("可选", StringComparison.OrdinalIgnoreCase) < 0)
                            requirement.Note = "描述推断为可选前置";
                    }
                }
            }

            if (aggregatedRequirements.Count == 0)
            {
                aggregatedRequirements.AddRange(descriptionHints);
            }

            SetRequirements(aggregatedRequirements);
            await EnrichRequirementMetadataAsync(aggregatedRequirements);
            CacheRequirements(aggregatedRequirements);
            SetRequirements(aggregatedRequirements);
        }
        catch (Exception ex)
        {
            Log.Debug($"[ModDetailsViewModel] 加载 Nexus 前置失败: {ex.Message}");
            CacheRequirements(Array.Empty<ModDependencyLink>());
            SetRequirements(Array.Empty<ModDependencyLink>());
        }
    }

    private async Task LoadCurseforgeRequirements(IEnumerable<CurseforgeFile> files, CurseforgeApiService.CurseforgeModInfo? modInfo = null)
    {
        try
        {
            var requirements = new List<ModDependencyLink>();
            var fileList = files?.ToList() ?? new List<CurseforgeFile>();
            var relationEntries = modInfo?.Relations?.ToList() ?? new List<CurseforgeApiService.CurseforgeModRelation>();
            var dependencyEntries = fileList
                .Where(file => file.Dependencies != null)
                .SelectMany(file => file.Dependencies.Select(dependency => new { FileId = file.Id, Dependency = dependency }))
                .ToList();

            Log.Info($"[ModDetailsViewModel] Curseforge 前置扫描: relations={relationEntries.Count}, files={fileList.Count}, dependencyEntries={dependencyEntries.Count}");

            foreach (var relation in relationEntries)
            {
                if (TryParseCurseforgeRelation(relation, out var modId, out var isRequired, out var relationType) && modId > 0)
                {
                    requirements.Add(new ModDependencyLink
                    {
                        DisplayName = $"Curseforge Mod {modId}",
                        ProjectId = modId.ToString(),
                        Source = "Curseforge",
                        Url = $"https://www.curseforge.com/stardewvalley/mods/{modId}",
                        IsRequired = isRequired,
                        Note = $"详情前置 ({relationType})"
                    });

                    Log.Debug($"[ModDetailsViewModel] Curseforge 详情 relations 前置: modId={modId}, relation={relationType}, required={isRequired}");
                }
            }

            foreach (var entry in dependencyEntries)
            {
                if (TryParseCurseforgeDependencyId(entry.Dependency, out var modId, out var isRequired, out var relationType) && modId > 0)
                {
                    requirements.Add(new ModDependencyLink
                    {
                        DisplayName = $"Curseforge Mod {modId}",
                        ProjectId = modId.ToString(),
                        Source = "Curseforge",
                        Url = $"https://www.curseforge.com/stardewvalley/mods/{modId}",
                        IsRequired = isRequired,
                        Note = $"文件前置 ({relationType})"
                    });

                    Log.Debug($"[ModDetailsViewModel] Curseforge 解析到前置: fileId={entry.FileId}, modId={modId}, relation={relationType}, required={isRequired}");
                }
            }

            Log.Info($"[ModDetailsViewModel] Curseforge 前置解析结果: {requirements.Count} 个");

            if (requirements.Count == 0)
            {
                requirements.AddRange(DetectCurseforgeRequirementsFromText(DisplayDescription));
            }

            SetRequirements(requirements);
            await EnrichRequirementMetadataAsync(requirements);
            CacheRequirements(requirements);
            SetRequirements(requirements);
        }
        catch (Exception ex)
        {
            Log.Debug($"[ModDetailsViewModel] 加载 Curseforge 前置失败: {ex.Message}");
            CacheRequirements(Array.Empty<ModDependencyLink>());
            SetRequirements(Array.Empty<ModDependencyLink>());
        }
    }

    private void SetRequirements(IEnumerable<ModDependencyLink> requirements)
    {
        var mergedRequirements = (requirements ?? Enumerable.Empty<ModDependencyLink>())
            .Where(item => item != null)
            .Select(CloneDependencyLink)
            .ToList();

        mergedRequirements.AddRange(BuildCommunityDependencyLinks());

        if (mergedRequirements.Any(item => !item.IsPlaceholder))
        {
            mergedRequirements = mergedRequirements
                .Where(item => !item.IsPlaceholder)
                .ToList();
        }

        RequiredMods.Clear();
        foreach (var requirement in mergedRequirements
                     .Where(item => item != null && !string.IsNullOrWhiteSpace(item.DisplayName))
                     .GroupBy(item => $"{item.Source}|{item.ProjectId}|{item.DisplayName}", StringComparer.OrdinalIgnoreCase)
                     .Select(MergeDependencyGroup))
        {
            RequiredMods.Add(requirement);
        }

        IsRequiredModsExpanded = RequiredMods.Count > 0 && s_requiredModsExpandedPreference;
        OnPropertyChanged(nameof(HasRequiredMods));
        OnPropertyChanged(nameof(RequiredModsCount));
    }

    private void RestoreOrInitializeRequirements()
    {
        var cacheKey = GetRequirementsCacheKey();
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            SetRequirements(Array.Empty<ModDependencyLink>());
            return;
        }

        if (s_requiredModsCache.TryGetValue(cacheKey, out var cachedRequirements))
        {
            SetRequirements(cachedRequirements.Select(CloneDependencyLink).ToList());
            return;
        }

        if (string.Equals(Mod?.Source, "NexusMods", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Mod?.Source, "Curseforge", StringComparison.OrdinalIgnoreCase))
        {
            if (HasCommunityDependencyData)
            {
                SetRequirements(Array.Empty<ModDependencyLink>());
                return;
            }

            SetRequirements(new[]
            {
                new ModDependencyLink
                {
                    DisplayName = "正在解析前置模组...",
                    Note = "请稍候",
                    Source = "placeholder",
                    IsPlaceholder = true
                }
            });
            return;
        }

        SetRequirements(Array.Empty<ModDependencyLink>());
    }

    private void CacheRequirements(IEnumerable<ModDependencyLink> requirements)
    {
        var cacheKey = GetRequirementsCacheKey();
        if (string.IsNullOrWhiteSpace(cacheKey))
            return;

        s_requiredModsCache[cacheKey] = (requirements ?? Enumerable.Empty<ModDependencyLink>())
            .Where(item => item != null && !item.IsPlaceholder)
            .Select(CloneDependencyLink)
            .ToList();
    }

    private string GetRequirementsCacheKey()
    {
        if (!string.IsNullOrWhiteSpace(Mod?.Id))
            return Mod.Id;

        if (!string.IsNullOrWhiteSpace(Mod?.Url))
            return Mod.Url;

        return string.Empty;
    }

    private static ModDependencyLink CloneDependencyLink(ModDependencyLink item)
    {
        return new ModDependencyLink
        {
            UniqueId = item.UniqueId,
            DisplayName = item.DisplayName,
            MinimumVersion = item.MinimumVersion,
            IsRequired = item.IsRequired,
            IsInstalled = item.IsInstalled,
            IsInstalledAndEnabled = item.IsInstalledAndEnabled,
            IsInstalledButDisabled = item.IsInstalledButDisabled,
            IsPlaceholder = item.IsPlaceholder,
            InstalledModId = item.InstalledModId,
            InstalledModName = item.InstalledModName,
            Source = item.Source,
            ProjectId = item.ProjectId,
            Url = item.Url,
            Note = item.Note
        };
    }

    private static ModDependencyLink MergeDependencyGroup(IGrouping<string, ModDependencyLink> group)
    {
        var items = group
            .Where(item => item != null)
            .ToList();

        var primary = items.FirstOrDefault(item => item.Note?.IndexOf("社区整理", StringComparison.OrdinalIgnoreCase) >= 0)
                      ?? items.First();

        var merged = CloneDependencyLink(primary);
        merged.IsRequired = !items.Any(item => !item.IsRequired);

        if (string.IsNullOrWhiteSpace(merged.MinimumVersion))
            merged.MinimumVersion = items.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.MinimumVersion))?.MinimumVersion ?? string.Empty;

        if (string.IsNullOrWhiteSpace(merged.Url))
            merged.Url = items.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Url))?.Url ?? string.Empty;

        if (string.IsNullOrWhiteSpace(merged.ProjectId))
            merged.ProjectId = items.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.ProjectId))?.ProjectId ?? string.Empty;

        var mergedNotes = items
            .Select(item => item.Note?.Trim())
            .Where(note => !string.IsNullOrWhiteSpace(note))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        merged.Note = mergedNotes.Count > 0 ? string.Join("；", mergedNotes) : string.Empty;
        return merged;
    }

    private bool HasCommunityDependencyData => _communityLocalizationEntry?.Dependencies?.Length > 0;

    private async Task LoadCommunityLocalizationRelationsAsync()
    {
        _communityLocalizationEntry = null;
        ClearCommunityRelationCollections();

        if (!LocalizationDisplayHelper.TryResolveRequest(Mod, out var entityType, out var platform, out var id) ||
            !string.Equals(entityType, "mod", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            _communityLocalizationEntry = await CommunityLocalizationService.GetAsync(entityType, platform, id).ConfigureAwait(false);
            SetHardConflictMods(BuildCommunityRelationLinks(_communityLocalizationEntry?.HardConflicts, "社区标注冲突"));
            SetFunctionalOverlapMods(BuildCommunityRelationLinks(_communityLocalizationEntry?.FunctionalOverlaps, "社区标注功能重复"));
        }
        catch (Exception ex)
        {
            _communityLocalizationEntry = null;
            ClearCommunityRelationCollections();
            Log.Debug($"[ModDetailsViewModel] 加载社区本地化关系数据失败: {ex.Message}");
        }
    }

    private List<ModDependencyLink> BuildCommunityDependencyLinks()
    {
        return (_communityLocalizationEntry?.Dependencies ?? Array.Empty<CommunityLocalizationDependency>())
            .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Id))
            .Select(item => new ModDependencyLink
            {
                DisplayName = !string.IsNullOrWhiteSpace(item.Name) ? item.Name : item.Id,
                ProjectId = item.Id,
                Source = ResolveCommunityRelationSource(),
                Url = BuildCommunityRelationUrl(item.Id),
                IsRequired = !item.Optional,
                Note = BuildCommunityNote("社区整理前置", item.Note)
            })
            .ToList();
    }

    private List<ModDependencyLink> BuildCommunityRelationLinks(IEnumerable<CommunityLocalizationRelation>? relations, string notePrefix)
    {
        return (relations ?? Array.Empty<CommunityLocalizationRelation>())
            .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Id))
            .Select(item => new ModDependencyLink
            {
                DisplayName = !string.IsNullOrWhiteSpace(item.Name) ? item.Name : item.Id,
                ProjectId = item.Id,
                Source = ResolveCommunityRelationSource(),
                Url = BuildCommunityRelationUrl(item.Id),
                Note = BuildCommunityNote(notePrefix, item.Reason)
            })
            .ToList();
    }

    private string ResolveCommunityRelationSource()
    {
        if (IsNexusSource)
            return "NexusMods";

        if (IsCurseforgeSource)
            return "Curseforge";

        return Mod?.Source ?? string.Empty;
    }

    private string BuildCommunityRelationUrl(string relationId)
    {
        if (string.IsNullOrWhiteSpace(relationId))
            return string.Empty;

        if (IsNexusSource && long.TryParse(relationId, out _))
            return $"https://www.nexusmods.com/stardewvalley/mods/{relationId}";

        return string.Empty;
    }

    private static string BuildCommunityNote(string prefix, string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return prefix;

        return $"{prefix}：{detail.Trim()}";
    }

    private void SetHardConflictMods(IEnumerable<ModDependencyLink> items)
    {
        SetRelatedMods(HardConflictMods, items);
        OnPropertyChanged(nameof(HasHardConflictMods));
        OnPropertyChanged(nameof(HardConflictModsCount));
    }

    private void SetFunctionalOverlapMods(IEnumerable<ModDependencyLink> items)
    {
        SetRelatedMods(FunctionalOverlapMods, items);
        OnPropertyChanged(nameof(HasFunctionalOverlapMods));
        OnPropertyChanged(nameof(FunctionalOverlapModsCount));
    }

    private void SetRelatedMods(ObservableCollection<ModDependencyLink> target, IEnumerable<ModDependencyLink> items)
    {
        // 在UI线程上执行集合操作
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            target.Clear();
            foreach (var item in (items ?? Enumerable.Empty<ModDependencyLink>())
                         .Where(link => link != null && !string.IsNullOrWhiteSpace(link.DisplayName))
                         .GroupBy(link => $"{link.Source}|{link.ProjectId}|{link.DisplayName}", StringComparer.OrdinalIgnoreCase)
                         .Select(MergeDependencyGroup))
            {
                target.Add(item);
            }
        });
    }

    private void ClearCommunityRelationCollections()
    {
        SetHardConflictMods(Array.Empty<ModDependencyLink>());
        SetFunctionalOverlapMods(Array.Empty<ModDependencyLink>());
    }

    private async Task<List<ModDependencyLink>> DetectNexusRequirementsFromDescriptionAsync(long modId)
    {
        var result = new List<ModDependencyLink>();

        result.AddRange(DetectNexusRequirementsFromText(DisplayDescription, modId));
        if (result.Count > 0)
            return result;

        try
        {
            var detail = await EnsureNexusDetailsAsync(modId);
            if (detail != null)
            {
                result.AddRange(DetectNexusRequirementsFromText(detail.Description, modId));
                result.AddRange(DetectNexusRequirementsFromText(detail.Summary, modId));
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"[ModDetailsViewModel] 解析 Nexus 描述前置失败: {ex.Message}");
        }

        return result;
    }

    private static IEnumerable<ModDependencyLink> DetectNexusRequirementsFromText(string? text, long currentModId)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var matches = Regex.Matches(
            text,
            @"nexusmods\.com/(?:[^/]+/)?mods/(?<id>\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return matches
            .Cast<Match>()
            .Select(match => match.Groups["id"].Value)
            .Where(value => long.TryParse(value, out var parsedId) && parsedId > 0 && parsedId != currentModId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(value =>
            {
                var match = matches.Cast<Match>().FirstOrDefault(item => item.Groups["id"].Value == value);
                var isRequired = match == null || !LooksOptionalRequirementContext(text, match.Index, match.Length);
                return new ModDependencyLink
                {
                    DisplayName = $"Nexus Mod {value}",
                    ProjectId = value,
                    Source = "NexusMods",
                    Url = $"https://www.nexusmods.com/stardewvalley/mods/{value}",
                    IsRequired = isRequired,
                    Note = isRequired ? "描述推断前置" : "描述推断前置（可选）"
                };
            })
            .ToList();
    }

    private static IEnumerable<ModDependencyLink> DetectCurseforgeRequirementsFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<ModDependencyLink>();

        var matches = Regex.Matches(
            text,
            @"curseforge\.com/(?:stardewvalley|sdv)/mods/(?<slug>[a-z0-9\-_]+)|curseforge\.com/stardewvalley/mods/(?<id>\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return matches
            .Cast<Match>()
            .Select(match => new ModDependencyLink
            {
                DisplayName = string.IsNullOrWhiteSpace(match.Groups["id"].Value)
                    ? $"Curseforge Mod {match.Groups["slug"].Value}"
                    : $"Curseforge Mod {match.Groups["id"].Value}",
                ProjectId = match.Groups["id"].Value,
                Source = "Curseforge",
                Url = match.Value.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? match.Value
                    : $"https://www.{match.Value.TrimStart('/')}",
                IsRequired = !LooksOptionalRequirementContext(text, match.Index, match.Length),
                Note = "描述推断前置"
            })
            .GroupBy(item => item.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static bool LooksOptionalRequirementContext(string text, int index, int length)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var start = Math.Max(0, index - 80);
        var end = Math.Min(text.Length, index + length + 80);
        var context = text.Substring(start, end - start);

        return Regex.IsMatch(context, @"optional|optionally|recommended|soft\s+requirement|可选|推荐", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private async Task EnrichRequirementMetadataAsync(List<ModDependencyLink> requirements)
    {
        if (requirements == null || requirements.Count == 0)
            return;

        var nexusTargets = requirements
            .Where(item => string.Equals(item.Source, "NexusMods", StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.ProjectId)
            .Where(group => long.TryParse(group.Key, out var id) && id > 0)
            .ToList();

        foreach (var group in nexusTargets)
        {
            if (!long.TryParse(group.Key, out var nexusId) || nexusId <= 0)
                continue;

            try
            {
                var detail = await NexusModsService.GetModDetailsAsync(nexusId);
                if (detail == null)
                    continue;

                foreach (var item in group)
                {
                    if (!string.IsNullOrWhiteSpace(detail.Name) && item.DisplayName.StartsWith("Nexus Mod ", StringComparison.OrdinalIgnoreCase))
                        item.DisplayName = detail.Name;

                    if (string.IsNullOrWhiteSpace(item.Url))
                        item.Url = $"https://www.nexusmods.com/stardewvalley/mods/{nexusId}";
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"[ModDetailsViewModel] 解析 Nexus 前置名称失败: {group.Key}, {ex.Message}");
            }
        }

        var curseTargets = requirements
            .Where(item => string.Equals(item.Source, "Curseforge", StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.ProjectId)
            .Where(group => int.TryParse(group.Key, out var id) && id > 0)
            .ToList();

        foreach (var group in curseTargets)
        {
            if (!int.TryParse(group.Key, out var curseId) || curseId <= 0)
                continue;

            try
            {
                var modInfo = await CurseforgeApiService.GetModInfoAsync(curseId);
                if (modInfo == null)
                    continue;

                foreach (var item in group)
                {
                    if (!string.IsNullOrWhiteSpace(modInfo.Name) && item.DisplayName.StartsWith("Curseforge Mod ", StringComparison.OrdinalIgnoreCase))
                        item.DisplayName = modInfo.Name;

                    if (string.IsNullOrWhiteSpace(item.Url))
                        item.Url = modInfo.Links?.WebsiteUrl ?? $"https://www.curseforge.com/stardewvalley/mods/{curseId}";
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"[ModDetailsViewModel] 解析 Curseforge 前置名称失败: {group.Key}, {ex.Message}");
            }
        }
    }

    private async Task<NexusMod?> EnsureNexusDetailsAsync(long modId)
    {
        if (_loadedNexusDetails != null)
        {
            var loadedModId = _loadedNexusDetails.ModId > 0 ? _loadedNexusDetails.ModId : _loadedNexusDetails.ModIdGraphQl;
            if (loadedModId == modId)
                return _loadedNexusDetails;
        }

        _loadedNexusDetails = await NexusModsService.GetModDetailsWithSearchFallbackAsync(modId, Mod?.Name);
        return _loadedNexusDetails;
    }

    private async Task<ModSearchItem?> ResolveDependencySearchItemAsync(ModDependencyLink dependency)
    {
        if (dependency == null)
            return null;

        if (string.Equals(dependency.Source, "NexusMods", StringComparison.OrdinalIgnoreCase) && long.TryParse(dependency.ProjectId, out var nexusId) && nexusId > 0)
        {
            var detail = await NexusModsService.GetModDetailsAsync(nexusId);
            return new ModSearchItem
            {
                Id = $"nexus-{nexusId}",
                Name = detail?.Name ?? dependency.DisplayName,
                Author = string.IsNullOrWhiteSpace(detail?.Author) ? "NexusMods" : detail.Author,
                Description = detail?.Description ?? detail?.Summary ?? dependency.Note,
                Summary = detail?.Summary ?? dependency.Note,
                Source = "NexusMods",
                IconUrl = !string.IsNullOrWhiteSpace(detail?.PictureUrl) ? detail.PictureUrl : detail?.PictureUrlLegacy ?? string.Empty,
                DownloadCount = detail?.Downloads ?? 0,
                LastUpdateTime = detail?.UpdatedAt != default ? detail!.UpdatedAt.ToString("yyyy-MM-dd") : string.Empty,
                Url = string.IsNullOrWhiteSpace(dependency.Url) ? $"https://www.nexusmods.com/stardewvalley/mods/{nexusId}" : dependency.Url
            };
        }

        if (string.Equals(dependency.Source, "Curseforge", StringComparison.OrdinalIgnoreCase) && int.TryParse(dependency.ProjectId, out var curseId) && curseId > 0)
        {
            var modInfo = await CurseforgeApiService.GetModInfoAsync(curseId);
            return new ModSearchItem
            {
                Id = $"curse-{curseId}",
                Name = modInfo?.Name ?? dependency.DisplayName,
                Description = modInfo?.Description ?? modInfo?.Summary ?? dependency.Note,
                Summary = modInfo?.Summary ?? dependency.Note,
                Source = "Curseforge",
                IconUrl = modInfo?.Logo?.ThumbnailUrl ?? string.Empty,
                DownloadCount = modInfo?.DownloadCount ?? 0,
                LastUpdateTime = modInfo?.DateModified ?? string.Empty,
                Url = modInfo?.Links?.WebsiteUrl ?? dependency.Url
            };
        }

        return null;
    }

    private static long TryExtractNumericId(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return 0;

        var match = Regex.Match(raw, "(\\d+)(?!.*\\d)");
        return match.Success && long.TryParse(match.Groups[1].Value, out var id) ? id : 0;
    }

    private static bool TryParseCurseforgeDependencyId(CurseforgeFileDependency dependency, out int modId, out bool isRequired, out string relationType)
    {
        modId = 0;
        isRequired = true;
        relationType = "Unknown";

        if (dependency == null)
            return false;

        modId = dependency.ModId > 0 ? dependency.ModId : dependency.AddonId;
        if (modId <= 0)
            return false;

        var parsedRelationType = Enum.IsDefined(typeof(CurseforgeFileRelationType), dependency.RelationType)
            ? (CurseforgeFileRelationType)dependency.RelationType
            : 0;

        relationType = parsedRelationType.ToString();

        if (dependency.Required.HasValue)
            isRequired = dependency.Required.Value;

        switch (parsedRelationType)
        {
            case CurseforgeFileRelationType.OptionalDependency:
                isRequired = false;
                return true;
            case CurseforgeFileRelationType.RequiredDependency:
            case CurseforgeFileRelationType.EmbeddedLibrary:
                isRequired = true;
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseCurseforgeRelation(CurseforgeApiService.CurseforgeModRelation relation, out int modId, out bool isRequired, out string relationType)
    {
        modId = 0;
        isRequired = true;
        relationType = "Unknown";

        if (relation == null || relation.ModId <= 0)
            return false;

        modId = relation.ModId;

        var parsedRelationType = Enum.IsDefined(typeof(CurseforgeFileRelationType), relation.RelationType)
            ? (CurseforgeFileRelationType)relation.RelationType
            : 0;

        relationType = parsedRelationType.ToString();

        switch (parsedRelationType)
        {
            case CurseforgeFileRelationType.OptionalDependency:
                isRequired = false;
                return true;
            case CurseforgeFileRelationType.RequiredDependency:
            case CurseforgeFileRelationType.EmbeddedLibrary:
                isRequired = true;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// 获取发布类型字符串
    /// </summary>
    private string GetReleaseTypeString(int releaseType)
    {
        return releaseType switch
        {
            1 => "Release",
            2 => "Beta",
            3 => "Alpha",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// 返回搜索页面
    /// </summary>
    [RelayCommand]
    private void GoBack()
    {
        // 导航回下载页面
        if (System.Windows.Application.Current.MainWindow is MainWindow mainWindow &&
            mainWindow.DataContext is MainWindowViewModel mainViewModel)
        {
            mainViewModel.CurrentPage = PageType.Download;
            Log.Info("[ModDetailsViewModel] 返回下载页面");
        }
    }

    /// <summary>
    /// 打开 MOD 来源页面
    /// </summary>
    [RelayCommand]
    private void OpenSourceUrl()
    {
        if (!string.IsNullOrEmpty(Mod.Url))
        {
            try
            {
                ProcessEx.OpenUrl(Mod.Url);
                Log.Info($"[ModDetailsViewModel] 已打开来源页面: {Mod.Url}");
            }
            catch (System.Exception ex)
            {
                Log.Error(ex, "[ModDetailsViewModel] 打开来源页面失败");
            }
        }
    }

    /// <summary>
    /// 复制 MOD 名称
    /// </summary>
    [RelayCommand]
    private void CopyName()
    {
        var displayName = Mod?.DisplayName ?? Mod?.Name ?? string.Empty;
        System.Windows.Clipboard.SetText(displayName);
        Log.Info($"[ModDetailsViewModel] 已复制 MOD 名称: {displayName}");
        FloatingNotificationControl.Show(
            title: "已复制",
            message: $"已复制名称：{displayName}",
            autoCloseDelay: 1800,
            notificationType: NotificationType.Success);
    }

    [RelayCommand]
    private void OpenLocalizationContributionPage()
    {
        try
        {
            // 获取与复制ID相同的值（保留完整ID格式，包括前缀）
            var contributionId = Mod?.Id ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(Mod?.Id) && Mod.Id.StartsWith("nexuscol-", StringComparison.OrdinalIgnoreCase))
            {
                // Nexus集合：提取尾链(slug)
                var slug = ExtractCollectionSlug(Mod.Url);
                if (!string.IsNullOrWhiteSpace(slug))
                    contributionId = slug;
            }
            // 其他情况直接保留 Mod.Id（如 nexus-2400, curse-898372）

            // 构建URL参数：id=<完整ID或尾链>
            var idParam = string.Empty;
            if (!string.IsNullOrWhiteSpace(contributionId))
            {
                idParam = $"?id={contributionId}";
            }

            var url = $"{LocalizationContributionUrl}{idParam}&auto=1";
            ProcessEx.OpenUrl(url);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[ModDetailsViewModel] 打开本地化贡献页面失败");
            SvlMessageBox.Warning($"无法打开贡献页面：{ex.Message}", "打开失败");
        }
    }

    [RelayCommand]
    private void ShowLocalizationContributorInfo()
    {
        var message = "贡献本地化说明\n\n" +
            "点击「贡献本地化」按钮可以跳转到社区本地化贡献页面，为当前 Mod 添加中文翻译。\n\n";

        if (!string.IsNullOrWhiteSpace(LocalizationContributor))
        {
            message += $"当前资源的本地化贡献者：{LocalizationContributor}";
        }
        else
        {
            message += "当前资源还没有人进行汉化贡献，欢迎前往贡献页面参与补充。";
        }

        if (!string.IsNullOrWhiteSpace(Mod?.LocalizationUpdatedAt))
        {
            message += $"\n\n本地化最终更新时间：{Mod.LocalizationUpdatedAt}";
        }

        SvlMessageBox.Info(message, "贡献本地化");
    }

    /// <summary>
    /// 复制 MOD ID
    /// </summary>
    [RelayCommand]
    private void CopyId()
    {
        var copiedId = Mod?.Id ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(Mod?.Id) && Mod.Id.StartsWith("nexuscol-", StringComparison.OrdinalIgnoreCase))
        {
            var slug = ExtractCollectionSlug(Mod.Url);
            if (!string.IsNullOrWhiteSpace(slug))
                copiedId = slug;
        }

        System.Windows.Clipboard.SetText(copiedId);
        Log.Info($"[ModDetailsViewModel] 已复制 MOD ID: {copiedId}");
        FloatingNotificationControl.Show(
            title: "已复制",
            message: $"已复制{CopyIdNotificationLabel}：{copiedId}",
            autoCloseDelay: 1800,
            notificationType: NotificationType.Success);
    }

    /// <summary>
    /// 下载指定版本的 MOD（执行方法）
    /// </summary>
    private async Task DownloadModAsync(ModVersionItem version)
    {
        Log.Info($"[ModDetailsViewModel] DownloadModAsync 被调用，version: {version?.FileName}");

        try
        {
            if (version == null)
            {
                Log.Warn("[ModDetailsViewModel] version 参数为 null");
                return;
            }

            // 检测是否是 Nexus Collection 类型
            if (IsNexusCollection(version))
            {
                // Nexus Collection 使用安装对话框流程（选择 base → 输入版本名称）
                if (System.Windows.Application.Current.MainWindow is System.Windows.Window owner)
                {
                    await StartNexusCollectionInstallAsync(owner, version);
                }
                return;
            }

            // 检测是否是 Curseforge 整合包
            if (IsModpackMode && Mod.Source == "Curseforge")
            {
                await DownloadCurseforgeModpackAsync(version);
                return;
            }

            if (IsModpackMode)
            {
                // 整合包应该通过拖放导入功能安装，MOD 详情页面不处理整合包安装
                Log.Warn($"[ModDetailsViewModel] 整合包模式不支持从 MOD 详情页面安装，请使用拖放导入功能");
                SvlMessageBox.Info(
                    "整合包请通过以下方式安装：\n\n1. 将整合包文件拖放到主窗口\n2. 点击「版本选择」页面的「导入整合包」按钮",
                    "安装提示");
                return;
            }

            Log.Info($"[ModDetailsViewModel] 下载 MOD: {Mod.Name}, 版本: {version.Version}, 游戏版本: {version.GameVersion}");

            // 1. 判断是否是 SMAPI
            if (IsSmapApiMod())
            {
                Log.Info($"[ModDetailsViewModel] 检测到 SMAPI 模组，使用 SMAPI 安装流程");
                await DownloadSmapApiAsync(version);
                return;
            }

            // 2. 获取当前选中的游戏实例
            var currentInstance = GetCurrentSelectedInstance();
            if (currentInstance == null)
            {
                Log.Warn("[ModDetailsViewModel] 未找到选中的游戏实例");
                SvlMessageBox.Warning(
                    "未找到选中的游戏实例，请先在启动页面选择一个游戏实例。",
                    "无法下载");
                return;
            }

            Log.Info($"[ModDetailsViewModel] 当前实例: {currentInstance.Name}, 路径: {currentInstance.Path}");
            Log.Info($"[ModDetailsViewModel] 实例配置 - SMAPI: {currentInstance.IsSMAPIInstance}, 版本隔离: {currentInstance.EnableIsolation}");

            // 3. 计算默认的 Mods 路径
            string defaultModsPath;
            if (currentInstance.EnableIsolation)
            {
                defaultModsPath = InstanceIsolationService.GetIsolatedModsPath(currentInstance.Path, currentInstance.Name);
            }
            else
            {
                defaultModsPath = System.IO.Path.Combine(currentInstance.Path, "Mods");
            }

            Log.Info($"[ModDetailsViewModel] 默认 Mods 路径: {defaultModsPath}");

            // 4. 检查 MOD 是否已存在
            var existingModVersion = CheckModExists(defaultModsPath, Mod.Name);
            if (existingModVersion != null)
            {
                Log.Info($"[ModDetailsViewModel] MOD 已存在，当前版本: {existingModVersion}, 新版本: {version.Version}");

                // 判断是更新还是降级
                bool isUpdate = CompareVersions(version.Version, existingModVersion) > 0;

                // 显示更新确认对话框
                bool confirmed = await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (System.Windows.Application.Current.MainWindow is System.Windows.Window owner)
                    {
                        return Controls.ModUpdateConfirmDialog.Show(
                            owner,
                            Mod.Name,
                            existingModVersion,
                            version.Version,
                            isUpdate
                        );
                    }
                    return false;
                });

                if (!confirmed)
                {
                    Log.Info("[ModDetailsViewModel] 用户取消了更新");
                    return;
                }

                Log.Info($"[ModDetailsViewModel] 用户确认{(isUpdate ? "更新" : "降级")} MOD");
            }
            else
            {
                Log.Info("[ModDetailsViewModel] MOD 不存在，将执行新安装");
            }

            // 5. 弹出另存为对话框（无论版本是否匹配）
            string finalModsPath = await ShowSaveAsDialog(defaultModsPath, version.FileName);
            if (finalModsPath == null)
            {
                Log.Info("[ModDetailsViewModel] 用户取消了下载");
                return;
            }

            Log.Info($"[ModDetailsViewModel] 最终安装路径: {finalModsPath}");

            // 6. NexusMods 复用 SMAPI 流程：先下载 zip（OAuth + 缓存 + 进度），再创建本地安装任务
            if (Mod.Source == "NexusMods")
            {
                var nexusModId = await EnsureNexusModIdAsync();
                if (nexusModId <= 0)
                {
                    Log.Error("[ModDetailsViewModel] 无法解析 NexusMods Mod ID");
                    SvlMessageBox.Error(
                        "无法解析 NexusMods Mod ID，请稍后重试。",
                        "下载错误");
                    return;
                }

                var fileIdMatch = Regex.Match(version.FileId ?? string.Empty, @"(\d+)$");
                if (!fileIdMatch.Success || !long.TryParse(fileIdMatch.Groups[1].Value, out var nexusFileId) || nexusFileId <= 0)
                {
                    Log.Error("[ModDetailsViewModel] 无法解析 NexusMods 文件 ID");
                    SvlMessageBox.Error(
                        "无法解析 NexusMods 文件 ID，请稍后重试。",
                        "下载错误");
                    return;
                }

                var tempDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SVL",
                    "temp",
                    "mods"
                );

                string zipPath;
                try
                {
                    zipPath = await SVL.Core.Download.NexusMods.NexusDownloadWorkflow.DownloadZipAsync(
                        gameId: "stardewvalley",
                        modId: nexusModId,
                        fileId: nexusFileId,
                        workingDirectory: tempDir,
                        progressCallback: _ => { },
                        useCache: true);
                }
                catch (SVL.Core.Download.NexusMods.NexusPremiumRequiredException premiumEx)
                {
                    Log.Warn($"[ModDetailsViewModel] NexusMods 非 Premium，创建任务等待浏览器下载: modId={nexusModId}, fileId={nexusFileId}");

                    // 创建等待浏览器下载的任务
                    var browserDownloadTask = new SVL.Core.Download.NexusMods.NexusModsBrowserDownloadTask(
                        gameId: "stardewvalley",
                        modId: nexusModId,
                        fileId: nexusFileId,
                        modName: Mod.Name,
                        fileName: version.FileName,
                        downloadPageUrl: premiumEx.DownloadPageUrl,
                        targetModsPath: finalModsPath,
                        gameBasePath: currentInstance.Path
                    );

                    await SVL.Core.Download.DownloadManager.Instance.AddTaskAsync(browserDownloadTask);

                    Log.Info($"[ModDetailsViewModel] 已添加浏览器下载任务: {Mod.Name}");

                    // 自动导航到任务管理页面
                    if (System.Windows.Application.Current.MainWindow is MainWindow mw1 &&
                        mw1.DataContext is MainWindowViewModel mvm1)
                    {
                        mvm1.CurrentPage = PageType.Download;
                    }

                    // 显示提示用户在浏览器中操作
                    Controls.FloatingNotificationControl.Show(
                        title: "已打开浏览器",
                        message: "请在浏览器中点击 Manual Download，SVL 将自动接收下载。",
                        autoCloseDelay: 5000,
                        notificationType: Controls.NotificationType.Info
                    );
                    return;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[ModDetailsViewModel] NexusMods 下载失败");
                    SvlMessageBox.Error(
                        $"NexusMods 下载失败：{ex.Message}",
                        "下载错误");
                    return;
                }

                var nexusInstallTask = new ModDownloadTask(
                    modId: Mod.Id,
                    modName: Mod.Name,
                    fileName: version.FileName,
                    localZipPath: zipPath,
                    isLocalFile: true,
                    gameBasePath: currentInstance.Path,
                    targetModsPath: finalModsPath,
                    sourcePlatform: "NexusMods",
                    sourceProjectId: nexusModId.ToString(),
                    sourceFileId: nexusFileId.ToString()
                );

                await DownloadManager.Instance.AddTaskAsync(nexusInstallTask);

                Log.Info($"[ModDetailsViewModel] 已添加 NexusMods 安装任务: {Mod.Name}");

                // 自动导航到任务管理页面
                if (System.Windows.Application.Current.MainWindow is MainWindow mw2 &&
                    mw2.DataContext is MainWindowViewModel mvm2)
                {
                    mvm2.CurrentPage = PageType.Download;
                }

                // 显示提示
                Controls.FloatingNotificationControl.Show(
                    title: "下载任务已添加",
                    message: $"{Mod.Name} 正在下载中，请在任务管理页面查看进度。",
                    autoCloseDelay: 3000,
                    notificationType: Controls.NotificationType.Success
                );
                return;
            }

            // 7. 其它来源沿用原下载 URL 流程
            string downloadUrl = version.DownloadUrl;
            if (string.IsNullOrEmpty(downloadUrl))
            {
                Log.Error("[ModDetailsViewModel] 下载链接为空，无法继续");
                SvlMessageBox.Error(
                    "下载链接为空，无法继续。",
                    "下载错误");
                return;
            }

            var downloadTask = new ModDownloadTask(
                modId: Mod.Id,
                modName: Mod.Name,
                fileName: version.FileName,
                downloadUrl: downloadUrl,
                gameBasePath: currentInstance.Path,
                targetModsPath: finalModsPath,
                sourcePlatform: Mod.Source,
                sourceProjectId: Mod.Source == "Curseforge" ? Mod.Id : null,
                sourceFileId: version.FileId
            );

            await DownloadManager.Instance.AddTaskAsync(downloadTask);

            Log.Info($"[ModDetailsViewModel] 已添加下载任务: {Mod.Name}");

            // 自动导航到任务管理页面
            if (System.Windows.Application.Current.MainWindow is MainWindow mw3 &&
                mw3.DataContext is MainWindowViewModel mvm3)
            {
                mvm3.CurrentPage = PageType.Download;
            }

            // 显示提示
            Controls.FloatingNotificationControl.Show(
                title: "下载任务已添加",
                message: $"{Mod.Name} 正在下载中，请在任务管理页面查看进度。",
                autoCloseDelay: 3000,
                notificationType: Controls.NotificationType.Success
            );
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "[ModDetailsViewModel] 下载 MOD 失败");
            SvlMessageBox.Error(
                $"下载失败: {ex.Message}",
                "下载错误");
        }
    }

    /// <summary>
    /// 检测是否是 Nexus Collection 类型
    /// </summary>
    private bool IsNexusCollection(ModVersionItem version)
    {
        // FileId 格式: nexuscol-{slug}-rev{revisionNumber}
        return !string.IsNullOrEmpty(version.FileId) && version.FileId.StartsWith("nexuscol-");
    }

    /// <summary>
    /// 下载 Curseforge 整合包（参考 SMAPI 安装流程）
    /// </summary>
    private async Task DownloadCurseforgeModpackAsync(ModVersionItem version)
    {
        if (_isCurseforgeModpackDownloadStarting)
        {
            Log.Warn("[ModDetailsViewModel] Curseforge 整合包下载正在启动中，忽略重复触发");
            return;
        }

        Log.Info($"[ModDetailsViewModel] 下载 Curseforge 整合包: {version.FileName}");

        _isCurseforgeModpackDownloadStarting = true;
        try
        {
            // 1. 解析 Curseforge Modpack ID 和 File ID
            if (!TryParseCurseforgeModpackId(out var projectId))
            {
                Log.Warn($"[ModDetailsViewModel] 无法解析 Curseforge Modpack Project ID: {Mod.Id}");
                OpenModpackWebPage();
                return;
            }

            if (!TryParseCurseforgeFileId(version, out var fileId))
            {
                Log.Warn($"[ModDetailsViewModel] 无法解析 Curseforge File ID: {version.FileId}");
                OpenModpackWebPage();
                return;
            }

            Log.Info($"[ModDetailsViewModel] 解析到 ProjectId: {projectId}, FileId: {fileId}");

            // 2. 获取安装配置（游戏路径、实例名称）
            var owner = System.Windows.Application.Current.MainWindow;
            var config = await GetModpackInstallConfigAsync(owner, Mod.Name);
            if (config == null)
            {
                Log.Info("[ModDetailsViewModel] 用户取消了整合包安装");
                return;
            }

            // 3. 创建整合包下载任务
            var downloadTask = new CurseforgeModpackDownloadTask(
                modpackName: Mod.Name,
                fileName: version.FileName,
                projectId: projectId,
                fileId: fileId,
                gameBasePath: config.GameBasePath,
                instanceName: config.InstanceName,
                targetModsPath: config.TargetModsPath,
                directDownloadUrl: version.DownloadUrl
            );

            await DownloadManager.Instance.AddTaskAsync(downloadTask);

            Log.Info($"[ModDetailsViewModel] 已创建 Curseforge 整合包下载任务: {Mod.Name}");

            // 4. 跳转到下载状态页面
            NavigateToDownloadStatusPage(downloadTask);
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[ModDetailsViewModel] Curseforge 整合包下载失败: {Mod.Name}");
            SvlMessageBox.Error(
                $"整合包下载失败: {ex.Message}\n\n请尝试手动从网站下载。",
                "下载错误");
        }
        finally
        {
            _isCurseforgeModpackDownloadStarting = false;
        }
    }

    /// <summary>
    /// 跳转到下载状态页面
    /// </summary>
    private void NavigateToDownloadStatusPage(DownloadTask downloadTask)
    {
        if (System.Windows.Application.Current.MainWindow is MainWindow mainWindow &&
            mainWindow.DataContext is MainWindowViewModel mainViewModel)
        {
            mainViewModel.CurrentPage = PageType.DownloadFailure;

            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (mainViewModel.LeftPanelContent is TaskStatusViewModel statusViewModel)
                {
                    statusViewModel.SetProgressInfo(downloadTask);
                }
            }));
        }
    }

    /// <summary>
    /// 打开整合包网页（用于无法自动下载的情况）
    /// </summary>
    private void OpenModpackWebPage()
    {
        if (!string.IsNullOrEmpty(Mod.Url))
        {
            try
            {
                ProcessEx.OpenUrl(Mod.Url);
                Log.Info($"[ModDetailsViewModel] 已打开整合包网页: {Mod.Url}");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[ModDetailsViewModel] 打开网页失败");
            }
        }
    }

    /// <summary>
    /// 下载 Nexus Collection（Premium 直接下载，非 Premium 打开浏览器）
    /// </summary>
    private async Task DownloadNexusCollectionAsync(ModVersionItem version)
    {
        Log.Info($"[ModDetailsViewModel] 检测到 Nexus Collection: {version.FileId}");

        // 从 FileId 解析 slug 和 revisionNumber
        // 格式: nexuscol-{slug}-rev{revisionNumber}
        var match = Regex.Match(version.FileId ?? "", @"nexuscol-(.+)-rev(\d+)");
        if (!match.Success)
        {
            Log.Error($"[ModDetailsViewModel] 无法解析 Collection FileId: {version.FileId}");
            SvlMessageBox.Error(
                "无法解析 Collection 信息，请稍后重试。",
                "下载错误");
            return;
        }

        var collectionSlug = match.Groups[1].Value;
        var revisionNumber = int.Parse(match.Groups[2].Value);

        Log.Info($"[ModDetailsViewModel] Collection Slug: {collectionSlug}, Revision: {revisionNumber}");

        // 检查 OAuth Token
        var accessToken = SVL.Core.Config.AppConfig.GetSettings().NexusModsOAuthToken;
        if (string.IsNullOrEmpty(accessToken))
        {
            Log.Warn("[ModDetailsViewModel] 未登录 NexusMods，无法下载 Collection");
            SvlMessageBox.Warning(
                "请先在设置中登录 NexusMods 账户后再下载 Collection。",
                "需要登录");
            return;
        }

        // 尝试 Premium 直接下载
        try
        {
            Log.Info($"[ModDetailsViewModel] 尝试 Premium 直接下载 Collection: {collectionSlug}");

            var collectionTask = new SVL.Core.Download.NexusMods.NexusCollectionDownloadTask(
                gameId: "stardewvalley",
                collectionSlug: collectionSlug,
                revisionNumber: revisionNumber,
                oauthToken: accessToken
            );

            await SVL.Core.Download.DownloadManager.Instance.AddTaskAsync(collectionTask);

            Log.Info($"[ModDetailsViewModel] Collection 下载任务已添加: {Mod.Name}");

            // 显示成功通知
            Controls.FloatingNotificationControl.Show(
                title: "Collection 下载已开始",
                message: $"正在下载 {Mod.Name}...",
                autoCloseDelay: 3000,
                notificationType: NotificationType.Success
            );
        }
        catch (SVL.Core.Download.NexusMods.NexusPremiumRequiredException)
        {
            // 非 Premium 用户：注册待处理下载并打开浏览器
            Log.Info($"[ModDetailsViewModel] 非 Premium 用户，切换浏览器引导流程: {collectionSlug}");

            await DownloadRightViewModel.RegisterPendingNexusCollectionDownloadAsync(
                collectionSlug: collectionSlug,
                revisionNumber: revisionNumber,
                collectionName: Mod.Name
            );

            // 构造浏览器下载页面 URL
            var downloadPageUrl = $"https://www.nexusmods.com/games/stardewvalley/collections/{collectionSlug}/revisions/{revisionNumber}";

            await ShowNexusCollectionBrowserGuideAsync(collectionSlug, revisionNumber, downloadPageUrl);
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[ModDetailsViewModel] Collection 下载失败: {collectionSlug}");
            SvlMessageBox.Error(
                $"Collection 下载失败: {ex.Message}",
                "下载错误");
        }
    }

    /// <summary>
    /// 显示 Nexus Collection 浏览器引导对话框
    /// </summary>
    private async Task ShowNexusCollectionBrowserGuideAsync(string slug, int revisionNumber, string downloadPageUrl, string? savePath = null)
    {
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            // 打开浏览器
            try
            {
                ProcessEx.OpenUrl(downloadPageUrl);

                Log.Info($"[ModDetailsViewModel] 已打开浏览器: {downloadPageUrl}");

                // 显示引导通知
                var message = "请在浏览器中点击「Download」下载 Collection。\n\nSVL 将自动接收下载并开始安装。";
                if (!string.IsNullOrEmpty(savePath))
                {
                    message += $"\n\n保存位置: {savePath}";
                }

                Controls.FloatingNotificationControl.Show(
                    title: "浏览器已打开",
                    message: message,
                    autoCloseDelay: 10000,
                    notificationType: NotificationType.Info
                );
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[ModDetailsViewModel] 打开浏览器失败");
                SvlMessageBox.Info(
                    $"无法打开浏览器，请手动访问:\n{downloadPageUrl}",
                    "请手动下载");
            }
        });
    }

    /// <summary>
    /// 另存为 Nexus Collection（使用浏览器下载任务方式）
    /// </summary>
    private async Task SaveNexusCollectionAsAsync(ModVersionItem version)
    {
        Log.Info($"[ModDetailsViewModel] SaveNexusCollectionAsAsync 被调用，version: {version?.FileId}");

        try
        {
            // 1. 解析 Collection 信息
            var match = System.Text.RegularExpressions.Regex.Match(version.FileId ?? "", @"nexuscol-(.+)-rev(\d+)");
            if (!match.Success)
            {
                Log.Error($"[ModDetailsViewModel] 无法解析 Collection FileId: {version.FileId}");
                SvlMessageBox.Error(
                    "无法解析 Collection 信息，请稍后重试。",
                    "另存为错误");
                return;
            }

            var collectionSlug = match.Groups[1].Value;
            var revisionNumber = int.Parse(match.Groups[2].Value);

            Log.Info($"[ModDetailsViewModel] Collection Slug: {collectionSlug}, Revision: {revisionNumber}");

            // 2. 使用另存为对话框（类似 Curseforge 整合包）
            var fileName = $"{Mod.Name}_{collectionSlug}_r{revisionNumber}.7z";
            var savePath = await ShowSimpleSaveAsDialog(fileName);
            if (savePath == null)
            {
                Log.Info("[ModDetailsViewModel] 用户取消了另存为");
                return;
            }

            // 获取保存目录（文件名不包含路径）
            var saveFolder = System.IO.Path.GetDirectoryName(savePath);

            Log.Info($"[ModDetailsViewModel] 另存为路径: {savePath}, 文件夹: {saveFolder}");

            // 3. 注册待处理的 Collection 浏览器下载（非 Premium 流程）
            // 注意：RegisterPendingNexusCollectionDownloadAsync 会自动创建占位符任务
            await DownloadRightViewModel.RegisterPendingNexusCollectionDownloadAsync(
                collectionSlug: collectionSlug,
                revisionNumber: revisionNumber,
                collectionName: Mod.Name,
                saveDirectory: saveFolder
            );

            Log.Info($"[ModDetailsViewModel] Collection 浏览器下载任务已添加: {Mod.Name}");

            // 4. 显示浏览器下载引导（包含 URL 和保存位置）
            var downloadPageUrl = $"https://www.nexusmods.com/games/stardewvalley/collections/{collectionSlug}/revisions/{revisionNumber}";
            await ShowNexusCollectionBrowserGuideAsync(collectionSlug, revisionNumber, downloadPageUrl, saveFolder);
        }
        catch (System.OperationCanceledException)
        {
            Log.Info("[ModDetailsViewModel] Collection 另存为已取消");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[ModDetailsViewModel] Collection 另存为失败");
            SvlMessageBox.Error(
                $"Collection 另存为失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 显示文件夹选择对话框
    /// </summary>
    private async Task<string?> ShowSimpleFolderDialogAsync(string title = "选择文件夹")
    {
        return await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            using var dialog = new Utilities.SimpleFolderDialog
            {
                Title = title
            };

            if (dialog.ShowDialog())
            {
                return dialog.SelectedPath;
            }
            return null;
        });
    }

    /// <summary>
    /// 启动 Nexus Collection 安装流程（与 Curseforge 一致）
    /// </summary>
    private async Task StartNexusCollectionInstallAsync(System.Windows.Window owner, ModVersionItem version)
    {
        if (_isNexusCollectionInstalling)
        {
            Log.Warn("[ModDetailsViewModel] Nexus Collection 安装正在启动中，忽略重复触发");
            return;
        }

        Log.Info($"[ModDetailsViewModel] 安装 Nexus Collection: {version.FileName}");

        _isNexusCollectionInstalling = true;
        try
        {
            // 1. 解析 Collection 信息
            var match = System.Text.RegularExpressions.Regex.Match(version.FileId ?? "", @"nexuscol-(.+)-rev(\d+)");
            if (!match.Success)
            {
                Log.Error($"[ModDetailsViewModel] 无法解析 Collection FileId: {version.FileId}");
                SvlMessageBox.Error(
                    "无法解析 Collection 信息，请稍后重试。",
                    "安装错误");
                return;
            }

            var collectionSlug = match.Groups[1].Value;
            var revisionNumber = int.Parse(match.Groups[2].Value);

            Log.Info($"[ModDetailsViewModel] Collection Slug: {collectionSlug}, Revision: {revisionNumber}");

            // 2. 获取当前游戏实例（确定游戏路径）
            var currentInstance = GetCurrentSelectedInstance();
            if (currentInstance == null)
            {
                Log.Warn("[ModDetailsViewModel] 未找到选中的游戏实例");
                SvlMessageBox.Warning(
                    "未找到选中的游戏实例，请先在启动页面选择一个游戏实例。",
                    "无法安装");
                return;
            }

            var gameBasePath = currentInstance.Path;
            Log.Info($"[ModDetailsViewModel] 游戏基础路径: {gameBasePath}");

            // 3. 生成默认实例名称
            var defaultInstanceName = Mod.Name.Replace("[整合包]", "").Replace("整合包", "").Trim();
            if (string.IsNullOrEmpty(defaultInstanceName))
            {
                defaultInstanceName = Mod.Name;
            }

            // 4. 使用 InstanceNameDialog 获取实例名称（复用 Curseforge 的美观对话框）
            string? instanceName = null;
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                instanceName = SVL.Desktop.Controls.InstanceNameDialog.Show(
                    owner,
                    SVL.Core.IO.FileNameValidator.SanitizeFolderName(defaultInstanceName),
                    checkNameExists: (name) =>
                    {
                        // 使用完整的验证逻辑：检查实例列表 + 文件系统版本目录
                        var (isValid, errorMessage) = SVL.Core.Stardew.Instance.InstanceIsolationService.ValidateInstanceName(name, gameBasePath);
                        return !isValid; // 如果无效（名称已存在），返回 true
                    },
                    autoSanitize: true);
            });

            if (string.IsNullOrEmpty(instanceName))
            {
                Log.Info("[ModDetailsViewModel] 用户取消了实例名称输入");
                return;
            }

            Log.Info($"[ModDetailsViewModel] 用户输入的实例名称: {instanceName}");

            // 5. 计算默认的 Mods 路径（默认开启版本隔离）
            var targetModsPath = InstanceIsolationService.GetIsolatedModsPath(gameBasePath, instanceName);
            Log.Info($"[ModDetailsViewModel] Mods 路径: {targetModsPath}");

            var config = new ModpackInstallConfig
            {
                GameBasePath = gameBasePath,
                InstanceName = instanceName,
                TargetModsPath = targetModsPath
            };

            // 6. 检查 Premium 状态
            var settings = SVL.Core.Config.AppConfig.GetSettings();
            bool isPremium = settings.IsNexusModsPremium;

            Log.Info($"[ModDetailsViewModel] NexusMods Premium 状态: {isPremium}");

            if (!isPremium)
            {
                // 非 Premium 用户，使用向导流程
                Log.Info("[ModDetailsViewModel] 非 Premium 用户，使用浏览器下载向导");
                await ShowNonPremiumWizardAsync(owner, collectionSlug, revisionNumber, config);
                return;
            }

            // 4. Premium 用户：下载 Collection 并自动安装
            var downloadTask = new SVL.Core.Download.NexusMods.NexusCollectionDownloadTask(
                gameId: "stardewvalley",
                collectionSlug: collectionSlug,
                revisionNumber: revisionNumber,
                downloadDirectory: System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SVL", "collections", Guid.NewGuid().ToString()),
                gameBasePath: config.GameBasePath,
                instanceName: config.InstanceName,
                targetModsPath: config.TargetModsPath);

            await DownloadManager.Instance.AddTaskAsync(downloadTask);

            Log.Info($"[ModDetailsViewModel] 已创建 Nexus Collection 下载任务（Premium 用户）: {Mod.Name}");

            // 5. 跳转到下载状态页面
            NavigateToDownloadStatusPage(downloadTask);
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[ModDetailsViewModel] Nexus Collection 安装失败: {Mod.Name}");
            SvlMessageBox.Error(
                $"Collection 安装失败: {ex.Message}\n\n请尝试稍后重试。",
                "安装错误");
        }
        finally
        {
            _isNexusCollectionInstalling = false;
        }
    }

    /// <summary>
    /// 显示非 Premium 用户的 Collection 安装向导
    /// </summary>
    private async Task ShowNonPremiumWizardAsync(
        System.Windows.Window owner,
        string collectionSlug,
        int revisionNumber,
        ModpackInstallConfig config)
    {
        try
        {
            Log.Info("[ModDetailsViewModel] 准备显示非 Premium 安装向导");

            // 通过 API 获取 Collection Revision 详情
            var settings = SVL.Core.Config.AppConfig.GetSettings();
            var accessToken = settings.NexusModsOAuthToken;

            if (string.IsNullOrEmpty(accessToken))
            {
                SvlMessageBox.Warning(
                    "未找到 NexusMods OAuth Token，请先登录。",
                    "需要登录");
                return;
            }

            // 获取 Collection 信息（包含 download_links）
            // 从设置读取是否使用缓存
            var useCache = settings.EnableNexusModsSearchCache;

            var revisionDetail = await SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsService.GetCollectionRevisionDetailAsync(
                collectionSlug, revisionNumber, "stardewvalley", useCache);

            if (revisionDetail == null)
            {
                SvlMessageBox.Error(
                    $"获取 Collection 信息失败: {collectionSlug} r{revisionNumber}",
                    "获取失败");
                return;
            }

            Log.Info($"[ModDetailsViewModel] Collection Revision Detail: {collectionSlug} r{revisionNumber}, DownloadLink: {revisionDetail.DownloadLink}");

            // 获取 Collection 图片 URL（直接从 Mod.IconUrl 获取）
            string collectionPictureUrl = string.Empty;
            if (!string.IsNullOrEmpty(Mod.IconUrl))
            {
                collectionPictureUrl = Mod.IconUrl;
                Log.Info($"[ModDetailsViewModel] ✓ 从 Mod 获取 Collection 图片: {collectionPictureUrl}");
            }
            else if (!string.IsNullOrEmpty(Mod.LocalIconPath))
            {
                // 如果有本地缓存图标路径，直接使用
                collectionPictureUrl = Mod.LocalIconPath;
                Log.Info($"[ModDetailsViewModel] ✓ 使用本地缓存图标: {collectionPictureUrl}");
            }
            else
            {
                Log.Warn($"[ModDetailsViewModel] 未找到 Collection 图标 URL，将使用默认图标");
            }

            // 直接创建任务，让任务自己下载 JSON 和压缩包
            var wizardTask = new SVL.Core.Download.NexusMods.NexusCollectionWizardTask(
                revisionDetail.DownloadLink,
                accessToken,
                collectionSlug,
                config.InstanceName,
                config.GameBasePath,
                config.TargetModsPath,
                collectionPictureUrl);

            await DownloadManager.Instance.AddTaskAsync(wizardTask);

            Log.Info("[ModDetailsViewModel] 已创建 Collection 安装任务，请在下载管理器中查看");

            FloatingNotificationControl.Show(
                title: "Collection 安装",
                message: "Collection 安装任务已创建，请在下载管理器中查看安装进度。",
                autoCloseDelay: 5000
            );
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[ModDetailsViewModel] 创建 Collection 安装任务失败");
            SvlMessageBox.Error(
                $"创建安装任务失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 下载 Collection JSON
    /// </summary>
    private async Task<string?> DownloadCollectionJsonAsync(string downloadLink, string accessToken)
    {
        try
        {
            var fullUrl = downloadLink.StartsWith("http")
                ? downloadLink
                : $"https://api.nexusmods.com{downloadLink}";

            Log.Info($"[ModDetailsViewModel] 下载 Collection JSON: {fullUrl}");

            using var client = new System.Net.Http.HttpClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
            client.DefaultRequestHeaders.Add("User-Agent", "SVL-StardewLauncher/1.0");
            client.Timeout = TimeSpan.FromSeconds(30);

            var response = await client.GetAsync(fullUrl);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"[ModDetailsViewModel] 下载 Collection JSON 失败: {response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            Log.Info($"[ModDetailsViewModel] Collection JSON 下载成功，大小: {json.Length} 字节");

            return json;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[ModDetailsViewModel] 下载 Collection JSON 异常");
            return null;
        }
    }

    /// <summary>
    /// 下载 Collection 压缩包（7z 格）
    /// </summary>
    private async Task<string?> DownloadCollectionArchiveAsync(string downloadLinksJson, string accessToken, string collectionSlug, int revisionNumber)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(downloadLinksJson);
            var downloadLinksElement = doc.RootElement.GetProperty("download_links");

            if (downloadLinksElement.GetArrayLength() == 0)
            {
                Log.Warn("[ModDetailsViewModel] download_links 数组为空");
                return null;
            }

            var firstLink = downloadLinksElement[0];
            var downloadUrl = firstLink.GetProperty("URI").GetString();

            if (string.IsNullOrEmpty(downloadUrl))
            {
                Log.Warn("[ModDetailsViewModel] download_links 中没有有效的 URI");
                return null;
            }

            Log.Info($"[ModDetailsViewModel] 下载 Collection 压缩包: {downloadUrl}");

            var uri = new Uri(downloadUrl);
            var fileName = System.IO.Path.GetFileName(uri.LocalPath);
            if (string.IsNullOrEmpty(fileName) || !fileName.EndsWith(".7z"))
            {
                fileName = $"collection_{collectionSlug}_r{revisionNumber}.7z";
            }

            var savePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SVL", "collections", fileName);
            var directory = System.IO.Path.GetDirectoryName(savePath);

            if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            using (var client = new System.Net.Http.HttpClient())
            {
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
                client.DefaultRequestHeaders.Add("User-Agent", "SVL-StardewLauncher/1.0");
                client.Timeout = TimeSpan.FromMinutes(30);

                var response = await client.GetAsync(downloadUrl);
                if (!response.IsSuccessStatusCode)
                {
                    Log.Warn($"[ModDetailsViewModel] 下载 Collection 压缩包失败: {response.StatusCode}");
                    return null;
                }

                var zipData = await response.Content.ReadAsByteArrayAsync();
                System.IO.File.WriteAllBytes(savePath, zipData);

                Log.Info($"[ModDetailsViewModel] Collection 压缩包下载成功: {savePath}, 大小: {zipData.Length} 字节");
            }

            return savePath;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[ModDetailsViewModel] 下载 Collection 压缩包异常");
            return null;
        }
    }

    /// <summary>
    /// 整合包安装配置（游戏路径和实例名称）
    /// </summary>
    private class ModpackInstallConfig
    {
        public string GameBasePath { get; set; } = string.Empty;
        public string InstanceName { get; set; } = string.Empty;
        public string TargetModsPath { get; set; } = string.Empty;
    }

    /// <summary>
    /// 通用的整合包安装流程配置（获取游戏路径、确认对话框、输入实例名称）
    /// </summary>
    /// <param name="owner">父窗口</param>
    /// <param name="defaultInstanceName">默认实例名称</param>
    /// <returns>安装配置，如果用户取消则返回 null</returns>
    private async Task<ModpackInstallConfig?> GetModpackInstallConfigAsync(System.Windows.Window owner, string defaultInstanceName)
    {
        // 1. 获取当前游戏实例（确定默认的 BASE 地址）
        var currentInstance = GetCurrentSelectedInstance();
        if (currentInstance == null)
        {
            Log.Warn("[ModDetailsViewModel] 未找到选中的游戏实例");
            SvlMessageBox.Warning(
                "未找到选中的游戏实例，请先在启动页面选择一个游戏实例。",
                "无法安装");
            return null;
        }

        var gameBasePath = currentInstance.Path;
        Log.Info($"[ModDetailsViewModel] 默认游戏基础路径: {gameBasePath}");

        // 2. 显示 BASE 路径确认对话框
        string confirmedGameBasePath = null;
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var basePathConfirmDialog = new SVL.Desktop.Controls.GamePathConfirmDialog();
            basePathConfirmDialog.SetGamePath(gameBasePath);
            basePathConfirmDialog.Owner = System.Windows.Application.Current.MainWindow;

            var basePathResult = basePathConfirmDialog.ShowDialog();
            if (basePathResult == true)
            {
                confirmedGameBasePath = basePathConfirmDialog.GetSelectedPath() ?? gameBasePath;
                Log.Info($"[ModDetailsViewModel] 用户确认游戏路径: {confirmedGameBasePath}");
            }
        });

        if (string.IsNullOrEmpty(confirmedGameBasePath))
        {
            Log.Info("[ModDetailsViewModel] 用户取消了游戏路径确认");
            return null;
        }

        gameBasePath = confirmedGameBasePath;

        // 3. 处理默认实例名称
        defaultInstanceName = defaultInstanceName.Replace("[整合包]", "").Replace("整合包", "").Trim();
        if (string.IsNullOrEmpty(defaultInstanceName))
        {
            defaultInstanceName = Mod.Name;
        }

        // 4. 提示用户输入实例名称（带版本名重复验证）
        string instanceName = null;

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            // 复用 Curseforge 整合包的版本名称验证逻辑
            instanceName = SVL.Desktop.Controls.InputDialog.Show(
                owner,
                "请输入新实例的名称：",
                defaultInstanceName,
                (name) => InstanceIsolationService.ValidateInstanceName(name)
            ) ?? "";
        });

        if (string.IsNullOrEmpty(instanceName))
        {
            Log.Info("[ModDetailsViewModel] 用户取消了实例名称输入");
            return null;
        }

        Log.Info($"[ModDetailsViewModel] 用户输入的实例名称: {instanceName}");

        // 5. 计算默认的 Mods 路径（默认开启版本隔离）
        string defaultModsPath = InstanceIsolationService.GetIsolatedModsPath(gameBasePath, instanceName);
        Log.Info($"[ModDetailsViewModel] 使用版本隔离，Mods 路径: {defaultModsPath}");

        return new ModpackInstallConfig
        {
            GameBasePath = gameBasePath,
            InstanceName = instanceName,
            TargetModsPath = defaultModsPath
        };
    }

    /// <summary>
    /// 选择 Collection 7z 文件
    /// </summary>
    private async Task<string?> SelectCollectionFileAsync(System.Windows.Window owner)
    {
        return await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new System.Windows.Forms.OpenFileDialog
            {
                Title = "选择 Nexus Collection 7z 文件",
                Filter = "7z 文件 (*.7z)|*.7z|所有文件 (*.*)|*.*",
                RestoreDirectory = true
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                return dialog.FileName;
            }

            return null;
        });
    }

    /// <summary>
    /// 另存为指定版本的 MOD（只保存 ZIP 文件，不安装）
    /// </summary>
    private async Task SaveModAsAsync(ModVersionItem version)
    {
        Log.Info($"[ModDetailsViewModel] SaveModAsAsync 被调用，version: {version?.FileName}");

        try
        {
            if (version == null)
            {
                Log.Warn("[ModDetailsViewModel] 版本信息为空");
                return;
            }

            // Nexus Collection：导出 Collection 元数据和 Mod 列表
            if (IsNexusCollection(version))
            {
                await SaveNexusCollectionAsAsync(version);
                return;
            }

            // 1. 弹出另存为对话框
            var savePath = await ShowSimpleSaveAsDialog(version.FileName);
            if (savePath == null)
            {
                Log.Info("[ModDetailsViewModel] 用户取消了另存为");
                return;
            }

            Log.Info($"[ModDetailsViewModel] 另存为路径: {savePath}");

            if (IsModpackMode && Mod.Source == "Curseforge")
            {
                if (!TryParseCurseforgeModpackId(out var modId))
                {
                    Log.Warn($"[ModDetailsViewModel] 无法解析 Curseforge Modpack ID: {Mod.Id}");
                    SvlMessageBox.Warning(
                        "无法解析 Curseforge 整合包 ID，请稍后重试。",
                        "另存为错误");
                    return;
                }

                var resolvedDownloadUrl = await ResolveCurseforgeModpackDownloadUrlAsync(version, modId);
                if (string.IsNullOrWhiteSpace(resolvedDownloadUrl))
                {
                    SvlMessageBox.Warning(
                        "无法获取整合包下载地址，请尝试从网页手动下载。",
                        "另存为错误");
                    return;
                }

                var sourceFileId = TryParseCurseforgeFileId(version, out var fileId) ? fileId.ToString() : version.FileId;

                var modpackSaveTask = new ModDownloadTask(
                    modId: Mod.Id,
                    modName: Mod.Name,
                    fileName: version.FileName,
                    downloadUrl: resolvedDownloadUrl,
                    targetModsPath: savePath,
                    saveOnly: true,
                    sourcePlatform: "Curseforge",
                    sourceProjectId: modId.ToString(),
                    sourceFileId: sourceFileId,
                    isModpack: true,  // 标记为整合包
                    modpackIconUrl: Mod.IconUrl,
                    modpackIconLocalPath: Mod.LocalIconPath
                );

                await DownloadManager.Instance.AddTaskAsync(modpackSaveTask);
                Log.Info($"[ModDetailsViewModel] 已添加 Curseforge 整合包另存为任务: {Mod.Name}");
                return;
            }

            // NexusMods：复用统一下载工作流，先下载到本地缓存/临时目录，再用本地文件任务执行“另存为”
            if (Mod.Source == "NexusMods")
            {
                var nexusModId = await EnsureNexusModIdAsync();
                if (nexusModId <= 0)
                {
                    SvlMessageBox.Error(
                        "无法解析 NexusMods Mod ID，请稍后重试。",
                        "另存为错误");
                    return;
                }

                var fileIdMatch = Regex.Match(version.FileId ?? string.Empty, @"(\d+)$");
                if (!fileIdMatch.Success || !long.TryParse(fileIdMatch.Groups[1].Value, out var nexusFileId) || nexusFileId <= 0)
                {
                    SvlMessageBox.Error(
                        "无法解析 NexusMods 文件 ID，请稍后重试。",
                        "另存为错误");
                    return;
                }

                var tempDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SVL",
                    "temp",
                    "mods"
                );

                string zipPath;
                try
                {
                    zipPath = await SVL.Core.Download.NexusMods.NexusDownloadWorkflow.DownloadZipAsync(
                        gameId: "stardewvalley",
                        modId: nexusModId,
                        fileId: nexusFileId,
                        workingDirectory: tempDir,
                        progressCallback: _ => { },
                        useCache: true);
                }
                catch (SVL.Core.Download.NexusMods.NexusPremiumRequiredException premiumEx)
                {
                    Log.Warn($"[ModDetailsViewModel] NexusMods 另存为：非 Premium，切换浏览器引导流程: modId={nexusModId}, fileId={nexusFileId}");

                    await DownloadRightViewModel.RegisterPendingNexusModDownloadAsync(
                        modId: nexusModId,
                        fileId: nexusFileId,
                        modName: Mod.Name,
                        fileName: version.FileName,
                        targetModsPath: savePath,
                        saveOnly: true,
                        gameBasePath: null);

                    await ShowNexusBrowserGuideAsync(nexusModId, nexusFileId, premiumEx.DownloadPageUrl);
                    return;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[ModDetailsViewModel] NexusMods 另存为下载失败");
                    SvlMessageBox.Error(
                        $"NexusMods 下载失败：{ex.Message}",
                        "另存为错误");
                    return;
                }

                var saveTask = new ModDownloadTask(
                    modId: Mod.Id,
                    modName: Mod.Name,
                    fileName: version.FileName,
                    localZipPath: zipPath,
                    isLocalFile: true,
                    gameBasePath: null,
                    targetModsPath: savePath,
                    saveOnly: true,
                    sourcePlatform: "NexusMods",
                    sourceProjectId: nexusModId.ToString(),
                    sourceFileId: nexusFileId.ToString()
                );

                await DownloadManager.Instance.AddTaskAsync(saveTask);
                Log.Info($"[ModDetailsViewModel] 已添加 Nexus 另存为任务: {Mod.Name}");
                return;
            }

            // 2. 创建并添加下载任务（只保存模式）
            if (string.IsNullOrWhiteSpace(version.DownloadUrl))
            {
                Log.Warn("[ModDetailsViewModel] 下载链接为空，无法另存为");
                SvlMessageBox.Warning(
                    "下载链接为空，无法另存为。",
                    "另存为错误");
                return;
            }

            var downloadTask = new ModDownloadTask(
                modId: Mod.Id,
                modName: Mod.Name,
                fileName: version.FileName,
                downloadUrl: version.DownloadUrl,
                targetModsPath: savePath,
                saveOnly: true,
                sourcePlatform: Mod.Source,
                sourceProjectId: Mod.Source == "Curseforge" && TryParseCurseforgeModpackId(out var curseforgeModId) ? curseforgeModId.ToString() : null,
                sourceFileId: version.FileId
            );

            await DownloadManager.Instance.AddTaskAsync(downloadTask);

            Log.Info($"[ModDetailsViewModel] 已添加另存为任务: {Mod.Name}");
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "[ModDetailsViewModel] 另存为 MOD 失败");
            SvlMessageBox.Error(
                $"另存为失败: {ex.Message}",
                "另存为错误");
        }
    }

    /// <summary>
    /// 安装 Mod（用于 Nexus Collection）
    /// </summary>
    private async Task InstallModAsync(ModVersionItem version)
    {
        Log.Info($"[ModDetailsViewModel] InstallModAsync 被调用，version: {version?.FileId}");

        try
        {
            if (version == null)
            {
                Log.Warn("[ModDetailsViewModel] 版本信息为空");
                return;
            }

            // 检查是否是 Nexus Collection 类型
            if (!IsNexusCollection(version))
            {
                Log.Warn("[ModDetailsViewModel] 此版本不是 Nexus Collection，不支持直接安装");
                SvlMessageBox.Info(
                    "此功能仅支持 Nexus Collection（.7z 文件）的安装。\n\n对于普通 Mod，请使用「下载」按钮。",
                    "安装功能");
                return;
            }

            // 启动 Nexus Collection 安装流程
            if (System.Windows.Application.Current.MainWindow is not System.Windows.Window owner)
            {
                Log.Warn("[ModDetailsViewModel] 找不到主窗口，无法打开安装对话框");
                return;
            }

            await StartNexusCollectionInstallAsync(owner, version);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[ModDetailsViewModel] Collection 安装启动失败");
            SvlMessageBox.Error(
                $"启动 Collection 安装失败：{ex.Message}",
                "安装错误");
        }
    }

    /// <summary>
    /// 检查 MOD 是否已存在，返回已安装的版本号
    /// </summary>
    private string CheckModExists(string modsPath, string modName)
    {
        try
        {
            // 首先尝试通过文件夹名匹配（ZIP 通常会解压到同名文件夹）
            // 从 modName 中移除特殊字符，尝试匹配文件夹
            var cleanModName = modName;
            if (cleanModName.Contains(" "))
            {
                // 如果 MOD 名称有空格，尝试查找包含名称的文件夹
                var matchingDirs = System.IO.Directory.GetDirectories(modsPath)
                    .Where(d =>
                    {
                        var dirName = System.IO.Path.GetFileName(d);
                        return dirName.Contains(modName.Split(' ')[0]) || // 简化匹配
                               modName.Contains(dirName);
                    })
                    .ToArray();

                if (matchingDirs.Length > 0)
                {
                    // 找到匹配的文件夹，尝试读取 manifest.json
                    foreach (var dir in matchingDirs)
                    {
                        var manifestPath = System.IO.Path.Combine(dir, "manifest.json");
                        if (System.IO.File.Exists(manifestPath))
                        {
                            var manifestJson = System.IO.File.ReadAllText(manifestPath);
                            var manifest = System.Text.Json.JsonDocument.Parse(manifestJson);
                            if (manifest.RootElement.TryGetProperty("Version", out var versionProp))
                            {
                                return versionProp.GetString();
                            }
                        }
                    }
                }
            }

            // 如果没找到，遍历所有 manifest.json 文件
            if (!System.IO.Directory.Exists(modsPath))
            {
                return null;
            }

            foreach (var modDir in System.IO.Directory.GetDirectories(modsPath))
            {
                var manifestPath = System.IO.Path.Combine(modDir, "manifest.json");
                if (System.IO.File.Exists(manifestPath))
                {
                    try
                    {
                        var manifestJson = System.IO.File.ReadAllText(manifestPath);
                        var manifest = System.Text.Json.JsonDocument.Parse(manifestJson);

                        // 通过 Name 字段匹配
                        if (manifest.RootElement.TryGetProperty("Name", out var nameProp))
                        {
                            if (nameProp.GetString()?.Equals(modName, System.StringComparison.OrdinalIgnoreCase) == true)
                            {
                                if (manifest.RootElement.TryGetProperty("Version", out var versionProp))
                                {
                                    return versionProp.GetString();
                                }
                            }
                        }
                    }
                    catch
                    {
                        // 忽略解析失败的 manifest.json
                        continue;
                    }
                }
            }

            return null;
        }
        catch (System.Exception ex)
        {
            Log.Warn($"[ModDetailsViewModel] 检查 MOD 是否存在时出错: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 比较两个版本号，返回 1（v1 > v2）、0（v1 == v2）或 -1（v1 < v2）
    /// </summary>
    private int CompareVersions(string v1, string v2)
    {
        try
        {
            var parts1 = v1.Split('.').Select(int.Parse).ToArray();
            var parts2 = v2.Split('.').Select(int.Parse).ToArray();

            int maxLength = Math.Max(parts1.Length, parts2.Length);
            for (int i = 0; i < maxLength; i++)
            {
                int p1 = i < parts1.Length ? parts1[i] : 0;
                int p2 = i < parts2.Length ? parts2[i] : 0;

                if (p1 > p2) return 1;
                if (p1 < p2) return -1;
            }

            return 0;
        }
        catch
        {
            // 如果版本号解析失败，按字符串比较
            return string.Compare(v1, v2, System.StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 显示另存为对话框（选择保存位置，然后自动下载并解压 MOD）
    /// </summary>
    private async System.Threading.Tasks.Task<string> ShowSaveAsDialog(string defaultPath, string fileName)
    {
        return await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            // 确保文件名以 .zip 结尾
            var zipFileName = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? fileName
                : fileName + ".zip";

            // 清理文件名中的非法字符
            var invalidChars = System.IO.Path.GetInvalidFileNameChars();
            var safeFileName = string.Join("_", zipFileName.Split(invalidChars));

            // 使用系统原生的"另存为"文件对话框选择保存位置
            // 用户选择 ZIP 文件的保存路径，下载后会自动解压到同名文件夹
            using (var dialog = new System.Windows.Forms.SaveFileDialog
            {
                FileName = safeFileName,
                InitialDirectory = defaultPath,
                DefaultExt = ".zip",
                Filter = "ZIP 压缩文件 (*.zip)|*.zip",
                Title = "保存 MOD 文件（将自动解压到同名文件夹）",
                RestoreDirectory = true
            })
            {
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    // 返回 ZIP 文件路径，ModDownloadTask 会检测到这是 .zip 文件
                    // 下载后会保存 ZIP 文件并自动解压到同名文件夹
                    return dialog.FileName;
                }

                return null;
            }
        });
    }

    /// <summary>
    /// 显示简单的另存为对话框（只保存 ZIP 文件，不安装）
    /// </summary>
    private async System.Threading.Tasks.Task<string> ShowSimpleSaveAsDialog(string fileName)
    {
        return await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            // 先清理文件名中的非法字符
            var invalidChars = System.IO.Path.GetInvalidFileNameChars();
            var safeFileName = string.Join("_", fileName.Split(invalidChars));

            // 获取扩展名（从清理后的文件名）
            var ext = System.IO.Path.GetExtension(safeFileName);
            var normalizedFileName = string.IsNullOrWhiteSpace(ext)
                ? safeFileName + ".zip"
                : safeFileName;

            var defaultExt = string.IsNullOrWhiteSpace(ext) ? ".zip" : ext;

            // 根据扩展名设置过滤器
            string filter;
            if (string.Equals(defaultExt, ".cfmodpack", StringComparison.OrdinalIgnoreCase))
            {
                filter = "Curseforge 整合包 (*.cfmodpack)|*.cfmodpack|所有文件 (*.*)|*.*";
            }
            else if (string.Equals(defaultExt, ".7z", StringComparison.OrdinalIgnoreCase))
            {
                filter = "7z 压缩文件 (*.7z)|*.7z|所有文件 (*.*)|*.*";
            }
            else
            {
                filter = "ZIP 压缩文件 (*.zip)|*.zip|所有文件 (*.*)|*.*";
            }

            // 使用系统原生的"另存为"文件对话框
            using (var dialog = new System.Windows.Forms.SaveFileDialog
            {
                FileName = normalizedFileName,
                DefaultExt = defaultExt,
                Filter = filter,
                Title = "另存为 MOD 文件",
                RestoreDirectory = true
            })
            {
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    return dialog.FileName;
                }

                return null;
            }
        });
    }

    private bool TryParseCurseforgeFileId(ModVersionItem version, out int fileId)
    {
        fileId = 0;
        if (version == null)
            return false;

        if (!string.IsNullOrWhiteSpace(version.FileId))
        {
            var match = Regex.Match(version.FileId, @"(\d+)$");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var parsed) && parsed > 0)
            {
                fileId = parsed;
                return true;
            }
        }

        return false;
    }

    private async Task<string> ResolveCurseforgeModpackDownloadUrlAsync(ModVersionItem version, int modId)
    {
        var fallbackUrl = version?.DownloadUrl ?? string.Empty;

        if (TryParseCurseforgeFileId(version, out var fileId))
        {
            var resolved = await CurseforgeApiService.ResolveFileDownloadUrlAsync(
                modId,
                fileId,
                version.FileName,
                fallbackUrl);

            if (!string.IsNullOrWhiteSpace(resolved))
                return resolved;
        }

        return fallbackUrl;
    }

    private async Task ShowNexusBrowserGuideAsync(long modId, long fileId, string downloadPageUrl)
    {
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var guideDialog = new SVL.Desktop.Controls.BrowserDownloadGuideDialog(
                modId,
                fileId,
                "stardewvalley"
            );
            guideDialog.Owner = System.Windows.Application.Current.MainWindow;
            guideDialog.ShowWithBlur(System.Windows.Application.Current.MainWindow);
        });

        Log.Info($"[ModDetailsViewModel] 已显示浏览器下载引导: {downloadPageUrl}");
    }

    /// <summary>
    /// 判断是否是 SMAPI 模组
    /// </summary>
    private bool IsSmapApiMod()
    {
        // 通过 MOD 名称或 ID 判断
        var modNameLower = Mod.Name?.ToLower() ?? "";
        var modIdLower = Mod.Id?.ToLower() ?? "";

        return modNameLower.Contains("smapi") ||
               modIdLower.Contains("smapi") ||
               modNameLower.Contains("stardew modding api") ||
               modIdLower.Contains("2400"); // NexusMods SMAPI ID
    }

    /// <summary>
    /// 下载 SMAPI
    /// </summary>
    private async Task DownloadSmapApiAsync(ModVersionItem version)
    {
        try
        {
            // 获取当前选中的游戏实例
            var currentInstance = GetCurrentSelectedInstance();
            if (currentInstance == null)
            {
                Log.Warn("[ModDetailsViewModel] 未找到选中的游戏实例，无法安装 SMAPI");
                SvlMessageBox.Warning(
                    "未找到选中的游戏实例，请先在启动页面选择一个游戏实例。",
                    "无法安装 SMAPI");
                return;
            }

            // 获取游戏基础路径
            var gameBasePath = currentInstance.Path;
            Log.Info($"[ModDetailsViewModel] 默认游戏基础路径：{gameBasePath}");

            // 显示 BASE 路径确认对话框
            string confirmedGameBasePath = null;
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var basePathConfirmDialog = new SVL.Desktop.Controls.GamePathConfirmDialog();
                basePathConfirmDialog.SetGamePath(gameBasePath);
                basePathConfirmDialog.Owner = System.Windows.Application.Current.MainWindow;

                var basePathResult = basePathConfirmDialog.ShowDialog();
                if (basePathResult == true)
                {
                    confirmedGameBasePath = basePathConfirmDialog.GetSelectedPath() ?? gameBasePath;
                    Log.Info($"[ModDetailsViewModel] 用户确认游戏路径：{confirmedGameBasePath}");
                }
            });

            if (string.IsNullOrEmpty(confirmedGameBasePath))
            {
                Log.Info("[ModDetailsViewModel] 用户取消了游戏路径确认");
                return;
            }

            gameBasePath = confirmedGameBasePath;

            
            // 提示用户输入实例名称
            string smapiVersion = version.Version;
            // 确保 smapiVersion 不会重复 "SMAPI " 前缀
            string instanceDefaultName = smapiVersion;
            if (!instanceDefaultName.StartsWith("SMAPI", StringComparison.OrdinalIgnoreCase))
            {
                instanceDefaultName = $"SMAPI {smapiVersion}";
            }
            string instanceName = null;

            // 在 UI 线程上显示实例名称对话框（复用 Curseforge 整合包的验证逻辑）
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var mainWindow = System.Windows.Application.Current.MainWindow;

                // 使用 InstanceNameDialog，自动包含非法字符检测和版本名重复验证
                instanceName = SVL.Desktop.Controls.InstanceNameDialog.Show(
                    owner: mainWindow,
                    defaultName: instanceDefaultName,
                    checkNameExists: (name) =>
                    {
                        // 使用完整的验证逻辑：检查实例列表 + 文件系统版本目录
                        var (isValid, errorMessage) = InstanceIsolationService.ValidateInstanceName(name, gameBasePath);
                        return !isValid; // 如果无效（名称已存在），返回 true
                    },
                    autoSanitize: true);
            });

            if (string.IsNullOrEmpty(instanceName))
            {
                Log.Info("[ModDetailsViewModel] 用户取消了 SMAPI 安装");
                return;
            }

            Log.Info($"[ModDetailsViewModel] 用户输入的实例名称: {instanceName}");

            // 判断下载来源
            SmapiSource source = SmapiSource.GitHub;
            long? fileId = null;
            string downloadUrl = version.DownloadUrl;

            if (string.Equals(Mod.Source, "NexusMods", StringComparison.OrdinalIgnoreCase))
            {
                source = SmapiSource.NexusMods;

                var fileIdMatch = Regex.Match(version.FileId ?? string.Empty, @"(\d+)$");
                if (fileIdMatch.Success && long.TryParse(fileIdMatch.Groups[1].Value, out var nexusFileId) && nexusFileId > 0)
                {
                    fileId = nexusFileId;
                    Log.Info($"[ModDetailsViewModel] NexusMods 文件 ID: {nexusFileId}");
                }
                else
                {
                    Log.Warn("[ModDetailsViewModel] 未解析到有效 NexusMods 文件 ID，将回退到 GitHub 来源");
                    source = SmapiSource.GitHub;
                }
            }
            else if (Mod.Source == "Curseforge")
            {
                source = SmapiSource.Curseforge;
                // 从 FileId 中提取 Curseforge 文件 ID
                if (version.FileId.StartsWith("curse-file-"))
                {
                    var fileIdStr = version.FileId.Substring(11); // 去掉 "curse-file-" 前缀
                    if (long.TryParse(fileIdStr, out long curseFileId))
                    {
                        fileId = curseFileId;
                        Log.Info($"[ModDetailsViewModel] Curseforge 文件 ID: {curseFileId}");
                    }
                }
            }

            if (source == SmapiSource.NexusMods && fileId.HasValue)
            {
                var tempDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SVL",
                    "temp"
                );

                try
                {
                    var zipPath = await SVL.Core.Download.NexusMods.NexusDownloadWorkflow.DownloadZipAsync(
                        gameId: "stardewvalley",
                        modId: 2400,
                        fileId: fileId.Value,
                        workingDirectory: tempDir,
                        progressCallback: _ => { },
                        useCache: true);

                    var localTask = new SmapiDownloadTask(
                        gameBasePath,
                        instanceName,
                        zipPath,
                        SmapiSource.NexusMods,
                        debugMode: false,
                        version: smapiVersion
                    );

                    await DownloadManager.Instance.AddTaskAsync(localTask);
                    Log.Info($"[ModDetailsViewModel] 已添加 NexusMods 本地安装任务: {instanceName}");
                    return;
                }
                catch (SVL.Core.Download.NexusMods.NexusPremiumRequiredException premiumEx)
                {
                    await DownloadRightViewModel.RegisterPendingNexusSmapiDownloadAsync(
                        modId: 2400,
                        fileId: fileId.Value,
                        gameBasePath: gameBasePath,
                        instanceName: instanceName,
                        debugMode: false,
                        version: smapiVersion);

                    await ShowNexusBrowserGuideAsync(2400, fileId.Value, premiumEx.DownloadPageUrl);
                    return;
                }
            }

            // 创建 SMAPI 下载任务
            var smapiTask = new SmapiDownloadTask(
                gameBasePath: gameBasePath,
                instanceName: instanceName,
                smapiVersion: smapiVersion,
                source: source,
                fileId: fileId,
                downloadUrl: downloadUrl
            );

            await DownloadManager.Instance.AddTaskAsync(smapiTask);

            Log.Info($"[ModDetailsViewModel] 已添加 SMAPI 下载任务: {instanceName}");
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "[ModDetailsViewModel] 下载 SMAPI 失败");
            SvlMessageBox.Error(
                $"下载 SMAPI 失败: {ex.Message}",
                "下载错误");
        }
    }

    /// <summary>
    /// 获取当前选中的游戏实例（从 SettingsService 加载）
    /// </summary>
    private IStardewInstance GetCurrentSelectedInstance()
    {
        try
        {
            var selectedPath = _mainViewModel?.GetActiveVersionSettingsInstance(requireModManage: false);

            if (selectedPath == null)
            {
                // 从配置服务加载已保存的实例（与 LaunchLeftViewModel 相同的逻辑）
                var savedInstances = SettingsService.LoadInstances();
                var defaultInstanceId = SettingsService.LoadDefaultInstanceId();

                if (savedInstances.Count > 0)
                {
                    if (!string.IsNullOrEmpty(defaultInstanceId))
                    {
                        selectedPath = savedInstances.FirstOrDefault(i => i.Id == defaultInstanceId);
                    }

                    selectedPath ??= savedInstances[0];
                }
            }

            if (selectedPath != null)
            {
                Log.Info($"[ModDetailsViewModel] 找到选中的实例: {selectedPath.Name}, 路径: {selectedPath.GamePath}, 版本: {selectedPath.Version}");
                Log.Info($"[ModDetailsViewModel] 实例配置 - SMAPI: {selectedPath.IsSMAPIInstance}, 版本隔离: {selectedPath.EnableIsolation}");

                return new GamePathInfoAdapter(selectedPath);
            }

            Log.Warn("[ModDetailsViewModel] 未找到选中的游戏实例");
            return null;
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "[ModDetailsViewModel] 获取当前选中实例失败");
            return null;
        }
    }


}
