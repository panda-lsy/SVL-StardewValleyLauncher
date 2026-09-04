using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SVL.Avalonia.Services;
using SVL.Core.Platform.Abstractions;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;

namespace SVL.Avalonia.ViewModels;

public sealed class PathEntryItem
{
    public string DisplayName { get; set; } = string.Empty;

    public string GamePath { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public ObservableCollection<InstanceItem> Instances { get; } = [];
}

public sealed partial class InstanceItem : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _path = string.Empty;

    [ObservableProperty]
    private string _source = string.Empty;

    [ObservableProperty]
    private string _iconSource = string.Empty;

    [ObservableProperty]
    private string _version = string.Empty;

    [ObservableProperty]
    private bool _isSmapiInstance;

    public bool IsVanillaInstance => !IsSmapiInstance;

    [ObservableProperty]
    private bool _hasSmapiInstalled;

    [ObservableProperty]
    private string _smapiVersion = string.Empty;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isFavorite;

    [ObservableProperty]
    private bool _isBaseInstance;

    public ObservableCollection<string> Tags { get; } = [];

    public string DisplayVersion => string.IsNullOrWhiteSpace(Version)
        ? "未知版本"
        : FormatVersion(Version);

    public string FavoriteGlyph => IsFavorite ? "★" : "☆";

    private static string FormatVersion(string version)
    {
        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? string.Join('.', parts.Take(3)) : version;
    }

    partial void OnIsSmapiInstanceChanged(bool value)
    {
        OnPropertyChanged(nameof(IsVanillaInstance));
    }

    partial void OnIsFavoriteChanged(bool value)
    {
        OnPropertyChanged(nameof(FavoriteGlyph));
    }
}

public partial class InstancesPageViewModel : ObservableObject
{
    private readonly IGameInstallPathLocator _gameInstallPathLocator;
    private readonly DialogService _dialogService;
    private readonly InstanceRegistryStore _instanceRegistryStore;
    private readonly AppUserSettingsStore _settingsStore;
    private readonly ImageResourceService _imageResourceService;
    private readonly LocalizationService _localizationService;

    public event Action<InstanceItem>? InstanceActivated;

    public event Action<InstanceItem>? InstanceSettingsRequested;

    public event Action? ModpackImportRequested;

    [ObservableProperty]
    private string _status = "就绪";

    [ObservableProperty]
    private PathEntryItem? _selectedPathEntry;

    [ObservableProperty]
    private InstanceItem? _selectedInstance;

    [ObservableProperty]
    private bool _hasPathEntries;

    [ObservableProperty]
    private string _pageTitle = "版本选择 / 实例管理";

    [ObservableProperty]
    private string _pathListTitle = "路径列表";

    [ObservableProperty]
    private string _pathActionsTitle = "路径操作";

    [ObservableProperty]
    private string _chooseVersionTitle = "选择启动版本";

    [ObservableProperty]
    private string _refreshButtonText = "刷新";

    [ObservableProperty]
    private string _addManualButtonText = "添加";

    [ObservableProperty]
    private string _importModpackButtonText = "导入整合包";

    [ObservableProperty]
    private string _openFolderButtonText = "打开文件夹";

    [ObservableProperty]
    private string _emptyHintPrimaryText = "请从左侧选择一个游戏路径";

    [ObservableProperty]
    private string _emptyHintSecondaryText = "点击路径后，右侧将显示该路径下的所有可用版本";

    public bool HasSelectedPath => SelectedPathEntry != null;

    public bool HasNoSelectedPath => !HasSelectedPath;

    public bool HasSelectedInstance => SelectedInstance != null;

    public bool HasNoPathEntries => !HasPathEntries;

    public bool CanOpenSelectedPathFolder =>
        SelectedPathEntry != null &&
        !string.IsNullOrWhiteSpace(SelectedPathEntry.GamePath) &&
        Directory.Exists(SelectedPathEntry.GamePath);

    public ObservableCollection<PathEntryItem> PathEntries { get; } = [];

    public ObservableCollection<InstanceItem> SelectedPathInstances { get; } = [];

    public ObservableCollection<InstanceItem> BaseInstances { get; } = [];

    public ObservableCollection<InstanceItem> VersionInstances { get; } = [];

    public bool HasBaseInstances => BaseInstances.Count > 0;

