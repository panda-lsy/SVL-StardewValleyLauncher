using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;

namespace SVL.Avalonia.Models;

public enum DownloadTaskKind
{
    Generic,
    NxmMod,
    NxmCollection,
    /// <summary>SVL 格式整合包（modpack.json + sources.json + mods/ + settings/）。</summary>
    SvlModpack,
    /// <summary>Curseforge 格式整合包（manifest.json + overrides/）。</summary>
    CurseforgeModpack,
    /// <summary>Nexus Collection 7z 包安装（collection.json + Phase 分阶段）。</summary>
    NexusCollection
}

public enum DownloadTaskAction
{
    InstallMod,
    SaveOnly,
    InstallSmapi,
    /// <summary>安装整合包（SVL 或 Curseforge 格式，由 TaskKind 区分）。</summary>
    InstallModpack,
    /// <summary>安装 Nexus Collection（7z 包解析 + Phase 分阶段安装）。</summary>
    InstallCollection
}

/// <summary>任务状态机枚举。权威状态来源，取代字符串匹配。</summary>
public enum DownloadTaskState
{
    /// <summary>排队等待（含重试入队、等待浏览器回调）。</summary>
    Pending,
    /// <summary>解析清单/依赖（Collection 获取清单、解析资源）。</summary>
    Resolving,
    /// <summary>下载中。</summary>
    Downloading,
    /// <summary>安装中（含 SMAPI 安装、Collection 条目安装）。</summary>
    Installing,
    /// <summary>已完成（含另存为、SMAPI 安装完成）。</summary>
    Completed,
    /// <summary>失败（可重试）。含下载失败、安装失败、上次运行中断。</summary>
    Failed,
    /// <summary>已取消。含安装已取消。</summary>
    Cancelled
}

