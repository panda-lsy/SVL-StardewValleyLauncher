using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SVL.Core.Logging;
using SVL.Core.Modpack;

namespace SVL.Core.Download;

public class LocalModpackInstallTask : DownloadTask
{
    private readonly string _modpackFilePath;
    private readonly string _instanceName;
    private readonly string _gameBasePath;
    private readonly string _targetModsPath;
    private readonly CancellationTokenSource _cts = new();

    public LocalModpackInstallTask(string modpackFilePath, string instanceName, string gameBasePath, string targetModsPath)
    {
        _modpackFilePath = modpackFilePath;
        _instanceName = instanceName;
        _gameBasePath = gameBasePath;
        _targetModsPath = targetModsPath;

        Type = DownloadTaskType.Modpack;
        Name = $"整合包安装: {Path.GetFileNameWithoutExtension(modpackFilePath)}";
        Status = DownloadTaskStatus.Pending;
        StatusMessage = "等待安装...";
        Progress = 0;
    }

    public override async Task ExecuteAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_modpackFilePath) || !File.Exists(_modpackFilePath))
            {
                throw new FileNotFoundException("整合包文件不存在", _modpackFilePath);
            }

            Status = DownloadTaskStatus.Installing;
            StatusMessage = "正在解析整合包...";
            Progress = 10;

            var manifest = CurseforgeModpackParser.Parse(_modpackFilePath);
            if (manifest == null)
            {
                throw new InvalidOperationException("无法解析整合包 manifest");
            }

            if (_cts.IsCancellationRequested)
            {
                Status = DownloadTaskStatus.Cancelled;
                StatusMessage = "已取消";
                return;
            }

            StatusMessage = "正在解压整合包...";
            Progress = 35;

            var tempDir = CurseforgeModpackParser.ExtractToTemp(_modpackFilePath);

            try
            {
                if (_cts.IsCancellationRequested)
                {
                    Status = DownloadTaskStatus.Cancelled;
                    StatusMessage = "已取消";
                    return;
                }

                Directory.CreateDirectory(_targetModsPath);

                int copiedFiles = 0;
                var overridesRoot = string.IsNullOrWhiteSpace(manifest.Overrides)
                    ? Path.Combine(tempDir, "overrides")
                    : Path.Combine(tempDir, manifest.Overrides);

                StatusMessage = "正在安装覆盖文件...";
                Progress = 60;

                if (Directory.Exists(overridesRoot))
                {
                    copiedFiles += CopyDirectoryContent(overridesRoot, _gameBasePath, _cts.Token);
                }

                if (_cts.IsCancellationRequested)
                {
                    Status = DownloadTaskStatus.Cancelled;
                    StatusMessage = "已取消";
                    return;
                }

                Progress = 90;
                StatusMessage = "正在收尾...";

                CompletedTime = DateTime.Now;
                Status = DownloadTaskStatus.Completed;

                var manifestFileCount = manifest.Files?.Count ?? 0;
                StatusMessage = $"已完成：{manifest.Name}（覆盖文件 {copiedFiles} 个，清单文件 {manifestFileCount} 个）";

                Log.Info($"[LocalModpackInstallTask] 安装完成: instance={_instanceName}, modsPath={_targetModsPath}, file={_modpackFilePath}");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, true);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("[LocalModpackInstallTask] 清理临时目录失败", ex);
                }
            }

            await Task.CompletedTask;
        }
        catch (OperationCanceledException)
        {
            Status = DownloadTaskStatus.Cancelled;
            StatusMessage = "已取消";
        }
        catch (Exception ex)
        {
            Status = DownloadTaskStatus.Failed;
            StatusMessage = $"安装失败: {ex.Message}";
            CompletedTime = DateTime.Now;
            Log.Error(ex, "[LocalModpackInstallTask] 安装失败");
            throw;
        }
    }

    public override void Cancel()
    {
        _cts.Cancel();
        Status = DownloadTaskStatus.Cancelled;
        StatusMessage = "正在取消...";
    }

    private static int CopyDirectoryContent(string sourceDir, string destinationDir, CancellationToken ct)
    {
        if (!Directory.Exists(sourceDir))
        {
            return 0;
        }

        var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
        int copied = 0;

        foreach (var sourceFile in files)
        {
            ct.ThrowIfCancellationRequested();

            var relative = sourceFile.Substring(sourceDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var targetFile = Path.Combine(destinationDir, relative);
            var targetFolder = Path.GetDirectoryName(targetFile);

            if (!string.IsNullOrWhiteSpace(targetFolder) && !Directory.Exists(targetFolder))
            {
                Directory.CreateDirectory(targetFolder);
            }

            File.Copy(sourceFile, targetFile, true);
            copied++;
        }

        return copied;
    }
}
