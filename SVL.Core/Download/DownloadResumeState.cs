using System;
using System.IO;
using System.Text.Json;
using SVL.Core.Logging;

namespace SVL.Core.Download;

/// <summary>
/// 下载续传状态
/// </summary>
public class DownloadResumeState
{
    public string TaskId { get; set; }
    public string FileName { get; set; }
    public string FilePath { get; set; }
    public long TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public string DownloadUrl { get; set; }
    public DateTime LastUpdateTime { get; set; }
    public bool IsCompleted => DownloadedBytes >= TotalBytes && TotalBytes > 0;
}

/// <summary>
/// 下载续传状态管理器
/// </summary>
public static class DownloadResumeManager
{
    private static readonly string StateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SVL",
        "cache",
        "download_states"
    );

    static DownloadResumeManager()
    {
        if (!Directory.Exists(StateDir))
        {
            Directory.CreateDirectory(StateDir);
        }
    }

    /// <summary>
    /// 获取状态文件路径
    /// </summary>
    private static string GetStatePath(string taskId)
    {
        return Path.Combine(StateDir, $"{taskId}.json");
    }

    /// <summary>
    /// 保存下载状态
    /// </summary>
    public static void SaveState(DownloadResumeState state)
    {
        try
        {
            var statePath = GetStatePath(state.TaskId);
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(state, options);
            File.WriteAllText(statePath, json);
            Log.Debug($"[DownloadResume] 已保存下载状态: {state.TaskId}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DownloadResume] 保存下载状态失败");
        }
    }

    /// <summary>
    /// 加载下载状态
    /// </summary>
    public static DownloadResumeState? LoadState(string taskId)
    {
        try
        {
            var statePath = GetStatePath(taskId);
            if (!File.Exists(statePath))
                return null;

            var json = File.ReadAllText(statePath);
            var state = JsonSerializer.Deserialize<DownloadResumeState>(json);

            // 检查文件是否还存在且大小一致
            if (state != null && File.Exists(state.FilePath))
            {
                var fileInfo = new FileInfo(state.FilePath);
                if (fileInfo.Length == state.DownloadedBytes)
                {
                    Log.Info($"[DownloadResume] 找到有效的续传状态: {taskId}");
                    return state;
                }
                else
                {
                    Log.Warn($"[DownloadResume] 文件大小不匹配，删除续传状态: {taskId}");
                    DeleteState(taskId);
                }
            }
            else
            {
                Log.Warn($"[DownloadResume] 文件不存在，删除续传状态: {taskId}");
                DeleteState(taskId);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DownloadResume] 加载下载状态失败");
        }

        return null;
    }

    /// <summary>
    /// 删除下载状态
    /// </summary>
    public static void DeleteState(string taskId)
    {
        try
        {
            var statePath = GetStatePath(taskId);
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
                Log.Debug($"[DownloadResume] 已删除下载状态: {taskId}");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DownloadResume] 删除下载状态失败");
        }
    }

    /// <summary>
    /// 清除所有续传状态
    /// </summary>
    public static void ClearAllStates()
    {
        try
        {
            if (Directory.Exists(StateDir))
            {
                var files = Directory.GetFiles(StateDir);
                foreach (var file in files)
                {
                    File.Delete(file);
                }
                Log.Info($"[DownloadResume] 已清除 {files.Length} 个续传状态");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DownloadResume] 清除续传状态失败");
        }
    }
}
