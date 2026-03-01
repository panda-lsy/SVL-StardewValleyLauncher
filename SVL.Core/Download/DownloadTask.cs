using System;
using System.Threading.Tasks;

namespace SVL.Core.Download;

/// <summary>
/// 下载任务状态
/// </summary>
public enum DownloadTaskStatus
{
    Pending,               // 等待中
    Downloading,           // 下载中
    Installing,            // 安装中
    WaitingConfirmation,   // 等待用户确认
    Completed,             // 已完成
    Failed,                // 失败
    Cancelled              // 已取消
}

/// <summary>
/// 下载任务类型
/// </summary>
public enum DownloadTaskType
{
    SMAPI,          // SMAPI 安装
    Mod,            // Mod 下载
    Modpack,        // 整合包
    Utility         // 实用工具
}

/// <summary>
/// 下载任务基类
/// </summary>
public abstract class DownloadTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public DownloadTaskType Type { get; set; }
    public DownloadTaskStatus Status { get; set; } = DownloadTaskStatus.Pending;
    public double Progress { get; set; } = 0;
    /// <summary>
    /// 当前文件下载进度（0-100），仅表示正在下载的单个文件的进度
    /// 与 Progress 不同，Progress 包含所有任务阶段的综合进度
    /// </summary>
    public double FileDownloadProgress { get; set; } = 0;
    /// <summary>
    /// 当前文件已下载字节数
    /// </summary>
    public long FileDownloadBytes { get; set; } = 0;
    /// <summary>
    /// 当前文件总字节数
    /// </summary>
    public long FileDownloadTotalBytes { get; set; } = 0;
    public string StatusMessage { get; set; } = "等待中...";
    public DateTime CreatedTime { get; set; } = DateTime.Now;
    public DateTime? CompletedTime { get; set; }

    /// <summary>
    /// 取消任务
    /// </summary>
    public abstract void Cancel();

    /// <summary>
    /// 执行下载任务
    /// </summary>
    public abstract Task ExecuteAsync();
}
