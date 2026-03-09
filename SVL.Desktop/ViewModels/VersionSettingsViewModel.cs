using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SVL.Core.Logging;
using SVL.Core.Stardew.Instance;
using SVL.Core.Stardew.Mod;
using SVL.Core.Stardew.Mod.SMAPI;
using SVL.Core.Download;
using SVL.Desktop.Controls;

namespace SVL.Desktop.ViewModels;

/// <summary>
/// 版本设置页面导航项枚举
/// </summary>
public enum VersionSettingsPage
{
    Overview,      // 概览
    AutoInstall,   // 自动安装
    Settings,      // 设置
    ModManagement, // Mod管理
    Export         // 导出
}

public partial class VersionSettingsRightViewModel : ObservableObject
{
    private MainWindowViewModel _mainViewModel;
    private CancellationTokenSource? _nameSaveCts;
    private CancellationTokenSource? _descriptionSaveCts;
    private bool _isLoadingInstanceData;
    private const int AutoSaveDelayMs = 800;

    [ObservableProperty]
    private VersionSettingsPage _currentPage;

    [ObservableProperty]
    private GamePathInfo? _selectedInstance;

    partial void OnSelectedInstanceChanged(GamePathInfo? value)
    {
        _nameSaveCts?.Cancel();
        _descriptionSaveCts?.Cancel();

        if (value != null)
        {
            LoadInstanceData(value);
        }
    }

    // 第一个卡片：版本信息
    [ObservableProperty]
    private string _instanceIcon = "/Images/Vanilla.png";

    [ObservableProperty]
    private string _instanceName = string.Empty;

    partial void OnInstanceNameChanged(string value)
    {
        if (_isLoadingInstanceData || SelectedInstance == null)
            return;

        ScheduleNameSave(value);
    }

    [ObservableProperty]
    private string _instanceVersion = string.Empty;

    [ObservableProperty]
    private bool _isSMAPIInstance;

    [ObservableProperty]
    private string _smapiVersion = string.Empty;

    // 第二个卡片：个性化
    [ObservableProperty]
    private string _description = string.Empty;

    partial void OnDescriptionChanged(string value)
    {
        if (_isLoadingInstanceData || SelectedInstance == null)
            return;

        ScheduleDescriptionSave(value);
    }

    [ObservableProperty]
    private bool _isFavorite;

    /// <summary>
    /// 当前状态信息（显示在右上角）
    /// </summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    // 第三个卡片：快捷方式
    [ObservableProperty]
    private bool _hasSaveFolder;

    [ObservableProperty]
    private string _saveFolderPath = string.Empty;

    [ObservableProperty]
    private bool _hasModFolder;

    [ObservableProperty]
    private string _modFolderPath = string.Empty;

    [ObservableProperty]
    private string _instanceGamePath = string.Empty;

    public VersionSettingsRightViewModel(MainWindowViewModel mainViewModel, GamePathInfo instance)
    {
        _mainViewModel = mainViewModel;
        _selectedInstance = instance;
        _currentPage = VersionSettingsPage.Overview;
        LoadInstanceData(instance);
    }

    private void LoadInstanceData(GamePathInfo instance)
    {
        _isLoadingInstanceData = true;
        try
        {
            InstanceName = instance.Name;
            InstanceVersion = instance.DisplayVersion;
            IsSMAPIInstance = instance.IsSMAPIInstance;
            // 使用实例的自定义图标（如果有）
            InstanceIcon = instance.GetIconPath();
            InstanceGamePath = instance.GamePath;

            // 使用实例中已检测的 SMAPI 版本
            if (instance.IsSMAPIInstance && !string.IsNullOrEmpty(instance.SMAPIVersion))
            {
                SmapiVersion = instance.SMAPIVersion;
            }
            else if (instance.IsSMAPIInstance)
            {
                // 如果实例中没有版本信息，尝试从版本隔离目录检测
                SmapiVersion = GetSMAPIVersionFromInstance(instance);
            }

            Description = instance.Description ?? string.Empty;
            IsFavorite = instance.IsFavorite;

            // 检查文件夹（传入实例信息以支持版本隔离）
            CheckFolders(instance);
        }
        finally
        {
            _isLoadingInstanceData = false;
        }
    }

    private void ScheduleNameSave(string value)
    {
        _nameSaveCts?.Cancel();
        _nameSaveCts = new CancellationTokenSource();
        _ = DebouncedSaveNameAsync(value, _nameSaveCts.Token);
    }

    private async Task DebouncedSaveNameAsync(string pendingName, CancellationToken token)
    {
        try
        {
            await Task.Delay(AutoSaveDelayMs, token);
            if (token.IsCancellationRequested)
                return;

            if (SelectedInstance == null)
                return;

            var normalizedName = (pendingName ?? string.Empty).Trim();
            if (!IsValidInstanceName(normalizedName))
            {
                StatusMessage = "名称非法，未保存";
                return;
            }

            if (IsDuplicateInstanceName(normalizedName))
            {
                if (SelectedInstance != null)
                {
                    InstanceName = SelectedInstance.Name;
                }

                StatusMessage = "名称已存在，未保存";
                return;
            }

            if (SelectedInstance.Name == normalizedName)
                return;

            var oldName = SelectedInstance.Name;
            SelectedInstance.Name = normalizedName;

            // Name setter 在隔离目录重命名失败时会拒绝更新，这里同步回滚输入框显示。
            if (!string.Equals(SelectedInstance.Name, normalizedName, StringComparison.OrdinalIgnoreCase))
            {
                InstanceName = SelectedInstance.Name;
                StatusMessage = "重命名失败，未保存";
                return;
            }

            InstanceName = normalizedName;
            SaveInstanceConfig("✓ 已保存");
        }
        catch (TaskCanceledException)
        {
        }
    }

    private bool IsDuplicateInstanceName(string name)
    {
        if (SelectedInstance == null)
            return false;

        var allInstances = SettingsService.LoadInstances();
        return allInstances.Any(i =>
            !string.Equals(i.Id, SelectedInstance.Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private void ScheduleDescriptionSave(string value)
    {
        _descriptionSaveCts?.Cancel();
        _descriptionSaveCts = new CancellationTokenSource();
        _ = DebouncedSaveDescriptionAsync(value, _descriptionSaveCts.Token);
    }

    private async Task DebouncedSaveDescriptionAsync(string pendingDescription, CancellationToken token)
    {
        try
        {
            await Task.Delay(AutoSaveDelayMs, token);
            if (token.IsCancellationRequested)
                return;

            if (SelectedInstance == null)
                return;

            if (SelectedInstance.Description == pendingDescription)
                return;

            SelectedInstance.Description = pendingDescription ?? string.Empty;
            SaveInstanceConfig("✓ 已保存");
        }
        catch (TaskCanceledException)
        {
        }
    }

    private static bool IsValidInstanceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal))
            return false;

        return name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    private string GetSMAPIVersionFromInstance(GamePathInfo instance)
    {
        try
        {
            string smapiExePath;

            if (instance.EnableIsolation)
            {
                // 版本隔离模式：从隔离的版本目录查找
                var instanceFolderName = InstanceIsolationService.GenerateVersionFolderName(
                    instance.Name, instance.IsSMAPIInstance);
                var versionPath = InstanceIsolationService.GetVersionPath(instance.GamePath, instanceFolderName);
                smapiExePath = Path.Combine(versionPath, "StardewModdingAPI.exe");
            }
            else
            {
                // 非隔离模式：从游戏根目录查找
                smapiExePath = Path.Combine(instance.GamePath, "StardewModdingAPI.exe");
            }

            if (File.Exists(smapiExePath))
            {
                var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(smapiExePath);
                return versionInfo.FileVersion ?? "未知版本";
            }
        }
        catch
        {
            // 忽略错误
        }
        return "未知版本";
    }

    private void CheckFolders(GamePathInfo instance)
    {
        string gamePath = instance.GamePath;

        // 存档文件夹始终使用系统默认路径（不支持隔离）
        var savePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StardewValley",
            "Saves");
        HasSaveFolder = Directory.Exists(savePath);
        SaveFolderPath = savePath;

        // 检查 Mod 文件夹
        if (instance.EnableIsolation)
        {
            // 版本隔离模式：使用隔离的 Mod 目录
            var instanceFolderName = InstanceIsolationService.GenerateVersionFolderName(
                instance.Name, instance.IsSMAPIInstance);
            var isolatedModPath = InstanceIsolationService.GetIsolatedModsPath(gamePath, instanceFolderName);

            HasModFolder = Directory.Exists(isolatedModPath);
            ModFolderPath = isolatedModPath;
            InstanceGamePath = InstanceIsolationService.GetVersionPath(gamePath, instanceFolderName);
        }
        else
        {
            // 非隔离模式：使用游戏根目录的 Mods 文件夹
            var modPath = Path.Combine(gamePath, "Mods");
            HasModFolder = Directory.Exists(modPath);
            ModFolderPath = modPath;
            InstanceGamePath = gamePath;
        }
    }

