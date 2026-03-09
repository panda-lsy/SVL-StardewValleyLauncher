using System;
using System.Collections.ObjectModel;
using System.Linq;
using SVL.Core.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SVL.Core.Download;
using SVL.Core.Download.NexusMods;
using SVL.Core.Logging;

namespace SVL.Desktop.ViewModels;

/// <summary>
/// 任务状态页面ViewModel（显示进度或失败信息）
/// </summary>
public partial class TaskStatusViewModel : ObservableObject
{
    private readonly MainWindowViewModel _mainViewModel;
    private string _trackedTaskId = "";
    private readonly System.Windows.Threading.DispatcherTimer _refreshTimer;

    // Wizard Mod 列表翻页相关
    private int _wizardCurrentPage = 1;
    private const int _wizardPageSize = 5; // 每页显示5个Mod

    [ObservableProperty]
    private string _taskName = "";

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private string _errorMessage = "";

    [ObservableProperty]
    private string _detailMessage = "";

    [ObservableProperty]
    private string _innerErrorMessage = "";

    [ObservableProperty]
    private bool _hasInnerError;

    [ObservableProperty]
    private bool _isFailed = false;  // true=失败, false=进行中

    [ObservableProperty]
    private bool _isCancelled = false;  // true=已取消, false=未取消

    [ObservableProperty]
    private double _progress = 0;

    [ObservableProperty]
    private string _progressDetail = "";

    /// <summary>
    /// 所有进行中的任务列表
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<DownloadTaskViewModel> _activeTasks = new();

    /// <summary>
    /// 当前选中的任务
    /// </summary>
    [ObservableProperty]
    private DownloadTaskViewModel? _selectedTask;

    /// <summary>
    /// 是否显示多个任务
    /// </summary>
    public bool ShowTaskList => ActiveTasks.Count > 1;

    /// <summary>
    /// 操作按钮文本（根据状态动态变化）
    /// </summary>
    [ObservableProperty]
    private string _actionButtonText = "取消任务";

    /// <summary>
    /// 是否显示操作按钮
    /// </summary>
    [ObservableProperty]
    private bool _showActionButton = true;

    [ObservableProperty]
    private string _adviceTitle = "建议操作";

    [ObservableProperty]
    private ObservableCollection<string> _suggestedActions = new();

    /// <summary>
    /// 是否为 Collection Wizard 任务
    /// </summary>
    public bool IsCollectionWizardTask
    {
        get
        {
            var task = DownloadManager.Instance.GetAllTasks()
                .FirstOrDefault(t => string.Equals(t.Id, _trackedTaskId, StringComparison.Ordinal));
            return task is NexusCollectionWizardTask;
        }
    }

    /// <summary>
    /// 是否为批量更新任务
    /// </summary>
    public bool IsBatchUpdateTask
    {
        get
        {
            var task = DownloadManager.Instance.GetAllTasks()
                .FirstOrDefault(t => string.Equals(t.Id, _trackedTaskId, StringComparison.Ordinal));
            return task is ModBatchUpdateTask;
        }
    }

    /// <summary>
    /// 是否为 Curseforge 整合包任务
    /// </summary>
    public bool IsCurseforgeModpackTask
    {
        get
        {
            var task = DownloadManager.Instance.GetAllTasks()
                .FirstOrDefault(t => string.Equals(t.Id, _trackedTaskId, StringComparison.Ordinal));
            return task is LocalCurseforgeModpackInstallTask or CurseforgeModpackDownloadTask;
        }
    }

    /// <summary>
    /// 是否为 SVL 整合包导入任务
    /// </summary>
    public bool IsSvlModpackTask
    {
        get
        {
            var task = DownloadManager.Instance.GetAllTasks()
                .FirstOrDefault(t => string.Equals(t.Id, _trackedTaskId, StringComparison.Ordinal));
            return task is SvlModpackInstallTask;
        }
    }

    /// <summary>
    /// 是否为 NexusMods 浏览器下载任务
    /// </summary>
    public bool IsBrowserDownloadTask
    {
        get
        {
            var task = DownloadManager.Instance.GetAllTasks()
                .FirstOrDefault(t => string.Equals(t.Id, _trackedTaskId, StringComparison.Ordinal));
            return task is NexusModsBrowserDownloadTask;
        }
    }

    /// <summary>
    /// 是否为显示 Mod 列表的任务（Collection Wizard、批量更新、Curseforge整合包、SVL整合包或浏览器下载）
    /// </summary>
    public bool IsShowModListTask => IsCollectionWizardTask || IsBatchUpdateTask || IsCurseforgeModpackTask || IsSvlModpackTask || IsBrowserDownloadTask;

    /// <summary>
    /// 当前 Wizard Mod 名称（支持 Collection Wizard 和浏览器下载）
    /// </summary>
    public string CurrentWizardModName
    {
        get
        {
            var task = DownloadManager.Instance.GetAllTasks()
                .FirstOrDefault(t => string.Equals(t.Id, _trackedTaskId, StringComparison.Ordinal));

            // 浏览器下载任务
            if (task is NexusModsBrowserDownloadTask browserTask)
                return browserTask.Name?.Replace(" (浏览器下载)", "") ?? "";

            // Collection Wizard 任务
            if (task is NexusCollectionWizardTask wizardTask)
            {
                if (wizardTask.ModListResult == null)
                    return "";
                return wizardTask.CurrentMod?.Name ?? "";
            }

            // SVL 整合包导入任务
            if (task is SvlModpackInstallTask svlTask)
                return svlTask.CurrentMod ?? "";

            return "";
        }
    }

    /// <summary>
    /// Wizard 是否正在等待确认（支持 Collection Wizard 和浏览器下载）
    /// </summary>
    public bool IsWizardWaitingConfirmation
    {
        get
        {
            var task = DownloadManager.Instance.GetAllTasks()
                .FirstOrDefault(t => string.Equals(t.Id, _trackedTaskId, StringComparison.Ordinal));

            if (task is NexusCollectionWizardTask wizardTask)
                return wizardTask.Status == DownloadTaskStatus.WaitingConfirmation;

            if (task is NexusModsBrowserDownloadTask browserTask)
                return browserTask.Status == DownloadTaskStatus.WaitingConfirmation;

            if (task is SvlModpackInstallTask svlTask)
                return svlTask.Status == DownloadTaskStatus.WaitingConfirmation;

            return false;
        }
    }

    /// <summary>
    /// Wizard 提示文本（在提示 Card 中显示，支持 Collection Wizard 和浏览器下载）
    /// </summary>
    public string WizardHintText
    {
        get
        {
            var task = DownloadManager.Instance.GetAllTasks()
                .FirstOrDefault(t => string.Equals(t.Id, _trackedTaskId, StringComparison.Ordinal));

            // 浏览器下载任务
            if (task is NexusModsBrowserDownloadTask browserTask)
            {
                if (browserTask.Status == DownloadTaskStatus.WaitingConfirmation)
                    return $"请在浏览器中点击 Manual Download，SVL 将自动接收";
                return browserTask.StatusMessage ?? "";
            }

            // Collection Wizard 任务
            if (task is NexusCollectionWizardTask wt)
            {
                if (wt.ModListResult == null)
                    return "正在准备 Collection 安装，请稍候...";
                if (wt.CurrentMod == null)
                    return "";
                return $"请在浏览器中点击下载，SVL 将自动接收 ({wt.CurrentMod.Name})";
            }

            // SVL 整合包导入任务
            if (task is SvlModpackInstallTask svlTask)
            {
                if (svlTask.Status == DownloadTaskStatus.WaitingConfirmation)
                    return $"请在浏览器中点击 Manual Download，SVL 将自动接收 ({svlTask.CurrentMod})";
                return svlTask.StatusMessage ?? "";
            }

            return "";
        }
    }

    /// <summary>
    /// 是否显示"当前需要下载" Card（支持 Collection Wizard、批量更新、Curseforge整合包和浏览器下载）
    /// </summary>
    public bool ShowCurrentModCard
    {
        get
        {
            var task = DownloadManager.Instance.GetAllTasks()
                .FirstOrDefault(t => string.Equals(t.Id, _trackedTaskId, StringComparison.Ordinal));

            // Collection Wizard 任务
            if (task is NexusCollectionWizardTask wizardTask)
            {
                return wizardTask.ModListResult != null && wizardTask.CurrentMod != null;
            }
            // 批量更新任务
            else if (task is ModBatchUpdateTask batchUpdateTask)
            {
                return batchUpdateTask.CurrentMod != null;
            }
            // Curseforge 整合包任务（本地）
            else if (task is LocalCurseforgeModpackInstallTask curseforgeTask)
            {
                return curseforgeTask.CurrentMod != null;
            }
            // Curseforge 整合包任务（在线下载）
            else if (task is CurseforgeModpackDownloadTask cfDownloadTask)
            {
                return cfDownloadTask.CurrentMod != null;
            }
            // NexusMods 浏览器下载任务
            else if (task is NexusModsBrowserDownloadTask browserTask)
            {
                return browserTask.Status == DownloadTaskStatus.WaitingConfirmation;
            }
            // SVL 整合包导入任务
            else if (task is SvlModpackInstallTask svlTask)
            {
                return svlTask.CurrentModItem != null;
            }

            return false;
        }
    }

    /// <summary>
    /// 是否显示"Mod 列表" Card（支持 Collection Wizard、批量更新、Curseforge整合包和SVL整合包）
    /// </summary>
    public bool ShowModListCard
    {
        get
        {
            var task = DownloadManager.Instance.GetAllTasks()
                .FirstOrDefault(t => string.Equals(t.Id, _trackedTaskId, StringComparison.Ordinal));

            // Collection Wizard 任务
            if (task is NexusCollectionWizardTask wizardTask)
            {
                return wizardTask.ModListResult != null;
            }
            // Curseforge 整合包任务（本地）
            else if (task is LocalCurseforgeModpackInstallTask curseforgeTask)
            {
                return curseforgeTask.ModList != null && curseforgeTask.ModList.Count > 0;
            }
            // Curseforge 整合包任务（在线下载）
            else if (task is CurseforgeModpackDownloadTask cfDownloadTask)
            {
                return cfDownloadTask.ModList != null && cfDownloadTask.ModList.Count > 0;
            }
            // SVL 整合包导入任务
            else if (task is SvlModpackInstallTask svlTask)
            {
                return svlTask.ModList != null && svlTask.ModList.Count > 0;
            }

            return false;
        }
    }

