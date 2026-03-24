using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SVL.Core.Logging;

namespace SVL.Core.Download;

/// <summary>
/// 下载管理器（单例）
/// </summary>
public class DownloadManager
{
    private static readonly Lazy<DownloadManager> _instance = new(() => new DownloadManager());
    public static DownloadManager Instance => _instance.Value;

    private readonly ConcurrentDictionary<string, DownloadTask> _tasks = new();
    private readonly object _lock = new();

    // 并发控制：最多同时下载 3 个任务
    private SemaphoreSlim _downloadSemaphore = new(3, 3);
    private int _maxConcurrentDownloads = 3;

    // 事件
    public event Action<DownloadTask>? TaskAdded;
    public event Action<DownloadTask>? TaskUpdated;
    public event Action<DownloadTask>? TaskCompleted;
    public event Action<DownloadTask, Exception>? TaskFailed;
    public event Action? TaskListChanged;

    private DownloadManager() { }

    /// <summary>
    /// 获取所有任务
    /// </summary>
    public IReadOnlyList<DownloadTask> GetAllTasks()
    {
        return _tasks.Values.OrderBy(t => t.CreatedTime).ToList();
    }

    /// <summary>
    /// 根据ID获取任务
    /// </summary>
    public DownloadTask? GetTask(string taskId)
    {
        _tasks.TryGetValue(taskId, out var task);
        return task;
    }

    /// <summary>
    /// 获取活动任务（进行中或等待中）
    /// </summary>
    public IReadOnlyList<DownloadTask> GetActiveTasks()
    {
        return _tasks.Values
            .Where(t => t.Status == DownloadTaskStatus.Downloading ||
                       t.Status == DownloadTaskStatus.Installing ||
                       t.Status == DownloadTaskStatus.Pending ||
                       t.Status == DownloadTaskStatus.WaitingConfirmation) // 等待用户确认的任务也应该被视为活跃任务
            .OrderBy(t => t.CreatedTime)
            .ToList();
    }

    /// <summary>
    /// 获取已完成任务
    /// </summary>
    public IReadOnlyList<DownloadTask> GetCompletedTasks()
    {
        return _tasks.Values
            .Where(t => t.Status == DownloadTaskStatus.Completed ||
                       t.Status == DownloadTaskStatus.Failed ||
                       t.Status == DownloadTaskStatus.Cancelled)
            .OrderByDescending(t => t.CompletedTime ?? t.CreatedTime)
            .ToList();
    }

    /// <summary>
    /// 添加下载任务
    /// </summary>
    public async Task<string> AddTaskAsync(DownloadTask task)
    {
        _tasks[task.Id] = task;

        // 使用信号量控制并发，在后台执行任务
        _ = Task.Run(async () =>
        {
            // 等待可用下载槽位
            await GetSemaphore().WaitAsync();

            try
            {
                await ExecuteTaskWithUpdates(task);
            }
            finally
            {
                // 释放下载槽位
                GetSemaphore().Release();
            }
        });

        TaskAdded?.Invoke(task);
        TaskListChanged?.Invoke();

        Log.Info($"[DownloadManager] 已添加任务: {task.Name} (当前队列: {GetSemaphore().CurrentCount} 可用)");
        return task.Id;
    }

    /// <summary>
    /// 设置最大并发下载数
    /// </summary>
    public void SetMaxConcurrentDownloads(int max)
    {
        if (max < 1)
            max = 1;
        if (max > 10)
            max = 10;

        lock (_lock)
        {
            if (_maxConcurrentDownloads == max)
            {
                return;
            }

            var oldSemaphore = _downloadSemaphore;
            var oldMax = _maxConcurrentDownloads;
            var inFlight = Math.Max(0, oldMax - oldSemaphore.CurrentCount);

            _maxConcurrentDownloads = max;

            var initialCount = Math.Max(0, _maxConcurrentDownloads - inFlight);
            _downloadSemaphore = new SemaphoreSlim(initialCount, _maxConcurrentDownloads);

            // 旧信号量可能仍被已在排队的任务引用，不能立即释放。
        }

        Log.Info($"[DownloadManager] 最大并发下载数设置为: {_maxConcurrentDownloads}");
    }

    /// <summary>
    /// 获取当前下载队列状态
    /// </summary>
    public (int available, int total) GetQueueStatus()
    {
        var semaphore = GetSemaphore();
        return (semaphore.CurrentCount, _maxConcurrentDownloads);
    }

    private SemaphoreSlim GetSemaphore()
    {
        lock (_lock)
        {
            return _downloadSemaphore;
        }
    }

