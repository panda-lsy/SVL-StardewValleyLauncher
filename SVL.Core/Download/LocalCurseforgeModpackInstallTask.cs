using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.Zip;
using SVL.Core.Config;
using SVL.Core.IO;
using SVL.Core.Logging;
using SVL.Core.Modpack;
using SVL.Core.Stardew.Instance;
using SVL.Core.Stardew.Mod.SMAPI;

namespace SVL.Core.Download;

/// <summary>
/// Curseforge 模组下载状态
/// </summary>
public enum CurseforgeModDownloadStatus
{
    Pending,
    Downloading,
    Completed,
    Failed,
    Skipped
}

/// <summary>
/// Curseforge 模组下载项（用于UI显示）
/// </summary>
public class CurseforgeModDownloadItem
{
    public long ProjectId { get; set; }
    public long FileId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Required { get; set; } = true;
    public CurseforgeModDownloadStatus Status { get; set; } = CurseforgeModDownloadStatus.Pending;
}

/// <summary>
/// 本地 Curseforge 整合包安装任务
/// 用于从本地 ZIP 文件导入整合包（拖放导入场景）
/// </summary>
public class LocalCurseforgeModpackInstallTask : DownloadTask
{
    private readonly string _zipFilePath;
    private readonly string _instanceName;
    private readonly string _gameBasePath;
    private readonly string _targetModsPath;
    private readonly CancellationTokenSource _cts = new();

    private string? _extractDir;
    private CurseforgeModpackManifest? _manifest;

    /// <summary>
    /// 版本根路径（_targetModsPath 的父目录），用于失败/取消时清理空目录
    /// </summary>
    private string? _versionRootPath;

    /// <summary>
    /// 是否创建了新的版本目录（用于区分已存在的版本目录）
    /// </summary>
    private bool _versionDirectoryCreated = false;

    /// <summary>
    /// 失败的模组列表
    /// </summary>
    public List<FailedModInfo> FailedMods { get; } = new();

    /// <summary>
    /// 模组列表（用于UI显示）
    /// </summary>
    public ObservableCollection<CurseforgeModDownloadItem> ModList { get; } = new();

    /// <summary>
    /// 当前正在下载的模组
    /// </summary>
    public CurseforgeModDownloadItem? CurrentMod { get; private set; }

    public LocalCurseforgeModpackInstallTask(
        string zipFilePath,
        string instanceName,
        string gameBasePath)
    {
        _zipFilePath = zipFilePath;
        _instanceName = instanceName;
        _gameBasePath = gameBasePath;
        // 使用版本隔离路径
        _targetModsPath = InstanceIsolationService.GetIsolatedModsPath(gameBasePath, instanceName);

        Type = DownloadTaskType.Modpack;
        Name = $"整合包导入: {instanceName}";
        Status = DownloadTaskStatus.Pending;
        StatusMessage = "准备导入整合包...";
        Progress = 0;
    }