    public bool HasVersionInstances => VersionInstances.Count > 0;

    public InstancesPageViewModel(
        IGameInstallPathLocator gameInstallPathLocator,
        DialogService dialogService,
        InstanceRegistryStore instanceRegistryStore,
        AppUserSettingsStore settingsStore,
        ImageResourceService imageResourceService,
        LocalizationService localizationService)
    {
        _gameInstallPathLocator = gameInstallPathLocator;
        _dialogService = dialogService;
        _instanceRegistryStore = instanceRegistryStore;
        _settingsStore = settingsStore;
        _imageResourceService = imageResourceService;
        _localizationService = localizationService;

        _imageResourceService.ResourcesChanged += Reload;
        _localizationService.LanguageChanged += HandleLanguageChanged;

        ApplyLocalizedTexts();
        Reload();
    }

    partial void OnSelectedPathEntryChanged(PathEntryItem? value)
    {
        SelectedPathInstances.Clear();
        BaseInstances.Clear();
        VersionInstances.Clear();
        SelectedInstance = null;

        if (value != null)
        {
            foreach (var instance in value.Instances)
            {
                instance.IsSelected = false;
                SelectedPathInstances.Add(instance);
            }

            RebuildInstanceGroups();

            Status = string.Format(
                L("Instances.Status.PathSelected", "已选择路径：{0}，共 {1} 个版本实例"),
                value.DisplayName,
                value.Instances.Count);
        }

        OnPropertyChanged(nameof(HasSelectedPath));
        OnPropertyChanged(nameof(HasNoSelectedPath));
        OnPropertyChanged(nameof(CanOpenSelectedPathFolder));
        OnPropertyChanged(nameof(HasSelectedInstance));
        OnPropertyChanged(nameof(HasBaseInstances));
        OnPropertyChanged(nameof(HasVersionInstances));
    }

    partial void OnSelectedInstanceChanged(InstanceItem? value)
    {
        foreach (var item in SelectedPathInstances)
        {
            item.IsSelected = ReferenceEquals(item, value);
        }

        OnPropertyChanged(nameof(HasSelectedInstance));
    }

    [RelayCommand]
    private void SelectPathEntry(PathEntryItem? entry)
    {
        if (entry == null)
        {
            return;
        }

        SelectedPathEntry = entry;
    }

    [RelayCommand]
    private void Reload()
    {
        var previousPath = SelectedPathEntry?.GamePath;
        var previousInstanceName = SelectedInstance?.Name;
        var previousIsSmapi = SelectedInstance?.IsSmapiInstance ?? false;
        var settings = _settingsStore.Load();
        var favoriteKeys = BuildFavoriteKeySet(settings);

        PathEntries.Clear();
        SelectedPathInstances.Clear();
        SelectedPathEntry = null;
        SelectedInstance = null;

        var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in DiscoverAutoPathCandidates())
        {
            AddPathEntryIfValid(candidate.Path, candidate.Source, null, addedPaths, favoriteKeys);
        }

        var manualInstances = _instanceRegistryStore.LoadManualInstances();
        foreach (var manual in manualInstances)
        {
            if (string.IsNullOrWhiteSpace(manual.Path))
            {
                continue;
            }

            AddPathEntryIfValid(
                manual.Path,
                L("Instances.Source.Local", "本地路径"),
                manual.Name,
                addedPaths,
                favoriteKeys);
        }

        HasPathEntries = PathEntries.Count > 0;

        var preferredPath = settings.PreferredInstancePath;

