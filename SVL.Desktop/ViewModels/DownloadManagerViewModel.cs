using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SVL.Core.Download;
using SVL.Core.Download.NexusMods;
using SVL.Core.Logging;

namespace SVL.Desktop.ViewModels;

/// <summary>
/// 下载管理器ViewModel（全屏下载窗口）
/// </summary>
public partial class DownloadManagerViewModel : ObservableObject
{
    private static DownloadManagerViewModel? _instance;

    public static DownloadManagerViewModel Instance => _instance ??= new DownloadManagerViewModel();

    private readonly DownloadManager _manager = DownloadManager.Instance;
    private MainWindowViewModel? _mainViewModel;

    private DownloadManagerViewModel()
    {
        LoadTasks();

        // 订阅管理器事件
        _manager.TaskAdded += OnTaskAdded;
        _manager.TaskUpdated += OnTaskUpdated;
        _manager.TaskCompleted += OnTaskCompleted;
        _manager.TaskFailed += OnTaskFailed;
        _manager.TaskListChanged += OnTaskListChanged;
    }

    /// <summary>
    /// 设置主窗口ViewModel
    /// </summary>
    public void SetMainWindowViewModel(MainWindowViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
    }

    [ObservableProperty]
    private ObservableCollection<DownloadTaskViewModel> _activeTasks = new();

    [ObservableProperty]
    private ObservableCollection<DownloadTaskViewModel> _completedTasks = new();

    [ObservableProperty]
    private int _activeTaskCount;

    [ObservableProperty]
    private bool _isVisible;

    /// <summary>
    /// 选中的任务（用于高亮显示）
    /// </summary>
    [ObservableProperty]
    private DownloadTaskViewModel? _selectedTask;

    /// <summary>
    /// 显示下载窗口（公共方法）
    /// </summary>
    public void ShowWindow()
    {
        IsVisible = true;
        LoadTasks();
    }

    /// <summary>
    /// 隐藏下载窗口（公共方法）
    /// </summary>
    public void HideWindow()
    {
        IsVisible = false;
    }

    /// <summary>
    /// 显示下载窗口
    /// </summary>
    [RelayCommand]
    private void Show()
    {
        ShowWindow();
    }

    /// <summary>
    /// 隐藏下载窗口
    /// </summary>
    [RelayCommand]
    private void Hide()
    {
        HideWindow();
    }