    public override async Task ExecuteAsync()
    {
        try
        {
            Status = DownloadTaskStatus.Downloading;
            StatusMessage = "正在读取整合包...";
            Progress = 0;

            Log.Info($"[LocalCurseforgeModpack] 开始导入本地整合包: {_zipFilePath}");

            // 验证文件存在
            if (!File.Exists(_zipFilePath))
            {
                throw new Exception($"整合包文件不存在: {_zipFilePath}");
            }

            // 记录版本根路径（使用版本隔离路径）
            _versionRootPath = InstanceIsolationService.GetVersionPath(_gameBasePath, _instanceName);
            Log.Info($"[LocalCurseforgeModpack] 版本根路径: {_versionRootPath}");

            // 检查版本名是否重复
            if (Directory.Exists(_versionRootPath))
            {
                Log.Error($"[LocalCurseforgeModpack] 版本目录已存在: {_versionRootPath}");
                throw new Exception($"版本名称 '{_instanceName}' 已存在，请使用不同的名称");
            }

            _versionDirectoryCreated = true;

            // 解压整合包
            StatusMessage = "正在解压整合包...";
            Progress = 10;

            _extractDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SVL",
                "temp",
                "modpack_import",
                Guid.NewGuid().ToString());

            Directory.CreateDirectory(_extractDir);
            Log.Info($"[LocalCurseforgeModpack] 解压到: {_extractDir}");

            await Task.Run(() => ExtractZip(_zipFilePath, _extractDir));

            Progress = 30;
            StatusMessage = "正在解析整合包...";

            // 解析 manifest.json
            var manifestPath = FindFileInDirectory(_extractDir, "manifest.json");
            if (string.IsNullOrEmpty(manifestPath))
            {
                throw new Exception("未找到 manifest.json 文件，无法识别整合包格式");
            }

            _manifest = CurseforgeModpackParser.ParseFromJsonFile(manifestPath);
            if (_manifest == null)
            {
                throw new Exception("无法解析 manifest.json 文件");
            }

            Log.Info($"[LocalCurseforgeModpack] 解析成功: {_manifest.Name}, 模组数: {_manifest.Files.Count}");
            Progress = 35;

            // 获取 API Key
            var apiKey = CurseforgeApiService.GetApiKey();
            if (string.IsNullOrEmpty(apiKey))
            {
                throw new Exception("Curseforge API Key 未配置，请在设置中配置 API Key");
            }

            // 安装 SMAPI
            await InstallSMAPIAsync(apiKey);

            Progress = 60;

            // 下载模组
            await InstallModsFromManifestAsync(apiKey);

            Progress = 85;

            // 处理 overrides
            await ProcessOverridesAsync();

            // 保存实例信息
            await SaveInstanceInfoAsync();

            Progress = 100;
            Status = DownloadTaskStatus.Completed;
            StatusMessage = $"整合包 {_manifest.Name} 导入完成！";
            CompletedTime = DateTime.Now;

            Log.Info($"[LocalCurseforgeModpack] 整合包导入完成: {_manifest.Name}");
        }
        catch (OperationCanceledException)
        {
            Status = DownloadTaskStatus.Cancelled;
            StatusMessage = "导入已取消";
            CleanupAsync().Wait();
            Log.Info($"[LocalCurseforgeModpack] 导入已取消");
        }
        catch (Exception ex)
        {
            Status = DownloadTaskStatus.Failed;
            StatusMessage = $"导入失败: {ex.Message}";
            CleanupAsync().Wait();
            Log.Error(ex, $"[LocalCurseforgeModpack] 导入失败");
            throw;
        }
        finally
        {
            // 清理临时目录
            CleanupTempDirectory();
        }
    }

    private void ExtractZip(string zipPath, string targetDir)
    {
        using var zipFile = new ZipFile(zipPath);
        foreach (ZipEntry entry in zipFile)
        {
            if (entry.IsDirectory)
                continue;

            var destinationPath = Path.Combine(targetDir, entry.Name.Replace('/', Path.DirectorySeparatorChar));
            var destinationDir = Path.GetDirectoryName(destinationPath);

            if (!string.IsNullOrEmpty(destinationDir) && !Directory.Exists(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
            }

            using var stream = zipFile.GetInputStream(entry);
            using var fileStream = File.Create(destinationPath);
            stream.CopyTo(fileStream);
        }
    }

    private string? FindFileInDirectory(string directory, string fileName)
    {
        // 首先检查根目录
        var rootFile = Path.Combine(directory, fileName);
        if (File.Exists(rootFile))
            return rootFile;

        // 检查子目录
        var subDirs = Directory.GetDirectories(directory);
        if (subDirs.Length == 1)
        {
            var subFile = Path.Combine(subDirs[0], fileName);
            if (File.Exists(subFile))
                return subFile;
        }

        // 递归搜索
        var files = Directory.GetFiles(directory, fileName, SearchOption.AllDirectories);
        return files.FirstOrDefault();
    }

    /// <summary>
    /// 安装 SMAPI
    /// </summary>
    private async Task InstallSMAPIAsync(string apiKey)
    {
        Log.Info("[LocalCurseforgeModpack] 安装 SMAPI");

        Progress = 40;
        StatusMessage = "正在准备安装 SMAPI...";

        // 获取 SMAPI 最新版本
        var smapiFiles = await CurseforgeApiService.GetSmapifiFilesAsync(0, 5);
        if (smapiFiles == null || smapiFiles.Count == 0)
        {
            Log.Warn("[LocalCurseforgeModpack] 无法获取 SMAPI 文件列表，跳过 SMAPI 安装");
            return;
        }

        var latestSmapi = smapiFiles.FirstOrDefault();
        if (latestSmapi == null)
        {
            Log.Warn("[LocalCurseforgeModpack] 无法找到 SMAPI 文件，跳过 SMAPI 安装");
            return;
        }

        // 解析并清理 SMAPI 版本名（去除重复前缀）
        var smapiDisplayName = CurseforgeHelper.ParseSmapiDisplayName(latestSmapi.DisplayName, latestSmapi.FileName);
        Log.Info($"[LocalCurseforgeModpack] 找到最新版 SMAPI: {smapiDisplayName} (原始：{latestSmapi.DisplayName}, FileId: {latestSmapi.Id})");

        // 使用缓存服务下载 SMAPI（使用清理后的 displayName）
        var smapiCacheKey = DownloadCacheService.GenerateCacheKey("smapi", latestSmapi.Id.ToString(), smapiDisplayName);
        string? smapiZipPath = null;

        try
        {
            // 尝试从缓存获取
            smapiZipPath = DownloadCacheService.GetCachedFile(smapiCacheKey, minFileSize: 1024 * 1024);

            if (smapiZipPath == null)
            {
                // 获取 SMAPI 下载链接
                var smapiDownloadUrl = await CurseforgeApiService.GetFileDownloadUrlAsync(898372, latestSmapi.Id);
                if (string.IsNullOrWhiteSpace(smapiDownloadUrl))
                {
                    Log.Warn("[LocalCurseforgeModpack] 无法获取 SMAPI 下载链接，跳过 SMAPI 安装");
                    return;
                }

                Log.Info($"[LocalCurseforgeModpack] 下载 SMAPI: {smapiDownloadUrl}");

                // 使用缓存服务下载
                smapiZipPath = await DownloadCacheService.DownloadAndCacheAsync(
                    smapiCacheKey,
                    smapiDownloadUrl,
                    progressCallback: progress => { }
                );
            }
            else
            {
                Log.Info($"[LocalCurseforgeModpack] 使用 SMAPI 缓存: {smapiZipPath}");
            }

            // 创建 SMAPI 安装任务（使用清理后的 displayName）
            var smapiTask = new SmapiDownloadTask(
                _gameBasePath,
                _instanceName,
                smapiZipPath,
                SmapiSource.Curseforge,
                false,
                smapiDisplayName
            );

            Progress = 45;
            StatusMessage = "正在安装 SMAPI...";

            await smapiTask.ExecuteAsync();

            Progress = 55;
            StatusMessage = "SMAPI 安装完成";
            Log.Info($"[LocalCurseforgeModpack] SMAPI 安装完成");
        }
        catch (Exception ex)
        {
            Log.Warn($"[LocalCurseforgeModpack] SMAPI 下载或安装失败", ex);
            // 不抛出异常，继续安装模组
        }
    }

    /// <summary>
    /// 从 manifest.json 安装模组
    /// </summary>
    private async Task InstallModsFromManifestAsync(string apiKey)
    {
        Log.Info("[LocalCurseforgeModpack] 安装 manifest 中的模组");

        if (_manifest?.Files == null || _manifest.Files.Count == 0)
        {
            Log.Info("[LocalCurseforgeModpack] 没有需要安装的模组");
            return;
        }

        // 计算需要安装的模组数量（排除 SMAPI 和可选模组）
        var requiredFiles = _manifest.Files
            .Where(f => f.ProjectId != 898372 && f.Required)
            .ToList();

        int totalRequiredMods = requiredFiles.Count;
        int skippedCount = _manifest.Files.Count(f => f.ProjectId == 898372 || !f.Required);

        Log.Info($"[LocalCurseforgeModpack] 需要安装 {totalRequiredMods} 个必需模组（跳过 {skippedCount} 个）");

        if (totalRequiredMods == 0)
        {
            Log.Info("[LocalCurseforgeModpack] 没有需要安装的必需模组");
            return;
        }

        // 初始化模组列表（用于UI显示）
        ModList.Clear();
        foreach (var file in requiredFiles)
        {
            ModList.Add(new CurseforgeModDownloadItem
            {
                ProjectId = file.ProjectId,
                FileId = file.FileId,
                Name = $"Project {file.ProjectId}", // 暂时使用 ProjectId，下载时会更新
                Required = file.Required,
                Status = CurseforgeModDownloadStatus.Pending
            });
        }

        // 从配置读取并发下载数
        var settings = AppConfig.GetSettings();
        var maxConcurrentDownloads = Math.Max(1, Math.Min(10, settings.MaxConcurrentModDownloads));

        using var downloadSemaphore = new SemaphoreSlim(maxConcurrentDownloads, maxConcurrentDownloads);
        var failedMods = new System.Collections.Concurrent.ConcurrentBag<(long projectId, long fileId, string error)>();
        int completedCount = 0;
        var progressLock = new object();

        StatusMessage = $"正在准备下载 {totalRequiredMods} 个模组...";

        var downloadTasks = requiredFiles.Select(async file =>
        {
            _cts.Token.ThrowIfCancellationRequested();

            await downloadSemaphore.WaitAsync(_cts.Token);
            try
            {
                // 查找对应的模组列表项并更新状态
                var modItem = ModList.FirstOrDefault(m => m.ProjectId == file.ProjectId && m.FileId == file.FileId);
                if (modItem != null)
                {
                    modItem.Status = CurseforgeModDownloadStatus.Downloading;
                    CurrentMod = modItem;
                }

                await DownloadAndInstallModAsync(file, apiKey);

                // 更新模组状态为完成
                if (modItem != null)
                {
                    modItem.Status = CurseforgeModDownloadStatus.Completed;
                }

                lock (progressLock)
                {
                    completedCount++;
                    var progress = 60 + (completedCount * 25 / totalRequiredMods);
                    Progress = progress;
                    StatusMessage = $"正在下载模组 ({completedCount}/{totalRequiredMods})...";
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[LocalCurseforgeModpack] 下载模组失败: ProjectId={file.ProjectId}, FileId={file.FileId}, 错误: {ex.Message}");
                failedMods.Add((file.ProjectId, file.FileId, ex.Message));

                // 更新模组状态为失败
                var modItem = ModList.FirstOrDefault(m => m.ProjectId == file.ProjectId && m.FileId == file.FileId);
                if (modItem != null)
                {
                    modItem.Status = CurseforgeModDownloadStatus.Failed;
                }
            }
            finally
            {
                downloadSemaphore.Release();
            }
        });

        await Task.WhenAll(downloadTasks);

        // 记录失败的模组
        foreach (var (projectId, fileId, error) in failedMods)
        {
            FailedMods.Add(new FailedModInfo
            {
                Platform = "Curseforge",
                ProjectId = projectId,
                FileId = fileId,
                ModName = $"Project {projectId}",
                Error = error
            });
        }

        Log.Info($"[LocalCurseforgeModpack] 模组下载完成: {completedCount}/{totalRequiredMods}, 失败: {FailedMods.Count}");
    }

    /// <summary>
    /// 下载并安装单个模组
    /// </summary>
    private async Task DownloadAndInstallModAsync(CurseforgeModpackFile file, string apiKey)
    {
        // 获取下载链接
        var downloadUrl = await CurseforgeApiService.GetFileDownloadUrlAsync((int)file.ProjectId, (int)file.FileId);
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            throw new Exception("无法获取下载链接");
        }

        // 从 URL 获取文件名
        var fileName = Path.GetFileName(new Uri(downloadUrl).LocalPath);

        // 更新模组列表中的名称
        var modItem = ModList.FirstOrDefault(m => m.ProjectId == file.ProjectId && m.FileId == file.FileId);
        if (modItem != null)
        {
            // 从文件名提取模组名称（去除版本号等）
            var modName = fileName;
            if (modName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                modName = modName.Substring(0, modName.Length - 4);
            }
            modItem.Name = modName;
        }

        // 生成缓存 key
        var cacheKey = DownloadCacheService.GenerateCacheKey("curseforge", $"{file.ProjectId}_{file.FileId}", fileName);

        // 尝试从缓存获取
        var cachedPath = DownloadCacheService.GetCachedFile(cacheKey, minFileSize: 1024);
        string? zipPath;

        if (cachedPath != null)
        {
            zipPath = cachedPath;
            Log.Info($"[LocalCurseforgeModpack] 使用缓存: {file.ProjectId}/{file.FileId}");
            // 缓存文件，直接设置进度为 100%
            FileDownloadProgress = 100;
            FileDownloadBytes = new FileInfo(zipPath).Length;
            FileDownloadTotalBytes = FileDownloadBytes;
        }
        else
        {
            // 下载文件，带进度回调
            zipPath = await DownloadCacheService.DownloadAndCacheAsync(
                cacheKey,
                downloadUrl,
                progressCallback: progress =>
                {
                    // progress 是 0.0-1.0 的值，转换为百分比
                    FileDownloadProgress = progress * 100;
                    // 计算已下载字节数（假设总大小约 1MB，实际大小会在下载完成后更新）
                    if (FileDownloadTotalBytes == 0)
                    {
                        FileDownloadTotalBytes = 1024 * 1024; // 默认 1MB
                    }
                    FileDownloadBytes = (long)(FileDownloadTotalBytes * progress);
                }
            );

            // 下载完成后更新实际文件大小
            if (File.Exists(zipPath))
            {
                var fileInfo = new FileInfo(zipPath);
                FileDownloadBytes = fileInfo.Length;
                FileDownloadTotalBytes = fileInfo.Length;
                FileDownloadProgress = 100;
            }
        }

        // 解压到 Mods 目录
        if (!Directory.Exists(_targetModsPath))
        {
            Directory.CreateDirectory(_targetModsPath);
        }

        await Task.Run(() =>
        {
            using var zipFile = new ZipFile(zipPath);
            foreach (ZipEntry entry in zipFile)
            {
                if (entry.IsDirectory)
                    continue;

                var destinationPath = Path.Combine(_targetModsPath, entry.Name.Replace('/', Path.DirectorySeparatorChar));
                var destinationDir = Path.GetDirectoryName(destinationPath);

                if (!string.IsNullOrEmpty(destinationDir) && !Directory.Exists(destinationDir))
                {
                    Directory.CreateDirectory(destinationDir);
                }

                using var stream = zipFile.GetInputStream(entry);
                using var fileStream = File.Create(destinationPath);
                stream.CopyTo(fileStream);
            }
        });
    }

    private async Task ProcessOverridesAsync()
    {
        if (_manifest == null || string.IsNullOrEmpty(_manifest.Overrides))
        {
            return;
        }

        StatusMessage = "正在处理 overrides...";

        var overridesPath = Path.Combine(_extractDir!, _manifest.Overrides);
        if (!Directory.Exists(overridesPath))
        {
            Log.Warn($"[LocalCurseforgeModpack] Overrides 目录不存在: {overridesPath}");
            return;
        }

        // 复制 overrides 内容到目标目录
        await Task.Run(() =>
        {
            CopyDirectory(overridesPath, _targetModsPath, true);
        });

        Log.Info($"[LocalCurseforgeModpack] Overrides 处理完成");
    }

    private void CopyDirectory(string sourceDir, string targetDir, bool overwrite)
    {
        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var fileName = Path.GetFileName(file);
            var targetFile = Path.Combine(targetDir, fileName);
            File.Copy(file, targetFile, overwrite);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(dir);
            var targetSubDir = Path.Combine(targetDir, dirName);
            CopyDirectory(dir, targetSubDir, overwrite);
        }
    }

    private async Task SaveInstanceInfoAsync()
    {
        var versionIconPath = TrySavePackIconToVersionDirectory();

        // 获取现有实例列表
        var existingInstances = SettingsService.LoadInstances();

        // 检查是否已存在同名实例
        var existingInstance = existingInstances.FirstOrDefault(i => i.Name.Equals(_instanceName, StringComparison.OrdinalIgnoreCase));
        if (existingInstance != null)
        {
            // 更新现有实例
            existingInstance.IsSMAPIInstance = true;
            existingInstance.EnableIsolation = true;
            if (!string.IsNullOrEmpty(versionIconPath))
            {
                existingInstance.CustomIcon = versionIconPath;
            }
        }
        else
        {
            // 创建新实例配置
            var newInstance = new GamePathInfo
            {
                Id = Guid.NewGuid().ToString(),
                Name = _instanceName,
                GamePath = _gameBasePath,
                Version = "1.6.0", // 默认版本
                IsSMAPIInstance = true,
                EnableIsolation = true,
                CustomIcon = versionIconPath
            };

            existingInstances.Add(newInstance);
        }

        // 保存实例配置
        SettingsService.SaveInstances(existingInstances);
        Log.Info($"[LocalCurseforgeModpack] 实例信息已保存: {_instanceName}");

        await Task.CompletedTask;
    }

    private string? TrySavePackIconToVersionDirectory()
    {
        try
        {
            if (string.IsNullOrEmpty(_extractDir) || !Directory.Exists(_extractDir) || string.IsNullOrEmpty(_versionRootPath))
                return null;

            var candidateNames = new[]
            {
                "modpack-icon.png", "modpack-icon.jpg", "modpack-icon.jpeg", "modpack-icon.gif",
                "icon.png", "icon.jpg", "icon.jpeg", "icon.gif",
                "logo.png", "logo.jpg", "logo.jpeg", "logo.gif",
                "thumbnail.png", "thumbnail.jpg", "thumbnail.jpeg", "thumbnail.gif",
                "cover.png", "cover.jpg", "cover.jpeg", "cover.gif"
            };

            string? sourceIconPath = null;
            foreach (var candidate in candidateNames)
            {
                sourceIconPath = Directory.GetFiles(_extractDir, candidate, SearchOption.AllDirectories).FirstOrDefault();
                if (!string.IsNullOrEmpty(sourceIconPath))
                    break;
            }

            if (string.IsNullOrEmpty(sourceIconPath) || !File.Exists(sourceIconPath))
                return null;

            Directory.CreateDirectory(_versionRootPath);
            var ext = Path.GetExtension(sourceIconPath);
            if (string.IsNullOrEmpty(ext))
                ext = ".png";

            var targetPath = Path.Combine(_versionRootPath, $".svl-instance-icon{ext}");
            File.Copy(sourceIconPath, targetPath, true);
            Log.Info($"[LocalCurseforgeModpack] 已保存整合包图标到版本目录: {targetPath}");
            return targetPath;
        }
        catch (Exception ex)
        {
            Log.Warn($"[LocalCurseforgeModpack] 保存整合包图标失败: {ex.Message}");
            return null;
        }
    }

    private async Task CleanupAsync()
    {
        try
        {
            // 清理已创建的版本目录
            if (_versionDirectoryCreated && !string.IsNullOrEmpty(_versionRootPath) && Directory.Exists(_versionRootPath))
            {
                var files = Directory.GetFiles(_versionRootPath, "*", SearchOption.AllDirectories);
                if (files.Length == 0)
                {
                    Directory.Delete(_versionRootPath, true);
                    Log.Info($"[LocalCurseforgeModpack] 已清理版本目录: {_versionRootPath}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[LocalCurseforgeModpack] 清理版本目录失败: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    private void CleanupTempDirectory()
    {
        try
        {
            if (!string.IsNullOrEmpty(_extractDir) && Directory.Exists(_extractDir))
            {
                Directory.Delete(_extractDir, true);
                Log.Info($"[LocalCurseforgeModpack] 已清理临时目录: {_extractDir}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[LocalCurseforgeModpack] 清理临时目录失败: {ex.Message}");
        }
    }

    public override void Cancel()
    {
        _cts.Cancel();
    }
}
