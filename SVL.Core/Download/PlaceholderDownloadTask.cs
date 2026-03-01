using System;
using System.Threading;
using System.Threading.Tasks;
using SVL.Core.Logging;

namespace SVL.Core.Download;

/// <summary>
/// 占位下载任务（仅用于显示按钮，不执行实际操作）
/// 用于在下载前显示任务管理按钮，实际下载由外部控制
/// </summary>
public class PlaceholderDownloadTask : DownloadTask
{
    private readonly string _statusMessage;
    private readonly CancellationTokenSource _cts = new();

    public PlaceholderDownloadTask(string name, DownloadTaskType type, string statusMessage = "准备中...")
    {
        Name = name;
        Type = type;
        _statusMessage = statusMessage;
        Status = DownloadTaskStatus.Pending;
        StatusMessage = _statusMessage;
        Progress = 0;
    }

    /// <summary>
    /// 获取取消令牌（用于传递给外部下载方法）
    /// </summary>
    public CancellationToken CancellationToken => _cts.Token;

    public override Task ExecuteAsync()
    {
        // 占位任务不执行任何操作，直接返回
        // 实际的下载和安装逻辑由外部控制
        return Task.CompletedTask;
    }

    public override void Cancel()
    {
        try
        {
            _cts.Cancel();
            Status = DownloadTaskStatus.Cancelled;
            StatusMessage = "正在取消...";
            Log.Info($"[PlaceholderDownloadTask] 占位任务已取消: {Name}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[PlaceholderDownloadTask] 取消任务失败");
        }
    }
}
