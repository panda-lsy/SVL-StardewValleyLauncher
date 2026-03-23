using System;
using CommunityToolkit.Mvvm.ComponentModel;
using SVL.Core.Download;

namespace SVL.Desktop.ViewModels;

/// <summary>
/// 下载任务ViewModel（包装Core层的DownloadTask）
/// </summary>
public partial class DownloadTaskViewModel : ObservableObject
{
    private readonly DownloadTask _task;

    public DownloadTaskViewModel(DownloadTask task)
    {
        _task = task;

        // 监听任务更新（通过定时器刷新）
        System.Windows.Threading.DispatcherTimer timer = new()
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        timer.Tick += (s, e) =>
        {
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(Progress));
            OnPropertyChanged(nameof(FileDownloadProgress));
            OnPropertyChanged(nameof(StatusMessage));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(IsCompleted));
            OnPropertyChanged(nameof(IsFailed));
        };
        timer.Start();
    }

    public string Id => _task.Id;
    public string Name => _task.Name;
    public DownloadTaskType Type => _task.Type;
    public string TypeDisplayName => Type switch
    {
        DownloadTaskType.SMAPI => "SMAPI",
        DownloadTaskType.Mod => "Mod",
        DownloadTaskType.Modpack => "整合包",
        _ => "未知"
    };

    public DownloadTaskStatus Status => _task.Status;
    public double Progress => _task.Progress;
    /// <summary>
    /// 当前文件下载进度（0-100），仅表示正在下载的单个文件的进度
    /// </summary>
    public double FileDownloadProgress => _task.FileDownloadProgress;
    public string StatusMessage => _task.StatusMessage;
    public DateTime CreatedTime => _task.CreatedTime;
    public DateTime? CompletedTime => _task.CompletedTime;

    public bool IsActive => Status == DownloadTaskStatus.Pending ||
                            Status == DownloadTaskStatus.Downloading ||
                            Status == DownloadTaskStatus.Installing;

    public bool IsCompleted => Status == DownloadTaskStatus.Completed ||
                              Status == DownloadTaskStatus.Failed ||
                              Status == DownloadTaskStatus.Cancelled;

    public bool IsFailed => Status == DownloadTaskStatus.Failed;

    /// <summary>
    /// 是否被选中（用于任务切换界面）
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// 取消任务
    /// </summary>
    public void Cancel()
    {
        _task.Cancel();
    }
}
