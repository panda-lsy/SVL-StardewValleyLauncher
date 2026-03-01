using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SVL.Core.Download;
using SVL.Core.Download.NexusMods;
using SVL.Core.Logging;
using SVL.Core.Stardew.Instance;
using SVL.Core.Stardew.Mod;
using SVL.Desktop.Controls;

namespace SVL.Desktop.ViewModels;

/// <summary>
/// 下载子页面类型
/// </summary>
public enum DownloadSubPageType
{
    SMAPI,
    Mods,
    Modpacks,
    Utilities,
    ModDetails
}

public enum PageType
{
    Launch,
    Mods,
    Download,
    DownloadFailure,
    Instances,
    Modpacks,
    Settings,
    VersionSettings,
    ModDetails
}

public partial class MainWindowViewModel : ObservableObject
{
    private ModManager _modManager = new ModManager();

    [ObservableProperty]
    private IStardewInstance? _selectedInstance;

    [ObservableProperty]
    private List<IStardewInstance> _instances = [];

    [ObservableProperty]
    private SVL.Core.Stardew.Instance.GamePathInfo? _selectedVersionSettingsInstance;

    [ObservableProperty]
    private List<SdVMod> _mods = [];

    [ObservableProperty]
    private SdVMod? _selectedMod;

    [ObservableProperty]
    private SVL.Desktop.Models.ModSearchItem? _selectedModSearch;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private PageType _currentPage = PageType.Launch;

    [ObservableProperty]
    private object? _leftPanelContent;

    [ObservableProperty]
    private object? _rightPanelContent;

    [ObservableProperty]
    private DownloadSubPageType _currentDownloadSubPage = DownloadSubPageType.SMAPI;

    [ObservableProperty]
    private PageType _modDetailsBackPage = PageType.Download;

    [ObservableProperty]
    private bool _openVersionSettingsAtModManage;

    private TaskCompletionSource<bool> _loadComplete = new TaskCompletionSource<bool>();

    public MainWindowViewModel()
    {
        // 确保 NXM URL 处理器已初始化（不依赖于下载页面是否被浏览过）
        DownloadRightViewModel.InitializeNxmHandler();

        // 订阅下载任务事件
        DownloadManager.Instance.TaskAdded += OnDownloadTaskAdded;
        DownloadManager.Instance.TaskFailed += OnDownloadTaskFailed;
        DownloadManager.Instance.TaskCompleted += OnDownloadTaskCompleted;
        DownloadManager.Instance.TaskUpdated += OnDownloadTaskUpdated;

        // 设置下载管理器的引用
        DownloadManagerViewModel.Instance.SetMainWindowViewModel(this);
    }

    public async Task InitializeAsync()
    {
        await LoadDataAsync();
        UpdatePageContent(CurrentPage);
    }

    public async Task WaitForLoadAsync()
    {
        await _loadComplete.Task;
    }

    private async Task LoadDataAsync()
    {
        try
        {
            // 实例已在 ApplicationService.InitializeAsync() 中加载，直接使用
            var instanceManager = new SVL.Core.Stardew.Instance.InstanceManager();
            Instances = instanceManager.Instances.ToList();

            if (Instances.Count > 0)
            {
                SelectedInstance = Instances[0];
                var modsPath = System.IO.Path.Combine(SelectedInstance.Path, "Mods");
                Mods = await _modManager.LoadModsAsync(modsPath);
            }

            StatusMessage = $"已加载 {Instances.Count} 个实例，{Mods.Count} 个 Mod";
            _loadComplete.TrySetResult(true);
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败: {ex.Message}";
            _loadComplete.TrySetResult(false);
        }
    }

    partial void OnInstancesChanged(List<IStardewInstance> value)
    {
        StatusMessage = $"已加载 {value.Count} 个实例";
    }

    partial void OnModsChanged(List<SdVMod> value)
    {
        StatusMessage = $"已加载 {value.Count} 个 Mod";
    }

    partial void OnCurrentPageChanged(PageType value)
    {
        UpdatePageContent(value);
        ResetTransientInputStateAfterNavigation();
    }