    /// <summary>
    /// Wizard 当前文件下载进度（格式：XX.XX% XXMB/XXMB）
    /// 支持 Collection Wizard、Curseforge 整合包和SVL整合包
    /// </summary>
    public string WizardCurrentFileProgress
    {
        get
        {
            var task = DownloadManager.Instance.GetAllTasks()
                .FirstOrDefault(t => string.Equals(t.Id, _trackedTaskId, StringComparison.Ordinal));

            // 只有在正在下载状态时才显示文件下载进度
            if (task?.Status != DownloadTaskStatus.Downloading)
                return "";

            // Collection Wizard 任务
            if (task is NexusCollectionWizardTask wizardTask && wizardTask.CurrentMod != null)
            {
                var fileProgress = wizardTask.FileDownloadProgress;
                var bytesRead = wizardTask.FileDownloadBytes;
                var totalBytes = wizardTask.FileDownloadTotalBytes;

                if (totalBytes > 0)
                {
                    var currentMB = bytesRead / 1024.0 / 1024.0;
                    var totalMB = totalBytes / 1024.0 / 1024.0;
                    return $"{fileProgress:F2}% {currentMB:F2}MB/{totalMB:F2}MB";
                }
                else if (fileProgress > 0)
                {
                    return $"{fileProgress:F2}%";
                }
            }
            // Curseforge 整合包任务（本地）
            else if (task is LocalCurseforgeModpackInstallTask curseforgeTask && curseforgeTask.CurrentMod != null)
            {
                var fileProgress = curseforgeTask.FileDownloadProgress;
                var bytesRead = curseforgeTask.FileDownloadBytes;
                var totalBytes = curseforgeTask.FileDownloadTotalBytes;

                if (totalBytes > 0)
                {
                    var currentMB = bytesRead / 1024.0 / 1024.0;
                    var totalMB = totalBytes / 1024.0 / 1024.0;
                    return $"{fileProgress:F2}% {currentMB:F2}MB/{totalMB:F2}MB";
                }
                else if (fileProgress > 0)
                {
                    return $"{fileProgress:F2}%";
                }
            }
            // Curseforge 整合包任务（在线下载）
            else if (task is CurseforgeModpackDownloadTask cfDownloadTask && cfDownloadTask.CurrentMod != null)
            {
                var fileProgress = cfDownloadTask.FileDownloadProgress;
                var bytesRead = cfDownloadTask.FileDownloadBytes;
                var totalBytes = cfDownloadTask.FileDownloadTotalBytes;

                if (totalBytes > 0)
                {
                    var currentMB = bytesRead / 1024.0 / 1024.0;
                    var totalMB = totalBytes / 1024.0 / 1024.0;
                    return $"{fileProgress:F2}% {currentMB:F2}MB/{totalMB:F2}MB";
                }
                else if (fileProgress > 0)
                {
                    return $"{fileProgress:F2}%";
                }
            }

            return "";
        }
    }

    /// <summary>
    /// Wizard 任务状态显示文本（"MOD等待下载/下载中/安装中"）
    /// 支持 Collection Wizard 和 Curseforge 整合包
    /// </summary>
    public string WizardStatusDisplayText
    {
        get
        {
            var task = DownloadManager.Instance.GetAllTasks()
                .FirstOrDefault(t => string.Equals(t.Id, _trackedTaskId, StringComparison.Ordinal));

            if (task == null)
                return "MOD等待下载";

            return task.Status switch
            {
                DownloadTaskStatus.Pending => "MOD等待下载",
                DownloadTaskStatus.WaitingConfirmation => "MOD等待下载",
                DownloadTaskStatus.Downloading => "MOD下载中",
                DownloadTaskStatus.Installing => "MOD安装中",
                DownloadTaskStatus.Completed => "安装完成",
                DownloadTaskStatus.Failed => "安装失败",
                DownloadTaskStatus.Cancelled => "已取消",
                _ => "MOD等待下载"
            };
        }
    }

    /// <summary>
    /// Wizard 任务进度文本（例如 "3/93 已完成"）
    /// 支持 Collection Wizard 和 Curseforge 整合包
    /// </summary>
    public string WizardProgressText
    {
        get
        {
            var task = DownloadManager.Instance.GetAllTasks()
                .FirstOrDefault(t => string.Equals(t.Id, _trackedTaskId, StringComparison.Ordinal));

            // Collection Wizard 任务
            if (task is NexusCollectionWizardTask wizardTask && wizardTask.ModListResult?.NexusMods != null)
            {
                var totalMods = wizardTask.ModListResult.NexusMods.Count;
                if (totalMods == 0)
                    return "";

                var completedMods = wizardTask.ModListResult.NexusMods.Count(m =>
                    m.Status == CollectionModDownloadStatus.Completed ||
                    m.Status == CollectionModDownloadStatus.Skipped);

                return $"{completedMods}/{totalMods} 已完成";
            }
            // Curseforge 整合包任务（本地）
            else if (task is LocalCurseforgeModpackInstallTask curseforgeTask && curseforgeTask.ModList != null)
            {
                var totalMods = curseforgeTask.ModList.Count;
                if (totalMods == 0)
                    return "";

                var completedMods = curseforgeTask.ModList.Count(m =>
                    m.Status == CurseforgeModDownloadStatus.Completed ||
                    m.Status == CurseforgeModDownloadStatus.Skipped);

                return $"{completedMods}/{totalMods} 已完成";
            }
            // Curseforge 整合包任务（在线下载）
            else if (task is CurseforgeModpackDownloadTask cfDownloadTask && cfDownloadTask.ModList != null)
            {
                var totalMods = cfDownloadTask.ModList.Count;
                if (totalMods == 0)
                    return "";

                var completedMods = cfDownloadTask.ModList.Count(m =>
                    m.Status == CurseforgeModDownloadStatus.Completed ||
                    m.Status == CurseforgeModDownloadStatus.Skipped);

                return $"{completedMods}/{totalMods} 已完成";
            }
            // SVL 整合包导入任务
            else if (task is SvlModpackInstallTask svlTask && svlTask.ModList != null)
            {
                var totalMods = svlTask.ModList.Count;
                if (totalMods == 0)
                    return "";

                var completedMods = svlTask.ModList.Count(m =>
                    m.Status == SvlModpackModStatus.Completed ||
                    m.Status == SvlModpackModStatus.Skipped);

                return $"{completedMods}/{totalMods} 已完成";
            }

            return "";
        }
    }

    /// <summary>
    /// Wizard 任务进度百分比（0-100）
    /// 支持 Collection Wizard、Curseforge 整合包和SVL整合包
    /// </summary>
    public double WizardProgressPercent
    {
        get
        {
            var task = DownloadManager.Instance.GetAllTasks()
                .FirstOrDefault(t => string.Equals(t.Id, _trackedTaskId, StringComparison.Ordinal));

            // Collection Wizard 任务
            if (task is NexusCollectionWizardTask wizardTask && wizardTask.ModListResult?.NexusMods != null)
            {
                var totalMods = wizardTask.ModListResult.NexusMods.Count;
                if (totalMods == 0)
                    return 0;

                var completedMods = wizardTask.ModListResult.NexusMods.Count(m =>
                    m.Status == CollectionModDownloadStatus.Completed ||
                    m.Status == CollectionModDownloadStatus.Skipped);

                return (double)completedMods / totalMods * 100;
            }
            // Curseforge 整合包任务（本地）
            else if (task is LocalCurseforgeModpackInstallTask curseforgeTask && curseforgeTask.ModList != null)
            {
                var totalMods = curseforgeTask.ModList.Count;
                if (totalMods == 0)
                    return 0;

                var completedMods = curseforgeTask.ModList.Count(m =>
                    m.Status == CurseforgeModDownloadStatus.Completed ||
                    m.Status == CurseforgeModDownloadStatus.Skipped);

                return (double)completedMods / totalMods * 100;
            }
            // Curseforge 整合包任务（在线下载）
            else if (task is CurseforgeModpackDownloadTask cfDownloadTask && cfDownloadTask.ModList != null)
            {
                var totalMods = cfDownloadTask.ModList.Count;
                if (totalMods == 0)
                    return 0;

                var completedMods = cfDownloadTask.ModList.Count(m =>
                    m.Status == CurseforgeModDownloadStatus.Completed ||
                    m.Status == CurseforgeModDownloadStatus.Skipped);

                return (double)completedMods / totalMods * 100;
            }
            // SVL 整合包导入任务
            else if (task is SvlModpackInstallTask svlTask && svlTask.ModList != null)
            {
                var totalMods = svlTask.ModList.Count;
                if (totalMods == 0)
                    return 0;

                var completedMods = svlTask.ModList.Count(m =>
                    m.Status == SvlModpackModStatus.Completed ||
                    m.Status == SvlModpackModStatus.Skipped);

                return (double)completedMods / totalMods * 100;
            }

            return 0;
        }
    }

    /// <summary>
    /// Wizard Mod 列表项（当前页）
    /// 支持 Collection Wizard 和 Curseforge 整合包
    /// </summary>
    public ObservableCollection<WizardModListItemViewModel> WizardModListItems
    {
        get
        {
            var items = new ObservableCollection<WizardModListItemViewModel>();
            var task = DownloadManager.Instance.GetAllTasks()
                .FirstOrDefault(t => string.Equals(t.Id, _trackedTaskId, StringComparison.Ordinal));

            // Collection Wizard 任务
            if (task is NexusCollectionWizardTask wizardTask && wizardTask.ModListResult?.NexusMods != null)
            {
                var allMods = wizardTask.ModListResult.NexusMods.OrderBy(m => m.Phase).ThenBy(m => m.Name).ToList();
                var totalMods = allMods.Count;
                var totalPages = (int)Math.Ceiling((double)totalMods / _wizardPageSize);

                // 计算有效的当前页码（不修改 _wizardCurrentPage，只用于计算）
                var currentPage = _wizardCurrentPage;
                if (currentPage < 1) currentPage = 1;
                if (currentPage > totalPages && totalPages > 0) currentPage = totalPages;

                // 计算当前页的数据范围
                var startIndex = (currentPage - 1) * _wizardPageSize;
                var endIndex = Math.Min(startIndex + _wizardPageSize, totalMods);

                // 生成当前页的数据
                for (int i = startIndex; i < endIndex; i++)
                {
                    items.Add(new WizardModListItemViewModel(allMods[i], i + 1));
                }
            }
            // Curseforge 整合包任务（本地）
            else if (task is LocalCurseforgeModpackInstallTask curseforgeTask && curseforgeTask.ModList != null)
            {
                var allMods = curseforgeTask.ModList.OrderBy(m => m.Name).ToList();
                var totalMods = allMods.Count;
                var totalPages = (int)Math.Ceiling((double)totalMods / _wizardPageSize);

                // 计算有效的当前页码
                var currentPage = _wizardCurrentPage;
                if (currentPage < 1) currentPage = 1;
                if (currentPage > totalPages && totalPages > 0) currentPage = totalPages;

                // 计算当前页的数据范围
                var startIndex = (currentPage - 1) * _wizardPageSize;
                var endIndex = Math.Min(startIndex + _wizardPageSize, totalMods);

                // 生成当前页的数据
                for (int i = startIndex; i < endIndex; i++)
                {
                    items.Add(new WizardModListItemViewModel(allMods[i], i + 1));
                }
            }
            // Curseforge 整合包任务（在线下载）
            else if (task is CurseforgeModpackDownloadTask cfDownloadTask && cfDownloadTask.ModList != null)
            {
                var allMods = cfDownloadTask.ModList.OrderBy(m => m.Name).ToList();
                var totalMods = allMods.Count;
                var totalPages = (int)Math.Ceiling((double)totalMods / _wizardPageSize);

                var currentPage = _wizardCurrentPage;
                if (currentPage < 1) currentPage = 1;
                if (currentPage > totalPages && totalPages > 0) currentPage = totalPages;

                var startIndex = (currentPage - 1) * _wizardPageSize;
                var endIndex = Math.Min(startIndex + _wizardPageSize, totalMods);

                for (int i = startIndex; i < endIndex; i++)
                {
                    items.Add(new WizardModListItemViewModel(allMods[i], i + 1));
                }
            }

            return items;
        }
    }

