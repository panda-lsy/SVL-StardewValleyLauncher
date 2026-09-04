using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Win32;
using SVL.Avalonia.Models;
using SVL.Core.Platform.Abstractions;
using System;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Text;

namespace SVL.Avalonia.ViewModels;

public abstract partial class FeaturePageViewModelBase : ObservableObject
{
    public abstract string Title { get; }

    public abstract string Description { get; }
}

public sealed partial class TaskStatusPageViewModel : FeaturePageViewModelBase
{
    public override string Title => "任务状态";
    public override string Description => "统一任务视图：左栏多任务列表（含进度条），右栏选中任务详情与操作栏。";

    /// <summary>任务日志（实时滚动）。</summary>
    public ObservableCollection<string> TaskLogs { get; } = [];

    /// <summary>任务列表（与 DownloadPage.DownloadTasks 同步，左栏绑定）。</summary>
    public ObservableCollection<DownloadTaskItem> Tasks { get; } = [];

    /// <summary>重试报告历史路径列表。</summary>
    public ObservableCollection<string> RetryReportHistory { get; } = [];

    /// <summary>冲突预览条目。</summary>
    public ObservableCollection<string> ConflictPreviewItems { get; } = [];

    /// <summary>建议操作列表。</summary>
    public ObservableCollection<string> SuggestedActions { get; } = [];

    /// <summary>当前选中任务（右栏绑定到它的详情与操作）。null 表示无选中。</summary>
    [ObservableProperty]
    private DownloadTaskItem? _selectedTask;

    [ObservableProperty]
    private bool _isEmptyState;

    [ObservableProperty]
    private string _adviceTitle = "建议操作";

    [ObservableProperty]
    private bool _isFailedState;

    [ObservableProperty]
    private bool _isCompletedState;

    [ObservableProperty]
    private bool _isCancelledState;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _failureSummary = "暂无失败信息";

    [ObservableProperty]
    private string _latestRetryReportPath = string.Empty;

    [ObservableProperty]
    private int _activeTasksCount;

    [ObservableProperty]
    private int _finishedTasksCount;

    [ObservableProperty]
    private string _selectedTaskHint = "未选择任务";

    // 兼容旧 View 绑定（部分卡片仍引用 CurrentTaskName/CurrentTaskStatus）
    public string CurrentTaskName => SelectedTask?.Name ?? "暂无任务";
    public string CurrentTaskStatus => SelectedTask?.Status ?? "-";
    public bool CanRetryFailedItems => SelectedTask?.CanRetry ?? false;
    public bool HasSelectedTask => SelectedTask != null;

    // 代理属性：避免 XAML 中 SelectedTask.XXX 绑定在 null 时报错
    public string SelectedTaskSpeedText => SelectedTask?.SpeedText ?? string.Empty;
    public bool SelectedTaskHasSpeedInfo => SelectedTask?.HasSpeedInfo ?? false;
    public string SelectedTaskDownloadedSizeText => SelectedTask?.DownloadedSizeText ?? string.Empty;
    public string SelectedTaskTotalSizeText => SelectedTask?.TotalSizeText ?? string.Empty;
    public string SelectedTaskEtaText => SelectedTask?.EtaText ?? string.Empty;
    public bool SelectedTaskHasSubProgress => SelectedTask?.HasSubProgress ?? false;
    public string SelectedTaskSubProgressText => SelectedTask?.SubProgressText ?? string.Empty;
    public int SelectedTaskSubProgress => SelectedTask?.SubProgress ?? 0;
    public bool SelectedTaskCanCancel => SelectedTask?.CanCancel ?? false;
    public bool SelectedTaskCanRetry => SelectedTask?.CanRetry ?? false;
    public bool SelectedTaskHasInstalledDirectory => SelectedTask?.HasInstalledDirectory ?? false;
    public bool SelectedTaskHasReportPath => SelectedTask?.HasReportPath ?? false;
    public bool SelectedTaskHasRetryReportPath => SelectedTask?.HasRetryReportPath ?? false;

    public bool IsRunningState => HasSelectedTask && !IsFailedState && !IsCompletedState && !IsCancelledState;
    public bool ShowMainStateContent => !IsEmptyState;
    public bool HasConflictPreview => ConflictPreviewItems.Count > 0;
    public bool HasRetryReportHistory => RetryReportHistory.Count > 0;
    public bool HasLatestRetryReport => !string.IsNullOrWhiteSpace(LatestRetryReportPath);

    /// <summary>是否有可清空的已完成任务（左栏"清空已完成"按钮启用条件）。</summary>
    public bool CanClearCompleted => FinishedTasksCount > 0;

    // 任务操作事件（由 MainWindowViewModel 订阅，转发到 DownloadPageViewModel 执行）
    public event Action<DownloadTaskItem>? CancelTaskRequested;
    public event Action<DownloadTaskItem>? RetryTaskRequested;
    public event Action<DownloadTaskItem>? RemoveTaskRequested;
    public event Action<DownloadTaskItem>? OpenDirectoryRequested;
    public event Action<DownloadTaskItem>? OpenReportRequested;
    public event Action<DownloadTaskItem>? OpenRetryReportRequested;
    public event Action? ClearCompletedRequested;
    public event Action? RetryFailedItemsRequested;
    public event Action? NavigateToDownloadRequested;

    /// <summary>同步任务列表（由 MainWindowViewModel 在 DownloadTasks 集合变化时调用）。</summary>
    public void SyncTasks(IEnumerable<DownloadTaskItem> tasks)
    {
        Tasks.Clear();
        foreach (var task in tasks)
        {
            Tasks.Add(task);
        }

        // 若当前选中任务已不在列表中，清空选中
        if (SelectedTask != null && !Tasks.Contains(SelectedTask))
        {
            SetCurrentTask(null);
        }
    }

    /// <summary>设置选中任务并刷新右栏状态展示。</summary>
    public void SetCurrentTask(DownloadTaskItem? task)
    {
        SelectedTask = task;
        if (task == null)
        {
            IsEmptyState = ActiveTasksCount + FinishedTasksCount <= 0;
            OnPropertyChanged(nameof(CurrentTaskName));
            OnPropertyChanged(nameof(CurrentTaskStatus));
            OnPropertyChanged(nameof(CanRetryFailedItems));
            return;
        }

        IsEmptyState = false;
        SelectedTaskHint = $"已选择任务: {task.Name} ({task.Status})";
        SetConflictPreview(task.ConflictPreviewItems);
        RefreshStatusPresentation(task);
        OnPropertyChanged(nameof(CurrentTaskName));
        OnPropertyChanged(nameof(CurrentTaskStatus));
        OnPropertyChanged(nameof(CanRetryFailedItems));
    }

    /// <summary>兼容旧调用：按名称+状态文本设置当前任务（仅当无法直接传 task 时使用）。</summary>
    public void SetCurrentTask(string name, string status)
    {
        // 优先在 SelectedTask 中匹配；找不到则仅更新文本展示
        if (SelectedTask != null && string.Equals(SelectedTask.Name, name, StringComparison.Ordinal))
        {
            SetCurrentTask(SelectedTask);
            return;
        }

        OnPropertyChanged(nameof(CurrentTaskName));
        OnPropertyChanged(nameof(CurrentTaskStatus));
        if (!IsEmptyState)
        {
            RefreshStatusPresentationFromText(name, status);
        }
    }

    public void SetCanRetryFailedItems(bool canRetry)
    {
        OnPropertyChanged(nameof(CanRetryFailedItems));
        RefreshSuggestedActions();
    }

    public void AddLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        TaskLogs.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        while (TaskLogs.Count > 200)
        {
            TaskLogs.RemoveAt(0);
        }
    }

    public void AddRetryReport(string reportPath)
    {
        if (string.IsNullOrWhiteSpace(reportPath) || RetryReportHistory.Contains(reportPath))
        {
            return;
        }

        RetryReportHistory.Insert(0, reportPath);
        LatestRetryReportPath = reportPath;
        while (RetryReportHistory.Count > 30)
        {
            RetryReportHistory.RemoveAt(RetryReportHistory.Count - 1);
        }

        OnPropertyChanged(nameof(HasRetryReportHistory));
        OnPropertyChanged(nameof(HasLatestRetryReport));
        RefreshSuggestedActions();
    }

    public void SetConflictPreview(IEnumerable<string> previewItems)
    {
        ConflictPreviewItems.Clear();
        foreach (var item in previewItems)
        {
            ConflictPreviewItems.Add(item);
        }
        OnPropertyChanged(nameof(HasConflictPreview));
    }

    public void UpdateTaskOverview(int activeCount, int finishedCount, string selectedHint)
    {
        ActiveTasksCount = Math.Max(0, activeCount);
        FinishedTasksCount = Math.Max(0, finishedCount);
        SelectedTaskHint = string.IsNullOrWhiteSpace(selectedHint) ? "未选择任务" : selectedHint;
        OnPropertyChanged(nameof(CanClearCompleted));

        if (ActiveTasksCount + FinishedTasksCount <= 0)
        {
            SetEmptyState();
            return;
        }

        if (IsEmptyState)
        {
            IsEmptyState = false;
        }
    }

    public void SetEmptyState()
    {
        SelectedTask = null;
        FailureSummary = "暂无失败信息";
        ProgressPercent = 0;
        IsFailedState = false;
        IsCompletedState = false;
        IsCancelledState = false;
        IsEmptyState = true;

        SuggestedActions.Clear();
        AdviceTitle = "建议操作";
        SuggestedActions.Add("前往下载页发起新任务");
        SuggestedActions.Add("下载过程中可通过顶部“任务”或右下角按钮查看进度");

        ConflictPreviewItems.Clear();
        OnPropertyChanged(nameof(CurrentTaskName));
        OnPropertyChanged(nameof(CurrentTaskStatus));
        OnPropertyChanged(nameof(CanRetryFailedItems));
        OnPropertyChanged(nameof(HasConflictPreview));
        OnPropertyChanged(nameof(IsRunningState));
        OnPropertyChanged(nameof(ShowMainStateContent));
    }

    /// <summary>根据选中任务刷新右栏状态卡片（失败/运行/完成/取消）。</summary>
    private void RefreshStatusPresentation(DownloadTaskItem task)
    {
        IsFailedState = task.IsFailed;
        IsCompletedState = task.IsCompleted;
        IsCancelledState = task.IsCancelled;

        FailureSummary = IsFailedState ? (string.IsNullOrWhiteSpace(task.FailedDetails) ? task.Status : task.FailedDetails) : "暂无失败信息";
        ProgressPercent = task.IsFinished && task.Progress >= 100 ? 100 : Math.Clamp(task.Progress, 0, 100);

        OnPropertyChanged(nameof(IsRunningState));
        RefreshSuggestedActions();
    }

    /// <summary>兼容旧文本路径的状态刷新（无 task 引用时）。</summary>
    private void RefreshStatusPresentationFromText(string name, string status)
    {
        IsFailedState = status.Contains("失败", StringComparison.Ordinal);
        IsCompletedState = !IsFailedState && status.Contains("完成", StringComparison.Ordinal);
        IsCancelledState = !IsFailedState && !IsCompletedState && status.Contains("取消", StringComparison.Ordinal);

        FailureSummary = IsFailedState ? status : "暂无失败信息";
        var match = Regex.Match(status, "(\\d{1,3}(?:\\.\\d+)?)\\s*%", RegexOptions.CultureInvariant);
        if (match.Success && double.TryParse(match.Groups[1].Value, out var percent))
        {
            ProgressPercent = Math.Clamp(percent, 0, 100);
        }
        else if (IsCompletedState)
        {
            ProgressPercent = 100;
        }
        else
        {
            ProgressPercent = 0;
        }

        OnPropertyChanged(nameof(IsRunningState));
        RefreshSuggestedActions();
    }

    private void RefreshSuggestedActions()
    {
        SuggestedActions.Clear();

        if (IsFailedState)
        {
            AdviceTitle = "失败后的建议";
            SuggestedActions.Add("点击右栏“重试”重新尝试，或“移除”清理后重新发起。");
            SuggestedActions.Add("若仍失败，检查代理地址与网络连通性。");
            if (HasLatestRetryReport)
            {
                SuggestedActions.Add("打开最新重试报告，对比本次与上次失败差异。");
            }
            return;
        }

        if (IsCompletedState)
        {
            AdviceTitle = "任务已完成";
            SuggestedActions.Add("可在右栏点击“打开目录”查看安装结果，或“打开报告”查看详情。");
            if (HasConflictPreview)
            {
                SuggestedActions.Add("本次有冲突预览记录，可复核安装策略是否符合预期。");
            }
            return;
        }

        if (IsCancelledState)
        {
            AdviceTitle = "任务已取消";
            SuggestedActions.Add("如需继续，可返回下载页重新入队。");
            return;
        }

        AdviceTitle = "进行中的建议";
        SuggestedActions.Add("保持窗口开启，SVL 会持续更新下载与安装状态。");
        SuggestedActions.Add("如果长时间无进展，可检查代理设置、磁盘空间和权限。");
    }

    [RelayCommand]
    private void SelectTask(DownloadTaskItem? task)
    {
        SetCurrentTask(task);
    }

    [RelayCommand]
    private void ClearLogs()
    {
        TaskLogs.Clear();
        AddLog("已清空任务日志");
    }

    [RelayCommand]
    private async Task CopyLogsAsync()
    {
        if (TaskLogs.Count == 0)
        {
            AddLog("暂无日志可复制");
            return;
        }

        var text = string.Join(Environment.NewLine, TaskLogs);
        var clipboard = TopLevel.GetTopLevel(Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null)?.Clipboard;

        if (clipboard != null)
        {
            await clipboard.SetTextAsync(text);
            AddLog($"已复制 {TaskLogs.Count} 条日志到剪贴板");
        }
        else
        {
            AddLog("剪贴板不可用，无法复制日志");
        }
    }

    [RelayCommand]
    private void OpenLatestRetryReport()
    {
        if (!HasLatestRetryReport)
        {
            AddLog("暂无可打开的重试报告");
            return;
        }
        OpenPath(LatestRetryReportPath);
    }

    [RelayCommand]
    private void OpenRetryReport(string? reportPath)
    {
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            AddLog("报告路径无效");
            return;
        }
        OpenPath(reportPath);
    }

    private void OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            AddLog($"已打开: {path}");
        }
        catch (Exception ex)
        {
            AddLog($"打开失败: {ex.Message}");
        }
    }

    // 任务操作命令：转发事件到 MainWindowViewModel → DownloadPageViewModel
    [RelayCommand]
    private void CancelTask(DownloadTaskItem? task)
    {
        if (task == null) return;
        CancelTaskRequested?.Invoke(task);
        AddLog($"已请求取消任务: {task.Name}");
    }

    [RelayCommand]
    private void RetryTask(DownloadTaskItem? task)
    {
        if (task == null || !task.CanRetry)
        {
            AddLog("当前任务不可重试");
            return;
        }
        RetryTaskRequested?.Invoke(task);
        AddLog($"已请求重试任务: {task.Name}");
    }

    [RelayCommand]
    private void RemoveTask(DownloadTaskItem? task)
    {
        if (task == null) return;
        RemoveTaskRequested?.Invoke(task);
        AddLog($"已请求移除任务: {task.Name}");
    }

    [RelayCommand]
    private void OpenDirectory(DownloadTaskItem? task)
    {
        if (task == null) return;
        OpenDirectoryRequested?.Invoke(task);
    }

    [RelayCommand]
    private void OpenReport(DownloadTaskItem? task)
    {
        if (task == null) return;
        OpenReportRequested?.Invoke(task);
    }

    [RelayCommand]
    private void OpenRetryReportForTask(DownloadTaskItem? task)
    {
        if (task == null) return;
        OpenRetryReportRequested?.Invoke(task);
    }

    [RelayCommand]
    private void ClearCompleted()
    {
        ClearCompletedRequested?.Invoke();
    }

    [RelayCommand]
    private void RetryFailedItems()
    {
        RetryFailedItemsRequested?.Invoke();
        AddLog("已发起失败项重试请求");
    }

    [RelayCommand]
    private void GoToDownload()
    {
        NavigateToDownloadRequested?.Invoke();
    }

    partial void OnIsEmptyStateChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowMainStateContent));
    }

    partial void OnSelectedTaskChanged(DownloadTaskItem? value)
    {
        // 订阅选中任务内部属性变化，让重试/取消按钮与进度等代理属性即时刷新（无需切换卡片）
        if (_previousSelectedTask != null)
        {
            _previousSelectedTask.PropertyChanged -= OnSelectedTaskItemPropertyChanged;
        }
        _previousSelectedTask = value;
        if (value != null)
        {
            value.PropertyChanged += OnSelectedTaskItemPropertyChanged;
        }

        OnPropertyChanged(nameof(CurrentTaskName));
        OnPropertyChanged(nameof(CurrentTaskStatus));
        OnPropertyChanged(nameof(CanRetryFailedItems));
        OnPropertyChanged(nameof(HasSelectedTask));
        // 通知所有代理属性
        OnPropertyChanged(nameof(SelectedTaskSpeedText));
        OnPropertyChanged(nameof(SelectedTaskHasSpeedInfo));
        OnPropertyChanged(nameof(SelectedTaskDownloadedSizeText));
        OnPropertyChanged(nameof(SelectedTaskTotalSizeText));
        OnPropertyChanged(nameof(SelectedTaskEtaText));
        OnPropertyChanged(nameof(SelectedTaskHasSubProgress));
        OnPropertyChanged(nameof(SelectedTaskSubProgressText));
        OnPropertyChanged(nameof(SelectedTaskSubProgress));
        OnPropertyChanged(nameof(SelectedTaskCanCancel));
        OnPropertyChanged(nameof(SelectedTaskCanRetry));
        OnPropertyChanged(nameof(SelectedTaskHasInstalledDirectory));
        OnPropertyChanged(nameof(SelectedTaskHasReportPath));
        OnPropertyChanged(nameof(SelectedTaskHasRetryReportPath));
    }

    private DownloadTaskItem? _previousSelectedTask;

    private void OnSelectedTaskItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DownloadTaskItem.CanRetry):
                OnPropertyChanged(nameof(SelectedTaskCanRetry));
                OnPropertyChanged(nameof(CanRetryFailedItems));
                break;
            case nameof(DownloadTaskItem.CanCancel):
                OnPropertyChanged(nameof(SelectedTaskCanCancel));
                break;
            case nameof(DownloadTaskItem.TaskState):
            case nameof(DownloadTaskItem.Status):
                OnPropertyChanged(nameof(CurrentTaskStatus));
                OnPropertyChanged(nameof(SelectedTaskCanRetry));
                OnPropertyChanged(nameof(SelectedTaskCanCancel));
                break;
            case nameof(DownloadTaskItem.Progress):
            case nameof(DownloadTaskItem.SpeedText):
            case nameof(DownloadTaskItem.DownloadedSizeText):
            case nameof(DownloadTaskItem.TotalSizeText):
            case nameof(DownloadTaskItem.EtaText):
            case nameof(DownloadTaskItem.SubProgressText):
                OnPropertyChanged(nameof(SelectedTaskSpeedText));
                OnPropertyChanged(nameof(SelectedTaskHasSpeedInfo));
                OnPropertyChanged(nameof(SelectedTaskDownloadedSizeText));
                OnPropertyChanged(nameof(SelectedTaskTotalSizeText));
                OnPropertyChanged(nameof(SelectedTaskEtaText));
                OnPropertyChanged(nameof(SelectedTaskHasSubProgress));
                OnPropertyChanged(nameof(SelectedTaskSubProgressText));
                break;
        }
    }

    partial void OnFinishedTasksCountChanged(int value)
    {
        OnPropertyChanged(nameof(CanClearCompleted));
    }
}

public sealed partial class ModSearchPageViewModel : FeaturePageViewModelBase
{
    private readonly Services.RemoteCatalogService _catalogService;
    private bool _isInitialized;
    private int _currentPage = 1;

    public override string Title => "Mod 搜索";
    public override string Description => "对应 WPF ModSearchView，承载 Nexus/Curseforge 搜索与筛选。";

    public event Action<Models.CatalogResourceIdentity>? OpenDetailsRequested;

    public ObservableCollection<string> Sources { get; } = ["全部", "NexusMods", "Curseforge"];

    public ObservableCollection<Models.ModSearchResultItem> Results { get; } = [];

    public string Query { get; set; } = string.Empty;

    public string SelectedSource { get; set; } = "全部";

    [ObservableProperty]
    private string _status = "加载热门模组中...";

    [ObservableProperty]
    private bool _hasNextPage;

    [ObservableProperty]
    private bool _hasPreviousPage;

    [ObservableProperty]
    private string _pageInfoText = "第 1 页";

    public ModSearchPageViewModel(Services.RemoteCatalogService catalogService)
    {
        _catalogService = catalogService;
    }

    /// <summary>
    /// 页面显示时自动调用，首次加载热门 Mod
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        await LoadPopularModsAsync();
    }

    [RelayCommand]
    private async Task Search()
    {
        _currentPage = 1;
        if (string.IsNullOrWhiteSpace(Query))
        {
            await LoadPopularModsAsync();
            return;
        }

        await LoadSearchResultsAsync();
    }

    [RelayCommand(CanExecute = nameof(HasNextPage))]
    private async Task NextPageAsync()
    {
        if (!HasNextPage) return;
        _currentPage++;
        if (string.IsNullOrWhiteSpace(Query))
        {
            await LoadPopularModsAsync();
        }
        else
        {
            await LoadSearchResultsAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(HasPreviousPage))]
    private async Task PreviousPageAsync()
    {
        if (!HasPreviousPage || _currentPage <= 1) return;
        _currentPage--;
        if (string.IsNullOrWhiteSpace(Query))
        {
            await LoadPopularModsAsync();
        }
        else
        {
            await LoadSearchResultsAsync();
        }
    }

    private async Task LoadSearchResultsAsync()
    {
        Results.Clear();
        Status = "正在搜索...";
        OnPropertyChanged(nameof(Status));

        try
        {
            var remote = await _catalogService.SearchModsAdvancedAsync(
                keyword: Query,
                source: SelectedSource,
                page: _currentPage);
            foreach (var item in remote)
            {
                Results.Add(item);
            }
        }
        catch (Exception ex)
        {
            Status = $"搜索失败: {ex.Message}";
            OnPropertyChanged(nameof(Status));
            return;
        }

        HasNextPage = Results.Count >= 10;
        HasPreviousPage = _currentPage > 1;
        PageInfoText = $"第 {_currentPage} 页";
        Status = $"已找到 {Results.Count} 条结果（第 {_currentPage} 页）";
        OnPropertyChanged(nameof(Status));
    }

    private async Task LoadPopularModsAsync()
    {
        Results.Clear();
        Status = "正在加载热门 Mod...";
        OnPropertyChanged(nameof(Status));

        try
        {
            var remote = await _catalogService.SearchModsAdvancedAsync(
                keyword: string.Empty,
                source: SelectedSource,
                hotOnly: true,
                page: _currentPage);
            foreach (var item in remote)
            {
                Results.Add(item);
            }

            HasNextPage = Results.Count >= 10;
            HasPreviousPage = _currentPage > 1;
            PageInfoText = $"第 {_currentPage} 页";
            Status = Results.Count > 0
                ? $"已加载 {Results.Count} 个热门 Mod（第 {_currentPage} 页）"
                : "暂无推荐，请输入关键词搜索";
        }
        catch (Exception ex)
        {
            Status = $"加载热门 Mod 失败: {ex.Message}";
        }

        OnPropertyChanged(nameof(Status));
    }

    [RelayCommand]
    private void OpenDetails(Models.ModSearchResultItem? item)
    {
        if (item is null)
        {
            return;
        }

        OpenDetailsRequested?.Invoke(item.Identity);
    }
}

public sealed partial class ModpackSearchPageViewModel : FeaturePageViewModelBase
{
    private readonly Services.RemoteCatalogService _catalogService;

    public override string Title => "Modpack 搜索";
    public override string Description => "对应 WPF ModpackSearchView，承载整合包搜索与导入流程。";

    public event Action<Models.CatalogResourceIdentity>? OpenDetailsRequested;

    public ObservableCollection<string> Sources { get; } = ["全部", "NexusMods", "Curseforge"];

    public ObservableCollection<Models.ModSearchResultItem> Results { get; } = [];

    public string Query { get; set; } = string.Empty;

    public string SelectedSource { get; set; } = "全部";

    public string Status { get; private set; } = "请输入关键词开始搜索";

    public ModpackSearchPageViewModel(Services.RemoteCatalogService catalogService)
    {
        _catalogService = catalogService;
    }

    [RelayCommand]
    private async Task Search()
    {
        Results.Clear();

        if (string.IsNullOrWhiteSpace(Query))
        {
            Status = "请输入 Modpack 关键词";
            OnPropertyChanged(nameof(Status));
            return;
        }

        Status = "正在请求远程数据源...";
        OnPropertyChanged(nameof(Status));

        try
        {
            var remote = await _catalogService.SearchModpacksAsync(Query, SelectedSource);
            foreach (var item in remote)
            {
                Results.Add(item);
            }
        }
        catch (Exception ex)
        {
            Status = $"远程搜索失败: {ex.Message}";
            OnPropertyChanged(nameof(Status));
            return;
        }

        Status = $"已找到 {Results.Count} 条结果";
        OnPropertyChanged(nameof(Status));
    }

    [RelayCommand]
    private void OpenDetails(Models.ModSearchResultItem? item)
    {
        if (item is null)
        {
            return;
        }

        OpenDetailsRequested?.Invoke(item.Identity);
    }
}

public sealed partial class ModDetailsPageViewModel : FeaturePageViewModelBase
{
    private readonly Services.RemoteCatalogService _catalogService;
    private readonly Services.DialogService _dialogService;
    private string _lastDisplayText = string.Empty;
    private string _currentSourceToken = string.Empty;
    private string _currentResourceId = string.Empty;
    private string _currentCompatTag = string.Empty;
    private string _currentCollectionSlug = string.Empty;
    private int _currentCollectionRevision = -1;
    private readonly List<VersionGroupItem> _allVersionGroups = [];
    private const string LocalizationContributionUrl = "https://svl.qzz.io/contribute";

    public override string Title => "资源详情";
    public override string Description => "对应 WPF ModDetailsView，展示资源信息、版本与下载动作。";

    public event Action<ExternalDownloadRequest>? QueueDownloadRequested;

    /// <summary>
    /// 当前实例 Mods 路径解析器。由 MainWindowViewModel 注入，
    /// 用于检测 Mod 是否已安装（扫描 Mods 目录中的 manifest.json）。
    /// </summary>
    public Func<string?>? CurrentModsPathResolver { get; set; }

    public string ResourceName { get; private set; } = "未选择资源";

    public string ResourceNotes { get; private set; } = "-";

    public string ResourceSource { get; private set; } = "-";

    public string DetailsStatus { get; private set; } = "等待加载";

    public string ResourceMetricTag { get; private set; } = string.Empty;

    public string ResourceTimeTag { get; private set; } = string.Empty;

    public string SourceResourceName { get; private set; } = string.Empty;

    public string LocalizedResourceName { get; private set; } = string.Empty;

    public string SourceResourceSummary { get; private set; } = string.Empty;

    public string LocalizedResourceSummary { get; private set; } = string.Empty;

    public string SourcePageUrl { get; private set; } = string.Empty;

    public string LocalizationContributor { get; private set; } = string.Empty;

    public string ResourceIdText { get; private set; } = "-";

    public string IconUrl { get; private set; } = string.Empty;

    public string FullIconUrl { get; private set; } = string.Empty;

    public string DisplayIconUrl
    {
        get
        {
            var preferIconFirst = ResourceSource.Contains("nexus", StringComparison.OrdinalIgnoreCase) ||
                                  _currentSourceToken.Contains("nexus", StringComparison.OrdinalIgnoreCase);

            if (preferIconFirst)
            {
                return !string.IsNullOrWhiteSpace(IconUrl) ? IconUrl : FullIconUrl;
            }

            return !string.IsNullOrWhiteSpace(FullIconUrl) ? FullIconUrl : IconUrl;
        }
    }

    public bool HasDisplayIcon => !string.IsNullOrWhiteSpace(DisplayIconUrl);

    public bool HasNoDisplayIcon => !HasDisplayIcon;

    public bool IsCollectionDetails { get; private set; }

    [ObservableProperty]
    private bool _useLocalizedName = true;

    [ObservableProperty]
    private bool _useLocalizedSummary = true;

    /// <summary>是否正在后台加载详情数据。导航立即发生，详情异步加载完毕后置回 false。</summary>
    [ObservableProperty]
    private bool _isLoadingDetails;

    public ObservableCollection<string> VersionOptions { get; } = [];

    public ObservableCollection<VersionGroupItem> VersionGroups { get; } = [];

    public ObservableCollection<GameVersionFilterItem> GameVersionFilters { get; } = [];

    public ObservableCollection<VersionDownloadGroupItem> VersionDownloadGroups { get; } = [];

    public ObservableCollection<string> DependencyItems { get; } = [];

    public ObservableCollection<string> DownloadOptions { get; } = [];

    public ObservableCollection<string> RequiredDependencyItems { get; } = [];

    public ObservableCollection<string> RelatedDependencyItems { get; } = [];

    public ObservableCollection<string> LocalizedDependencyItems { get; } = [];

    public ObservableCollection<string> HardConflictDependencyItems { get; } = [];

    public ObservableCollection<string> FunctionalOverlapDependencyItems { get; } = [];

    [ObservableProperty]
    private bool _isRequiredDependenciesExpanded = true;

    [ObservableProperty]
    private string _selectedGameVersion = "全部";

    [ObservableProperty]
    private string _selectedDownloadOption = string.Empty;

    public bool HasLocalizedResourceName => !string.IsNullOrWhiteSpace(LocalizedResourceName);

    public bool HasLocalizedResourceSummary => !string.IsNullOrWhiteSpace(LocalizedResourceSummary);

    public bool HasSourcePageUrl => !string.IsNullOrWhiteSpace(SourcePageUrl);

    public bool HasResourceMetricTag => !string.IsNullOrWhiteSpace(ResourceMetricTag);

    public bool HasResourceTimeTag => !string.IsNullOrWhiteSpace(ResourceTimeTag);

    public bool HasVersionOptions => VersionOptions.Count > 0;

    public bool HasNoVersionOptions => !HasVersionOptions;

    public bool HasVersionGroups => VersionGroups.Count > 0;

    public bool HasNoVersionGroups => !HasVersionGroups;

    public bool HasGameVersionFilters => GameVersionFilters.Count > 0;

    public bool HasVersionDownloadGroups => VersionDownloadGroups.Count > 0;

    public bool HasNoVersionDownloadGroups => !HasVersionDownloadGroups;

    public bool HasDependencyItems => DependencyItems.Count > 0;

    public bool HasNoDependencyItems => !HasDependencyItems;

    public bool HasRequiredDependencyItems => RequiredDependencyItems.Count > 0;

    public bool HasNoRequiredDependencyItems => !HasRequiredDependencyItems;

    public bool HasHardConflictDependencyItems => HardConflictDependencyItems.Count > 0;

    public bool HasFunctionalOverlapDependencyItems => FunctionalOverlapDependencyItems.Count > 0;

    public bool HasRelatedDependencyItems => RelatedDependencyItems.Count > 0;

    public bool HasLocalizedDependencyItems => LocalizedDependencyItems.Count > 0;

    public bool HasDownloadOptions => DownloadOptions.Count > 0;

    public bool CanOpenSelectedDownloadOptionInBrowser => TryResolveDownloadOptionUrl(SelectedDownloadOption).Length > 0;

    public bool CanQueueDownload => !string.IsNullOrWhiteSpace(SelectedDownloadOption);

    public bool CanInstallSelectedDownloadOption => CanQueueDownload;

    // Collection 没有单一文件可下载，SaveAs 不适用（会错误走 Mod 下载路径）
    public bool CanSaveSelectedDownloadOptionAs => CanQueueDownload && !IsCollectionDetails;

    public bool IsSmapiResource => IsSmapiResourceCore(DisplayResourceName, ResourceIdText, _currentSourceToken, SourcePageUrl);

    public bool ShowRequiredDependencyList => HasRequiredDependencyItems && IsRequiredDependenciesExpanded;

    public string RequiredDependencyHeaderText => HasRequiredDependencyItems
        ? $"前置依赖（必需）{RequiredDependencyItems.Count}"
        : "前置依赖（必需）";

    public string RequiredDependencyCountText => RequiredDependencyItems.Count.ToString(CultureInfo.InvariantCulture);

    public string RelatedDependencyCountText => RelatedDependencyItems.Count.ToString(CultureInfo.InvariantCulture);

    public string LocalizedDependencyCountText => LocalizedDependencyItems.Count.ToString(CultureInfo.InvariantCulture);

    public string HardConflictDependencyCountText => HardConflictDependencyItems.Count.ToString(CultureInfo.InvariantCulture);

    public string FunctionalOverlapDependencyCountText => FunctionalOverlapDependencyItems.Count.ToString(CultureInfo.InvariantCulture);

    public string RequiredDependencyToggleText => IsRequiredDependenciesExpanded ? "收起" : "展开";

    public string VersionSectionTitle => HasVersionOptions ? $"可选版本（{VersionOptions.Count}）" : "可选版本";

    public string DependencySectionTitle => HasDependencyItems ? $"依赖信息（{DependencyItems.Count}）" : "依赖信息";

    public string DownloadSectionTitle => HasDownloadOptions ? $"下载选项（{DownloadOptions.Count}）" : "下载选项";

    public string VersionGroupHintText => string.Equals(SelectedGameVersion, "全部", StringComparison.Ordinal)
        ? $"来源：{(string.IsNullOrWhiteSpace(ResourceSource) ? "未知" : ResourceSource)} · 按兼容策略分组"
        : $"筛选：Stardew Valley {SelectedGameVersion}";

    public string CopyIdButtonText => IsCollectionDetails ? "尾链" : "ID";

    public bool HasLocalizationContributor => !string.IsNullOrWhiteSpace(LocalizationContributor);

    public bool IsLocalizedDisplay => UseLocalizedName && UseLocalizedSummary;

    public string DisplayLanguageToggleText => IsLocalizedDisplay ? "EN" : "中";

    public string DisplayResourceName =>
        UseLocalizedName && !string.IsNullOrWhiteSpace(LocalizedResourceName)
            ? LocalizedResourceName
            : (string.IsNullOrWhiteSpace(SourceResourceName) ? ResourceName : SourceResourceName);

    public string DisplayResourceSummary =>
        UseLocalizedSummary && !string.IsNullOrWhiteSpace(LocalizedResourceSummary)
            ? LocalizedResourceSummary
            : (string.IsNullOrWhiteSpace(SourceResourceSummary) ? ResourceNotes : SourceResourceSummary);

    public ModDetailsPageViewModel(Services.RemoteCatalogService catalogService, Services.DialogService dialogService)
    {
        _catalogService = catalogService;
        _dialogService = dialogService;

        VersionOptions.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasVersionOptions));
            OnPropertyChanged(nameof(HasNoVersionOptions));
            OnPropertyChanged(nameof(VersionSectionTitle));
            RebuildVersionGroups();
        };
        VersionGroups.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasVersionGroups));
            OnPropertyChanged(nameof(HasNoVersionGroups));
        };
        GameVersionFilters.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasGameVersionFilters));
        };
        VersionDownloadGroups.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasVersionDownloadGroups));
            OnPropertyChanged(nameof(HasNoVersionDownloadGroups));
        };
        DependencyItems.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasDependencyItems));
            OnPropertyChanged(nameof(HasNoDependencyItems));
            OnPropertyChanged(nameof(DependencySectionTitle));
        };
        RequiredDependencyItems.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasRequiredDependencyItems));
            OnPropertyChanged(nameof(HasNoRequiredDependencyItems));
            OnPropertyChanged(nameof(RequiredDependencyHeaderText));
            OnPropertyChanged(nameof(RequiredDependencyCountText));
            OnPropertyChanged(nameof(ShowRequiredDependencyList));
        };
        RelatedDependencyItems.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasRelatedDependencyItems));
            OnPropertyChanged(nameof(RelatedDependencyCountText));
        };
        LocalizedDependencyItems.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasLocalizedDependencyItems));
            OnPropertyChanged(nameof(LocalizedDependencyCountText));
        };
        HardConflictDependencyItems.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasHardConflictDependencyItems));
            OnPropertyChanged(nameof(HardConflictDependencyCountText));
        };
        FunctionalOverlapDependencyItems.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasFunctionalOverlapDependencyItems));
            OnPropertyChanged(nameof(FunctionalOverlapDependencyCountText));
        };
        DownloadOptions.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasDownloadOptions));
            OnPropertyChanged(nameof(CanQueueDownload));
            OnPropertyChanged(nameof(CanInstallSelectedDownloadOption));
            OnPropertyChanged(nameof(CanSaveSelectedDownloadOptionAs));
            OnPropertyChanged(nameof(DownloadSectionTitle));
            OnPropertyChanged(nameof(CanOpenSelectedDownloadOptionInBrowser));
            RebuildGameVersionFilters();
            RebuildVersionDownloadGroups();
        };
    }

    public void SetResource(string name, string notes)
    {
        _lastDisplayText = string.IsNullOrWhiteSpace(name) ? string.Empty : name;
        var parsed = ParseDisplayText(_lastDisplayText);

        ResourceName = string.IsNullOrWhiteSpace(parsed.SourceName) ? (string.IsNullOrWhiteSpace(name) ? "未选择资源" : name) : parsed.SourceName;
        SourceResourceName = string.IsNullOrWhiteSpace(parsed.SourceName) ? ResourceName : parsed.SourceName;
        LocalizedResourceName = parsed.LocalizedName;
        ResourceMetricTag = parsed.MetricTag;
        ResourceTimeTag = parsed.TimeTag;
        ResourceSource = string.IsNullOrWhiteSpace(parsed.SourceLabel) ? "-" : parsed.SourceLabel;
        ResourceIdText = string.IsNullOrWhiteSpace(parsed.ResourceId) ? "-" : parsed.ResourceId;
        IconUrl = parsed.IconUrl;
        FullIconUrl = parsed.FullIconUrl;
        _currentResourceId = parsed.ResourceId;
        _currentSourceToken = parsed.SourceToken;
        IsCollectionDetails = parsed.IsCollection;
        SourcePageUrl = BuildSourcePageUrl(parsed);
        LocalizationContributor = parsed.LocalizedContributor;
        _currentCompatTag = parsed.CompatTag;

        var fallbackSummary = string.IsNullOrWhiteSpace(notes) ? "-" : notes;
        ResourceNotes = string.IsNullOrWhiteSpace(parsed.SourceSummary) ? fallbackSummary : parsed.SourceSummary;
        SourceResourceSummary = string.IsNullOrWhiteSpace(parsed.SourceSummary) ? ResourceNotes : parsed.SourceSummary;
        LocalizedResourceSummary = parsed.LocalizedSummary;

        UseLocalizedName = true;
        UseLocalizedSummary = true;
        DetailsStatus = "待加载详情";

        VersionOptions.Clear();
        VersionGroups.Clear();
        GameVersionFilters.Clear();
        VersionDownloadGroups.Clear();
        _allVersionGroups.Clear();
        SelectedGameVersion = "全部";
        DependencyItems.Clear();
        RequiredDependencyItems.Clear();
        RelatedDependencyItems.Clear();
        LocalizedDependencyItems.Clear();
        HardConflictDependencyItems.Clear();
        FunctionalOverlapDependencyItems.Clear();
        DownloadOptions.Clear();
        SelectedDownloadOption = string.Empty;
        IsRequiredDependenciesExpanded = true;

        Debug.WriteLine($"[ModDetails] SetResource source={parsed.SourceLabel}, id={parsed.ResourceId}, name={DisplayResourceName}");

        RaiseResourceHeaderState();
    }

    /// <summary>
    /// displayText 字符串重载（DownloadPage/VersionSettingsPage 未迁移到结构化模型，仍传 displayText）。
    /// 内部用 ParseDisplayText 解析后构造 CatalogResourceIdentity 委托给结构化重载。
    /// </summary>
    public async Task LoadDetailsAsync(string displayText)
    {
        if (string.IsNullOrWhiteSpace(displayText))
        {
            return;
        }

        var parsed = ParseDisplayText(displayText);
        long.TryParse(parsed.ResourceId, out var resourceId);
        var source = ResolveCatalogSourceFromToken(parsed.SourceToken);
        var identity = new CatalogResourceIdentity(resourceId, parsed.SourceName, source, parsed.IsCollection, parsed.CollectionSlug ?? string.Empty);
        await LoadDetailsAsync(identity);
    }

    private static CatalogSource ResolveCatalogSourceFromToken(string sourceToken)
    {
        if (string.IsNullOrWhiteSpace(sourceToken))
        {
            return CatalogSource.Unknown;
        }

        if (sourceToken.Contains("github", StringComparison.OrdinalIgnoreCase))
        {
            return CatalogSource.GitHub;
        }

        if (sourceToken.Contains("nexus", StringComparison.OrdinalIgnoreCase))
        {
            return CatalogSource.NexusMods;
        }

        if (sourceToken.Contains("curse", StringComparison.OrdinalIgnoreCase))
        {
            return CatalogSource.Curseforge;
        }

        return CatalogSource.Unknown;
    }

    public async Task LoadDetailsAsync(Models.CatalogResourceIdentity identity, Models.ModSearchResultItem? context = null)
    {
        IsLoadingDetails = true;
        DetailsStatus = "正在加载详情...";
        OnPropertyChanged(nameof(DetailsStatus));
        Debug.WriteLine($"[ModDetails] LoadDetails start identity={identity}");

        try
        {
        var details = await _catalogService.GetResourceDetailsAsync(identity);

        // 优先用详情数据，其次用列表项上下文（列表项携带的 Stat/TimeTag/IconUrl 等显示字段）
        ResourceName = string.IsNullOrWhiteSpace(details.Name)
            ? (context?.Name ?? identity.Name)
            : details.Name;
        SourceResourceName = identity.Name;
        // GitHub 源（仅 SMAPI）的 identity.Name 是发布版本号（如 "4.5.2"），
        // 详情服务已返回稳定名称（如 "SMAPI - Stardew Modding API"），这里用它覆盖，
        // 否则 DisplayResourceName 优先取 SourceResourceName/LocalizedResourceName 仍会显示版本号。
        if (identity.Source == Models.CatalogSource.GitHub && !string.IsNullOrWhiteSpace(details.Name))
        {
            SourceResourceName = details.Name;
            // 本地化名同样兜底到稳定名称，避免 DisplayResourceName 因 UseLocalizedName
            // 命中 identity.Name 的版本号（4.5.2）。
            LocalizedResourceName = details.Name;
        }
        else
        {
            LocalizedResourceName = !string.IsNullOrWhiteSpace(details.LocalizedName)
                ? details.LocalizedName
                : (context?.Name ?? identity.Name);
        }

        ResourceSource = string.IsNullOrWhiteSpace(details.Source) ? "-" : details.Source;
        ResourceMetricTag = context?.Stat ?? string.Empty;
        ResourceTimeTag = context?.TimeTag ?? string.Empty;
        ResourceIdText = identity.ResourceId <= 0 ? "-" : identity.ResourceId.ToString();
        var detailsIconUrl = details.IconUrl?.Trim() ?? string.Empty;
        var detailsFullIconUrl = details.FullIconUrl?.Trim() ?? string.Empty;
        IconUrl = !string.IsNullOrWhiteSpace(detailsIconUrl)
            ? detailsIconUrl
            : (context?.IconUrl ?? string.Empty);
        FullIconUrl = !string.IsNullOrWhiteSpace(detailsFullIconUrl)
            ? detailsFullIconUrl
            : (!string.IsNullOrWhiteSpace(context?.FullIconUrl) ? context!.FullIconUrl : IconUrl);
        if (string.IsNullOrWhiteSpace(IconUrl))
        {
            IconUrl = FullIconUrl;
        }
        if (string.IsNullOrWhiteSpace(FullIconUrl))
        {
            FullIconUrl = IconUrl;
        }
        Debug.WriteLine($"[ModDetails] Icon: IconUrl='{IconUrl}', FullIconUrl='{FullIconUrl}', DisplayIconUrl='{DisplayIconUrl}', HasDisplayIcon={HasDisplayIcon}");
        _currentResourceId = identity.ResourceId <= 0 ? _currentResourceId : identity.ResourceId.ToString();
        _currentSourceToken = identity.Source switch
        {
            Models.CatalogSource.GitHub => "GitHub",
            Models.CatalogSource.NexusMods => (identity.IsModpack || !string.IsNullOrWhiteSpace(identity.CollectionSlug)) ? "NexusPack" : "NexusMods",
            Models.CatalogSource.Curseforge => identity.IsModpack ? "CurseforgePack" : "Curseforge",
            _ => _currentSourceToken
        };
        // 仅 Nexus Collection 走 Collection 安装流程；Curseforge 整合包走 Modpack 安装流程
        // NexusMods 资源若有 CollectionSlug 也视为 Collection（即使来源标记未含 IsModpack）
        IsCollectionDetails = identity.Source == Models.CatalogSource.NexusMods &&
                              (identity.IsModpack || !string.IsNullOrWhiteSpace(identity.CollectionSlug));
        _currentCollectionSlug = identity.CollectionSlug ?? string.Empty;
        SourcePageUrl = BuildSourcePageUrl(identity);
        LocalizationContributor = !string.IsNullOrWhiteSpace(details.LocalizedContributor)
            ? details.LocalizedContributor
            : string.Empty;
        _currentCompatTag = context?.GameVersionTag ?? string.Empty;

        ResourceNotes = string.IsNullOrWhiteSpace(details.Summary)
            ? (string.IsNullOrWhiteSpace(context?.Summary) ? "-" : context!.Summary)
            : details.Summary;
        SourceResourceSummary = context?.Summary ?? ResourceNotes;
        LocalizedResourceSummary = !string.IsNullOrWhiteSpace(details.LocalizedSummary)
            ? details.LocalizedSummary
            : (context?.Summary ?? string.Empty);

        VersionOptions.Clear();
        foreach (var item in details.VersionOptions.Take(12))
        {
            VersionOptions.Add(item);
        }
        RebuildVersionGroups();

        DependencyItems.Clear();
        foreach (var item in details.Dependencies.Take(12))
        {
            DependencyItems.Add(item);
        }
        ClassifyDependencyGroups();

        DownloadOptions.Clear();
        foreach (var item in details.DownloadOptions.Take(20))
        {
            DownloadOptions.Add(item);
        }

        RebuildVersionDownloadGroups();

        SelectedDownloadOption = DownloadOptions.FirstOrDefault() ?? string.Empty;
        var hasNexusAuthIssue = ResourceSource.Contains("nexus", StringComparison.OrdinalIgnoreCase) &&
                                (ResourceNotes.Contains("登录已过期", StringComparison.OrdinalIgnoreCase) ||
                                 ResourceNotes.Contains("重新登录", StringComparison.OrdinalIgnoreCase) ||
                                 DownloadOptions.Any(option => option.Contains("重新登录", StringComparison.OrdinalIgnoreCase)));

        var isCurseforgeBlocked = ResourceSource.Contains("curse", StringComparison.OrdinalIgnoreCase) &&
                                  VersionOptions.Count == 0 && DownloadOptions.Count == 0;

        // Nexus Collection 的 revisions 拉取失败时，服务端会保留基本信息但 VersionOptions 为空。
        // 此时通过 DetailsStatus 给出提示，而不是用错误文本覆盖 ResourceNotes。
        var isNexusCollectionRevisionsFailed = IsCollectionDetails &&
                                               ResourceSource.Contains("nexus", StringComparison.OrdinalIgnoreCase) &&
                                               VersionOptions.Count == 0;

        if (isCurseforgeBlocked)
        {
            DetailsStatus = "CurseForge API 暂不可用（Cloudflare 拦截），请稍后重试或使用其他来源";
        }
        else if (hasNexusAuthIssue)
        {
            DetailsStatus = "Nexus OAuth 登录已失效，请前往设置页重新登录";
        }
        else if (isNexusCollectionRevisionsFailed)
        {
            DetailsStatus = "Collection 版本列表获取失败，可改用 NXM Collection 链接导入下载";
        }
        else
        {
            DetailsStatus = "详情已加载";
        }
        IsRequiredDependenciesExpanded = true;

        Debug.WriteLine($"[ModDetails] LoadDetails done source={ResourceSource}, versions={VersionOptions.Count}, downloads={DownloadOptions.Count}, deps={DependencyItems.Count}");

        RaiseResourceHeaderState();
        }
        catch (Exception ex)
        {
            DetailsStatus = $"详情加载失败: {ex.Message}";
            OnPropertyChanged(nameof(DetailsStatus));
            Debug.WriteLine($"[ModDetails] LoadDetails failed: {ex}");
        }
        finally
        {
            IsLoadingDetails = false;
        }
    }

    [RelayCommand]
    private void ShowLocalizedName()
    {
        if (!HasLocalizedResourceName)
        {
            return;
        }

        UseLocalizedName = true;
    }

    [RelayCommand]
    private void ShowSourceName()
    {
        UseLocalizedName = false;
    }

    [RelayCommand]
    private void ShowLocalizedSummary()
    {
        if (!HasLocalizedResourceSummary)
        {
            return;
        }

        UseLocalizedSummary = true;
    }

    [RelayCommand]
    private void ShowSourceSummary()
    {
        UseLocalizedSummary = false;
    }

    [RelayCommand]
    private void ToggleDisplayLanguage()
    {
        var targetLocalized = !IsLocalizedDisplay;
        UseLocalizedName = targetLocalized;
        UseLocalizedSummary = targetLocalized;
        OnPropertyChanged(nameof(IsLocalizedDisplay));
        OnPropertyChanged(nameof(DisplayLanguageToggleText));
    }

    [RelayCommand]
    private void OpenSourcePage()
    {
        if (!HasSourcePageUrl)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = SourcePageUrl,
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore browser open failures to keep details page responsive.
        }
    }

    [RelayCommand]
    private async Task CopyNameAsync()
    {
        var text = DisplayResourceName;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var clipboard = GetClipboard();
        if (clipboard == null)
        {
            DetailsStatus = "当前环境不支持剪贴板";
            OnPropertyChanged(nameof(DetailsStatus));
            return;
        }

        await clipboard.SetTextAsync(text);
        DetailsStatus = "已复制名称";
        OnPropertyChanged(nameof(DetailsStatus));
    }

    [RelayCommand]
    private async Task CopyIdAsync()
    {
        var rawId = ResourceIdText?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawId) || string.Equals(rawId, "-", StringComparison.Ordinal))
        {
            return;
        }

        var copyValue = IsCollectionDetails ? $"collections/{rawId}" : rawId;
        var clipboard = GetClipboard();
        if (clipboard == null)
        {
            DetailsStatus = "当前环境不支持剪贴板";
            OnPropertyChanged(nameof(DetailsStatus));
            return;
        }

        await clipboard.SetTextAsync(copyValue);
        DetailsStatus = $"已复制{CopyIdButtonText}";
        OnPropertyChanged(nameof(DetailsStatus));
    }

    [RelayCommand]
    private void OpenLocalizationContributionPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = LocalizationContributionUrl,
                UseShellExecute = true
            });
            DetailsStatus = "已打开本地化贡献页面";
            OnPropertyChanged(nameof(DetailsStatus));
        }
        catch
        {
            DetailsStatus = "打开本地化贡献页面失败";
            OnPropertyChanged(nameof(DetailsStatus));
        }
    }

    [RelayCommand]
    private async Task ShowLocalizationContributorInfoAsync()
    {
        var message = "贡献本地化说明\n\n"
            + "点击「贡献本地化」按钮可以跳转到社区贡献页面，为当前资源补充中文译名与说明。\n\n";

        if (!string.IsNullOrWhiteSpace(LocalizationContributor))
        {
            message += $"当前资源的本地化贡献者：{LocalizationContributor}";
        }
        else
        {
            message += "当前资源还没有人进行汉化贡献，欢迎前往贡献页面参与补充。";
        }

        await _dialogService.ShowMessageAsync(
            "贡献本地化",
            message);
    }

    [RelayCommand]
    private void SelectGameVersionFilter(string? version)
    {
        var normalized = string.IsNullOrWhiteSpace(version) ? "全部" : version.Trim();
        if (string.Equals(SelectedGameVersion, normalized, StringComparison.Ordinal))
        {
            return;
        }

        SelectedGameVersion = normalized;
    }

    [RelayCommand]
    private void ToggleVersionGroupExpanded(VersionDownloadGroupItem? group)
    {
        if (group == null)
        {
            return;
        }

        group.IsExpanded = !group.IsExpanded;
    }

    [RelayCommand]
    private void InstallVersionItem(string? option)
    {
        if (string.IsNullOrWhiteSpace(option))
        {
            return;
        }

        SelectedDownloadOption = option;
        InstallSelectedDownloadOption();
    }

    [RelayCommand]
    private void SaveVersionItemAs(string? option)
    {
        if (string.IsNullOrWhiteSpace(option))
        {
            return;
        }

        SelectedDownloadOption = option;
        SaveSelectedDownloadOptionAs();
    }

    [RelayCommand]
    private void QueueDownload()
    {
        InstallSelectedDownloadOption();
    }

    [RelayCommand]
    private void InstallSelectedDownloadOption()
    {
        if (string.IsNullOrWhiteSpace(SelectedDownloadOption))
        {
            return;
        }

        var request = BuildExternalDownloadRequest(ExternalDownloadAction.Install);
        QueueDownloadRequested?.Invoke(request);
        DetailsStatus = IsSmapiResource ? "已提交 SMAPI 安装任务" : "已提交安装任务";
        OnPropertyChanged(nameof(DetailsStatus));
    }

    [RelayCommand]
    private void SaveSelectedDownloadOptionAs()
    {
        if (string.IsNullOrWhiteSpace(SelectedDownloadOption))
        {
            return;
        }

        var request = BuildExternalDownloadRequest(ExternalDownloadAction.SaveAs);
        QueueDownloadRequested?.Invoke(request);
        DetailsStatus = "已提交另存为任务";
        OnPropertyChanged(nameof(DetailsStatus));
    }

    [RelayCommand]
    private void ToggleRequiredDependenciesExpanded()
    {
        if (!HasRequiredDependencyItems)
        {
            return;
        }

        IsRequiredDependenciesExpanded = !IsRequiredDependenciesExpanded;
    }

    [RelayCommand]
    private void OpenSelectedDownloadOptionInBrowser()
    {
        var url = TryResolveDownloadOptionUrl(SelectedDownloadOption);
        if (string.IsNullOrWhiteSpace(url))
        {
            DetailsStatus = "当前条目不支持浏览器打开";
            OnPropertyChanged(nameof(DetailsStatus));
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            DetailsStatus = "已在浏览器打开下载链接";
        }
        catch
        {
            DetailsStatus = "打开下载链接失败";
        }

        OnPropertyChanged(nameof(DetailsStatus));
    }

    [RelayCommand]
    private void OpenDependencySource(string? dependencyText)
    {
        var targetUrl = ResolveDependencySourceUrl(dependencyText);
        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            DetailsStatus = "当前依赖没有可跳转来源";
            OnPropertyChanged(nameof(DetailsStatus));
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = targetUrl,
                UseShellExecute = true
            });
            DetailsStatus = "已打开依赖来源页";
        }
        catch
        {
            DetailsStatus = "打开依赖来源页失败";
        }

        OnPropertyChanged(nameof(DetailsStatus));
    }

    partial void OnUseLocalizedNameChanged(bool value)
    {
        OnPropertyChanged(nameof(DisplayResourceName));
        OnPropertyChanged(nameof(IsSmapiResource));
        OnPropertyChanged(nameof(IsLocalizedDisplay));
        OnPropertyChanged(nameof(DisplayLanguageToggleText));
    }

    partial void OnUseLocalizedSummaryChanged(bool value)
    {
        OnPropertyChanged(nameof(DisplayResourceSummary));
        OnPropertyChanged(nameof(IsLocalizedDisplay));
        OnPropertyChanged(nameof(DisplayLanguageToggleText));
    }

    partial void OnSelectedDownloadOptionChanged(string value)
    {
        OnPropertyChanged(nameof(CanQueueDownload));
        OnPropertyChanged(nameof(CanInstallSelectedDownloadOption));
        OnPropertyChanged(nameof(CanSaveSelectedDownloadOptionAs));
        OnPropertyChanged(nameof(CanOpenSelectedDownloadOptionInBrowser));
    }

    partial void OnIsRequiredDependenciesExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(RequiredDependencyToggleText));
        OnPropertyChanged(nameof(ShowRequiredDependencyList));
    }

    partial void OnSelectedGameVersionChanged(string value)
    {
        ApplyVersionGroupFilter();
        RebuildVersionDownloadGroups();
        OnPropertyChanged(nameof(VersionGroupHintText));
    }

    private void RaiseResourceHeaderState()
    {
        OnPropertyChanged(nameof(ResourceName));
        OnPropertyChanged(nameof(ResourceNotes));
        OnPropertyChanged(nameof(ResourceSource));
        OnPropertyChanged(nameof(ResourceMetricTag));
        OnPropertyChanged(nameof(ResourceTimeTag));
        OnPropertyChanged(nameof(ResourceIdText));
        OnPropertyChanged(nameof(IconUrl));
        OnPropertyChanged(nameof(FullIconUrl));
        OnPropertyChanged(nameof(DisplayIconUrl));
        OnPropertyChanged(nameof(HasDisplayIcon));
        OnPropertyChanged(nameof(HasNoDisplayIcon));
        OnPropertyChanged(nameof(IsCollectionDetails));
        OnPropertyChanged(nameof(CopyIdButtonText));
        OnPropertyChanged(nameof(HasResourceMetricTag));
        OnPropertyChanged(nameof(HasResourceTimeTag));
        OnPropertyChanged(nameof(SourceResourceName));
        OnPropertyChanged(nameof(LocalizedResourceName));
        OnPropertyChanged(nameof(SourceResourceSummary));
        OnPropertyChanged(nameof(LocalizedResourceSummary));
        OnPropertyChanged(nameof(HasLocalizedResourceName));
        OnPropertyChanged(nameof(HasLocalizedResourceSummary));
        OnPropertyChanged(nameof(HasVersionOptions));
        OnPropertyChanged(nameof(HasNoVersionOptions));
        OnPropertyChanged(nameof(HasVersionGroups));
        OnPropertyChanged(nameof(HasNoVersionGroups));
        OnPropertyChanged(nameof(HasGameVersionFilters));
        OnPropertyChanged(nameof(HasVersionDownloadGroups));
        OnPropertyChanged(nameof(HasNoVersionDownloadGroups));
        OnPropertyChanged(nameof(VersionGroupHintText));
        OnPropertyChanged(nameof(VersionSectionTitle));
        OnPropertyChanged(nameof(HasDependencyItems));
        OnPropertyChanged(nameof(HasNoDependencyItems));
        OnPropertyChanged(nameof(DependencySectionTitle));
        OnPropertyChanged(nameof(HasRequiredDependencyItems));
        OnPropertyChanged(nameof(HasNoRequiredDependencyItems));
        OnPropertyChanged(nameof(HasRelatedDependencyItems));
        OnPropertyChanged(nameof(HasLocalizedDependencyItems));
        OnPropertyChanged(nameof(HasHardConflictDependencyItems));
        OnPropertyChanged(nameof(HasFunctionalOverlapDependencyItems));
        OnPropertyChanged(nameof(RequiredDependencyHeaderText));
        OnPropertyChanged(nameof(RequiredDependencyCountText));
        OnPropertyChanged(nameof(RelatedDependencyCountText));
        OnPropertyChanged(nameof(LocalizedDependencyCountText));
        OnPropertyChanged(nameof(HardConflictDependencyCountText));
        OnPropertyChanged(nameof(FunctionalOverlapDependencyCountText));
        OnPropertyChanged(nameof(RequiredDependencyToggleText));
        OnPropertyChanged(nameof(ShowRequiredDependencyList));
        OnPropertyChanged(nameof(HasDownloadOptions));
        OnPropertyChanged(nameof(DownloadSectionTitle));
        OnPropertyChanged(nameof(DisplayResourceName));
        OnPropertyChanged(nameof(DisplayResourceSummary));
        OnPropertyChanged(nameof(IsLocalizedDisplay));
        OnPropertyChanged(nameof(DisplayLanguageToggleText));
        OnPropertyChanged(nameof(SourcePageUrl));
        OnPropertyChanged(nameof(HasSourcePageUrl));
        OnPropertyChanged(nameof(LocalizationContributor));
        OnPropertyChanged(nameof(HasLocalizationContributor));
        OnPropertyChanged(nameof(DetailsStatus));
        OnPropertyChanged(nameof(CanQueueDownload));
        OnPropertyChanged(nameof(CanInstallSelectedDownloadOption));
        OnPropertyChanged(nameof(CanSaveSelectedDownloadOptionAs));
        OnPropertyChanged(nameof(CanOpenSelectedDownloadOptionInBrowser));
        OnPropertyChanged(nameof(IsSmapiResource));
    }

    private void RebuildVersionGroups()
    {
        _allVersionGroups.Clear();
        if (VersionOptions.Count <= 0)
        {
            VersionGroups.Clear();
            GameVersionFilters.Clear();
            Debug.WriteLine("[ModDetails] RebuildVersionGroups: no versions");
            return;
        }

        var source = string.IsNullOrWhiteSpace(ResourceSource) ? "未知来源" : ResourceSource;
        var grouped = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["兼容推荐"] = [],
            ["稳定版本"] = [],
            ["预发布测试"] = [],
            ["来源链接"] = [],
            ["其它信息"] = []
        };

        foreach (var version in VersionOptions)
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                continue;
            }

            var key = ClassifyVersionStrategy(version);
            Debug.WriteLine($"[ModDetails] Version '{version.Substring(0, Math.Min(60, version.Length))}' => {key}");
            if (!grouped.TryGetValue(key, out var bucket))
            {
                bucket = [];
                grouped[key] = bucket;
            }

            bucket.Add(version.Trim());
        }

        foreach (var item in grouped)
        {
            if (item.Value.Count <= 0)
            {
                continue;
            }

            _allVersionGroups.Add(new VersionGroupItem
            {
                GroupTitle = item.Key,
                SourceTag = source,
                CountText = item.Value.Count.ToString(CultureInfo.InvariantCulture),
                Items = item.Value
            });
        }

        Debug.WriteLine($"[ModDetails] RebuildVersionGroups: {VersionOptions.Count} versions => {_allVersionGroups.Count} groups: {string.Join(", ", _allVersionGroups.Select(g => $"{g.GroupTitle}({g.Items.Count})"))}");
        RebuildGameVersionFilters();
        ApplyVersionGroupFilter();
    }

    private void RebuildGameVersionFilters()
    {
        var allVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var version in ExtractGameVersionTokens(_currentCompatTag))
        {
            allVersions.Add(version);
        }

        foreach (var option in DownloadOptions)
        {
            foreach (var version in ExtractGameVersionTokens(option))
            {
                allVersions.Add(version);
            }
        }

        if (allVersions.Count == 0)
        {
            foreach (var group in _allVersionGroups)
            {
                foreach (var item in group.Items)
                {
                    foreach (var version in ExtractGameVersionTokens(item))
                    {
                        allVersions.Add(version);
                    }
                }
            }
        }

        var ordered = allVersions
            .OrderByDescending(ParseVersionTokenForSort)
            .ToList();

        GameVersionFilters.Clear();
        GameVersionFilters.Add(new GameVersionFilterItem("全部", true));
        foreach (var version in ordered)
        {
            GameVersionFilters.Add(new GameVersionFilterItem(version));
        }

        if (!GameVersionFilters.Any(item => string.Equals(item.Value, SelectedGameVersion, StringComparison.Ordinal)))
        {
            SelectedGameVersion = "全部";
        }

        UpdateGameVersionFilterSelectionState();
    }

    private void ApplyVersionGroupFilter()
    {
        VersionGroups.Clear();
        UpdateGameVersionFilterSelectionState();

        var selected = SelectedGameVersion;
        foreach (var group in _allVersionGroups)
        {
            var filteredItems = string.Equals(selected, "全部", StringComparison.Ordinal)
                ? group.Items.ToList()
                : group.Items.Where(item => IsVersionCompatible(item, selected)).ToList();

            if (filteredItems.Count <= 0)
            {
                continue;
            }

            VersionGroups.Add(new VersionGroupItem
            {
                GroupTitle = group.GroupTitle,
                SourceTag = group.SourceTag,
                CountText = filteredItems.Count.ToString(CultureInfo.InvariantCulture),
                Items = filteredItems
            });
        }
    }

    private void UpdateGameVersionFilterSelectionState()
    {
        foreach (var item in GameVersionFilters)
        {
            item.IsSelected = string.Equals(item.Value, SelectedGameVersion, StringComparison.Ordinal);
        }
    }

    private void RebuildVersionDownloadGroups()
    {
        // 整合包（CurseforgePack/NexusPack）不从标题提取版本号，因为无法区分是游戏版本还是整合包版本
        var isModpackContext = !string.IsNullOrEmpty(_currentSourceToken) &&
                               _currentSourceToken.Contains("Pack", StringComparison.OrdinalIgnoreCase);
        var parsedItems = DownloadOptions
            .Select(option => ParseDownloadOptionItem(option, _currentCompatTag, isModpackContext))
            .Where(item => item != null)
            .Cast<VersionDownloadItem>()
            .ToList();

        if (parsedItems.Count == 0)
        {
            VersionDownloadGroups.Clear();
            return;
        }

        var selected = SelectedGameVersion;
        var grouped = parsedItems
            .Where(item => string.Equals(selected, "全部", StringComparison.Ordinal) || IsVersionKeyCompatible(item.GameVersion, selected))
            .GroupBy(item => item.GameVersion)
            .OrderByDescending(group => ParseVersionTokenForSort(group.Key))
            .ToList();

        VersionDownloadGroups.Clear();
        foreach (var group in grouped)
        {
            var files = group
                .OrderByDescending(item => ParseVersionTokenForSort(item.VersionText))
                .ToList();

            VersionDownloadGroups.Add(new VersionDownloadGroupItem(group.Key, files, true));
        }

        // 检测 Mod 是否已安装，更新安装按钮文本（安装/升级/回退/重新安装）
        UpdateInstallActionTexts(parsedItems);

        Debug.WriteLine($"[ModDetails] RebuildVersionDownloadGroups selected={SelectedGameVersion}, groups={VersionDownloadGroups.Count}");
    }

    /// <summary>
    /// 扫描当前实例 Mods 目录，检测 Mod 是否已安装。
    /// 参考旧架构 ModDetailsViewModel.CheckModExists：
    /// 1. 通过文件夹名匹配
    /// 2. 回退到遍历所有 manifest.json 的 Name 字段匹配
    /// 返回已安装版本号，未安装返回 null。
    /// </summary>
    private string? CheckModInstalled()
    {
        var modsPath = CurrentModsPathResolver?.Invoke();
        if (string.IsNullOrWhiteSpace(modsPath) || !Directory.Exists(modsPath))
        {
            return null;
        }

        var modName = DisplayResourceName;
        if (string.IsNullOrWhiteSpace(modName))
        {
            return null;
        }

        try
        {
            // 1. 文件夹名匹配（ZIP 通常解压到同名文件夹）
            foreach (var dir in Directory.GetDirectories(modsPath))
            {
                var dirName = Path.GetFileName(dir);
                if (dirName.Contains(modName, StringComparison.OrdinalIgnoreCase) ||
                    modName.Contains(dirName, StringComparison.OrdinalIgnoreCase))
                {
                    var version = TryReadManifestVersion(dir);
                    if (!string.IsNullOrWhiteSpace(version))
                    {
                        return version;
                    }
                }
            }

            // 2. 遍历所有 manifest.json 的 Name 字段匹配
            foreach (var modDir in Directory.GetDirectories(modsPath))
            {
                var manifestPath = Path.Combine(modDir, "manifest.json");
                if (!File.Exists(manifestPath)) continue;

                try
                {
                    var json = File.ReadAllText(manifestPath);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("Name", out var nameProp) &&
                        nameProp.GetString()?.Equals(modName, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        return TryReadManifestVersion(modDir);
                    }
                }
                catch
                {
                    // 忽略解析失败的 manifest.json
                }
            }
        }
        catch
        {
            // best-effort 检测，失败不阻塞详情加载
        }

        return null;
    }

    private static string? TryReadManifestVersion(string modDir)
    {
        var manifestPath = Path.Combine(modDir, "manifest.json");
        if (!File.Exists(manifestPath)) return null;

        try
        {
            var json = File.ReadAllText(manifestPath);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("Version", out var versionProp))
            {
                return versionProp.GetString();
            }
        }
        catch
        {
            // 忽略解析失败
        }

        return null;
    }

    /// <summary>
    /// 根据已安装版本号更新各版本项的安装按钮文本。
    /// - 未安装 → "安装"
    /// - 已安装且版本相同 → "重新安装"
    /// - 已安装且选中版本较新 → "升级"
    /// - 已安装且选中版本较旧 → "回退"
    /// </summary>
    private void UpdateInstallActionTexts(List<VersionDownloadItem> items)
    {
        var installedVersion = CheckModInstalled();
        if (string.IsNullOrWhiteSpace(installedVersion))
        {
            // 未安装，重置为默认 "安装"
            foreach (var item in items)
            {
                item.InstallActionText = "安装";
            }
            return;
        }

        foreach (var item in items)
        {
            item.InstallActionText = ResolveInstallActionText(item.VersionText, installedVersion);
        }
    }

    /// <summary>
    /// 实例切换后重新检测 Mod 安装状态并更新所有版本项的安装按钮文本。
    /// 由 MainWindowViewModel 在实例切换时调用，避免停留在详情页时按钮文本不刷新。
    /// </summary>
    public void RefreshInstallActionTexts()
    {
        // 尚未加载详情时无需刷新
        if (VersionDownloadGroups.Count == 0)
        {
            return;
        }

        var allItems = VersionDownloadGroups.SelectMany(g => g.Files).ToList();
        UpdateInstallActionTexts(allItems);
    }

    private static string ResolveInstallActionText(string itemVersion, string installedVersion)
    {
        if (string.IsNullOrWhiteSpace(itemVersion))
        {
            return "重新安装";
        }

        var cmp = CompareVersions(itemVersion, installedVersion);
        return cmp switch
        {
            > 0 => "升级",
            < 0 => "回退",
            _ => "重新安装"
        };
    }

    /// <summary>
    /// 比较两个版本号。优先用 Version.TryParse，失败则用字符串比较。
    /// </summary>
    private static int CompareVersions(string a, string b)
    {
        if (Version.TryParse(a, out var va) && Version.TryParse(b, out var vb))
        {
            return va.CompareTo(vb);
        }

        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static VersionDownloadItem? ParseDownloadOptionItem(string option, string compatTag, bool isModpackContext = false)
    {
        if (string.IsNullOrWhiteSpace(option))
        {
            return null;
        }

        var (cleanOption, meta) = SplitOptionMetadata(option);
        var url = TryResolveDownloadOptionUrl(cleanOption);
        var text = cleanOption.Trim();
        var title = text;

        var split = text.Split('|', 2, StringSplitOptions.TrimEntries);
        if (split.Length > 1)
        {
            title = split[0];
        }
        else if (!string.IsNullOrWhiteSpace(url) && text.Contains(url, StringComparison.OrdinalIgnoreCase))
        {
            title = text.Replace(url, string.Empty, StringComparison.OrdinalIgnoreCase).Trim(' ', '-', '|');
        }

        // 去掉 Nexus/Curseforge 选项前缀 "File {fileId}: "，标题只保留显示名/标签部分。
        var filePrefixMatch = Regex.Match(title, "^File\\s+\\d+\\s*:\\s*", RegexOptions.CultureInvariant);
        if (filePrefixMatch.Success)
        {
            title = title[filePrefixMatch.Length..].Trim();
        }

        var gameVersion = !string.IsNullOrWhiteSpace(meta.GameVersion)
            ? meta.GameVersion
            : ExtractPrimaryGameVersion(title, compatTag);
        var fileName = TryExtractFileName(title, url);
        // 整合包不从标题提取版本号（无法区分游戏版本与整合包版本），仅使用元数据中的 Version
        // 普通资源回退到从标题/文件名提取版本号
        var versionText = !string.IsNullOrWhiteSpace(meta.Version)
            ? meta.Version
            : (isModpackContext ? fileName : ExtractVersionText(title, fileName));

        // 标题行显示：Curseforge 通过 displayname 元数据传入 DisplayName，优先使用；
        // Nexus 无 displayname 元数据，回退到 version（版本号），再回退到标题文本。
        var titleDisplay = !string.IsNullOrWhiteSpace(meta.DisplayName)
            ? meta.DisplayName
            : (!string.IsNullOrWhiteSpace(meta.Version) ? meta.Version : title);

        return new VersionDownloadItem
        {
            RawOption = option,
            Title = string.IsNullOrWhiteSpace(title) ? fileName : title,
            FileName = fileName,
            Url = url,
            GameVersion = gameVersion,
            VersionText = versionText,
            TitleDisplay = titleDisplay,
            Channel = meta.Channel ?? string.Empty,
            ChannelColor = ResolveChannelColor(meta.Channel),
            SizeText = meta.Size ?? string.Empty,
            DownloadCountText = meta.Downloads ?? string.Empty,
            DateText = meta.Date ?? string.Empty
        };
    }

    private static (string Option, OptionMetadata Meta) SplitOptionMetadata(string option)
    {
        const string separator = " ~~ ";
        var index = option.IndexOf(separator, StringComparison.Ordinal);
        if (index < 0)
        {
            return (option, OptionMetadata.Empty);
        }

        var optionPart = option[..index];
        var metaPart = option[(index + separator.Length)..];
        return (optionPart, OptionMetadata.Parse(metaPart));
    }

    private static string ResolveChannelColor(string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel))
        {
            return string.Empty;
        }

        return channel switch
        {
            "Release" => "#2EA043",
            "Beta" => "#1F6FEB",
            "Alpha" => "#8B949E",
            _ => string.Empty
        };
    }

    private sealed class OptionMetadata
    {
        public string? Channel { get; set; }

        public string? GameVersion { get; set; }

        public string? Version { get; set; }

        public string? DisplayName { get; set; }

        public string? Size { get; set; }

        public string? Downloads { get; set; }

        public string? Date { get; set; }

        public static OptionMetadata Empty => new();

        public static OptionMetadata Parse(string text)
        {
            var meta = new OptionMetadata();
            if (string.IsNullOrWhiteSpace(text))
            {
                return meta;
            }

            foreach (var pair in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                var key = pair[..eq].Trim();
                var value = pair[(eq + 1)..].Trim();
                switch (key)
                {
                    case "channel":
                        meta.Channel = value;
                        break;
                    case "gamever":
                        meta.GameVersion = value;
                        break;
                    case "version":
                        meta.Version = value;
                        break;
                    case "displayname":
                        meta.DisplayName = value;
                        break;
                    case "size":
                        meta.Size = value;
                        break;
                    case "downloads":
                        meta.Downloads = value;
                        break;
                    case "date":
                        meta.Date = value;
                        break;
                }
            }

            return meta;
        }
    }

    private static string ExtractPrimaryGameVersion(string text, string compatTag)
    {
        foreach (var compat in ExtractGameVersionTokens(compatTag))
        {
            return compat;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return "未知";
        }

        var bracketMatch = Regex.Match(text, "\\[(\\d+\\.\\d+(?:\\.\\d+)?\\+?)\\]", RegexOptions.CultureInvariant);
        if (bracketMatch.Success && TryNormalizeStardewVersionToken(bracketMatch.Groups[1].Value, out var bracketVersion))
        {
            return bracketVersion;
        }

        var stardewMatch = Regex.Match(text, "(?i)stardew(?:\\s+valley)?[^\\d]*(\\d+\\.\\d+(?:\\.\\d+)?)\\+?");
        if (stardewMatch.Success && TryNormalizeStardewVersionToken(stardewMatch.Groups[1].Value, out var stardewVersion))
        {
            return stardewVersion;
        }

        var svMatch = Regex.Match(text, "(?i)(?:sv|sdv|兼容|for)\\D{0,12}(\\d+\\.\\d+(?:\\.\\d+)?)");
        if (svMatch.Success && TryNormalizeStardewVersionToken(svMatch.Groups[1].Value, out var svVersion))
        {
            return svVersion;
        }

        foreach (var token in ExtractGameVersionTokens(text))
        {
            return token;
        }

        return "未知";
    }

    private static string TryExtractFileName(string title, string url)
    {
        var candidate = title?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(candidate) && candidate.IndexOf('.', StringComparison.Ordinal) > 0)
        {
            return candidate;
        }

        if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            var name = Path.GetFileName(Uri.UnescapeDataString(parsed.AbsolutePath));
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return string.IsNullOrWhiteSpace(candidate) ? "未知文件" : candidate;
    }

    private static string ExtractVersionText(string text, string fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        var match = Regex.Match(text, "\\b(v?\\d+\\.\\d+(?:\\.\\d+)?)\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        return fallback;
    }

    private static bool IsVersionKeyCompatible(string key, string selected)
    {
        if (string.Equals(key, "未知", StringComparison.Ordinal))
        {
            return string.Equals(selected, "未知", StringComparison.Ordinal);
        }

        return IsVersionCompatible(key, selected);
    }

    private static bool TryNormalizeStardewVersionToken(string token, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var match = Regex.Match(token.Trim(), "^(?<major>\\d+)\\.(?<minor>\\d+)(?:\\.(?<patch>\\d+))?\\+?$", RegexOptions.CultureInvariant);
        if (!match.Success ||
            !int.TryParse(match.Groups["major"].Value, out var major) ||
            !int.TryParse(match.Groups["minor"].Value, out var minor) ||
            major != 1)
        {
            return false;
        }

        if (match.Groups["patch"].Success && int.TryParse(match.Groups["patch"].Value, out var patch))
        {
            normalized = $"{major}.{minor}.{patch}";
            return true;
        }

        normalized = $"{major}.{minor}";
        return true;
    }

    private static IEnumerable<string> ExtractGameVersionTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(text, "\\b(\\d+\\.\\d+(?:\\.\\d+)?)\\+?\\b", RegexOptions.CultureInvariant))
        {
            if (!TryNormalizeStardewVersionToken(match.Groups[1].Value, out var version))
            {
                continue;
            }

            if (seen.Add(version))
            {
                yield return version;
            }
        }
    }

    private static Version ParseVersionTokenForSort(string token)
    {
        if (!TryNormalizeStardewVersionToken(token, out var normalized))
        {
            return new Version(0, 0);
        }

        return Version.TryParse(normalized, out var parsed) ? parsed : new Version(0, 0);
    }

    private static bool IsVersionCompatible(string line, string selectedVersion)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var selected = ParseVersionTokenForSort(selectedVersion);
        foreach (Match match in Regex.Matches(line, "\\b(\\d+\\.\\d+(?:\\.\\d+)?)\\+?\\b", RegexOptions.CultureInvariant))
        {
            var token = match.Value.Trim();
            var parsed = ParseVersionTokenForSort(token);

            if (token.EndsWith("+", StringComparison.Ordinal))
            {
                if (parsed <= selected)
                {
                    return true;
                }

                continue;
            }

            if (parsed == selected)
            {
                return true;
            }
        }

        return line.Contains(selectedVersion, StringComparison.OrdinalIgnoreCase);
    }

    private static string ClassifyVersionStrategy(string version)
    {
        var text = version.Trim();
        if (text.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "来源链接";
        }

        if (text.Contains("兼容", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("support", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("stardew", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("game", StringComparison.OrdinalIgnoreCase))
        {
            return "兼容推荐";
        }

        if (Regex.IsMatch(text, "(?:alpha|beta|preview|rc|nightly|dev|pre)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "预发布测试";
        }

        if (Regex.IsMatch(text, "(?:tag|v?\\d+\\.\\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "稳定版本";
        }

        return "其它信息";
    }

    private string ResolveDependencySourceUrl(string? dependencyText)
    {
        var source = ResourceSource ?? string.Empty;

        if (source.Contains("nexus", StringComparison.OrdinalIgnoreCase))
        {
            if (TryExtractFirstNumericId(dependencyText, out var nexusId) && nexusId > 0)
            {
                return $"https://www.nexusmods.com/stardewvalley/mods/{nexusId}";
            }

            return "https://www.nexusmods.com/stardewvalley/mods";
        }

        if (source.Contains("curse", StringComparison.OrdinalIgnoreCase))
        {
            if (TryExtractFirstNumericId(dependencyText, out var curseId) && curseId > 0)
            {
                return $"https://www.curseforge.com/projects/{curseId}";
            }

            return "https://www.curseforge.com/stardewvalley/mods";
        }

        if (source.Contains("github", StringComparison.OrdinalIgnoreCase))
        {
            return "https://github.com/Pathoschild/SMAPI/releases";
        }

        return HasSourcePageUrl ? SourcePageUrl : string.Empty;
    }

    private static bool TryExtractFirstNumericId(string? text, out long id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = Regex.Match(text, "(\\d+)", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        return long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
    }

    private static string TryResolveDownloadOptionUrl(string option)
    {
        if (string.IsNullOrWhiteSpace(option))
        {
            return string.Empty;
        }

        var trimmed = option.Trim();
        var markerIndex = trimmed.IndexOf("http://", StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            markerIndex = trimmed.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
        }

        if (markerIndex >= 0)
        {
            var candidate = trimmed[markerIndex..].Trim();
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var parsedByMarker) &&
                (parsedByMarker.Scheme == Uri.UriSchemeHttp || parsedByMarker.Scheme == Uri.UriSchemeHttps))
            {
                return parsedByMarker.ToString();
            }
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) &&
            (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            return parsed.ToString();
        }

        return string.Empty;
    }

    private ExternalDownloadRequest BuildExternalDownloadRequest(ExternalDownloadAction action)
    {
        var resolvedResourceId = string.IsNullOrWhiteSpace(_currentResourceId)
            ? ResourceIdText
            : _currentResourceId;

        if (string.Equals(resolvedResourceId, "-", StringComparison.Ordinal))
        {
            resolvedResourceId = string.Empty;
        }

        // Curseforge/SVL 整合包（CurseforgePack）走 Modpack 安装流程
        // 1. SourceToken 含 "Pack"（如 CurseforgePack）且非 NexusPack
        // 2. 或下载选项中含 .cfmodpack 扩展名（Curseforge 整合包文件格式）
        var isModpack = !IsCollectionDetails &&
                        ((_currentSourceToken?.Contains("Pack", StringComparison.OrdinalIgnoreCase) ?? false) &&
                         !string.Equals(_currentSourceToken, "NexusPack", StringComparison.OrdinalIgnoreCase));

        if (!isModpack && !IsCollectionDetails)
        {
            var optionText = SelectedDownloadOption ?? string.Empty;
            if (optionText.Contains(".cfmodpack", StringComparison.OrdinalIgnoreCase) ||
                optionText.Contains("modpack", StringComparison.OrdinalIgnoreCase))
            {
                isModpack = true;
            }
        }

        return new ExternalDownloadRequest
        {
            Action = action,
            ResourceName = DisplayResourceName,
            ResourceSource = ResourceSource,
            ResourceId = resolvedResourceId,
            SourceToken = _currentSourceToken ?? string.Empty,
            SourcePageUrl = SourcePageUrl,
            IsSmapiResource = IsSmapiResource,
            SelectedDownloadOption = SelectedDownloadOption ?? string.Empty,
            IsCollection = IsCollectionDetails,
            IsModpack = isModpack,
            CollectionSlug = ResolveCollectionSlugFromContext(),
            CollectionRevision = ResolveCollectionRevisionFromContext()
        };
    }

    /// <summary>从当前上下文中解析 Collection slug（从 SelectedDownloadOption 或 _currentSourceToken 中提取）。</summary>
    private string ResolveCollectionSlugFromContext()
    {
        if (!IsCollectionDetails) return string.Empty;

        // 从 SelectedDownloadOption 中提取 slug= 段
        var option = SelectedDownloadOption ?? string.Empty;
        var slugIndex = option.IndexOf("slug=", StringComparison.OrdinalIgnoreCase);
        if (slugIndex >= 0)
        {
            var slugStart = slugIndex + 5;
            var slugEnd = option.IndexOf('|', slugStart);
            var tildeIndex = option.IndexOf("~~", slugStart, StringComparison.Ordinal);
            if (tildeIndex >= 0 && (slugEnd < 0 || tildeIndex < slugEnd))
            {
                slugEnd = tildeIndex;
            }
            var slug = slugEnd > slugStart
                ? option[slugStart..slugEnd].Trim()
                : option[slugStart..].Trim();
            return slug;
        }

        return _currentCollectionSlug ?? string.Empty;
    }

    /// <summary>从当前上下文中解析 Collection revision 号。</summary>
    private int ResolveCollectionRevisionFromContext()
    {
        return _currentCollectionRevision;
    }

    private static bool IsSmapiResourceCore(
        string displayName,
        string resourceIdText,
        string sourceToken,
        string sourcePageUrl)
    {
        var checks = new[]
        {
            displayName,
            resourceIdText,
            sourceToken,
            sourcePageUrl
        };

        if (checks.Any(text => !string.IsNullOrWhiteSpace(text) && text.Contains("smapi", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(resourceIdText) &&
            (string.Equals(resourceIdText.Trim(), "2400", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(resourceIdText.Trim(), "898372", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private void ClassifyDependencyGroups()
    {
        RequiredDependencyItems.Clear();
        RelatedDependencyItems.Clear();
        LocalizedDependencyItems.Clear();
        HardConflictDependencyItems.Clear();
        FunctionalOverlapDependencyItems.Clear();

        foreach (var item in DependencyItems)
        {
            var normalized = item?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (IsLocalizedDependency(normalized))
            {
                LocalizedDependencyItems.Add(normalized);
                continue;
            }

            if (IsHardConflictDependency(normalized))
            {
                HardConflictDependencyItems.Add(normalized);
                continue;
            }

            if (IsFunctionalOverlapDependency(normalized))
            {
                FunctionalOverlapDependencyItems.Add(normalized);
                continue;
            }

            if (IsRelatedDependency(normalized))
            {
                RelatedDependencyItems.Add(normalized);
                continue;
            }

            RequiredDependencyItems.Add(normalized);
        }
    }

    private static bool IsLocalizedDependency(string text)
    {
        return text.Contains("汉化", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("翻译", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("i18n", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("translation", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("locale", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRelatedDependency(string text)
    {
        return text.Contains("相关", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("可选", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("推荐", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("related", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("optional", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("recommended", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHardConflictDependency(string text)
    {
        return text.Contains("冲突", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("conflict", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("不兼容", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("incompatible", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFunctionalOverlapDependency(string text)
    {
        return text.Contains("功能重叠", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("功能重复", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("同类", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("overlap", StringComparison.OrdinalIgnoreCase);
    }

    private static global::Avalonia.Input.Platform.IClipboard? GetClipboard()
    {
        if (global::Avalonia.Application.Current?.ApplicationLifetime is global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow?.Clipboard;
        }

        return null;
    }

    private static ParsedDisplayText ParseDisplayText(string displayText)
    {
        var parsed = new ParsedDisplayText
        {
            SourceLabel = "-",
            SourceName = string.Empty,
            SourceSummary = string.Empty,
            LocalizedName = string.Empty,
            LocalizedSummary = string.Empty,
            LocalizedContributor = string.Empty,
            ResourceId = string.Empty,
            MetricTag = string.Empty,
            TimeTag = string.Empty,
            SourceToken = string.Empty,
            IsCollection = false
        };

        if (string.IsNullOrWhiteSpace(displayText))
        {
            return parsed;
        }

        var parts = displayText.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return parsed;
        }

        var header = parts[0];
        if (header.StartsWith("[", StringComparison.Ordinal))
        {
            var closeIndex = header.IndexOf(']');
            if (closeIndex > 1)
            {
                var sourceSegment = header[1..closeIndex].Trim();
                var nameSegment = header[(closeIndex + 1)..].Trim();
                parsed.SourceName = string.IsNullOrWhiteSpace(nameSegment) ? parsed.SourceName : nameSegment;

                var sourceParts = sourceSegment.Split('#', 2, StringSplitOptions.TrimEntries);
                parsed.SourceToken = sourceParts[0];
                parsed.SourceLabel = ResolveSourceLabel(sourceParts[0]);
                parsed.ResourceId = sourceParts.Length > 1 ? sourceParts[1] : string.Empty;
                // 仅 Nexus Collection（NexusPack）走 Collection 安装流程（collection.json）。
                // Curseforge 整合包（CurseforgePack）走 Modpack 安装流程（manifest.json），不应标记为 Collection。
                parsed.IsCollection = string.Equals(parsed.SourceToken, "NexusPack", StringComparison.OrdinalIgnoreCase) ||
                                      parsed.SourceToken.Contains("Collection", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                parsed.SourceName = header;
            }
        }
        else
        {
            parsed.SourceName = header;
        }

        for (var index = 1; index < parts.Length; index++)
        {
            var segment = parts[index];
            if (segment.StartsWith("metric=", StringComparison.OrdinalIgnoreCase))
            {
                parsed.MetricTag = segment[7..].Trim();
                continue;
            }

            if (segment.StartsWith("time=", StringComparison.OrdinalIgnoreCase))
            {
                parsed.TimeTag = segment[5..].Trim();
                continue;
            }

            if (segment.StartsWith("srcName=", StringComparison.OrdinalIgnoreCase))
            {
                parsed.SourceName = segment[8..].Trim();
                continue;
            }

            if (segment.StartsWith("srcSummary=", StringComparison.OrdinalIgnoreCase))
            {
                parsed.SourceSummary = segment[11..].Trim();
                continue;
            }

            if (segment.StartsWith("zhName=", StringComparison.OrdinalIgnoreCase))
            {
                parsed.LocalizedName = segment[7..].Trim();
                continue;
            }

            if (segment.StartsWith("zhSummary=", StringComparison.OrdinalIgnoreCase))
            {
                parsed.LocalizedSummary = segment[10..].Trim();
                continue;
            }

            if (segment.StartsWith("zhBy=", StringComparison.OrdinalIgnoreCase))
            {
                parsed.LocalizedContributor = segment[5..].Trim();
                continue;
            }

            if (segment.StartsWith("compat=", StringComparison.OrdinalIgnoreCase))
            {
                parsed.CompatTag = segment[7..].Trim();
                continue;
            }

            if (segment.StartsWith("icon=", StringComparison.OrdinalIgnoreCase))
            {
                parsed.IconUrl = segment[5..].Trim();
                continue;
            }

            if (segment.StartsWith("fullIcon=", StringComparison.OrdinalIgnoreCase))
            {
                parsed.FullIconUrl = segment[9..].Trim();
                continue;
            }

            if (segment.StartsWith("slug=", StringComparison.OrdinalIgnoreCase))
            {
                parsed.CollectionSlug = segment[5..].Trim();
                continue;
            }

            if (string.IsNullOrWhiteSpace(parsed.SourceSummary))
            {
                parsed.SourceSummary = segment;
            }
        }

        return parsed;
    }

    private static string ResolveSourceLabel(string sourceToken)
    {
        if (sourceToken.Contains("github", StringComparison.OrdinalIgnoreCase))
        {
            return "GitHub";
        }

        if (sourceToken.Contains("nexus", StringComparison.OrdinalIgnoreCase))
        {
            return "NexusMods";
        }

        if (sourceToken.Contains("curse", StringComparison.OrdinalIgnoreCase))
        {
            return "Curseforge";
        }

        return "-";
    }

    private static string BuildSourcePageUrl(ParsedDisplayText parsed)
    {
        if (string.IsNullOrWhiteSpace(parsed.SourceToken))
        {
            return string.Empty;
        }

        if (parsed.SourceToken.Contains("github", StringComparison.OrdinalIgnoreCase))
        {
            return "https://github.com/Pathoschild/SMAPI/releases";
        }

        if (parsed.SourceToken.Contains("nexus", StringComparison.OrdinalIgnoreCase))
        {
            if (parsed.IsCollection)
            {
                return "https://next.nexusmods.com/stardewvalley/collections";
            }

            if (long.TryParse(parsed.ResourceId, out var nexusModId) && nexusModId > 0)
            {
                return $"https://www.nexusmods.com/stardewvalley/mods/{nexusModId}";
            }

            return "https://www.nexusmods.com/stardewvalley/mods";
        }

        if (parsed.SourceToken.Contains("curse", StringComparison.OrdinalIgnoreCase))
        {
            if (long.TryParse(parsed.ResourceId, out var curseforgeId) && curseforgeId > 0)
            {
                return $"https://www.curseforge.com/projects/{curseforgeId}";
            }

            return "https://www.curseforge.com/stardewvalley/mods";
        }

        return string.Empty;
    }

    /// <summary>结构化身份版本的来源页 URL 构建（替代解析字符串的 ParsedDisplayText 版本）。</summary>
    private static string BuildSourcePageUrl(Models.CatalogResourceIdentity identity)
    {
        return identity.Source switch
        {
            Models.CatalogSource.GitHub => "https://github.com/Pathoschild/SMAPI/releases",
            // NexusMods：Collection（IsModpack 或有 CollectionSlug）走 collections 链接，否则走 mods 链接
            Models.CatalogSource.NexusMods => (identity.IsModpack || !string.IsNullOrWhiteSpace(identity.CollectionSlug))
                ? (!string.IsNullOrWhiteSpace(identity.CollectionSlug)
                    ? $"https://next.nexusmods.com/stardewvalley/collections/{identity.CollectionSlug}"
                    : "https://next.nexusmods.com/stardewvalley/collections")
                : (identity.ResourceId > 0
                    ? $"https://www.nexusmods.com/stardewvalley/mods/{identity.ResourceId}"
                    : "https://www.nexusmods.com/stardewvalley/mods"),
            Models.CatalogSource.Curseforge => identity.ResourceId > 0
                ? $"https://www.curseforge.com/projects/{identity.ResourceId}"
                : "https://www.curseforge.com/stardewvalley/mods",
            _ => string.Empty
        };
    }

    private sealed class ParsedDisplayText
    {
        public string SourceLabel { get; set; } = "-";
        public string SourceName { get; set; } = string.Empty;
        public string SourceSummary { get; set; } = string.Empty;
        public string LocalizedName { get; set; } = string.Empty;
        public string LocalizedSummary { get; set; } = string.Empty;
        public string LocalizedContributor { get; set; } = string.Empty;
        public string ResourceId { get; set; } = string.Empty;
        public string MetricTag { get; set; } = string.Empty;
        public string TimeTag { get; set; } = string.Empty;
        public string SourceToken { get; set; } = string.Empty;
        public string CompatTag { get; set; } = string.Empty;
        public string IconUrl { get; set; } = string.Empty;
        public string FullIconUrl { get; set; } = string.Empty;
        public bool IsCollection { get; set; }
        public string CollectionSlug { get; set; } = string.Empty;
    }

    public sealed class VersionGroupItem
    {
        public string GroupTitle { get; init; } = string.Empty;

        public string SourceTag { get; init; } = string.Empty;

        public string CountText { get; init; } = "0";

        public List<string> Items { get; init; } = [];
    }

    public sealed partial class GameVersionFilterItem : ObservableObject
    {
        public string Value { get; }

        public string DisplayText => string.Equals(Value, "全部", StringComparison.Ordinal) ? "全部" : $"SV {Value}";

        public string BackgroundColor => IsSelected ? "#FF2B4C7E" : "#153A4D66";

        public string ForegroundColor => IsSelected ? "White" : "#FFCBD5E1";

        [ObservableProperty]
        private bool _isSelected;

        public GameVersionFilterItem(string value, bool isSelected = false)
        {
            Value = string.IsNullOrWhiteSpace(value) ? "全部" : value.Trim();
            IsSelected = isSelected;
        }

        partial void OnIsSelectedChanged(bool value)
        {
            OnPropertyChanged(nameof(BackgroundColor));
            OnPropertyChanged(nameof(ForegroundColor));
        }
    }

    public sealed partial class VersionDownloadGroupItem : ObservableObject
    {
        public string GameVersion { get; }

        public ObservableCollection<VersionDownloadItem> Files { get; }

        [ObservableProperty]
        private bool _isExpanded;

        public string ExpandButtonText => IsExpanded ? "收起" : "展开";

        public VersionDownloadGroupItem(string gameVersion, IEnumerable<VersionDownloadItem> files, bool isExpanded)
        {
            GameVersion = string.IsNullOrWhiteSpace(gameVersion) ? "未知" : gameVersion;
            Files = new ObservableCollection<VersionDownloadItem>(files ?? Enumerable.Empty<VersionDownloadItem>());
            IsExpanded = isExpanded;
        }

        partial void OnIsExpandedChanged(bool value)
        {
            OnPropertyChanged(nameof(ExpandButtonText));
        }
    }

    public sealed partial class VersionDownloadItem : ObservableObject
    {
        public string RawOption { get; init; } = string.Empty;

        public string Title { get; init; } = string.Empty;

        public string FileName { get; init; } = string.Empty;

        public string Url { get; init; } = string.Empty;

        public string GameVersion { get; init; } = "未知";

        public string VersionText { get; init; } = string.Empty;

        /// <summary>
        /// 标题行显示文本：Curseforge 优先显示 DisplayName，Nexus 优先显示 Version。
        /// </summary>
        public string TitleDisplay { get; init; } = string.Empty;

        public string Channel { get; init; } = string.Empty;

        public string ChannelColor { get; init; } = string.Empty;

        public string ChannelLetter => Channel switch
        {
            "Release" => "R",
            "Alpha" => "A",
            "Beta" => "B",
            _ => string.Empty
        };

        public string SizeText { get; init; } = string.Empty;

        public string DownloadCountText { get; init; } = string.Empty;

        public string DateText { get; init; } = string.Empty;

        public bool HasChannel => !string.IsNullOrWhiteSpace(Channel);

        public bool HasSize => !string.IsNullOrWhiteSpace(SizeText);

        public bool HasDownloadCount => !string.IsNullOrWhiteSpace(DownloadCountText);

        public bool HasDate => !string.IsNullOrWhiteSpace(DateText);

        public string DownloadCountDisplay => HasDownloadCount ? $"{DownloadCountText}次下载" : string.Empty;

        /// <summary>
        /// 安装按钮文本：安装/升级/回退/重新安装。
        /// 根据 Mod 是否已安装及版本对比结果设置，在 RebuildVersionDownloadGroups 中赋值，
        /// 实例切换后通过 RefreshInstallActionTexts() 重新检测并更新。
        /// </summary>
        [ObservableProperty]
        private string _installActionText = "安装";
    }
}

public sealed class ModTagFilterOption
{
    private const string FolderPrefix = "[目录] ";
    private const string PrefixFolderPrefix = "[前缀] ";
    private const string CustomTagPrefix = "[标签] ";

    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string TagId { get; set; } = string.Empty;
    public bool IsFolderTag { get; set; }
    public bool IsPrefixFolderTag { get; set; }
    public bool IsAllOption { get; set; }

    public string DisplayText => IsAllOption
        ? Name
        : $"{(IsFolderTag ? (IsPrefixFolderTag ? PrefixFolderPrefix : FolderPrefix) : CustomTagPrefix)}{Name}";
}

public sealed partial class ModTagPanelItem : ObservableObject
{
    public ModTagFilterOption Option { get; }

    [ObservableProperty]
    private bool _isSelected;

    public ModTagPanelItem(ModTagFilterOption option)
    {
        Option = option;
    }

    public string Key => Option.Key;
    public string Name => Option.Name;
    public string TagId => Option.TagId;
    public bool IsFolderTag => Option.IsFolderTag;
    public bool IsCustomTag => !Option.IsFolderTag && !Option.IsAllOption;
    public string DisplayText => Option.DisplayText;
}

public sealed class ModTagConfig
{
    public List<ModCustomTagDefinition> CustomTags { get; set; } = [];
    public Dictionary<string, List<string>> Assignments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> FolderTagOrder { get; set; } = [];
    public List<string> CustomTagOrder { get; set; } = [];
}

public sealed class ModCustomTagDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public enum ModManagePrimaryTab
{
    Mods,
    Backup
}

public enum ModManageSubFilter
{
    All,
    Enabled,
    Disabled,
    Updatable
}

public sealed class ModBackupRecord
{
    public string OriginalFolderName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string UniqueId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public sealed class ModDependencyDisplayItem
{
    public string UniqueId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string MinimumVersion { get; set; } = string.Empty;
    public bool IsRequired { get; set; } = true;
    public bool IsInstalled { get; set; }
    public bool IsInstalledAndEnabled { get; set; }
    public bool IsInstalledButDisabled { get; set; }
    public string InstalledModId { get; set; } = string.Empty;
    public string InstalledModName { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;

    public string DisplayText => string.IsNullOrWhiteSpace(MinimumVersion)
        ? DisplayName
        : $"{DisplayName} >= {MinimumVersion}";

    public string StatusPrefix
    {
        get
        {
            if (!IsRequired)
            {
                if (!IsInstalled)
                {
                    return "[未安装] ";
                }

                if (IsInstalledButDisabled)
                {
                    return "[被禁用] ";
                }

                return "[可选] ";
            }

            if (!IsInstalled)
            {
                return "[未安装] ";
            }

            if (IsInstalledButDisabled)
            {
                return "[被禁用] ";
            }

            return string.Empty;
        }
    }

    public string StatusPrefixColor
    {
        get
        {
            if (!IsRequired)
            {
                return (!IsInstalled || IsInstalledButDisabled) ? "#D8A131" : "#3E8EDE";
            }

            return (!IsInstalled || IsInstalledButDisabled) ? "#D45555" : "#3E8EDE";
        }
    }

    public string StatusSuffix => !IsRequired && (!IsInstalled || IsInstalledButDisabled)
        ? "[可选] "
        : string.Empty;
}

public static class ModTagConfigService
{
    private const string ConfigFileName = ".svl-mod-tags.json";

    public static ModTagConfig Load(string modsPath)
    {
        try
        {
            var filePath = GetConfigPath(modsPath);
            if (!File.Exists(filePath))
            {
                return new ModTagConfig();
            }

            var json = File.ReadAllText(filePath);
            var config = JsonSerializer.Deserialize<ModTagConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            }) ?? new ModTagConfig();

            config.CustomTags ??= [];
            config.Assignments ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            config.FolderTagOrder ??= [];
            config.CustomTagOrder ??= [];
            return Normalize(config);
        }
        catch
        {
            return new ModTagConfig();
        }
    }

    public static bool Save(string modsPath, ModTagConfig config)
    {
        try
        {
            Directory.CreateDirectory(modsPath);
            var filePath = GetConfigPath(modsPath);
            var normalized = Normalize(config);
            var json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetConfigPath(string modsPath)
    {
        return Path.Combine(modsPath, ConfigFileName);
    }

    private static ModTagConfig Normalize(ModTagConfig config)
    {
        config ??= new ModTagConfig();
        config.CustomTags ??= [];
        config.Assignments ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        config.FolderTagOrder ??= [];
        config.CustomTagOrder ??= [];

        config.CustomTags = config.CustomTags
            .Where(tag => !string.IsNullOrWhiteSpace(tag.Id) && !string.IsNullOrWhiteSpace(tag.Name))
            .GroupBy(tag => tag.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var validTagIds = config.CustomTags
            .Select(tag => tag.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        config.CustomTagOrder = config.CustomTagOrder
            .Where(tagId => !string.IsNullOrWhiteSpace(tagId) && validTagIds.Contains(tagId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var tag in config.CustomTags)
        {
            if (!config.CustomTagOrder.Any(id => string.Equals(id, tag.Id, StringComparison.OrdinalIgnoreCase)))
            {
                config.CustomTagOrder.Add(tag.Id);
            }
        }

        config.FolderTagOrder = config.FolderTagOrder
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var cleanedAssignments = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in config.Assignments)
        {
            var modKey = assignment.Key;
            var tags = assignment.Value;
            if (string.IsNullOrWhiteSpace(modKey) || tags == null)
            {
                continue;
            }

            var filtered = tags
                .Where(tagId => !string.IsNullOrWhiteSpace(tagId) && validTagIds.Contains(tagId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (filtered.Count > 0)
            {
                cleanedAssignments[modKey] = filtered;
            }
        }

        config.Assignments = cleanedAssignments;
        return config;
    }
}

public sealed partial class VersionSettingsPageViewModel : FeaturePageViewModelBase
{
    private const string NexusGameDomain = "stardewvalley";
    private static readonly HttpClient s_modNetworkHttp = CreateModNetworkHttpClient();
    private static readonly JsonSerializerOptions s_sourceJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    private readonly Services.AppUserSettingsStore _settingsStore;
    private readonly IGameInstallPathLocator _gameInstallPathLocator;
    private readonly Services.LocalizationService _localizationService;
    private readonly Services.ImageResourceService _imageResourceService;
    private readonly Services.DialogService _dialogService;
    private readonly Services.RemoteCatalogService _catalogService;
    private readonly Services.SmapiInstallService _smapiInstallService;
    private readonly Services.SmapiDownloadService _smapiDownloadService;
    private readonly Services.CommunityLocalizationService _communityLocalizationService;

    [ObservableProperty]
    private bool _isCheckingModUpdates;

    [ObservableProperty]
    private bool _isCheckingLocalization;

    [ObservableProperty]
    private string _currentUpdateProcessingModName = string.Empty;

    [ObservableProperty]
    private string _currentLocalizationProcessingModName = string.Empty;

    [ObservableProperty]
    private string _updateCheckProgressText = string.Empty;

    [ObservableProperty]
    private string _localizationCheckProgressText = string.Empty;

    public bool HasRunningModTask => IsCheckingModUpdates || IsCheckingLocalization;

    [ObservableProperty]
    private bool _isInstallingSmapi;

    [ObservableProperty]
    private string _smapiInstallProgressText = string.Empty;

    partial void OnIsCheckingModUpdatesChanged(bool value)
    {
        OnPropertyChanged(nameof(HasRunningModTask));
    }

    partial void OnIsCheckingLocalizationChanged(bool value)
    {
        OnPropertyChanged(nameof(HasRunningModTask));
    }

    private ModTagConfig _modTagConfig = new();
    private string _currentModsPathForTagConfig = string.Empty;
    private string _titleText = "版本设置";
    private string _descriptionText = "实例级配置。";

    public event Action? InstanceContextChanged;
    public event Action<string>? OpenDetailsRequested;
    public event Action<Models.DownloadTaskItem>? SmapiInstallTaskCreated;
    /// <summary>批量更新请求：携带可更新 Mod 的下载入口列表，由 MainWindow 路由到 DownloadPage 入队。</summary>
    public event Action<IReadOnlyList<ModBatchUpdateEntry>>? BatchUpdateModsRequested;
    /// <summary>请求返回主页面（删除版本后自动切换到 Base 版本并返回启动页）。</summary>
    public event Action? RequestReturnToLaunch;

    public override string Title => _titleText;
    public override string Description => _descriptionText;

    [ObservableProperty]
    private string _generalSectionButtonText = "版本设置";

    [ObservableProperty]
    private string _modManageSectionButtonText = "Mod管理";

    [ObservableProperty]
    private string _detectedGamePathLabelText = "探测到的游戏目录";

    [ObservableProperty]
    private string _defaultLaunchModeLabelText = "默认启动模式";

    [ObservableProperty]
    private string _safeLaunchCheckBoxText = "启用安全启动（保守参数）";

    [ObservableProperty]
    private string _refreshDetectedPathButtonText = "刷新探测";

    [ObservableProperty]
    private string _saveVersionSettingsButtonText = "保存设置";

    [ObservableProperty]
    private string _modManageTitleText = "Mod 管理";

    [ObservableProperty]
    private string _reloadModsButtonText = "刷新列表";

    [ObservableProperty]
    private string _openModsFolderButtonText = "打开 Mods 文件夹";

    [ObservableProperty]
    private string _selectedModTitleText = "当前选中 Mod";

    [ObservableProperty]
    private string _modOperationsHintText = "当前 Mod 操作（启用/禁用会直接改目录后缀）：";

    [ObservableProperty]
    private string _enableModButtonText = "启用";

    [ObservableProperty]
    private string _disableModButtonText = "禁用";

    [ObservableProperty]
    private string _uninstallModButtonText = "卸载";

    [ObservableProperty]
    private string _checkUpdateModButtonText = "检查更新";

    [ObservableProperty]
    private string _emptyModsHintText = "当前未检测到 Mod。可先在下载页安装，或通过“打开 Mods 文件夹”手动放入。";

    [ObservableProperty]
    private string _modColumnTitleText = "Mod";

    [ObservableProperty]
    private string _versionColumnTitleText = "版本";

    [ObservableProperty]
    private string _statusColumnTitleText = "状态";

    [ObservableProperty]
    private string _updateColumnTitleText = "更新";

    [ObservableProperty]
    private string _modDescriptionLabelText = "描述：";

    [ObservableProperty]
    private string _overviewVersionCardTitleText = "版本信息";

    [ObservableProperty]
    private string _overviewPersonalizationCardTitleText = "个性化";

    [ObservableProperty]
    private string _instanceNameLabelText = "版本名称";

    [ObservableProperty]
    private string _instanceDescriptionLabelText = "版本描述";

    [ObservableProperty]
    private string _favoriteInstanceLabelText = "收藏实例";

    [ObservableProperty]
    private string _savePersonalizationButtonText = "保存个性化";

    [ObservableProperty]
    private string _changeIconButtonText = "更改图标";

    [ObservableProperty]
    private string _overviewShortcutsCardTitleText = "快捷方式";

    [ObservableProperty]
    private string _openInstanceFolderButtonText = "版本文件夹";

    [ObservableProperty]
    private string _openSaveFolderButtonText = "存档文件夹";

    [ObservableProperty]
    private string _openModsFolderShortcutButtonText = "Mod 文件夹";

    [ObservableProperty]
    private string _overviewAdvancedCardTitleText = "高级管理";

    [ObservableProperty]
    private string _advancedWarningText = "危险操作区域：以下操作不可逆，请谨慎操作";

    [ObservableProperty]
    private string _uninstallBaseSmapiButtonText = "卸载 Base SMAPI";

    [ObservableProperty]
    private string _deleteCurrentVersionButtonText = "删除当前版本";

    [ObservableProperty]
    private string _deleteCurrentVersionHintText = "Base 版本不可删除，仅可卸载其 SMAPI。";

    [ObservableProperty]
    private string _autoInstallInfoTitleText = "安装选项";

    [ObservableProperty]
    private string _changeSmapiButtonText = "安装 / 更改 SMAPI";

    [ObservableProperty]
    private string _smapiGuideText = "点击上方按钮可以选择并安装不同版本的 SMAPI。安装将覆盖当前版本。";

    [ObservableProperty]
    private string _switchToSmapiButtonText = "切换到 SMAPI";

    [ObservableProperty]
    private string _switchToSmapiTipText = "检测到该路径已安装SMAPI，切换到SMAPI版本以启用Mod管理功能。";

    [ObservableProperty]
    private string _overviewNavText = "概览";

    [ObservableProperty]
    private string _autoInstallNavText = "自动安装";

    [ObservableProperty]
    private string _modManageNavText = "Mod管理";

    [ObservableProperty]
    private string _instanceSettingsNavText = "设置";

    [ObservableProperty]
    private string _exportNavText = "导出";

    [ObservableProperty]
    private string _instanceSettingsTitleText = "实例设置";

    [ObservableProperty]
    private string _instanceName = "Default Instance";

    [ObservableProperty]
    private string _instanceDescription = string.Empty;

    [ObservableProperty]
    private string _gameWindowTitle = "<default>";

    [ObservableProperty]
    private string _instanceCustomLaunchArguments = string.Empty;

    [ObservableProperty]
    private bool _isFavoriteInstance;

    [ObservableProperty]
    private bool _overrideSteamLaunchOptions;

    [ObservableProperty]
    private string _steamLaunchOptions = string.Empty;

    [ObservableProperty]
    private string _saveInstanceSettingsButtonText = "保存实例设置";

    [ObservableProperty]
    private string _exportSectionTitleText = "导出";

    [ObservableProperty]
    private string _modpackName = "我的整合包";

    [ObservableProperty]
    private string _modpackVersion = "1.0.0";

    [ObservableProperty]
    private string _modpackAuthor = string.Empty;

    [ObservableProperty]
    private bool _includeMods = true;

    [ObservableProperty]
    private bool _includeModSettings = true;

    [ObservableProperty]
    private bool _includeSvlLauncher;

    [ObservableProperty]
    private string _exportNamePrefix = "SVL-Modpack";

    [ObservableProperty]
    private string _lastExportPath = string.Empty;

    [ObservableProperty]
    private int _exportProgress;

    [ObservableProperty]
    private bool _isExporting;

    [ObservableProperty]
    private string _exportStatusMessage = "就绪";

    [ObservableProperty]
    private string _exportCurrentModsButtonText = "导出当前 Mods";

    [ObservableProperty]
    private string _openExportFolderButtonText = "打开导出目录";

    [ObservableProperty]
    private string _selectedSection = "Overview";

    [ObservableProperty]
    private string _detectedGamePath = "未探测到";

    [ObservableProperty]
    private string _selectedLaunchMode = "自动";

    [ObservableProperty]
    private string _instanceIconSource = "avares://SVL.Avalonia/Assets/Icons/Vanilla.png";

    [ObservableProperty]
    private string _instanceDisplayName = "未选择实例";

    [ObservableProperty]
    private string _instanceVersionText = "未知版本";

    [ObservableProperty]
    private bool _isSmapiInstance;

    [ObservableProperty]
    private bool _hasInstalledSmapi;

    [ObservableProperty]
    private string _smapiVersionText = "未安装";

    [ObservableProperty]
    private bool _enableSafeLaunch;

    [ObservableProperty]
    private string _status = "就绪";

    [ObservableProperty]
    private bool _isModManageSection;

    [ObservableProperty]
    private ModManageItem? _selectedMod;

    [ObservableProperty]
    private ModTagFilterOption? _selectedTagFilter;

    [ObservableProperty]
    private ModTagFilterOption? _selectedCustomTag;

    [ObservableProperty]
    private string _searchKeyword = string.Empty;

    [ObservableProperty]
    private string _tagSearchKeyword = string.Empty;

    [ObservableProperty]
    private ModManagePrimaryTab _currentModManageTab = ModManagePrimaryTab.Mods;

    [ObservableProperty]
    private ModManageSubFilter _currentModSubFilter = ModManageSubFilter.All;

    [ObservableProperty]
    private bool _isTagPanelExpanded = true;

    [ObservableProperty]
    private bool _showFolderTags = true;

    [ObservableProperty]
    private bool _showPrefixTags = true;

    [ObservableProperty]
    private bool _showCustomTags = true;

    [ObservableProperty]
    private string _newCustomTagName = string.Empty;

    [ObservableProperty]
    private string _renameCustomTagName = string.Empty;

    [ObservableProperty]
    private string _inlineTagHint = "点击标签可多选，支持快速筛选；可一键清除当前选中 Tags。";

    [ObservableProperty]
    private string _modManageHint = "请选择一个 Mod 进行管理";

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private bool _showSelectionActions;

    [ObservableProperty]
    private int _currentPageIndex = 1;

    [ObservableProperty]
    private int _totalPages = 1;

    [ObservableProperty]
    private int _totalFilteredCount;

    [ObservableProperty]
    private List<string> _pageNumbers = [];

    private readonly List<ModManageItem> _filteredSource = [];
    private const int ModsPageSize = 10;
    private const string BackupRootFolderName = "ModsBackup";
    private const string BackupMetaFileName = ".svl-backup.json";

    public ObservableCollection<ModManageItem> Mods { get; } = [];

    public ObservableCollection<ModManageItem> BackupMods { get; } = [];

    public ObservableCollection<ExportModSelectionItem> ExportModItems { get; } = [];

    public ObservableCollection<ModManageItem> FilteredMods { get; } = [];

    public ObservableCollection<ModTagFilterOption> TagFilters { get; } = [];

    public ObservableCollection<ModTagPanelItem> TagPanelItems { get; } = [];

    public ObservableCollection<ModTagFilterOption> CustomTagDefinitions { get; } = [];

    public bool IsGeneralSection => IsOverviewSection;

    public bool IsOverviewSection => string.Equals(SelectedSection, "Overview", StringComparison.Ordinal);

    public bool IsAutoInstallSection => string.Equals(SelectedSection, "AutoInstall", StringComparison.Ordinal);

    public bool IsSettingsSection => string.Equals(SelectedSection, "Settings", StringComparison.Ordinal);

    public bool IsExportSection => string.Equals(SelectedSection, "Export", StringComparison.Ordinal);

    public bool ShowSmapiVersion => IsSmapiInstance;

    public bool ShowNoSmapiHint => !IsSmapiInstance;

    public bool ShowBaseModeModWarning => !IsSmapiInstance && !HasInstalledSmapi;

    public bool ShowSwitchToSmapiHint => HasInstalledSmapi && !IsSmapiInstance;

    public bool CanManageMods => IsSmapiInstance;

    public bool CanChangeSmapiVersion => !ShowSwitchToSmapiHint && !IsInstallingSmapi;

    public bool HasSelectedMod => SelectedMod != null;

    public bool HasMods => IsBackupTab ? BackupMods.Count > 0 : Mods.Count > 0;

    public bool HasFilteredMods => FilteredMods.Count > 0;

    public bool ShowEmptyModsHint => !HasFilteredMods;

    public bool CanOperateSelectedMod => IsModsTab && SelectedMod is { IsBackupItem: false };

    public bool HasSelectedCustomTag => SelectedCustomTag != null;

    public bool HasSelectedTagPanelItems => TagPanelItems.Any(item => item.IsSelected);

    public bool CanBatchAddSelectedTags => SelectedCount > 0 && HasSelectedTagPanelItems;

    public bool CanBatchRemoveSelectedTags => SelectedCount > 0 && HasSelectedTagPanelItems;

    public bool ShowTagBatchAction => IsModsTab && SelectedCount > 0 &&
                                      (HasSelectedTagPanelItems || !string.IsNullOrWhiteSpace(TagSearchKeyword));

    public string TagBatchActionText => ShouldRemoveSelectedTagsFromSelectedMods() ? "删除标签" : "添加标签";

    public bool CanApplyTagSelectionToSearch =>
        TagPanelItems.Any(item => item.IsSelected) ||
        (SelectedTagFilter != null && !SelectedTagFilter.IsAllOption);

    public bool IsModsTab => CurrentModManageTab == ModManagePrimaryTab.Mods;

    public bool IsBackupTab => CurrentModManageTab == ModManagePrimaryTab.Backup;

    public bool IsAllFilter => CurrentModSubFilter == ModManageSubFilter.All;

    public bool IsEnabledFilter => CurrentModSubFilter == ModManageSubFilter.Enabled;

    public bool IsDisabledFilter => CurrentModSubFilter == ModManageSubFilter.Disabled;

    public bool IsUpdatableFilter => CurrentModSubFilter == ModManageSubFilter.Updatable;

    public string TagPanelToggleText => IsTagPanelExpanded ? "收起" : "展开";

    public bool HasPreviousPage => CurrentPageIndex > 1;

    public bool HasNextPage => CurrentPageIndex < TotalPages;

    public string PageInfo => $"{CurrentPageIndex}/{TotalPages}";

    public bool IsCurrentPageAllSelected => FilteredMods.Count > 0 && FilteredMods.All(item => item.IsSelected);

    public bool CanToggleCurrentPageSelection => FilteredMods.Count > 0;

    public bool ShowSelectionActionsBar => IsModManageSection && ShowSelectionActions;

    public string CurrentInstanceFolderPath => ResolveCurrentInstancePath();

    public string SaveFolderPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "StardewValley",
        "Saves");

    public string ModsFolderPath => Path.Combine(CurrentInstanceFolderPath, "Mods");

    public bool HasSaveFolder => Directory.Exists(SaveFolderPath);

    public bool HasModsFolder => Directory.Exists(ModsFolderPath);

    public bool CanDeleteCurrentVersion => TryGetVersionRootDirectory(CurrentInstanceFolderPath, out _);

    public bool CanUninstallBaseSmapi => !CanDeleteCurrentVersion && HasInstalledSmapi;

    public string ModsSummary => IsBackupTab
        ? (BackupMods.Count == 0 ? "当前没有备份记录" : $"共 {BackupMods.Count} 个备份")
        : (Mods.Count == 0
            ? "当前实例 Mods 目录为空"
            : $"共 {Mods.Count} 个 Mod：启用 {Mods.Count(item => item.IsEnabled)} 个，禁用 {Mods.Count(item => !item.IsEnabled)} 个");

    public string CurrentEmptyHintText => IsBackupTab
        ? "当前没有备份记录。可在 Mods 标签页选择 Mod 后执行备份。"
        : EmptyModsHintText;

    public int EnabledModsCount => Mods.Count(item => item.IsEnabled);

    public int DisabledModsCount => Mods.Count(item => !item.IsEnabled);

    public int UpdatableModsCount => Mods.Count(item => item.HasUpdate);

    public int BackupModsCount => BackupMods.Count;

    public string ModsTabText => $"Mods({Mods.Count})";

    public string BackupTabText => $"备份({BackupModsCount})";

    public string AllFilterText => $"全部({Mods.Count})";

    public string EnabledFilterText => $"启用({EnabledModsCount})";

    public string DisabledFilterText => $"禁用({DisabledModsCount})";

    public string UpdatableFilterText => $"可更新({UpdatableModsCount})";

    public bool CanEnableSelectedMod => IsModsTab && SelectedMod is { IsEnabled: false, IsBackupItem: false };

    public bool CanDisableSelectedMod => IsModsTab && SelectedMod is { IsEnabled: true, IsBackupItem: false };

    public string SelectedModDetails => SelectedMod == null
        ? "当前未选中 Mod"
        : (SelectedMod.IsBackupItem
            ? $"{SelectedMod.DisplayName} · 原目录: {SelectedMod.BackupOriginalFolderName} · 备份时间: {SelectedMod.BackupTimeText}"
            : $"{SelectedMod.DisplayName} · {SelectedMod.Version} · {SelectedMod.EnableStateText} · 目录: {SelectedMod.DirectoryName}");

    public string ExportHint => string.IsNullOrWhiteSpace(LastExportPath)
        ? "尚未导出"
        : $"最近导出: {LastExportPath}";

    public int SelectedExportModCount => ExportModItems.Count(item => item.IsSelected);

    public int TotalExportModCount => ExportModItems.Count;

    public bool ShowExportModList => IncludeMods;

    public bool CanStartExport => !IsExporting && (!IncludeMods || SelectedExportModCount > 0);

    public string ExportProgressText => IsExporting
        ? $"导出进度 {ExportProgress}%"
        : $"已选择 {SelectedExportModCount}/{TotalExportModCount} 个 Mod";

    public ObservableCollection<string> LaunchModes { get; } = ["自动", "SMAPI", "原版"];

    public string DefaultSteamLaunchOptions => BuildDefaultSteamLaunchOptions();

    public string SteamLaunchOptionsPreview
    {
        get
        {
            var options = (SteamLaunchOptions ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(options))
            {
                options = DefaultSteamLaunchOptions;
            }

            return string.IsNullOrWhiteSpace(options)
                ? "（无法生成默认参数，请确认实例路径）"
                : options;
        }
    }

    public VersionSettingsPageViewModel(
        Services.AppUserSettingsStore settingsStore,
        IGameInstallPathLocator gameInstallPathLocator,
        Services.LocalizationService localizationService,
        Services.ImageResourceService imageResourceService,
        Services.DialogService dialogService,
        Services.RemoteCatalogService catalogService,
        Services.SmapiInstallService smapiInstallService,
        Services.SmapiDownloadService smapiDownloadService,
        Services.CommunityLocalizationService communityLocalizationService)
    {
        _settingsStore = settingsStore;
        _gameInstallPathLocator = gameInstallPathLocator;
        _localizationService = localizationService;
        _imageResourceService = imageResourceService;
        _dialogService = dialogService;
        _catalogService = catalogService;
        _smapiInstallService = smapiInstallService;
        _smapiDownloadService = smapiDownloadService;
        _communityLocalizationService = communityLocalizationService;
        _localizationService.LanguageChanged += ApplyLocalizedTexts;
        _imageResourceService.ResourcesChanged += RefreshInstanceRuntimeInfo;
        ModpackAuthor = Environment.UserName;

        ApplyLocalizedTexts();

        Mods.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasMods));
            OnPropertyChanged(nameof(HasFilteredMods));
            OnPropertyChanged(nameof(ShowEmptyModsHint));
            OnPropertyChanged(nameof(ModsSummary));
            OnPropertyChanged(nameof(EnabledModsCount));
            OnPropertyChanged(nameof(DisabledModsCount));
            OnPropertyChanged(nameof(UpdatableModsCount));
            OnPropertyChanged(nameof(ModsTabText));
            OnPropertyChanged(nameof(AllFilterText));
            OnPropertyChanged(nameof(EnabledFilterText));
            OnPropertyChanged(nameof(DisabledFilterText));
            OnPropertyChanged(nameof(UpdatableFilterText));
            UpdateSelectionState();
        };

        BackupMods.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(BackupModsCount));
            OnPropertyChanged(nameof(HasMods));
            OnPropertyChanged(nameof(ModsSummary));
            OnPropertyChanged(nameof(HasFilteredMods));
            OnPropertyChanged(nameof(ShowEmptyModsHint));
            OnPropertyChanged(nameof(CurrentEmptyHintText));
            OnPropertyChanged(nameof(BackupTabText));
            UpdateSelectionState();
        };

        FilteredMods.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasFilteredMods));
            OnPropertyChanged(nameof(ShowEmptyModsHint));
            OnPropertyChanged(nameof(IsCurrentPageAllSelected));
            OnPropertyChanged(nameof(CanToggleCurrentPageSelection));
        };

        ExportModItems.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SelectedExportModCount));
            OnPropertyChanged(nameof(TotalExportModCount));
            OnPropertyChanged(nameof(CanStartExport));
            OnPropertyChanged(nameof(ExportProgressText));
        };

        ReloadFromSettings();
        SelectedSection = "Overview";
    }

    public void ReloadFromSettings(bool reloadModsWhenActive = false)
    {
        var settings = _settingsStore.Load();
        SelectedLaunchMode = string.IsNullOrWhiteSpace(settings.PreferredLaunchMode)
            ? "自动"
            : settings.PreferredLaunchMode;
        EnableSafeLaunch = settings.EnableSafeLaunch;
        InstanceName = settings.InstanceName;
        InstanceDescription = settings.InstanceDescription;
        if (string.IsNullOrWhiteSpace(ModpackName) || string.Equals(ModpackName, "我的整合包", StringComparison.Ordinal))
        {
            ModpackName = string.IsNullOrWhiteSpace(settings.InstanceName) ? "我的整合包" : settings.InstanceName;
        }

        if (string.IsNullOrWhiteSpace(ModpackAuthor))
        {
            ModpackAuthor = Environment.UserName;
        }

        GameWindowTitle = string.IsNullOrWhiteSpace(settings.GameWindowTitle) ? "<default>" : settings.GameWindowTitle;
        InstanceCustomLaunchArguments = settings.InstanceCustomLaunchArguments;
        IsFavoriteInstance = settings.IsFavoriteInstance;
        OverrideSteamLaunchOptions = settings.OverrideSteamLaunchOptions;
        SteamLaunchOptions = settings.SteamLaunchOptions;

        RefreshDetectedPathCore(updateStatus: false);

        if (string.IsNullOrWhiteSpace(SteamLaunchOptions))
        {
            SteamLaunchOptions = DefaultSteamLaunchOptions;
        }

        OnPropertyChanged(nameof(DefaultSteamLaunchOptions));
        OnPropertyChanged(nameof(SteamLaunchOptionsPreview));

        if (reloadModsWhenActive && IsModManageSection)
        {
            ReloadMods();
        }
    }

    private void ApplyLocalizedTexts()
    {
        _titleText = L("VersionSettings.Title", "版本设置");
        _descriptionText = L("VersionSettings.Description", "实例级配置。");
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));

        GeneralSectionButtonText = L("VersionSettings.Tab.General", "版本设置");
        ModManageSectionButtonText = L("VersionSettings.Tab.ModManage", "Mod管理");
        DetectedGamePathLabelText = L("VersionSettings.General.DetectedPathLabel", "探测到的游戏目录");
        DefaultLaunchModeLabelText = L("VersionSettings.General.LaunchModeLabel", "默认启动模式");
        SafeLaunchCheckBoxText = L("VersionSettings.General.SafeLaunch", "启用安全启动（保守参数）");
        RefreshDetectedPathButtonText = L("VersionSettings.General.RefreshPath", "刷新探测");
        SaveVersionSettingsButtonText = L("VersionSettings.General.Save", "保存设置");

        ModManageTitleText = L("VersionSettings.ModManage.Title", "Mod 管理");
        ReloadModsButtonText = L("VersionSettings.ModManage.Reload", "刷新列表");
        OpenModsFolderButtonText = L("VersionSettings.ModManage.OpenFolder", "打开 Mods 文件夹");
        SelectedModTitleText = L("VersionSettings.ModManage.SelectedTitle", "当前选中 Mod");
        ModOperationsHintText = L("VersionSettings.ModManage.OperationsHint", "当前 Mod 操作（启用/禁用会直接改目录后缀）：");
        EnableModButtonText = L("VersionSettings.ModManage.Enable", "启用");
        DisableModButtonText = L("VersionSettings.ModManage.Disable", "禁用");
        UninstallModButtonText = L("VersionSettings.ModManage.Uninstall", "卸载");
        CheckUpdateModButtonText = L("VersionSettings.ModManage.CheckUpdate", "检查更新");
        EmptyModsHintText = L("VersionSettings.ModManage.EmptyHint", "当前未检测到 Mod。可先在下载页安装，或通过“打开 Mods 文件夹”手动放入。");
        ModColumnTitleText = L("VersionSettings.ModManage.Column.Mod", "Mod");
        VersionColumnTitleText = L("VersionSettings.ModManage.Column.Version", "版本");
        StatusColumnTitleText = L("VersionSettings.ModManage.Column.Status", "状态");
        UpdateColumnTitleText = L("VersionSettings.ModManage.Column.Update", "更新");
        ModDescriptionLabelText = L("VersionSettings.ModManage.DescriptionLabel", "描述：");

        OverviewNavText = L("VersionSettings.Nav.Overview", "概览");
        AutoInstallNavText = L("VersionSettings.Nav.AutoInstall", "自动安装");
        ModManageNavText = L("VersionSettings.Nav.ModManage", "Mod管理");
        InstanceSettingsNavText = L("VersionSettings.Nav.Settings", "设置");
        ExportNavText = L("VersionSettings.Nav.Export", "导出");
        OverviewVersionCardTitleText = L("VersionSettings.Overview.VersionInfoTitle", "版本信息");
        OverviewPersonalizationCardTitleText = L("VersionSettings.Overview.PersonalizationTitle", "个性化");
        InstanceNameLabelText = L("VersionSettings.Overview.InstanceNameLabel", "版本名称");
        InstanceDescriptionLabelText = L("VersionSettings.Overview.InstanceDescriptionLabel", "版本描述");
        FavoriteInstanceLabelText = L("VersionSettings.Overview.FavoriteLabel", "收藏实例");
        SavePersonalizationButtonText = L("VersionSettings.Overview.SavePersonalization", "保存个性化");
        ChangeIconButtonText = L("VersionSettings.Overview.ChangeIcon", "更改图标");
        OverviewShortcutsCardTitleText = L("VersionSettings.Overview.ShortcutsTitle", "快捷方式");
        OpenInstanceFolderButtonText = L("VersionSettings.Overview.Shortcut.InstanceFolder", "版本文件夹");
        OpenSaveFolderButtonText = L("VersionSettings.Overview.Shortcut.SaveFolder", "存档文件夹");
        OpenModsFolderShortcutButtonText = L("VersionSettings.Overview.Shortcut.ModsFolder", "Mod 文件夹");
        OverviewAdvancedCardTitleText = L("VersionSettings.Overview.AdvancedTitle", "高级管理");
        AdvancedWarningText = L("VersionSettings.Overview.AdvancedWarning", "危险操作区域：以下操作不可逆，请谨慎操作");
        UninstallBaseSmapiButtonText = L("VersionSettings.Overview.UninstallBaseSmapi", "卸载 Base SMAPI");
        DeleteCurrentVersionButtonText = L("VersionSettings.Overview.DeleteVersion", "删除当前版本");
        DeleteCurrentVersionHintText = L("VersionSettings.Overview.DeleteVersionHint", "Base 版本不可删除，仅可卸载其 SMAPI。");
        AutoInstallInfoTitleText = L("VersionSettings.AutoInstall.OptionsTitle", "安装选项");
        ChangeSmapiButtonText = L("VersionSettings.AutoInstall.ChangeSmapi", "安装 / 更改 SMAPI");
        SmapiGuideText = L("VersionSettings.AutoInstall.Guide", "点击上方按钮可以选择并安装不同版本的 SMAPI。安装将覆盖当前版本。");
        SwitchToSmapiButtonText = L("VersionSettings.AutoInstall.SwitchToSmapi", "切换到 SMAPI");
        SwitchToSmapiTipText = L("VersionSettings.AutoInstall.SwitchTip", "检测到该路径已安装SMAPI，切换到SMAPI版本以启用Mod管理功能。");
        InstanceSettingsTitleText = L("VersionSettings.Settings.Title", "实例设置");
        SaveInstanceSettingsButtonText = L("VersionSettings.Settings.Save", "保存实例设置");
        ExportSectionTitleText = L("VersionSettings.Export.Title", "导出");
        ExportCurrentModsButtonText = L("VersionSettings.Export.CurrentMods", "导出当前 Mods");
        OpenExportFolderButtonText = L("VersionSettings.Export.OpenFolder", "打开导出目录");

        OnPropertyChanged(nameof(ModsSummary));
        OnPropertyChanged(nameof(SelectedModDetails));
        OnPropertyChanged(nameof(ExportHint));
        RefreshModManageHint();
        NotifyOverviewActionStateChanged();
    }

    private string L(string key, string fallback)
    {
        var value = _localizationService.Get(key);
        return string.Equals(value, key, StringComparison.Ordinal) ? fallback : value;
    }

    private string F(string key, string fallback, params object[] args)
    {
        return string.Format(L(key, fallback), args);
    }

    private void NotifyInstanceContextChanged()
    {
        InstanceContextChanged?.Invoke();
    }

    public void SwitchToGeneral()
    {
        SelectedSection = "Overview";
        IsModManageSection = false;
        Status = "当前处于版本设置";
    }

    public void SwitchToOverview()
    {
        SwitchToGeneral();
    }

    public void SwitchToModManage()
    {
        SelectedSection = "ModManage";
        IsModManageSection = true;
    }

    public void SwitchToSettings()
    {
        SelectedSection = "Settings";
        IsModManageSection = false;
        Status = "当前处于实例设置";
    }

    public void SwitchToExport()
    {
        SelectedSection = "Export";
        IsModManageSection = false;
        ReloadExportModItems();
        Status = "当前处于导出页面";
    }

    public void SwitchToAutoInstall()
    {
        SelectedSection = "AutoInstall";
        IsModManageSection = false;
        Status = "自动安装 SMAPI：选择版本并下载安装";
    }

    partial void OnSelectedSectionChanged(string value)
    {
        OnPropertyChanged(nameof(IsOverviewSection));
        OnPropertyChanged(nameof(IsAutoInstallSection));
        OnPropertyChanged(nameof(IsSettingsSection));
        OnPropertyChanged(nameof(IsExportSection));
        OnPropertyChanged(nameof(IsGeneralSection));

        if (string.Equals(value, "ModManage", StringComparison.Ordinal))
        {
            IsModManageSection = true;
            ReloadMods();
            return;
        }

        if (string.Equals(value, "Export", StringComparison.Ordinal))
        {
            ReloadExportModItems();
        }

        IsModManageSection = false;
    }

    partial void OnIncludeModsChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowExportModList));
        OnPropertyChanged(nameof(CanStartExport));
    }

    partial void OnIsExportingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartExport));
        OnPropertyChanged(nameof(ExportProgressText));
    }

    partial void OnExportProgressChanged(int value)
    {
        OnPropertyChanged(nameof(ExportProgressText));
    }

    partial void OnLastExportPathChanged(string value)
    {
        OnPropertyChanged(nameof(ExportHint));
    }

    partial void OnIsModManageSectionChanged(bool value)
    {
        OnPropertyChanged(nameof(IsGeneralSection));
        OnPropertyChanged(nameof(ShowSelectionActionsBar));
    }

    partial void OnSelectedLaunchModeChanged(string value)
    {
        RefreshInstanceRuntimeInfo();
        OnPropertyChanged(nameof(DefaultSteamLaunchOptions));
        OnPropertyChanged(nameof(SteamLaunchOptionsPreview));
    }

    partial void OnOverrideSteamLaunchOptionsChanged(bool value)
    {
        OnPropertyChanged(nameof(SteamLaunchOptionsPreview));
    }

    partial void OnSteamLaunchOptionsChanged(string value)
    {
        OnPropertyChanged(nameof(SteamLaunchOptionsPreview));
    }

    partial void OnInstanceCustomLaunchArgumentsChanged(string value)
    {
        OnPropertyChanged(nameof(DefaultSteamLaunchOptions));
        OnPropertyChanged(nameof(SteamLaunchOptionsPreview));
    }

    partial void OnGameWindowTitleChanged(string value)
    {
        OnPropertyChanged(nameof(DefaultSteamLaunchOptions));
        OnPropertyChanged(nameof(SteamLaunchOptionsPreview));
    }

    partial void OnIsSmapiInstanceChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSmapiVersion));
        OnPropertyChanged(nameof(ShowNoSmapiHint));
        OnPropertyChanged(nameof(ShowBaseModeModWarning));
        OnPropertyChanged(nameof(ShowSwitchToSmapiHint));
        OnPropertyChanged(nameof(CanManageMods));
        OnPropertyChanged(nameof(CanChangeSmapiVersion));
    }

    partial void OnHasInstalledSmapiChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowBaseModeModWarning));
        OnPropertyChanged(nameof(ShowSwitchToSmapiHint));
        OnPropertyChanged(nameof(CanChangeSmapiVersion));
        NotifyOverviewActionStateChanged();
    }

    partial void OnIsInstallingSmapiChanged(bool value)
    {
        OnPropertyChanged(nameof(CanChangeSmapiVersion));
    }

    private void NotifyOverviewActionStateChanged()
    {
        OnPropertyChanged(nameof(CurrentInstanceFolderPath));
        OnPropertyChanged(nameof(SaveFolderPath));
        OnPropertyChanged(nameof(ModsFolderPath));
        OnPropertyChanged(nameof(HasSaveFolder));
        OnPropertyChanged(nameof(HasModsFolder));
        OnPropertyChanged(nameof(CanDeleteCurrentVersion));
        OnPropertyChanged(nameof(CanUninstallBaseSmapi));
    }

    partial void OnSelectedModChanged(ModManageItem? value)
    {
        OnPropertyChanged(nameof(HasSelectedMod));
        OnPropertyChanged(nameof(CanOperateSelectedMod));
        OnPropertyChanged(nameof(CanEnableSelectedMod));
        OnPropertyChanged(nameof(CanDisableSelectedMod));
        OnPropertyChanged(nameof(CanBatchAddSelectedTags));
        OnPropertyChanged(nameof(CanBatchRemoveSelectedTags));
        OnPropertyChanged(nameof(SelectedModDetails));
        RefreshModManageHint();
    }

    partial void OnSelectedTagFilterChanged(ModTagFilterOption? value)
    {
        OnPropertyChanged(nameof(CanApplyTagSelectionToSearch));
        OnPropertyChanged(nameof(TagBatchActionText));
        OnPropertyChanged(nameof(ShowTagBatchAction));
    }

    partial void OnSelectedCustomTagChanged(ModTagFilterOption? value)
    {
        RenameCustomTagName = value?.Name ?? string.Empty;
        OnPropertyChanged(nameof(HasSelectedCustomTag));
    }

    partial void OnCurrentModManageTabChanged(ModManagePrimaryTab value)
    {
        CurrentPageIndex = 1;
        ClearSelection();
        OnPropertyChanged(nameof(IsModsTab));
        OnPropertyChanged(nameof(IsBackupTab));
        OnPropertyChanged(nameof(HasMods));
        OnPropertyChanged(nameof(ModsSummary));
        OnPropertyChanged(nameof(CurrentEmptyHintText));
        OnPropertyChanged(nameof(CanOperateSelectedMod));
        OnPropertyChanged(nameof(CanEnableSelectedMod));
        OnPropertyChanged(nameof(CanDisableSelectedMod));
        OnPropertyChanged(nameof(ShowTagBatchAction));
        OnPropertyChanged(nameof(TagBatchActionText));
        ApplyTagFilter();
    }

    partial void OnEmptyModsHintTextChanged(string value)
    {
        OnPropertyChanged(nameof(CurrentEmptyHintText));
    }

    partial void OnCurrentModSubFilterChanged(ModManageSubFilter value)
    {
        CurrentPageIndex = 1;
        OnPropertyChanged(nameof(IsAllFilter));
        OnPropertyChanged(nameof(IsEnabledFilter));
        OnPropertyChanged(nameof(IsDisabledFilter));
        OnPropertyChanged(nameof(IsUpdatableFilter));
        if (IsModsTab)
        {
            ApplyTagFilter();
        }
    }

    partial void OnIsTagPanelExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(TagPanelToggleText));
    }

    partial void OnSearchKeywordChanged(string value)
    {
        CurrentPageIndex = 1;
        ApplyTagFilter();
    }

    partial void OnTagSearchKeywordChanged(string value)
    {
        RefreshTagFilters();
        OnPropertyChanged(nameof(CanApplyTagSelectionToSearch));
        OnPropertyChanged(nameof(ShowTagBatchAction));
        OnPropertyChanged(nameof(TagBatchActionText));
    }

    partial void OnShowFolderTagsChanged(bool value)
    {
        RefreshTagFilters();
    }

    partial void OnShowPrefixTagsChanged(bool value)
    {
        RefreshTagFilters();
    }

    partial void OnShowCustomTagsChanged(bool value)
    {
        RefreshTagFilters();
    }

    partial void OnCurrentPageIndexChanged(int value)
    {
        RefreshPagedMods();
        UpdatePageNumbers();
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));
        OnPropertyChanged(nameof(PageInfo));
        OnPropertyChanged(nameof(CanToggleCurrentPageSelection));
    }

    partial void OnTotalPagesChanged(int value)
    {
        UpdatePageNumbers();
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));
        OnPropertyChanged(nameof(PageInfo));
        OnPropertyChanged(nameof(CanToggleCurrentPageSelection));
    }

    private void RefreshModManageHint(string? overrideHint = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideHint))
        {
            ModManageHint = overrideHint;
            return;
        }

        if (SelectedMod == null)
        {
            if (IsBackupTab)
            {
                ModManageHint = ShowEmptyModsHint
                    ? "当前没有备份记录，可在 Mods 标签页执行备份"
                    : "请选择一个备份执行恢复或删除";
            }
            else
            {
                ModManageHint = ShowEmptyModsHint
                    ? "未检测到 Mod，可前往下载页安装后再返回管理"
                    : "请选择一个 Mod 进行启用、禁用或卸载";
            }
            return;
        }

        ModManageHint = $"已选中：{SelectedMod.DisplayName}（{SelectedMod.EnableStateText}，{SelectedMod.UpdateStatus}）";
    }

    private void AttachModItem(ModManageItem item)
    {
        item.PropertyChanged += HandleModItemPropertyChanged;
    }

    private void DetachModItem(ModManageItem item)
    {
        item.PropertyChanged -= HandleModItemPropertyChanged;
    }

    private void HandleModItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(ModManageItem.IsEnabled), StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(ModsSummary));
            OnPropertyChanged(nameof(EnabledModsCount));
            OnPropertyChanged(nameof(DisabledModsCount));
            OnPropertyChanged(nameof(EnabledFilterText));
            OnPropertyChanged(nameof(DisabledFilterText));
        }

        if (string.Equals(e.PropertyName, nameof(ModManageItem.HasUpdate), StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(UpdatableModsCount));
            OnPropertyChanged(nameof(UpdatableFilterText));
        }

        if (string.Equals(e.PropertyName, nameof(ModManageItem.IsSelected), StringComparison.Ordinal))
        {
            UpdateSelectionState();
        }

        if (ReferenceEquals(sender, SelectedMod) &&
            (string.Equals(e.PropertyName, nameof(ModManageItem.IsEnabled), StringComparison.Ordinal) ||
             string.Equals(e.PropertyName, nameof(ModManageItem.UpdateStatus), StringComparison.Ordinal) ||
             string.Equals(e.PropertyName, nameof(ModManageItem.DisplayName), StringComparison.Ordinal) ||
             string.Equals(e.PropertyName, nameof(ModManageItem.Version), StringComparison.Ordinal) ||
             string.Equals(e.PropertyName, nameof(ModManageItem.DirectoryName), StringComparison.Ordinal)))
        {
            OnPropertyChanged(nameof(SelectedModDetails));
            OnPropertyChanged(nameof(CanEnableSelectedMod));
            OnPropertyChanged(nameof(CanDisableSelectedMod));
            RefreshModManageHint();
        }
    }

    private bool TryGetSelectedModForAction(string actionText, out ModManageItem target)
    {
        if (SelectedMod == null)
        {
            Status = $"请先选择需要{actionText}的 Mod";
            RefreshModManageHint($"请先选择 Mod，再执行{actionText}操作");
            target = null!;
            return false;
        }

        if (string.IsNullOrWhiteSpace(SelectedMod.FullPath) || !Directory.Exists(SelectedMod.FullPath))
        {
            Status = $"{actionText}失败：目标目录不可用";
            RefreshModManageHint("目标目录已丢失，请刷新列表后重试");
            target = null!;
            return false;
        }

        target = SelectedMod;
        return true;
    }

    [RelayCommand]
    private void SwitchToGeneralSection()
    {
        SwitchToOverview();
    }

    [RelayCommand]
    private void SwitchToAutoInstallSection()
    {
        SwitchToAutoInstall();
    }

    [RelayCommand]
    private void SwitchToModManageSection()
    {
        SwitchToModManage();
    }

    [RelayCommand]
    private void SwitchModsTab()
    {
        CurrentModManageTab = ModManagePrimaryTab.Mods;
    }

    [RelayCommand]
    private void SwitchBackupTab()
    {
        CurrentModManageTab = ModManagePrimaryTab.Backup;
    }

    [RelayCommand]
    private void SetAllFilter()
    {
        CurrentModSubFilter = ModManageSubFilter.All;
    }

    [RelayCommand]
    private void SetEnabledFilter()
    {
        CurrentModSubFilter = ModManageSubFilter.Enabled;
    }

    [RelayCommand]
    private void SetDisabledFilter()
    {
        CurrentModSubFilter = ModManageSubFilter.Disabled;
    }

    [RelayCommand]
    private void SetUpdatableFilter()
    {
        CurrentModSubFilter = ModManageSubFilter.Updatable;
    }

    [RelayCommand]
    private void ToggleTagPanelExpanded()
    {
        IsTagPanelExpanded = !IsTagPanelExpanded;
    }

    [RelayCommand]
    private void SwitchToInstanceSettingsSection()
    {
        SwitchToSettings();
    }

    [RelayCommand]
    private void SwitchToExportSection()
    {
        SwitchToExport();
    }

    [RelayCommand]
    private void OpenCurrentInstanceModsFolder()
    {
        if (!TryGetCurrentModsPath(out var modsPath))
        {
            Status = "当前实例目录不可用，无法打开 Mods 文件夹";
            RefreshModManageHint("当前实例目录不可用，请先在版本选择页选中实例");
            return;
        }

        Directory.CreateDirectory(modsPath);
        Status = $"Mod 文件夹: {modsPath}";
        RefreshModManageHint("已打开 Mods 文件夹，可直接拖入或整理 Mod");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = modsPath,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            Status = "打开 Mods 文件夹失败，请手动前往实例目录";
            RefreshModManageHint("打开文件夹失败，请检查系统权限后重试");
        }
    }

    [RelayCommand]
    private void OpenBackupFolder()
    {
        if (!TryGetCurrentModsPath(out var modsPath))
        {
            Status = "当前实例目录不可用，无法打开备份目录";
            return;
        }

        var backupRoot = GetBackupRootPath(modsPath);
        Directory.CreateDirectory(backupRoot);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = backupRoot,
                UseShellExecute = true
            };
            Process.Start(psi);
            Status = "已打开备份目录";
        }
        catch
        {
            Status = "打开备份目录失败";
        }
    }

    [RelayCommand]
    private void ToggleAllSelection()
    {
        if (_filteredSource.Count == 0)
        {
            return;
        }

        var allSelected = _filteredSource.All(item => item.IsSelected);
        foreach (var mod in _filteredSource)
        {
            mod.IsSelected = !allSelected;
        }

        UpdateSelectionState();
    }

    [RelayCommand]
    private async Task CheckAllModsUpdate()
    {
        if (IsBackupTab)
        {
            Status = "备份标签页不支持检测更新";
            return;
        }

        if (IsCheckingModUpdates || IsCheckingLocalization)
        {
            Status = "已有检测任务正在运行，请稍后重试";
            return;
        }

        if (Mods.Count == 0)
        {
            Status = "当前没有可检测的 Mod";
            return;
        }

        IsCheckingModUpdates = true;
        try
        {
            var modsToCheck = Mods.Where(mod => !mod.IsBackupItem).ToList();
            var totalCount = modsToCheck.Count;
            var maxThreads = GetModCheckConcurrency();
            var completedCount = 0;
            var updatableCount = 0;
            var tokenExpiredFlag = 0;

            Status = $"更新检测：准备中 | 0/{totalCount}";
            UpdateCheckProgressText = $"更新检测 - 准备中 | 0/{totalCount}";
            CurrentUpdateProcessingModName = string.Empty;

            using var semaphore = new SemaphoreSlim(maxThreads, maxThreads);
            var tasks = modsToCheck.Select(async mod =>
            {
                await semaphore.WaitAsync();
                try
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        CurrentUpdateProcessingModName = mod.DisplayName;
                    });

                    var checkResult = await CheckUpdateForModAsync(mod);
                    if (checkResult == null)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            mod.HasUpdate = false;
                            mod.UpdateStatus = "缺少来源信息";
                        });
                        return;
                    }

                    if (checkResult.HasUpdate)
                    {
                        Interlocked.Increment(ref updatableCount);
                    }

                    if (checkResult.IsTokenExpired)
                    {
                        Interlocked.Exchange(ref tokenExpiredFlag, 1);
                    }

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        mod.HasUpdate = checkResult.HasUpdate;
                        mod.CurseforgeProjectId = FirstNonEmpty(checkResult.CurseforgeProjectId, mod.CurseforgeProjectId);
                        mod.NexusModsProjectId = FirstNonEmpty(checkResult.NexusModsProjectId, mod.NexusModsProjectId);
                        mod.UpdateSource = FirstNonEmpty(checkResult.UpdateSource, mod.UpdateSource);
                        mod.UpdateStatus = BuildUpdateStatusText(checkResult);
                        mod.LatestVersion = checkResult.LatestVersion;
                        mod.UpdateUrl = checkResult.UpdateUrl;
                    });
                }
                catch
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        mod.HasUpdate = false;
                        mod.UpdateStatus = "检测失败";
                    });
                }
                finally
                {
                    var completed = Interlocked.Increment(ref completedCount);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        Status = $"更新检测：进行中 | {completed}/{totalCount}（可更新 {Volatile.Read(ref updatableCount)}，线程数 {maxThreads}）";
                        UpdateCheckProgressText = $"更新检测 - 当前处理：{mod.DisplayName} | {completed}/{totalCount}";
                    });
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            Status = tokenExpiredFlag == 1
                ? $"更新检测：已完成 | {totalCount}/{totalCount}（可更新 {updatableCount}，线程数 {maxThreads}，Nexus 登录已过期）"
                : $"更新检测：已完成 | {totalCount}/{totalCount}（可更新 {updatableCount}，线程数 {maxThreads}）";
            UpdateCheckProgressText = $"更新检测完成 | {totalCount}/{totalCount}";
            CurrentUpdateProcessingModName = string.Empty;

            RefreshModManageHint("更新检测完成");
        }
        finally
        {
            IsCheckingModUpdates = false;
        }
    }

    /// <summary>
    /// 批量更新：把所有标记为可更新且具有 UpdateUrl 的 Mod 交给 DownloadPage 入队下载。
    /// NexusMods 走 NXM 解析（含浏览器回退），Curseforge 走 HTTP 直链。
    /// </summary>
    [RelayCommand]
    private void BatchUpdateMods()
    {
        if (IsBackupTab)
        {
            Status = "备份标签页不支持批量更新";
            return;
        }

        var updatable = Mods
            .Where(m => !m.IsBackupItem && m.HasUpdate && !string.IsNullOrWhiteSpace(m.UpdateUrl))
            .Select(m => new ModBatchUpdateEntry(m.DisplayName, m.UpdateUrl, m.UpdateSource))
            .ToList();

        if (updatable.Count == 0)
        {
            Status = "没有可批量更新的 Mod（需先执行更新检测且 Mod 具有可用下载链接）";
            return;
        }

        BatchUpdateModsRequested?.Invoke(updatable);
        Status = $"批量更新：已提交 {updatable.Count} 个 Mod 到下载队列";
        RefreshModManageHint($"批量更新：已提交 {updatable.Count} 个 Mod 到下载队列");
    }

    [RelayCommand]
    private async Task CheckAllModsLocalization()
    {
        if (IsBackupTab)
        {
            Status = "备份标签页不支持检测汉化";
            return;
        }

        if (IsCheckingModUpdates || IsCheckingLocalization)
        {
            Status = "已有检测任务正在运行，请稍后重试";
            return;
        }

        if (Mods.Count == 0)
        {
            Status = "当前没有可检测的 Mod";
            return;
        }

        IsCheckingLocalization = true;
        try
        {
            var modsWithSource = Mods.Where(HasAnySourceForLocalization).ToList();
            var totalToCheck = modsWithSource.Count;
            if (totalToCheck == 0)
            {
                Status = "未找到可用于汉化检测的来源信息";
                return;
            }

            var maxThreads = GetModCheckConcurrency();
            var foundCount = 0;
            var checkedCount = 0;
            var outdatedCount = 0;
            var appliedCount = 0;

            Status = $"汉化检测：准备中 | 0/{totalToCheck}";
            LocalizationCheckProgressText = $"汉化检测 - 准备中 | 0/{totalToCheck}";
            CurrentLocalizationProcessingModName = string.Empty;

            using var semaphore = new SemaphoreSlim(maxThreads, maxThreads);
            var tasks = modsWithSource.Select(async mod =>
            {
                await semaphore.WaitAsync();
                try
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        CurrentLocalizationProcessingModName = mod.DisplayName;
                    });

                    var sourceInfo = TryGetLocalizationSourceInfo(mod);
                    var localization = await TryFetchLocalizationEntryAsync(sourceInfo, mod.UniqueId);

                    if (localization != null)
                    {
                        Interlocked.Increment(ref foundCount);
                        if (ShouldApplyLocalization(mod, localization))
                        {
                            Interlocked.Increment(ref outdatedCount);
                            if (await ApplyLocalizationEntryToModAsync(mod, localization, sourceInfo))
                            {
                                Interlocked.Increment(ref appliedCount);
                            }
                        }
                    }
                }
                catch
                {
                    // ignore single-mod localization failures
                }
                finally
                {
                    var checkedNow = Interlocked.Increment(ref checkedCount);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        Status = $"汉化检测：进行中 | {checkedNow}/{totalToCheck}（命中 {Volatile.Read(ref foundCount)}，可更新 {Volatile.Read(ref outdatedCount)}，已应用 {Volatile.Read(ref appliedCount)}，线程数 {maxThreads}）";
                        LocalizationCheckProgressText = $"汉化检测 - 当前处理：{mod.DisplayName} | {checkedNow}/{totalToCheck}";
                    });
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
            Status = $"汉化检测：已完成 | {checkedCount}/{totalToCheck}（命中 {foundCount}，可更新 {outdatedCount}，已应用 {appliedCount}）";
            LocalizationCheckProgressText = $"汉化检测完成 | {checkedCount}/{totalToCheck}";
            CurrentLocalizationProcessingModName = string.Empty;
            RefreshModManageHint("汉化检测完成");
        }
        finally
        {
            IsCheckingLocalization = false;
        }
    }

    [RelayCommand]
    private async Task InstallModFromLocal()
    {
        if (!TryGetCurrentModsPath(out var modsPath))
        {
            Status = "当前实例目录不可用，无法从本地安装";
            return;
        }

        Directory.CreateDirectory(modsPath);

        var importZip = await _dialogService.ShowConfirmAsync(
            "从本地安装 Mod",
            "点击“确定”选择压缩包（zip）；点击“取消”选择文件夹。") ;

        string? sourcePath;
        if (importZip)
        {
            sourcePath = await _dialogService.BrowseFilePathAsync(
                "选择 Mod 压缩包",
                [
                    new global::Avalonia.Platform.Storage.FilePickerFileType("压缩包")
                    {
                        Patterns = ["*.zip"],
                        MimeTypes = ["application/zip"]
                    }
                ]);
        }
        else
        {
            sourcePath = await _dialogService.BrowseFolderPathAsync("选择 Mod 文件夹");
        }

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        var imported = ImportModsFromLocalSource(sourcePath, modsPath, importZip);
        ReloadMods();
        Status = imported > 0
            ? $"已从本地安装 {imported} 个 Mod"
            : "未检测到可安装的 Mod（请确认包含 manifest.json）";
    }

    [RelayCommand]
    private void BackupSelectedMods()
    {
        if (!TryGetCurrentModsPath(out var modsPath))
        {
            Status = "当前实例目录不可用，无法执行备份";
            return;
        }

        var selectedMods = GetEffectiveSelectedMods()
            .Where(mod => !mod.IsBackupItem)
            .ToList();
        if (selectedMods.Count == 0)
        {
            Status = "请先选择至少一个 Mod 再执行备份";
            return;
        }

        var backupRoot = GetBackupRootPath(modsPath);
        Directory.CreateDirectory(backupRoot);

        var success = 0;
        foreach (var mod in selectedMods)
        {
            if (TryBackupMod(mod, backupRoot))
            {
                success++;
            }
        }

        LoadBackups(modsPath);
        ApplyTagFilter();
        Status = $"已完成备份：{success}/{selectedMods.Count}";
    }

    [RelayCommand]
    private void RestoreSelectedBackups()
    {
        if (!TryGetCurrentModsPath(out var modsPath))
        {
            Status = "当前实例目录不可用，无法恢复备份";
            return;
        }

        var selectedBackups = GetEffectiveSelectedMods()
            .Where(mod => mod.IsBackupItem)
            .ToList();
        if (selectedBackups.Count == 0)
        {
            Status = "请先选择至少一个备份";
            return;
        }

        var backupRoot = GetBackupRootPath(modsPath);
        Directory.CreateDirectory(backupRoot);

        var restored = 0;
        foreach (var backup in selectedBackups)
        {
            if (TryRestoreBackup(backup, modsPath, backupRoot))
            {
                restored++;
            }
        }

        ReloadMods();
        Status = $"已恢复备份：{restored}/{selectedBackups.Count}";
    }

    [RelayCommand]
    private async Task DeleteSelectedBackups()
    {
        var selectedBackups = GetEffectiveSelectedMods()
            .Where(mod => mod.IsBackupItem)
            .ToList();
        if (selectedBackups.Count == 0)
        {
            Status = "请先选择至少一个备份";
            return;
        }

        var confirmed = await _dialogService.ShowConfirmAsync(
            "删除备份",
            $"确定删除选中的 {selectedBackups.Count} 个备份吗？");
        if (!confirmed)
        {
            return;
        }

        var deleted = 0;
        foreach (var backup in selectedBackups)
        {
            if (string.IsNullOrWhiteSpace(backup.FullPath) || !Directory.Exists(backup.FullPath))
            {
                continue;
            }

            try
            {
                Directory.Delete(backup.FullPath, true);
                deleted++;
            }
            catch
            {
                // continue deleting others
            }
        }

        if (TryGetCurrentModsPath(out var modsPath))
        {
            LoadBackups(modsPath);
        }
        ApplyTagFilter();
        Status = $"已删除备份：{deleted}/{selectedBackups.Count}";
    }

    [RelayCommand]
    private void ToggleItemEnabled(ModManageItem? item)
    {
        if (item == null || item.IsBackupItem)
        {
            return;
        }

        SelectedMod = item;
        if (item.IsEnabled)
        {
            DisableSelectedMod();
        }
        else
        {
            EnableSelectedMod();
        }
    }

    [RelayCommand]
    private async Task CheckItemUpdate(ModManageItem? item)
    {
        if (item == null || item.IsBackupItem)
        {
            return;
        }

        SelectedMod = item;
        await CheckUpdateSelectedMod();
    }

    [RelayCommand]
    private void BackupItem(ModManageItem? item)
    {
        if (item == null || item.IsBackupItem)
        {
            return;
        }

        if (!TryGetCurrentModsPath(out var modsPath))
        {
            Status = "当前实例目录不可用，无法执行备份";
            return;
        }

        var backupRoot = GetBackupRootPath(modsPath);
        Directory.CreateDirectory(backupRoot);

        var success = TryBackupMod(item, backupRoot);
        LoadBackups(modsPath);
        ApplyTagFilter();
        Status = success ? $"已备份：{item.DisplayName}" : $"备份失败：{item.DisplayName}";
    }

    [RelayCommand]
    private void RestoreItem(ModManageItem? item)
    {
        if (item == null || !item.IsBackupItem)
        {
            return;
        }

        if (!TryGetCurrentModsPath(out var modsPath))
        {
            Status = "当前实例目录不可用，无法恢复备份";
            return;
        }

        var backupRoot = GetBackupRootPath(modsPath);
        Directory.CreateDirectory(backupRoot);
        var success = TryRestoreBackup(item, modsPath, backupRoot);
        ReloadMods();
        Status = success ? $"已恢复备份：{item.DisplayName}" : $"恢复失败：{item.DisplayName}";
    }

    [RelayCommand]
    private async Task DeleteItem(ModManageItem? item)
    {
        if (item == null)
        {
            return;
        }

        if (item.IsBackupItem)
        {
            var confirmed = await _dialogService.ShowConfirmAsync("删除备份", $"确定删除备份“{item.DisplayName}”吗？");
            if (!confirmed)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(item.FullPath) && Directory.Exists(item.FullPath))
            {
                Directory.Delete(item.FullPath, true);
            }

            if (TryGetCurrentModsPath(out var modsPath))
            {
                LoadBackups(modsPath);
            }

            ApplyTagFilter();
            Status = $"已删除备份：{item.DisplayName}";
            return;
        }

        var approveDelete = await _dialogService.ShowConfirmAsync("卸载 Mod", $"确定卸载“{item.DisplayName}”吗？");
        if (!approveDelete)
        {
            return;
        }

        SelectedMod = item;
        UninstallSelectedMod();
    }

    [RelayCommand]
    private void OpenItemFolder(ModManageItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.FullPath) || !Directory.Exists(item.FullPath))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = item.FullPath,
                UseShellExecute = true
            });
        }
        catch
        {
            // optional action
        }
    }

    [RelayCommand]
    private async Task ShowLocalItemDetail(ModManageItem? item)
    {
        if (item == null)
        {
            return;
        }

        await _dialogService.ShowLocalModDetailDialogAsync(
            modName: item.DisplayName,
            version: item.Version,
            author: item.Author,
            description: item.Description,
            folderPath: item.FullPath,
            sourceFileName: item.SourceFileName,
            uniqueId: item.UniqueId,
            isEnabled: item.IsEnabled,
            hasUpdate: item.HasUpdate,
            dependencies: item.DisplayDependencies,
            onDependencyClick: parameter =>
            {
                if (parameter is not ModDependencyDisplayItem dependency)
                {
                    return;
                }

                if (IsBackupTab)
                {
                    CurrentModManageTab = ModManagePrimaryTab.Mods;
                }

                ClickDependencySearch(dependency);
            },
            title: item.IsBackupItem ? "备份详情" : "本地 Mod 详情");
    }

    [RelayCommand]
    private void ShowOnlineItemDetail(ModManageItem? item)
    {
        if (item == null)
        {
            return;
        }

        var displayName = string.IsNullOrWhiteSpace(item.DisplayName) ? item.UniqueId : item.DisplayName;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            Status = "无法打开详情：缺少资源标识";
            return;
        }

        var detailToken = BuildOnlineDetailToken(item, displayName);
        OpenDetailsRequested?.Invoke(detailToken);

        // 保留外部链接兜底，便于快速跳转 Nexus 搜索页。
        var targetUrl = BuildOnlineDetailFallbackUrl(item, displayName);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = targetUrl,
                UseShellExecute = true
            });
        }
        catch
        {
            // ignore optional action failure
        }

        Status = $"已打开详情：{item.DisplayName}";
    }

    private static string BuildOnlineDetailToken(ModManageItem item, string displayName)
    {
        if (long.TryParse(item.NexusModsProjectId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nexusId) && nexusId > 0)
        {
            return $"[NexusMods#{nexusId}] {displayName}";
        }

        if (long.TryParse(item.CurseforgeProjectId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var curseId) && curseId > 0)
        {
            return $"[Curseforge#{curseId}] {displayName}";
        }

        if (!string.IsNullOrWhiteSpace(item.UpdateSource) && item.UpdateSource.Contains("nexus", StringComparison.OrdinalIgnoreCase))
        {
            return $"[NexusMods] {displayName}";
        }

        if (!string.IsNullOrWhiteSpace(item.UpdateSource) && item.UpdateSource.Contains("curse", StringComparison.OrdinalIgnoreCase))
        {
            return $"[Curseforge] {displayName}";
        }

        var searchKey = string.IsNullOrWhiteSpace(item.UniqueId) ? displayName : item.UniqueId;
        return searchKey;
    }

    private static string BuildOnlineDetailFallbackUrl(ModManageItem item, string displayName)
    {
        if (long.TryParse(item.NexusModsProjectId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nexusId) && nexusId > 0)
        {
            return $"https://www.nexusmods.com/stardewvalley/mods/{nexusId}";
        }

        if (long.TryParse(item.CurseforgeProjectId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var curseId) && curseId > 0)
        {
            return $"https://www.curseforge.com/projects/{curseId}";
        }

        var searchKey = string.IsNullOrWhiteSpace(item.UniqueId) ? displayName : item.UniqueId;
        return $"https://www.nexusmods.com/stardewvalley/search/?gsearch={Uri.EscapeDataString(searchKey)}&gsearchtype=mods";
    }

    private static bool TryBackupMod(ModManageItem mod, string backupRoot)
    {
        if (string.IsNullOrWhiteSpace(mod.FullPath) || !Directory.Exists(mod.FullPath))
        {
            return false;
        }

        var originalFolderName = Path.GetFileName(mod.FullPath);
        if (string.IsNullOrWhiteSpace(originalFolderName))
        {
            return false;
        }

        var snapshotName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{SanitizeFileName(originalFolderName)}_{Guid.NewGuid().ToString("N")[..6]}";
        var snapshotDir = Path.Combine(backupRoot, snapshotName);

        try
        {
            CopyDirectory(mod.FullPath, snapshotDir);
            var record = new ModBackupRecord
            {
                OriginalFolderName = originalFolderName,
                DisplayName = mod.DisplayName,
                Version = mod.Version,
                Author = mod.Author,
                Description = mod.Description,
                UniqueId = mod.UniqueId,
                CreatedAt = DateTime.Now
            };

            var metaPath = Path.Combine(snapshotDir, BackupMetaFileName);
            File.WriteAllText(metaPath, JsonSerializer.Serialize(record, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryRestoreBackup(ModManageItem backup, string modsPath, string backupRoot)
    {
        if (string.IsNullOrWhiteSpace(backup.FullPath) || !Directory.Exists(backup.FullPath))
        {
            return false;
        }

        var targetName = string.IsNullOrWhiteSpace(backup.BackupOriginalFolderName)
            ? backup.FolderName
            : backup.BackupOriginalFolderName;
        if (string.IsNullOrWhiteSpace(targetName))
        {
            return false;
        }

        var targetPath = Path.Combine(modsPath, targetName);
        try
        {
            if (Directory.Exists(targetPath))
            {
                var conflictPath = Path.Combine(
                    backupRoot,
                    $"conflict_{DateTime.Now:yyyyMMdd_HHmmss}_{SanitizeFileName(targetName)}");
                Directory.Move(targetPath, conflictPath);
            }

            CopyDirectory(backup.FullPath, targetPath);
            var copiedMetaPath = Path.Combine(targetPath, BackupMetaFileName);
            if (File.Exists(copiedMetaPath))
            {
                File.Delete(copiedMetaPath);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int ImportModsFromLocalSource(string sourcePath, string modsPath, bool sourceIsZip)
    {
        var tempRoot = string.Empty;

        try
        {
            var root = sourcePath;
            if (sourceIsZip)
            {
                if (!File.Exists(sourcePath))
                {
                    return 0;
                }

                tempRoot = Path.Combine(Path.GetTempPath(), $"SVL_ModImport_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempRoot);
                ZipFile.ExtractToDirectory(sourcePath, tempRoot, overwriteFiles: true);
                root = tempRoot;
            }
            else if (!Directory.Exists(sourcePath))
            {
                return 0;
            }

            var candidates = CollectLocalImportCandidates(root).ToList();
            if (candidates.Count == 0)
            {
                return 0;
            }

            var imported = 0;
            foreach (var candidate in candidates)
            {
                if (!Directory.Exists(candidate))
                {
                    continue;
                }

                var folderName = Path.GetFileName(candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(folderName))
                {
                    continue;
                }

                var targetPath = Path.Combine(modsPath, folderName);
                if (Directory.Exists(targetPath))
                {
                    targetPath = Path.Combine(modsPath, $"{folderName}_local_{DateTime.Now:yyyyMMdd_HHmmss}");
                }

                CopyDirectory(candidate, targetPath);
                imported++;
            }

            return imported;
        }
        catch
        {
            return 0;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempRoot) && Directory.Exists(tempRoot))
            {
                try
                {
                    Directory.Delete(tempRoot, true);
                }
                catch
                {
                    // ignore cleanup failure
                }
            }
        }
    }

    private static IEnumerable<string> CollectLocalImportCandidates(string rootPath)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(rootPath))
        {
            return result;
        }

        if (File.Exists(Path.Combine(rootPath, "manifest.json")))
        {
            result.Add(rootPath);
        }

        foreach (var path in EnumerateCandidateModDirectories(rootPath))
        {
            result.Add(path);
        }

        return result;
    }

    private bool TryGetCurrentModsPath(out string modsPath)
    {
        var settings = _settingsStore.Load();
        if (string.IsNullOrWhiteSpace(settings.PreferredInstancePath) || !Directory.Exists(settings.PreferredInstancePath))
        {
            modsPath = string.Empty;
            return false;
        }

        modsPath = Path.Combine(settings.PreferredInstancePath, "Mods");
        return true;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            sb.Append(invalid.Contains(ch) ? '_' : ch);
        }

        return sb.ToString();
    }

    private static void CopyDirectory(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);

        foreach (var filePath in Directory.GetFiles(sourcePath))
        {
            var fileName = Path.GetFileName(filePath);
            File.Copy(filePath, Path.Combine(destinationPath, fileName), overwrite: true);
        }

        foreach (var directoryPath in Directory.GetDirectories(sourcePath))
        {
            var directoryName = Path.GetFileName(directoryPath);
            CopyDirectory(directoryPath, Path.Combine(destinationPath, directoryName));
        }
    }

    [RelayCommand]
    private void ReloadMods()
    {
        foreach (var existingItem in Mods)
        {
            DetachModItem(existingItem);
        }

        foreach (var backupItem in BackupMods)
        {
            DetachModItem(backupItem);
        }

        foreach (var panelItem in TagPanelItems)
        {
            DetachTagPanelItem(panelItem);
        }

        Mods.Clear();
        BackupMods.Clear();
        FilteredMods.Clear();
        _filteredSource.Clear();
        TagFilters.Clear();
        CustomTagDefinitions.Clear();
        TagPanelItems.Clear();
        SelectedTagFilter = null;
        SelectedCustomTag = null;
        SelectedMod = null;
        SelectedCount = 0;
        ShowSelectionActions = false;
        CurrentPageIndex = 1;
        TotalPages = 1;
        TotalFilteredCount = 0;

        var settings = _settingsStore.Load();
        if (string.IsNullOrWhiteSpace(settings.PreferredInstancePath) || !Directory.Exists(settings.PreferredInstancePath))
        {
            Status = "当前未选择可用实例，请先在版本选择中选中实例";
            RefreshModManageHint("未找到可用实例，无法加载 Mod 列表");
            return;
        }

        var modsPath = Path.Combine(settings.PreferredInstancePath, "Mods");
        Directory.CreateDirectory(modsPath);

        var dependencyEntriesByPath = new Dictionary<string, List<(string UniqueId, string MinimumVersion, bool IsRequired, string Note)>>(StringComparer.OrdinalIgnoreCase);

        var modDirectories = EnumerateCandidateModDirectories(modsPath)
            .OrderBy(path =>
            {
                var folderName = Path.GetFileName(path);
                return !string.IsNullOrWhiteSpace(folderName) &&
                      IsDisabledFolderName(folderName)
                    ? 1
                    : 0;
            })
            .ThenBy(path =>
            {
                var folderName = Path.GetFileName(path);
                if (string.IsNullOrWhiteSpace(folderName))
                {
                    return string.Empty;
                }

                return NormalizeFolderName(folderName);
            }, StringComparer.OrdinalIgnoreCase);

        foreach (var modDirectory in modDirectories)
        {
            var folderName = Path.GetFileName(modDirectory);
            if (string.IsNullOrWhiteSpace(folderName))
            {
                continue;
            }

            var isEnabled = !IsDisabledFolderName(folderName);
            var actualName = NormalizeFolderName(folderName);

            var manifestPath = Path.Combine(modDirectory, "manifest.json");
            var displayName = actualName;
            var version = "未知版本";
            var author = string.Empty;
            var description = string.Empty;
            var uniqueId = string.Empty;
            var sourceFileName = string.Empty;
            var updateSource = string.Empty;
            var curseforgeProjectId = string.Empty;
            var nexusModsProjectId = string.Empty;
            var localizationUpdatedAt = string.Empty;
            var dependencyEntries = new List<(string UniqueId, string MinimumVersion, bool IsRequired, string Note)>();

            if (File.Exists(manifestPath))
            {
                try
                {
                    using var doc = TryReadManifestDocument(manifestPath);
                    if (doc != null)
                    {
                        var manifestRoot = doc.RootElement;
                        displayName = FirstNonEmpty(GetJsonStringFlexibleByCandidates(manifestRoot, "Name"), displayName);
                        uniqueId = FirstNonEmpty(GetJsonStringFlexibleByCandidates(manifestRoot, "UniqueID", "UniqueId"), uniqueId);
                        version = FirstNonEmpty(GetJsonStringFlexibleByCandidates(manifestRoot, "Version"), version);
                        author = FirstNonEmpty(GetJsonStringFlexibleByCandidates(manifestRoot, "Author"), author);
                        description = FirstNonEmpty(GetJsonStringFlexibleByCandidates(manifestRoot, "Description"), description);

                        ParseModDependencies(manifestRoot, dependencyEntries);
                        TryResolveSourceFromUpdateKeys(manifestRoot, ref curseforgeProjectId, ref nexusModsProjectId, ref updateSource);
                    }
                }
                catch
                {
                    // keep fallback values when manifest parsing fails
                }
            }

            var sourceCredential = TryReadSourceCredential(modDirectory);
            ApplySourceCredentialToModItem(
                sourceCredential,
                ref displayName,
                ref description,
                ref version,
                ref sourceFileName,
                ref updateSource,
                ref curseforgeProjectId,
                ref nexusModsProjectId,
                ref localizationUpdatedAt);

            dependencyEntriesByPath[modDirectory] = dependencyEntries;

            var item = new ModManageItem
            {
                DisplayName = displayName,
                Version = version,
                Author = author,
                Description = description,
                DirectoryName = actualName,
                FolderName = actualName,
                FullPath = modDirectory,
                UniqueId = uniqueId,
                SourceFileName = sourceFileName,
                CurseforgeProjectId = curseforgeProjectId,
                NexusModsProjectId = nexusModsProjectId,
                UpdateSource = updateSource,
                LocalizationUpdatedAt = localizationUpdatedAt,
                IsEnabled = isEnabled,
                UpdateStatus = "未检查"
            };

            foreach (var tag in BuildFolderTagsForMod(modsPath, modDirectory, actualName))
            {
                item.FolderTags.Add(tag);
            }

            AttachModItem(item);
            Mods.Add(item);
        }

        BuildDisplayDependenciesForMods(dependencyEntriesByPath);

        LoadAndApplyTags(modsPath);
        LoadBackups(modsPath);
        ApplyTagFilter();
        SyncExportModItemsFromCurrentMods();

        Status = Mods.Count == 0
            ? "当前实例 Mods 目录为空"
            : $"已加载 {Mods.Count} 个 Mod（启用 {Mods.Count(item => item.IsEnabled)} / 禁用 {Mods.Count(item => !item.IsEnabled)}）";
        RefreshModManageHint();
    }

    private void ReloadExportModItems()
    {
        if (Mods.Count == 0 && TryGetCurrentModsPath(out var modsPath) && Directory.Exists(modsPath))
        {
            ReloadMods();
            return;
        }

        SyncExportModItemsFromCurrentMods();
    }

    private void SyncExportModItemsFromCurrentMods()
    {
        foreach (var existing in ExportModItems)
        {
            DetachExportModItem(existing);
        }

        ExportModItems.Clear();

        var sourceMods = Mods
            .Where(mod => !mod.IsBackupItem)
            .Where(mod => !IsSmapiBundledModForExport(mod.UniqueId, mod.DirectoryName))
            .OrderBy(mod => mod.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var mod in sourceMods)
        {
            var sourceCredential = TryReadSourceCredential(mod.FullPath);
            var sourcePlatform = string.Empty;
            var sourceProjectId = string.Empty;
            var sourceFileId = string.Empty;

            if (sourceCredential != null)
            {
                sourcePlatform = NormalizePlatform(sourceCredential.Platform);
                sourceProjectId = NormalizeProjectId(sourceCredential.ProjectId);
                sourceFileId = FirstNonEmpty(sourceCredential.FileId);
            }

            sourcePlatform = FirstNonEmpty(sourcePlatform, NormalizePlatform(mod.UpdateSource));
            sourceProjectId = FirstNonEmpty(sourceProjectId,
                NormalizeProjectId(mod.CurseforgeProjectId),
                NormalizeProjectId(mod.NexusModsProjectId));

            var exportItem = new ExportModSelectionItem
            {
                Name = FirstNonEmpty(mod.DisplayName, mod.DirectoryName),
                UniqueId = mod.UniqueId,
                Version = mod.Version,
                Author = mod.Author,
                ModPath = mod.FullPath,
                DirectoryName = mod.DirectoryName,
                IsEnabled = mod.IsEnabled,
                IsSelected = mod.IsEnabled,
                SourcePlatform = string.IsNullOrWhiteSpace(sourcePlatform) ? "未知" : sourcePlatform,
                SourceProjectId = sourceProjectId,
                SourceFileId = sourceFileId
            };

            AttachExportModItem(exportItem);
            ExportModItems.Add(exportItem);
        }

        ExportStatusMessage = ExportModItems.Count == 0
            ? "当前实例没有可导出的 Mod"
            : $"导出列表已准备，共 {ExportModItems.Count} 个 Mod";

        OnPropertyChanged(nameof(SelectedExportModCount));
        OnPropertyChanged(nameof(TotalExportModCount));
        OnPropertyChanged(nameof(CanStartExport));
        OnPropertyChanged(nameof(ExportProgressText));
    }

    private static bool IsSmapiBundledModForExport(string? uniqueId, string? folderName)
    {
        if (string.Equals(uniqueId, "SMAPI.ConsoleCommands", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uniqueId, "SMAPI.SaveBackup", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uniqueId, "ConsoleCommands", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uniqueId, "SaveBackup", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(folderName, "ConsoleCommands", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(folderName, "SaveBackup", StringComparison.OrdinalIgnoreCase);
    }

    private void AttachExportModItem(ExportModSelectionItem item)
    {
        item.PropertyChanged += HandleExportModItemPropertyChanged;
    }

    private void DetachExportModItem(ExportModSelectionItem item)
    {
        item.PropertyChanged -= HandleExportModItemPropertyChanged;
    }

    private void HandleExportModItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(ExportModSelectionItem.IsSelected), StringComparison.Ordinal))
        {
            return;
        }

        OnPropertyChanged(nameof(SelectedExportModCount));
        OnPropertyChanged(nameof(CanStartExport));
        OnPropertyChanged(nameof(ExportProgressText));
    }

    private void BuildDisplayDependenciesForMods(IReadOnlyDictionary<string, List<(string UniqueId, string MinimumVersion, bool IsRequired, string Note)>> dependencyEntriesByPath)
    {
        var installedByUniqueId = Mods
            .Where(mod => !string.IsNullOrWhiteSpace(mod.UniqueId))
            .GroupBy(mod => mod.UniqueId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var mod in Mods)
        {
            mod.DisplayDependencies.Clear();

            if (string.IsNullOrWhiteSpace(mod.FullPath) ||
                !dependencyEntriesByPath.TryGetValue(mod.FullPath, out var dependencyEntries) ||
                dependencyEntries == null ||
                dependencyEntries.Count == 0)
            {
                continue;
            }

            var distinctEntries = dependencyEntries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.UniqueId))
                .GroupBy(entry => entry.UniqueId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            foreach (var entry in distinctEntries)
            {
                mod.DisplayDependencies.Add(CreateDependencyLinkForModList(
                    entry.UniqueId,
                    entry.MinimumVersion,
                    installedByUniqueId,
                    entry.Note,
                    entry.IsRequired));
            }
        }
    }

    private static void ParseModDependencies(JsonElement manifestRoot, List<(string UniqueId, string MinimumVersion, bool IsRequired, string Note)> dependencies)
    {
        if (manifestRoot.TryGetProperty("ContentPackFor", out var contentPackForElement) &&
            contentPackForElement.ValueKind == JsonValueKind.Object)
        {
            var contentPackForUniqueId = GetJsonStringByCandidates(contentPackForElement, "UniqueID", "UniqueId");
            if (!string.IsNullOrWhiteSpace(contentPackForUniqueId))
            {
                var contentPackForMinVersion = GetJsonStringByCandidates(contentPackForElement, "MinimumVersion");
                dependencies.Add((contentPackForUniqueId.Trim(), contentPackForMinVersion, true, "内容包前置"));
            }
        }

        if (!manifestRoot.TryGetProperty("Dependencies", out var dependenciesElement) ||
            dependenciesElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var dependency in dependenciesElement.EnumerateArray())
        {
            if (dependency.ValueKind == JsonValueKind.String)
            {
                var dependencyUniqueId = dependency.GetString();
                if (!string.IsNullOrWhiteSpace(dependencyUniqueId))
                {
                    dependencies.Add((dependencyUniqueId.Trim(), string.Empty, true, string.Empty));
                }

                continue;
            }

            if (dependency.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var dependencyId = GetJsonStringByCandidates(dependency, "UniqueID", "UniqueId");
            if (string.IsNullOrWhiteSpace(dependencyId))
            {
                continue;
            }

            var isRequired = GetJsonBoolByCandidates(dependency, "IsRequired") ?? true;
            var minimumVersion = GetJsonStringByCandidates(dependency, "MinimumVersion");
            dependencies.Add((dependencyId.Trim(), minimumVersion, isRequired, isRequired ? string.Empty : "可选前置"));
        }
    }

    private static string GetJsonStringByCandidates(JsonElement element, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (element.TryGetProperty(candidate, out var propertyElement) && propertyElement.ValueKind == JsonValueKind.String)
            {
                return propertyElement.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static bool? GetJsonBoolByCandidates(JsonElement element, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!element.TryGetProperty(candidate, out var propertyElement))
            {
                continue;
            }

            if (propertyElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return propertyElement.GetBoolean();
            }
        }

        return null;
    }

    private static JsonDocument? TryReadManifestDocument(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
        {
            return null;
        }

        var bytes = File.ReadAllBytes(manifestPath);
        var options = new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        };

        return JsonDocument.Parse(bytes, options);
    }

    private static string GetJsonStringFlexibleByCandidates(JsonElement element, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!element.TryGetProperty(candidate, out var propertyElement))
            {
                continue;
            }

            switch (propertyElement.ValueKind)
            {
                case JsonValueKind.String:
                    return propertyElement.GetString() ?? string.Empty;
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return propertyElement.ToString();
                case JsonValueKind.Array:
                {
                    var parts = propertyElement
                        .EnumerateArray()
                        .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .ToList();
                    if (parts.Count > 0)
                    {
                        return string.Join(", ", parts!);
                    }

                    break;
                }
            }
        }

        return string.Empty;
    }

    private static void TryResolveSourceFromUpdateKeys(
        JsonElement manifestRoot,
        ref string curseforgeProjectId,
        ref string nexusModsProjectId,
        ref string updateSource)
    {
        if (!manifestRoot.TryGetProperty("UpdateKeys", out var updateKeysElement) || updateKeysElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var updateKeyElement in updateKeysElement.EnumerateArray())
        {
            if (updateKeyElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var updateKey = updateKeyElement.GetString();
            if (string.IsNullOrWhiteSpace(updateKey))
            {
                continue;
            }

            var parts = updateKey.Split(new[] { ':' }, 2);
            if (parts.Length != 2)
            {
                continue;
            }

            var source = parts[0].Trim();
            var identifier = parts[1].Trim();
            if (string.IsNullOrWhiteSpace(identifier))
            {
                continue;
            }

            if (string.Equals(source, "curseforge", StringComparison.OrdinalIgnoreCase))
            {
                var normalized = NormalizeProjectId(identifier);
                curseforgeProjectId = FirstNonEmpty(normalized, identifier, curseforgeProjectId);
                updateSource = "Curseforge";
                continue;
            }

            if (string.Equals(source, "nexus", StringComparison.OrdinalIgnoreCase))
            {
                var normalized = NormalizeProjectId(identifier);
                nexusModsProjectId = FirstNonEmpty(normalized, identifier, nexusModsProjectId);
                if (string.IsNullOrWhiteSpace(updateSource))
                {
                    updateSource = "NexusMods";
                }
            }
        }
    }

    private static LocalSourceMetadata? TryReadSourceCredential(string modDir)
    {
        if (string.IsNullOrWhiteSpace(modDir) || !Directory.Exists(modDir))
        {
            return null;
        }

        var credential = TryReadSvlSourceMetadata(modDir);
        if (credential != null)
        {
            return credential;
        }

        return TryReadLegacyDotSource(modDir);
    }

    private static LocalSourceMetadata? TryReadSvlSourceMetadata(string modDir)
    {
        try
        {
            var path = Path.Combine(modDir, "svl-source.json");
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<LocalSourceMetadata>(File.ReadAllText(path), s_sourceJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static LocalSourceMetadata? TryReadLegacyDotSource(string modDir)
    {
        try
        {
            var path = Path.Combine(modDir, ".source.json");
            if (!File.Exists(path))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var source = GetJsonStringFlexibleByCandidates(root, "source", "platform");
            var collection = GetJsonStringFlexibleByCandidates(root, "collection", "projectId", "modId");

            if (string.IsNullOrWhiteSpace(source) && string.IsNullOrWhiteSpace(collection))
            {
                return null;
            }

            var normalizedSource = string.Equals(source, "nexus", StringComparison.OrdinalIgnoreCase)
                ? "NexusMods"
                : source;

            return new LocalSourceMetadata
            {
                Platform = string.IsNullOrWhiteSpace(normalizedSource) ? "NexusMods" : normalizedSource,
                ProjectId = collection,
                FileId = GetJsonStringFlexibleByCandidates(root, "file", "fileId")
            };
        }
        catch
        {
            return null;
        }
    }

    private static void ApplySourceCredentialToModItem(
        LocalSourceMetadata? sourceCredential,
        ref string displayName,
        ref string description,
        ref string version,
        ref string sourceFileName,
        ref string updateSource,
        ref string curseforgeProjectId,
        ref string nexusModsProjectId,
        ref string localizationUpdatedAt)
    {
        if (sourceCredential == null)
        {
            return;
        }

        var normalizedPlatform = NormalizePlatform(sourceCredential.Platform);
        if (string.Equals(normalizedPlatform, "Curseforge", StringComparison.OrdinalIgnoreCase))
        {
            var normalized = NormalizeProjectId(sourceCredential.ProjectId);
            curseforgeProjectId = FirstNonEmpty(normalized, sourceCredential.ProjectId, curseforgeProjectId);
            updateSource = "Curseforge";
        }
        else if (string.Equals(normalizedPlatform, "NexusMods", StringComparison.OrdinalIgnoreCase))
        {
            var normalized = NormalizeProjectId(sourceCredential.ProjectId);
            nexusModsProjectId = FirstNonEmpty(normalized, sourceCredential.ProjectId, nexusModsProjectId);
            if (string.IsNullOrWhiteSpace(updateSource))
            {
                updateSource = "NexusMods";
            }
        }

        sourceFileName = FirstNonEmpty(sourceFileName, sourceCredential.FileName);
        version = IsUnknownVersion(version)
            ? FirstNonEmpty(ExtractVersionFromText(sourceCredential.FileName), version)
            : version;
        displayName = FirstNonEmpty(sourceCredential.Localization?.NameZhCn, displayName, sourceCredential.ModName);
        description = FirstNonEmpty(sourceCredential.Localization?.DescriptionZhCn, description);
        localizationUpdatedAt = FirstNonEmpty(sourceCredential.Localization?.UpdatedAt, localizationUpdatedAt);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static bool IsUnknownVersion(string version)
    {
        return string.IsNullOrWhiteSpace(version) ||
               string.Equals(version, "未知版本", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractVersionFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var match = Regex.Match(text, @"\d+(?:\.\d+){1,3}");
        return match.Success ? match.Value : string.Empty;
    }

    private static string NormalizeProjectId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var match = Regex.Match(raw, @"(\d+)(?!.*\d)");
        return match.Success ? match.Groups[1].Value : raw.Trim();
    }

    private static string NormalizePlatform(string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
        {
            return string.Empty;
        }

        if (string.Equals(platform, "Curseforge", StringComparison.OrdinalIgnoreCase))
        {
            return "Curseforge";
        }

        if (string.Equals(platform, "NexusMods", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(platform, "Nexus", StringComparison.OrdinalIgnoreCase))
        {
            return "NexusMods";
        }

        return platform.Trim();
    }

    private async Task<LocalModUpdateCheckResult?> CheckUpdateForModAsync(ModManageItem mod)
    {
        if (mod == null || string.IsNullOrWhiteSpace(mod.FullPath) || !Directory.Exists(mod.FullPath))
        {
            return null;
        }

        var sourceInfo = TryGetLocalizationSourceInfo(mod);
        if (!sourceInfo.HasValue)
        {
            return null;
        }

        var normalizedPlatform = NormalizePlatform(sourceInfo.Value.Platform);
        var normalizedProjectId = NormalizeProjectId(sourceInfo.Value.ProjectId);
        if (string.IsNullOrWhiteSpace(normalizedProjectId))
        {
            return null;
        }

        if (string.Equals(normalizedPlatform, "NexusMods", StringComparison.OrdinalIgnoreCase))
        {
            return await CheckNexusUpdateAsync(mod, normalizedProjectId);
        }

        if (string.Equals(normalizedPlatform, "Curseforge", StringComparison.OrdinalIgnoreCase))
        {
            return await CheckCurseforgeUpdateAsync(mod, normalizedProjectId);
        }

        return null;
    }

    private async Task<LocalModUpdateCheckResult> CheckNexusUpdateAsync(ModManageItem mod, string projectId)
    {
        var result = new LocalModUpdateCheckResult
        {
            UpdateSource = "NexusMods",
            NexusModsProjectId = projectId
        };

        var settings = _settingsStore.Load();
        if (!HasNexusCredential(settings))
        {
            return result;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.nexusmods.com/v1/games/{NexusGameDomain}/mods/{projectId}/files.json");
            ApplyNexusHeaders(request, settings);

            using var response = await s_modNetworkHttp.SendAsync(request);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                result.IsTokenExpired = true;
                return result;
            }

            if (!response.IsSuccessStatusCode)
            {
                return result;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            if (!doc.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            JsonElement? latest = null;
            long latestTimestamp = -1;
            long latestFileId = -1;

            foreach (var file in files.EnumerateArray())
            {
                var timestamp = GetJsonLongByCandidates(file, "uploaded_timestamp", "uploadedTimestamp");
                var fileId = GetJsonLongByCandidates(file, "file_id", "fileId", "id");
                var candidateScore = timestamp > 0 ? timestamp : fileId;
                var currentScore = latestTimestamp > 0 ? latestTimestamp : latestFileId;
                if (latest == null || candidateScore > currentScore)
                {
                    latest = file;
                    latestTimestamp = timestamp;
                    latestFileId = fileId;
                }
            }

            if (latest.HasValue)
            {
                var remoteVersion = FirstNonEmpty(
                    GetJsonStringFlexibleByCandidates(latest.Value, "version"),
                    ExtractVersionFromText(GetJsonStringFlexibleByCandidates(latest.Value, "file_name", "fileName", "name")));
                result.LatestVersion = remoteVersion;
                result.HasUpdate = IsRemoteVersionNewer(mod.Version, remoteVersion);
                result.IsChecked = true;
                result.UpdateFileId = latestFileId;
                // 构造 NXM 链接供批量更新入队（DownloadPage 会解析并下载）
                if (latestFileId > 0)
                {
                    result.UpdateUrl = $"nxm://stardewvalley/mods/{projectId}/files/{latestFileId}";
                }
            }

            return result;
        }
        catch
        {
            return result;
        }
    }

    private static async Task<LocalModUpdateCheckResult> CheckCurseforgeUpdateAsync(ModManageItem mod, string projectId)
    {
        var result = new LocalModUpdateCheckResult
        {
            UpdateSource = "Curseforge",
            CurseforgeProjectId = projectId
        };

        try
        {
            using var response = await s_modNetworkHttp.GetAsync($"https://api.curse.tools/v1/cf/mods/{projectId}/files?index=0&pageSize=30");
            if (!response.IsSuccessStatusCode)
            {
                return result;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            if (!doc.RootElement.TryGetProperty("data", out var files) || files.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            JsonElement? latest = null;
            long latestFileId = -1;
            foreach (var file in files.EnumerateArray())
            {
                var fileId = GetJsonLongByCandidates(file, "id", "fileId");
                if (latest == null || fileId > latestFileId)
                {
                    latest = file;
                    latestFileId = fileId;
                }
            }

            if (latest.HasValue)
            {
                var remoteVersion = FirstNonEmpty(
                    ExtractVersionFromText(GetJsonStringFlexibleByCandidates(latest.Value, "displayName", "display_name")),
                    ExtractVersionFromText(GetJsonStringFlexibleByCandidates(latest.Value, "fileName", "file_name", "name")));
                result.LatestVersion = remoteVersion;
                result.HasUpdate = IsRemoteVersionNewer(mod.Version, remoteVersion);
                result.IsChecked = true;
                // 提取 Curseforge 直链供批量更新入队
                result.UpdateUrl = GetJsonStringFlexibleByCandidates(latest.Value, "downloadUrl", "download_url");
            }

            return result;
        }
        catch
        {
            return result;
        }
    }

    private static bool IsRemoteVersionNewer(string localVersion, string remoteVersion)
    {
        if (string.IsNullOrWhiteSpace(remoteVersion))
        {
            return false;
        }

        if (IsUnknownVersion(localVersion))
        {
            return true;
        }

        if (TryParseComparableVersion(remoteVersion, out var remoteParsed) &&
            TryParseComparableVersion(localVersion, out var localParsed))
        {
            return remoteParsed > localParsed;
        }

        return !string.Equals(
            ExtractVersionFromText(remoteVersion),
            ExtractVersionFromText(localVersion),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseComparableVersion(string? rawVersion, out Version parsed)
    {
        parsed = new Version(0, 0);
        var candidate = FirstNonEmpty(ExtractVersionFromText(rawVersion), rawVersion);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (!Version.TryParse(candidate, out var parsedCandidate) || parsedCandidate == null)
        {
            return false;
        }

        parsed = parsedCandidate;
        return true;
    }

    private static string BuildUpdateStatusText(LocalModUpdateCheckResult result)
    {
        if (result.IsTokenExpired)
        {
            return "Nexus 登录已过期";
        }

        if (!result.IsChecked)
        {
            return string.Equals(result.UpdateSource, "NexusMods", StringComparison.OrdinalIgnoreCase)
                ? "需登录 Nexus"
                : "检测失败";
        }

        if (result.HasUpdate)
        {
            return string.IsNullOrWhiteSpace(result.LatestVersion)
                ? "可更新"
                : $"可更新 -> {result.LatestVersion}";
        }

        if (!string.IsNullOrWhiteSpace(result.UpdateSource) &&
            !string.Equals(result.UpdateSource, "None", StringComparison.OrdinalIgnoreCase))
        {
            return $"已检查({result.UpdateSource}) {DateTime.Now:HH:mm}";
        }

        return $"已检查 {DateTime.Now:HH:mm}";
    }

    private bool HasAnySourceForLocalization(ModManageItem mod)
    {
        if (mod == null || mod.IsBackupItem)
        {
            return false;
        }

        if (TryReadSourceCredential(mod.FullPath) != null)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(mod.CurseforgeProjectId) || !string.IsNullOrWhiteSpace(mod.NexusModsProjectId))
        {
            return true;
        }

        var manifestPath = Path.Combine(mod.FullPath ?? string.Empty, "manifest.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                using var doc = TryReadManifestDocument(manifestPath);
                if (doc != null)
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("UpdateKeys", out var updateKeysElement) && updateKeysElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var updateKeyElement in updateKeysElement.EnumerateArray())
                        {
                            if (updateKeyElement.ValueKind != JsonValueKind.String)
                            {
                                continue;
                            }

                            var updateKey = updateKeyElement.GetString();
                            if (string.IsNullOrWhiteSpace(updateKey))
                            {
                                continue;
                            }

                            if (updateKey.StartsWith("curseforge:", StringComparison.OrdinalIgnoreCase) ||
                                updateKey.StartsWith("nexus:", StringComparison.OrdinalIgnoreCase))
                            {
                                return true;
                            }
                        }
                    }
                }
            }
            catch
            {
                // ignore malformed manifest
            }
        }

        return !string.IsNullOrWhiteSpace(mod.UniqueId);
    }

    private (string Platform, string ProjectId)? TryGetLocalizationSourceInfo(ModManageItem mod)
    {
        if (mod == null || string.IsNullOrWhiteSpace(mod.FullPath))
        {
            return null;
        }

        var credential = TryReadSourceCredential(mod.FullPath);
        if (credential != null && !string.IsNullOrWhiteSpace(credential.Platform) && !string.IsNullOrWhiteSpace(credential.ProjectId))
        {
            return (NormalizePlatform(credential.Platform), NormalizeProjectId(credential.ProjectId));
        }

        var manifestPath = Path.Combine(mod.FullPath, "manifest.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                using var doc = TryReadManifestDocument(manifestPath);
                if (doc != null)
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("UpdateKeys", out var updateKeysElement) && updateKeysElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var updateKeyElement in updateKeysElement.EnumerateArray())
                        {
                            if (updateKeyElement.ValueKind != JsonValueKind.String)
                            {
                                continue;
                            }

                            var updateKey = updateKeyElement.GetString();
                            if (string.IsNullOrWhiteSpace(updateKey))
                            {
                                continue;
                            }

                            var parts = updateKey.Split(new[] { ':' }, 2);
                            if (parts.Length != 2)
                            {
                                continue;
                            }

                            var source = NormalizePlatform(parts[0]);
                            var id = NormalizeProjectId(parts[1]);
                            if ((string.Equals(source, "Curseforge", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(source, "NexusMods", StringComparison.OrdinalIgnoreCase)) &&
                                !string.IsNullOrWhiteSpace(id))
                            {
                                return (source, id);
                            }
                        }
                    }
                }
            }
            catch
            {
                // ignore malformed manifest
            }
        }

        if (!string.IsNullOrWhiteSpace(mod.CurseforgeProjectId))
        {
            return ("Curseforge", NormalizeProjectId(mod.CurseforgeProjectId));
        }

        if (!string.IsNullOrWhiteSpace(mod.NexusModsProjectId))
        {
            return ("NexusMods", NormalizeProjectId(mod.NexusModsProjectId));
        }

        return null;
    }

    private async Task<LocalCommunityLocalizationEntry?> TryFetchLocalizationEntryAsync(
        (string Platform, string ProjectId)? sourceInfo,
        string? uniqueId)
    {
        if (sourceInfo.HasValue)
        {
            var pathBySource = BuildCommunityLocalizationRelativePath(sourceInfo.Value.Platform, sourceInfo.Value.ProjectId);
            if (!string.IsNullOrWhiteSpace(pathBySource))
            {
                var bySource = await FetchCommunityLocalizationByPathAsync(pathBySource);
                if (bySource != null)
                {
                    return bySource;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(uniqueId))
        {
            var pathByUniqueId = BuildCommunityLocalizationRelativePath("UniqueID", uniqueId);
            return await FetchCommunityLocalizationByPathAsync(pathByUniqueId);
        }

        return null;
    }

    private static string BuildCommunityLocalizationRelativePath(string platform, string id)
    {
        var normalizedPlatform = NormalizeCommunityLocalizationPlatform(platform);
        var normalizedId = NormalizeCommunityLocalizationId(id);
        if (string.IsNullOrWhiteSpace(normalizedPlatform) || string.IsNullOrWhiteSpace(normalizedId))
        {
            return string.Empty;
        }

        if (string.Equals(normalizedPlatform, "NexusMods", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedPlatform, "Curseforge", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedPlatform, "UniqueID", StringComparison.OrdinalIgnoreCase))
        {
            return $"Mods/{normalizedPlatform}/{normalizedId}.json";
        }

        return string.Empty;
    }

    private async Task<LocalCommunityLocalizationEntry?> FetchCommunityLocalizationByPathAsync(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        // 优先走 CommunityLocalizationService（带缓存 + 源选择 + 降级）
        var entry = await _communityLocalizationService.GetByRelativePathAsync(relativePath);
        if (entry != null)
        {
            return new LocalCommunityLocalizationEntry
            {
                EntityType = entry.EntityType,
                Platform = entry.Platform,
                Id = entry.Id,
                Name = new LocalCommunityLocalizedText { ZhCn = entry.Name?.ZhCn ?? string.Empty, Source = entry.Name?.Source ?? string.Empty },
                Description = new LocalCommunityLocalizedText { ZhCn = entry.Description?.ZhCn ?? string.Empty, Source = entry.Description?.Source ?? string.Empty },
                Meta = new LocalCommunityLocalizationMeta { Contributor = entry.Meta?.Contributor ?? string.Empty, SourceUrl = entry.Meta?.SourceUrl ?? string.Empty, UpdatedAt = entry.Meta?.UpdatedAt ?? string.Empty }
            };
        }

        return null;
    }

    private static string NormalizeCommunityLocalizationPlatform(string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
        {
            return string.Empty;
        }

        if (string.Equals(platform, "Nexus", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(platform, "NexusMods", StringComparison.OrdinalIgnoreCase))
        {
            return "NexusMods";
        }

        if (string.Equals(platform, "Curseforge", StringComparison.OrdinalIgnoreCase))
        {
            return "Curseforge";
        }

        if (string.Equals(platform, "UniqueID", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(platform, "LocalUniqueID", StringComparison.OrdinalIgnoreCase))
        {
            return "UniqueID";
        }

        return platform.Trim();
    }

    private static string NormalizeCommunityLocalizationId(string? id)
    {
        return string.IsNullOrWhiteSpace(id) ? string.Empty : id.Trim();
    }

    private static bool ShouldApplyLocalization(ModManageItem mod, LocalCommunityLocalizationEntry localization)
    {
        if (mod == null || localization == null)
        {
            return false;
        }

        var remoteUpdatedAt = ParseUpdatedAt(localization.Meta?.UpdatedAt);
        var localUpdatedAt = ParseUpdatedAt(mod.LocalizationUpdatedAt);

        if (!remoteUpdatedAt.HasValue)
        {
            return string.IsNullOrWhiteSpace(mod.LocalizationUpdatedAt);
        }

        if (!localUpdatedAt.HasValue)
        {
            return true;
        }

        return remoteUpdatedAt.Value > localUpdatedAt.Value;
    }

    private static DateTimeOffset? ParseUpdatedAt(string? updatedAt)
    {
        if (string.IsNullOrWhiteSpace(updatedAt))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(updatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private async Task<bool> ApplyLocalizationEntryToModAsync(
        ModManageItem mod,
        LocalCommunityLocalizationEntry localization,
        (string Platform, string ProjectId)? sourceInfo)
    {
        if (mod == null || localization == null || string.IsNullOrWhiteSpace(mod.FullPath))
        {
            return false;
        }

        try
        {
            var platform = sourceInfo?.Platform ?? NormalizeCommunityLocalizationPlatform(localization.Platform);
            var projectId = sourceInfo?.ProjectId ?? NormalizeCommunityLocalizationId(localization.Id);

            var credential = TryReadSourceCredential(mod.FullPath) ?? new LocalSourceMetadata();
            credential.Platform = FirstNonEmpty(platform, credential.Platform);
            credential.ProjectId = FirstNonEmpty(projectId, credential.ProjectId);
            credential.ModName = FirstNonEmpty(credential.ModName, mod.DisplayName);
            credential.Localization = new LocalSourceLocalization
            {
                EntityType = FirstNonEmpty(localization.EntityType, "mod"),
                Platform = FirstNonEmpty(localization.Platform, platform),
                Id = FirstNonEmpty(localization.Id, projectId),
                NameZhCn = localization.Name?.ZhCn ?? string.Empty,
                NameSource = localization.Name?.Source ?? string.Empty,
                DescriptionZhCn = localization.Description?.ZhCn ?? string.Empty,
                DescriptionSource = localization.Description?.Source ?? string.Empty,
                SourceUrl = localization.Meta?.SourceUrl ?? string.Empty,
                UpdatedAt = localization.Meta?.UpdatedAt ?? string.Empty,
                Contributor = localization.Meta?.Contributor ?? string.Empty
            };

            if (!WriteSourceCredential(mod.FullPath, credential))
            {
                return false;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                mod.DisplayName = FirstNonEmpty(localization.Name?.ZhCn, mod.DisplayName);
                mod.Description = FirstNonEmpty(localization.Description?.ZhCn, mod.Description);
                mod.LocalizationUpdatedAt = credential.Localization?.UpdatedAt ?? mod.LocalizationUpdatedAt;

                var normalized = NormalizePlatform(credential.Platform);
                if (string.Equals(normalized, "Curseforge", StringComparison.OrdinalIgnoreCase))
                {
                    mod.CurseforgeProjectId = FirstNonEmpty(NormalizeProjectId(credential.ProjectId), mod.CurseforgeProjectId);
                    mod.UpdateSource = "Curseforge";
                }
                else if (string.Equals(normalized, "NexusMods", StringComparison.OrdinalIgnoreCase))
                {
                    mod.NexusModsProjectId = FirstNonEmpty(NormalizeProjectId(credential.ProjectId), mod.NexusModsProjectId);
                    mod.UpdateSource = "NexusMods";
                }
            });

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ModDependencyDisplayItem CreateDependencyLinkForModList(
        string uniqueId,
        string minimumVersion,
        IReadOnlyDictionary<string, ModManageItem> installedByUniqueId,
        string note,
        bool isRequired = true)
    {
        installedByUniqueId.TryGetValue(uniqueId, out var installedMod);

        return new ModDependencyDisplayItem
        {
            UniqueId = uniqueId,
            DisplayName = installedMod?.DisplayName ?? SimplifyUniqueIdForDisplay(uniqueId),
            MinimumVersion = minimumVersion ?? string.Empty,
            IsRequired = isRequired,
            IsInstalled = installedMod != null,
            IsInstalledAndEnabled = installedMod?.IsEnabled == true,
            IsInstalledButDisabled = installedMod != null && !installedMod.IsEnabled,
            InstalledModId = installedMod?.UniqueId ?? string.Empty,
            InstalledModName = installedMod?.DisplayName ?? string.Empty,
            Note = note
        };
    }

    private static string SimplifyUniqueIdForDisplay(string uniqueId)
    {
        if (string.IsNullOrWhiteSpace(uniqueId))
        {
            return string.Empty;
        }

        var parts = uniqueId.Split('.');
        return parts.Length == 0 ? uniqueId : parts[^1];
    }

    private void LoadAndApplyTags(string modsPath)
    {
        _currentModsPathForTagConfig = modsPath;
        _modTagConfig = ModTagConfigService.Load(modsPath);

        var customTagNameById = _modTagConfig.CustomTags
            .Where(tag => !string.IsNullOrWhiteSpace(tag.Id) && !string.IsNullOrWhiteSpace(tag.Name))
            .ToDictionary(tag => tag.Id, tag => tag.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var mod in Mods)
        {
            ApplyCustomTagsToMod(mod, customTagNameById);
        }

        RefreshTagFilters();
    }

    private void LoadBackups(string modsPath)
    {
        foreach (var item in BackupMods)
        {
            DetachModItem(item);
        }

        BackupMods.Clear();

        var backupRoot = GetBackupRootPath(modsPath);
        if (!Directory.Exists(backupRoot))
        {
            return;
        }

        var snapshotDirs = Directory.GetDirectories(backupRoot)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var snapshotDir in snapshotDirs)
        {
            var folderName = Path.GetFileName(snapshotDir) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(folderName))
            {
                continue;
            }

            var record = TryReadBackupRecord(snapshotDir);
            var originalFolderName = string.IsNullOrWhiteSpace(record?.OriginalFolderName)
                ? folderName
                : record!.OriginalFolderName;

            var isEnabled = !originalFolderName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);

            var item = new ModManageItem
            {
                DisplayName = string.IsNullOrWhiteSpace(record?.DisplayName) ? originalFolderName : record!.DisplayName,
                Version = string.IsNullOrWhiteSpace(record?.Version) ? "未知版本" : record!.Version,
                Author = record?.Author ?? string.Empty,
                Description = record?.Description ?? string.Empty,
                DirectoryName = folderName,
                FolderName = originalFolderName,
                FullPath = snapshotDir,
                UniqueId = record?.UniqueId ?? string.Empty,
                IsEnabled = isEnabled,
                IsBackupItem = true,
                BackupOriginalFolderName = originalFolderName,
                BackupTime = record?.CreatedAt,
                UpdateStatus = record?.CreatedAt is DateTime dt
                    ? $"备份于 {dt:yyyy-MM-dd HH:mm}"
                    : "备份记录"
            };

            AttachModItem(item);
            BackupMods.Add(item);
        }
    }

    private static ModBackupRecord? TryReadBackupRecord(string backupDir)
    {
        try
        {
            var path = Path.Combine(backupDir, BackupMetaFileName);
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ModBackupRecord>(json);
        }
        catch
        {
            return null;
        }
    }

    private static string GetBackupRootPath(string modsPath)
    {
        var instanceRoot = Directory.GetParent(modsPath)?.FullName ?? modsPath;
        return Path.Combine(instanceRoot, BackupRootFolderName);
    }

    private void RefreshTagFilters()
    {
        var selectedChipKeys = TagPanelItems
            .Where(item => item.IsSelected)
            .Select(item => item.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var previousKey = SelectedTagFilter?.Key;

        var folderTagSourceFlags = Mods
            .SelectMany(mod => mod.FolderTags
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => new
                {
                    Name = tag,
                    IsPrefix = string.Equals(tag, TryExtractPrefixCategory(mod.FolderName), StringComparison.OrdinalIgnoreCase)
                }))
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Any(item => item.IsPrefix), StringComparer.OrdinalIgnoreCase);

        var folderTags = folderTagSourceFlags.Keys
            .OrderBy(tag =>
            {
                var index = _modTagConfig.FolderTagOrder.FindIndex(name => string.Equals(name, tag, StringComparison.OrdinalIgnoreCase));
                return index >= 0 ? index : int.MaxValue;
            })
            .ThenBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .Select(tag => new ModTagFilterOption
            {
                Key = $"folder:{tag}",
                Name = tag,
                IsFolderTag = true,
                IsPrefixFolderTag = folderTagSourceFlags.TryGetValue(tag, out var isPrefix) && isPrefix
            })
            .Where(tag =>
                (tag.IsPrefixFolderTag && ShowPrefixTags) ||
                (!tag.IsPrefixFolderTag && ShowFolderTags))
            .ToList();

        var customTagsById = _modTagConfig.CustomTags
            .Where(tag => !string.IsNullOrWhiteSpace(tag.Id) && !string.IsNullOrWhiteSpace(tag.Name))
            .ToDictionary(tag => tag.Id, tag => tag, StringComparer.OrdinalIgnoreCase);

        var customTags = _modTagConfig.CustomTagOrder
            .Where(id => customTagsById.ContainsKey(id))
            .Select(id => customTagsById[id])
            .Concat(_modTagConfig.CustomTags.Where(tag =>
                !string.IsNullOrWhiteSpace(tag.Id) &&
                !string.IsNullOrWhiteSpace(tag.Name) &&
                !_modTagConfig.CustomTagOrder.Any(id => string.Equals(id, tag.Id, StringComparison.OrdinalIgnoreCase))))
            .Where(tag => !string.IsNullOrWhiteSpace(tag.Id) && !string.IsNullOrWhiteSpace(tag.Name))
            .Select(tag => new ModTagFilterOption
            {
                Key = $"custom:{tag.Id}",
                TagId = tag.Id,
                Name = tag.Name,
                IsFolderTag = false
            })
            .Where(_ => ShowCustomTags)
            .ToList();

        var tagSearch = (TagSearchKeyword ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(tagSearch))
        {
            folderTags = folderTags
                .Where(tag => tag.Name.Contains(tagSearch, StringComparison.OrdinalIgnoreCase))
                .ToList();

            customTags = customTags
                .Where(tag => tag.Name.Contains(tagSearch, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        TagFilters.Clear();
        TagFilters.Add(new ModTagFilterOption
        {
            Key = "all",
            Name = "全部标签",
            IsAllOption = true
        });

        foreach (var tag in folderTags)
        {
            TagFilters.Add(tag);
        }

        foreach (var tag in customTags)
        {
            TagFilters.Add(tag);
        }

        CustomTagDefinitions.Clear();
        foreach (var tag in customTags)
        {
            CustomTagDefinitions.Add(tag);
        }

        foreach (var item in TagPanelItems)
        {
            DetachTagPanelItem(item);
        }

        TagPanelItems.Clear();
        foreach (var tag in folderTags)
        {
            var item = new ModTagPanelItem(tag)
            {
                IsSelected = selectedChipKeys.Contains(tag.Key)
            };
            AttachTagPanelItem(item);
            TagPanelItems.Add(item);
        }

        foreach (var tag in customTags)
        {
            var item = new ModTagPanelItem(tag)
            {
                IsSelected = selectedChipKeys.Contains(tag.Key)
            };
            AttachTagPanelItem(item);
            TagPanelItems.Add(item);
        }

        SelectedTagFilter = TagFilters.FirstOrDefault(tag => string.Equals(tag.Key, previousKey, StringComparison.OrdinalIgnoreCase))
            ?? TagFilters.FirstOrDefault();

        OnPropertyChanged(nameof(CanApplyTagSelectionToSearch));
        OnPropertyChanged(nameof(HasSelectedTagPanelItems));
        OnPropertyChanged(nameof(CanBatchAddSelectedTags));
        OnPropertyChanged(nameof(CanBatchRemoveSelectedTags));
        OnPropertyChanged(nameof(ShowTagBatchAction));
        OnPropertyChanged(nameof(TagBatchActionText));
    }

    private void AttachTagPanelItem(ModTagPanelItem item)
    {
        item.PropertyChanged += HandleTagPanelItemPropertyChanged;
    }

    private void DetachTagPanelItem(ModTagPanelItem item)
    {
        item.PropertyChanged -= HandleTagPanelItemPropertyChanged;
    }

    private void HandleTagPanelItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(ModTagPanelItem.IsSelected), StringComparison.Ordinal))
        {
            return;
        }

        OnPropertyChanged(nameof(HasSelectedTagPanelItems));
        OnPropertyChanged(nameof(CanBatchAddSelectedTags));
        OnPropertyChanged(nameof(CanBatchRemoveSelectedTags));
        OnPropertyChanged(nameof(CanApplyTagSelectionToSearch));
        OnPropertyChanged(nameof(ShowTagBatchAction));
        OnPropertyChanged(nameof(TagBatchActionText));
    }

    private void ApplyCustomTagsToMod(ModManageItem mod, IReadOnlyDictionary<string, string> customTagNameById)
    {
        mod.CustomTags.Clear();
        var modKey = GetModTagKey(mod);
        if (string.IsNullOrWhiteSpace(modKey))
        {
            mod.NotifyTagChanged();
            return;
        }

        if (!_modTagConfig.Assignments.TryGetValue(modKey, out var assignedTagIds) || assignedTagIds == null)
        {
            mod.NotifyTagChanged();
            return;
        }

        foreach (var tagId in assignedTagIds)
        {
            if (!string.IsNullOrWhiteSpace(tagId) &&
                customTagNameById.TryGetValue(tagId, out var tagName) &&
                !string.IsNullOrWhiteSpace(tagName))
            {
                mod.CustomTags.Add(tagName);
            }
        }

        mod.NotifyTagChanged();
    }

    private void ApplyTagFilter()
    {
        IEnumerable<ModManageItem> source = IsBackupTab ? BackupMods : Mods;

        var keyword = (SearchKeyword ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            source = source.Where(mod => MatchesSearch(mod, keyword));
        }

        if (IsModsTab)
        {
            source = CurrentModSubFilter switch
            {
                ModManageSubFilter.Enabled => source.Where(mod => mod.IsEnabled),
                ModManageSubFilter.Disabled => source.Where(mod => !mod.IsEnabled),
                ModManageSubFilter.Updatable => source.Where(mod => mod.HasUpdate),
                _ => source
            };
        }

        _filteredSource.Clear();
        _filteredSource.AddRange(source);

        TotalFilteredCount = _filteredSource.Count;
        TotalPages = Math.Max(1, (int)Math.Ceiling(TotalFilteredCount / (double)ModsPageSize));

        if (CurrentPageIndex > TotalPages)
        {
            CurrentPageIndex = TotalPages;
        }
        else if (CurrentPageIndex < 1)
        {
            CurrentPageIndex = 1;
        }
        else
        {
            RefreshPagedMods();
            UpdatePageNumbers();
            OnPropertyChanged(nameof(HasPreviousPage));
            OnPropertyChanged(nameof(HasNextPage));
            OnPropertyChanged(nameof(PageInfo));
        }

        if (SelectedMod != null && !_filteredSource.Contains(SelectedMod))
        {
            SelectedMod = null;
        }

        UpdateSelectionState();
        RefreshModManageHint();
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

        OnPropertyChanged(nameof(IsCurrentPageAllSelected));
    }

    private void UpdatePageNumbers()
    {
        var total = Math.Max(1, TotalPages);
        var current = Math.Clamp(CurrentPageIndex, 1, total);

        if (total <= 7)
        {
            PageNumbers = Enumerable.Range(1, total).Select(static page => page.ToString()).ToList();
            return;
        }

        var tokens = new List<string>
        {
            "1"
        };

        var start = Math.Max(2, current - 1);
        var end = Math.Min(total - 1, current + 1);

        if (start > 2)
        {
            tokens.Add("...");
        }

        for (var page = start; page <= end; page++)
        {
            tokens.Add(page.ToString());
        }

        if (end < total - 1)
        {
            tokens.Add("...");
        }

        tokens.Add(total.ToString());
        PageNumbers = tokens;
    }

    private static bool MatchesSearch(ModManageItem mod, string query)
    {
        var parsed = ParseAdvancedSearchQuery(query);
        return MatchesSearchSingle(mod, parsed);
    }

    private static bool MatchesSearchSingle(
        ModManageItem mod,
        (Dictionary<string, List<string>> Clauses, List<string> Keywords, List<string> ExcludedKeywords) parsed)
    {
        foreach (var clause in parsed.Clauses)
        {
            foreach (var value in clause.Value)
            {
                var matched = clause.Key switch
                {
                    "tag" => mod.AllTags.Any(tag => tag.Contains(value, StringComparison.OrdinalIgnoreCase)),
                    "author" => mod.Author.Contains(value, StringComparison.OrdinalIgnoreCase),
                    "description" => mod.Description.Contains(value, StringComparison.OrdinalIgnoreCase),
                    "name" => mod.DisplayName.Contains(value, StringComparison.OrdinalIgnoreCase),
                    "uid" => mod.UniqueId.Contains(value, StringComparison.OrdinalIgnoreCase),
                    "folder" => mod.FolderName.Contains(value, StringComparison.OrdinalIgnoreCase) ||
                                mod.DirectoryName.Contains(value, StringComparison.OrdinalIgnoreCase),
                    "enabled" => mod.IsEnabled,
                    "disabled" => !mod.IsEnabled,
                    _ => MatchesKeyword(mod, value)
                };

                if (!matched)
                {
                    return false;
                }
            }
        }

        if (!parsed.Keywords.All(keyword => MatchesKeyword(mod, keyword)))
        {
            return false;
        }

        if (parsed.ExcludedKeywords.Any(keyword => MatchesKeyword(mod, keyword)))
        {
            return false;
        }

        return true;
    }

    private static bool MatchesKeyword(ModManageItem mod, string keyword)
    {
        return mod.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || mod.DirectoryName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || mod.Version.Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || mod.Author.Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || mod.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || mod.UniqueId.Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || mod.AllTags.Any(tag => tag.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeDisplayTagForSearch(string? displayTag)
    {
        if (string.IsNullOrWhiteSpace(displayTag))
        {
            return string.Empty;
        }

        var tag = displayTag.Trim();
        if (tag.Length >= 3 &&
            (tag.StartsWith("[前缀] ", StringComparison.Ordinal) ||
             tag.StartsWith("[目录] ", StringComparison.Ordinal) ||
             tag.StartsWith("[标签] ", StringComparison.Ordinal)))
        {
            return tag[3..].Trim();
        }

        return tag;
    }

    private static (Dictionary<string, List<string>> Clauses, List<string> Keywords, List<string> ExcludedKeywords) ParseAdvancedSearchQuery(string query)
    {
        var clauses = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var keywords = new List<string>();
        var excludedKeywords = new List<string>();

        var raw = (query ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return (clauses, keywords, excludedKeywords);
        }

        var regex = new Regex(@"(?<key>tag|author|description|name|uid|folder|enabled|disabled):(?<value>""[^""]+""|\S+)", RegexOptions.IgnoreCase);
        var consumed = new List<(int Start, int Length)>();

        foreach (Match match in regex.Matches(raw))
        {
            var key = match.Groups["key"].Value.Trim().ToLowerInvariant();
            var value = match.Groups["value"].Value.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (!clauses.TryGetValue(key, out var list))
            {
                list = [];
                clauses[key] = list;
            }

            list.Add(value);
            consumed.Add((match.Index, match.Length));
        }

        var remainder = raw;
        foreach (var piece in consumed.OrderByDescending(p => p.Start))
        {
            remainder = remainder.Remove(piece.Start, piece.Length);
        }

        foreach (var token in remainder.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = token.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (normalized.StartsWith("-", StringComparison.Ordinal) && normalized.Length > 1)
            {
                excludedKeywords.Add(normalized.Substring(1));
                continue;
            }

            keywords.Add(normalized);
        }

        return (clauses, keywords, excludedKeywords);
    }

    private static IEnumerable<string> EnumerateCandidateModDirectories(string modsPath)
    {
        var directDirectories = Directory.GetDirectories(modsPath)
            .Where(path => !string.Equals(Path.GetFileName(path), BackupRootFolderName, StringComparison.OrdinalIgnoreCase));
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in directDirectories)
        {
            var manifestPath = Path.Combine(directory, "manifest.json");
            if (File.Exists(manifestPath))
            {
                results.Add(directory);
                continue;
            }

            foreach (var child in Directory.GetDirectories(directory))
            {
                var childManifestPath = Path.Combine(child, "manifest.json");
                if (File.Exists(childManifestPath))
                {
                    results.Add(child);
                }
            }
        }

        return results;
    }

    private static bool MatchesTagFilterSingle(ModManageItem mod, ModTagFilterOption option)
    {
        if (option.IsFolderTag)
        {
            return mod.FolderTags.Any(tag => string.Equals(tag, option.Name, StringComparison.OrdinalIgnoreCase));
        }

        return mod.CustomTags.Any(tag => string.Equals(tag, option.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> BuildFolderTagsForMod(string modsPath, string modDirectory, string folderName)
    {
        var relative = Path.GetRelativePath(modsPath, modDirectory);
        var relativeParts = relative
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

        var tags = relativeParts.Length > 1
            ? relativeParts.Take(relativeParts.Length - 1).Select(NormalizeModFolderName).ToList()
            : new List<string>();

        var prefixCategory = TryExtractPrefixCategory(folderName);
        if (!string.IsNullOrWhiteSpace(prefixCategory) &&
            !tags.Any(tag => string.Equals(tag, prefixCategory, StringComparison.OrdinalIgnoreCase)))
        {
            tags.Add(prefixCategory);
        }

        return tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeModFolderName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return NormalizeFolderName(trimmed);
    }

    private static bool IsDisabledFolderName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var folderName = GetLeafFolderName(value);
        return folderName.StartsWith(".", StringComparison.Ordinal)
               || folderName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeFolderName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var folderName = GetLeafFolderName(value);
        if (folderName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
        {
            folderName = folderName[..^".disabled".Length];
        }

        return folderName.Trim('.');
    }

    private static string GetDisabledFolderPath(string folderPath, bool useTrailingDotFallback = false)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return folderPath;
        }

        var directory = Path.GetDirectoryName(folderPath);
        var baseName = NormalizeFolderName(Path.GetFileName(folderPath));
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "_";
        }

        var disabledName = useTrailingDotFallback ? $".{baseName}." : $".{baseName}";
        return string.IsNullOrWhiteSpace(directory)
            ? disabledName
            : Path.Combine(directory, disabledName);
    }

    private static string GetEnabledFolderPath(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return folderPath;
        }

        var directory = Path.GetDirectoryName(folderPath);
        var enabledName = NormalizeFolderName(Path.GetFileName(folderPath));
        return string.IsNullOrWhiteSpace(directory)
            ? enabledName
            : Path.Combine(directory, enabledName);
    }

    private static string GetLeafFolderName(string value)
    {
        var trimmed = value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFileName(trimmed) ?? string.Empty;
    }

    private static string TryExtractPrefixCategory(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return string.Empty;
        }

        var trimmed = folderName.Trim();
        var squareMatch = Regex.Match(trimmed, @"^\[(?<name>[^\]]{1,64})\]");
        if (squareMatch.Success)
        {
            return squareMatch.Groups["name"].Value.Trim();
        }

        var cnSquareMatch = Regex.Match(trimmed, @"^【(?<name>[^】]{1,64})】");
        if (cnSquareMatch.Success)
        {
            return cnSquareMatch.Groups["name"].Value.Trim();
        }

        return string.Empty;
    }

    private static string GetModTagKey(ModManageItem mod)
    {
        if (!string.IsNullOrWhiteSpace(mod.UniqueId))
        {
            return $"uid:{mod.UniqueId.Trim().ToLowerInvariant()}";
        }

        if (!string.IsNullOrWhiteSpace(mod.FolderName))
        {
            return $"folder:{mod.FolderName.Trim().ToLowerInvariant()}";
        }

        return string.IsNullOrWhiteSpace(mod.DirectoryName)
            ? string.Empty
            : $"id:{mod.DirectoryName.Trim().ToLowerInvariant()}";
    }

    [RelayCommand]
    private void AddCustomTag()
    {
        var name = (TagSearchKeyword ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            Status = "请先在筛选框中输入标签名，再点新增";
            return;
        }

        if (_modTagConfig.CustomTags.Any(tag => string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Status = "标签已存在";
            return;
        }

        var newTagId = Guid.NewGuid().ToString("N");
        _modTagConfig.CustomTags.Add(new ModCustomTagDefinition
        {
            Id = newTagId,
            Name = name
        });
        _modTagConfig.CustomTagOrder.Add(newTagId);

        if (!ModTagConfigService.Save(_currentModsPathForTagConfig, _modTagConfig))
        {
            Status = "保存标签失败";
            return;
        }

        TagSearchKeyword = string.Empty;
        LoadAndApplyTags(_currentModsPathForTagConfig);
        ApplyTagFilter();
        Status = "已创建自定义标签";
    }

    [RelayCommand]
    private void RenameCustomTag()
    {
        if (SelectedCustomTag == null || string.IsNullOrWhiteSpace(SelectedCustomTag.TagId))
        {
            Status = "请先选择一个自定义标签";
            return;
        }

        var newName = (RenameCustomTagName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            Status = "请输入新的标签名称";
            return;
        }

        if (_modTagConfig.CustomTags.Any(tag =>
                !string.Equals(tag.Id, SelectedCustomTag.TagId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(tag.Name, newName, StringComparison.OrdinalIgnoreCase)))
        {
            Status = "目标标签名称已存在";
            return;
        }

        var target = _modTagConfig.CustomTags.FirstOrDefault(tag => string.Equals(tag.Id, SelectedCustomTag.TagId, StringComparison.OrdinalIgnoreCase));
        if (target == null)
        {
            return;
        }

        target.Name = newName;
        if (!ModTagConfigService.Save(_currentModsPathForTagConfig, _modTagConfig))
        {
            Status = "保存标签失败";
            return;
        }

        LoadAndApplyTags(_currentModsPathForTagConfig);
        ApplyTagFilter();
        Status = "标签已重命名";
    }

    [RelayCommand]
    private async Task DeleteCustomTag()
    {
        if (SelectedCustomTag == null || string.IsNullOrWhiteSpace(SelectedCustomTag.TagId))
        {
            Status = "请先选择一个自定义标签";
            return;
        }

        var confirmed = await _dialogService.ShowConfirmAsync("删除标签", $"确定删除标签“{SelectedCustomTag.Name}”吗？");
        if (!confirmed)
        {
            return;
        }

        _modTagConfig.CustomTags.RemoveAll(tag => string.Equals(tag.Id, SelectedCustomTag.TagId, StringComparison.OrdinalIgnoreCase));
        _modTagConfig.CustomTagOrder.RemoveAll(id => string.Equals(id, SelectedCustomTag.TagId, StringComparison.OrdinalIgnoreCase));

        foreach (var key in _modTagConfig.Assignments.Keys.ToList())
        {
            var list = _modTagConfig.Assignments[key];
            list.RemoveAll(id => string.Equals(id, SelectedCustomTag.TagId, StringComparison.OrdinalIgnoreCase));
            if (list.Count == 0)
            {
                _modTagConfig.Assignments.Remove(key);
            }
        }

        if (!ModTagConfigService.Save(_currentModsPathForTagConfig, _modTagConfig))
        {
            Status = "保存标签失败";
            return;
        }

        LoadAndApplyTags(_currentModsPathForTagConfig);
        ApplyTagFilter();
        Status = "标签已删除";
    }

    [RelayCommand]
    private async Task RenameCustomTagFromChip(ModTagPanelItem? item)
    {
        if (item == null || !item.IsCustomTag || string.IsNullOrWhiteSpace(item.TagId))
        {
            return;
        }

        var target = CustomTagDefinitions.FirstOrDefault(tag => string.Equals(tag.TagId, item.TagId, StringComparison.OrdinalIgnoreCase));
        if (target == null)
        {
            return;
        }

        var input = await _dialogService.ShowInputAsync("重命名标签", "请输入新的标签名称", target.Name);
        input = input?.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        SelectedCustomTag = target;
        RenameCustomTagName = input;
        RenameCustomTag();
    }

    [RelayCommand]
    private async Task DeleteCustomTagFromChip(ModTagPanelItem? item)
    {
        if (item == null || !item.IsCustomTag || string.IsNullOrWhiteSpace(item.TagId))
        {
            return;
        }

        var target = CustomTagDefinitions.FirstOrDefault(tag => string.Equals(tag.TagId, item.TagId, StringComparison.OrdinalIgnoreCase));
        if (target == null)
        {
            return;
        }

        SelectedCustomTag = target;
        await DeleteCustomTag();
    }

    [RelayCommand]
    private void ApplySelectedTagsToSearch()
    {
        var selectedNames = TagPanelItems
            .Where(item => item.IsSelected)
            .Select(item => item.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (selectedNames.Count == 0 && SelectedTagFilter is { IsAllOption: false })
        {
            selectedNames.Add(SelectedTagFilter.Name);
        }

        if (selectedNames.Count == 0)
        {
            Status = "请先选择至少一个 Tag，再点击搜索";
            return;
        }

        SearchKeyword = string.Join(" ", selectedNames.Select(name => $"tag:\"{name}\""));
        Status = $"已写入 {selectedNames.Count} 个 Tag 搜索条件";
    }

    [RelayCommand]
    private void ClickTagSearch(string? tagDisplayText)
    {
        var normalized = NormalizeDisplayTagForSearch(tagDisplayText);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        SearchKeyword = $"tag:\"{normalized}\"";
    }

    [RelayCommand]
    private void ClickDependencySearch(ModDependencyDisplayItem? dependency)
    {
        if (dependency == null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(dependency.UniqueId))
        {
            SearchKeyword = $"uid:\"{dependency.UniqueId}\"";
        }
        else if (!string.IsNullOrWhiteSpace(dependency.DisplayName))
        {
            SearchKeyword = $"name:\"{dependency.DisplayName}\"";
        }

        Status = string.IsNullOrWhiteSpace(dependency.DisplayName)
            ? "已根据前置条件写入搜索"
            : $"已定位前置：{dependency.DisplayName}";
    }

    [RelayCommand]
    private void BatchBindSelectedTagsToSelectedMod()
    {
        BatchApplySelectedTagChips(bind: true);
    }

    [RelayCommand]
    private void BatchUnbindSelectedTagsFromSelectedMod()
    {
        BatchApplySelectedTagChips(bind: false);
    }

    [RelayCommand]
    private void ApplyTagBatchAction()
    {
        var bind = !ShouldRemoveSelectedTagsFromSelectedMods();
        BatchApplySelectedTagChips(bind);
    }

    [RelayCommand]
    private void ClearSelectedTags()
    {
        foreach (var item in TagPanelItems)
        {
            item.IsSelected = false;
        }

        InlineTagHint = "已清除选中 Tags";
        OnPropertyChanged(nameof(HasSelectedTagPanelItems));
        OnPropertyChanged(nameof(CanApplyTagSelectionToSearch));
        OnPropertyChanged(nameof(ShowTagBatchAction));
        OnPropertyChanged(nameof(TagBatchActionText));
    }

    private void BatchApplySelectedTagChips(bool bind)
    {
        if (IsBackupTab)
        {
            Status = "备份模式下不支持标签编辑";
            return;
        }

        var selectedMods = GetEffectiveSelectedMods();
        if (selectedMods.Count == 0)
        {
            Status = "请先勾选至少一个 Mod";
            return;
        }

        var selectedTagIds = ResolveSelectedCustomTagIdsForBatch(bind);
        if (selectedTagIds.Count == 0)
        {
            Status = bind
                ? "请先选中标签，或在输入框填写标签名后点击添加标签"
                : "请先在标签面板中选中至少一个自定义标签";
            return;
        }

        var changed = false;
        var changedMods = 0;
        foreach (var mod in selectedMods)
        {
            var key = GetModTagKey(mod);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (!_modTagConfig.Assignments.TryGetValue(key, out var assigned))
            {
                assigned = [];
                _modTagConfig.Assignments[key] = assigned;
            }

            var modChanged = false;
            foreach (var tagId in selectedTagIds)
            {
                if (bind)
                {
                    if (!assigned.Any(id => string.Equals(id, tagId, StringComparison.OrdinalIgnoreCase)))
                    {
                        assigned.Add(tagId);
                        modChanged = true;
                    }
                }
                else
                {
                    var removed = assigned.RemoveAll(id => string.Equals(id, tagId, StringComparison.OrdinalIgnoreCase));
                    modChanged = modChanged || removed > 0;
                }
            }

            if (assigned.Count == 0)
            {
                _modTagConfig.Assignments.Remove(key);
            }

            if (modChanged)
            {
                changed = true;
                changedMods++;
            }
        }

        if (!changed)
        {
            Status = bind ? "当前 Mod 已包含选中标签" : "当前 Mod 不包含选中标签";
            return;
        }

        if (!ModTagConfigService.Save(_currentModsPathForTagConfig, _modTagConfig))
        {
            Status = "保存标签失败";
            return;
        }

        LoadAndApplyTags(_currentModsPathForTagConfig);
        ApplyTagFilter();
        InlineTagHint = bind
            ? $"已将选中标签绑定到 {changedMods} 个 Mod"
            : $"已从 {changedMods} 个 Mod 解绑选中标签";

        OnPropertyChanged(nameof(ShowTagBatchAction));
        OnPropertyChanged(nameof(TagBatchActionText));
    }

    private List<string> ResolveSelectedCustomTagIdsForBatch(bool bind)
    {
        var selectedTagIds = TagPanelItems
            .Where(item => item.IsSelected && item.IsCustomTag && !string.IsNullOrWhiteSpace(item.TagId))
            .Select(item => item.TagId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!bind || selectedTagIds.Count > 0)
        {
            return selectedTagIds;
        }

        var inputName = (TagSearchKeyword ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(inputName))
        {
            return selectedTagIds;
        }

        var existingTag = _modTagConfig.CustomTags.FirstOrDefault(tag =>
            !string.IsNullOrWhiteSpace(tag.Id) &&
            string.Equals(tag.Name, inputName, StringComparison.OrdinalIgnoreCase));

        if (existingTag == null)
        {
            var newTagId = Guid.NewGuid().ToString("N");
            existingTag = new ModCustomTagDefinition
            {
                Id = newTagId,
                Name = inputName
            };
            _modTagConfig.CustomTags.Add(existingTag);
            _modTagConfig.CustomTagOrder.Add(newTagId);
            TagSearchKeyword = string.Empty;
        }

        selectedTagIds.Add(existingTag.Id);
        return selectedTagIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool ShouldRemoveSelectedTagsFromSelectedMods()
    {
        var selectedMods = GetEffectiveSelectedMods().Where(mod => !mod.IsBackupItem).ToList();
        if (selectedMods.Count == 0)
        {
            return false;
        }

        var selectedTagIds = ResolveSelectedCustomTagIdsForBatch(bind: false);
        if (selectedTagIds.Count == 0)
        {
            return false;
        }

        foreach (var mod in selectedMods)
        {
            var modKey = GetModTagKey(mod);
            if (string.IsNullOrWhiteSpace(modKey) ||
                !_modTagConfig.Assignments.TryGetValue(modKey, out var assigned) ||
                assigned == null)
            {
                return false;
            }

            foreach (var tagId in selectedTagIds)
            {
                if (!assigned.Any(id => string.Equals(id, tagId, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    [RelayCommand]
    private void ToggleCurrentPageSelection()
    {
        if (FilteredMods.Count == 0)
        {
            return;
        }

        var allSelected = FilteredMods.All(item => item.IsSelected);
        foreach (var mod in FilteredMods)
        {
            mod.IsSelected = !allSelected;
        }

        UpdateSelectionState();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        var source = IsBackupTab ? BackupMods : Mods;
        foreach (var mod in source)
        {
            mod.IsSelected = false;
        }

        UpdateSelectionState();
    }

    [RelayCommand]
    private void EnableSelectedMods()
    {
        if (IsBackupTab)
        {
            return;
        }

        var targets = GetEffectiveSelectedMods()
            .Where(mod => !mod.IsBackupItem && !mod.IsEnabled)
            .ToList();
        if (targets.Count == 0)
        {
            Status = "没有可启用的已选 Mod";
            return;
        }

        var changed = 0;
        foreach (var target in targets)
        {
            SelectedMod = target;
            EnableSelectedMod();
            if (target.IsEnabled)
            {
                changed++;
            }
        }

        Status = $"已启用 {changed}/{targets.Count} 个 Mod";
    }

    [RelayCommand]
    private void DisableSelectedMods()
    {
        if (IsBackupTab)
        {
            return;
        }

        var targets = GetEffectiveSelectedMods()
            .Where(mod => !mod.IsBackupItem && mod.IsEnabled)
            .ToList();
        if (targets.Count == 0)
        {
            Status = "没有可禁用的已选 Mod";
            return;
        }

        var changed = 0;
        foreach (var target in targets)
        {
            SelectedMod = target;
            DisableSelectedMod();
            if (!target.IsEnabled)
            {
                changed++;
            }
        }

        Status = $"已禁用 {changed}/{targets.Count} 个 Mod";
    }

    [RelayCommand]
    private async Task DeleteSelectedMods()
    {
        if (IsBackupTab)
        {
            return;
        }

        var targets = GetEffectiveSelectedMods()
            .Where(mod => !mod.IsBackupItem)
            .ToList();
        if (targets.Count == 0)
        {
            Status = "请先选择至少一个 Mod";
            return;
        }

        var confirmed = await _dialogService.ShowConfirmAsync("删除 Mod", $"确定删除选中的 {targets.Count} 个 Mod 吗？");
        if (!confirmed)
        {
            return;
        }

        var deleted = 0;
        foreach (var target in targets)
        {
            if (string.IsNullOrWhiteSpace(target.FullPath) || !Directory.Exists(target.FullPath))
            {
                continue;
            }

            try
            {
                Directory.Delete(target.FullPath, true);
                DetachModItem(target);
                Mods.Remove(target);
                deleted++;
            }
            catch
            {
                // Continue deleting remaining items.
            }
        }

        ApplyTagFilter();
        Status = $"已删除 {deleted}/{targets.Count} 个 Mod";
    }

    [RelayCommand]
    private void PreviousPage()
    {
        if (HasPreviousPage)
        {
            CurrentPageIndex--;
        }
    }

    [RelayCommand]
    private void NextPage()
    {
        if (HasNextPage)
        {
            CurrentPageIndex++;
        }
    }

    [RelayCommand]
    private void GoToPage(string? pageToken)
    {
        if (!int.TryParse(pageToken, out var pageNumber))
        {
            return;
        }

        if (pageNumber >= 1 && pageNumber <= TotalPages && pageNumber != CurrentPageIndex)
        {
            CurrentPageIndex = pageNumber;
        }
    }

    [RelayCommand]
    private void ClearSearchKeyword()
    {
        SearchKeyword = string.Empty;
    }

    [RelayCommand]
    private async Task ShowAdvancedSearchHelp()
    {
        var helpText =
            "支持语法关键字\n" +
            "• tag: 按标签筛选\n" +
            "• author: 按作者筛选\n" +
            "• description: 按描述筛选\n" +
            "• name: 按名称筛选\n" +
            "• uid: 按 UniqueID 筛选\n" +
            "• folder: 按目录名筛选\n" +
            "• enabled: 仅启用项\n" +
            "• disabled: 仅禁用项\n\n" +
            "书写示例\n" +
            "• tag:\"UI\" author:Pathos\n" +
            "• enabled:yes tag:地图\n" +
            "• name:\"CJB Cheats Menu\" -test\n\n" +
            "提示\n" +
            "• 普通关键词可直接输入，多个词默认同时匹配\n" +
            "• 使用 -关键词 可排除内容\n" +
            "• 含空格的值建议用英文双引号包裹";

        await _dialogService.ShowWindowTitleHelpDialogAsync(helpText, "高级语法速查");
    }

    [RelayCommand]
    private async Task AddSelectedModsToNewTag()
    {
        if (IsBackupTab)
        {
            Status = "备份模式下不支持标签编辑";
            return;
        }

        var selectedMods = GetEffectiveSelectedMods()
            .Where(mod => !mod.IsBackupItem)
            .ToList();
        if (selectedMods.Count == 0)
        {
            Status = "请先选择至少一个 Mod";
            return;
        }

        var inputName = await _dialogService.ShowInputAsync("添加到新标签", "请输入新标签名称");
        inputName = inputName?.Trim();
        if (string.IsNullOrWhiteSpace(inputName))
        {
            return;
        }

        var targetTag = _modTagConfig.CustomTags.FirstOrDefault(tag =>
            !string.IsNullOrWhiteSpace(tag.Id) &&
            string.Equals(tag.Name, inputName, StringComparison.OrdinalIgnoreCase));

        if (targetTag == null)
        {
            var newTagId = Guid.NewGuid().ToString("N");
            targetTag = new ModCustomTagDefinition
            {
                Id = newTagId,
                Name = inputName
            };
            _modTagConfig.CustomTags.Add(targetTag);
            _modTagConfig.CustomTagOrder.Add(newTagId);
        }

        var changed = 0;
        foreach (var mod in selectedMods)
        {
            var modKey = GetModTagKey(mod);
            if (string.IsNullOrWhiteSpace(modKey))
            {
                continue;
            }

            if (!_modTagConfig.Assignments.TryGetValue(modKey, out var assignedTagIds))
            {
                assignedTagIds = [];
                _modTagConfig.Assignments[modKey] = assignedTagIds;
            }

            if (assignedTagIds.Any(tagId => string.Equals(tagId, targetTag.Id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            assignedTagIds.Add(targetTag.Id);
            changed++;
        }

        if (changed == 0)
        {
            Status = "已选 Mod 已包含该标签";
            return;
        }

        if (!ModTagConfigService.Save(_currentModsPathForTagConfig, _modTagConfig))
        {
            Status = "保存标签失败";
            return;
        }

        LoadAndApplyTags(_currentModsPathForTagConfig);
        ApplyTagFilter();
        InlineTagHint = $"已将 {selectedMods.Count} 个 Mod 添加到标签“{targetTag.Name}”";
    }

    private List<ModManageItem> GetEffectiveSelectedMods()
    {
        var source = IsBackupTab ? BackupMods : Mods;
        var selected = source.Where(mod => mod.IsSelected).ToList();
        if (selected.Count > 0)
        {
            return selected;
        }

        if (SelectedMod != null && source.Contains(SelectedMod))
        {
            return [SelectedMod];
        }

        return [];
    }

    private void UpdateSelectionState()
    {
        var source = IsBackupTab ? BackupMods : Mods;
        SelectedCount = source.Count(mod => mod.IsSelected);
        ShowSelectionActions = SelectedCount > 0;
        OnPropertyChanged(nameof(IsCurrentPageAllSelected));
        OnPropertyChanged(nameof(CanBatchAddSelectedTags));
        OnPropertyChanged(nameof(CanBatchRemoveSelectedTags));
        OnPropertyChanged(nameof(ShowTagBatchAction));
        OnPropertyChanged(nameof(TagBatchActionText));
        OnPropertyChanged(nameof(CanToggleCurrentPageSelection));
        OnPropertyChanged(nameof(ShowSelectionActionsBar));
    }

    [RelayCommand]
    private void EnableSelectedMod()
    {
        if (!TryGetSelectedModForAction("启用", out var target))
        {
            return;
        }

        if (target.IsEnabled)
        {
            return;
        }

        var currentFolderName = Path.GetFileName(target.FullPath);
        if (!IsDisabledFolderName(currentFolderName))
        {
            Status = "启用失败：当前 Mod 目录后缀异常";
            RefreshModManageHint("启用失败：目录后缀异常，请刷新列表后重试");
            return;
        }

        var newPath = GetEnabledFolderPath(target.FullPath);
        if (Directory.Exists(newPath))
        {
            Status = "启用失败：已存在同名启用目录";
            RefreshModManageHint("启用失败：存在同名目录，请先处理冲突后重试");
            return;
        }

        try
        {
            Directory.Move(target.FullPath, newPath);
            UpdateModPathAfterRename(target, newPath);
            target.IsEnabled = true;
            target.UpdateStatus = "已启用（本次）";
            Status = $"已启用 Mod: {target.DisplayName}";
            RefreshModManageHint($"已启用 {target.DisplayName}，可直接启动游戏验证");
            OnPropertyChanged(nameof(CanEnableSelectedMod));
            OnPropertyChanged(nameof(CanDisableSelectedMod));
            OnPropertyChanged(nameof(ModsSummary));
            OnPropertyChanged(nameof(SelectedModDetails));
            ApplyTagFilter();
        }
        catch (Exception ex)
        {
            Status = $"启用失败: {ex.Message}";
            RefreshModManageHint($"启用失败：{target.DisplayName}");
        }
    }

    [RelayCommand]
    private void DisableSelectedMod()
    {
        if (!TryGetSelectedModForAction("禁用", out var target))
        {
            return;
        }

        if (!target.IsEnabled)
        {
            return;
        }

        var currentFolderName = Path.GetFileName(target.FullPath);
        if (IsDisabledFolderName(currentFolderName))
        {
            Status = "禁用失败：当前 Mod 已处于禁用目录";
            RefreshModManageHint("禁用失败：目录后缀异常，请刷新列表后重试");
            return;
        }

        var newPath = GetDisabledFolderPath(target.FullPath);
        if (Directory.Exists(newPath))
        {
            var fallbackPath = GetDisabledFolderPath(target.FullPath, useTrailingDotFallback: true);
            if (!Directory.Exists(fallbackPath))
            {
                newPath = fallbackPath;
            }
            else
            {
                Status = "禁用失败：已存在同名禁用目录";
                RefreshModManageHint("禁用失败：存在同名目录，请先处理冲突后重试");
                return;
            }
        }

        try
        {
            Directory.Move(target.FullPath, newPath);
            UpdateModPathAfterRename(target, newPath);
            target.IsEnabled = false;
            target.UpdateStatus = "已禁用（本次）";
            Status = $"已禁用 Mod: {target.DisplayName}";
            RefreshModManageHint($"已禁用 {target.DisplayName}，需要时可重新启用");
            OnPropertyChanged(nameof(CanEnableSelectedMod));
            OnPropertyChanged(nameof(CanDisableSelectedMod));
            OnPropertyChanged(nameof(ModsSummary));
            OnPropertyChanged(nameof(SelectedModDetails));
            ApplyTagFilter();
        }
        catch (Exception ex)
        {
            Status = $"禁用失败: {ex.Message}";
            RefreshModManageHint($"禁用失败：{target.DisplayName}");
        }
    }

    private void UpdateModPathAfterRename(ModManageItem target, string newPath)
    {
        target.FullPath = newPath;
        target.FolderName = NormalizeFolderName(Path.GetFileName(newPath));

        if (TryGetCurrentModsPath(out var modsPath) && !string.IsNullOrWhiteSpace(modsPath))
        {
            target.DirectoryName = Path.GetRelativePath(modsPath, newPath);
            return;
        }

        target.DirectoryName = Path.GetFileName(newPath);
    }

    [RelayCommand]
    private void UninstallSelectedMod()
    {
        if (!TryGetSelectedModForAction("卸载", out var target))
        {
            return;
        }

        var targetIndex = Mods.IndexOf(target);
        try
        {
            Directory.Delete(target.FullPath, true);
            DetachModItem(target);
            Mods.Remove(target);
            SelectedMod = Mods.Count == 0
                ? null
                : Mods[Math.Clamp(targetIndex, 0, Mods.Count - 1)];
            target.UpdateStatus = "已卸载";
            Status = $"已卸载 Mod: {target.DisplayName}";
            ApplyTagFilter();
            RefreshModManageHint($"已卸载 {target.DisplayName}，可在下载页重新安装");
            OnPropertyChanged(nameof(ModsSummary));
        }
        catch (Exception ex)
        {
            Status = $"卸载失败: {ex.Message}";
            RefreshModManageHint($"卸载失败：{target.DisplayName}");
        }
    }

    [RelayCommand]
    private async Task CheckUpdateSelectedMod()
    {
        if (!TryGetSelectedModForAction("检查更新", out var target))
        {
            return;
        }

        var checkResult = await CheckUpdateForModAsync(target);
        if (checkResult == null)
        {
            target.UpdateStatus = "缺少来源信息";
            Status = $"更新检测失败：{target.DisplayName}";
            return;
        }

        target.HasUpdate = checkResult.HasUpdate;
        target.CurseforgeProjectId = FirstNonEmpty(checkResult.CurseforgeProjectId, target.CurseforgeProjectId);
        target.NexusModsProjectId = FirstNonEmpty(checkResult.NexusModsProjectId, target.NexusModsProjectId);
        target.UpdateSource = FirstNonEmpty(checkResult.UpdateSource, target.UpdateSource);
        target.UpdateStatus = BuildUpdateStatusText(checkResult);
        target.LatestVersion = checkResult.LatestVersion;
        target.UpdateUrl = checkResult.UpdateUrl;
        Status = checkResult.IsTokenExpired
            ? "Nexus 登录已过期，请先重新登录"
            : $"已完成更新检查：{target.DisplayName}";
        RefreshModManageHint($"已完成更新检查：{target.DisplayName}");
        OnPropertyChanged(nameof(SelectedModDetails));
    }

    [RelayCommand]
    private void SaveInstanceSettings()
    {
        var settings = _settingsStore.Load();
        settings.InstanceName = string.IsNullOrWhiteSpace(InstanceName) ? "Default Instance" : InstanceName.Trim();
        settings.InstanceDescription = InstanceDescription?.Trim() ?? string.Empty;
        settings.GameWindowTitle = string.IsNullOrWhiteSpace(GameWindowTitle) ? "<default>" : GameWindowTitle.Trim();
        settings.InstanceCustomLaunchArguments = InstanceCustomLaunchArguments?.Trim() ?? string.Empty;
        settings.IsFavoriteInstance = IsFavoriteInstance;
        settings.OverrideSteamLaunchOptions = OverrideSteamLaunchOptions;
        settings.SteamLaunchOptions = SteamLaunchOptions?.Trim() ?? string.Empty;
        _settingsStore.Save(settings);

        RefreshInstanceRuntimeInfo();
        Status = $"实例设置已保存（{DateTime.Now:HH:mm:ss}）";
        NotifyInstanceContextChanged();
    }

    [RelayCommand]
    private void ResetStartupOptions()
    {
        GameWindowTitle = "<default>";
        InstanceCustomLaunchArguments = string.Empty;

        if (string.IsNullOrWhiteSpace(SteamLaunchOptions))
        {
            SteamLaunchOptions = DefaultSteamLaunchOptions;
        }

        Status = "已重置启动选项到默认值";
    }

    [RelayCommand]
    private async Task WriteSteamLaunchOptionsAsync()
    {
        var options = (SteamLaunchOptions ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(options))
        {
            options = DefaultSteamLaunchOptions;
        }

        if (string.IsNullOrWhiteSpace(options))
        {
            Status = "写入失败：无法生成默认 Steam 启动参数";
            await _dialogService.ShowMessageAsync("写入失败", "无法生成 Steam 启动参数，请确认实例路径和启动参数配置。");
            return;
        }

        var steamWasRunning = IsSteamRunning();
        var steamClosedByLauncher = false;
        if (steamWasRunning)
        {
            var approved = await _dialogService.ShowConfirmAsync(
                "需要关闭 Steam",
                "检测到 Steam 正在运行。\n\n需要先关闭 Steam 才能可靠写入启动参数。\n\n是否允许 SVL 自动关闭 Steam，写入后再尝试重启 Steam？");
            if (!approved)
            {
                return;
            }

            var closeResult = await Task.Run(TryCloseSteam);
            if (!closeResult.Success)
            {
                Status = $"写入失败：{closeResult.ErrorMessage}";
                await _dialogService.ShowMessageAsync("写入失败", closeResult.ErrorMessage);
                return;
            }

            steamClosedByLauncher = true;
        }

        var writeResult = await Task.Run(() => TryWriteLaunchOptionsToSteamUserConfig(options));
        if (!writeResult.Success)
        {
            Status = $"写入失败：{writeResult.ErrorMessage}";
            await _dialogService.ShowMessageAsync("写入失败", writeResult.ErrorMessage);
            return;
        }

        OverrideSteamLaunchOptions = true;
        SteamLaunchOptions = options;

        var settings = _settingsStore.Load();
        settings.OverrideSteamLaunchOptions = true;
        settings.SteamLaunchOptions = options;
        _settingsStore.Save(settings);

        var restartWarning = string.Empty;
        if (steamClosedByLauncher)
        {
            var restartResult = await Task.Run(TryStartSteam);
            if (!restartResult.Success)
            {
                restartWarning = $"\n\n启动参数已写入，但自动重启 Steam 失败：{restartResult.ErrorMessage}\n请手动启动 Steam。";
            }
        }

        Status = $"Steam 启动参数已写入（修改 {writeResult.UpdatedFileCount} 个配置，匹配 {writeResult.MatchedFileCount} 个账号）";
        await _dialogService.ShowMessageAsync(
            "写入完成",
            $"已写入 Steam 启动参数（修改 {writeResult.UpdatedFileCount} 个配置，匹配 {writeResult.MatchedFileCount} 个账号）。{restartWarning}");
        NotifyInstanceContextChanged();
    }

    /// <summary>由 MainWindowViewModel 注入，用于获取版本选择页面的 Base 路径列表供 SMAPI 安装对话框选择。</summary>
    public Func<IReadOnlyList<string>>? AvailableGamePathsProvider { get; set; }

    [RelayCommand]
    private async Task ChangeSmapiVersionAsync()
    {
        if (ShowSwitchToSmapiHint)
        {
            Status = "检测到当前路径已安装 SMAPI，请先切换到 SMAPI 版本";
            return;
        }

        var targetPath = ResolveCurrentInstancePath();
        if (string.IsNullOrWhiteSpace(targetPath) || !Directory.Exists(targetPath))
        {
            Status = "当前实例路径不可用";
            return;
        }

        // 判断当前实例形态：隔离实例（base/versions/<name>）走"更新当前实例"（保留用户 Mods，替换运行时），
        // Base 路径走"创建新隔离实例"。与旧架构 ChangeSmapiVersionAsync 的 IsUpdateMode 语义对齐。
        var isUpdate = TryGetVersionRootDirectory(targetPath, out var currentVersionRoot);
        var derivedBasePath = string.Empty;
        var instanceName = ResolveCurrentInstanceName();
        if (isUpdate)
        {
            var trimmedVersionRoot = currentVersionRoot.TrimEnd(
                System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            var versionsDir = System.IO.Path.GetDirectoryName(trimmedVersionRoot);
            derivedBasePath = System.IO.Path.GetDirectoryName(versionsDir ?? string.Empty) ?? string.Empty;
            // 更新必须复用现有版本目录名（显示用实例名可能与目录名不一致）
            instanceName = System.IO.Path.GetFileName(trimmedVersionRoot);
            if (string.IsNullOrWhiteSpace(derivedBasePath) || !Directory.Exists(derivedBasePath))
            {
                Status = "无法解析当前实例的 Base 游戏路径";
                return;
            }
        }

        Models.DownloadTaskItem? taskItem = null;
        var actionText = isUpdate ? "更新" : "安装";
        using var installCts = new CancellationTokenSource();
        try
        {
            // 获取可选路径列表（来自版本选择页面的 Base 路径列表）
            var availablePaths = AvailableGamePathsProvider?.Invoke();
            if (availablePaths == null || availablePaths.Count == 0)
            {
                availablePaths = new List<string> { isUpdate ? derivedBasePath : targetPath };
            }

            // 弹出版本选择弹窗
            var selected = await _dialogService.ShowSmapiVersionPickerAsync(
                targetPath, targetPath, _catalogService, "选择 SMAPI 版本", availablePaths);

            if (selected == null || string.IsNullOrWhiteSpace(selected.DownloadUrl))
            {
                Status = "已取消 SMAPI 安装";
                return;
            }

            // 安装目标 Base 路径：更新模式固定为当前实例所属 Base（否则会在实例目录下装出嵌套 versions），
            // 新装模式使用用户在对话框中选择的路径
            var gameBasePath = isUpdate
                ? derivedBasePath
                : (!string.IsNullOrWhiteSpace(selected.TargetPath) ? selected.TargetPath : targetPath);

            // 创建任务项并通知主窗口加入任务队列
            taskItem = new Models.DownloadTaskItem
            {
                Name = $"SMAPI {selected.Version} - {instanceName}",
                Status = "等待下载",
                Progress = 0,
                TaskKind = Models.DownloadTaskKind.Generic,
                TaskAction = Models.DownloadTaskAction.InstallSmapi,
                SourceUrl = selected.DownloadUrl,
                CanCancel = true,
                CanRetry = false,
                TargetGamePath = gameBasePath,
                TargetInstanceName = instanceName
            };
            taskItem.CancelRequested = () =>
            {
                try { installCts.Cancel(); } catch { /* CTS 已释放时忽略 */ }
            };

            SmapiInstallTaskCreated?.Invoke(taskItem);

            Status = $"正在下载 SMAPI {selected.Version}...";
            IsInstallingSmapi = true;
            SmapiInstallProgressText = "正在下载...";

            // 下载 SMAPI zip（委托给 SmapiDownloadService，支持缓存与 NXM 回调）
            var zipPath = await _smapiDownloadService.DownloadZipAsync(
                selected,
                taskItem,
                text => SmapiInstallProgressText = text,
                installCts.Token);
            if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
            {
                taskItem.SetState(DownloadTaskState.Failed, "下载失败");
                taskItem.FailedDetails = "SMAPI 下载失败：文件不存在";
                Status = "SMAPI 下载失败";
                return;
            }

            taskItem.SetState(DownloadTaskState.Installing, "安装中");
            taskItem.Progress = 60;
            SmapiInstallProgressText = $"正在{actionText}...";
            Status = $"正在{actionText} SMAPI...";

            // 安装/更新 SMAPI
            var installResult = await _smapiInstallService.InstallFromZipAsync(
                zipPath,
                gameBasePath,
                instanceName,
                updateExisting: isUpdate,
                cancellationToken: installCts.Token,
                logger: msg => System.Diagnostics.Debug.WriteLine($"[VersionSettings][SMAPI] {msg}"));

            if (installResult.IsSuccess)
            {
                // 更新实例配置
                HasInstalledSmapi = true;
                IsSmapiInstance = true;
                SelectedLaunchMode = "SMAPI";
                SmapiVersionText = selected.Version;
                SaveVersionSettings();

                // 写入 SMAPI 预设图标（Modded.png），物化"此实例为 SMAPI"标识到磁盘。
                // 仅当用户未设置自定义图标时写入，避免覆盖个性化设置。
                // CleanupRuntimeDirectoryForUpdate 更新时会保留 .svl-* 文件，图标自动延续。
                TryWriteDefaultSmapiIcon();

                RefreshInstanceRuntimeInfo();

                taskItem.Progress = 100;
                taskItem.SetState(DownloadTaskState.Completed, $"SMAPI {selected.Version} {actionText}完成");
                taskItem.InstalledPath = installResult.VersionRootPath;
                Status = $"SMAPI {selected.Version} {actionText}成功";
            }
            else if (installResult.IsCancelled)
            {
                taskItem.SetState(DownloadTaskState.Cancelled, "已取消");
                taskItem.FailedDetails = installResult.Message;
                Status = installResult.Message;
            }
            else
            {
                taskItem.SetState(DownloadTaskState.Failed, $"{actionText}失败");
                taskItem.FailedDetails = installResult.Message;
                Status = $"SMAPI {actionText}失败: {installResult.Message}";
            }
        }
        catch (OperationCanceledException)
        {
            taskItem?.SetState(DownloadTaskState.Cancelled, "已取消");
            Status = $"SMAPI {actionText}已取消";
        }
        catch (Exception ex)
        {
            if (taskItem != null &&
                taskItem.TaskState is not (DownloadTaskState.Completed or DownloadTaskState.Cancelled))
            {
                taskItem.SetState(DownloadTaskState.Failed, $"{actionText}失败");
                taskItem.FailedDetails = ex.Message;
            }

            Status = $"SMAPI {actionText}失败: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[VersionSettings] 更改 SMAPI 版本失败: {ex.Message}");
        }
        finally
        {
            if (taskItem != null)
            {
                taskItem.CancelRequested = null;
                taskItem.CanCancel = false;
            }

            IsInstallingSmapi = false;
            SmapiInstallProgressText = string.Empty;
        }
    }

    [RelayCommand]
    private async Task ChangeIcon()
    {
        var selected = await _dialogService.ShowIconPickerDialogAsync();
        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        var instancePath = ResolveCurrentInstancePath();
        if (string.IsNullOrWhiteSpace(instancePath) || !Directory.Exists(instancePath))
        {
            Status = "当前实例目录不可用，无法保存图标";
            return;
        }

        var iconStorageDir = Services.InstanceIconResolver.ResolveStorageDirectory(instancePath);
        if (string.IsNullOrWhiteSpace(iconStorageDir))
        {
            Status = "图标保存路径不可用";
            return;
        }

        try
        {
            // SMAPI 实例使用独立图标文件名，避免与同路径下的原版实例共享图标
            var iconFileName = IsSmapiInstance
                ? ".svl-instance-icon-smapi.png"
                : ".svl-instance-icon.png";
            var targetPath = Path.Combine(iconStorageDir, iconFileName);
            Directory.CreateDirectory(iconStorageDir);

            if (selected.StartsWith("avares://", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = AssetLoader.Open(new Uri(selected, UriKind.Absolute));
                using var output = File.Create(targetPath);
                stream.CopyTo(output);
            }
            else
            {
                if (!File.Exists(selected))
                {
                    Status = "所选图标文件不存在";
                    return;
                }

                File.Copy(selected, targetPath, true);
            }

            RefreshInstanceRuntimeInfo();
            Status = "图标已更新";
            NotifyInstanceContextChanged();
        }
        catch (Exception ex)
        {
            Status = $"图标保存失败: {ex.Message}";
        }
    }

    /// <summary>
    /// SMAPI 安装成功后写入预设图标（Modded.png），物化到磁盘。
    /// 委托给 InstanceIconResolver.TryWriteDefaultSmapiIcon 公共方法，
    /// 供 VersionSettingsPageViewModel 和 DownloadPageViewModel 共用。
    /// </summary>
    private void TryWriteDefaultSmapiIcon()
    {
        var instancePath = ResolveCurrentInstancePath();
        Services.InstanceIconResolver.TryWriteDefaultSmapiIcon(instancePath);
    }

    [RelayCommand]
    private void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            Status = "目标目录不存在";
            return;
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
            Status = $"已打开目录: {path}";
        }
        catch (Exception ex)
        {
            Status = $"打开目录失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DeleteCurrentVersion()
    {
        var instancePath = ResolveCurrentInstancePath();
        if (!TryGetVersionRootDirectory(instancePath, out var versionRoot))
        {
            Status = "Base 版本不可删除，仅可卸载其 SMAPI";
            return;
        }

        var confirmed = await _dialogService.ShowConfirmAsync(
            "确认删除版本",
            $"确定要删除当前版本目录吗？\n\n{versionRoot}\n\n该操作不可撤销。");

        if (!confirmed)
        {
            return;
        }

        // 等待对话框完全关闭，避免 UI 线程阻塞导致对话框残留
        await Task.Delay(50);

        // 删除前先清除图标引用：Image 控件加载 .svl-instance-icon.gif 后会锁定文件，
        // 导致 Directory.Delete 失败。将图标切换为预设图标（不引用本地文件），
        // 同时通知 LaunchPage 清除图标引用，等待 UI 刷新释放文件句柄后再删除。
        InstanceIconSource = "avares://SVL.Avalonia/Assets/Icons/Vanilla.png";
        NotifyInstanceContextChanged();
        await Task.Delay(100);

        try
        {
            // 在后台线程执行删除，避免阻塞 UI 线程导致对话框无法关闭
            // 先处理 Content/game junction/symlink，避免 Directory.Delete 跟随连接误删源目录
            // 参考 SVL.Core.Platform.Services.SmapiInstallService.CleanupVersionDirectory
            await Task.Run(() => DeleteVersionDirectorySafe(versionRoot));

            // 删除后自动切换到所在的 Base 版本路径
            // versionRoot 结构: gameBasePath/versions/instanceName
            // Base 路径 = versions 目录的父目录
            var settings = _settingsStore.Load();
            var versionDirParent = new DirectoryInfo(versionRoot).Parent; // versions 目录
            var basePath = versionDirParent?.Parent?.FullName; // gameBasePath

            if (!string.IsNullOrWhiteSpace(basePath) && Directory.Exists(basePath))
            {
                settings.PreferredInstancePath = basePath;
                // 实例名取 Base 路径的目录名（与 InstancesPage 的显示一致），避免固定显示 "Default Instance"
                var baseInstanceName = ResolvePathDisplayName(basePath);
                settings.InstanceName = baseInstanceName;
                settings.PreferredLaunchMode = "Vanilla";
                _settingsStore.Save(settings);

                // 同步当前 ViewModel 的 InstanceName 属性，使 UI 立即显示正确的实例名
                InstanceName = baseInstanceName;
            }
            else
            {
                // 找不到 Base 路径，清空当前选择
                if (!string.IsNullOrWhiteSpace(settings.PreferredInstancePath) &&
                    settings.PreferredInstancePath.StartsWith(versionRoot, StringComparison.OrdinalIgnoreCase))
                {
                    settings.PreferredInstancePath = string.Empty;
                    _settingsStore.Save(settings);
                }
            }

            RefreshInstanceRuntimeInfo();
            NotifyInstanceContextChanged();
            Status = "当前版本已删除，已切换到 Base 版本";
            NotifyOverviewActionStateChanged();

            // 返回主页面
            RequestReturnToLaunch?.Invoke();
        }
        catch (Exception ex)
        {
            Status = $"删除版本失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 安全删除版本目录，先处理 game/Content junction/symlink 避免误删源目录。
    /// 参考 SVL.Core.Platform.Services.SmapiInstallService.CleanupVersionDirectory。
    /// </summary>
    private static void DeleteVersionDirectorySafe(string versionRoot)
    {
        if (string.IsNullOrWhiteSpace(versionRoot) || !Directory.Exists(versionRoot))
        {
            return;
        }

        // 检查 game 本身是否为 junction/symlink（新版隔离模式：game -> gameBasePath）
        var gamePath = Path.Combine(versionRoot, "game");
        if (Directory.Exists(gamePath) && IsJunctionOrSymlink(gamePath))
        {
            RemoveJunction(gamePath);
        }

        // 检查 game/Content 是否为 junction/symlink（旧版隔离模式：Content -> gameBasePath/Content）
        var contentPath = Path.Combine(versionRoot, "game", "Content");
        if (Directory.Exists(contentPath) && IsJunctionOrSymlink(contentPath))
        {
            RemoveJunction(contentPath);
        }

        // 也检查直接 Content 目录（不嵌套 game/）
        var directContentPath = Path.Combine(versionRoot, "Content");
        if (Directory.Exists(directContentPath) && IsJunctionOrSymlink(directContentPath))
        {
            RemoveJunction(directContentPath);
        }

        Directory.Delete(versionRoot, true);
    }

    /// <summary>判断路径是否为 junction 或 symbolic link。</summary>
    private static bool IsJunctionOrSymlink(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>移除 junction/symlink 目录（不跟随目标，仅删除链接本身）。</summary>
    private static void RemoveJunction(string junctionPath)
    {
        try
        {
            // 使用 cmd /c rmdir 移除 junction（不会删除 junction 指向的实际内容）
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c rmdir \"{junctionPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process != null)
            {
                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // best-effort
        }
    }

    [RelayCommand]
    private async Task UninstallBaseSmapi()
    {
        if (!CanUninstallBaseSmapi)
        {
            Status = "当前实例不是可卸载的 Base SMAPI 实例";
            return;
        }

        var confirmed = await _dialogService.ShowConfirmAsync(
            "确认卸载 Base SMAPI",
            "将移除当前 Base 路径下的 SMAPI 可执行文件，并保留你的 Mods。是否继续？");

        if (!confirmed)
        {
            return;
        }

        var instancePath = ResolveCurrentInstancePath();
        if (string.IsNullOrWhiteSpace(instancePath) || !Directory.Exists(instancePath))
        {
            Status = "当前实例目录不可用";
            return;
        }

        try
        {
            var smapiFiles = new[]
            {
                "StardewModdingAPI.exe",
                "StardewModdingAPI",
                "StardewModdingAPI.dll"
            };

            foreach (var file in smapiFiles)
            {
                var fullPath = Path.Combine(instancePath, file);
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
            }

            HasInstalledSmapi = false;
            IsSmapiInstance = false;
            SmapiVersionText = "未安装";
            SelectedLaunchMode = "原版";
            SaveVersionSettings();
            RefreshInstanceRuntimeInfo();
            Status = "Base SMAPI 已卸载";
        }
        catch (Exception ex)
        {
            Status = $"卸载 Base SMAPI 失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private void SwitchToSmapiVersion()
    {
        if (!ShowSwitchToSmapiHint)
        {
            Status = "当前无需切换到 SMAPI";
            return;
        }

        SelectedLaunchMode = "SMAPI";
        SaveVersionSettings();
        Status = "已切换到 SMAPI 启动模式";
    }

    [RelayCommand]
    private void ToggleSelectAllExportMods()
    {
        if (!IncludeMods || ExportModItems.Count == 0)
        {
            return;
        }

        var allSelected = ExportModItems.All(item => item.IsSelected);
        foreach (var item in ExportModItems)
        {
            item.IsSelected = !allSelected;
        }

        OnPropertyChanged(nameof(SelectedExportModCount));
        OnPropertyChanged(nameof(CanStartExport));
        OnPropertyChanged(nameof(ExportProgressText));
    }

    [RelayCommand]
    private async Task SaveExportConfig()
    {
        try
        {
            var folderPath = await _dialogService.BrowseFolderPathAsync("选择导出配置保存目录");
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return;
            }

            Directory.CreateDirectory(folderPath);
            var targetFileName = $"{SanitizeFileName(FirstNonEmpty(ModpackName, ExportNamePrefix, "SVL-Modpack"))}_export_config.svlexport";
            var targetPath = Path.Combine(folderPath, targetFileName);

            var config = new VersionSettingsExportConfig
            {
                ModpackName = ModpackName,
                ModpackVersion = ModpackVersion,
                ModpackAuthor = ModpackAuthor,
                IncludeMods = IncludeMods,
                IncludeModSettings = IncludeModSettings,
                IncludeSvlLauncher = IncludeSvlLauncher,
                SelectedModKeys = ExportModItems
                    .Where(item => item.IsSelected)
                    .Select(item => item.SelectionKey)
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };

            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(targetPath, json, Encoding.UTF8);

            ExportStatusMessage = $"导出配置已保存: {targetFileName}";
            Status = ExportStatusMessage;
        }
        catch (Exception ex)
        {
            ExportStatusMessage = $"保存配置失败: {ex.Message}";
            Status = ExportStatusMessage;
        }
    }

    [RelayCommand]
    private async Task LoadExportConfig()
    {
        try
        {
            var configPath = await _dialogService.BrowseFilePathAsync(
                "读取导出配置",
                [new global::Avalonia.Platform.Storage.FilePickerFileType("导出配置") { Patterns = ["*.svlexport", "*.json"] }]);

            if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
            {
                return;
            }

            var json = File.ReadAllText(configPath, Encoding.UTF8);
            var config = JsonSerializer.Deserialize<VersionSettingsExportConfig>(json);
            if (config == null)
            {
                ExportStatusMessage = "配置文件格式无效";
                Status = ExportStatusMessage;
                return;
            }

            ModpackName = FirstNonEmpty(config.ModpackName, ModpackName);
            ModpackVersion = FirstNonEmpty(config.ModpackVersion, ModpackVersion);
            ModpackAuthor = FirstNonEmpty(config.ModpackAuthor, ModpackAuthor);
            IncludeMods = config.IncludeMods;
            IncludeModSettings = config.IncludeModSettings;
            IncludeSvlLauncher = config.IncludeSvlLauncher;

            ReloadExportModItems();

            if (config.SelectedModKeys is { Length: > 0 })
            {
                var selectedSet = new HashSet<string>(config.SelectedModKeys, StringComparer.OrdinalIgnoreCase);
                foreach (var item in ExportModItems)
                {
                    item.IsSelected = selectedSet.Contains(item.SelectionKey);
                }
            }

            OnPropertyChanged(nameof(SelectedExportModCount));
            OnPropertyChanged(nameof(CanStartExport));
            OnPropertyChanged(nameof(ExportProgressText));

            ExportStatusMessage = "导出配置已加载";
            Status = "导出配置已加载";
        }
        catch (Exception ex)
        {
            ExportStatusMessage = $"读取配置失败: {ex.Message}";
            Status = ExportStatusMessage;
        }
    }

    [RelayCommand]
    private async Task ExportCurrentMods()
    {
        if (IsExporting)
        {
            return;
        }

        ReloadExportModItems();

        var selectedMods = ExportModItems.Where(item => item.IsSelected).ToList();
        if (IncludeMods && selectedMods.Count == 0)
        {
            ExportStatusMessage = "请至少选择一个 Mod";
            Status = ExportStatusMessage;
            return;
        }

        if (string.IsNullOrWhiteSpace(ModpackName))
        {
            ExportStatusMessage = "请输入整合包名称";
            Status = ExportStatusMessage;
            return;
        }

        var targetFolder = await _dialogService.BrowseFolderPathAsync("选择整合包导出目录");
        if (string.IsNullOrWhiteSpace(targetFolder))
        {
            return;
        }

        Directory.CreateDirectory(targetFolder);
        var defaultName = SanitizeFileName(FirstNonEmpty(ModpackName, ExportNamePrefix, "SVL-Modpack"));
        var outputPath = Path.Combine(targetFolder, $"{defaultName}-{DateTime.Now:yyyyMMddHHmmss}.zip");

        IsExporting = true;
        ExportProgress = 0;
        ExportStatusMessage = "正在准备导出...";
        Status = ExportStatusMessage;

        try
        {
            var settings = _settingsStore.Load();
            var instancePath = settings.PreferredInstancePath;
            var selectedSnapshots = selectedMods
                .Select(item => new ExportModPackageItem
                {
                    Name = item.Name,
                    UniqueId = item.UniqueId,
                    Version = item.Version,
                    Author = item.Author,
                    ModPath = item.ModPath,
                    DirectoryName = item.DirectoryName,
                    SourcePlatform = item.SourcePlatform,
                    SourceProjectId = item.SourceProjectId,
                    SourceFileId = item.SourceFileId
                })
                .ToList();

            ExportProgress = 12;
            await Task.Run(() => BuildVersionSettingsExportPackage(outputPath, instancePath, selectedSnapshots));

            ExportProgress = 100;
            LastExportPath = outputPath;
            ExportStatusMessage = $"导出完成：{Path.GetFileName(outputPath)}";
            Status = ExportStatusMessage;
        }
        catch (Exception ex)
        {
            ExportStatusMessage = $"导出失败: {ex.Message}";
            Status = ExportStatusMessage;
        }
        finally
        {
            IsExporting = false;
        }
    }

    private void BuildVersionSettingsExportPackage(
        string outputPath,
        string instancePath,
        IReadOnlyList<ExportModPackageItem> selectedMods)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "svl-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var exportManifest = new VersionSettingsExportManifest
            {
                SchemaVersion = 1,
                ModpackName = ModpackName,
                ModpackVersion = ModpackVersion,
                ModpackAuthor = ModpackAuthor,
                ExportedAtUtc = DateTimeOffset.UtcNow,
                InstancePath = instancePath,
                IncludeMods = IncludeMods,
                IncludeModSettings = IncludeModSettings,
                IncludeSvlLauncher = IncludeSvlLauncher,
                Notes = "遵循来源平台分发规则，导出包不包含 Mod 本体，仅包含清单与可选配置。",
                Mods = selectedMods.Select(mod => new VersionSettingsExportManifestMod
                {
                    Name = mod.Name,
                    UniqueId = mod.UniqueId,
                    Version = mod.Version,
                    Author = mod.Author,
                    DirectoryName = mod.DirectoryName,
                    SourcePlatform = mod.SourcePlatform,
                    SourceProjectId = mod.SourceProjectId,
                    SourceFileId = mod.SourceFileId,
                    RequiresManualInstall = !mod.HasSourceCredential
                }).ToList()
            };

            File.WriteAllText(
                Path.Combine(tempRoot, "export-manifest.json"),
                JsonSerializer.Serialize(exportManifest, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);

            if (IncludeModSettings)
            {
                var settingsRoot = Path.Combine(tempRoot, "mod-settings");
                foreach (var mod in selectedMods)
                {
                    CopyModSettingsForExport(mod, settingsRoot);
                }
            }

            if (IncludeSvlLauncher)
            {
                var processPath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
                {
                    var launcherRoot = Path.Combine(tempRoot, "launcher");
                    Directory.CreateDirectory(launcherRoot);
                    File.Copy(processPath, Path.Combine(launcherRoot, Path.GetFileName(processPath)), overwrite: true);
                }
            }

            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            ZipFile.CreateFromDirectory(tempRoot, outputPath, CompressionLevel.SmallestSize, includeBaseDirectory: false);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, true);
                }
            }
            catch
            {
                // ignore temp cleanup failure
            }
        }
    }

    private static void CopyModSettingsForExport(ExportModPackageItem mod, string settingsRoot)
    {
        if (string.IsNullOrWhiteSpace(mod.ModPath) || !Directory.Exists(mod.ModPath))
        {
            return;
        }

        var folderName = SanitizeFileName(FirstNonEmpty(mod.UniqueId, mod.DirectoryName, Path.GetFileName(mod.ModPath)));
        var targetRoot = Path.Combine(settingsRoot, folderName);
        Directory.CreateDirectory(targetRoot);

        var rootJsonFiles = Directory.GetFiles(mod.ModPath, "*.json", SearchOption.TopDirectoryOnly)
            .Where(file =>
            {
                var fileName = Path.GetFileName(file);
                return !string.Equals(fileName, "manifest.json", StringComparison.OrdinalIgnoreCase) &&
                       !string.Equals(fileName, "svl-source.json", StringComparison.OrdinalIgnoreCase) &&
                       !string.Equals(fileName, ".source.json", StringComparison.OrdinalIgnoreCase);
            });

        foreach (var jsonFile in rootJsonFiles)
        {
            var fileName = Path.GetFileName(jsonFile);
            File.Copy(jsonFile, Path.Combine(targetRoot, fileName), overwrite: true);
        }

        var configDir = Path.Combine(mod.ModPath, "config");
        if (Directory.Exists(configDir))
        {
            CopyDirectory(configDir, Path.Combine(targetRoot, "config"));
        }
    }

    [RelayCommand]
    private void OpenExportFolder()
    {
        var path = LastExportPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Status = "暂无可打开的导出文件";
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                Status = "导出目录不可用";
                return;
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
            Status = "已打开导出目录";
        }
        catch
        {
            Status = "打开导出目录失败";
        }
    }

    [RelayCommand]
    private void RefreshDetectedPath()
    {
        RefreshDetectedPathCore(updateStatus: true);
    }

    private void RefreshDetectedPathCore(bool updateStatus)
    {
        var gamePath = _gameInstallPathLocator.TryLocateSteamStardewPath() ?? _gameInstallPathLocator.TryLocateGogStardewPath();
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
        {
            DetectedGamePath = "未探测到";
            RefreshInstanceRuntimeInfo();
            if (updateStatus)
            {
                Status = "未找到游戏目录，请先在实例页配置路径";
            }
            return;
        }

        DetectedGamePath = gamePath;
        RefreshInstanceRuntimeInfo();
        if (updateStatus)
        {
            Status = $"已探测到路径（{DateTime.Now:HH:mm:ss}）";
        }
    }

    [RelayCommand]
    private void SaveVersionSettings()
    {
        var settings = _settingsStore.Load();
        settings.PreferredLaunchMode = SelectedLaunchMode;
        settings.EnableSafeLaunch = EnableSafeLaunch;
        _settingsStore.Save(settings);

        RefreshInstanceRuntimeInfo();
        Status = $"版本设置已保存（{DateTime.Now:HH:mm:ss}）";
        NotifyInstanceContextChanged();
    }

    private void RefreshInstanceRuntimeInfo()
    {
        var settings = _settingsStore.Load();
        var preferredPath = settings.PreferredInstancePath;
        var path = !string.IsNullOrWhiteSpace(preferredPath) && Directory.Exists(preferredPath)
            ? preferredPath
            : DetectedGamePath;
        var hasValidPath = !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);

        InstanceDisplayName = string.IsNullOrWhiteSpace(settings.InstanceName)
            ? ResolvePathDisplayName(path)
            : settings.InstanceName;

        InstanceDescription = settings.InstanceDescription ?? string.Empty;
        IsFavoriteInstance = settings.IsFavoriteInstance;

        InstanceVersionText = DetectGameVersion(path);
        var (hasSmapi, smapiVersion) = DetectSmapi(path);
        HasInstalledSmapi = hasSmapi;
        SmapiVersionText = hasSmapi ? smapiVersion : "未安装";

        var mode = NormalizeLaunchMode(SelectedLaunchMode);
        var preferSmapiByName = !string.IsNullOrWhiteSpace(settings.InstanceName) &&
                                settings.InstanceName.Contains("SMAPI", StringComparison.OrdinalIgnoreCase);
        IsSmapiInstance = string.Equals(mode, "smapi", StringComparison.OrdinalIgnoreCase) ||
                          (string.Equals(mode, "auto", StringComparison.OrdinalIgnoreCase) && preferSmapiByName);

        if (!hasValidPath)
        {
            // 路径无效 → 异常图标。【临时占位】后续有新的预设条件可随时更换
            SetInstanceIconSource(GetImageResource("launch.instance.anomaly",
                Services.InstanceIconResolver.ResolveDefaultPresetIcon(IsSmapiInstance, isAnomaly: true)));
        }
        else
        {
            // 自定义图标优先级高于系统预设图标
            var customIcon = Services.InstanceIconResolver.ResolveIconPath(path, IsSmapiInstance);
            if (!string.IsNullOrWhiteSpace(customIcon))
            {
                SetInstanceIconSource(customIcon);
            }
            else
            {
                // 异常检测：游戏版本未知（关键游戏文件缺失）
                var isAnomaly = string.Equals(InstanceVersionText, "未知版本", StringComparison.Ordinal);
                var iconKey = isAnomaly
                    ? "launch.instance.anomaly"
                    : (IsSmapiInstance ? "launch.instance.modded" : "launch.instance.vanilla");
                var fallback = Services.InstanceIconResolver.ResolveDefaultPresetIcon(IsSmapiInstance, isAnomaly);
                SetInstanceIconSource(GetImageResource(iconKey, fallback));
            }
        }

        OnPropertyChanged(nameof(DefaultSteamLaunchOptions));
        OnPropertyChanged(nameof(SteamLaunchOptionsPreview));

        NotifyOverviewActionStateChanged();
    }

    private void SetInstanceIconSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        var index = source.IndexOfAny(['?', '#']);
        var normalized = index < 0 ? source : source[..index];

        if (File.Exists(normalized))
        {
            InstanceIconSource = $"{normalized}?v={DateTime.UtcNow.Ticks}";
            return;
        }

        if (string.Equals(InstanceIconSource, normalized, StringComparison.OrdinalIgnoreCase))
        {
            InstanceIconSource = string.Empty;
        }

        InstanceIconSource = normalized;
    }

    private string BuildDefaultSteamLaunchOptions()
    {
        var instancePath = ResolveCurrentInstancePath();
        if (string.IsNullOrWhiteSpace(instancePath))
        {
            return string.Empty;
        }

        var smapiCandidates = new[]
        {
            Path.Combine(instancePath, "StardewModdingAPI.exe"),
            Path.Combine(instancePath, "StardewModdingAPI"),
            Path.Combine(instancePath, "StardewModdingAPI.dll")
        };

        var smapiPath = smapiCandidates.FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(smapiPath))
        {
            smapiPath = OperatingSystem.IsWindows()
                ? Path.Combine(instancePath, "StardewModdingAPI.exe")
                : Path.Combine(instancePath, "StardewModdingAPI");
        }

        var optionsBuilder = new StringBuilder($"\"{smapiPath}\" %command%");
        var customArgs = (InstanceCustomLaunchArguments ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(customArgs))
        {
            optionsBuilder.Append(' ');
            optionsBuilder.Append(customArgs);
        }

        return optionsBuilder.ToString();
    }

    private static bool IsSteamRunning()
    {
        try
        {
            return Process.GetProcesses().Any(IsSteamProcess);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSteamProcess(Process process)
    {
        var name = process.ProcessName;
        return string.Equals(name, "steam", StringComparison.OrdinalIgnoreCase);
    }

    private static (bool Success, string ErrorMessage) TryCloseSteam()
    {
        try
        {
            var steamProcesses = Process.GetProcesses().Where(IsSteamProcess).ToArray();
            if (steamProcesses.Length == 0)
            {
                return (true, string.Empty);
            }

            foreach (var process in steamProcesses)
            {
                try
                {
                    if (process.MainWindowHandle != IntPtr.Zero)
                    {
                        process.CloseMainWindow();
                    }
                }
                catch
                {
                    // Continue handling the remaining processes.
                }
            }

            var gracefulDeadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < gracefulDeadline)
            {
                if (!IsSteamRunning())
                {
                    return (true, string.Empty);
                }

                System.Threading.Thread.Sleep(200);
            }

            foreach (var process in Process.GetProcesses().Where(IsSteamProcess))
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                    // Keep going; we validate again below.
                }
            }

            var forcedDeadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < forcedDeadline)
            {
                if (!IsSteamRunning())
                {
                    return (true, string.Empty);
                }

                System.Threading.Thread.Sleep(200);
            }

            return (false, "无法关闭 Steam，请先手动关闭后再重试。");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static (bool Success, string ErrorMessage) TryStartSteam()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null)?.ToString();
                if (string.IsNullOrWhiteSpace(steamPath))
                {
                    steamPath = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null)?.ToString();
                }

                if (string.IsNullOrWhiteSpace(steamPath) || !Directory.Exists(steamPath))
                {
                    return (false, "未找到 Steam 安装目录。");
                }

                var steamExe = Path.Combine(steamPath, "steam.exe");
                if (!File.Exists(steamExe))
                {
                    return (false, "未找到 steam.exe。");
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = steamExe,
                    UseShellExecute = true
                });
                return (true, string.Empty);
            }

            if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    ArgumentList = { "-a", "Steam" },
                    UseShellExecute = false
                });
                return (true, string.Empty);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "steam",
                UseShellExecute = false
            });
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private (bool Success, int UpdatedFileCount, int MatchedFileCount, string ErrorMessage) TryWriteLaunchOptionsToSteamUserConfig(string launchOptions)
    {
        try
        {
            List<string> configFiles;

            if (OperatingSystem.IsWindows())
            {
                var steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null)?.ToString();
                if (string.IsNullOrWhiteSpace(steamPath))
                {
                    steamPath = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null)?.ToString();
                }

                if (string.IsNullOrWhiteSpace(steamPath) || !Directory.Exists(steamPath))
                {
                    return (false, 0, 0, "未找到 Steam 安装目录。请先启动 Steam，再重试。");
                }

                var userdataDir = Path.Combine(steamPath, "userdata");
                if (!Directory.Exists(userdataDir))
                {
                    return (false, 0, 0, "未找到 Steam userdata 目录。请确认本机 Steam 已登录过账号。");
                }

                configFiles = GetSteamLocalConfigFilesInPriorityOrder(userdataDir);
            }
            else
            {
                configFiles = GetSteamLocalConfigFilesInPriorityOrderFallback();
            }

            if (configFiles.Count == 0)
            {
                return (false, 0, 0, "未找到任何 Steam 账号配置（userdata 下无 localconfig.vdf）。");
            }

            var updatedCount = 0;
            var matchedCount = 0;

            foreach (var configPath in configFiles)
            {
                if (!TryUpsertLaunchOptionsInLocalConfig(configPath, "413150", launchOptions, out var changed, out var matched))
                {
                    continue;
                }

                if (matched)
                {
                    matchedCount++;
                }

                if (changed)
                {
                    updatedCount++;
                }
            }

            if (matchedCount == 0)
            {
                return (false, 0, 0, "未在 Steam 配置中找到 Stardew Valley（AppId 413150）条目。请先通过 Steam 启动一次游戏后再试。");
            }

            return (true, updatedCount, matchedCount, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, 0, 0, ex.Message);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static List<string> GetSteamLocalConfigFilesInPriorityOrder(string userdataDir)
    {
        var result = new List<string>();

        var activeUserText = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", null)?.ToString();
        if (long.TryParse(activeUserText, out _))
        {
            var activeConfig = Path.Combine(userdataDir, activeUserText, "config", "localconfig.vdf");
            if (File.Exists(activeConfig))
            {
                result.Add(activeConfig);
            }
        }

        var others = Directory.GetDirectories(userdataDir)
            .Where(path => long.TryParse(Path.GetFileName(path), out _))
            .Select(path => Path.Combine(path, "config", "localconfig.vdf"))
            .Where(File.Exists)
            .Where(path => !result.Contains(path, StringComparer.OrdinalIgnoreCase));

        result.AddRange(others);
        return result;
    }

    private List<string> GetSteamLocalConfigFilesInPriorityOrderFallback()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();

        foreach (var userdataRoot in GetSteamUserdataRoots())
        {
            if (!Directory.Exists(userdataRoot))
            {
                continue;
            }

            var accountDirs = Directory.GetDirectories(userdataRoot)
                .Where(path => long.TryParse(Path.GetFileName(path), out _));

            foreach (var accountDir in accountDirs)
            {
                var configPath = Path.Combine(accountDir, "config", "localconfig.vdf");
                if (!File.Exists(configPath))
                {
                    continue;
                }

                if (seen.Add(configPath))
                {
                    ordered.Add(configPath);
                }
            }
        }

        return ordered;
    }

    private IEnumerable<string> GetSteamUserdataRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static void TryAdd(HashSet<string> set, string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (!Directory.Exists(path))
            {
                return;
            }

            set.Add(path);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        TryAdd(roots, Path.Combine(home, "Library", "Application Support", "Steam", "userdata"));
        TryAdd(roots, Path.Combine(home, ".steam", "steam", "userdata"));
        TryAdd(roots, Path.Combine(home, ".local", "share", "Steam", "userdata"));
        TryAdd(roots, Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam", "userdata"));

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        TryAdd(roots, Path.Combine(programFilesX86, "Steam", "userdata"));
        TryAdd(roots, Path.Combine(programFiles, "Steam", "userdata"));

        var steamGamePath = _gameInstallPathLocator.TryLocateSteamStardewPath();
        if (!string.IsNullOrWhiteSpace(steamGamePath) && Directory.Exists(steamGamePath))
        {
            var current = new DirectoryInfo(steamGamePath);
            while (current != null)
            {
                if (string.Equals(current.Name, "steamapps", StringComparison.OrdinalIgnoreCase))
                {
                    var steamRoot = current.Parent?.FullName;
                    TryAdd(roots, Path.Combine(steamRoot ?? string.Empty, "userdata"));
                    break;
                }

                current = current.Parent;
            }
        }

        return roots;
    }

    private static bool TryUpsertLaunchOptionsInLocalConfig(
        string configPath,
        string appId,
        string launchOptions,
        out bool changed,
        out bool matched)
    {
        changed = false;
        matched = false;

        var lines = File.ReadAllLines(configPath).ToList();
        if (lines.Count == 0)
        {
            return false;
        }

        var escaped = launchOptions.Replace("\\", "\\\\").Replace("\"", "\\\"");

        for (var i = 0; i < lines.Count; i++)
        {
            if (!string.Equals(lines[i].Trim(), "\"apps\"", StringComparison.Ordinal))
            {
                continue;
            }

            var appsBraceLine = FindNextNonEmptyLine(lines, i + 1);
            if (appsBraceLine < 0 || lines[appsBraceLine].Trim() != "{")
            {
                continue;
            }

            var appsEndLine = FindMatchingBraceLine(lines, appsBraceLine);
            if (appsEndLine < 0)
            {
                continue;
            }

            var appKey = $"\"{appId}\"";
            var appLine = -1;
            for (var j = appsBraceLine + 1; j < appsEndLine; j++)
            {
                if (!string.Equals(lines[j].Trim(), appKey, StringComparison.Ordinal))
                {
                    continue;
                }

                appLine = j;
                break;
            }

            if (appLine >= 0)
            {
                matched = true;
                var appBodyStart = FindNextNonEmptyLine(lines, appLine + 1);
                if (appBodyStart < 0 || lines[appBodyStart].Trim() != "{")
                {
                    return false;
                }

                var appBodyEnd = FindMatchingBraceLine(lines, appBodyStart);
                if (appBodyEnd < 0)
                {
                    return false;
                }

                var launchLine = -1;
                for (var j = appBodyStart + 1; j < appBodyEnd; j++)
                {
                    if (!lines[j].TrimStart().StartsWith("\"LaunchOptions\"", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    launchLine = j;
                    break;
                }

                if (launchLine >= 0)
                {
                    var originalLine = lines[launchLine];
                    var keyPos = originalLine.IndexOf("\"LaunchOptions\"", StringComparison.Ordinal);
                    var valueStart = keyPos >= 0
                        ? originalLine.IndexOf('"', keyPos + "\"LaunchOptions\"".Length)
                        : -1;

                    if (valueStart < 0)
                    {
                        var prefixLen = originalLine.IndexOf('"');
                        var prefix = prefixLen >= 0 ? originalLine[..prefixLen] : string.Empty;
                        var fallbackLine = $"{prefix}\"LaunchOptions\"\t\t\"{escaped}\"";
                        if (!string.Equals(originalLine, fallbackLine, StringComparison.Ordinal))
                        {
                            lines[launchLine] = fallbackLine;
                            changed = true;
                        }
                    }
                    else
                    {
                        var valueEnd = FindNextUnescapedQuote(originalLine, valueStart + 1);
                        if (valueEnd > valueStart)
                        {
                            var rewritten = originalLine[..(valueStart + 1)]
                                            + escaped
                                            + originalLine[valueEnd..];

                            if (!string.Equals(originalLine, rewritten, StringComparison.Ordinal))
                            {
                                lines[launchLine] = rewritten;
                                changed = true;
                            }
                        }
                    }
                }
                else
                {
                    var indent = GetLineIndent(lines[appLine]) + "\t";
                    lines.Insert(appBodyEnd, $"{indent}\"LaunchOptions\"\t\t\"{escaped}\"");
                    changed = true;
                }

                break;
            }

            var appIndent = GetLineIndent(lines[appsBraceLine]) + "\t";
            var appLines = new[]
            {
                $"{appIndent}{appKey}",
                $"{appIndent}{{",
                $"{appIndent}\t\"LaunchOptions\"\t\t\"{escaped}\"",
                $"{appIndent}}}"
            };
            lines.InsertRange(appsEndLine, appLines);
            matched = true;
            changed = true;
            break;
        }

        if (changed)
        {
            File.WriteAllLines(configPath, lines, Encoding.UTF8);
        }

        return true;
    }

    private static int FindNextNonEmptyLine(List<string> lines, int startIndex)
    {
        for (var i = startIndex; i < lines.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindMatchingBraceLine(List<string> lines, int openBraceLine)
    {
        var depth = 0;
        for (var i = openBraceLine; i < lines.Count; i++)
        {
            var line = lines[i];
            for (var c = 0; c < line.Length; c++)
            {
                if (line[c] == '{')
                {
                    depth++;
                }
                else if (line[c] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }
        }

        return -1;
    }

    private static int FindNextUnescapedQuote(string text, int startIndex)
    {
        for (var i = startIndex; i < text.Length; i++)
        {
            if (text[i] != '"')
            {
                continue;
            }

            var slashCount = 0;
            for (var j = i - 1; j >= 0 && text[j] == '\\'; j--)
            {
                slashCount++;
            }

            if (slashCount % 2 == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static string GetLineIndent(string line)
    {
        var idx = 0;
        while (idx < line.Length && char.IsWhiteSpace(line[idx]))
        {
            idx++;
        }

        return idx > 0 ? line[..idx] : string.Empty;
    }

    private string ResolveCurrentInstancePath()
    {
        var settings = _settingsStore.Load();
        if (!string.IsNullOrWhiteSpace(settings.PreferredInstancePath) && Directory.Exists(settings.PreferredInstancePath))
        {
            return settings.PreferredInstancePath;
        }

        return !string.IsNullOrWhiteSpace(DetectedGamePath) && Directory.Exists(DetectedGamePath)
            ? DetectedGamePath
            : string.Empty;
    }

    private string ResolveCurrentInstanceName()
    {
        var name = InstanceName?.Trim();
        return string.IsNullOrWhiteSpace(name) ? "Default Instance" : name;
    }

    private static bool TryGetVersionRootDirectory(string instancePath, out string versionRoot)
    {
        versionRoot = string.Empty;
        if (string.IsNullOrWhiteSpace(instancePath) || !Directory.Exists(instancePath))
        {
            return false;
        }

        var current = new DirectoryInfo(instancePath);
        DirectoryInfo? child = null;

        while (current != null)
        {
            if (string.Equals(current.Name, "versions", StringComparison.OrdinalIgnoreCase))
            {
                if (child != null)
                {
                    versionRoot = child.FullName;
                    return true;
                }

                return false;
            }

            child = current;
            current = current.Parent;
        }

        return false;
    }

    private string GetImageResource(string key, string fallback)
    {
        var value = _imageResourceService.Get(key);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string ResolvePathDisplayName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Default Instance";
        }

        var trimmedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmedPath);
        return string.IsNullOrWhiteSpace(name) ? "Default Instance" : name;
    }

    private static string DetectGameVersion(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
        {
            return "未知版本";
        }

        var depsPath = Path.Combine(gamePath, "Stardew Valley.deps.json");
        if (File.Exists(depsPath))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(depsPath));
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
                // fallback to file version
            }
        }

        var dllPath = Path.Combine(gamePath, "Stardew Valley.dll");
        if (File.Exists(dllPath))
        {
            try
            {
                var fileVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(dllPath).FileVersion;
                return string.IsNullOrWhiteSpace(fileVersion) ? "未知版本" : fileVersion;
            }
            catch
            {
                return "未知版本";
            }
        }

        return "未知版本";
    }

    private static (bool HasSmapi, string Version) DetectSmapi(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
        {
            return (false, "未安装");
        }

        var markers = new[]
        {
            Path.Combine(gamePath, "StardewModdingAPI.exe"),
            Path.Combine(gamePath, "StardewModdingAPI"),
            Path.Combine(gamePath, "StardewModdingAPI.dll")
        };

        var markerPath = markers.FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(markerPath))
        {
            return (false, "未安装");
        }

        try
        {
            var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(markerPath).FileVersion;
            return (true, string.IsNullOrWhiteSpace(version) ? "Unknown" : version);
        }
        catch
        {
            return (true, "Unknown");
        }
    }

    private static string NormalizeLaunchMode(string mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return "auto";
        }

        if (string.Equals(mode, "SMAPI", StringComparison.OrdinalIgnoreCase))
        {
            return "smapi";
        }

        if (string.Equals(mode, "原版", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "vanilla", StringComparison.OrdinalIgnoreCase))
        {
            return "vanilla";
        }

        return "auto";
    }

    private int GetModCheckConcurrency()
    {
        var settings = _settingsStore.Load();
        var configured = settings.MaxConcurrentModUpdateChecks;
        if (configured <= 0)
        {
            configured = 4;
        }

        return Math.Max(1, Math.Min(16, configured));
    }

    private static HttpClient CreateModNetworkHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("SVL-Avalonia-ModChecks/1.0");
        return client;
    }

    private static void ApplyNexusHeaders(HttpRequestMessage request, AppUserSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.NexusOAuthAccessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.NexusOAuthAccessToken);
        }
        else if (!string.IsNullOrWhiteSpace(settings.NexusApiKey))
        {
            request.Headers.TryAddWithoutValidation("apikey", settings.NexusApiKey);
        }

        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Application-Name", "Stardew Valley Launcher");
        request.Headers.TryAddWithoutValidation("Application-Version", "1.0.0");
        request.Headers.TryAddWithoutValidation("Protocol-Version", "1.0.0");
    }

    private static bool HasNexusCredential(AppUserSettings settings)
    {
        return !string.IsNullOrWhiteSpace(settings.NexusOAuthAccessToken) ||
               !string.IsNullOrWhiteSpace(settings.NexusApiKey);
    }

    private static long GetJsonLongByCandidates(JsonElement element, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!element.TryGetProperty(candidate, out var propertyElement))
            {
                continue;
            }

            if (propertyElement.ValueKind == JsonValueKind.Number && propertyElement.TryGetInt64(out var value))
            {
                return value;
            }

            if (propertyElement.ValueKind == JsonValueKind.String && long.TryParse(propertyElement.GetString(), out value))
            {
                return value;
            }
        }

        return 0;
    }

    private static bool WriteSourceCredential(string modDir, LocalSourceMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(modDir) || !Directory.Exists(modDir) || metadata == null)
        {
            return false;
        }

        try
        {
            var path = Path.Combine(modDir, "svl-source.json");
            File.WriteAllText(path, JsonSerializer.Serialize(metadata, s_sourceJsonOptions), Encoding.UTF8);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed class LocalModUpdateCheckResult
    {
        public bool IsChecked { get; set; }

        public bool HasUpdate { get; set; }

        public bool IsTokenExpired { get; set; }

        public string LatestVersion { get; set; } = string.Empty;

        public string UpdateSource { get; set; } = string.Empty;

        public string CurseforgeProjectId { get; set; } = string.Empty;

        public string NexusModsProjectId { get; set; } = string.Empty;

        /// <summary>更新下载 URL（Curseforge 直链或 Nexus NXM 链接），用于批量更新入队。</summary>
        public string UpdateUrl { get; set; } = string.Empty;

        /// <summary>Nexus file id（用于构造 NXM 下载链接）。</summary>
        public long UpdateFileId { get; set; }
    }

    private sealed class LocalSourceMetadata
    {
        [JsonPropertyName("platform")]
        public string Platform { get; set; } = string.Empty;

        [JsonPropertyName("projectId")]
        public string ProjectId { get; set; } = string.Empty;

        [JsonPropertyName("fileId")]
        public string FileId { get; set; } = string.Empty;

        [JsonPropertyName("modName")]
        public string ModName { get; set; } = string.Empty;

        [JsonPropertyName("fileName")]
        public string FileName { get; set; } = string.Empty;

        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = 3;

        [JsonPropertyName("localization")]
        public LocalSourceLocalization? Localization { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }

    private sealed class LocalSourceLocalization
    {
        [JsonPropertyName("entityType")]
        public string EntityType { get; set; } = string.Empty;

        [JsonPropertyName("platform")]
        public string Platform { get; set; } = string.Empty;

        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("nameZhCn")]
        public string NameZhCn { get; set; } = string.Empty;

        [JsonPropertyName("nameSource")]
        public string NameSource { get; set; } = string.Empty;

        [JsonPropertyName("descriptionZhCn")]
        public string DescriptionZhCn { get; set; } = string.Empty;

        [JsonPropertyName("descriptionSource")]
        public string DescriptionSource { get; set; } = string.Empty;

        [JsonPropertyName("sourceUrl")]
        public string SourceUrl { get; set; } = string.Empty;

        [JsonPropertyName("updatedAt")]
        public string UpdatedAt { get; set; } = string.Empty;

        [JsonPropertyName("contributor")]
        public string Contributor { get; set; } = string.Empty;
    }

    private sealed class LocalCommunityLocalizationEntry
    {
        [JsonPropertyName("entityType")]
        public string EntityType { get; set; } = string.Empty;

        [JsonPropertyName("platform")]
        public string Platform { get; set; } = string.Empty;

        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public LocalCommunityLocalizedText Name { get; set; } = new();

        [JsonPropertyName("description")]
        public LocalCommunityLocalizedText Description { get; set; } = new();

        [JsonPropertyName("meta")]
        public LocalCommunityLocalizationMeta Meta { get; set; } = new();
    }

    private sealed class LocalCommunityLocalizedText
    {
        [JsonPropertyName("zh-CN")]
        public string ZhCn { get; set; } = string.Empty;

        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;
    }

    private sealed class LocalCommunityLocalizationMeta
    {
        [JsonPropertyName("contributor")]
        public string Contributor { get; set; } = string.Empty;

        [JsonPropertyName("sourceUrl")]
        public string SourceUrl { get; set; } = string.Empty;

        [JsonPropertyName("updatedAt")]
        public string UpdatedAt { get; set; } = string.Empty;
    }
}

public partial class ExportModSelectionItem : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected = true;

    public string Name { get; set; } = string.Empty;

    public string UniqueId { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string Author { get; set; } = string.Empty;

    public string ModPath { get; set; } = string.Empty;

    public string DirectoryName { get; set; } = string.Empty;

    public bool IsEnabled { get; set; }

    public string SourcePlatform { get; set; } = "未知";

    public string SourceProjectId { get; set; } = string.Empty;

    public string SourceFileId { get; set; } = string.Empty;

    public bool HasSourceCredential =>
        !string.IsNullOrWhiteSpace(SourcePlatform) &&
        !string.Equals(SourcePlatform, "未知", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(SourceProjectId);

    public string SourceDescription => HasSourceCredential
        ? $"{SourcePlatform} #{SourceProjectId}"
        : "无来源信息（需手动安装）";

    public string SelectionKey => !string.IsNullOrWhiteSpace(UniqueId) ? UniqueId : DirectoryName;
}

public sealed class VersionSettingsExportConfig
{
    public string ModpackName { get; set; } = string.Empty;

    public string ModpackVersion { get; set; } = "1.0.0";

    public string ModpackAuthor { get; set; } = string.Empty;

    public bool IncludeMods { get; set; } = true;

    public bool IncludeModSettings { get; set; } = true;

    public bool IncludeSvlLauncher { get; set; }

    public string[] SelectedModKeys { get; set; } = Array.Empty<string>();
}

public sealed class VersionSettingsExportManifest
{
    public int SchemaVersion { get; set; } = 1;

    public string ModpackName { get; set; } = string.Empty;

    public string ModpackVersion { get; set; } = "1.0.0";

    public string ModpackAuthor { get; set; } = string.Empty;

    public DateTimeOffset ExportedAtUtc { get; set; }

    public string InstancePath { get; set; } = string.Empty;

    public bool IncludeMods { get; set; }

    public bool IncludeModSettings { get; set; }

    public bool IncludeSvlLauncher { get; set; }

    public string Notes { get; set; } = string.Empty;

    public List<VersionSettingsExportManifestMod> Mods { get; set; } = [];
}

public sealed class VersionSettingsExportManifestMod
{
    public string Name { get; set; } = string.Empty;

    public string UniqueId { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string Author { get; set; } = string.Empty;

    public string DirectoryName { get; set; } = string.Empty;

    public string SourcePlatform { get; set; } = string.Empty;

    public string SourceProjectId { get; set; } = string.Empty;

    public string SourceFileId { get; set; } = string.Empty;

    public bool RequiresManualInstall { get; set; }
}

internal sealed class ExportModPackageItem
{
    public string Name { get; set; } = string.Empty;

    public string UniqueId { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string Author { get; set; } = string.Empty;

    public string ModPath { get; set; } = string.Empty;

    public string DirectoryName { get; set; } = string.Empty;

    public string SourcePlatform { get; set; } = string.Empty;

    public string SourceProjectId { get; set; } = string.Empty;

    public string SourceFileId { get; set; } = string.Empty;

    public bool HasSourceCredential =>
        !string.IsNullOrWhiteSpace(SourcePlatform) &&
        !string.Equals(SourcePlatform, "未知", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(SourceProjectId);
}

/// <summary>批量更新入口项：一个可更新 Mod 的下载信息，供 DownloadPage 入队。</summary>
/// <param name="DisplayName">Mod 显示名（用作任务名前缀）。</param>
/// <param name="UpdateUrl">更新下载 URL（Curseforge HTTP 直链或 Nexus NXM 链接）。</param>
/// <param name="UpdateSource">更新来源标记（"NexusMods" / "Curseforge"）。</param>
public sealed record ModBatchUpdateEntry(string DisplayName, string UpdateUrl, string UpdateSource);

public partial class ModManageItem : ObservableObject
{
    public ModManageItem()
    {
        FolderTags.CollectionChanged += (_, _) => NotifyTagChanged();
        CustomTags.CollectionChanged += (_, _) => NotifyTagChanged();
        DisplayDependencies.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasDisplayDependencies));
        };
    }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isBackupItem;

    [ObservableProperty]
    private string _backupOriginalFolderName = string.Empty;

    [ObservableProperty]
    private DateTime? _backupTime;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _version = "未知版本";

    [ObservableProperty]
    private string _directoryName = string.Empty;

    [ObservableProperty]
    private string _folderName = string.Empty;

    [ObservableProperty]
    private string _fullPath = string.Empty;

    [ObservableProperty]
    private string _uniqueId = string.Empty;

    [ObservableProperty]
    private string _author = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _sourceFileName = string.Empty;

    [ObservableProperty]
    private string _curseforgeProjectId = string.Empty;

    [ObservableProperty]
    private string _nexusModsProjectId = string.Empty;

    [ObservableProperty]
    private string _updateSource = string.Empty;

    [ObservableProperty]
    private string _localizationUpdatedAt = string.Empty;

    [ObservableProperty]
    private bool _hasUpdate;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private string _updateStatus = "待检查";

    /// <summary>远端最新版本号（检测后回写），供 UI 展示与批量更新参考。</summary>
    [ObservableProperty]
    private string _latestVersion = string.Empty;

    /// <summary>更新下载 URL（Curseforge 直链或 Nexus NXM 链接），批量更新入队时使用。</summary>
    [ObservableProperty]
    private string _updateUrl = string.Empty;

    public ObservableCollection<string> FolderTags { get; } = [];

    public ObservableCollection<string> CustomTags { get; } = [];

    public ObservableCollection<ModDependencyDisplayItem> DisplayDependencies { get; } = [];

    public IEnumerable<string> AllTags => FolderTags
        .Concat(CustomTags)
        .Where(tag => !string.IsNullOrWhiteSpace(tag))
        .Distinct(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<string> DisplayTags
    {
        get
        {
            var prefixTag = ExtractPrefixCategory(FolderName);
            var folderDisplay = FolderTags
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => string.Equals(tag, prefixTag, StringComparison.OrdinalIgnoreCase)
                    ? $"[前缀] {tag}"
                    : $"[目录] {tag}");

            var customDisplay = CustomTags
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => $"[标签] {tag}");

            return folderDisplay.Concat(customDisplay);
        }
    }

    public bool HasAnyTag => DisplayTags.Any();

    public string TagsDisplay => !AllTags.Any() ? "无标签" : string.Join(" / ", DisplayTags);

    public string EnableStateText => IsEnabled ? "已启用" : "已禁用";

    public string BackupTimeText => BackupTime is DateTime time ? time.ToString("yyyy-MM-dd HH:mm") : "未知";

    public string PrimaryStateText => IsBackupItem ? "备份" : EnableStateText;

    public string SecondaryStateText => IsBackupItem ? BackupTimeText : UpdateStatus;

    public bool IsNormalItem => !IsBackupItem;

    public bool HasDisplayDependencies => DisplayDependencies.Count > 0;

    public bool HasBackupSummary => IsBackupItem && !string.IsNullOrWhiteSpace(BackupOriginalFolderName);

    public string BackupSummaryText => HasBackupSummary ? $"来源目录：{BackupOriginalFolderName}" : string.Empty;

    public string UpdateTagText => HasUpdate ? " | 可更新" : string.Empty;

    public string UpdateTagColor => HasUpdate ? "#3E8EDE" : "#B0B0B0";

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(EnableStateText));
        OnPropertyChanged(nameof(PrimaryStateText));
    }

    partial void OnBackupTimeChanged(DateTime? value)
    {
        OnPropertyChanged(nameof(BackupTimeText));
        OnPropertyChanged(nameof(SecondaryStateText));
    }

    partial void OnIsBackupItemChanged(bool value)
    {
        OnPropertyChanged(nameof(PrimaryStateText));
        OnPropertyChanged(nameof(SecondaryStateText));
        OnPropertyChanged(nameof(IsNormalItem));
        OnPropertyChanged(nameof(HasBackupSummary));
        OnPropertyChanged(nameof(BackupSummaryText));
    }

    partial void OnHasUpdateChanged(bool value)
    {
        OnPropertyChanged(nameof(UpdateTagText));
        OnPropertyChanged(nameof(UpdateTagColor));
    }

    partial void OnBackupOriginalFolderNameChanged(string value)
    {
        OnPropertyChanged(nameof(HasBackupSummary));
        OnPropertyChanged(nameof(BackupSummaryText));
    }

    partial void OnUpdateStatusChanged(string value)
    {
        OnPropertyChanged(nameof(SecondaryStateText));
    }

    public void NotifyTagChanged()
    {
        OnPropertyChanged(nameof(AllTags));
        OnPropertyChanged(nameof(DisplayTags));
        OnPropertyChanged(nameof(HasAnyTag));
        OnPropertyChanged(nameof(TagsDisplay));
    }

    private static string ExtractPrefixCategory(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return string.Empty;
        }

        var trimmed = folderName.Trim();
        var squareMatch = Regex.Match(trimmed, @"^\[(?<name>[^\]]{1,64})\]");
        if (squareMatch.Success)
        {
            return squareMatch.Groups["name"].Value.Trim();
        }

        var cnSquareMatch = Regex.Match(trimmed, @"^【(?<name>[^】]{1,64})】");
        if (cnSquareMatch.Success)
        {
            return cnSquareMatch.Groups["name"].Value.Trim();
        }

        return string.Empty;
    }
}

public sealed partial class InstanceSettingsPageViewModel : FeaturePageViewModelBase
{
    private readonly Services.AppUserSettingsStore _settingsStore;

    public override string Title => "实例设置";
    public override string Description => "对应 WPF InstanceSettingsView，承载 Steam 参数与实例元数据。";

    public string InstanceName { get; set; } = "Default Instance";

    public bool OverrideSteamLaunchOptions { get; set; }

    public string SteamLaunchOptions { get; set; } = string.Empty;

    public string Status { get; private set; } = "已加载";

    public InstanceSettingsPageViewModel(Services.AppUserSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
        var settings = _settingsStore.Load();
        InstanceName = settings.InstanceName;
        OverrideSteamLaunchOptions = settings.OverrideSteamLaunchOptions;
        SteamLaunchOptions = settings.SteamLaunchOptions;
    }

    [RelayCommand]
    private void Save()
    {
        var settings = _settingsStore.Load();
        settings.InstanceName = InstanceName;
        settings.OverrideSteamLaunchOptions = OverrideSteamLaunchOptions;
        settings.SteamLaunchOptions = SteamLaunchOptions;
        _settingsStore.Save(settings);

        Status = $"实例设置已保存（{DateTime.Now:HH:mm:ss}）";
        OnPropertyChanged(nameof(Status));
    }
}

public sealed partial class ExportPageViewModel : FeaturePageViewModelBase
{
    private readonly IGameInstallPathLocator _gameInstallPathLocator;
    private readonly IExternalProcessService _externalProcessService;
    private readonly Services.AppUserSettingsStore _settingsStore;

    public override string Title => "导出";
    public override string Description => "对应 WPF ExportView，承载 Modpack 导出链路。";

    [ObservableProperty]
    private string _exportNamePrefix = "SVL-Modpack";

    [ObservableProperty]
    private string _lastExportPath = string.Empty;

    [ObservableProperty]
    private string _status = "就绪";

    public ExportPageViewModel(
        IGameInstallPathLocator gameInstallPathLocator,
        IExternalProcessService externalProcessService,
        Services.AppUserSettingsStore settingsStore)
    {
        _gameInstallPathLocator = gameInstallPathLocator;
        _externalProcessService = externalProcessService;
        _settingsStore = settingsStore;
    }

    [RelayCommand]
    private void ExportCurrentMods()
    {
        var settings = _settingsStore.Load();
        var gamePath = !string.IsNullOrWhiteSpace(settings.PreferredInstancePath) &&
                       Directory.Exists(settings.PreferredInstancePath)
            ? settings.PreferredInstancePath
            : _gameInstallPathLocator.TryLocateSteamStardewPath() ?? _gameInstallPathLocator.TryLocateGogStardewPath();
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
        {
            Status = "未探测到游戏目录，无法导出";
            return;
        }

        var modsPath = Path.Combine(gamePath, "Mods");
        if (!Directory.Exists(modsPath))
        {
            Status = "当前实例不存在 Mods 目录，无法导出";
            return;
        }

        try
        {
            var exportRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SVL",
                "Avalonia",
                "Exports");
            Directory.CreateDirectory(exportRoot);

            var prefix = string.IsNullOrWhiteSpace(ExportNamePrefix) ? "SVL-Modpack" : ExportNamePrefix.Trim();
            var fileName = $"{prefix}-{DateTime.Now:yyyyMMddHHmmss}.zip";
            var exportPath = Path.Combine(exportRoot, fileName);

            if (File.Exists(exportPath))
            {
                File.Delete(exportPath);
            }

            ZipFile.CreateFromDirectory(modsPath, exportPath, CompressionLevel.SmallestSize, true);

            var metadataPath = Path.ChangeExtension(exportPath, ".metadata.json");
            var metadata = new
            {
                ExportName = fileName,
                ExportTime = DateTimeOffset.Now,
                GamePath = gamePath,
                ModsPath = modsPath,
                InstanceName = settings.InstanceName,
                PreferredLaunchMode = settings.PreferredLaunchMode,
                EnableSafeLaunch = settings.EnableSafeLaunch,
                Source = "SVL.Avalonia"
            };
            File.WriteAllText(metadataPath, System.Text.Json.JsonSerializer.Serialize(metadata, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            }));

            LastExportPath = exportPath;
            Status = $"导出成功（{DateTime.Now:HH:mm:ss}），元数据: {Path.GetFileName(metadataPath)}";
        }
        catch (Exception ex)
        {
            Status = $"导出失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenExportFolder()
    {
        var path = LastExportPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Status = "暂无可打开的导出文件";
            return;
        }

        var opened = _externalProcessService.TryOpenPath(path);
        Status = opened ? "已打开导出文件" : "打开导出文件失败";
    }
}