    private void ResetTransientInputStateAfterNavigation()
    {
        try
        {
            // 使用 Dispatcher 确保在布局/绑定刷新后执行
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    // 释放任何残留鼠标捕获（例如 ComboBox/Popup 的特殊状态）
                    System.Windows.Input.Mouse.Capture(null);
                }
                catch { }

                try
                {
                    // 清除键盘焦点，避免输入控件/弹出层占用输入导致首击只用于“取消激活”
                    System.Windows.Input.Keyboard.ClearFocus();
                }
                catch { }

                try
                {
                    Application.Current.MainWindow?.Focus();
                }
                catch { }
            }), System.Windows.Threading.DispatcherPriority.Input);
        }
        catch
        {
            // 忽略：导航不应因此失败
        }
    }

    private void UpdatePageContent(PageType page)
    {
        switch (page)
        {
            case PageType.Launch:
                LeftPanelContent = new LaunchLeftViewModel(this);
                RightPanelContent = new LaunchRightViewModel(this);
                break;
            case PageType.Mods:
                LeftPanelContent = new ModsLeftViewModel(this);
                RightPanelContent = new ModsRightViewModel(this);
                break;
            case PageType.Download:
                LeftPanelContent = new DownloadLeftViewModel(this);
                UpdateDownloadRightPanel();
                break;
            case PageType.DownloadFailure:
                // 任务状态页面占据整个区域
                LeftPanelContent = new TaskStatusViewModel(this);
                RightPanelContent = null;
                break;
            case PageType.Instances:
                // 使用新的版本选择界面
                LeftPanelContent = new InstanceSelectorViewModel(this);
                RightPanelContent = null; // InstanceSelectorView 占据整个区域
                break;
            case PageType.Modpacks:
                LeftPanelContent = new ModpacksLeftViewModel(this);
                RightPanelContent = new ModpacksRightViewModel(this);
                break;
            case PageType.Settings:
                // 设置页面占据整个区域
                LeftPanelContent = new SettingsViewModel();
                RightPanelContent = null;
                break;
            case PageType.VersionSettings:
                LeftPanelContent = new VersionSettingsViewModel(this);
                if (OpenVersionSettingsAtModManage && LeftPanelContent is VersionSettingsViewModel versionSettingsViewModel)
                {
                    versionSettingsViewModel.SelectedPage = VersionSettingsPageType.ModManage;
                    OpenVersionSettingsAtModManage = false;
                }
                RightPanelContent = null; // 版本设置占据整个区域
                break;
            case PageType.ModDetails:
                // MOD 详情页面占据整个区域
                LeftPanelContent = new ModDetailsViewModel();
                RightPanelContent = null;
                break;
        }
    }

    public async Task LaunchSelectedInstanceAsync()
    {
        if (SelectedInstance == null)
        {
            StatusMessage = "未选择实例";
            return;
        }

        try
        {
            StatusMessage = $"正在启动 {SelectedInstance.Name}...";
            var success = await SVL.Core.Stardew.Launch.LaunchOrchestrator.LaunchInstanceAsync(SelectedInstance);

            if (success)
            {
                StatusMessage = $"已启动 {SelectedInstance.Name}";
            }
            else
            {
                StatusMessage = $"启动失败：{SelectedInstance.Name}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"启动异常：{ex.Message}";
        }
    }

    [RelayCommand]
    private void NavigateToLaunch()
    {
        CurrentPage = PageType.Launch;
    }

    [RelayCommand]
    private void NavigateToMods()
    {
        CurrentPage = PageType.Mods;
    }

    [RelayCommand]
    private void NavigateToDownload()
    {
        CurrentPage = PageType.Download;
    }

    [RelayCommand]
    private void NavigateToInstances()
    {
        CurrentPage = PageType.Instances;
    }

    [RelayCommand]
    private void NavigateToModpacks()
    {
        CurrentPage = PageType.Modpacks;
    }

    [RelayCommand]
    private void NavigateToSettings()
    {
        CurrentPage = PageType.Settings;
    }

    [RelayCommand]
    private void NavigateToVersionSettings()
    {
        CurrentPage = PageType.VersionSettings;
    }

    /// <summary>
    /// 返回启动主页面
    /// </summary>
    [RelayCommand]
    private void NavigateToHome()
    {
        CurrentPage = PageType.Launch;
    }

    /// <summary>
    /// 智能返回命令
    /// - MOD详情 → 返回下载页面
    /// - 版本选择、版本设置、下载失败 → 返回启动主页
    /// </summary>
    [RelayCommand]
    private void NavigateBack()
    {
        switch (CurrentPage)
        {
            case PageType.ModDetails:
                CurrentPage = ModDetailsBackPage;
                break;
            case PageType.Instances:
            case PageType.VersionSettings:
            case PageType.DownloadFailure:
                // 版本选择、版本设置、下载失败返回主页
                CurrentPage = PageType.Launch;
                break;
            default:
                // 默认返回主页
                CurrentPage = PageType.Launch;
                break;
        }
    }

    /// <summary>
    /// 处理下载任务失败事件
    /// </summary>
    private void OnDownloadTaskFailed(DownloadTask task, Exception exception)
    {
        // 确保在 UI 线程执行
        Application.Current.Dispatcher.Invoke(() =>
        {
            // 导航到失败页面（会创建新的 TaskStatusViewModel）
            CurrentPage = PageType.DownloadFailure;

            // 使用 Dispatcher 确保新 ViewModel 已创建并绑定后再设置失败信息
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (LeftPanelContent is TaskStatusViewModel statusViewModel)
                {
                    statusViewModel.SetFailureInfo(
                        task.Name,
                        task.StatusMessage,
                        exception.Message,
                        exception,
                        task.Id
                    );
                }
                else
                {
                    Log.Warn($"[MainWindowViewModel] LeftPanelContent 不是 TaskStatusViewModel: {LeftPanelContent?.GetType().Name}");
                }
            }));
        });
    }

    /// <summary>
    /// 处理下载任务添加事件（显示开始下载的提示）
    /// </summary>
    private void OnDownloadTaskAdded(DownloadTask task)
    {
        // 批量更新任务不显示通知（已有专门的批量更新通知）
        if (task.Type == DownloadTaskType.Modpack && task.Name == "MOD 批量更新")
        {
            return;
        }

        // 确保在 UI 线程执行
        Application.Current.Dispatcher.Invoke(() =>
        {
            // 显示下载开始提示
            FloatingNotificationControl.Show(
                title: "开始下载",
                message: $"📥 正在下载 {task.Name}...\n点击右下角「任务状态」按钮查看进度",
                autoCloseDelay: 3000
            );
        });
    }

    /// <summary>
    /// 处理下载任务完成事件
    /// </summary>
    private void OnDownloadTaskCompleted(DownloadTask task)
    {
        // 确保在 UI 线程执行
        Application.Current.Dispatcher.Invoke(() =>
        {
            // 根据任务状态显示不同的提示
            if (task.Status == DownloadTaskStatus.Cancelled)
            {
                // 用户取消操作 - 显示取消状态页面
                ShowTaskStatusCancelled(task);
                ShowInstallationCancelledAlert(task);
            }
            else if (task.Status == DownloadTaskStatus.Completed)
            {
                // 安装完成
                // 如果当前在任务状态页面，自动跳转回下载页面
                if (CurrentPage == PageType.DownloadFailure)
                {
                    CurrentPage = PageType.Download;
                }

                // 检查是否有失败的模组（针对整合包安装任务）
                if (task is CurseforgeModpackDownloadTask modpackTask && modpackTask.FailedMods.Any())
                {
                    ShowModpackFailureDialog(modpackTask);
                }
                else if (task is NexusCollectionWizardTask wizardTask && wizardTask.FailedMods.Any())
                {
                    ShowModpackFailureDialog(wizardTask);
                }
                else if (task is LocalCurseforgeModpackInstallTask localModpackTask && localModpackTask.FailedMods.Any())
                {
                    ShowModpackFailureDialog(localModpackTask);
                }

                ShowInstallationCompletedAlert(task);
            }
            else if (task.Status == DownloadTaskStatus.Failed)
            {
                // 任务失败 - 显示失败状态页面
                ShowTaskStatusFailed(task, new Exception(task.StatusMessage ?? "任务执行失败"));
            }
        });
    }

    /// <summary>
    /// 显示安装完成提示
    /// </summary>
    private void ShowInstallationCompletedAlert(DownloadTask task)
    {
        // 使用浮窗通知替代阻塞式MessageBox
        FloatingNotificationControl.Show(
            title: "安装成功",
            message: $"✓ {task.Name} 安装完成！",
            autoCloseDelay: 5000,
            onClosed: null
        );
    }

    /// <summary>
    /// 显示用户取消操作提示
    /// </summary>
    private void ShowInstallationCancelledAlert(DownloadTask task)
    {
        FloatingNotificationControl.Show(
            title: "用户取消操作",
            message: $"ⓘ {task.Name} 已取消",
            autoCloseDelay: 3000,
            onClosed: null,
            notificationType: NotificationType.Warning
        );
    }

    /// <summary>
    /// 显示任务取消状态页面
    /// </summary>
    private void ShowTaskStatusCancelled(DownloadTask task)
    {
        CurrentPage = PageType.DownloadFailure;
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (LeftPanelContent is TaskStatusViewModel statusViewModel)
            {
                statusViewModel.SetCancelledInfo(task.Name, task.Id);
            }
        }));
    }

    /// <summary>
    /// 显示任务失败状态页面
    /// </summary>
    private void ShowTaskStatusFailed(DownloadTask task, Exception exception)
    {
        CurrentPage = PageType.DownloadFailure;
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (LeftPanelContent is TaskStatusViewModel statusViewModel)
            {
                statusViewModel.SetFailureInfo(
                    task.Name,
                    task.StatusMessage ?? "任务执行失败",
                    "请查看日志了解详细信息",
                    exception,
                    task.Id
                );
            }
        }));
    }

    /// <summary>
    /// 显示整合包失败模组对话框（Curseforge）
    /// </summary>
    private void ShowModpackFailureDialog(CurseforgeModpackDownloadTask modpackTask)
    {
        ShowModpackFailureDialog(modpackTask.FailedMods.ToList(), modpackTask.Name);
    }

    /// <summary>
    /// 显示整合包失败模组对话框（Nexus Collection）
    /// </summary>
    private void ShowModpackFailureDialog(NexusCollectionWizardTask wizardTask)
    {
        ShowModpackFailureDialog(wizardTask.FailedMods.ToList(), wizardTask.Name);
    }

    /// <summary>
    /// 显示整合包失败模组对话框（本地 Curseforge 整合包）
    /// </summary>
    private void ShowModpackFailureDialog(LocalCurseforgeModpackInstallTask localModpackTask)
    {
        ShowModpackFailureDialog(localModpackTask.FailedMods.ToList(), localModpackTask.Name);
    }

    /// <summary>
    /// 显示整合包失败模组对话框（通用）
    /// </summary>
    private void ShowModpackFailureDialog(List<FailedModInfo> failedMods, string taskName)
    {
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var owner = Application.Current.MainWindow;
                var dialog = new SVL.Desktop.Controls.ModpackFailureDialog(
                    failedMods,
                    taskName
                );
                if (owner != null)
                {
                    dialog.Owner = owner;
                }
                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                SVL.Core.Logging.Log.Error(ex, "[MainWindowViewModel] 显示失败模组对话框失败");
            }
        }));
    }

    /// <summary>
    /// 更新下载页面的右侧面板（根据子页面类型）
    /// </summary>
    public void UpdateDownloadRightPanel()
    {
        switch (CurrentDownloadSubPage)
        {
            case DownloadSubPageType.SMAPI:
            case DownloadSubPageType.Utilities:
                RightPanelContent = new DownloadRightViewModel(this);
                break;
            case DownloadSubPageType.Mods:
                RightPanelContent = new ModSearchViewModel();
                break;
            case DownloadSubPageType.Modpacks:
                {
                    var modpackSearchVm = new ModpackSearchViewModel();
                    RightPanelContent = modpackSearchVm;
                    _ = modpackSearchVm.InitializeAsync(); // 加载热门整合包
                    break;
                }
            case DownloadSubPageType.ModDetails:
                RightPanelContent = new ModDetailsViewModel();
                break;
        }
    }

    /// <summary>
    /// 处理下载任务更新事件（实时更新进度）
    /// </summary>
    private void OnDownloadTaskUpdated(DownloadTask task)
    {
        // 确保在 UI 线程执行
        Application.Current.Dispatcher.Invoke(() =>
        {
            // 如果当前在任务状态页面，实时更新进度信息
            // 注意：只更新进行中的任务，不覆盖失败或取消状态
            if (CurrentPage == PageType.DownloadFailure &&
                LeftPanelContent is TaskStatusViewModel statusViewModel &&
                task.Status != DownloadTaskStatus.Failed &&
                task.Status != DownloadTaskStatus.Cancelled)  // 不覆盖失败或取消状态
            {
                statusViewModel.SetProgressInfo(task);
            }
        });
    }

    #region 整合包拖放导入

    /// <summary>
    /// 处理整合包拖放（显示对话框）
    /// </summary>
    public void HandleModpackDrop(string filePath)
    {
        try
        {
            Log.Info($"[MainWindowViewModel] 处理整合包拖放: {filePath}");

            // 显示导入对话框
            var mainWindow = Application.Current.MainWindow as MainWindow;
            if (mainWindow == null) return;

            mainWindow.ApplyBlurEffect();

            var result = ModpackDropDialog.Show(mainWindow, filePath);

            mainWindow.RemoveBlurEffect();

            if (result != null && result.IsValid && result.DetectionResult != null)
            {
                // 根据整合包类型启动安装任务
                StartModpackInstall(result);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MainWindowViewModel] 处理整合包拖放失败");

            FloatingNotificationControl.Show(
                title: "导入失败",
                message: $"无法导入整合包: {ex.Message}",
                autoCloseDelay: 5000,
                notificationType: NotificationType.Error);
        }
    }

    /// <summary>
    /// 处理已有的整合包对话框结果（用于从按钮调用）
    /// </summary>
    public void HandleModpackDialogResult(ModpackDropDialogViewModel result)
    {
        if (result != null && result.IsValid && result.DetectionResult != null)
        {
            StartModpackInstall(result);
        }
    }

    /// <summary>
    /// 启动整合包安装任务
    /// </summary>
    private async void StartModpackInstall(ModpackDropDialogViewModel dialogViewModel)
    {
        if (dialogViewModel.DetectionResult == null || dialogViewModel.SelectedGamePath == null)
            return;

        var detectionResult = dialogViewModel.DetectionResult;
        var instanceName = dialogViewModel.InstanceName;
        var gamePath = dialogViewModel.SelectedGamePath.GamePath;

        if (detectionResult.Type == Core.Modpack.ModpackType.Curseforge)
        {
            // Curseforge 整合包 - 使用本地文件导入任务
            Log.Info($"[MainWindowViewModel] 启动 Curseforge 整合包安装: {instanceName}");

            var task = new LocalCurseforgeModpackInstallTask(
                dialogViewModel.ModpackFilePath,
                instanceName,
                gamePath);

            await DownloadManager.Instance.AddTaskAsync(task);

            // 显示通知，任务已添加到任务管理器
            FloatingNotificationControl.Show(
                title: "开始导入",
                message: $"正在导入整合包 {instanceName}...\n点击右下角「任务状态」按钮查看进度",
                autoCloseDelay: 3000);
        }
        else if (detectionResult.Type == Core.Modpack.ModpackType.NexusCollection)
        {
            // Nexus Collection - 传递原始文件路径，Task 会自行解压并查找 collection.json
            Log.Info($"[MainWindowViewModel] 启动 Nexus Collection 安装: {instanceName}");

            // 计算 targetModsPath（通过版本隔离服务获取正确路径）
            var targetModsPath = SVL.Core.Stardew.Instance.InstanceIsolationService.GetIsolatedModsPath(gamePath, instanceName);

            // 创建 Collection Wizard 任务（传入原始 7z/zip 文件路径）
            var wizardTask = new NexusCollectionWizardTask(
                dialogViewModel.ModpackFilePath,
                instanceName,
                gamePath,
                targetModsPath);

            await DownloadManager.Instance.AddTaskAsync(wizardTask);

            // 显示通知，任务已添加到任务管理器
            FloatingNotificationControl.Show(
                title: "开始导入",
                message: $"正在导入 Nexus Collection {instanceName}...\n点击右下角「任务状态」按钮查看进度",
                autoCloseDelay: 3000);
        }
        else if (detectionResult.Type == Core.Modpack.ModpackType.SVL)
        {
            // SVL 整合包 - 使用 SVL 专用安装任务
            Log.Info($"[MainWindowViewModel] 启动 SVL 整合包安装: {instanceName}");

            var targetModsPath = SVL.Core.Stardew.Instance.InstanceIsolationService.GetIsolatedModsPath(gamePath, instanceName);

            var svlTask = new SVL.Core.Download.SvlModpackInstallTask(
                dialogViewModel.ModpackFilePath,
                instanceName,
                gamePath,
                targetModsPath);

            // 订阅 NexusMods Token 过期事件：仅通知，不跳转
            svlTask.NexusTokenExpired += scene =>
            {
                Utilities.NexusAuthStateHelper.HandleTokenExpired(
                    scene, "SvlModpackInstallTask",
                    showNotification: true, navigateToSettings: false);
            };

            // 订阅 NexusMods 非 Premium 用户事件：仅通知，不跳转
            svlTask.NexusPremiumRequired += scene =>
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    FloatingNotificationControl.Show(
                        title: "NexusMods 非 Premium 用户",
                        message: "NexusMods API 下载需要 Premium 会员。\n已自动切换到其他下载源。",
                        autoCloseDelay: 5000,
                        notificationType: Controls.NotificationType.Warning);
                });
            };

            await DownloadManager.Instance.AddTaskAsync(svlTask);

            FloatingNotificationControl.Show(
                title: "开始导入",
                message: $"正在导入 SVL 整合包 {instanceName}...\n点击右下角「任务状态」按钮查看进度",
                autoCloseDelay: 3000);
        }
    }

    #endregion
}