    /// <summary>
    /// 切换显示状态
    /// </summary>
    [RelayCommand]
    private void Toggle()
    {
        if (_mainViewModel == null)
        {
            Log.Warn("[DownloadManagerViewModel] MainWindowViewModel is not set");
            return;
        }

        // 获取当前活动的任务
        var activeTasks = _manager.GetActiveTasks();
        if (activeTasks.Count > 0)
        {
            Log.Info($"[DownloadManagerViewModel] 导航到任务状态页面，活动任务数: {activeTasks.Count}");

            // 导航到任务状态页面并显示第一个任务的进度
            _mainViewModel.CurrentPage = PageType.DownloadFailure;

            // 使用 Dispatcher 确保 UI 更新后再设置数据
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_mainViewModel.LeftPanelContent is TaskStatusViewModel statusViewModel)
                {
                    statusViewModel.SetProgressInfo(activeTasks[0]);
                    Log.Info($"[DownloadManagerViewModel] 已设置任务进度信息: {activeTasks[0].Name}");
                }
                else
                {
                    Log.Warn($"[DownloadManagerViewModel] LeftPanelContent 类型不匹配: {_mainViewModel.LeftPanelContent?.GetType().Name}");
                }
            }));
        }
        else
        {
            Log.Info("[DownloadManagerViewModel] No active tasks to show");
        }
    }

    /// <summary>
    /// 取消任务
    /// </summary>
    [RelayCommand]
    private void CancelTask(string taskId)
    {
        _manager.CancelTask(taskId);
    }

    /// <summary>
    /// 选择并切换到任务状态页面
    /// </summary>
    [RelayCommand]
    private void SelectTask(DownloadTaskViewModel? taskViewModel)
    {
        if (taskViewModel == null || _mainViewModel == null)
            return;

        // 清除所有任务的选中状态
        foreach (var task in ActiveTasks)
        {
            task.IsSelected = false;
        }
        foreach (var task in CompletedTasks)
        {
            task.IsSelected = false;
        }

        // 设置新任务为选中
        taskViewModel.IsSelected = true;
        SelectedTask = taskViewModel;

        // 获取实际的下载任务
        var actualTask = _manager.GetAllTasks()
            .FirstOrDefault(t => string.Equals(t.Id, taskViewModel.Id, StringComparison.Ordinal));

        if (actualTask != null)
        {
            Log.Info($"[DownloadManagerViewModel] 切换到任务: {actualTask.Name}");

            // 导航到任务状态页面
            _mainViewModel.CurrentPage = PageType.DownloadFailure;

            // 使用双重 BeginInvoke 确保页面已完全切换
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_mainViewModel.LeftPanelContent is TaskStatusViewModel statusViewModel)
                    {
                        statusViewModel.SetProgressInfo(actualTask);
                        Log.Info($"[DownloadManagerViewModel] 已设置任务进度信息: {actualTask.Name}");
                    }
                    else
                    {
                        Log.Warn($"[DownloadManagerViewModel] LeftPanelContent 类型不匹配: {_mainViewModel.LeftPanelContent?.GetType().Name}");
                    }
                }));
            }));
        }
    }

    /// <summary>
    /// 移除任务
    /// </summary>
    [RelayCommand]
    private void RemoveTask(string taskId)
    {
        _manager.RemoveTask(taskId);
        LoadTasks();
    }

    /// <summary>
    /// 清空已完成任务
    /// </summary>
    [RelayCommand]
    private void ClearCompleted()
    {
        _manager.ClearCompletedTasks();
        LoadTasks();
    }

    /// <summary>
    /// 刷新任务列表
    /// </summary>
    [RelayCommand]
    private void Refresh()
    {
        LoadTasks();
    }

    /// <summary>
    /// 加载任务
    /// </summary>
    private void LoadTasks()
    {
        // 确保在UI线程上执行
        if (Application.Current.Dispatcher.CheckAccess())
        {
            LoadTasksInternal();
        }
        else
        {
            Application.Current.Dispatcher.Invoke(LoadTasksInternal);
        }
    }

    /// <summary>
    /// 内部加载任务方法（已在UI线程上）
    /// </summary>
    private void LoadTasksInternal()
    {
        var active = _manager.GetActiveTasks();
        var completed = _manager.GetCompletedTasks();

        ActiveTasks.Clear();
        CompletedTasks.Clear();

        foreach (var task in active)
        {
            ActiveTasks.Add(new DownloadTaskViewModel(task));
        }

        foreach (var task in completed)
        {
            CompletedTasks.Add(new DownloadTaskViewModel(task));
        }

        ActiveTaskCount = active.Count;

        // 降低日志级别：每次刷新不需要记录日志
        // Log.Info($"[DownloadManagerViewModel] LoadTasksInternal: ActiveTaskCount={ActiveTaskCount}, Active tasks={active.Count}, Completed tasks={completed.Count}");
    }

    private void OnTaskAdded(DownloadTask task)
    {
        InvokeOnUI(() =>
        {
            LoadTasks();

            // 如果是批量更新任务或 Collection Wizard 任务，自动选中它
            if (task is ModBatchUpdateTask || task is NexusCollectionWizardTask)
            {
                Log.Info($"[DownloadManagerViewModel] 自动选中 {task.GetType().Name} 任务: {task.Name}");
                AutoSelectTask(task.Id);
            }
        });
    }

    /// <summary>
    /// 自动选中指定任务
    /// </summary>
    private void AutoSelectTask(string taskId)
    {
        var taskViewModel = ActiveTasks.FirstOrDefault(t => string.Equals(t.Id, taskId, StringComparison.Ordinal));
        if (taskViewModel == null)
        {
            // 新任务可能还没有出现在列表中，延迟一下
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                taskViewModel = ActiveTasks.FirstOrDefault(t => string.Equals(t.Id, taskId, StringComparison.Ordinal));
                if (taskViewModel != null && _mainViewModel != null)
                {
                    SelectTask(taskViewModel);
                }
            }));
            return;
        }

        if (_mainViewModel != null)
        {
            SelectTask(taskViewModel);
        }
    }

    private void OnTaskUpdated(DownloadTask task)
    {
        // 实时更新任务状态（包括进度、失败状态等）
        InvokeOnUI(() => LoadTasks());
    }

    private void OnTaskCompleted(DownloadTask task)
    {
        InvokeOnUI(() =>
        {
            LoadTasks();

            // 如果是整合包安装任务，触发实例列表刷新
            if (task.Type == DownloadTaskType.Modpack)
            {
                GlobalEvents.OnInstanceChanged(string.Empty); // 使用空字符串触发完整刷新
            }
        });
    }

    private void OnTaskFailed(DownloadTask task, Exception exception)
    {
        InvokeOnUI(() =>
        {
            LoadTasks();

            // 失败时优先将状态页切到当前失败任务，避免继续显示无关任务。
            if (_mainViewModel?.LeftPanelContent is TaskStatusViewModel statusViewModel)
            {
                statusViewModel.SetFailureInfo(
                    taskName: task.Name,
                    errorMessage: task.StatusMessage ?? exception.Message,
                    detailMessage: exception.Message,
                    exception: exception,
                    taskId: task.Id);
            }

            // 如果是整合包安装任务，触发实例列表刷新（可能删除了不完整的版本目录）
            if (task.Type == DownloadTaskType.Modpack)
            {
                GlobalEvents.OnInstanceChanged(string.Empty); // 使用空字符串触发完整刷新
            }
        });
    }

    private void OnTaskListChanged()
    {
        InvokeOnUI(() =>
        {
            LoadTasks();

            // 检查是否有已取消的整合包任务，触发实例列表刷新
            var cancelledModpackTasks = _manager.GetCompletedTasks()
                .Where(t => t.Type == DownloadTaskType.Modpack && t.Status == DownloadTaskStatus.Cancelled);

            foreach (var task in cancelledModpackTasks)
            {
                GlobalEvents.OnInstanceChanged(string.Empty);
                break; // 只需要触发一次刷新
            }
        });
    }

    /// <summary>
    /// 在UI线程上执行操作
    /// </summary>
    private void InvokeOnUI(Action action)
    {
        if (Application.Current.Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            Application.Current.Dispatcher.Invoke(action);
        }
    }
}