        var restorePath = PathEntries.FirstOrDefault(item =>
            !string.IsNullOrWhiteSpace(previousPath) &&
            string.Equals(item.GamePath, previousPath, StringComparison.OrdinalIgnoreCase))
            ?? PathEntries.FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(preferredPath) &&
                string.Equals(item.GamePath, preferredPath, StringComparison.OrdinalIgnoreCase))
            ?? PathEntries.FirstOrDefault();

        if (restorePath != null)
        {
            SelectedPathEntry = restorePath;

            var restoreInstance = SelectedPathInstances.FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(previousInstanceName) &&
                string.Equals(item.Name, previousInstanceName, StringComparison.OrdinalIgnoreCase) &&
                item.IsSmapiInstance == previousIsSmapi)
                ?? SelectedPathInstances.FirstOrDefault(item =>
                    !string.IsNullOrWhiteSpace(settings.InstanceName) &&
                    string.Equals(item.Name, settings.InstanceName, StringComparison.OrdinalIgnoreCase))
                ?? SelectedPathInstances.FirstOrDefault();

            if (restoreInstance != null)
            {
                SelectedInstance = restoreInstance;
            }
        }

        Status = PathEntries.Count == 0
            ? L("Instances.Status.Empty", "未发现本机实例，可手动添加")
            : string.Format(L("Instances.Status.Detected", "已发现 {0} 个路径，{1} 个版本实例"),
                PathEntries.Count,
                PathEntries.Sum(path => path.Instances.Count));

        OnPropertyChanged(nameof(HasPathEntries));
        OnPropertyChanged(nameof(HasNoPathEntries));
    }

    public void RefreshFromSettingsChange()
    {
        Reload();
    }

    [RelayCommand]
    private void SelectInstance(InstanceItem? item)
    {
        if (item == null)
        {
            return;
        }

        SelectedInstance = item;
        PersistPreferredInstance(item);
        Status = string.Format(
            L("Instances.Status.Selected", "已选择实例: {0} ({1})"),
            item.Name,
            item.Source);

        InstanceActivated?.Invoke(item);
    }

    [RelayCommand]
    private void OpenVersionSettings(InstanceItem? item)
    {
        if (item == null)
        {
            return;
        }

        SelectedInstance = item;
        PersistPreferredInstance(item);
        Status = string.Format(L("Instances.Status.OpenVersionSettings", "正在打开版本设置: {0}"), item.Name);
        InstanceSettingsRequested?.Invoke(item);
    }

    [RelayCommand]
    private void ToggleFavorite(InstanceItem? item)
    {
        if (item == null)
        {
            return;
        }

        item.IsFavorite = !item.IsFavorite;
        SaveFavoriteState(item, item.IsFavorite);
        Status = item.IsFavorite
            ? string.Format(L("Instances.Status.Favorited", "已收藏实例: {0}"), item.Name)
            : string.Format(L("Instances.Status.Unfavorited", "已取消收藏: {0}"), item.Name);
    }

    [RelayCommand]
    private void OpenSelectedPathFolder()
    {
        if (!CanOpenSelectedPathFolder)
        {
            Status = L("Instances.Status.PathUnavailable", "当前路径不可用，无法打开文件夹");
            return;
        }

        OpenPathFolder(SelectedPathEntry);
    }

    [RelayCommand]
    private async Task AddInstanceAsync()
    {
        await AddManualInstanceAsync();
    }

    [RelayCommand]
    private async Task AddManualInstanceAsync()
    {
        var inputPath = await _dialogService.ShowGamePathSelectionDialogAsync(
            string.Empty,
            L("Instances.Dialog.Add.Title", "添加实例"),
            L("Instances.Dialog.Add.PathPrompt", "请输入实例目录路径"));

        if (string.IsNullOrWhiteSpace(inputPath))
        {
            return;
        }

        var resolvedPath = ResolveGameRootPath(inputPath.Trim());
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            Status = L("Instances.Status.InvalidPath", "目录无效：未检测到游戏核心文件");
            return;
        }

        var baseName = ResolveBaseInstanceName();

        var records = _instanceRegistryStore.LoadManualInstances();
        if (records.Any(record => string.Equals(NormalizePathKey(record.Path), NormalizePathKey(resolvedPath), StringComparison.Ordinal)))
        {
            Status = L("Instances.Status.ManualDuplicate", "该目录已存在于实例列表");
            return;
        }

        records.Add(new ManualInstanceRecord
        {
            Name = baseName,
            Path = resolvedPath
        });

        _instanceRegistryStore.SaveManualInstances(records);
        Reload();

        var targetPath = PathEntries.FirstOrDefault(path =>
            string.Equals(path.GamePath, resolvedPath, StringComparison.OrdinalIgnoreCase));
        if (targetPath != null)
        {
            SelectedPathEntry = targetPath;
            SelectedInstance = targetPath.Instances.FirstOrDefault(item =>
                string.Equals(item.Name, baseName, StringComparison.OrdinalIgnoreCase) &&
                !item.IsSmapiInstance)
                ?? targetPath.Instances.FirstOrDefault();
        }

        Status = string.Format(L("Instances.Status.ManualAdded", "已添加实例: {0}"), baseName);
    }

    [RelayCommand]
    private void ImportModpack()
    {
        ModpackImportRequested?.Invoke();
        Status = L("Instances.Status.ImportModpack", "正在进入整合包导入流程");
    }

    [RelayCommand]
    private async Task RenamePathAsync(PathEntryItem? entry)
    {
        if (entry == null)
        {
            return;
        }

        var newName = await _dialogService.ShowInstanceNameDialogAsync(
            L("Instances.Dialog.RenamePath.Title", "重命名路径"),
            entry.DisplayName);

        if (string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        var normalized = newName.Trim();
        if (string.Equals(entry.DisplayName, normalized, StringComparison.Ordinal))
        {
            return;
        }

        entry.DisplayName = normalized;

        var records = _instanceRegistryStore.LoadManualInstances();
        var changed = false;
        foreach (var record in records)
        {
            if (string.Equals(NormalizePathKey(record.Path), NormalizePathKey(entry.GamePath), StringComparison.Ordinal))
            {
                record.Name = normalized;
                changed = true;
            }
        }

        if (changed)
        {
            _instanceRegistryStore.SaveManualInstances(records);
        }

        foreach (var instance in entry.Instances)
        {
            if (!instance.IsBaseInstance)
            {
                continue;
            }

            var suffix = instance.IsSmapiInstance ? " (SMAPI)" : string.Empty;
            instance.Name = $"{normalized}{suffix}";
        }

        Status = string.Format(L("Instances.Status.PathRenamed", "路径已重命名为: {0}"), normalized);
    }

    [RelayCommand]
    private void OpenFolder(PathEntryItem? entry)
    {
        OpenPathFolder(entry);
    }

    [RelayCommand]
    private void RefreshPath(PathEntryItem? entry)
    {
        if (entry == null)
        {
            return;
        }

        var previousName = SelectedInstance?.Name;
        var previousIsSmapi = SelectedInstance?.IsSmapiInstance ?? false;

        if (!RefreshSinglePathEntry(entry))
        {
            Status = string.Format(L("Instances.Status.RefreshPathFailed", "刷新失败，路径不可用: {0}"), entry.DisplayName);
            return;
        }

        if (ReferenceEquals(SelectedPathEntry, entry))
        {
            OnSelectedPathEntryChanged(entry);
            SelectedInstance = SelectedPathInstances.FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(previousName) &&
                string.Equals(item.Name, previousName, StringComparison.OrdinalIgnoreCase) &&
                item.IsSmapiInstance == previousIsSmapi)
                ?? SelectedPathInstances.FirstOrDefault();
        }

        Status = string.Format(L("Instances.Status.PathRefreshed", "已刷新路径信息: {0}"), entry.DisplayName);
    }

    [RelayCommand]
    private async Task DeletePathAsync(PathEntryItem? entry)
    {
        if (entry == null)
        {
            return;
        }

        var confirm = await _dialogService.ShowConfirmAsync(
            L("Instances.Dialog.DeletePath.Title", "删除路径"),
            string.Format(L("Instances.Dialog.DeletePath.Message", "确认从列表中移除路径 {0} 吗？该操作不会删除游戏文件。"), entry.DisplayName));

        if (!confirm)
        {
            return;
        }

        var records = _instanceRegistryStore.LoadManualInstances();
        records.RemoveAll(record =>
            string.Equals(NormalizePathKey(record.Path), NormalizePathKey(entry.GamePath), StringComparison.Ordinal));
        _instanceRegistryStore.SaveManualInstances(records);

        var settings = _settingsStore.Load();
        if (string.Equals(NormalizePathKey(settings.PreferredInstancePath), NormalizePathKey(entry.GamePath), StringComparison.Ordinal))
        {
            settings.PreferredInstancePath = string.Empty;
            settings.InstanceName = "Default Instance";
            _settingsStore.Save(settings);
        }

        PathEntries.Remove(entry);
        HasPathEntries = PathEntries.Count > 0;
        OnPropertyChanged(nameof(HasPathEntries));
        OnPropertyChanged(nameof(HasNoPathEntries));

        if (ReferenceEquals(SelectedPathEntry, entry))
        {
            SelectedPathEntry = PathEntries.FirstOrDefault();
        }

        Status = string.Format(L("Instances.Status.PathDeleted", "已移除路径: {0}"), entry.DisplayName);
    }

    private void OpenPathFolder(PathEntryItem? entry)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.GamePath) || !Directory.Exists(entry.GamePath))
        {
            Status = L("Instances.Status.PathUnavailable", "当前路径不可用，无法打开文件夹");
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = entry.GamePath,
                UseShellExecute = true
            };

            Process.Start(psi);
            Status = string.Format(
                L("Instances.Status.PathOpened", "已打开路径: {0}"),
                entry.GamePath);
        }
        catch
        {
            Status = L("Instances.Status.OpenPathFailed", "打开路径失败，请检查系统权限");
        }
    }

    private void HandleLanguageChanged()
    {
        ApplyLocalizedTexts();
        Reload();
    }

    private void ApplyLocalizedTexts()
    {
        PageTitle = L("Instances.Page.Title", "版本选择 / 实例管理");
        PathListTitle = L("Instances.PathList.Title", "路径列表");
        PathActionsTitle = L("Instances.PathActions.Title", "路径操作");
        ChooseVersionTitle = L("Instances.ChooseVersion.Title", "选择启动版本");
        RefreshButtonText = L("Instances.Button.Refresh", "刷新");
        AddManualButtonText = L("Instances.Button.Add", "添加");
        ImportModpackButtonText = L("Instances.Button.ImportModpack", "导入整合包");
        OpenFolderButtonText = L("Instances.Button.OpenFolder", "打开文件夹");
        EmptyHintPrimaryText = L("Instances.EmptyHint.Primary", "请从左侧选择一个游戏路径");
        EmptyHintSecondaryText = L("Instances.EmptyHint.Secondary", "点击路径后，右侧将显示该路径下的所有可用版本");
    }

    private string L(string key, string fallback)
    {
        var value = _localizationService.Get(key);
        return string.Equals(value, key, StringComparison.Ordinal) ? fallback : value;
    }

    private void AddPathEntryIfValid(
        string candidatePath,
        string source,
        string? manualName,
        ISet<string> addedPaths,
        ISet<string> favoriteKeys)
    {
        var resolvedPath = ResolveGameRootPath(candidatePath);
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            return;
        }

        if (!addedPaths.Add(resolvedPath))
        {
            return;
        }

        var instances = CreateInstancesForPath(resolvedPath, source, manualName, favoriteKeys);
        if (instances.Count == 0)
        {
            return;
        }

        var version = instances.Select(item => item.Version).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

        var entry = new PathEntryItem
        {
            DisplayName = ResolveDisplayName(resolvedPath, manualName),
            GamePath = resolvedPath,
            Source = source,
            Version = version
        };

        foreach (var instance in instances)
        {
            entry.Instances.Add(instance);
        }

        PathEntries.Add(entry);
    }

    private List<InstanceItem> CreateInstancesForPath(
        string gamePath,
        string source,
        string? manualName,
        ISet<string> favoriteKeys)
    {
        var results = new List<InstanceItem>();

        var version = DetectGameVersion(gamePath);
        var hasSmapi = DetectSmapi(gamePath, out var smapiVersion);
        var baseName = ResolveDisplayName(gamePath, manualName);
        var vanillaFavorite = favoriteKeys.Contains(BuildFavoriteKey(gamePath, false));
        var smapiFavorite = favoriteKeys.Contains(BuildFavoriteKey(gamePath, true));

        var vanilla = new InstanceItem
        {
            Name = baseName,
            Path = gamePath,
            Source = source,
            IconSource = ResolveInstanceIcon(gamePath, false),
            Version = version,
            IsSmapiInstance = false,
            HasSmapiInstalled = hasSmapi,
            SmapiVersion = string.Empty,
            IsFavorite = vanillaFavorite,
            IsBaseInstance = true
        };

        vanilla.Tags.Add(L("Instances.Tag.Base", "Base"));
        results.Add(vanilla);

        if (hasSmapi)
        {
            var smapi = new InstanceItem
            {
                Name = string.Format(L("Instances.Name.SmapiPattern", "{0} (SMAPI)"), baseName),
                Path = gamePath,
                Source = source,
                IconSource = ResolveInstanceIcon(gamePath, true),
                Version = version,
                IsSmapiInstance = true,
                HasSmapiInstalled = true,
                SmapiVersion = smapiVersion,
                IsFavorite = smapiFavorite,
                IsBaseInstance = true
            };

            smapi.Tags.Add(L("Instances.Tag.Base", "Base"));
            results.Add(smapi);
        }

        var isolatedInstances = CreateVersionIsolatedInstances(gamePath, source, favoriteKeys);
        foreach (var isolated in isolatedInstances)
        {
            results.Add(isolated);
        }

        return results;
    }

    private List<InstanceItem> CreateVersionIsolatedInstances(
        string gamePath,
        string source,
        ISet<string> favoriteKeys)
    {
        var results = new List<InstanceItem>();
        var versionsPath = Path.Combine(gamePath, "versions");
        if (!Directory.Exists(versionsPath))
        {
            return results;
        }

        foreach (var versionDir in Directory.GetDirectories(versionsPath)
                     .OrderBy(static path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            var versionName = Path.GetFileName(versionDir);
            if (string.IsNullOrWhiteSpace(versionName))
            {
                continue;
            }

            var runtimePath = ResolveVersionRuntimePath(versionDir);
            if (string.IsNullOrWhiteSpace(runtimePath))
            {
                continue;
            }

            var hasSmapi = DetectSmapi(runtimePath, out var smapiVersion);
            var version = DetectGameVersion(runtimePath);
            var favorite = favoriteKeys.Contains(BuildFavoriteKey(runtimePath, hasSmapi));

            if (results.Any(existing =>
                    string.Equals(existing.Path, runtimePath, StringComparison.OrdinalIgnoreCase) &&
                    existing.IsSmapiInstance == hasSmapi))
            {
                continue;
            }

            var item = new InstanceItem
            {
                Name = versionName,
                Path = runtimePath,
                Source = source,
                IconSource = ResolveInstanceIcon(runtimePath, hasSmapi),
                Version = version,
                IsSmapiInstance = hasSmapi,
                HasSmapiInstalled = hasSmapi,
                SmapiVersion = hasSmapi ? smapiVersion : string.Empty,
                IsFavorite = favorite,
                IsBaseInstance = false
            };

            results.Add(item);
        }

        return results;
    }

    private IEnumerable<(string Path, string Source)> DiscoverAutoPathCandidates()
    {
        var steamSource = L("Instances.Source.Steam", "Steam");
        var gogSource = L("Instances.Source.Gog", "GOG");

        var steam = _gameInstallPathLocator.TryLocateSteamStardewPath();
        if (!string.IsNullOrWhiteSpace(steam))
        {
            yield return (steam, steamSource);
        }

        var gog = _gameInstallPathLocator.TryLocateGogStardewPath();
        if (!string.IsNullOrWhiteSpace(gog))
        {
            yield return (gog, gogSource);
        }

        foreach (var path in GetSteamFallbackCandidates())
        {
            yield return (path, steamSource);
        }

        foreach (var path in GetGogFallbackCandidates())
        {
            yield return (path, gogSource);
        }
    }

    private static IEnumerable<string> GetSteamFallbackCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(home, "Library", "Application Support", "Steam", "steamapps", "common", "Stardew Valley");
            yield return Path.Combine(home, ".steam", "steam", "steamapps", "common", "Stardew Valley");
            yield return Path.Combine(home, "Library", "Application Support", "Steam", "steamapps", "common", "Stardew Valley", "Stardew Valley.app", "Contents", "MacOS");
            yield break;
        }

        if (OperatingSystem.IsLinux())
        {
            yield return Path.Combine(home, ".steam", "steam", "steamapps", "common", "Stardew Valley");
            yield return Path.Combine(home, ".local", "share", "Steam", "steamapps", "common", "Stardew Valley");
            yield return Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam", "steamapps", "common", "Stardew Valley");
            yield break;
        }

        if (OperatingSystem.IsWindows())
        {
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            yield return Path.Combine(programFilesX86, "Steam", "steamapps", "common", "Stardew Valley");
            yield return Path.Combine(programFiles, "Steam", "steamapps", "common", "Stardew Valley");

            foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.IsReady))
            {
                yield return Path.Combine(drive.RootDirectory.FullName, "SteamLibrary", "steamapps", "common", "Stardew Valley");
                yield return Path.Combine(drive.RootDirectory.FullName, "Steam", "steamapps", "common", "Stardew Valley");
            }
        }
    }

    private static IEnumerable<string> GetGogFallbackCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsWindows())
        {
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            yield return Path.Combine(programFilesX86, "GOG Galaxy", "Games", "Stardew Valley");
            yield return Path.Combine(programFiles, "GOG Galaxy", "Games", "Stardew Valley");
            yield return Path.Combine(home, "GOG Games", "Stardew Valley");
            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            yield return "/Applications/Stardew Valley.app/Contents/MacOS";
            yield return Path.Combine(home, "Applications", "Stardew Valley.app", "Contents", "MacOS");
            yield return Path.Combine(home, "GOG Games", "Stardew Valley");
            yield break;
        }

        yield return Path.Combine(home, "GOG Games", "Stardew Valley");
        yield return Path.Combine(home, "Games", "Stardew Valley");
    }

    private static string? ResolveGameRootPath(string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return null;
        }

        var normalized = candidatePath.Trim().Trim('"');

        var possiblePaths = new List<string>
        {
            normalized,
            Path.Combine(normalized, "Content"),
            Path.Combine(normalized, "Stardew Valley.app", "Contents", "MacOS")
        };

        if (normalized.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            possiblePaths.Add(Path.Combine(normalized, "Contents", "MacOS"));
        }

        foreach (var path in possiblePaths)
        {
            if (Directory.Exists(path) && IsValidGamePath(path))
            {
                return path;
            }
        }

        return null;
    }

    private static bool IsValidGamePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return false;
        }

        var markers = new[]
        {
            "Stardew Valley.dll",
            "Stardew Valley.deps.json",
            "Stardew Valley.exe",
            "StardewValley.exe",
            "StardewValley",
            "Stardew Valley",
            "Stardew Valley.app"
        };

        return markers.Any(marker =>
            File.Exists(Path.Combine(path, marker)) ||
            Directory.Exists(Path.Combine(path, marker)));
    }

    private static string ResolveVersionRuntimePath(string versionDir)
    {
        var linkedGamePath = Path.Combine(versionDir, "game");
        if (Directory.Exists(linkedGamePath) && IsValidGamePath(linkedGamePath))
        {
            return linkedGamePath;
        }

        return IsValidGamePath(versionDir) ? versionDir : string.Empty;
    }

    private static string DetectGameVersion(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
        {
            return string.Empty;
        }

        var depsPath = Path.Combine(gamePath, "Stardew Valley.deps.json");
        if (File.Exists(depsPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(depsPath));
                if (doc.RootElement.TryGetProperty("targets", out var targetsElement))
                {
                    foreach (var target in targetsElement.EnumerateObject())
                    {
                        foreach (var package in target.Value.EnumerateObject())
                        {
                            if (package.Name.StartsWith("Stardew Valley/", StringComparison.OrdinalIgnoreCase))
                            {
                                var parts = package.Name.Split('/');
                                if (parts.Length == 2)
                                {
                                    return parts[1];
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Keep fallback path when deps parsing fails.
            }
        }

        var dllPath = Path.Combine(gamePath, "Stardew Valley.dll");
        if (File.Exists(dllPath))
        {
            try
            {
                var fileVersion = FileVersionInfo.GetVersionInfo(dllPath).FileVersion;
                return fileVersion ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        return string.Empty;
    }

    private static bool DetectSmapi(string gamePath, out string smapiVersion)
    {
        smapiVersion = string.Empty;

        var markers = new[]
        {
            Path.Combine(gamePath, "StardewModdingAPI.exe"),
            Path.Combine(gamePath, "StardewModdingAPI"),
            Path.Combine(gamePath, "StardewModdingAPI.dll")
        };

        var markerPath = markers.FirstOrDefault(path => File.Exists(path));
        if (string.IsNullOrWhiteSpace(markerPath))
        {
            return false;
        }

        try
        {
            smapiVersion = FileVersionInfo.GetVersionInfo(markerPath).FileVersion ?? string.Empty;
        }
        catch
        {
            smapiVersion = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(smapiVersion))
        {
            smapiVersion = "Unknown";
        }

        return true;
    }

    private void PersistPreferredInstance(InstanceItem? item)
    {
        if (item == null)
        {
            return;
        }

        var settings = _settingsStore.Load();
        settings.InstanceName = item.Name;
        settings.PreferredInstancePath = item.Path;
        settings.PreferredLaunchMode = item.IsSmapiInstance ? "SMAPI" : "Vanilla";
        _settingsStore.Save(settings);
    }

    private string ResolveInstanceIcon(string instancePath, bool isSmapiInstance)
    {
        var customIcon = InstanceIconResolver.ResolveIconPath(instancePath, isSmapiInstance);
        if (!string.IsNullOrWhiteSpace(customIcon))
        {
            return customIcon;
        }

        var key = isSmapiInstance ? "launch.instance.modded" : "launch.instance.vanilla";
        var fallback = isSmapiInstance
            ? "avares://SVL.Avalonia/Assets/Icons/Modded.png"
            : "avares://SVL.Avalonia/Assets/Icons/Vanilla.png";

        var resolved = _imageResourceService.Get(key);
        return string.IsNullOrWhiteSpace(resolved) ? fallback : resolved;
    }

    private static string ResolveDisplayName(string gamePath, string? customName)
    {
        if (!string.IsNullOrWhiteSpace(customName))
        {
            return customName.Trim();
        }

        var trimmedPath = gamePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var folderName = Path.GetFileName(trimmedPath);

        if (string.Equals(folderName, "MacOS", StringComparison.OrdinalIgnoreCase))
        {
            var appPath = Directory.GetParent(trimmedPath)?.Parent?.Parent;
            if (appPath != null)
            {
                var appName = Path.GetFileNameWithoutExtension(appPath.Name);
                if (!string.IsNullOrWhiteSpace(appName))
                {
                    return appName;
                }
            }
        }

        return string.IsNullOrWhiteSpace(folderName) ? "Stardew Valley" : folderName;
    }

    private string ResolveBaseInstanceName()
    {
        return L("Instances.Name.BaseDefault", "Stardew Valley");
    }

    private bool RefreshSinglePathEntry(PathEntryItem entry)
    {
        var resolvedPath = ResolveGameRootPath(entry.GamePath);
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            return false;
        }

        var settings = _settingsStore.Load();
        var favoriteKeys = BuildFavoriteKeySet(settings);
        var refreshed = CreateInstancesForPath(resolvedPath, entry.Source, entry.DisplayName, favoriteKeys);
        if (refreshed.Count == 0)
        {
            return false;
        }

        entry.GamePath = resolvedPath;
        entry.Version = refreshed.Select(item => item.Version).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
        entry.Instances.Clear();
        foreach (var item in refreshed)
        {
            entry.Instances.Add(item);
        }

        return true;
    }

    private void RebuildInstanceGroups()
    {
        BaseInstances.Clear();
        VersionInstances.Clear();

        foreach (var instance in SelectedPathInstances)
        {
            if (instance.IsBaseInstance)
            {
                BaseInstances.Add(instance);
            }
            else
            {
                VersionInstances.Add(instance);
            }
        }

        OnPropertyChanged(nameof(HasBaseInstances));
        OnPropertyChanged(nameof(HasVersionInstances));
    }

    private static string NormalizePathKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();
        }
        catch
        {
            return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        }
    }

    private static string BuildFavoriteKey(string path, bool isSmapi)
    {
        return $"{NormalizePathKey(path)}|{(isSmapi ? "SMAPI" : "VANILLA")}";
    }

    private static HashSet<string> BuildFavoriteKeySet(Models.AppUserSettings settings)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in settings.FavoriteInstanceKeys)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                result.Add(key.Trim());
            }
        }

        return result;
    }

    private void SaveFavoriteState(InstanceItem item, bool isFavorite)
    {
        var settings = _settingsStore.Load();
        var favorites = BuildFavoriteKeySet(settings);
        var key = BuildFavoriteKey(item.Path, item.IsSmapiInstance);

        if (isFavorite)
        {
            favorites.Add(key);
        }
        else
        {
            favorites.Remove(key);
        }

        settings.FavoriteInstanceKeys = favorites
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _settingsStore.Save(settings);
    }
}