    /// <summary>
    /// 执行任务并更新进度
    /// </summary>
    private async Task ExecuteTaskWithUpdates(DownloadTask task)
    {
        try
        {
            // 定期更新进度
            var previousProgress = 0.0;
            var previousStatus = task.Status;
            using var monitorCts = new CancellationTokenSource();

            // 启动进度监控
            var monitorTask = Task.Run(async () =>
            {
                while (!monitorCts.Token.IsCancellationRequested &&
                       (task.Status == DownloadTaskStatus.Downloading ||
                        task.Status == DownloadTaskStatus.Installing ||
                        task.Status == DownloadTaskStatus.Pending))
                {
                    await Task.Delay(500, monitorCts.Token);

                    if (Math.Abs(task.Progress - previousProgress) > 0.1 ||
                        task.Status != previousStatus)
                    {
                        TaskUpdated?.Invoke(task);
                        previousProgress = task.Progress;
                        previousStatus = task.Status;
                    }
                }
            });

            // 执行任务
            await task.ExecuteAsync();

            // 等待监控结束
            monitorCts.Cancel();
            try
            {
                await monitorTask;
            }
            catch (OperationCanceledException)
            {
                // ignore
            }

            // 根据任务最终状态发送相应事件
            if (task.Status == DownloadTaskStatus.Completed)
            {
                // 任务成功完成
                TaskCompleted?.Invoke(task);
                TaskListChanged?.Invoke();
            }
            else if (task.Status == DownloadTaskStatus.Failed)
            {
                // 任务失败（在 ExecuteAsync 中设置状态但没有抛出异常）
                var exception = new Exception(task.StatusMessage ?? "任务执行失败");
                Log.Error(exception, $"[DownloadManager] 任务执行失败: {task.Name}");
                TaskFailed?.Invoke(task, exception);
                TaskUpdated?.Invoke(task);
                TaskListChanged?.Invoke();
            }
            else if (task.Status == DownloadTaskStatus.Cancelled)
            {
                // 任务被取消，不触发 TaskCompleted 事件，只触发 TaskListChanged
                Log.Info($"[DownloadManager] 任务已取消: {task.Name}");
                TaskListChanged?.Invoke();
            }
            // 其他状态（Pending, Downloading, Installing 等）不触发事件
            else
            {
                TaskListChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[DownloadManager] 任务执行失败: {task.Name}");
            task.Status = DownloadTaskStatus.Failed;
            task.StatusMessage = $"执行失败: {ex.Message}";
            TaskFailed?.Invoke(task, ex);
            TaskUpdated?.Invoke(task);
            TaskListChanged?.Invoke();
        }
    }

    /// <summary>
    /// 取消任务
    /// </summary>
    public void CancelTask(string taskId)
    {
        if (_tasks.TryGetValue(taskId, out var task))
        {
            task.Cancel();
            TaskUpdated?.Invoke(task);
            TaskListChanged?.Invoke();
            Log.Info($"[DownloadManager] 已取消任务: {task.Name}");
        }
    }

    /// <summary>
    /// 移除任务
    /// </summary>
    public void RemoveTask(string taskId)
    {
        if (_tasks.TryRemove(taskId, out var task))
        {
            TaskListChanged?.Invoke();
            Log.Info($"[DownloadManager] 已移除任务: {task.Name}");
        }
    }

    /// <summary>
    /// 手动更新任务状态（用于占位任务等需要外部控制状态的任务）
    /// </summary>
    public void UpdateTaskStatus(string taskId, DownloadTaskStatus status, string? statusMessage = null, double? progress = null)
    {
        if (_tasks.TryGetValue(taskId, out var task))
        {
            task.Status = status;
            if (statusMessage != null)
            {
                task.StatusMessage = statusMessage;
            }
            if (progress.HasValue)
            {
                task.Progress = progress.Value;
            }

            // 触发更新事件
            TaskUpdated?.Invoke(task);
            TaskListChanged?.Invoke();

            // 如果任务状态为失败，触发 TaskFailed 事件
            if (status == DownloadTaskStatus.Failed)
            {
                var exception = new Exception(task.StatusMessage ?? "任务执行失败");
                TaskFailed?.Invoke(task, exception);
            }
            // 如果任务状态为已完成，触发 TaskCompleted 事件
            else if (status == DownloadTaskStatus.Completed)
            {
                TaskCompleted?.Invoke(task);
            }

            // 降低日志级别：每秒更新不需要记录 Debug 日志
            // Log.Debug($"[DownloadManager] 手动更新任务状态: {task.Name}, Status={status}, Progress={progress}");
        }
    }

    /// <summary>
    /// 清空已完成的任务
    /// </summary>
    public void ClearCompletedTasks()
    {
        var completedTasks = _tasks
            .Where(kvp => kvp.Value.Status == DownloadTaskStatus.Completed)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var taskId in completedTasks)
        {
            _tasks.TryRemove(taskId, out _);
        }

        if (completedTasks.Count > 0)
        {
            TaskListChanged?.Invoke();
            Log.Info($"[DownloadManager] 已清空 {completedTasks.Count} 个已完成任务");
        }
    }

    /// <summary>
    /// 获取活动任务数量
    /// </summary>
    public int GetActiveTaskCount()
    {
        return GetActiveTasks().Count;
    }

    /// <summary>
    /// 执行内部任务（不添加到全局任务列表，用于批量更新等场景）
    /// </summary>
    public async Task<DownloadTaskStatus> ExecuteInternalTaskAsync(DownloadTask task)
    {
        Log.Debug($"[DownloadManager] 执行内部任务: {task.Name}");

        // 直接执行任务，不添加到全局列表，不触发事件
        await ExecuteTaskWithUpdates(task);

        Log.Debug($"[DownloadManager] 内部任务完成: {task.Name}, Status={task.Status}");
        return task.Status;
    }
}