    /// <summary>
    /// Wizard Mod 列表总页数（同时支持 Collection Wizard、批量更新和 Curseforge 整合包）
    /// </summary>
    public int WizardTotalPages
    {
        get
        {
            var task = DownloadManager.Instance.GetAllTasks()
                .FirstOrDefault(t => string.Equals(t.Id, _trackedTaskId, StringComparison.Ordinal));

            int totalMods = 0;

            if (task is NexusCollectionWizardTask wizardTask && wizardTask.ModListResult?.NexusMods != null)
            {
                totalMods = wizardTask.ModListResult.NexusMods.Count;
            }
            else if (task is ModBatchUpdateTask batchUpdateTask && batchUpdateTask.ModList != null)
            {
                totalMods = batchUpdateTask.ModList.Count;
            }
            else if (task is LocalCurseforgeModpackInstallTask curseforgeTask && curseforgeTask.ModList != null)
            {
                totalMods = curseforgeTask.ModList.Count;
            }
            else if (task is CurseforgeModpackDownloadTask cfDownloadTask && cfDownloadTask.ModList != null)
            {
                totalMods = cfDownloadTask.ModList.Count;
            }
            else if (task is SvlModpackInstallTask svlTask && svlTask.ModList != null)
            {
                totalMods = svlTask.ModList.Count;
            }

            if (totalMods > 0)
            {
                var totalPages = (int)Math.Ceiling((double)totalMods / _wizardPageSize);

                // 自动调整当前页码（如果超出范围）
                if (_wizardCurrentPage > totalPages && totalPages > 0)
                {
                    WizardCurrentPage = totalPages;
                }
                else if (_wizardCurrentPage < 1)
                {
                    WizardCurrentPage = 1;
                }

                return totalPages;
            }

            return 0;
        }
    }

    /// <summary>
    /// Wizard Mod 列表当前页码
    /// </summary>
    public int WizardCurrentPage
    {
        get => _wizardCurrentPage;
        set
        {
            if (_wizardCurrentPage != value)
            {
                _wizardCurrentPage = value;
                OnPropertyChanged(nameof(WizardCurrentPage));
                OnPropertyChanged(nameof(WizardModListItems));
                OnPropertyChanged(nameof(WizardPageInfo));
                OnPropertyChanged(nameof(CanGoToWizardPreviousPage));
                OnPropertyChanged(nameof(CanGoToWizardNextPage));
                OnPropertyChanged(nameof(WizardModListScrollViewerHeight));
            }
        }
    }

    /// <summary>
    /// Wizard Mod 列表页面信息
    /// </summary>
    public string WizardPageInfo
    {
        get
        {
            var totalPages = WizardTotalPages;
            if (totalPages == 0) return "第 0/0 页";
            return $"第 {_wizardCurrentPage}/{totalPages} 页";
        }
    }

    /// <summary>
    /// 是否可以翻到上一页
    /// </summary>
    public bool CanGoToWizardPreviousPage => _wizardCurrentPage > 1;

    /// <summary>
    /// 是否可以翻到下一页
    /// </summary>
    public bool CanGoToWizardNextPage => _wizardCurrentPage < WizardTotalPages;

    /// <summary>
    /// MOD 列表 ScrollViewer 的动态高度
    /// 计算公式：每项高度(44px) × 当前页项数 + 上下内边距
    /// 支持 Collection Wizard、批量更新和 Curseforge 整合包
    /// </summary>
    public double WizardModListScrollViewerHeight
    {
        get
        {
            var task = DownloadManager.Instance.GetAllTasks()
                .FirstOrDefault(t => string.Equals(t.Id, _trackedTaskId, StringComparison.Ordinal));

            int totalMods = 0;

            if (task is NexusCollectionWizardTask wizardTask && wizardTask.ModListResult?.NexusMods != null)
            {
                totalMods = wizardTask.ModListResult.NexusMods.Count;
            }
            else if (task is ModBatchUpdateTask batchUpdateTask && batchUpdateTask.ModList != null)
            {
                totalMods = batchUpdateTask.ModList.Count;
            }
            else if (task is LocalCurseforgeModpackInstallTask curseforgeTask && curseforgeTask.ModList != null)
            {
                totalMods = curseforgeTask.ModList.Count;
            }
            else if (task is CurseforgeModpackDownloadTask cfDownloadTask && cfDownloadTask.ModList != null)
            {
                totalMods = cfDownloadTask.ModList.Count;
            }

            if (totalMods == 0)
                return 200; // 默认高度

            var totalPages = (int)Math.Ceiling((double)totalMods / _wizardPageSize);

            // 计算有效的当前页码
            var currentPage = _wizardCurrentPage;
            if (currentPage < 1) currentPage = 1;
            if (currentPage > totalPages && totalPages > 0) currentPage = totalPages;

            // 计算当前页的实际项数
            var startIndex = (currentPage - 1) * _wizardPageSize;
            var endIndex = Math.Min(startIndex + _wizardPageSize, totalMods);
            var itemsOnCurrentPage = endIndex - startIndex;

            // 每项高度 = 上内边距(12) + 内容高度(~20) + 下内边距(12) + 下边距(4) = 48px
            const double itemHeight = 48;

            // ScrollViewer 的 MaxHeight = 项数 × 每项高度
            return itemsOnCurrentPage * itemHeight;
        }
    }

    // ========== 批量更新任务相关属性 ==========

    /// <summary>
    /// 当前批量更新的 Mod 名称
    /// </summary>
    public string CurrentBatchUpdateModName
    {
        get
        {
            var task = DownloadManager.Instance.GetAllTasks()
                .FirstOrDefault(t => string.Equals(t.Id, _trackedTaskId, StringComparison.Ordinal)) as ModBatchUpdateTask;
            return task?.CurrentMod?.Name ?? "";
        }
    }

    /// <summary>
    /// 批量更新是否正在等待确认
    /// </summary>
    public bool IsBatchUpdateWaitingConfirmation
    {
        get
        {
            var task = DownloadManager.Instance.GetAllTasks()
                .FirstOrDefault(t => string.Equals(t.Id, _trackedTaskId, StringComparison.Ordinal)) as ModBatchUpdateTask;
            return task?.Status == DownloadTaskStatus.WaitingConfirmation;
        }
    }