    [RelayCommand]
    private void OpenFolder(string path)
    {
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", path);
        }
        catch (Exception ex)
        {
            SvlMessageBox.Error($"无法打开文件夹：{ex.Message}");
        }
    }

    [RelayCommand]
    private void SaveDescription()
    {
        if (SelectedInstance == null)
            return;

        SelectedInstance.Description = Description ?? string.Empty;
        SaveInstanceConfig("✓ 已保存");
    }

    /// <summary>
    /// 更改 SMAPI 版本（覆盖安装）
    /// </summary>
    [RelayCommand]
    private async Task ChangeSmapiVersionAsync()
    {
        if (SelectedInstance == null)
        {
            SvlMessageBox.Error("未选择实例");
            return;
        }

        try
        {
            Log.Info($"[VersionSettings] 开始更改 SMAPI 版本，实例: {SelectedInstance.Name}");

            // 获取版本隔离路径
            string targetPath;
            if (SelectedInstance.EnableIsolation)
            {
                var instanceFolderName = InstanceIsolationService.GenerateVersionFolderName(
                    SelectedInstance.Name, SelectedInstance.IsSMAPIInstance);
                targetPath = InstanceIsolationService.GetVersionPath(SelectedInstance.GamePath, instanceFolderName);
            }
            else
            {
                targetPath = SelectedInstance.GamePath;
            }

            Log.Info($"[VersionSettings] SMAPI 安装目标路径: {targetPath}");

            // 显示 SMAPI 版本选择对话框
            var dialog = new Controls.SmapiVersionPickerDialog(targetPath, SelectedInstance.GamePath)
            {
                Owner = System.Windows.Application.Current.MainWindow
            };

            if (dialog.ShowDialog() == true && dialog.SelectedVersion != null)
            {
                var selectedVersion = dialog.SelectedVersion;
                Log.Info($"[VersionSettings] 用户选择 SMAPI 版本: {selectedVersion.Version} ({selectedVersion.Source})");

                // 创建 SMAPI 下载任务（覆盖安装）
                SmapiDownloadTask smapiTask;

                if (selectedVersion.Source == "NexusMods" && selectedVersion.FileId.HasValue)
                {
                    // NexusMods 来源
                    smapiTask = new SmapiDownloadTask(
                        SelectedInstance.GamePath,
                        SelectedInstance.Name,
                        selectedVersion.Version,
                        SmapiSource.NexusMods,
                        selectedVersion.FileId.Value);
                }
                else if (selectedVersion.Source == "Curseforge" && !string.IsNullOrEmpty(selectedVersion.DownloadUrl))
                {
                    // Curseforge 来源
                    smapiTask = new SmapiDownloadTask(
                        SelectedInstance.GamePath,
                        SelectedInstance.Name,
                        selectedVersion.Version,
                        SmapiSource.Curseforge,
                        downloadUrl: selectedVersion.DownloadUrl);
                }
                else
                {
                    // GitHub 来源（默认）
                    smapiTask = new SmapiDownloadTask(
                        gameBasePath: SelectedInstance.GamePath,
                        instanceName: SelectedInstance.Name,
                        smapiVersion: selectedVersion.Version,
                        source: SmapiSource.GitHub);
                }

                // 标记为更新模式（不删除旧版本目录，由 SmapiDownloadTask 处理）
                smapiTask.IsUpdateMode = true;

                // 添加任务到下载管理器
                await DownloadManager.Instance.AddTaskAsync(smapiTask);
                Log.Info($"[VersionSettings] SMAPI 下载任务已添加，任务 ID: {smapiTask.Id}");

                // 显示通知
                Controls.FloatingNotificationControl.Show(
                    title: "SMAPI 更新已开始",
                    message: $"正在安装 SMAPI {selectedVersion.Version}，请在任务管理页面查看进度。",
                    autoCloseDelay: 4000,
                    notificationType: Controls.NotificationType.Info);

                // 自动导航到任务管理页面
                if (System.Windows.Application.Current.MainWindow is MainWindow mw &&
                    mw.DataContext is MainWindowViewModel mvm)
                {
                    mvm.CurrentPage = PageType.Download;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[VersionSettings] 更改 SMAPI 版本失败");
            SvlMessageBox.Error($"更改 SMAPI 版本失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private void ToggleFavorite()
    {
        // 注意：这个方法现在不需要了，因为 CheckBox 直接绑定到 IsFavorite
        // 保存操作由 OnIsFavoriteChanged 处理
    }

    partial void OnIsFavoriteChanged(bool value)
    {
        // 当收藏状态改变时保存到配置
        if (SelectedInstance != null)
        {
            SelectedInstance.IsFavorite = value;
            SaveFavoriteToConfig();
        }
    }

    private void SaveFavoriteToConfig()
    {
        try
        {
            var allInstances = SVL.Core.Stardew.Instance.SettingsService.LoadInstances();
            var index = allInstances.FindIndex(i => i.Id == SelectedInstance?.Id);
            if (index >= 0)
            {
                allInstances[index] = SelectedInstance;
                SVL.Core.Stardew.Instance.SettingsService.SaveInstances(allInstances);

                // 触发全局事件
                if (SelectedInstance != null)
                {
                    GlobalEvents.OnInstanceChanged(SelectedInstance.Id);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"保存收藏状态失败: {ex.Message}");
        }
    }

    private void SaveInstanceConfig(string successMessage = "✓ 已保存")
    {
        try
        {
            // 获取所有实例
            var allInstances = SVL.Core.Stardew.Instance.SettingsService.LoadInstances();

            // 找到当前实例并更新
            var index = allInstances.FindIndex(i => i.Id == SelectedInstance?.Id);
            if (index >= 0)
            {
                allInstances[index] = SelectedInstance;
                SVL.Core.Stardew.Instance.SettingsService.SaveInstances(allInstances);

                // 刷新主页面的显示
                if (_mainViewModel.LeftPanelContent is LaunchLeftViewModel launchLeftVm)
                {
                    launchLeftVm.LoadSelectedInstance();
                }

                // 触发全局事件，通知其他页面实例配置已更改
                if (SelectedInstance != null)
                {
                    GlobalEvents.OnInstanceChanged(SelectedInstance.Id);
                }

                // 在右上角显示保存成功提示
                StatusMessage = successMessage;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"✗ 保存失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void ChangeIcon()
    {
        var dialog = new Views.IconPickerDialog
        {
            Owner = System.Windows.Application.Current.MainWindow
        };

        if (dialog.ShowDialog() == true)
        {
            var selectedIcon = dialog.SelectedIcon;
            if (!string.IsNullOrEmpty(selectedIcon))
            {
                InstanceIcon = selectedIcon;

                // 保存到实例配置
                if (SelectedInstance != null)
                {
                    // 无论是预设图标还是自定义图标，都保存到 CustomIcon
                    // 这样可以保留用户的选择，不会被自动逻辑覆盖
                    SelectedInstance.CustomIcon = selectedIcon;

                    // 保存配置到文件
                    SaveInstanceConfig("✓ 图标已保存");
                }
            }
        }
    }

    /// <summary>
    /// 删除版本命令
    /// </summary>
    [RelayCommand]
    private void DeleteVersion()
    {
        if (SelectedInstance == null)
        {
            SvlMessageBox.Error("未选择实例");
            return;
        }

        // 显示删除路径信息（使用版本名称而非ID）
        var versionFolderName = InstanceIsolationService.GenerateVersionFolderName(
            SelectedInstance.Name,
            SelectedInstance.IsSMAPIInstance);
        var versionsPath = System.IO.Path.Combine(SelectedInstance.GamePath, "versions", versionFolderName);

        // 二次确认对话框
        if (SvlMessageBox.Confirm(
            $"确定要将版本 \"{SelectedInstance.Name}\" 移入回收站吗？\n\n" +
            $"此操作将删除该版本的所有文件，包括：\n" +
            $"• 游戏文件\n" +
            $"• Mods 文件夹\n" +
            $"• 配置文件\n\n" +
            $"删除路径：\n{versionsPath}\n\n" +
            $"此操作无法撤销。",
            "确认删除"))
        {
            // 用户确认删除
            var (success, errorMessage) = SVL.Core.Stardew.Instance.SettingsService.MoveInstanceToRecycleBin(SelectedInstance.Id);

            if (success)
            {
                SvlMessageBox.Success($"版本 \"{SelectedInstance.Name}\" 已成功删除", "删除成功");

                // 返回启动页面
                _mainViewModel.NavigateToLaunchCommand.Execute(null);
            }
            else
            {
                SvlMessageBox.Error(
                    $"删除版本失败！\n\n{errorMessage}\n\n" +
                    $"可能的原因：\n" +
                    $"• 文件正在被使用（请关闭游戏后重试）\n" +
                    $"• 权限不足（请以管理员身份运行）\n" +
                    $"• 路径不存在或已被删除",
                    "删除失败");
            }
        }
    }

    // ========== MOD 管理相关 ==========

    /// <summary>
    /// MOD 列表分类
    /// </summary>
    public enum ModFilterCategory
    {
        All,        // 全部
        Enabled,    // 已启用
        Disabled,   // 已禁用
        Updatable,  // 可更新
        Backup      // 备份
    }

    // MOD 列表
    [ObservableProperty]
    private ObservableCollection<SdVMod> _mods = new();

    [ObservableProperty]
    private ObservableCollection<SdVMod> _filteredMods = new();

    [ObservableProperty]
    private ObservableCollection<SdVMod> _backupMods = new();

    private readonly List<SdVMod> _filteredSource = [];
    private const int ModsPageSize = 10;

    // 搜索关键词
    [ObservableProperty]
    private string _searchKeyword = string.Empty;

    // 当前过滤分类
    [ObservableProperty]
    private ModFilterCategory _currentFilterCategory = ModFilterCategory.All;

    // 选中的 MOD 数量
    [ObservableProperty]
    private int _selectedCount = 0;

    // 是否显示选择操作栏
    [ObservableProperty]
    private bool _showSelectionActions = false;

    [ObservableProperty]
    private int _selectedUpdatableCount = 0;

    [ObservableProperty]
    private int _currentPageIndex = 1;

    [ObservableProperty]
    private int _totalPages = 1;

    [ObservableProperty]
    private int _totalFilteredCount = 0;

    [ObservableProperty]
    private List<int> _pageNumbers = [];

    public bool HasSelectedUpdatable => CurrentFilterCategory != ModFilterCategory.Backup && SelectedUpdatableCount > 0;
    public bool IsBackupFilterActive => CurrentFilterCategory == ModFilterCategory.Backup;
    public bool HasPreviousPage => CurrentPageIndex > 1;
    public bool HasNextPage => CurrentPageIndex < TotalPages;
    public bool CanGoPreviousPage => CurrentPageIndex > 1;
    public bool CanGoNextPage => CurrentPageIndex < TotalPages;
    public string PageInfo => $"{CurrentPageIndex}/{TotalPages}";

    // MOD 统计数量
    [ObservableProperty]
    private int _totalModsCount = 0;

    [ObservableProperty]
    private int _updatableModsCount = 0;

    [ObservableProperty]
    private int _enabledModsCount = 0;

    [ObservableProperty]
    private int _disabledModsCount = 0;

    [ObservableProperty]
    private int _backupModsCount = 0;

    // 更新检查状态
    [ObservableProperty]
    private bool _isCheckingUpdates = false;

    [ObservableProperty]
    private string _updateStatus = "点击检查更新";

    // MOD 管理器
    private IModManager _modManager;

    partial void OnCurrentPageChanged(VersionSettingsPage value)
    {
        // 当切换到 MOD 管理页面时，加载 MOD 列表
        if (value == VersionSettingsPage.ModManagement)
        {
            CurrentFilterCategory = ModFilterCategory.All;
            System.Diagnostics.Debug.WriteLine($"[VersionSettings] 切换到 MOD 管理页面，SelectedInstance={SelectedInstance?.Name}");

            if (SelectedInstance != null)
            {
                // 直接在当前上下文中调用，不要用 Task.Run
                // 异步方法会自动在后台执行
                _ = LoadModsAsync();
            }
        }
    }

    partial void OnSearchKeywordChanged(string value)
    {
        CurrentPageIndex = 1;
        ApplyFilter();
    }

    partial void OnCurrentFilterCategoryChanged(ModFilterCategory value)
    {
        ClearSelection();
        CurrentPageIndex = 1;
        ApplyFilter();
        OnPropertyChanged(nameof(IsBackupFilterActive));
        OnPropertyChanged(nameof(HasSelectedUpdatable));
    }

    partial void OnCurrentPageIndexChanged(int value)
    {
        RefreshPagedMods();
        UpdatePageNumbers();
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));
        OnPropertyChanged(nameof(CanGoPreviousPage));
        OnPropertyChanged(nameof(CanGoNextPage));
        OnPropertyChanged(nameof(PageInfo));
    }

    partial void OnTotalPagesChanged(int value)
    {
        UpdatePageNumbers();
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));
        OnPropertyChanged(nameof(CanGoPreviousPage));
        OnPropertyChanged(nameof(CanGoNextPage));
        OnPropertyChanged(nameof(PageInfo));
    }

    /// <summary>
    /// 加载 MOD 列表
    /// </summary>
    [RelayCommand]
    private async Task LoadModsAsync()
    {
        if (SelectedInstance == null)
        {
            System.Diagnostics.Debug.WriteLine("[VersionSettings] LoadModsAsync: SelectedInstance is null");
            return;
        }

        string modsPath = string.Empty;

        try
        {
            System.Diagnostics.Debug.WriteLine($"[VersionSettings] 开始加载 MOD 列表，实例：{SelectedInstance.Name}");
            System.Diagnostics.Debug.WriteLine($"[VersionSettings] IsSMAPIInstance={SelectedInstance.IsSMAPIInstance}");
            System.Diagnostics.Debug.WriteLine($"[VersionSettings] EnableIsolation={SelectedInstance.EnableIsolation}");

            // 获取 Mod 文件夹路径
            if (SelectedInstance.EnableIsolation)
            {
                var instanceFolderName = InstanceIsolationService.GenerateVersionFolderName(
                    SelectedInstance.Name, SelectedInstance.IsSMAPIInstance);
                modsPath = InstanceIsolationService.GetIsolatedModsPath(SelectedInstance.GamePath, instanceFolderName);
            }
            else
            {
                modsPath = Path.Combine(SelectedInstance.GamePath, "Mods");
            }

            System.Diagnostics.Debug.WriteLine($"[VersionSettings] Mods 路径：{modsPath}");

            // 检查目录是否存在
            if (!Directory.Exists(modsPath))
            {
                System.Diagnostics.Debug.WriteLine($"[VersionSettings] Mods 目录不存在：{modsPath}");
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Mods.Clear();
                    ApplyFilter();
                });
                return;
            }

            _modManager ??= new ModManager();
            var loadedMods = await _modManager.LoadModsAsync(modsPath);
            BuildDisplayDependencies(loadedMods);

            System.Diagnostics.Debug.WriteLine($"[VersionSettings] 成功加载 {loadedMods.Count} 个 MOD");

            // 在 UI 线程上更新集合
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Mods.Clear();
                foreach (var mod in loadedMods)
                {
                    System.Diagnostics.Debug.WriteLine($"[VersionSettings]   - {mod.Name} (Enabled: {mod.IsEnabled})");
                    Mods.Add(mod);
                }

                ApplyFilter();
                System.Diagnostics.Debug.WriteLine($"[VersionSettings] FilteredMods count: {FilteredMods.Count}");
            });

            var backups = ModBackupService.LoadBackups(modsPath);
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                BackupMods.Clear();
                foreach (var backup in backups)
                {
                    BackupMods.Add(backup);
                }
                ApplyFilter();
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VersionSettings] 加载 MOD 列表失败：{ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[VersionSettings] StackTrace: {ex.StackTrace}");

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                SvlMessageBox.Error($"加载 MOD 列表失败：{ex.Message}\n\n路径：{modsPath}");
            });
        }
    }

    /// <summary>
    /// 刷新 MOD 列表（手动刷新）
    /// </summary>
    [RelayCommand]
    private async Task RefreshModsAsync()
    {
        await LoadModsAsync();
    }

    public async Task RefreshAfterExternalInstallAsync()
    {
        await LoadModsAsync();
    }

    private void BuildDisplayDependencies(List<SdVMod> loadedMods)
    {
        var installedByUniqueId = loadedMods
            .Where(m => !string.IsNullOrWhiteSpace(m.UniqueId))
            .GroupBy(m => m.UniqueId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var mod in loadedMods)
        {
            var displayDependencies = new List<ModDependencyLink>();

            if (!string.IsNullOrWhiteSpace(mod.Manifest?.ContentPackFor?.UniqueId))
            {
                displayDependencies.Add(CreateDependencyLink(
                    mod.Manifest.ContentPackFor.UniqueId,
                    mod.Manifest.ContentPackFor.MinimumVersion,
                    installedByUniqueId,
                    note: "内容包前置"));
            }

            if (mod.Manifest?.Dependencies != null)
            {
                foreach (var dependency in mod.Manifest.Dependencies)
                {
                    if (string.IsNullOrWhiteSpace(dependency?.UniqueId))
                        continue;

                    displayDependencies.Add(CreateDependencyLink(
                        dependency.UniqueId,
                        dependency.MinimumVersion,
                        installedByUniqueId,
                        dependency.IsRequired ? string.Empty : "可选前置",
                        dependency.IsRequired));
                }
            }

            mod.DisplayDependencies = displayDependencies;
        }
    }

    private static ModDependencyLink CreateDependencyLink(
        string uniqueId,
        string? minimumVersion,
        IReadOnlyDictionary<string, SdVMod> installedByUniqueId,
        string note,
        bool isRequired = true)
    {
        installedByUniqueId.TryGetValue(uniqueId, out var installedMod);

        return new ModDependencyLink
        {
            UniqueId = uniqueId,
            DisplayName = installedMod?.Name ?? SimplifyUniqueId(uniqueId),
            MinimumVersion = minimumVersion ?? string.Empty,
            IsRequired = isRequired,
            IsInstalled = installedMod != null,
            IsInstalledAndEnabled = installedMod?.IsEnabled == true,
            IsInstalledButDisabled = installedMod != null && !installedMod.IsEnabled,
            InstalledModId = installedMod?.Id ?? string.Empty,
            InstalledModName = installedMod?.Name ?? string.Empty,
            Note = note
        };
    }

    private static string SimplifyUniqueId(string uniqueId)
    {
        if (string.IsNullOrWhiteSpace(uniqueId))
            return string.Empty;

        var parts = uniqueId.Split('.');
        return parts.Length == 0 ? uniqueId : parts[parts.Length - 1];
    }

    private static IEnumerable<SdVMod> GetVisibleModRoots(IEnumerable<SdVMod> mods)
    {
        return mods.Where(mod => !mod.IsChildMod);
    }

    private static bool MatchesKeyword(SdVMod mod, string keyword)
    {
         return (!string.IsNullOrWhiteSpace(mod.Name) && mod.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
             || (!string.IsNullOrWhiteSpace(mod.Author) && mod.Author.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
             || (!string.IsNullOrWhiteSpace(mod.UniqueId) && mod.UniqueId.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
             || (!string.IsNullOrWhiteSpace(mod.FolderName) && mod.FolderName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static bool MatchesSearch(SdVMod mod, string keyword)
    {
        if (MatchesKeyword(mod, keyword))
            return true;

        return mod.ChildMods.Any(child => MatchesKeyword(child, keyword));
    }

    private bool MatchesCurrentFilter(SdVMod mod)
    {
        return CurrentFilterCategory switch
        {
            ModFilterCategory.Enabled => mod.IsEnabled || mod.ChildMods.Any(child => child.IsEnabled),
            ModFilterCategory.Disabled => !mod.IsEnabled || mod.ChildMods.Any(child => !child.IsEnabled),
            ModFilterCategory.Updatable => mod.HasUpdate || mod.ChildMods.Any(child => child.HasUpdate),
            _ => true
        };
    }

    /// <summary>
    /// 手动检查 MOD 更新（仅检查，不下载）
    /// </summary>
    [RelayCommand]
    private async Task CheckModUpdatesAsync()
    {
        if (CurrentFilterCategory == ModFilterCategory.Backup)
        {
            UpdateStatus = "备份栏不执行更新检测";
            return;
        }

        if (SelectedInstance == null || Mods.Count == 0)
        {
            UpdateStatus = "请先选择实例并加载 MOD";
            return;
        }

        try
        {
            IsCheckingUpdates = true;
            UpdateStatus = "正在检查更新...";

            await _modManager.CheckModUpdatesAsync(Mods.ToList());

            UpdateModsCount();
            UpdateStatus = $"检查完成 - {UpdatableModsCount} 个可更新";
        }
        catch (SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsTokenExpiredException)
        {
            UpdateStatus = "NexusMods 登录已过期";
            SVL.Core.Logging.Log.Warn("[VersionSettings] NexusMods Token 已过期");
            Utilities.NexusAuthStateHelper.HandleTokenExpired("CheckModUpdates", "VersionSettingsViewModel", showNotification: true);
        }
        catch (Exception ex)
        {
            UpdateStatus = "检查更新失败";
            SVL.Core.Logging.Log.Error(ex, "[VersionSettings] 检查 MOD 更新失败");
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    /// <summary>
    /// 应用过滤条件
    /// </summary>
    private void ApplyFilter()
    {
        var source = CurrentFilterCategory == ModFilterCategory.Backup
            ? BackupMods.AsEnumerable()
            : GetVisibleModRoots(Mods).AsEnumerable();

        var filtered = source;

        // 搜索过滤
        if (!string.IsNullOrWhiteSpace(SearchKeyword))
        {
            var keyword = SearchKeyword.Trim();
            filtered = filtered.Where(mod => MatchesSearch(mod, keyword));
        }

        // 分类过滤
        if (CurrentFilterCategory != ModFilterCategory.Backup)
        {
            filtered = filtered.Where(MatchesCurrentFilter);
        }

        _filteredSource.Clear();
        _filteredSource.AddRange(filtered);

        TotalFilteredCount = _filteredSource.Count;
        TotalPages = Math.Max(1, (int)Math.Ceiling(TotalFilteredCount / (double)ModsPageSize));
        if (CurrentPageIndex > TotalPages)
            CurrentPageIndex = TotalPages;
        if (CurrentPageIndex < 1)
            CurrentPageIndex = 1;

        RefreshPagedMods();

        // 更新数量统计
        UpdateModsCount();
        UpdateSelectionState();
        OnPropertyChanged(nameof(CanGoPreviousPage));
        OnPropertyChanged(nameof(CanGoNextPage));
        OnPropertyChanged(nameof(PageInfo));
    }

    private void RefreshPagedMods()
    {
        FilteredMods.Clear();

        var pageItems = _filteredSource
            .Skip((Math.Max(CurrentPageIndex, 1) - 1) * ModsPageSize)
            .Take(ModsPageSize);

        foreach (var mod in pageItems)
        {
            FilteredMods.Add(mod);
        }
    }

    private void UpdatePageNumbers()
    {
        var pages = new List<int>();
        var totalPages = Math.Max(1, TotalPages);

        if (totalPages <= 7)
        {
            for (int i = 1; i <= totalPages; i++)
            {
                pages.Add(i);
            }
        }
        else
        {
            pages.Add(1);

            if (CurrentPageIndex <= 3)
            {
                for (int i = 2; i <= 5; i++)
                {
                    pages.Add(i);
                }

                pages.Add(-1);
                pages.Add(totalPages);
            }
            else if (CurrentPageIndex >= totalPages - 2)
            {
                pages.Add(-1);
                for (int i = totalPages - 4; i <= totalPages; i++)
                {
                    pages.Add(i);
                }
            }
            else
            {
                pages.Add(-1);
                pages.Add(CurrentPageIndex - 1);
                pages.Add(CurrentPageIndex);
                pages.Add(CurrentPageIndex + 1);
                pages.Add(-1);
                pages.Add(totalPages);
            }
        }

        PageNumbers = pages;
    }

    /// <summary>
    /// 更新 MOD 数量统计
    /// </summary>
    private void UpdateModsCount()
    {
        TotalModsCount = Mods.Count;
        EnabledModsCount = Mods.Count(m => m.IsEnabled);
        DisabledModsCount = Mods.Count(m => !m.IsEnabled);
        UpdatableModsCount = Mods.Count(m => m.HasUpdate);
        BackupModsCount = BackupMods.Count;
    }

    /// <summary>
    /// 切换 MOD 选中状态
    /// </summary>
    [RelayCommand]
    private void ToggleModSelection(SdVMod mod)
    {
        if (mod == null) return;

        var nextSelectedState = !mod.IsSelected;
        SetSelectionState(mod, nextSelectedState, cascadeToChildren: !mod.IsChildMod);
        UpdateSelectionState();
    }

    private static void SetSelectionState(SdVMod mod, bool isSelected, bool cascadeToChildren)
    {
        mod.IsSelected = isSelected;

        if (!cascadeToChildren)
            return;

        foreach (var child in mod.ChildMods)
        {
            SetSelectionState(child, isSelected, cascadeToChildren: true);
        }
    }

    /// <summary>
    /// 更新选择状态
    /// </summary>
    private void UpdateSelectionState()
    {
        var selectionSource = CurrentFilterCategory == ModFilterCategory.Backup ? BackupMods : Mods;
        SelectedCount = selectionSource.Count(m => m.IsSelected);
        SelectedUpdatableCount = CurrentFilterCategory == ModFilterCategory.Backup
            ? 0
            : Mods.Count(m => m.IsSelected && m.HasUpdate);
        ShowSelectionActions = SelectedCount > 0;
        OnPropertyChanged(nameof(HasSelectedUpdatable));
    }

    partial void OnSelectedUpdatableCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasSelectedUpdatable));
    }

    private string? GetCurrentModsPath()
    {
        if (SelectedInstance == null)
            return null;

        if (SelectedInstance.EnableIsolation)
        {
            var instanceFolderName = InstanceIsolationService.GenerateVersionFolderName(
                SelectedInstance.Name, SelectedInstance.IsSMAPIInstance);
            return InstanceIsolationService.GetIsolatedModsPath(SelectedInstance.GamePath, instanceFolderName);
        }

        return Path.Combine(SelectedInstance.GamePath, "Mods");
    }

    /// <summary>
    /// 全选/取消全选
    /// </summary>
    [RelayCommand]
    private void SelectAll()
    {
        if (FilteredMods.Count == 0)
            return;

        var allSelected = FilteredMods.All(m => m.IsSelected);

        foreach (var mod in FilteredMods)
        {
            SetSelectionState(mod, !allSelected, cascadeToChildren: !mod.IsChildMod);
        }

        UpdateSelectionState();
    }

    private List<SdVMod> GetEffectiveSelectedMods(Func<SdVMod, bool>? predicate = null)
    {
        IEnumerable<SdVMod> selected = CurrentFilterCategory == ModFilterCategory.Backup
            ? BackupMods.Where(mod => mod.IsSelected)
            : Mods.Where(mod => mod.IsSelected);

        if (predicate != null)
        {
            selected = selected.Where(predicate);
        }

        if (CurrentFilterCategory == ModFilterCategory.Backup)
        {
            return selected.ToList();
        }

        var selectedParentIds = selected
            .Where(mod => !mod.IsChildMod && !string.IsNullOrWhiteSpace(mod.Id))
            .Select(mod => mod.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return selected
            .Where(mod => !mod.IsChildMod
                          || string.IsNullOrWhiteSpace(mod.ParentModId)
                          || !selectedParentIds.Contains(mod.ParentModId))
            .ToList();
    }

    [RelayCommand]
    private void PreviousPage()
    {
        if (!CanGoPreviousPage)
            return;

        CurrentPageIndex--;
    }

    [RelayCommand]
    private void NextPage()
    {
        if (!CanGoNextPage)
            return;

        CurrentPageIndex++;
    }

    [RelayCommand]
    private void GoToPage(int pageNumber)
    {
        if (pageNumber >= 1 && pageNumber <= TotalPages && pageNumber != CurrentPageIndex)
        {
            CurrentPageIndex = pageNumber;
        }
    }

    /// <summary>
    /// 打开 Mods 文件夹
    /// </summary>
    [RelayCommand]
    private void OpenModsFolder()
    {
        if (SelectedInstance == null) return;

        try
        {
            string modsPath;
            if (SelectedInstance.EnableIsolation)
            {
                var instanceFolderName = InstanceIsolationService.GenerateVersionFolderName(
                    SelectedInstance.Name, SelectedInstance.IsSMAPIInstance);
                modsPath = InstanceIsolationService.GetIsolatedModsPath(SelectedInstance.GamePath, instanceFolderName);
            }
            else
            {
                modsPath = Path.Combine(SelectedInstance.GamePath, "Mods");
            }

            if (!Directory.Exists(modsPath))
            {
                Directory.CreateDirectory(modsPath);
            }

            System.Diagnostics.Process.Start("explorer.exe", modsPath);
        }
        catch (Exception ex)
        {
            SvlMessageBox.Error($"无法打开文件夹：{ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenBackupFolder()
    {
        var modsPath = GetCurrentModsPath();
        if (string.IsNullOrWhiteSpace(modsPath))
            return;

        try
        {
            var backupPath = ModBackupService.EnsureBackupRoot(modsPath);
            System.Diagnostics.Process.Start("explorer.exe", backupPath);
        }
        catch (Exception ex)
        {
            SvlMessageBox.Error($"无法打开备份文件夹：{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task BackupSelectedAsync()
    {
        var modsPath = GetCurrentModsPath();
        if (string.IsNullOrWhiteSpace(modsPath))
            return;

        var selectedMods = GetEffectiveSelectedMods();
        if (selectedMods.Count == 0)
            return;

        var success = 0;
        foreach (var mod in selectedMods)
        {
            if (!string.IsNullOrWhiteSpace(ModBackupService.BackupDirectory(modsPath, mod.ModPath, mod)))
                success++;
        }

        await LoadModsAsync();
        SvlMessageBox.Success($"已备份 {success}/{selectedMods.Count} 个模组");
    }

    [RelayCommand]
    private async Task RestoreSelectedBackupAsync()
    {
        var modsPath = GetCurrentModsPath();
        if (string.IsNullOrWhiteSpace(modsPath))
            return;

        var selectedBackups = BackupMods.Where(m => m.IsSelected).ToList();
        if (selectedBackups.Count == 0)
            return;

        foreach (var backup in selectedBackups)
        {
            var active = ModBackupService.FindActiveMod(modsPath, backup);
            if (active != null)
            {
                var backupTimeText = backup.BackupTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "未知";
                if (!SvlMessageBox.Confirm(
                    $"Mods 中已存在同名/同 UniqueId 模组：\n\n" +
                    $"当前版本: {active.Version}\n" +
                    $"备份版本: {backup.Version}\n" +
                    $"备份时间: {backupTimeText}\n\n" +
                    "是否执行互换替换？（不会覆盖文件）",
                    "确认替换"))
                {
                    continue;
                }
            }

            if (!ModBackupService.SwapBackupWithActive(modsPath, backup, out var message))
            {
                SvlMessageBox.Error($"恢复失败：{backup.Name}\n{message}");
            }
        }

        await LoadModsAsync();
    }

    [RelayCommand]
    private async Task BackupModAsync(SdVMod mod)
    {
        if (mod == null)
            return;

        var modsPath = GetCurrentModsPath();
        if (string.IsNullOrWhiteSpace(modsPath))
            return;

        var backup = ModBackupService.BackupDirectory(modsPath, mod.ModPath, mod);
        if (string.IsNullOrWhiteSpace(backup))
        {
            SvlMessageBox.Error("备份失败，请查看日志");
            return;
        }

        await LoadModsAsync();
        SvlMessageBox.Success($"已备份模组：{mod.Name}");
    }

    [RelayCommand]
    private async Task RestoreBackupModAsync(SdVMod mod)
    {
        if (mod == null)
            return;

        var modsPath = GetCurrentModsPath();
        if (string.IsNullOrWhiteSpace(modsPath))
            return;

        var active = ModBackupService.FindActiveMod(modsPath, mod);
        if (active != null)
        {
            var backupTimeText = mod.BackupTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "未知";
            if (!SvlMessageBox.Confirm(
                $"Mods 中已存在同名/同 UniqueId 模组：\n\n" +
                $"当前版本: {active.Version}\n" +
                $"备份版本: {mod.Version}\n" +
                $"备份时间: {backupTimeText}\n\n" +
                "是否执行互换替换？（不会覆盖文件）",
                "确认替换"))
            {
                return;
            }
        }

        if (!ModBackupService.SwapBackupWithActive(modsPath, mod, out var message))
        {
            SvlMessageBox.Error($"恢复失败：{message}");
            return;
        }

        await LoadModsAsync();
        SvlMessageBox.Success("已完成备份互换替换");
    }

    /// <summary>
    /// 从文件安装 MOD
    /// </summary>
    [RelayCommand]
    private async Task InstallModFromFileAsync()
    {
        if (SelectedInstance == null) return;

        var openFileDialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "MOD 文件 (*.zip)|*.zip|所有文件 (*.*)|*.*",
            Title = "选择 MOD 文件"
        };

        if (openFileDialog.ShowDialog() == true)
        {
            try
            {
                await InstallModFromPathAsync(openFileDialog.FileName, showResultDialog: true, reloadAfterInstall: true);
            }
            catch (Exception ex)
            {
                SvlMessageBox.Error($"安装 MOD 失败：{ex.Message}");
            }
        }
    }

    public async Task<bool> InstallModFromPathAsync(string modPath, bool showResultDialog = true, bool reloadAfterInstall = true)
    {
        if (SelectedInstance == null)
            return false;

        if (ModArchiveDetector.LooksLikeSmapiInstallerSource(modPath))
        {
            await _mainViewModel.CreateSmapiInstanceFromLocalZipAsync(modPath, SelectedInstance);
            return false;
        }

        if (!ModArchiveDetector.LooksLikeModInstallSource(modPath))
        {
            if (showResultDialog)
            {
                SvlMessageBox.Warning("这不是可安装的 Mod 文件。请导入包含 manifest.json 的 Mod 压缩包。", "无法安装");
            }
            return false;
        }

        var modsPath = GetCurrentModsPath();
        if (string.IsNullOrWhiteSpace(modsPath))
            return false;

        if (!Directory.Exists(modsPath))
        {
            Directory.CreateDirectory(modsPath);
        }

        _modManager ??= new ModManager();
        var success = await _modManager.InstallModAsync(modPath, modsPath);

        if (success)
        {
            if (reloadAfterInstall)
            {
                await LoadModsAsync();
            }

            if (showResultDialog)
            {
                SvlMessageBox.Success("MOD 安装成功！");
            }
        }
        else if (showResultDialog)
        {
            SvlMessageBox.Error("MOD 安装失败，请查看日志。");
        }

        return success;
    }

    [RelayCommand]
    private async Task NavigateDependencyAsync(ModDependencyLink dependency)
    {
        if (dependency == null)
            return;

        if (dependency.IsInstalled)
        {
            CurrentFilterCategory = ModFilterCategory.All;
            SearchKeyword = !string.IsNullOrWhiteSpace(dependency.UniqueId)
                ? dependency.UniqueId
                : dependency.InstalledModName;

            FloatingNotificationControl.Show(
                title: "已定位前置 Mod",
                message: $"已筛选出 {dependency.DisplayName}",
                autoCloseDelay: 2500,
                notificationType: NotificationType.Info);
            return;
        }

        var searchItem = await ResolveDependencySearchItemAsync(dependency);
        if (searchItem == null)
        {
            SvlMessageBox.Warning($"未找到 {dependency.DisplayName} 的在线详情页。", "前置 Mod 未找到");
            return;
        }

        await _mainViewModel.OpenModDetailsAsync(searchItem, PageType.VersionSettings);
    }

    private async Task<SVL.Desktop.Models.ModSearchItem?> ResolveDependencySearchItemAsync(ModDependencyLink dependency)
    {
        var directItem = await CreateSearchItemFromDependencyAsync(dependency);
        if (directItem != null)
            return directItem;

        var searchTerms = new List<string>();
        if (!string.IsNullOrWhiteSpace(dependency.DisplayName))
            searchTerms.Add(dependency.DisplayName);
        if (!string.IsNullOrWhiteSpace(dependency.UniqueId) && !searchTerms.Contains(dependency.UniqueId, StringComparer.OrdinalIgnoreCase))
            searchTerms.Add(dependency.UniqueId);

        foreach (var searchTerm in searchTerms)
        {
            var nexusResult = await TryResolveFromNexusAsync(searchTerm, dependency);
            if (nexusResult != null)
                return nexusResult;

            var curseResult = await TryResolveFromCurseforgeAsync(searchTerm, dependency);
            if (curseResult != null)
                return curseResult;
        }

        return null;
    }

    private static async Task<SVL.Desktop.Models.ModSearchItem?> CreateSearchItemFromDependencyAsync(ModDependencyLink dependency)
    {
        if (string.Equals(dependency.Source, "NexusMods", StringComparison.OrdinalIgnoreCase) && long.TryParse(dependency.ProjectId, out var nexusId) && nexusId > 0)
        {
            var detail = await SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsService.GetModDetailsAsync(nexusId);
            if (detail != null)
            {
                var resolvedModId = detail.ModId > 0 ? detail.ModId : detail.ModIdGraphQl;
                return new SVL.Desktop.Models.ModSearchItem
                {
                    Id = $"nexus-{(resolvedModId > 0 ? resolvedModId : nexusId)}",
                    Name = string.IsNullOrWhiteSpace(detail.Name) ? dependency.DisplayName : detail.Name,
                    Author = string.IsNullOrWhiteSpace(detail.Author) ? "NexusMods" : detail.Author,
                    Description = string.IsNullOrWhiteSpace(detail.Description) ? detail.Summary ?? dependency.Note : detail.Description,
                    Summary = string.IsNullOrWhiteSpace(detail.Summary) ? dependency.Note : detail.Summary,
                    Source = "NexusMods",
                    IconUrl = !string.IsNullOrWhiteSpace(detail.PictureUrl) ? detail.PictureUrl : detail.PictureUrlLegacy,
                    DownloadCount = detail.Downloads,
                    LastUpdateTime = detail.UpdatedAt != default ? detail.UpdatedAt.ToString("yyyy-MM-dd") : string.Empty,
                    Url = string.IsNullOrWhiteSpace(dependency.Url) ? $"https://www.nexusmods.com/stardewvalley/mods/{nexusId}" : dependency.Url
                };
            }

            return new SVL.Desktop.Models.ModSearchItem
            {
                Id = $"nexus-{nexusId}",
                Name = dependency.DisplayName,
                Author = "NexusMods",
                Description = string.IsNullOrWhiteSpace(dependency.Note) ? "前置 Mod" : dependency.Note,
                Summary = dependency.Note,
                Source = "NexusMods",
                Url = string.IsNullOrWhiteSpace(dependency.Url) ? $"https://www.nexusmods.com/stardewvalley/mods/{nexusId}" : dependency.Url
            };
        }

        if (string.Equals(dependency.Source, "Curseforge", StringComparison.OrdinalIgnoreCase) && int.TryParse(dependency.ProjectId, out var curseId) && curseId > 0)
        {
            var modInfo = await CurseforgeApiService.GetModInfoAsync(curseId);
            return new SVL.Desktop.Models.ModSearchItem
            {
                Id = $"curse-{curseId}",
                Name = modInfo?.Name ?? dependency.DisplayName,
                Author = modInfo?.Authors?.FirstOrDefault()?.Name ?? "Curseforge",
                Description = modInfo?.Description ?? modInfo?.Summary ?? dependency.Note,
                Summary = modInfo?.Summary ?? dependency.Note,
                Source = "Curseforge",
                IconUrl = modInfo?.Logo?.ThumbnailUrl ?? string.Empty,
                DownloadCount = modInfo?.DownloadCount ?? 0,
                LastUpdateTime = modInfo?.DateModified ?? string.Empty,
                Url = modInfo?.Links?.WebsiteUrl ?? (string.IsNullOrWhiteSpace(dependency.Url) ? $"https://www.curseforge.com/stardewvalley/mods/{curseId}" : dependency.Url)
            };
        }

        return null;
    }

    private static async Task<SVL.Desktop.Models.ModSearchItem?> TryResolveFromNexusAsync(string searchTerm, ModDependencyLink dependency)
    {
        try
        {
            var mods = await SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsService.SearchModsAsync(searchTerm, 1, 20, useCache: false);
            var candidate = mods.FirstOrDefault(m => string.Equals(m.Name, dependency.DisplayName, StringComparison.OrdinalIgnoreCase))
                ?? mods.FirstOrDefault(m => string.Equals(m.Name, searchTerm, StringComparison.OrdinalIgnoreCase))
                ?? mods.FirstOrDefault();

            if (candidate == null)
                return null;

            var modId = candidate.ModId > 0 ? candidate.ModId : candidate.ModIdGraphQl;
            if (modId <= 0)
                return null;

            return new SVL.Desktop.Models.ModSearchItem
            {
                Id = $"nexus-{modId}",
                Name = candidate.Name,
                Author = string.IsNullOrWhiteSpace(candidate.Author) ? "NexusMods" : candidate.Author,
                Description = string.IsNullOrWhiteSpace(candidate.Description) ? candidate.Summary ?? string.Empty : candidate.Description,
                Summary = candidate.Summary ?? string.Empty,
                Source = "NexusMods",
                IconUrl = !string.IsNullOrWhiteSpace(candidate.PictureUrl) ? candidate.PictureUrl : candidate.PictureUrlLegacy,
                DownloadCount = candidate.Downloads,
                LastUpdateTime = candidate.UpdatedAt != default ? candidate.UpdatedAt.ToString("yyyy-MM-dd") : string.Empty,
                Url = $"https://www.nexusmods.com/stardewvalley/mods/{modId}"
            };
        }
        catch (Exception ex)
        {
            Log.Debug($"[VersionSettings] 解析 Nexus 前置失败: {searchTerm}, {ex.Message}");
            return null;
        }
    }

    private static async Task<SVL.Desktop.Models.ModSearchItem?> TryResolveFromCurseforgeAsync(string searchTerm, ModDependencyLink dependency)
    {
        try
        {
            var mods = await CurseforgeApiService.SearchModsAsync(searchTerm, pageSize: 20);
            var candidate = mods.FirstOrDefault(m => string.Equals(m.Name, dependency.DisplayName, StringComparison.OrdinalIgnoreCase))
                ?? mods.FirstOrDefault(m => string.Equals(m.Name, searchTerm, StringComparison.OrdinalIgnoreCase))
                ?? mods.FirstOrDefault();

            if (candidate == null)
                return null;

            return new SVL.Desktop.Models.ModSearchItem
            {
                Id = $"curse-{candidate.Id}",
                Name = candidate.Name,
                Author = "Curseforge",
                Description = candidate.Summary ?? string.Empty,
                Summary = candidate.Summary ?? string.Empty,
                Source = "Curseforge",
                IconUrl = candidate.Logo?.ThumbnailUrl ?? string.Empty,
                DownloadCount = candidate.DownloadCount,
                LastUpdateTime = candidate.DateModified ?? string.Empty,
                Url = candidate.Links?.WebsiteUrl ?? $"https://www.curseforge.com/stardewvalley/mods/{candidate.Slug}"
            };
        }
        catch (Exception ex)
        {
            Log.Debug($"[VersionSettings] 解析 Curseforge 前置失败: {searchTerm}, {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 下载新 MOD（打开下载页面的MOD子页面）
    /// </summary>
    [RelayCommand]
    private void DownloadNewMod()
    {
        // 导航到下载页面
        _mainViewModel.CurrentPage = PageType.Download;

        // 设置下载页面的类别为Mods
        if (_mainViewModel.LeftPanelContent is DownloadLeftViewModel downloadLeftVm)
        {
            downloadLeftVm.SelectedCategory = DownloadCategory.Mods;
        }
    }

    /// <summary>
    /// 启用选中的 MOD
    /// </summary>
    [RelayCommand]
    private async Task EnableSelectedAsync()
    {
        if (CurrentFilterCategory == ModFilterCategory.Backup)
            return;

        if (SelectedInstance == null) return;

        var selectedMods = GetEffectiveSelectedMods(m => !m.IsEnabled);
        if (selectedMods.Count == 0) return;

        try
        {
            string modsPath;
            if (SelectedInstance.EnableIsolation)
            {
                var instanceFolderName = InstanceIsolationService.GenerateVersionFolderName(
                    SelectedInstance.Name, SelectedInstance.IsSMAPIInstance);
                modsPath = InstanceIsolationService.GetIsolatedModsPath(SelectedInstance.GamePath, instanceFolderName);
            }
            else
            {
                modsPath = Path.Combine(SelectedInstance.GamePath, "Mods");
            }

            _modManager ??= new ModManager();
            var dependencyEnableSelection = await PromptEnableDependenciesAsync(selectedMods);
            if (dependencyEnableSelection == null)
                return;

            var dependencySuccessCount = await EnableDependencySelectionAsync(dependencyEnableSelection, modsPath);
            int successCount = 0;

            foreach (var mod in selectedMods)
            {
                if (await _modManager.EnableModAsync(mod.Id, modsPath))
                {
                    successCount++;
                }
            }

            var message = dependencySuccessCount > 0
                ? $"已启用 {successCount}/{selectedMods.Count} 个 MOD，并额外启用 {dependencySuccessCount} 个前置 MOD"
                : $"已启用 {successCount}/{selectedMods.Count} 个 MOD";
            SvlMessageBox.Success(message, "完成");

            await LoadModsAsync();
            ClearSelection();
        }
        catch (Exception ex)
        {
            SvlMessageBox.Error($"启用 MOD 失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 禁用选中的 MOD
    /// </summary>
    [RelayCommand]
    private async Task DisableSelectedAsync()
    {
        if (CurrentFilterCategory == ModFilterCategory.Backup)
            return;

        if (SelectedInstance == null) return;

        var selectedMods = GetEffectiveSelectedMods(m => m.IsEnabled);
        if (selectedMods.Count == 0) return;

        try
        {
            string modsPath;
            if (SelectedInstance.EnableIsolation)
            {
                var instanceFolderName = InstanceIsolationService.GenerateVersionFolderName(
                    SelectedInstance.Name, SelectedInstance.IsSMAPIInstance);
                modsPath = InstanceIsolationService.GetIsolatedModsPath(SelectedInstance.GamePath, instanceFolderName);
            }
            else
            {
                modsPath = Path.Combine(SelectedInstance.GamePath, "Mods");
            }

            _modManager ??= new ModManager();
            int successCount = 0;

            foreach (var mod in selectedMods)
            {
                if (await _modManager.DisableModAsync(mod.Id, modsPath))
                {
                    successCount++;
                }
            }

            SvlMessageBox.Success($"已禁用 {successCount}/{selectedMods.Count} 个 MOD", "完成");

            await LoadModsAsync();
            ClearSelection();
        }
        catch (Exception ex)
        {
            SvlMessageBox.Error($"禁用 MOD 失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 删除选中的 MOD
    /// </summary>
    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (SelectedInstance == null) return;

        if (CurrentFilterCategory == ModFilterCategory.Backup)
        {
            var selectedBackups = BackupMods.Where(m => m.IsSelected).ToList();
            if (selectedBackups.Count == 0)
                return;

            if (!SvlMessageBox.Confirm(
                $"确定要删除选中的 {selectedBackups.Count} 个备份吗？\n\n此操作将移动到系统回收站。",
                "确认删除")) return;

            var deleted = 0;
            foreach (var backup in selectedBackups)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(backup.ModPath) && Directory.Exists(backup.ModPath))
                    {
                        if (ModBackupService.MovePathToRecycleBin(backup.ModPath))
                        {
                            deleted++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"[VersionSettings] 删除备份失败: {backup.ModPath}, {ex.Message}");
                }
            }

            await LoadModsAsync();
            SvlMessageBox.Success($"已删除 {deleted}/{selectedBackups.Count} 个备份");
            return;
        }

        var selectedMods = GetEffectiveSelectedMods();
        if (selectedMods.Count == 0) return;

        if (!SvlMessageBox.Confirm(
            $"确定要删除选中的 {selectedMods.Count} 个 MOD 吗？\n\n此操作无法撤销。",
            "确认删除")) return;

        try
        {
            var modsPath = GetCurrentModsPath();
            if (string.IsNullOrWhiteSpace(modsPath))
                return;

            _modManager ??= new ModManager();
            int successCount = 0;

            foreach (var mod in selectedMods)
            {
                if (await _modManager.UninstallModAsync(mod.Id, modsPath))
                {
                    successCount++;
                }
            }

            SvlMessageBox.Success($"已将 {successCount}/{selectedMods.Count} 个 MOD 移动到回收站", "完成");

            await LoadModsAsync();
            ClearSelection();
        }
        catch (Exception ex)
        {
            SvlMessageBox.Error($"删除 MOD 失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 更新选中的可更新 MOD
    /// </summary>
    [RelayCommand]
    private async Task UpdateSelectedAsync()
    {
        if (CurrentFilterCategory == ModFilterCategory.Backup)
        {
            await RestoreSelectedBackupAsync();
            return;
        }

        if (SelectedInstance == null)
        {
            Log.Warn("[VersionSettings] 批量更新: 未选择实例");
            return;
        }

        var selectedMods = GetEffectiveSelectedMods(m => m.HasUpdate);
        if (selectedMods.Count == 0)
        {
            Log.Warn("[VersionSettings] 批量更新: 没有选中任何需要更新的模组");
            return;
        }

        Log.Info($"[VersionSettings] ========== 开始批量更新 ==========");
        Log.Info($"[VersionSettings] 实例: {SelectedInstance.Name}");
        Log.Info($"[VersionSettings] 选中模组数量: {selectedMods.Count}");

        foreach (var mod in selectedMods)
        {
            Log.Debug($"[VersionSettings]   - {mod.Name} (v{mod.Version} -> v{mod.LatestVersion}) 来源: {mod.UpdateSource}");
        }

        try
        {
            var modsPath = GetCurrentModsPath();
            if (string.IsNullOrWhiteSpace(modsPath))
            {
                Log.Error("[VersionSettings] 批量更新: 无法获取模组路径");
                return;
            }

            Log.Info($"[VersionSettings] 模组路径: {modsPath}");

            if (!Directory.Exists(modsPath))
            {
                Log.Info($"[VersionSettings] 创建模组目录: {modsPath}");
                Directory.CreateDirectory(modsPath);
            }

            // 创建批量更新任务
            Log.Info("[VersionSettings] 创建批量更新任务...");
            var batchUpdateTask = new SVL.Core.Download.ModBatchUpdateTask(
                selectedMods,
                modsPath,
                _modManager ?? new ModManager());

            // 添加任务到下载管理器
            Log.Info("[VersionSettings] 添加任务到下载管理器...");
            await SVL.Core.Download.DownloadManager.Instance.AddTaskAsync(batchUpdateTask);

            // 显示单个浮动通知
            UpdateStatus = $"已开始 {selectedMods.Count} 个模组的更新任务";

            Log.Info($"[VersionSettings] 批量更新任务已添加到下载管理器，任务 ID: {batchUpdateTask.Id}");

            Controls.FloatingNotificationControl.Show(
                title: "批量更新已开始",
                message: $"已开始 {selectedMods.Count} 个模组的更新任务，请在任务管理页面查看进度。",
                autoCloseDelay: 4000,
                notificationType: Controls.NotificationType.Info);

            // 自动导航到任务管理页面
            if (System.Windows.Application.Current.MainWindow is MainWindow mw &&
                mw.DataContext is MainWindowViewModel mvm)
            {
                Log.Info("[VersionSettings] 自动导航到任务管理页面");
                mvm.CurrentPage = PageType.Download;
            }

            ClearSelection();
            Log.Info("[VersionSettings] ========== 批量更新任务已提交 ==========");
        }
        catch (Exception ex)
        {
            UpdateStatus = $"批量更新失败: {ex.Message}";
            Log.Error(ex, "[VersionSettings] 批量更新失败");
        }
    }

    private static DateTime ParseVersionDate(string value)
    {
        if (DateTime.TryParse(value, out var dt))
            return dt;
        return DateTime.MinValue;
    }

    private static long ExtractLongId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return 0;

        if (long.TryParse(raw, out var parsed) && parsed > 0)
            return parsed;

        var match = Regex.Match(raw, @"(\d+)(?!.*\d)");
        if (match.Success && long.TryParse(match.Groups[1].Value, out var extracted))
            return extracted;

        return 0;
    }

    private static int ExtractIntId(string? raw)
    {
        var value = ExtractLongId(raw);
        return value > int.MaxValue ? 0 : (int)value;
    }

    private static string? TryGetUpdateKeyId(SdVMod mod, string sourceName)
    {
        var keys = mod?.Manifest?.UpdateKeys;
        if (keys == null || keys.Count == 0)
            return null;

        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;

            var parts = key.Split(new[] { ':' }, 2);
            if (parts.Length != 2)
                continue;

            if (!string.Equals(parts[0], sourceName, StringComparison.OrdinalIgnoreCase))
                continue;

            var identifier = parts[1].Split('/')[0].Trim();
            if (!string.IsNullOrWhiteSpace(identifier))
                return identifier;
        }

        return null;
    }

    private sealed class SourceCredential
    {
        public string Platform { get; set; } = string.Empty;
        public string ProjectId { get; set; } = string.Empty;
    }

    private static SourceCredential? TryReadSourceCredential(SdVMod mod)
    {
        try
        {
            if (mod == null || string.IsNullOrWhiteSpace(mod.ModPath))
                return null;

            var path = Path.Combine(mod.ModPath, "svl-source.json");
            if (!File.Exists(path))
                return null;

            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var platform = root.TryGetProperty("platform", out var p) ? p.GetString() ?? string.Empty : string.Empty;
            var projectId = root.TryGetProperty("projectId", out var id) ? id.GetString() ?? string.Empty : string.Empty;

            if (string.IsNullOrWhiteSpace(platform) || string.IsNullOrWhiteSpace(projectId))
                return null;

            return new SourceCredential
            {
                Platform = platform,
                ProjectId = projectId
            };
        }
        catch
        {
            return null;
        }
    }

    private static (string Platform, long ProjectId)? ResolveUpdateSource(SdVMod mod)
    {
        var credential = TryReadSourceCredential(mod);
        if (credential != null)
        {
            if (string.Equals(credential.Platform, "Curseforge", StringComparison.OrdinalIgnoreCase))
            {
                var curseId = ExtractLongId(credential.ProjectId);
                if (curseId > 0)
                    return ("Curseforge", curseId);
            }

            if (string.Equals(credential.Platform, "NexusMods", StringComparison.OrdinalIgnoreCase)
                || string.Equals(credential.Platform, "Nexus", StringComparison.OrdinalIgnoreCase))
            {
                var nexusId = ExtractLongId(credential.ProjectId);
                if (nexusId > 0)
                    return ("NexusMods", nexusId);
            }
        }

        var updateCurseId = ExtractLongId(TryGetUpdateKeyId(mod, "Curseforge"));
        if (updateCurseId > 0)
            return ("Curseforge", updateCurseId);

        var updateNexusId = ExtractLongId(TryGetUpdateKeyId(mod, "Nexus"));
        if (updateNexusId > 0)
            return ("NexusMods", updateNexusId);

        return null;
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

        System.Diagnostics.Debug.WriteLine($"[VersionSettings] 已显示 Nexus 浏览器下载引导: {downloadPageUrl}");
    }

    private async Task WaitForTasksAndRefreshAsync(System.Collections.Generic.IEnumerable<string> taskIds)
    {
        var ids = taskIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
        if (ids.Count == 0)
            return;

        var manager = SVL.Core.Download.DownloadManager.Instance;
        var timeout = DateTime.UtcNow.AddMinutes(30);

        while (DateTime.UtcNow < timeout)
        {
            var allFinished = true;
            foreach (var id in ids)
            {
                var task = manager.GetTask(id);
                if (task == null)
                    continue;

                var status = task.Status;
                if (status != SVL.Core.Download.DownloadTaskStatus.Completed
                    && status != SVL.Core.Download.DownloadTaskStatus.Failed
                    && status != SVL.Core.Download.DownloadTaskStatus.Cancelled)
                {
                    allFinished = false;
                    break;
                }
            }

            if (allFinished)
                break;

            await Task.Delay(800);
        }

        await LoadModsAsync();
    }

    /// <summary>
    /// 取消选择
    /// </summary>
    [RelayCommand]
    private void CancelSelection()
    {
        ClearSelection();
    }

    /// <summary>
    /// 清除选择状态
    /// </summary>
    private void ClearSelection()
    {
        foreach (var mod in Mods)
        {
            SetSelectionState(mod, false, cascadeToChildren: !mod.IsChildMod);
        }
        foreach (var mod in BackupMods)
        {
            mod.IsSelected = false;
        }
        UpdateSelectionState();
    }

    /// <summary>
    /// 显示 MOD 详情
    /// </summary>
    [RelayCommand]
    private async void ShowModDetails(SdVMod mod)
    {
        if (mod == null) return;

        if (mod.IsChildMod)
        {
            ShowLocalModDetails(mod);
            return;
        }

        try
        {
            bool navigated = false;

            static SVL.Desktop.Models.ModSearchItem ToNexusSearchItem(SVL.Core.Stardew.ResourceProject.NexusMods.NexusMod info, SdVMod localMod)
            {
                var modId = info.ModId > 0 ? info.ModId : info.ModIdGraphQl;
                return new SVL.Desktop.Models.ModSearchItem
                {
                    Id = $"nexus-{modId}",
                    Name = string.IsNullOrWhiteSpace(info.Name) ? localMod.Name : info.Name,
                    Author = string.IsNullOrWhiteSpace(info.Author) ? "NexusMods" : info.Author,
                    Description = string.IsNullOrWhiteSpace(info.Summary) ? (info.Description ?? "无描述") : info.Summary,
                    Source = "NexusMods",
                    IconUrl = !string.IsNullOrWhiteSpace(info.PictureUrl) ? info.PictureUrl : (info.PictureUrlLegacy ?? ""),
                    DownloadCount = info.Downloads,
                    Category = string.IsNullOrWhiteSpace(info.Category) ? "未分类" : info.Category,
                    LastUpdateTime = info.UpdatedAt != default ? info.UpdatedAt.ToString("yyyy-MM-dd") : "",
                    Url = $"https://www.nexusmods.com/stardewvalley/mods/{modId}"
                };
            }

            static SVL.Desktop.Models.ModSearchItem ToCurseforgeSearchItemById(int curseId, SdVMod localMod)
            {
                return new SVL.Desktop.Models.ModSearchItem
                {
                    Id = $"curse-{curseId}",
                    Name = string.IsNullOrWhiteSpace(localMod.Name) ? $"Curseforge Mod {curseId}" : localMod.Name,
                    Author = string.IsNullOrWhiteSpace(localMod.Author) ? "Curseforge" : localMod.Author,
                    Description = string.IsNullOrWhiteSpace(localMod.Description) ? "无描述" : localMod.Description,
                    Summary = string.IsNullOrWhiteSpace(localMod.Description) ? "无描述" : localMod.Description,
                    Source = "Curseforge",
                    IconUrl = string.Empty,
                    DownloadCount = 0,
                    Category = "未分类",
                    LastUpdateTime = string.Empty,
                    Url = $"https://www.curseforge.com/stardewvalley/mods/{curseId}"
                };
            }

            async Task NavigateToOnlineAsync(SVL.Desktop.Models.ModSearchItem item)
            {
                _mainViewModel.SelectedModSearch = item;
                _mainViewModel.ModDetailsBackPage = PageType.VersionSettings;
                _mainViewModel.OpenVersionSettingsAtModManage = true;
                _mainViewModel.CurrentPage = PageType.ModDetails;

                for (var i = 0; i < 5; i++)
                {
                    if (_mainViewModel.LeftPanelContent is ModDetailsViewModel detailsViewModel)
                    {
                        await detailsViewModel.LoadModAsync(item.Id);
                        return;
                    }

                    await Task.Delay(40);
                }
            }

            var sourceCredential = TryReadSourceCredential(mod);

            // 优先：svl-source 指向的来源（Curseforge/Nexus）
            var preferredCurseId = sourceCredential != null && string.Equals(sourceCredential.Platform, "Curseforge", StringComparison.OrdinalIgnoreCase)
                ? ExtractIntId(sourceCredential.ProjectId)
                : 0;

            if (preferredCurseId <= 0 && string.Equals(mod.UpdateSource, "Curseforge", StringComparison.OrdinalIgnoreCase))
            {
                preferredCurseId = ExtractIntId(mod.CurseforgeProjectId);
            }

            if (preferredCurseId > 0)
            {
                System.Diagnostics.Debug.WriteLine($"[VersionSettings] MOD {mod.Name} has Curseforge ID: {preferredCurseId}");
                await NavigateToOnlineAsync(ToCurseforgeSearchItemById(preferredCurseId, mod));
                navigated = true;
            }

            var preferredNexusId = sourceCredential != null &&
                                   (string.Equals(sourceCredential.Platform, "NexusMods", StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(sourceCredential.Platform, "Nexus", StringComparison.OrdinalIgnoreCase))
                ? ExtractLongId(sourceCredential.ProjectId)
                : 0;

            // 无来源凭证时，回退使用 UpdateKey:Nexus
            if (preferredNexusId <= 0 && sourceCredential == null)
            {
                preferredNexusId = ExtractLongId(TryGetUpdateKeyId(mod, "Nexus"));
            }

            if (!navigated && preferredNexusId > 0)
            {
                System.Diagnostics.Debug.WriteLine($"[VersionSettings] MOD {mod.Name} has NexusMods ID: {preferredNexusId}");

                var nexusMod = await SVL.Core.Stardew.ResourceProject.NexusMods.NexusModsService.GetModDetailsAsync(preferredNexusId, useCache: false);
                if (nexusMod != null)
                {
                    await NavigateToOnlineAsync(ToNexusSearchItem(nexusMod, mod));
                    navigated = true;
                }
                else
                {
                    await NavigateToOnlineAsync(new SVL.Desktop.Models.ModSearchItem
                    {
                        Id = $"nexus-{preferredNexusId}",
                        Name = mod.Name,
                        Author = string.IsNullOrWhiteSpace(mod.Author) ? "NexusMods" : mod.Author,
                        Description = string.IsNullOrWhiteSpace(mod.Description) ? "无描述" : mod.Description,
                        Summary = string.IsNullOrWhiteSpace(mod.Description) ? "无描述" : mod.Description,
                        Source = "NexusMods",
                        Url = $"https://www.nexusmods.com/stardewvalley/mods/{preferredNexusId}"
                    });
                    navigated = true;
                }
            }

            // 如果无法按来源ID跳转在线详情，显示本地详情
            if (!navigated)
            {
                ShowLocalModDetails(mod);
            }
        }
        catch (Exception ex)
        {
            // 检查是否是 401 错误（Token 过期）
            if (Utilities.NexusAuthStateHelper.IsUnauthorized(ex))
            {
                Log.Warn($"[VersionSettings] 打开MOD在线详情失败: Access Token 已过期");
                // 清除过期 Token，但不显示通知（因为本地详情对话框会遮挡）
                Utilities.NexusAuthStateHelper.HandleTokenExpired("ShowModDetails", "VersionSettings", showNotification: false, navigateToSettings: false);
            }
            else
            {
                Log.Warn($"[VersionSettings] 打开MOD在线详情失败: {ex.Message}");
            }

            SvlMessageBox.Warning($"打开在线详情失败：{ex.Message}", "无法打开在线详情");
        }
    }

    /// <summary>
    /// 显示本地 MOD 详情
    /// </summary>
    [RelayCommand]
    private void ShowLocalModDetail(SdVMod mod)
    {
        if (mod == null)
            return;

        ShowLocalModDetails(mod);
    }

    [RelayCommand]
    private void ToggleModGroup(SdVMod mod)
    {
        if (mod == null || !mod.HasChildren)
            return;

        mod.IsGroupExpanded = !mod.IsGroupExpanded;
    }

    /// <summary>
    /// 显示本地MOD详情对话框
    /// </summary>
    private void ShowLocalModDetails(SdVMod mod)
    {
        var mainWindow = System.Windows.Application.Current.MainWindow;
        if (mainWindow != null)
        {
            Controls.LocalModDetailDialog.Show(mainWindow, mod, NavigateDependencyAsync);
        }
    }

    /// <summary>
    /// 打开 MOD 文件位置
    /// </summary>
    [RelayCommand]
    private void OpenModLocation(SdVMod mod)
    {
        if (mod == null || SelectedInstance == null) return;

        try
        {
            if (!string.IsNullOrEmpty(mod.ModPath) && Directory.Exists(mod.ModPath))
            {
                System.Diagnostics.Process.Start("explorer.exe", mod.ModPath);
            }
            else
            {
                SvlMessageBox.Error("MOD 文件夹不存在");
            }
        }
        catch (Exception ex)
        {
            SvlMessageBox.Error($"无法打开文件夹：{ex.Message}");
        }
    }

    /// <summary>
    /// 切换单个 MOD 的启用/禁用状态
    /// </summary>
    [RelayCommand]
    private async Task ToggleModEnabledAsync(SdVMod mod)
    {
        if (mod == null || SelectedInstance == null) return;

        if (CurrentFilterCategory == ModFilterCategory.Backup)
        {
            await RestoreBackupModAsync(mod);
            return;
        }

        try
        {
            string modsPath;
            if (SelectedInstance.EnableIsolation)
            {
                var instanceFolderName = InstanceIsolationService.GenerateVersionFolderName(
                    SelectedInstance.Name, SelectedInstance.IsSMAPIInstance);
                modsPath = InstanceIsolationService.GetIsolatedModsPath(SelectedInstance.GamePath, instanceFolderName);
            }
            else
            {
                modsPath = Path.Combine(SelectedInstance.GamePath, "Mods");
            }

            _modManager ??= new ModManager();

            if (mod.IsEnabled)
            {
                await _modManager.DisableModAsync(mod.Id, modsPath);
            }
            else
            {
                var dependencyEnableSelection = await PromptEnableDependenciesAsync([mod]);
                if (dependencyEnableSelection == null)
                    return;

                await EnableDependencySelectionAsync(dependencyEnableSelection, modsPath);
                await _modManager.EnableModAsync(mod.Id, modsPath);
            }

            await LoadModsAsync();
        }
        catch (Exception ex)
        {
            SvlMessageBox.Error($"操作失败：{ex.Message}");
        }
    }

    private async Task<IReadOnlyList<DependencyEnableOption>?> PromptEnableDependenciesAsync(IReadOnlyCollection<SdVMod> targetMods)
    {
        var dependencyGroups = CollectDisabledDependencyGroups(targetMods);
        if (dependencyGroups.Count == 0)
            return [];

        IReadOnlyList<DependencyEnableOption>? selection = null;
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var owner = System.Windows.Application.Current.MainWindow;
            selection = Controls.DependencyEnableDialog.Show(
                owner,
                dependencyGroups,
                $"以下前置 Mod 当前已安装但未启用，现已按目标 Mod 分组显示。启用目标 Mod 前，请勾选需要一并启用的前置；取消则中止本次启用操作。");
        });

        return selection;
    }

    private List<DependencyEnableGroup> CollectDisabledDependencyGroups(IReadOnlyCollection<SdVMod> targetMods)
    {
        var installedByUniqueId = Mods
            .Where(mod => !string.IsNullOrWhiteSpace(mod.UniqueId))
            .GroupBy(mod => mod.UniqueId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        return targetMods
            .OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)
            .Select(targetMod =>
            {
                var dependencies = (targetMod.DisplayDependencies ?? [])
                    .Where(dep => dep != null && dep.IsRequired && !string.IsNullOrWhiteSpace(dep.UniqueId))
                    .Select(dep => installedByUniqueId.TryGetValue(dep.UniqueId, out var installedMod) ? new { dep, installedMod } : null)
                    .Where(item => item != null && item.installedMod != null && !item.installedMod.IsEnabled)
                    .GroupBy(item => item!.installedMod.UniqueId, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new DependencyEnableOption
                    {
                        ModId = group.First()!.installedMod.Id,
                        DisplayName = group.First()!.installedMod.Name,
                        UniqueId = group.Key,
                        IsSelected = true
                    })
                    .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new DependencyEnableGroup
                {
                    TargetModName = targetMod.Name,
                    Dependencies = dependencies
                };
            })
            .Where(group => group.Dependencies.Count > 0)
            .ToList();
    }

    private async Task<int> EnableDependencySelectionAsync(IReadOnlyList<DependencyEnableOption> selection, string modsPath)
    {
        if (selection == null || selection.Count == 0)
            return 0;

        _modManager ??= new ModManager();
        var successCount = 0;
        foreach (var dependency in selection.Where(item => item.IsSelected))
        {
            if (await _modManager.EnableModAsync(dependency.ModId, modsPath))
            {
                successCount++;
            }
        }

        return successCount;
    }

    /// <summary>
    /// 删除单个 MOD
    /// </summary>
    [RelayCommand]
    private async Task DeleteModAsync(SdVMod mod)
    {
        if (mod == null || SelectedInstance == null) return;

        if (CurrentFilterCategory == ModFilterCategory.Backup)
        {
            if (!SvlMessageBox.Confirm(
                $"确定要删除备份 \"{mod.Name}\" 吗？",
                "确认删除备份")) return;

            try
            {
                if (!string.IsNullOrWhiteSpace(mod.ModPath) && Directory.Exists(mod.ModPath))
                {
                    if (!ModBackupService.MovePathToRecycleBin(mod.ModPath))
                    {
                        SvlMessageBox.Error("删除备份失败：无法移动到回收站");
                        return;
                    }
                }

                await LoadModsAsync();
                return;
            }
            catch (Exception ex)
            {
                SvlMessageBox.Error($"删除备份失败：{ex.Message}");
                return;
            }
        }

        if (!SvlMessageBox.Confirm(
            $"确定要删除 MOD \"{mod.Name}\" 吗？\n\n将移动到系统回收站。",
            "确认删除")) return;

        try
        {
            string modsPath;
            if (SelectedInstance.EnableIsolation)
            {
                var instanceFolderName = InstanceIsolationService.GenerateVersionFolderName(
                    SelectedInstance.Name, SelectedInstance.IsSMAPIInstance);
                modsPath = InstanceIsolationService.GetIsolatedModsPath(SelectedInstance.GamePath, instanceFolderName);
            }
            else
            {
                modsPath = Path.Combine(SelectedInstance.GamePath, "Mods");
            }

            _modManager ??= new ModManager();

            if (await _modManager.UninstallModAsync(mod.Id, modsPath))
            {
                SvlMessageBox.Success($"已将 MOD \"{mod.Name}\" 移动到回收站", "完成");

                await LoadModsAsync();
            }
            else
            {
                SvlMessageBox.Error("删除 MOD 失败");
            }
        }
        catch (Exception ex)
        {
            SvlMessageBox.Error($"删除 MOD 失败：{ex.Message}");
        }
    }
}
