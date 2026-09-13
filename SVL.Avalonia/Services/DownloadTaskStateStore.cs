using SVL.Avalonia.Models;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SVL.Avalonia.Services;

public sealed class DownloadTaskStateStore
{
    // 早期调试版本曾使用字符串枚举写入任务状态，当前版本默认写数字枚举。
    // 读取时同时接受两种形态，避免一个旧任务字段就导致整份 Pending 队列被
    // 当作损坏文件备份掉。
    private static readonly JsonSerializerOptions LoadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public void Save(string statePath, IReadOnlyList<DownloadTaskItem> tasks)
    {
        var records = tasks.Select(task => new DownloadTaskStateRecord
        {
            Name = task.Name,
            Status = task.Status,
            Progress = task.Progress,
            TaskState = task.TaskState,
            CanRetry = task.CanRetry,
            CanCancel = task.CanCancel,
            TaskKind = task.TaskKind,
            TaskAction = task.TaskAction,
            SourceModId = task.SourceModId,
            SourceFileId = task.SourceFileId,
            SourcePlatform = task.SourcePlatform,
            SourceRepository = task.SourceRepository,
            CollectionSlug = task.CollectionSlug,
            CollectionRevision = task.CollectionRevision,
            SourceUrl = task.SourceUrl,
            OutputFilePath = task.OutputFilePath,
            InstalledPath = task.InstalledPath,
            InstalledDirectory = task.InstalledDirectory,
            ReportPath = task.ReportPath,
            BackupPath = task.BackupPath,
            FailedDetails = task.FailedDetails,
            RetryReportPath = task.RetryReportPath,
            TargetGamePath = task.TargetGamePath,
            TargetInstanceName = task.TargetInstanceName,
            CustomIconPath = task.CustomIconPath,
            SkipConflictPrompt = task.SkipConflictPrompt,
            SpeedText = task.SpeedText,
            EtaText = task.EtaText,
            TotalSizeText = task.TotalSizeText,
            DownloadedSizeText = task.DownloadedSizeText,
            SubProgressText = task.SubProgressText,
            SubProgress = task.SubProgress,
            CollectionModItems = task.CollectionModItems.Select(item => new CollectionModTaskStateRecord
            {
                Name = item.Name,
                Phase = item.Phase,
                Optional = item.Optional,
                State = item.State,
                Message = item.Message,
                SourceUrl = item.SourceUrl,
                RequiresManualAction = item.RequiresManualAction
            }).ToList(),
            DependencyUrls = task.DependencyUrls.ToList(),
            FailedDownloadUrls = task.FailedDownloadUrls.ToList(),
            ConflictPreviewItems = task.ConflictPreviewItems.ToList()
        }).ToList();

        var envelope = new DownloadTaskStateEnvelope
        {
            Version = 5,
            Tasks = records
        };

        var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true });
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);

        var parent = Path.GetDirectoryName(statePath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var fullStatePath = Path.GetFullPath(statePath);
        var tempPath = fullStatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fileStream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       options: FileOptions.WriteThrough))
            {
                using (var gzip = new GZipStream(
                           fileStream,
                           CompressionLevel.SmallestSize,
                           leaveOpen: true))
                {
                    gzip.Write(bytes, 0, bytes.Length);
                }

                // GZipStream 关闭后再刷新底层流，确保压缩尾部已落盘。
                fileStream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, fullStatePath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // 状态文件属于恢复辅助数据，临时文件清理失败不应阻塞任务队列。
            }
        }
    }

    public IReadOnlyList<DownloadTaskStateRecord> Load(string statePath, out string corruptedBackupPath)
    {
        corruptedBackupPath = string.Empty;
        if (!File.Exists(statePath))
        {
            return [];
        }

        try
        {
            var json = ReadPersistedTaskStateJson(statePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return [];
            }

            try
            {
                var envelope = JsonSerializer.Deserialize<DownloadTaskStateEnvelope>(json, LoadJsonOptions);
                return envelope?.Tasks ?? [];
            }
            catch
            {
                // Backward compatibility with pre-envelope persistence payload.
                var records = JsonSerializer.Deserialize<List<DownloadTaskStateRecord>>(json, LoadJsonOptions);
                return records ?? [];
            }
        }
        catch
        {
            corruptedBackupPath = BackupCorruptedTaskState(statePath);
            return [];
        }
    }

    private static string ReadPersistedTaskStateJson(string statePath)
    {
        try
        {
            using var fileStream = File.OpenRead(statePath);
            using var gzip = new GZipStream(fileStream, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            return reader.ReadToEnd();
        }
        catch
        {
            return File.ReadAllText(statePath);
        }
    }

    private static string BackupCorruptedTaskState(string statePath)
    {
        if (!File.Exists(statePath))
        {
            return string.Empty;
        }

        var brokenPath = statePath + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss");
        try
        {
            File.Move(statePath, brokenPath, true);
            return brokenPath;
        }
        catch
        {
            return string.Empty;
        }
    }
}