public partial class DownloadTaskItem : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private int _progress;

    [ObservableProperty]
    private bool _canRetry;

    [ObservableProperty]
    private bool _canCancel;

    [ObservableProperty]
    private DownloadTaskKind _taskKind = DownloadTaskKind.Generic;

    [ObservableProperty]
    private DownloadTaskAction _taskAction = DownloadTaskAction.InstallMod;

    /// <summary>Nexus Mod ID（用于 Nexus 下载缓存键）。</summary>
    public long? SourceModId { get; set; }

    /// <summary>Nexus File ID（用于 Nexus 下载缓存键）。</summary>
    public long? SourceFileId { get; set; }

    /// <summary>任务状态机（权威状态来源）。Status 字符串仅用于显示。</summary>
    [ObservableProperty]
    private DownloadTaskState _taskState = DownloadTaskState.Pending;

    /// <summary>
    /// 外部取消回调。由在 DownloadPageViewModel 之外执行的任务流程设置
    /// （如版本设置页的 SMAPI 更新任务），CancelTask 命令会优先调用它而非查找内部 CTS 字典。
    /// 流程结束时应置回 null。
    /// </summary>
    public Action? CancelRequested { get; set; }

    /// <summary>同时设置状态机与显示文本的便捷方法。所有状态变更应走此方法。</summary>
    public void SetState(DownloadTaskState state, string displayText)
    {
        TaskState = state;
        Status = displayText;
    }

    [ObservableProperty]
    private string _sourceUrl = string.Empty;

    [ObservableProperty]
    private string _outputFilePath = string.Empty;

    [ObservableProperty]
    private string _installedPath = string.Empty;

    [ObservableProperty]
    private string _reportPath = string.Empty;

    [ObservableProperty]
    private string _backupPath = string.Empty;

    [ObservableProperty]
    private string _failedDetails = string.Empty;

    [ObservableProperty]
    private string _retryReportPath = string.Empty;

    [ObservableProperty]
    private string _statusIconSource = string.Empty;

    [ObservableProperty]
    private string _targetGamePath = string.Empty;

    [ObservableProperty]
    private string _targetInstanceName = string.Empty;

    /// <summary>下载速度文本（如 "2.3 MB/s"），由下载进度回调填充。空表示无速度信息。</summary>
    [ObservableProperty]
    private string _speedText = string.Empty;

    /// <summary>剩余时间预估文本（如 "约 1 分 30 秒"），由下载进度回调填充。空表示无法估算。</summary>
    [ObservableProperty]
    private string _etaText = string.Empty;

    /// <summary>文件总大小文本（如 "45.2 MB"），由下载开始时填充。空表示未知。</summary>
    [ObservableProperty]
    private string _totalSizeText = string.Empty;

    /// <summary>已下载大小文本（如 "12.8 MB"），由下载进度回调填充。</summary>
    [ObservableProperty]
    private string _downloadedSizeText = string.Empty;

    /// <summary>子进度文本（如 "3/93 已完成"），用于 Collection/整合包等多 Mod 场景。空表示无子任务。</summary>
    [ObservableProperty]
    private string _subProgressText = string.Empty;

    /// <summary>子进度百分比（0-100），用于 Collection/整合包等多 Mod 场景。-1 表示无子进度。</summary>
    [ObservableProperty]
    private int _subProgress = -1;

    /// <summary>安装目录路径（任务完成后填充，供"打开目录"操作使用）。</summary>
    [ObservableProperty]
    private string _installedDirectory = string.Empty;

    /// <summary>多线程分片进度（每个线程一条，进度条分块显示）。必须在 UI 线程上同步。</summary>
    public ObservableCollection<DownloadSegmentItem> SegmentItems { get; } = [];

    /// <summary>是否有分片进度可展示（多于 1 个线程时）。</summary>
    public bool HasSegmentProgress => SegmentItems.Count > 1;

    /// <summary>同步分片进度（创建或更新分片项）。必须通过 Dispatcher.UIThread 调用。</summary>
    public void SyncSegmentProgress(double[] segmentPercents)
    {
        if (segmentPercents == null || segmentPercents.Length <= 1)
        {
            return;
        }

        if (SegmentItems.Count != segmentPercents.Length)
        {
            SegmentItems.Clear();
            for (var i = 0; i < segmentPercents.Length; i++)
            {
                SegmentItems.Add(new DownloadSegmentItem { Index = i, Percent = segmentPercents[i] });
            }
            OnPropertyChanged(nameof(HasSegmentProgress));
        }
        else
        {
            for (var i = 0; i < segmentPercents.Length; i++)
            {
                SegmentItems[i].Percent = segmentPercents[i];
            }
        }
    }

    /// <summary>清空分片进度。必须通过 Dispatcher.UIThread 调用。</summary>
    public void ClearSegmentProgress()
    {
        if (SegmentItems.Count == 0)
        {
            return;
        }

        SegmentItems.Clear();
        OnPropertyChanged(nameof(HasSegmentProgress));
    }

    /// <summary>
    /// 上次同步到任务列表时的状态。用于检测状态类型是否变化（避免进度回调频繁 SyncTasks 导致闪烁）。
    /// 非 Observable 字段，仅供 HandleTaskStateChanged 内部判断使用。
    /// </summary>
    public DownloadTaskState? PreviousSyncedState { get; set; }

    public bool HasReportPath => !string.IsNullOrWhiteSpace(ReportPath);

    public bool HasBackupPath => !string.IsNullOrWhiteSpace(BackupPath);

    public bool HasFailedDetails => !string.IsNullOrWhiteSpace(FailedDetails);

    public bool HasRetryReportPath => !string.IsNullOrWhiteSpace(RetryReportPath);

    /// <summary>是否有速度信息可展示。</summary>
    public bool HasSpeedInfo => !string.IsNullOrWhiteSpace(SpeedText);

    /// <summary>是否有子进度可展示。</summary>
    public bool HasSubProgress => SubProgress >= 0 || !string.IsNullOrWhiteSpace(SubProgressText);

    /// <summary>是否有安装目录可打开。</summary>
    public bool HasInstalledDirectory => !string.IsNullOrWhiteSpace(InstalledDirectory);

    public bool IsRunning =>
        TaskState is DownloadTaskState.Resolving or DownloadTaskState.Downloading or DownloadTaskState.Installing;

    public bool IsFailed => TaskState == DownloadTaskState.Failed;

    public bool IsCancelled => TaskState == DownloadTaskState.Cancelled;

    public bool IsCompleted => TaskState == DownloadTaskState.Completed;

    public bool IsFinished =>
        TaskState is DownloadTaskState.Completed or DownloadTaskState.Failed or DownloadTaskState.Cancelled;

    public string DisplayStatusText
    {
        get
        {
            return TaskState switch
            {
                DownloadTaskState.Pending => "排队中",
                DownloadTaskState.Resolving or DownloadTaskState.Downloading or DownloadTaskState.Installing => Status,
                DownloadTaskState.Completed => "成功",
                DownloadTaskState.Failed => "失败",
                DownloadTaskState.Cancelled => "已取消",
                _ => Status
            };
        }
    }

    partial void OnReportPathChanged(string value)
    {
        OnPropertyChanged(nameof(HasReportPath));
    }

    partial void OnBackupPathChanged(string value)
    {
        OnPropertyChanged(nameof(HasBackupPath));
    }

    partial void OnFailedDetailsChanged(string value)
    {
        OnPropertyChanged(nameof(HasFailedDetails));
    }

    partial void OnRetryReportPathChanged(string value)
    {
        OnPropertyChanged(nameof(HasRetryReportPath));
    }

    partial void OnStatusChanged(string value)
    {
        // 运行态 DisplayStatusText 直接返回 Status，故需刷新
        OnPropertyChanged(nameof(DisplayStatusText));
    }

    partial void OnTaskStateChanged(DownloadTaskState value)
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsCancelled));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(DisplayStatusText));
    }

    partial void OnSpeedTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasSpeedInfo));
    }

    partial void OnSubProgressChanged(int value)
    {
        OnPropertyChanged(nameof(HasSubProgress));
    }

    partial void OnSubProgressTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasSubProgress));
    }

    partial void OnInstalledDirectoryChanged(string value)
    {
        OnPropertyChanged(nameof(HasInstalledDirectory));
    }

    public List<string> DependencyUrls { get; set; } = [];

    public List<string> FailedDownloadUrls { get; set; } = [];

    public List<string> ConflictPreviewItems { get; set; } = [];
}

/// <summary>多线程下载分片进度项（每线程一条，用于进度条分块显示）。</summary>
public partial class DownloadSegmentItem : ObservableObject
{
    /// <summary>分片序号（从 0 开始）。</summary>
    public int Index { get; init; }

    /// <summary>分片进度百分比（0-100）。</summary>
    [ObservableProperty]
    private double _percent;
}