    public bool ShowBatchUpdateBrowserButton
    {
        get
        {
            var task = DownloadManager.Instance.GetAllTasks()
                .FirstOrDefault(t => string.Equals(t.Id, _trackedTaskId, StringComparison.Ordinal)) as ModBatchUpdateTask;

            return task?.Status == DownloadTaskStatus.WaitingConfirmation
                   && string.Equals(task.CurrentMod?.Platform, "NexusMods", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 批量更新提示文本
    /// </summary>
    public string BatchUpdateHintText
    {
        get
        {
            var task = DownloadManager.Instance.GetAllTasks()
                .FirstOrDefault(t => string.Equals(t.Id, _trackedTaskId, StringComparison.Ordinal)) as ModBatchUpdateTask;

            if (task?.CurrentMod == null)
                return "正在准备批量更新，请稍候...";

            if (string.Equals(task.CurrentMod.Platform, "NexusMods", StringComparison.OrdinalIgnoreCase)
                && task.Status == DownloadTaskStatus.WaitingConfirmation)
            {
                return $"请在浏览器中点击下载，SVL 将自动接收 ({task.CurrentMod.Name})";
            }

            return task.StatusMessage ?? $"正在处理 {task.CurrentMod.Name}...";
        }
    }

    /// <summary>
    /// 是否显示批量更新"当前需要下载" Card
    /// </summary>
    public bool ShowBatchUpdateCurrentModCard
    {
        get
        {
            var task = DownloadManager.Instance.GetAllTasks()
                .FirstOrDefault(t => string.Equals(t.Id, _trackedTaskId, StringComparison.Ordinal)) as ModBatchUpdateTask;
            return task?.CurrentMod != null;
        }
    }

    /// <summary>
    /// 是否显示批量更新"Mod 列表" Card
    /// </summary>
    public bool ShowBatchUpdateModListCard
    {
        get
        {
            var task = DownloadManager.Instance.GetAllTasks()
                .FirstOrDefault(t => string.Equals(t.Id, _trackedTaskId, StringComparison.Ordinal)) as ModBatchUpdateTask;
            return task?.ModList != null && task.ModList.Count > 0;
        }
    }

    /// <summary>
    /// 批量更新 Mod 列表项（当前页）
    /// </summary>
    public ObservableCollection<BatchUpdateModListItemViewModel> BatchUpdateModListItems
    {
        get
        {
            var items = new ObservableCollection<BatchUpdateModListItemViewModel>();
            var task = DownloadManager.Instance.GetAllTasks()
                .FirstOrDefault(t => string.Equals(t.Id, _trackedTaskId, StringComparison.Ordinal)) as ModBatchUpdateTask;

            if (task?.ModList != null)
            {
                var allMods = task.ModList.OrderBy(m => m.Name).ToList();
                var totalMods = allMods.Count;
                var totalPages = (int)Math.Ceiling((double)totalMods / _wizardPageSize);

                // 计算有效的当前页码
                var currentPage = _wizardCurrentPage;
                if (currentPage < 1) currentPage = 1;
                if (currentPage > totalPages && totalPages > 0) currentPage = totalPages;

                // 计算当前页的数据范围
                var startIndex = (currentPage - 1) * _wizardPageSize;
                var endIndex = Math.Min(startIndex + _wizardPageSize, totalMods);

                // 生成当前页的数据
                for (int i = startIndex; i < endIndex; i++)
                {
                    items.Add(new BatchUpdateModListItemViewModel(allMods[i], i + 1, task.CurrentMod));
                }
            }

            return items;
        }
    }

    /// <summary>
    /// 批量更新当前文件下载进度（格式：XX.XX% XXMB/XXMB）
    /// </summary>
    public string BatchUpdateCurrentFileProgress
    {
        get
        {
            var task = DownloadManager.Instance.GetAllTasks()
                .FirstOrDefault(t => string.Equals(t.Id, _trackedTaskId, StringComparison.Ordinal)) as ModBatchUpdateTask;

            // 只有在正在下载状态时才显示文件下载进度
            if (task?.Status != DownloadTaskStatus.Downloading)
                return "";

            // 使用 FileDownloadProgress 属性获取当前文件下载进度（从 0% 开始）
            var fileProgress = task.FileDownloadProgress;
            var bytesRead = task.FileDownloadBytes;
            var totalBytes = task.FileDownloadTotalBytes;

            if (totalBytes > 0)
            {
                var currentMB = bytesRead / 1024.0 / 1024.0;
                var totalMB = totalBytes / 1024.0 / 1024.0;
                return $"{fileProgress:F2}% {currentMB:F2}MB/{totalMB:F2}MB";
            }
            else if (fileProgress > 0)
            {
                return $"{fileProgress:F2}%";
            }

            return "";
        }
    }

    /// <summary>
    /// 批量更新进度文本（例如 "3/10 已完成"）
    /// </summary>
    public string BatchUpdateProgressText
    {
        get
        {
            var task = DownloadManager.Instance.GetAllTasks()
                .FirstOrDefault(t => string.Equals(t.Id, _trackedTaskId, StringComparison.Ordinal)) as ModBatchUpdateTask;

            if (task?.ModList == null)
                return "";

            var totalMods = task.ModList.Count;
            if (totalMods == 0)
                return "";

            var completedMods = task.ModList.Count(m =>
                m.Status == ModBatchUpdateStatus.Success);

            return $"{completedMods}/{totalMods} 已完成";
        }
    }

    /// <summary>
    /// 统一的 Mod 列表项（用于 Collection Wizard、批量更新和 Curseforge 整合包）
    /// </summary>
    public ObservableCollection<ModListItemViewModel> ModListItems
    {
        get
        {
            var items = new ObservableCollection<ModListItemViewModel>();
            var task = DownloadManager.Instance.GetAllTasks()
                .FirstOrDefault(t => string.Equals(t.Id, _trackedTaskId, StringComparison.Ordinal));

            // Collection Wizard 任务
            if (task is NexusCollectionWizardTask wizardTask && wizardTask.ModListResult?.NexusMods != null)
            {
                var allMods = wizardTask.ModListResult.NexusMods.OrderBy(m => m.Name).ToList();
                var totalMods = allMods.Count;
                var totalPages = (int)Math.Ceiling((double)totalMods / _wizardPageSize);
                var currentPage = _wizardCurrentPage;
                if (currentPage < 1) currentPage = 1;
                if (currentPage > totalPages && totalPages > 0) currentPage = totalPages;

                var startIndex = (currentPage - 1) * _wizardPageSize;
                var endIndex = Math.Min(startIndex + _wizardPageSize, totalMods);

                for (int i = startIndex; i < endIndex; i++)
                {
                    var mod = allMods[i];
                    var statusText = mod.Status switch
                    {
                        CollectionModDownloadStatus.Pending => "等待中",
                        CollectionModDownloadStatus.BrowserOpened => "等待下载",
                        CollectionModDownloadStatus.Downloading => "下载中...",
                        CollectionModDownloadStatus.Completed => "已完成",
                        CollectionModDownloadStatus.Failed => "失败",
                        CollectionModDownloadStatus.Skipped => "已跳过",
                        _ => "未知"
                    };

                    items.Add(new ModListItemViewModel
                    {
                        Index = i + 1,
                        Name = mod.Name,
                        FileSize = FormatFileSize(mod.FileSize),
                        Phase = mod.Phase,
                        StatusText = statusText,
                        StatusColor = "#999999",
                        IsCurrent = wizardTask.CurrentMod != null && wizardTask.CurrentMod.Name == mod.Name
                    });
                }
            }
            // 批量更新任务
            else if (task is ModBatchUpdateTask batchUpdateTask && batchUpdateTask.ModList != null)
            {
                var allMods = batchUpdateTask.ModList.OrderBy(m => m.Name).ToList();
                var totalMods = allMods.Count;
                var totalPages = (int)Math.Ceiling((double)totalMods / _wizardPageSize);
                var currentPage = _wizardCurrentPage;
                if (currentPage < 1) currentPage = 1;
                if (currentPage > totalPages && totalPages > 0) currentPage = totalPages;

                var startIndex = (currentPage - 1) * _wizardPageSize;
                var endIndex = Math.Min(startIndex + _wizardPageSize, totalMods);

                for (int i = startIndex; i < endIndex; i++)
                {
                    var mod = allMods[i];
                    var (statusText, statusColor) = mod.Status switch
                    {
                        ModBatchUpdateStatus.Pending => ("等待中", "#999999"),
                        ModBatchUpdateStatus.Downloading => ("下载中...", "#2196F3"),
                        ModBatchUpdateStatus.WaitingBrowser => ("等待下载", "#FF9800"),
                        ModBatchUpdateStatus.Installing => ("安装中...", "#9C27B0"),
                        ModBatchUpdateStatus.Success => ("已完成", "#4CAF50"),
                        ModBatchUpdateStatus.Failed => ("失败", "#F44336"),
                        ModBatchUpdateStatus.Skipped => ("已跳过", "#9E9E9E"),
                        _ => ("未知", "#999999")
                    };

                    items.Add(new ModListItemViewModel
                    {
                        Index = i + 1,
                        Name = mod.Name,
                        CurrentVersion = mod.CurrentVersion ?? "未知",
                        NewVersion = mod.NewVersion ?? "未知",
                        Platform = mod.Platform ?? "未知",
                        StatusText = statusText,
                        StatusColor = statusColor,
                        IsCurrent = batchUpdateTask.CurrentMod != null && batchUpdateTask.CurrentMod == mod
                    });
                }
            }
            // Curseforge 整合包任务（本地）
            else if (task is LocalCurseforgeModpackInstallTask curseforgeTask && curseforgeTask.ModList != null)
            {
                var allMods = curseforgeTask.ModList.OrderBy(m => m.Name).ToList();
                var totalMods = allMods.Count;
                var totalPages = (int)Math.Ceiling((double)totalMods / _wizardPageSize);
                var currentPage = _wizardCurrentPage;
                if (currentPage < 1) currentPage = 1;
                if (currentPage > totalPages && totalPages > 0) currentPage = totalPages;

                var startIndex = (currentPage - 1) * _wizardPageSize;
                var endIndex = Math.Min(startIndex + _wizardPageSize, totalMods);

                for (int i = startIndex; i < endIndex; i++)
                {
                    var mod = allMods[i];
                    var (statusText, statusColor) = mod.Status switch
                    {
                        CurseforgeModDownloadStatus.Pending => ("等待中", "#999999"),
                        CurseforgeModDownloadStatus.Downloading => ("下载中...", "#2196F3"),
                        CurseforgeModDownloadStatus.Completed => ("已完成", "#4CAF50"),
                        CurseforgeModDownloadStatus.Failed => ("失败", "#F44336"),
                        CurseforgeModDownloadStatus.Skipped => ("已跳过", "#9E9E9E"),
                        _ => ("未知", "#999999")
                    };

                    items.Add(new ModListItemViewModel
                    {
                        Index = i + 1,
                        Name = mod.Name,
                        FileSize = "",
                        Phase = 0,
                        StatusText = statusText,
                        StatusColor = statusColor,
                        IsCurrent = curseforgeTask.CurrentMod != null && curseforgeTask.CurrentMod == mod
                    });
                }
            }
            // Curseforge 整合包任务（在线下载）
            else if (task is CurseforgeModpackDownloadTask cfDownloadTask && cfDownloadTask.ModList != null)
            {
                var allMods = cfDownloadTask.ModList.OrderBy(m => m.Name).ToList();
                var totalMods = allMods.Count;
                var totalPages = (int)Math.Ceiling((double)totalMods / _wizardPageSize);
                var currentPage = _wizardCurrentPage;
                if (currentPage < 1) currentPage = 1;
                if (currentPage > totalPages && totalPages > 0) currentPage = totalPages;

                var startIndex = (currentPage - 1) * _wizardPageSize;
                var endIndex = Math.Min(startIndex + _wizardPageSize, totalMods);

                for (int i = startIndex; i < endIndex; i++)
                {
                    var mod = allMods[i];
                    var (statusText, statusColor) = mod.Status switch
                    {
                        CurseforgeModDownloadStatus.Pending => ("等待中", "#999999"),
                        CurseforgeModDownloadStatus.Downloading => ("下载中...", "#2196F3"),
                        CurseforgeModDownloadStatus.Completed => ("已完成", "#4CAF50"),
                        CurseforgeModDownloadStatus.Failed => ("失败", "#F44336"),
                        CurseforgeModDownloadStatus.Skipped => ("已跳过", "#9E9E9E"),
                        _ => ("未知", "#999999")
                    };

                    items.Add(new ModListItemViewModel
                    {
                        Index = i + 1,
                        Name = mod.Name,
                        FileSize = "",
                        Phase = 0,
                        StatusText = statusText,
                        StatusColor = statusColor,
                        IsCurrent = cfDownloadTask.CurrentMod != null && cfDownloadTask.CurrentMod == mod
                    });
                }
            }
            // SVL 整合包导入任务
            else if (task is SvlModpackInstallTask svlModpackTask && svlModpackTask.ModList != null)
            {
                var allMods = svlModpackTask.ModList.OrderBy(m => m.Name).ToList();
                var totalMods = allMods.Count;
                var totalPages = (int)Math.Ceiling((double)totalMods / _wizardPageSize);
                var currentPage = _wizardCurrentPage;
                if (currentPage < 1) currentPage = 1;
                if (currentPage > totalPages && totalPages > 0) currentPage = totalPages;

                var startIndex = (currentPage - 1) * _wizardPageSize;
                var endIndex = Math.Min(startIndex + _wizardPageSize, totalMods);

                for (int i = startIndex; i < endIndex; i++)
                {
                    var mod = allMods[i];
                    var (statusText, statusColor) = mod.Status switch
                    {
                        SvlModpackModStatus.Pending when mod.IsBundled => ("已打包", "#00BCD4"),
                        SvlModpackModStatus.Pending => ("等待中", "#999999"),
                        SvlModpackModStatus.Downloading => ("下载中...", "#2196F3"),
                        SvlModpackModStatus.Extracting => ("解压中...", "#9C27B0"),
                        SvlModpackModStatus.Completed => ("已完成", "#4CAF50"),
                        SvlModpackModStatus.Failed => ("失败", "#F44336"),
                        SvlModpackModStatus.Skipped => ("已跳过", "#9E9E9E"),
                        _ => ("未知", "#999999")
                    };

                    items.Add(new ModListItemViewModel
                    {
                        Index = i + 1,
                        Name = mod.Name,
                        FileSize = "",
                        Phase = 0,
                        StatusText = statusText,
                        StatusColor = statusColor,
                        IsCurrent = svlModpackTask.CurrentModItem != null && svlModpackTask.CurrentModItem == mod
                    });
                }
            }

            return items;
        }
    }

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    public string StatusIcon => IsFailed ? "⚠" : (IsCancelled ? "ⓘ" : "⏳");

    public string StatusTitle
    {
        get
        {
            if (IsFailed)
                return "失败";
            if (IsCancelled)
                return "已取消";

            var status = StatusMessage ?? string.Empty;
            if (status.IndexOf("下载", StringComparison.OrdinalIgnoreCase) >= 0)
                return "下载中";
            if (status.IndexOf("安装", StringComparison.OrdinalIgnoreCase) >= 0)
                return "安装中";

            return "下载中";
        }
    }

    public Brush StatusColor => IsFailed
        ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF6B6B"))
        : (IsCancelled
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F57C00"))
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4CAF50")));

    public TaskStatusViewModel(MainWindowViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;

        // 创建定时器，每 500ms 刷新一次进度
        _refreshTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _refreshTimer.Tick += RefreshProgress;
        _refreshTimer.Start();
    }

    /// <summary>
    /// 定时刷新进度信息
    /// </summary>
    private void RefreshProgress(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_trackedTaskId))
            return;

        // 查找当前跟踪的任务
        var task = DownloadManager.Instance.GetAllTasks()
            .FirstOrDefault(t => string.Equals(t.Id, _trackedTaskId, StringComparison.Ordinal));

        if (task == null)
            return;

        // 更新进度信息
        Progress = task.Progress;

        // 解析 StatusMessage（支持两行格式）
        var lines = task.StatusMessage.Split('\n');
        if (lines.Length > 0 && !string.IsNullOrWhiteSpace(lines[0]))
            StatusMessage = lines[0];

        // 如果是 Wizard 任务或批量更新任务，使用自定义的 ProgressDetail 格式
        if (task is NexusCollectionWizardTask wizardTask)
        {
            var totalMods = wizardTask.ModListResult?.NexusMods.Count ?? 0;
            var completedMods = wizardTask.ModListResult?.NexusMods.Count(m =>
                m.Status == CollectionModDownloadStatus.Completed ||
                m.Status == CollectionModDownloadStatus.Skipped) ?? 0;

            // 基础进度：XX.XX% [3/93]
            var baseProgress = $"{task.Progress:F2}% [{completedMods}/{totalMods}]";

            // 如果正在下载，添加文件下载进度
            if (task.Status == DownloadTaskStatus.Downloading)
            {
                var fileProgress = WizardCurrentFileProgress;
                if (!string.IsNullOrEmpty(fileProgress))
                {
                    ProgressDetail = $"{baseProgress}\n下载中 {fileProgress}";
                }
                else
                {
                    ProgressDetail = baseProgress;
                }
            }
            else
            {
                ProgressDetail = baseProgress;
            }

            // 刷新 Wizard 相关属性
            OnPropertyChanged(nameof(IsCollectionWizardTask));
            OnPropertyChanged(nameof(CurrentWizardModName));
            OnPropertyChanged(nameof(IsWizardWaitingConfirmation));
            OnPropertyChanged(nameof(WizardModListItems));
            OnPropertyChanged(nameof(WizardStatusDisplayText));
            OnPropertyChanged(nameof(WizardProgressText));
            OnPropertyChanged(nameof(WizardProgressPercent));
            OnPropertyChanged(nameof(WizardHintText));
            OnPropertyChanged(nameof(WizardCurrentFileProgress));
            OnPropertyChanged(nameof(ShowCurrentModCard));
            OnPropertyChanged(nameof(ShowModListCard));
            OnPropertyChanged(nameof(WizardTotalPages));
            OnPropertyChanged(nameof(WizardPageInfo));
            OnPropertyChanged(nameof(WizardCurrentPage));
            OnPropertyChanged(nameof(CanGoToWizardPreviousPage));
            OnPropertyChanged(nameof(CanGoToWizardNextPage));
            OnPropertyChanged(nameof(WizardModListScrollViewerHeight));
            OnPropertyChanged(nameof(ModListItems));
        }
        else if (task is ModBatchUpdateTask batchUpdateTask)
        {
            var totalMods = batchUpdateTask.ModList.Count;
            var completedMods = batchUpdateTask.ModList.Count(m =>
                m.Status == ModBatchUpdateStatus.Success);

            // 基础进度：XX.XX% [3/10]
            var baseProgress = $"{task.Progress:F2}% [{completedMods}/{totalMods}]";
            ProgressDetail = baseProgress;

            // 刷新批量更新相关属性
            OnPropertyChanged(nameof(IsBatchUpdateTask));
            OnPropertyChanged(nameof(IsShowModListTask));
            OnPropertyChanged(nameof(CurrentBatchUpdateModName));
            OnPropertyChanged(nameof(IsBatchUpdateWaitingConfirmation));
            OnPropertyChanged(nameof(ShowBatchUpdateBrowserButton));
            OnPropertyChanged(nameof(BatchUpdateModListItems));
            OnPropertyChanged(nameof(BatchUpdateProgressText));
            OnPropertyChanged(nameof(BatchUpdateHintText));
            OnPropertyChanged(nameof(BatchUpdateCurrentFileProgress));
            OnPropertyChanged(nameof(ShowBatchUpdateCurrentModCard));
            OnPropertyChanged(nameof(ShowBatchUpdateModListCard));
            OnPropertyChanged(nameof(WizardTotalPages));
            OnPropertyChanged(nameof(WizardPageInfo));
            OnPropertyChanged(nameof(WizardCurrentPage));
            OnPropertyChanged(nameof(CanGoToWizardPreviousPage));
            OnPropertyChanged(nameof(CanGoToWizardNextPage));
            OnPropertyChanged(nameof(ModListItems));
        }
        else if (task is LocalCurseforgeModpackInstallTask curseforgeTask)
        {
            // Curseforge 整合包任务（本地）
            var totalMods = curseforgeTask.ModList.Count;
            var completedMods = curseforgeTask.ModList.Count(m =>
                m.Status == CurseforgeModDownloadStatus.Completed);

            // 基础进度：XX.XX% [3/10]
            var baseProgress = $"{task.Progress:F2}% [{completedMods}/{totalMods}]";

            // 添加文件下载进度
            var fileProgress = task.FileDownloadProgress;
            var bytesRead = task.FileDownloadBytes;
            var totalBytes = task.FileDownloadTotalBytes;

            if (totalBytes > 0 && fileProgress > 0)
            {
                var currentMB = bytesRead / 1024.0 / 1024.0;
                var totalMB = totalBytes / 1024.0 / 1024.0;
                ProgressDetail = $"{baseProgress} | {fileProgress:F2}% {currentMB:F2}MB/{totalMB:F2}MB";
            }
            else
            {
                ProgressDetail = baseProgress;
            }

            // 刷新 Curseforge 整合包相关属性
            OnPropertyChanged(nameof(IsCurseforgeModpackTask));
            OnPropertyChanged(nameof(IsShowModListTask));
            OnPropertyChanged(nameof(WizardStatusDisplayText));
            OnPropertyChanged(nameof(WizardProgressText));
            OnPropertyChanged(nameof(WizardProgressPercent));
            OnPropertyChanged(nameof(WizardCurrentFileProgress));
            OnPropertyChanged(nameof(ShowCurrentModCard));
            OnPropertyChanged(nameof(ShowModListCard));
            OnPropertyChanged(nameof(WizardTotalPages));
            OnPropertyChanged(nameof(WizardPageInfo));
            OnPropertyChanged(nameof(WizardCurrentPage));
            OnPropertyChanged(nameof(CanGoToWizardPreviousPage));
            OnPropertyChanged(nameof(CanGoToWizardNextPage));
            OnPropertyChanged(nameof(WizardModListScrollViewerHeight));
            OnPropertyChanged(nameof(ModListItems));
        }
        else if (task is CurseforgeModpackDownloadTask cfDownloadTask)
        {
            // Curseforge 整合包任务（在线下载）
            var totalMods = cfDownloadTask.ModList.Count;
            var completedMods = cfDownloadTask.ModList.Count(m =>
                m.Status == CurseforgeModDownloadStatus.Completed);

            // 基础进度：XX.XX% [3/10]
            var baseProgress = $"{task.Progress:F2}% [{completedMods}/{totalMods}]";

            // 添加文件下载进度
            var fileProgress = task.FileDownloadProgress;
            var bytesRead = task.FileDownloadBytes;
            var totalBytes = task.FileDownloadTotalBytes;

            if (totalBytes > 0 && fileProgress > 0)
            {
                var currentMB = bytesRead / 1024.0 / 1024.0;
                var totalMB = totalBytes / 1024.0 / 1024.0;
                ProgressDetail = $"{baseProgress} | {fileProgress:F2}% {currentMB:F2}MB/{totalMB:F2}MB";
            }
            else
            {
                ProgressDetail = baseProgress;
            }

            // 刷新 Curseforge 整合包（在线下载）相关属性
            OnPropertyChanged(nameof(IsCurseforgeModpackTask));
            OnPropertyChanged(nameof(IsShowModListTask));
            OnPropertyChanged(nameof(WizardStatusDisplayText));
            OnPropertyChanged(nameof(WizardProgressText));
            OnPropertyChanged(nameof(WizardProgressPercent));
            OnPropertyChanged(nameof(WizardCurrentFileProgress));
            OnPropertyChanged(nameof(ShowCurrentModCard));
            OnPropertyChanged(nameof(ShowModListCard));
            OnPropertyChanged(nameof(WizardTotalPages));
            OnPropertyChanged(nameof(WizardPageInfo));
            OnPropertyChanged(nameof(WizardCurrentPage));
            OnPropertyChanged(nameof(CanGoToWizardPreviousPage));
            OnPropertyChanged(nameof(CanGoToWizardNextPage));
            OnPropertyChanged(nameof(WizardModListScrollViewerHeight));
            OnPropertyChanged(nameof(ModListItems));
        }
        else if (task is SvlModpackInstallTask svlModpackTask)
        {
            // SVL 整合包导入任务
            var totalMods = svlModpackTask.ModList.Count;
            var completedMods = svlModpackTask.ModList.Count(m =>
                m.Status == SvlModpackModStatus.Completed);

            // 基础进度：XX.XX% [3/10]
            var baseProgress = $"{task.Progress:F2}% [{completedMods}/{totalMods}]";

            // 添加文件下载进度
            var fileProgress = task.FileDownloadProgress;
            var bytesRead = task.FileDownloadBytes;
            var totalBytes = task.FileDownloadTotalBytes;

            if (totalBytes > 0 && fileProgress > 0)
            {
                var currentMB = bytesRead / 1024.0 / 1024.0;
                var totalMB = totalBytes / 1024.0 / 1024.0;
                ProgressDetail = $"{baseProgress} | {fileProgress:F2}% {currentMB:F2}MB/{totalMB:F2}MB";
            }
            else
            {
                ProgressDetail = baseProgress;
            }

            // 刷新 SVL 整合包相关属性
            OnPropertyChanged(nameof(IsSvlModpackTask));
            OnPropertyChanged(nameof(IsShowModListTask));
            OnPropertyChanged(nameof(WizardStatusDisplayText));
            OnPropertyChanged(nameof(WizardProgressText));
            OnPropertyChanged(nameof(WizardProgressPercent));
            OnPropertyChanged(nameof(WizardCurrentFileProgress));
            OnPropertyChanged(nameof(ShowCurrentModCard));
            OnPropertyChanged(nameof(ShowModListCard));
            OnPropertyChanged(nameof(CurrentWizardModName));
            OnPropertyChanged(nameof(IsWizardWaitingConfirmation));
            OnPropertyChanged(nameof(WizardHintText));
            OnPropertyChanged(nameof(WizardTotalPages));
            OnPropertyChanged(nameof(WizardPageInfo));
            OnPropertyChanged(nameof(WizardCurrentPage));
            OnPropertyChanged(nameof(CanGoToWizardPreviousPage));
            OnPropertyChanged(nameof(CanGoToWizardNextPage));
            OnPropertyChanged(nameof(WizardModListScrollViewerHeight));
            OnPropertyChanged(nameof(ModListItems));
        }
        else if (task is NexusModsBrowserDownloadTask browserDownloadTask)
        {
            // NexusMods 浏览器下载任务
            var fileProgress = task.FileDownloadProgress;
            var bytesRead = task.FileDownloadBytes;
            var totalBytes = task.FileDownloadTotalBytes;

            if (totalBytes > 0 && fileProgress > 0)
            {
                var currentMB = bytesRead / 1024.0 / 1024.0;
                var totalMB = totalBytes / 1024.0 / 1024.0;
                ProgressDetail = $"{fileProgress:F2}% {currentMB:F2}MB/{totalMB:F2}MB";
            }
            else
            {
                ProgressDetail = $"{task.Progress:F0}%";
            }

            // 刷新浏览器下载相关属性
            OnPropertyChanged(nameof(IsBrowserDownloadTask));
            OnPropertyChanged(nameof(IsShowModListTask));
            OnPropertyChanged(nameof(CurrentWizardModName));
            OnPropertyChanged(nameof(IsWizardWaitingConfirmation));
            OnPropertyChanged(nameof(WizardHintText));
            OnPropertyChanged(nameof(ShowCurrentModCard));
        }
        else
        {
            // 非 Wizard 任务：检查是否有文件下载进度
            var fileProgress = task.FileDownloadProgress;
            var bytesRead = task.FileDownloadBytes;
            var totalBytes = task.FileDownloadTotalBytes;

            if (totalBytes > 0 && fileProgress > 0)
            {
                // 有文件下载进度，显示格式化的进度
                var currentMB = bytesRead / 1024.0 / 1024.0;
                var totalMB = totalBytes / 1024.0 / 1024.0;
                ProgressDetail = $"{fileProgress:F2}% {currentMB:F2}MB/{totalMB:F2}MB";
            }
            else if (lines.Length > 1 && !string.IsNullOrWhiteSpace(lines[1]))
            {
                ProgressDetail = lines[1];
            }
            else if (!string.IsNullOrWhiteSpace(task.StatusMessage))
            {
                ProgressDetail = $"{task.Progress:F0}%";
            }
        }

        // 如果任务失败或取消，停止定时器
        if (task.Status == DownloadTaskStatus.Failed)
        {
            SetFailureInfo(task.Name, task.StatusMessage, "请查看日志了解详细信息", null, task.Id);
            _refreshTimer.Stop();
        }
        else if (task.Status == DownloadTaskStatus.Cancelled)
        {
            SetCancelledInfo(task.Name, task.Id);
            _refreshTimer.Stop();
        }
        else if (task.Status == DownloadTaskStatus.Completed)
        {
            _refreshTimer.Stop();
        }
    }

    partial void OnIsFailedChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusIcon));
        OnPropertyChanged(nameof(StatusTitle));
        OnPropertyChanged(nameof(StatusColor));
    }

    partial void OnIsCancelledChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusIcon));
        OnPropertyChanged(nameof(StatusTitle));
        OnPropertyChanged(nameof(StatusColor));
    }

    partial void OnStatusMessageChanged(string value)
    {
        OnPropertyChanged(nameof(StatusTitle));
    }

    /// <summary>
    /// 刷新进行中的任务列表
    /// </summary>
    private void RefreshActiveTasks()
    {
        var allTasks = DownloadManager.Instance.GetAllTasks()
            .Where(t => t.Status == DownloadTaskStatus.Pending ||
                       t.Status == DownloadTaskStatus.Downloading ||
                       t.Status == DownloadTaskStatus.Installing ||
                       t.Status == DownloadTaskStatus.WaitingConfirmation)  // 包含等待用户确认的任务
            .Select(t => new DownloadTaskViewModel(t))
            .ToList();

        // 清除旧任务的选中状态
        foreach (var task in ActiveTasks)
        {
            task.IsSelected = false;
        }

        ActiveTasks.Clear();
        foreach (var task in allTasks)
        {
            ActiveTasks.Add(task);
        }

        // 如果当前选中的任务不在列表中，选择第一个任务
        if (SelectedTask == null || !allTasks.Any(t => string.Equals(t.Id, SelectedTask.Id, StringComparison.Ordinal)))
        {
            if (ActiveTasks.Count > 0)
            {
                SelectedTask = ActiveTasks[0];
                SelectedTask.IsSelected = true;
            }
        }
        else
        {
            // 更新选中状态
            var matchingTask = ActiveTasks.FirstOrDefault(t => string.Equals(t.Id, SelectedTask.Id, StringComparison.Ordinal));
            if (matchingTask != null)
            {
                matchingTask.IsSelected = true;
                SelectedTask = matchingTask;
            }
        }
    }

    /// <summary>
    /// 切换选中的任务
    /// </summary>
    [RelayCommand]
    private void SelectTask(DownloadTaskViewModel? task)
    {
        if (task == null)
            return;

        // 清除所有任务的选中状态
        foreach (var t in ActiveTasks)
        {
            t.IsSelected = false;
        }

        // 设置新任务为选中
        task.IsSelected = true;
        SelectedTask = task;

        var actualTask = DownloadManager.Instance.GetAllTasks()
            .FirstOrDefault(t => string.Equals(t.Id, task.Id, StringComparison.Ordinal));

        if (actualTask != null)
        {
            // 使用 forceUpdate=true 强制更新到新任务
            SetProgressInfo(actualTask, forceUpdate: true);

            // 重新启动定时器以跟踪新任务
            _refreshTimer.Stop();
            _refreshTimer.Start();

            Log.Info($"[TaskStatusViewModel] 切换到任务: {actualTask.Name}");
        }
    }

    /// <summary>
    /// 设置失败信息
    /// </summary>
    public void SetFailureInfo(string taskName, string errorMessage, string detailMessage, Exception? exception, string taskId = "")
    {
        if (!string.IsNullOrWhiteSpace(taskId))
            _trackedTaskId = taskId;

        TaskName = taskName;
        StatusMessage = string.IsNullOrWhiteSpace(errorMessage) ? "任务执行失败" : errorMessage;
        ErrorMessage = errorMessage;
        DetailMessage = detailMessage;
        IsFailed = true;
        IsCancelled = false;
        ProgressDetail = "";

        if (exception?.InnerException != null)
        {
            HasInnerError = true;
            InnerErrorMessage = exception.InnerException.Message;
        }
        else
        {
            HasInnerError = false;
            InnerErrorMessage = "";
        }

        // 失败状态：显示"关闭页面"按钮
        ActionButtonText = "关闭页面";
        ShowActionButton = true;
        AdviceTitle = "建议操作";
        SetSuggestedActions(
            "检查网络连接是否正常",
            "尝试重新下载或切换到其他来源",
            "查看日志文件获取更多信息",
            "检查磁盘空间是否充足"
        );
    }

    /// <summary>
    /// 设置进度信息
    /// </summary>
    public void SetProgressInfo(DownloadTask task, bool forceUpdate = false)
    {
        // 如果是强制更新或者任务 ID 匹配，则更新跟踪的任务 ID
        if (forceUpdate || string.IsNullOrWhiteSpace(_trackedTaskId) ||
            string.Equals(_trackedTaskId, task.Id, StringComparison.Ordinal))
        {
            _trackedTaskId = task.Id;
        }
        else
        {
            // 如果是不同的任务且非强制更新，只刷新任务列表，不更新其他信息
            RefreshActiveTasks();
            return;
        }

        // 刷新进行中的任务列表
        RefreshActiveTasks();

        // 更新选中的任务
        var taskViewModel = ActiveTasks.FirstOrDefault(t => string.Equals(t.Id, task.Id, StringComparison.Ordinal));
        if (taskViewModel != null)
        {
            SelectedTask = taskViewModel;
        }

        if (task.Status == DownloadTaskStatus.Failed)
        {
            SetFailureInfo(task.Name, task.StatusMessage, "请查看日志了解详细信息", null, task.Id);
            return;
        }

        if (task.Status == DownloadTaskStatus.Cancelled)
        {
            SetCancelledInfo(task.Name, task.Id);
            return;
        }

        TaskName = task.Name;
        Progress = task.Progress;
        IsFailed = false;
        IsCancelled = false;
        HasInnerError = false;

        // 解析 StatusMessage：格式为 "模组下载中\nXX.XX% XXMB/XXMB"
        // StatusMessage 只显示第一行（黑色文字）
        var lines = task.StatusMessage.Split('\n');
        StatusMessage = lines.Length > 0 ? lines[0] : task.StatusMessage;

        // ProgressDetail 显示第二行（橙色文字）
        // 如果是 Wizard 任务或批量更新任务，使用自定义格式
        if (task is NexusCollectionWizardTask wizardTask)
        {
            var totalMods = wizardTask.ModListResult?.NexusMods.Count ?? 0;
            var completedMods = wizardTask.ModListResult?.NexusMods.Count(m =>
                m.Status == CollectionModDownloadStatus.Completed ||
                m.Status == CollectionModDownloadStatus.Skipped) ?? 0;

            // 基础进度：XX.XX% [3/93]
            var baseProgress = $"{task.Progress:F2}% [{completedMods}/{totalMods}]";

            // 如果正在下载，添加文件下载进度
            if (task.Status == DownloadTaskStatus.Downloading)
            {
                var fileProgress = WizardCurrentFileProgress;
                if (!string.IsNullOrEmpty(fileProgress))
                {
                    ProgressDetail = $"{baseProgress}\n下载中 {fileProgress}";
                }
                else
                {
                    ProgressDetail = baseProgress;
                }
            }
            else
            {
                ProgressDetail = baseProgress;
            }

            // 刷新 Wizard 相关属性
            OnPropertyChanged(nameof(IsCollectionWizardTask));
            OnPropertyChanged(nameof(CurrentWizardModName));
            OnPropertyChanged(nameof(IsWizardWaitingConfirmation));
            OnPropertyChanged(nameof(WizardModListItems));
            OnPropertyChanged(nameof(WizardStatusDisplayText));
            OnPropertyChanged(nameof(WizardProgressText));
            OnPropertyChanged(nameof(WizardProgressPercent));
            OnPropertyChanged(nameof(WizardHintText));
            OnPropertyChanged(nameof(WizardCurrentFileProgress));
            OnPropertyChanged(nameof(ShowCurrentModCard));
            OnPropertyChanged(nameof(ShowModListCard));
            OnPropertyChanged(nameof(WizardTotalPages));
            OnPropertyChanged(nameof(WizardPageInfo));
            OnPropertyChanged(nameof(WizardCurrentPage));
            OnPropertyChanged(nameof(CanGoToWizardPreviousPage));
            OnPropertyChanged(nameof(CanGoToWizardNextPage));
            OnPropertyChanged(nameof(WizardModListScrollViewerHeight));
            OnPropertyChanged(nameof(ModListItems));
        }
        else if (task is ModBatchUpdateTask batchUpdateTask)
        {
            var totalMods = batchUpdateTask.ModList.Count;
            var completedMods = batchUpdateTask.ModList.Count(m =>
                m.Status == ModBatchUpdateStatus.Success);

            // 基础进度：XX.XX% [3/10]
            var baseProgress = $"{task.Progress:F2}% [{completedMods}/{totalMods}]";

            // 如果正在下载，添加文件下载进度
            if (task.Status == DownloadTaskStatus.Downloading)
            {
                var fileProgress = BatchUpdateCurrentFileProgress;
                if (!string.IsNullOrEmpty(fileProgress))
                {
                    ProgressDetail = $"{baseProgress}\n下载中 {fileProgress}";
                }
                else
                {
                    ProgressDetail = baseProgress;
                }
            }
            else
            {
                ProgressDetail = baseProgress;
            }

            // 刷新批量更新相关属性
            OnPropertyChanged(nameof(IsBatchUpdateTask));
            OnPropertyChanged(nameof(IsShowModListTask));
            OnPropertyChanged(nameof(CurrentBatchUpdateModName));
            OnPropertyChanged(nameof(IsBatchUpdateWaitingConfirmation));
            OnPropertyChanged(nameof(ShowBatchUpdateBrowserButton));
            OnPropertyChanged(nameof(BatchUpdateModListItems));
            OnPropertyChanged(nameof(BatchUpdateProgressText));
            OnPropertyChanged(nameof(BatchUpdateHintText));
            OnPropertyChanged(nameof(BatchUpdateCurrentFileProgress));
            OnPropertyChanged(nameof(ShowBatchUpdateCurrentModCard));
            OnPropertyChanged(nameof(ShowBatchUpdateModListCard));
            OnPropertyChanged(nameof(WizardTotalPages));
            OnPropertyChanged(nameof(WizardPageInfo));
            OnPropertyChanged(nameof(WizardCurrentPage));
            OnPropertyChanged(nameof(CanGoToWizardPreviousPage));
            OnPropertyChanged(nameof(CanGoToWizardNextPage));
            OnPropertyChanged(nameof(ModListItems));
        }
        else
        {
            // 非 Wizard 任务：检查是否有文件下载进度
            var fileProgress = task.FileDownloadProgress;
            var bytesRead = task.FileDownloadBytes;
            var totalBytes = task.FileDownloadTotalBytes;

            if (task.Status == DownloadTaskStatus.Downloading && totalBytes > 0 && fileProgress > 0)
            {
                // 有文件下载进度，显示格式化的进度
                var currentMB = bytesRead / 1024.0 / 1024.0;
                var totalMB = totalBytes / 1024.0 / 1024.0;
                ProgressDetail = $"{task.Progress:F2}%\n下载中 {fileProgress:F2}% {currentMB:F2}MB/{totalMB:F2}MB";
            }
            else if (lines.Length > 1 && !string.IsNullOrWhiteSpace(lines[1]))
            {
                ProgressDetail = lines[1];
            }
            else
            {
                ProgressDetail = $"{task.Progress:F0}%";
            }
        }

        // 进行中状态：显示"取消任务"按钮
        ActionButtonText = "取消任务";
        ShowActionButton = true;

        var isInstalling = (StatusMessage ?? string.Empty).IndexOf("安装", StringComparison.OrdinalIgnoreCase) >= 0;
        AdviceTitle = "提示";
        if (isInstalling)
        {
            SetSuggestedActions(
                "正在安装，请勿关闭应用",
                "安装过程中请勿修改目标目录文件",
                "如需中止，请点击「取消任务」"
            );
        }
        else
        {
            SetSuggestedActions(
                "正在下载，请保持网络连接稳定",
                "下载过程中请勿关闭应用",
                "如需中止，请点击「取消任务」"
            );
        }
    }

    /// <summary>
    /// 设置取消信息
    /// </summary>
    public void SetCancelledInfo(string taskName, string taskId = "")
    {
        if (!string.IsNullOrWhiteSpace(taskId))
            _trackedTaskId = taskId;

        TaskName = taskName;
        StatusMessage = "已取消";
        IsFailed = false;
        IsCancelled = true;
        HasInnerError = false;

        // 取消状态：显示"关闭页面"按钮
        ActionButtonText = "关闭页面";
        ShowActionButton = true;
        AdviceTitle = "提示";
        SetSuggestedActions(
            "任务已取消，未继续下载或安装",
            "如需继续，请返回下载页重新发起任务",
            "可在任务状态中检查是否还有其他进行中的任务"
        );
    }

    private void SetSuggestedActions(params string[] actions)
    {
        SuggestedActions.Clear();
        foreach (var action in actions)
        {
            if (!string.IsNullOrWhiteSpace(action))
                SuggestedActions.Add(action);
        }
    }

    /// <summary>
    /// 取消任务
    /// </summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task CancelTask()
    {
        try
        {
            DownloadTask currentTask = null;

            if (!string.IsNullOrWhiteSpace(_trackedTaskId))
            {
                currentTask = DownloadManager.Instance.GetAllTasks()
                    .FirstOrDefault(t => string.Equals(t.Id, _trackedTaskId, StringComparison.Ordinal));
            }

            if (currentTask == null)
            {
                currentTask = DownloadManager.Instance.GetAllTasks()
                    .FirstOrDefault(t => t.Status == DownloadTaskStatus.Downloading ||
                                       t.Status == DownloadTaskStatus.Installing ||
                                       t.Status == DownloadTaskStatus.Pending);
            }

            if (currentTask != null)
            {
                Log.Info($"[TaskStatusViewModel] 取消任务: {currentTask.Name}");
                DownloadManager.Instance.CancelTask(currentTask.Id);

                // 刷新任务列表
                RefreshActiveTasks();

                // 检查是否还有其他活跃任务
                if (ActiveTasks.Count > 0)
                {
                    // 还有其他任务，切换到第一个活跃任务并留在任务状态页面
                    var firstTask = ActiveTasks.FirstOrDefault();
                    if (firstTask != null)
                    {
                        SelectTask(firstTask);
                        Log.Info($"[TaskStatusViewModel] 切换到任务: {firstTask.Name}");
                    }
                }
                else
                {
                    // 没有其他活跃任务，返回启动页面
                    _mainViewModel.CurrentPage = PageType.Launch;
                }
            }
            else
            {
                // 没有活跃任务，返回启动页面
                _mainViewModel.CurrentPage = PageType.Launch;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[TaskStatusViewModel] 取消任务失败");
        }
    }

    /// <summary>
    /// 操作按钮命令（根据状态执行不同操作）
    /// </summary>
    [RelayCommand]
    private void ExecuteAction()
    {
        if (IsFailed || IsCancelled)
        {
            // 失败或取消状态：关闭页面
            GoBack();
        }
        else
        {
            // 进行中状态：取消任务
            CancelTask();
        }
    }

    [RelayCommand]
    private void GoBack()
    {
        _mainViewModel.CurrentPage = PageType.Launch;
    }

    /// <summary>
    /// 是否可以切换到上一个任务
    /// </summary>
    public bool CanGoToPreviousTask => SelectedTask != null && ActiveTasks.Count > 1 &&
        ActiveTasks.IndexOf(SelectedTask) > 0;

    /// <summary>
    /// 是否可以切换到下一个任务
    /// </summary>
    public bool CanGoToNextTask => SelectedTask != null && ActiveTasks.Count > 1 &&
        ActiveTasks.IndexOf(SelectedTask) < ActiveTasks.Count - 1;

    /// <summary>
    /// 切换到上一个任务
    /// </summary>
    [RelayCommand]
    private void GoToPreviousTask()
    {
        if (SelectedTask == null || !CanGoToPreviousTask)
            return;

        var currentIndex = ActiveTasks.IndexOf(SelectedTask);
        var previousTask = ActiveTasks[currentIndex - 1];
        SelectTask(previousTask);
    }

    /// <summary>
    /// 切换到下一个任务
    /// </summary>
    [RelayCommand]
    private void GoToNextTask()
    {
        if (SelectedTask == null || !CanGoToNextTask)
            return;

        var currentIndex = ActiveTasks.IndexOf(SelectedTask);
        var nextTask = ActiveTasks[currentIndex + 1];
        SelectTask(nextTask);
    }

    /// <summary>
    /// 为 Wizard Mod / 浏览器下载任务 打开浏览器
    /// </summary>
    [RelayCommand]
    private void OpenBrowserForWizardMod()
    {
        var task = DownloadManager.Instance.GetAllTasks()
            .FirstOrDefault(t => string.Equals(t.Id, _trackedTaskId, StringComparison.Ordinal));

        // NexusMods 浏览器下载任务 — 浏览器已在 ExecuteAsync 中自动打开，
        // 这里只是给用户一个"重新打开浏览器"的入口
        if (task is NexusModsBrowserDownloadTask browserTask)
        {
            try
            {
                var url = $"https://www.nexusmods.com/stardewvalley/mods/{browserTask.PendingModId}?tab=files&file_id={browserTask.PendingFileId}&nmm=1";
                Log.Info($"[TaskStatusViewModel] 重新打开浏览器下载页: {url}");
                ProcessEx.OpenUrl(url);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[TaskStatusViewModel] 打开浏览器失败");
            }
            return;
        }

        // Collection Wizard 任务
        if (task is NexusCollectionWizardTask wizardTask)
        {
            if (wizardTask.CurrentMod == null) return;
            try
            {
                var downloadUrl = wizardTask.CurrentMod.FilesPageUrl;
                Log.Info($"[TaskStatusViewModel] 打开浏览器: {downloadUrl}");
                ProcessEx.OpenUrl(downloadUrl);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[TaskStatusViewModel] 打开浏览器失败");
            }
        }

        // SVL 整合包导入任务 — 使用 PendingNexusModId/FileId 打开浏览器
        if (task is SvlModpackInstallTask svlTask)
        {
            if (svlTask.PendingNexusModId <= 0) return;
            try
            {
                var url = $"https://www.nexusmods.com/stardewvalley/mods/{svlTask.PendingNexusModId}?tab=files&file_id={svlTask.PendingNexusFileId}&nmm=1";
                Log.Info($"[TaskStatusViewModel] SVL整合包: 打开浏览器下载页: {url}");
                ProcessEx.OpenUrl(url);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[TaskStatusViewModel] 打开浏览器失败");
            }
        }
    }

    /// <summary>
    /// Wizard Mod 列表翻到上一页
    /// </summary>
    [RelayCommand]
    private void WizardPreviousPage()
    {
        if (CanGoToWizardPreviousPage)
        {
            WizardCurrentPage--;
        }
    }

    /// <summary>
    /// Wizard Mod 列表翻到下一页
    /// </summary>
    [RelayCommand]
    private void WizardNextPage()
    {
        if (CanGoToWizardNextPage)
        {
            WizardCurrentPage++;
        }
    }

    /// <summary>
    /// 跳过当前 Wizard Mod
    /// </summary>
    [RelayCommand]
    private void SkipWizardMod()
    {
        var task = DownloadManager.Instance.GetAllTasks()
            .FirstOrDefault(t => string.Equals(t.Id, _trackedTaskId, StringComparison.Ordinal)) as NexusCollectionWizardTask;

        if (task?.CurrentMod == null || !task.CurrentMod.IsOptional)
            return;

        Log.Info($"[TaskStatusViewModel] 跳过可选 Mod: {task.CurrentMod.Name}");

        // 调用任务的 SkipCurrentMod 方法
        task.SkipCurrentMod();
    }

    /// <summary>
    /// 为批量更新 Mod 打开浏览器
    /// </summary>
    [RelayCommand]
    private void OpenBrowserForBatchUpdateMod()
    {
        var task = DownloadManager.Instance.GetAllTasks()
            .FirstOrDefault(t => string.Equals(t.Id, _trackedTaskId, StringComparison.Ordinal)) as ModBatchUpdateTask;

        var currentMod = task?.CurrentMod;
        if (currentMod == null)
            return;

        if (!string.Equals(currentMod.Platform, "NexusMods", StringComparison.OrdinalIgnoreCase))
        {
            Log.Info($"[TaskStatusViewModel] 当前批量更新项 {currentMod.Name} 为 {currentMod.Platform}，无需打开浏览器");
            return;
        }

        try
        {
            // 从 DownloadUrl 获取下载页面 URL
            var downloadPageUrl = currentMod.DownloadUrl;
            if (string.IsNullOrEmpty(downloadPageUrl))
            {
                Log.Warn($"[TaskStatusViewModel] 无法获取 {currentMod.Name} 的下载页面 URL");
                return;
            }

            Log.Info($"[TaskStatusViewModel] 打开浏览器: {downloadPageUrl}");
            ProcessEx.OpenUrl(downloadPageUrl);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[TaskStatusViewModel] 打开浏览器失败");
        }
    }

    /// <summary>
    /// 批量更新跳过当前模组
    /// </summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task BatchUpdateSkipModAsync()
    {
        var task = DownloadManager.Instance.GetAllTasks()
            .FirstOrDefault(t => string.Equals(t.Id, _trackedTaskId, StringComparison.Ordinal)) as ModBatchUpdateTask;

        if (task == null)
            return;

        Log.Info($"[TaskStatusViewModel] 用户跳过模组: {task.CurrentMod?.Name}");

        await task.SkipCurrentModAsync();
    }

    /// <summary>
    /// 当选中任务改变时，更新按钮状态
    /// </summary>
    partial void OnSelectedTaskChanged(DownloadTaskViewModel? value)
    {
        OnPropertyChanged(nameof(CanGoToPreviousTask));
        OnPropertyChanged(nameof(CanGoToNextTask));
        OnPropertyChanged(nameof(IsCollectionWizardTask));
        OnPropertyChanged(nameof(IsBatchUpdateTask));
        OnPropertyChanged(nameof(IsShowModListTask));
        OnPropertyChanged(nameof(CurrentWizardModName));
        OnPropertyChanged(nameof(CurrentBatchUpdateModName));
        OnPropertyChanged(nameof(IsWizardWaitingConfirmation));
        OnPropertyChanged(nameof(IsBatchUpdateWaitingConfirmation));
        OnPropertyChanged(nameof(ShowBatchUpdateBrowserButton));
        OnPropertyChanged(nameof(WizardModListItems));
        OnPropertyChanged(nameof(BatchUpdateModListItems));
        OnPropertyChanged(nameof(WizardStatusDisplayText));
        OnPropertyChanged(nameof(WizardProgressText));
        OnPropertyChanged(nameof(WizardProgressPercent));
        OnPropertyChanged(nameof(BatchUpdateProgressText));
        OnPropertyChanged(nameof(WizardHintText));
        OnPropertyChanged(nameof(BatchUpdateHintText));
        OnPropertyChanged(nameof(ShowCurrentModCard));
        OnPropertyChanged(nameof(ShowModListCard));
        OnPropertyChanged(nameof(ShowBatchUpdateCurrentModCard));
        OnPropertyChanged(nameof(ShowBatchUpdateModListCard));
    }

    /// <summary>
    /// 当任务列表改变时，更新按钮状态
    /// </summary>
    partial void OnActiveTasksChanged(ObservableCollection<DownloadTaskViewModel> value)
    {
        OnPropertyChanged(nameof(CanGoToPreviousTask));
        OnPropertyChanged(nameof(CanGoToNextTask));
        OnPropertyChanged(nameof(ShowTaskList));
    }

    /// <summary>
    /// Collection Wizard Mod 列表项 ViewModel
    /// </summary>
    public class WizardModListItemViewModel
    {
        private readonly CollectionModDownloadItem? _mod;
        private readonly CurseforgeModDownloadItem? _curseforgeMod;

        public WizardModListItemViewModel(CollectionModDownloadItem mod, int index)
        {
            _mod = mod;
            Index = index;
            Name = mod.Name;
            FileSize = FormatFileSize(mod.FileSize);
            Phase = mod.Phase;

            switch (mod.Status)
            {
                case CollectionModDownloadStatus.Pending:
                    StatusText = "等待中";
                    break;
                case CollectionModDownloadStatus.BrowserOpened:
                    StatusText = "等待下载";
                    break;
                case CollectionModDownloadStatus.Downloading:
                    StatusText = "下载中...";
                    break;
                case CollectionModDownloadStatus.Completed:
                    StatusText = "已完成";
                    break;
                case CollectionModDownloadStatus.Failed:
                    StatusText = "失败";
                    break;
                case CollectionModDownloadStatus.Skipped:
                    StatusText = "已跳过";
                    break;
                default:
                    StatusText = "未知";
                    break;
            }
        }

        /// <summary>
        /// Curseforge 整合包 Mod 列表项构造函数
        /// </summary>
        public WizardModListItemViewModel(CurseforgeModDownloadItem mod, int index)
        {
            _curseforgeMod = mod;
            Index = index;
            Name = mod.Name;
            FileSize = ""; // Curseforge 模组没有文件大小信息
            Phase = 0;

            switch (mod.Status)
            {
                case CurseforgeModDownloadStatus.Pending:
                    StatusText = "等待中";
                    break;
                case CurseforgeModDownloadStatus.Downloading:
                    StatusText = "下载中...";
                    break;
                case CurseforgeModDownloadStatus.Completed:
                    StatusText = "已完成";
                    break;
                case CurseforgeModDownloadStatus.Failed:
                    StatusText = "失败";
                    break;
                case CurseforgeModDownloadStatus.Skipped:
                    StatusText = "已跳过";
                    break;
                default:
                    StatusText = "未知";
                    break;
            }
        }

        public int Index { get; }
        public string Name { get; }
        public string FileSize { get; }
        public int Phase { get; }
        public string StatusText { get; }

        private static string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }
    }

    /// <summary>
    /// 批量更新 Mod 列表项 ViewModel
    /// </summary>
    public class BatchUpdateModListItemViewModel
    {
        private readonly ModBatchUpdateItem _mod;
        private readonly ModBatchUpdateItem? _currentMod;

        public BatchUpdateModListItemViewModel(ModBatchUpdateItem mod, int index, ModBatchUpdateItem? currentMod)
        {
            _mod = mod;
            _currentMod = currentMod;
            Index = index;
            Name = mod.Name;
            CurrentVersion = mod.CurrentVersion ?? "未知";
            NewVersion = mod.NewVersion ?? "未知";
            Platform = mod.Platform ?? "未知";
            IsCurrent = currentMod != null && currentMod == mod;

            switch (mod.Status)
            {
                case ModBatchUpdateStatus.Pending:
                    StatusText = "等待中";
                    StatusColor = "#999999";
                    break;
                case ModBatchUpdateStatus.Downloading:
                    StatusText = "下载中...";
                    StatusColor = "#2196F3";
                    break;
                case ModBatchUpdateStatus.WaitingBrowser:
                    StatusText = "等待下载";
                    StatusColor = "#FF9800";
                    break;
                case ModBatchUpdateStatus.Installing:
                    StatusText = "安装中...";
                    StatusColor = "#9C27B0";
                    break;
                case ModBatchUpdateStatus.Success:
                    StatusText = "已完成";
                    StatusColor = "#4CAF50";
                    break;
                case ModBatchUpdateStatus.Failed:
                    StatusText = "失败";
                    StatusColor = "#F44336";
                    break;
                case ModBatchUpdateStatus.Skipped:
                    StatusText = "已跳过";
                    StatusColor = "#999999";
                    break;
                default:
                    StatusText = "未知";
                    StatusColor = "#999999";
                    break;
            }
        }

        public int Index { get; }
        public string Name { get; }
        public string CurrentVersion { get; }
        public string NewVersion { get; }
        public string Platform { get; }
        public string StatusText { get; }
        public string StatusColor { get; }
        public bool IsCurrent { get; }
    }

    /// <summary>
    /// 统一的 Mod 列表项 ViewModel（用于 Collection Wizard 和批量更新）
    /// </summary>
    public class ModListItemViewModel
    {
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public string StatusText { get; set; } = "";
        public string StatusColor { get; set; } = "#999999";
        public bool IsCurrent { get; set; }

        // Collection Wizard 专用
        public int Phase { get; set; }
        public string FileSize { get; set; } = "";

        // 批量更新专用
        public string CurrentVersion { get; set; } = "";
        public string NewVersion { get; set; } = "";
        public string Platform { get; set; } = "";
    }
}