public sealed class DownloadTaskStateRecord
{
    public string Name { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public int Progress { get; set; }

    /// <summary>任务状态机状态。旧版本状态文件没有此字段，读取时回退到 Status 推断。</summary>
    public DownloadTaskState? TaskState { get; set; }

    public bool CanRetry { get; set; }

    public bool CanCancel { get; set; }

    public DownloadTaskKind TaskKind { get; set; } = DownloadTaskKind.Generic;

    public DownloadTaskAction TaskAction { get; set; } = DownloadTaskAction.InstallMod;

    public long? SourceModId { get; set; }

    public long? SourceFileId { get; set; }

    public string SourcePlatform { get; set; } = string.Empty;

    public string SourceRepository { get; set; } = string.Empty;

    public string CollectionSlug { get; set; } = string.Empty;

    public int CollectionRevision { get; set; } = -1;

    public string SourceUrl { get; set; } = string.Empty;

    public string OutputFilePath { get; set; } = string.Empty;

    public string InstalledPath { get; set; } = string.Empty;

    /// <summary>安装版本根目录；与 InstalledPath（实际运行目录）分开保存，便于重启后打开目录和判断更新模式。</summary>
    public string InstalledDirectory { get; set; } = string.Empty;

    public string ReportPath { get; set; } = string.Empty;

    public string BackupPath { get; set; } = string.Empty;

    public string FailedDetails { get; set; } = string.Empty;

    public string RetryReportPath { get; set; } = string.Empty;

    public string TargetGamePath { get; set; } = string.Empty;

    public string TargetInstanceName { get; set; } = string.Empty;

    public string CustomIconPath { get; set; } = string.Empty;

    /// <summary>批量更新任务恢复后仍需跳过冲突弹窗，并在覆盖前自动备份。</summary>
    public bool SkipConflictPrompt { get; set; }

    /// <summary>下载展示字段。旧状态文件没有这些属性时使用默认空值，不影响恢复。</summary>
    public string SpeedText { get; set; } = string.Empty;

    public string EtaText { get; set; } = string.Empty;

    public string TotalSizeText { get; set; } = string.Empty;

    public string DownloadedSizeText { get; set; } = string.Empty;

    public string SubProgressText { get; set; } = string.Empty;

    public int SubProgress { get; set; } = -1;

    /// <summary>整合包逐 Mod 状态。旧版本没有此字段时按空集合兼容。</summary>
    public List<CollectionModTaskStateRecord> CollectionModItems { get; set; } = [];

    public List<string> DependencyUrls { get; set; } = [];

    public List<string> FailedDownloadUrls { get; set; } = [];

    public List<string> ConflictPreviewItems { get; set; } = [];
}

public sealed class CollectionModTaskStateRecord
{
    public string Name { get; set; } = string.Empty;

    public int Phase { get; set; } = 1;

    public bool Optional { get; set; }

    public CollectionModTaskState State { get; set; } = CollectionModTaskState.Pending;

    public string Message { get; set; } = string.Empty;

    public string SourceUrl { get; set; } = string.Empty;

    public bool RequiresManualAction { get; set; }
}

internal sealed class DownloadTaskStateEnvelope
{
    public int Version { get; set; }

    public List<DownloadTaskStateRecord> Tasks { get; set; } = [];
}
