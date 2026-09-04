using SVL.Avalonia.Models;
using System.IO.Compression;
using System.Text.Json;

namespace SVL.Avalonia.Services;

public sealed class DownloadTaskStateStore
{
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
            SourceUrl = task.SourceUrl,
            OutputFilePath = task.OutputFilePath,
            InstalledPath = task.InstalledPath,
            ReportPath = task.ReportPath,
            BackupPath = task.BackupPath,
            FailedDetails = task.FailedDetails,
            RetryReportPath = task.RetryReportPath,
            TargetGamePath = task.TargetGamePath,
            TargetInstanceName = task.TargetInstanceName,
            DependencyUrls = task.DependencyUrls.ToList(),
            FailedDownloadUrls = task.FailedDownloadUrls.ToList(),
            ConflictPreviewItems = task.ConflictPreviewItems.ToList()
        }).ToList();

        var envelope = new DownloadTaskStateEnvelope
        {
            Version = 3,
            Tasks = records
        };

        var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true });
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);

        var parent = Path.GetDirectoryName(statePath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var tempPath = statePath + ".tmp";
        using (var fileStream = File.Create(tempPath))
        using (var gzip = new GZipStream(fileStream, CompressionLevel.SmallestSize))
        {
            gzip.Write(bytes, 0, bytes.Length);
            gzip.Flush();
        }

        if (File.Exists(statePath))
        {
            File.Move(tempPath, statePath, true);
        }
        else
        {
            File.Move(tempPath, statePath);
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
                var envelope = JsonSerializer.Deserialize<DownloadTaskStateEnvelope>(json);
                return envelope?.Tasks ?? [];
            }
            catch
            {
                // Backward compatibility with pre-envelope persistence payload.
                var records = JsonSerializer.Deserialize<List<DownloadTaskStateRecord>>(json);
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

    public string SourceUrl { get; set; } = string.Empty;

    public string OutputFilePath { get; set; } = string.Empty;

    public string InstalledPath { get; set; } = string.Empty;

    public string ReportPath { get; set; } = string.Empty;

    public string BackupPath { get; set; } = string.Empty;

    public string FailedDetails { get; set; } = string.Empty;

    public string RetryReportPath { get; set; } = string.Empty;

    public string TargetGamePath { get; set; } = string.Empty;

    public string TargetInstanceName { get; set; } = string.Empty;

    public List<string> DependencyUrls { get; set; } = [];

    public List<string> FailedDownloadUrls { get; set; } = [];

    public List<string> ConflictPreviewItems { get; set; } = [];
}

internal sealed class DownloadTaskStateEnvelope
{
    public int Version { get; set; }

    public List<DownloadTaskStateRecord> Tasks { get; set; } = [];
}
