using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SVL.Core.Download;
using SVL.Core.Logging;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace SVL.Desktop.ViewModels;

/// <summary>
/// 下载类别枚举
/// </summary>
public enum DownloadCategory
{
    SMAPI,
    Mods,
    Modpacks,
    Utilities
}

/// <summary>
/// 下载页面左侧ViewModel，管理类别选择和下载任务列表
/// </summary>
public partial class DownloadLeftViewModel : ObservableObject
{
    private MainWindowViewModel _mainViewModel;
    private bool _isInitializing;

    public DownloadLeftViewModel(MainWindowViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;

        // 初始化选中类别：与右侧（CurrentDownloadSubPage）保持一致
        _isInitializing = true;
        SelectedCategory = mainViewModel.CurrentDownloadSubPage switch
        {
            DownloadSubPageType.Mods => DownloadCategory.Mods,
            DownloadSubPageType.Modpacks => DownloadCategory.Modpacks,
            DownloadSubPageType.Utilities => DownloadCategory.Utilities,
            _ => DownloadCategory.SMAPI
        };
        _isInitializing = false;

        // 获取下载管理器并订阅事件
        var manager = DownloadManager.Instance;
        manager.TaskAdded += OnTaskAdded;
        manager.TaskCompleted += OnTaskCompleted;
        manager.TaskFailed += OnTaskFailed;

        LoadDownloadTasks();
    }

    [ObservableProperty]
    private DownloadCategory _selectedCategory;

    [ObservableProperty]
    private ObservableCollection<DownloadTaskViewModel> _downloadTasks = new();

    partial void OnSelectedCategoryChanged(DownloadCategory value)
    {
        if (_isInitializing)
            return;

        ApplyCategory(value);
    }

    private void ApplyCategory(DownloadCategory value)
    {
        // 更新主 ViewModel 的子页面状态
        switch (value)
        {
            case DownloadCategory.SMAPI:
                _mainViewModel.CurrentDownloadSubPage = DownloadSubPageType.SMAPI;
                break;
            case DownloadCategory.Mods:
                _mainViewModel.CurrentDownloadSubPage = DownloadSubPageType.Mods;
                break;
            case DownloadCategory.Modpacks:
                _mainViewModel.CurrentDownloadSubPage = DownloadSubPageType.Modpacks;
                break;
            case DownloadCategory.Utilities:
                _mainViewModel.CurrentDownloadSubPage = DownloadSubPageType.Utilities;
                break;
        }

        // 更新右侧面板
        _mainViewModel.UpdateDownloadRightPanel();

        // 如果是下载右面板，通知更新内容
        if (_mainViewModel.RightPanelContent is DownloadRightViewModel rightViewModel)
        {
            rightViewModel.UpdateContent(value);
        }
    }

    [RelayCommand]
    private void SelectCategory(DownloadCategory category)
    {
        if (SelectedCategory == category)
        {
            // 值未变化时也强制刷新（避免"左侧已经选中 SMAPI，但右侧还停留在 Mods"这种状态）
            ApplyCategory(category);
            return;
        }

        SelectedCategory = category;
    }

    /// <summary>
    /// 选择任务（切换到任务状态页面）
    /// </summary>
    [RelayCommand]
    private void SelectTask(DownloadTaskViewModel taskViewModel)
    {
        if (taskViewModel == null)
            return;

        Log.Info($"[DownloadLeftViewModel] 选择任务: {taskViewModel.Name}");

        // 导航到任务状态页面
        _mainViewModel.CurrentPage = PageType.DownloadFailure;

        // 使用 Dispatcher 确保 UI 更新后再设置数据
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_mainViewModel.LeftPanelContent is TaskStatusViewModel statusViewModel)
            {
                // 获取原始任务
                var task = DownloadManager.Instance.GetAllTasks()
                    .FirstOrDefault(t => string.Equals(t.Id, taskViewModel.Id, StringComparison.Ordinal));

                if (task != null)
                {
                    statusViewModel.SetProgressInfo(task);
                    Log.Info($"[DownloadLeftViewModel] 已设置任务进度信息: {task.Name}");
                }
                else
                {
                    Log.Warn($"[DownloadLeftViewModel] 未找到任务: {taskViewModel.Id}");
                }
            }
            else
            {
                Log.Warn($"[DownloadLeftViewModel] LeftPanelContent 类型不匹配: {_mainViewModel.LeftPanelContent?.GetType().Name}");
            }
        }));
    }

    /// <summary>
    /// 加载下载任务列表
    /// </summary>
    private void LoadDownloadTasks()
    {
        var manager = DownloadManager.Instance;
        var allTasks = manager.GetAllTasks();
        Log.Info($"[DownloadLeftViewModel] LoadDownloadTasks: 总任务数={allTasks.Count}");

        foreach (var t in allTasks)
        {
            Log.Info($"[DownloadLeftViewModel]   任务: {t.Name}, 状态={t.Status}");
        }

        var tasks = allTasks
            .Where(t => t.Status == DownloadTaskStatus.Pending ||
                       t.Status == DownloadTaskStatus.Downloading ||
                       t.Status == DownloadTaskStatus.Installing ||
                       t.Status == DownloadTaskStatus.WaitingConfirmation)  // 包含等待确认状态
            .Select(t => new DownloadTaskViewModel(t))
            .ToList();

        Log.Info($"[DownloadLeftViewModel] 过滤后活动任务数: {tasks.Count}");

        DownloadTasks.Clear();
        foreach (var task in tasks)
        {
            DownloadTasks.Add(task);
        }
    }

    /// <summary>
    /// 任务添加时的事件处理
    /// </summary>
    private void OnTaskAdded(DownloadTask task)
    {
        var viewModel = new DownloadTaskViewModel(task);
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            DownloadTasks.Add(viewModel);
        });
    }

    /// <summary>
    /// 任务完成时的事件处理
    /// </summary>
    private void OnTaskCompleted(DownloadTask task)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var taskToRemove = DownloadTasks.FirstOrDefault(t => t.Id == task.Id);
            if (taskToRemove != null)
            {
                DownloadTasks.Remove(taskToRemove);
            }
        });
    }

    /// <summary>
    /// 任务失败时的事件处理
    /// </summary>
    private void OnTaskFailed(DownloadTask task, Exception ex)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var taskViewModel = DownloadTasks.FirstOrDefault(t => t.Id == task.Id);
            if (taskViewModel != null)
            {
                // 延迟移除任务，让用户看到失败状态（2秒后）
                // DownloadTaskViewModel 的定时器会自动刷新状态显示
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(2)
                };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    if (DownloadTasks.Contains(taskViewModel))
                    {
                        DownloadTasks.Remove(taskViewModel);
                    }
                };
                timer.Start();
            }
        });
    }
}
