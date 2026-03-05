using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SVL.Core.Config;
using SVL.Core.IO;
using SVL.Core.Logging;
using SVL.Core.Modpack;
using SVL.Core.Stardew.Instance;
using SVL.Core.Stardew.Mod.SMAPI;

namespace SVL.Core.Download;

/// <summary>
/// 失败的模组信息（支持多平台）
/// </summary>
public class FailedModInfo
{
    /// <summary>
    /// 平台类型（Curseforge, NexusMods）
    /// </summary>
    public string Platform { get; set; } = "Curseforge";

    /// <summary>
    /// 模组名称
    /// </summary>
    public string ModName { get; set; } = string.Empty;

    /// <summary>
    /// 项目 ID（Curseforge ProjectId 或 NexusMods ModId）
    /// </summary>
    public long ProjectId { get; set; }

    /// <summary>
    /// 文件 ID
    /// </summary>
    public long FileId { get; set; }

    /// <summary>
    /// 错误信息
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// 游戏域名（用于 NexusMods）
    /// </summary>
    public string GameDomain { get; set; } = "stardewvalley";

    /// <summary>
    /// ZIP 文件路径（用于手动解压）
    /// </summary>
    public string? ZipFilePath { get; set; }

    /// <summary>
    /// 获取模组 URL
    /// </summary>
    public string ModUrl => Platform.ToLower() switch
    {
        "nexusmods" or "nexus" => $"https://www.nexusmods.com/{GameDomain}/mods/{ProjectId}",
        _ => $"https://www.curseforge.com/stardewvalley/mods/{ProjectId}"
    };
}

/// <summary>
/// Curseforge 整合包下载任务
/// 流程：
/// 1. 下载整合包 ZIP 文件
/// 2. 解析 manifest.json
/// 3. 安装最新版 SMAPI
/// 4. 从 manifest.json 安装模组
/// 5. 处理 overrides 中的 Mods 文件夹
/// </summary>
public class CurseforgeModpackDownloadTask : DownloadTask
{
    private readonly string _modpackName;
    private readonly string _fileName;
    private readonly string _gameBasePath;
    private readonly string _instanceName;
    private readonly string _targetModsPath;
    private readonly int _projectId;
    private readonly int _fileId;
    private readonly string? _directDownloadUrl;
    private readonly CancellationTokenSource _cts = new();

    private string? _downloadedFilePath;
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
    /// 活跃的子MOD任务列表（用于取消时等待）
    /// </summary>
    private readonly System.Collections.Generic.List<Task> _activeChildTasks = new();

    // 失败的模组列表
    public List<FailedModInfo> FailedMods { get; } = new();

    /// <summary>
    /// Mod 列表（用于 UI 显示下载进度）
    /// </summary>
    public ObservableCollection<CurseforgeModDownloadItem> ModList { get; } = new();

    /// <summary>
    /// 当前正在下载的 Mod
    /// </summary>
    public CurseforgeModDownloadItem? CurrentMod { get; private set; }

    public CurseforgeModpackDownloadTask(
        string modpackName,
        string fileName,
        int projectId,
        int fileId,
        string gameBasePath,
        string instanceName,
        string targetModsPath,
        string? directDownloadUrl = null)
    {
        _modpackName = modpackName;
        _fileName = fileName;
        _projectId = projectId;
        _fileId = fileId;
        _gameBasePath = gameBasePath;
        _instanceName = instanceName;
        _targetModsPath = targetModsPath;
        _directDownloadUrl = directDownloadUrl;

        Type = DownloadTaskType.Modpack;
        Name = $"整合包: {modpackName}";
        StatusMessage = "准备下载整合包...";

        if (!string.IsNullOrWhiteSpace(_directDownloadUrl))
        {
            Log.Info($"[CurseforgeModpackDownload] 已提供直接下载链接作为备选方案");
        }
    }

    public override async Task ExecuteAsync()
    {
        try
        {
            Status = DownloadTaskStatus.Downloading;
            StatusMessage = $"正在获取下载链接...";
            Progress = 0;

            Log.Info($"[CurseforgeModpackDownload] 开始下载整合包: {_modpackName} (ProjectId: {_projectId}, FileId: {_fileId})");

            // 1. 检查 API Key
            var apiKey = CurseforgeApiService.GetApiKey();
            if (string.IsNullOrEmpty(apiKey))
            {
                throw new Exception("Curseforge API Key 未配置，请在设置中配置 API Key");
            }

            // 2. 通过 API 获取真实下载链接
            StatusMessage = "正在获取下载链接...";
            Progress = 5;

            var downloadUrl = await GetDownloadUrlFromApiAsync(apiKey);
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                throw new Exception("无法从 Curseforge API 获取下载链接");
            }

            Log.Info($"[CurseforgeModpackDownload] 获取到下载链接: {downloadUrl}");
            Progress = 10;

            // 记录版本根路径（用于取消/失败时清理空目录）
            _versionRootPath = Path.GetDirectoryName(_targetModsPath) ?? _targetModsPath;
            Log.Info($"[CurseforgeModpackDownload] 版本根路径: {_versionRootPath}");

            // 3. 检查版本名是否重复（不创建目录，让 SMAPI 负责）
            if (Directory.Exists(_versionRootPath))
            {
                Log.Error($"[CurseforgeModpackDownload] 版本目录已存在: {_versionRootPath}");
                throw new Exception($"版本名称 '{_instanceName}' 已存在，请使用不同的名称安装整合包");
            }

            // 版本名检查通过，但不创建目录（由 SMAPI 安装任务负责）
            // 设置标志为 true，以便在取消/失败时可以清理
            _versionDirectoryCreated = true;
            Log.Info($"[CurseforgeModpackDownload] 版本名检查通过，目录将由 SMAPI 安装任务创建");

            // 4. 使用缓存服务下载整合包 ZIP 文件
            StatusMessage = $"正在下载整合包...";

            var cacheKey = DownloadCacheService.GenerateCacheKey("modpack", $"{_projectId}_{_fileId}", _fileName);
            Log.Info($"[CurseforgeModpackDownload] 缓存 Key: {cacheKey}");

            try
            {
                // 尝试从缓存获取
                var cachedPath = DownloadCacheService.GetCachedFile(cacheKey, minFileSize: 1024 * 1024); // 至少 1MB

                if (cachedPath != null)
                {
                    Log.Info($"[CurseforgeModpackDownload] 使用缓存的整合包: {cachedPath}");
                    _downloadedFilePath = cachedPath;
                    StatusMessage = "正在使用缓存...";
                    Progress = 30;
                }
                else
                {
                    // 缓存未命中，下载整合包
                    Log.Info($"[CurseforgeModpackDownload] 缓存未命中，开始下载整合包");

                    // 准备临时目录
                    var tempDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "SVL",
                        "temp",
                        "modpacks"
                    );

                    if (!Directory.Exists(tempDir))
                    {
                        Directory.CreateDirectory(tempDir);
                    }

                    var safeFileName = string.Join("_", _fileName.Split(Path.GetInvalidFileNameChars()));
                    var zipFilePath = Path.Combine(tempDir, safeFileName);

                    // 下载整合包 ZIP 文件
                    await DownloadFileFromUrlAsync(downloadUrl, zipFilePath);

                    // 保存到缓存
                    await DownloadCacheService.SaveToCacheAsync(cacheKey, zipFilePath);
                    Log.Info($"[CurseforgeModpackDownload] 已保存整合包到缓存: {cacheKey}");

                    _downloadedFilePath = zipFilePath;
                    Progress = 30;
                }

                Log.Info($"[CurseforgeModpackDownload] 整合包文件就绪: {_downloadedFilePath}");
            }
            catch (Exception ex)
            {
                // 下载失败，清除可能的不完整缓存
                DownloadCacheService.ClearCache(cacheKey);
                throw new Exception($"下载整合包失败: {ex.Message}", ex);
            }

            // 5. 解析整合包 manifest
            Status = DownloadTaskStatus.Installing;
            StatusMessage = "正在解析整合包 manifest...";
            Progress = 35;

            _manifest = CurseforgeModpackParser.Parse(_downloadedFilePath);
            if (_manifest == null)
            {
                throw new InvalidOperationException("无法解析整合包 manifest.json，可能不是有效的 Curseforge 整合包");
            }

            Log.Info($"[CurseforgeModpackDownload] 解析成功: {_manifest.Name} v{_manifest.Version}, 包含 {_manifest.Files?.Count ?? 0} 个模组");

            // 6. 解压整合包到临时目录
            StatusMessage = "正在解压整合包...";
            Progress = 40;

            _extractDir = CurseforgeModpackParser.ExtractToTemp(_downloadedFilePath);

            try
            {
                // 7. 步骤 1: 安装最新版 SMAPI
                await InstallSMAPIAsync(apiKey);

                // 8. 步骤 2: 从 manifest.json 安装模组
                await InstallModsFromManifestAsync(apiKey);

                // 9. 步骤 3: 处理 overrides 中的内容
                await ProcessOverridesAsync();

                // 10. 步骤 4: 获取整合包图标并创建实例配置
                await FetchModpackIconAndCreateInstanceAsync(apiKey);

                // 11. 完成
                Progress = 100;
                Status = DownloadTaskStatus.Completed;
                CompletedTime = DateTime.Now;

                StatusMessage = $"✓ 整合包安装完成: {_manifest.Name} v{_manifest.Version}";

                Log.Info($"[CurseforgeModpackDownload] ✓ 整合包安装完成: {_modpackName}");
            }
            finally
            {
                // 清理临时目录
                try
                {
                    if (Directory.Exists(_extractDir))
                    {
                        Directory.Delete(_extractDir, true);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("[CurseforgeModpackDownload] 清理临时目录失败", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Status = DownloadTaskStatus.Cancelled;
            StatusMessage = "已取消";
            Log.Info($"[CurseforgeModpackDownload] 下载已取消: {_modpackName}");

            // 取消时清理空版本目录
            CleanupVersionRootDirectory();
        }
        catch (Exception ex)
        {
            Status = DownloadTaskStatus.Failed;
            StatusMessage = $"错误: {ex.Message}";
            CompletedTime = DateTime.Now;
            Log.Error(ex, $"[CurseforgeModpackDownload] 下载失败: {_modpackName}");

            // 失败时清理空版本目录
            CleanupVersionRootDirectory();

            throw;
        }
    }

    /// <summary>
    /// 步骤 1: 安装最新版 SMAPI
    /// </summary>
    private async Task InstallSMAPIAsync(string apiKey)
    {
        Log.Info("[CurseforgeModpackDownload] 步骤 1/3: 安装 SMAPI");

        // 进度范围: 45% -> 60%
        Progress = 45;
        StatusMessage = "正在准备安装 SMAPI...";

        // 获取 SMAPI 最新版本
        var smapiFiles = await CurseforgeApiService.GetSmapifiFilesAsync(0, 5);
        if (smapiFiles == null || smapiFiles.Count == 0)
        {
            Log.Warn("[CurseforgeModpackDownload] 无法获取 SMAPI 文件列表，跳过 SMAPI 安装");
            return;
        }

        // 获取最新版本的 SMAPI
        var latestSmapi = smapiFiles.FirstOrDefault();
        if (latestSmapi == null)
        {
            Log.Warn("[CurseforgeModpackDownload] 无法找到 SMAPI 文件，跳过 SMAPI 安装");
            return;
        }

        // 解析并清理 SMAPI 版本名（去除重复前缀）
        var smapiDisplayName = CurseforgeHelper.ParseSmapiDisplayName(latestSmapi.DisplayName, latestSmapi.FileName);
        Log.Info($"[CurseforgeModpackDownload] 找到最新版 SMAPI: {smapiDisplayName} (原始：{latestSmapi.DisplayName}, FileId: {latestSmapi.Id})");

        // 使用缓存服务下载 SMAPI（使用清理后的 displayName）
        var smapiCacheKey = DownloadCacheService.GenerateCacheKey("smapi", latestSmapi.Id.ToString(), smapiDisplayName);
        string? smapiZipPath = null;

        try
        {
            // 尝试从缓存获取
            smapiZipPath = DownloadCacheService.GetCachedFile(smapiCacheKey, minFileSize: 1024 * 1024); // 至少 1MB

            if (smapiZipPath == null)
            {
                // 缓存未命中，下载 SMAPI
                Log.Info($"[CurseforgeModpackDownload] SMAPI 缓存未命中，开始下载");

                // 获取 SMAPI 下载链接
                var smapiDownloadUrl = await CurseforgeApiService.GetFileDownloadUrlAsync(898372, latestSmapi.Id);
                if (string.IsNullOrWhiteSpace(smapiDownloadUrl))
                {
                    Log.Warn("[CurseforgeModpackDownload] 无法获取 SMAPI 下载链接，跳过 SMAPI 安装");
                    return;
                }

                Log.Info($"[CurseforgeModpackDownload] 下载 SMAPI: {smapiDownloadUrl}");

                // 使用缓存服务下载
                smapiZipPath = await DownloadCacheService.DownloadAndCacheAsync(
                    smapiCacheKey,
                    smapiDownloadUrl,
                    progressCallback: progress => { }
                );
            }
            else
            {
                Log.Info($"[CurseforgeModpackDownload] 使用 SMAPI 缓存: {smapiZipPath}");
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

        Progress = 50;
        StatusMessage = "正在安装 SMAPI...";

        await smapiTask.ExecuteAsync();

        Progress = 60;
        StatusMessage = "SMAPI 安装完成";
        Log.Info($"[CurseforgeModpackDownload] SMAPI 安装完成");
        }
        catch (Exception ex)
        {
            Log.Warn($"[CurseforgeModpackDownload] SMAPI 下载或安装失败", ex);
            throw;
        }
    }

    /// <summary>
    /// 步骤 2: 从 manifest.json 安装模组（支持多线程并发下载）
    /// </summary>
    private async Task InstallModsFromManifestAsync(string apiKey)
    {
        Log.Info("[CurseforgeModpackDownload] 步骤 2/3: 安装 manifest 中的模组");

        if (_manifest?.Files == null || _manifest.Files.Count == 0)
        {
            Log.Info("[CurseforgeModpackDownload] 没有需要安装的模组");
            Progress = 85;
            return;
        }

        // 计算需要安装的模组数量（排除 SMAPI 和可选模组）
        var requiredFiles = _manifest.Files
            .Where(f => f.ProjectId != 898372 && f.Required)
            .ToList();

        int totalRequiredMods = requiredFiles.Count;
        int skippedCount = _manifest.Files.Count(f => f.ProjectId == 898372 || !f.Required);

        Log.Info($"[CurseforgeModpackDownload] 需要安装 {totalRequiredMods} 个必需模组（跳过 {skippedCount} 个）");

        if (totalRequiredMods == 0)
        {
            Log.Info("[CurseforgeModpackDownload] 没有需要安装的必需模组");
            Progress = 85;
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
                Name = $"Project {file.ProjectId}",
                Required = file.Required,
                Status = CurseforgeModDownloadStatus.Pending
            });
        }

        // 从配置读取并发下载数（默认 3，范围 1-10）
        var settings = AppConfig.GetSettings();
        var maxConcurrentDownloads = Math.Max(1, Math.Min(10, settings.MaxConcurrentModDownloads));
        Log.Info($"[CurseforgeModpackDownload] 使用并发下载数: {maxConcurrentDownloads}");

        // 并发控制
        using var downloadSemaphore = new SemaphoreSlim(maxConcurrentDownloads, maxConcurrentDownloads);

        // 用于线程安全的结果收集
        var installedMods = new System.Collections.Concurrent.ConcurrentBag<long>();
        var failedMods = new System.Collections.Concurrent.ConcurrentBag<(long projectId, string error)>();

        // 进度跟踪
        int completedCount = 0;
        var progressLock = new object();

        // 进度范围: 60% -> 85%
        const double progressStart = 60;
        const double progressEnd = 85;
        double progressRange = progressEnd - progressStart;

        StatusMessage = $"正在准备下载 {totalRequiredMods} 个模组（并发 {maxConcurrentDownloads}）...";

        // 并发下载所有模组
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

                // 获取模组下载链接
                var modDownloadUrl = await CurseforgeApiService.GetFileDownloadUrlAsync((int)file.ProjectId, (int)file.FileId);
                if (string.IsNullOrWhiteSpace(modDownloadUrl))
                {
                    if (modItem != null) modItem.Status = CurseforgeModDownloadStatus.Failed;
                    failedMods.Add((file.ProjectId, "无法获取下载链接"));
                    Log.Warn($"[CurseforgeModpackDownload] 无法获取模组下载链接: ProjectId={file.ProjectId}, FileId={file.FileId}");
                    return;
                }

                // 从 URL 获取文件名并更新模组列表中的名称
                try
                {
                    var urlFileName = Path.GetFileName(new Uri(modDownloadUrl).LocalPath);
                    if (modItem != null && !string.IsNullOrWhiteSpace(urlFileName))
                    {
                        var modName = urlFileName;
                        if (modName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                            modName = modName.Substring(0, modName.Length - 4);
                        modItem.Name = modName;
                    }
                }
                catch { /* 忽略 URL 解析错误 */ }

                // 创建模组下载任务，传递整合包的取消令牌
                var modTask = new ModDownloadTask(
                    modId: file.ProjectId.ToString(),
                    modName: $"Mod_{file.ProjectId}",
                    fileName: $"mod_{file.ProjectId}_{file.FileId}.zip",
                    downloadUrl: modDownloadUrl,
                    gameBasePath: _gameBasePath,
                    targetModsPath: _targetModsPath,
                    saveOnly: false,
                    sourcePlatform: "Curseforge",
                    sourceProjectId: file.ProjectId.ToString(),
                    sourceFileId: file.FileId.ToString(),
                    parentCancellationToken: _cts.Token
                );

                // 执行下载和安装
                await modTask.ExecuteAsync();
                installedMods.Add(file.ProjectId);

                // 更新模组状态为完成
                if (modItem != null) modItem.Status = CurseforgeModDownloadStatus.Completed;

                Log.Info($"[CurseforgeModpackDownload] 模组安装成功: ProjectId={file.ProjectId}");
            }
            catch (OperationCanceledException)
            {
                // 任务被取消，不记录为失败
                Log.Info($"[CurseforgeModpackDownload] 模组任务被取消: ProjectId={file.ProjectId}");
            }
            catch (Exception ex)
            {
                // 更新模组状态为失败
                var modItem = ModList.FirstOrDefault(m => m.ProjectId == file.ProjectId && m.FileId == file.FileId);
                if (modItem != null) modItem.Status = CurseforgeModDownloadStatus.Failed;

                failedMods.Add((file.ProjectId, ex.Message));
                Log.Warn($"[CurseforgeModpackDownload] 模组安装失败: ProjectId={file.ProjectId}", ex);
            }
            finally
            {
                downloadSemaphore.Release();

                // 更新进度
                lock (progressLock)
                {
                    completedCount++;
                    double currentProgress = progressStart + (completedCount / (double)totalRequiredMods) * progressRange;
                    Progress = currentProgress;
                    StatusMessage = $"正在安装模组 ({completedCount}/{totalRequiredMods})...";
                }
            }
        }).ToList();

        // 跟踪所有子任务
        lock (_activeChildTasks)
        {
            _activeChildTasks.Clear();
            _activeChildTasks.AddRange(downloadTasks);
        }

        await Task.WhenAll(downloadTasks);

        // 清空子任务列表
        lock (_activeChildTasks)
        {
            _activeChildTasks.Clear();
        }

        Progress = 85;
        StatusMessage = $"模组安装完成: 成功 {installedMods.Count}, 跳过 {skippedCount}, 失败 {failedMods.Count}";
        Log.Info($"[CurseforgeModpackDownload] 模组安装完成: 成功 {installedMods.Count}, 跳过 {skippedCount}, 失败 {failedMods.Count}");

        // 如果有失败的模组，记录详细信息并添加到 FailedMods 列表
        if (failedMods.Any())
        {
            Log.Warn($"[CurseforgeModpackDownload] 失败的模组列表:");
            foreach (var (projectId, error) in failedMods)
            {
                Log.Warn($"  - ProjectId: {projectId}, 错误: {error}");

                // 找到对应的 FileId
                var file = requiredFiles.FirstOrDefault(f => f.ProjectId == projectId);
                FailedMods.Add(new FailedModInfo
                {
                    Platform = "Curseforge",
                    ModName = $"Mod_{projectId}",  // Curseforge manifest 没有包含 mod 名称
                    ProjectId = projectId,
                    FileId = file?.FileId ?? 0,
                    Error = error,
                    GameDomain = "stardewvalley"
                });
            }
        }
    }

    /// <summary>
    /// 步骤 3: 处理 overrides 中的内容
    /// </summary>
    private async Task ProcessOverridesAsync()
    {
        StatusMessage = "正在处理覆盖文件...";
        Progress = 90;

        Log.Info("[CurseforgeModpackDownload] 步骤 3/3: 处理 overrides 内容");

        if (_extractDir == null)
        {
            Log.Warn("[CurseforgeModpackDownload] 解压目录为空，跳过 overrides 处理");
            return;
        }

        var overridesRoot = string.IsNullOrWhiteSpace(_manifest?.Overrides)
            ? Path.Combine(_extractDir, "overrides")
            : Path.Combine(_extractDir, _manifest.Overrides);

        if (!Directory.Exists(overridesRoot))
        {
            Log.Info("[CurseforgeModpackDownload] overrides 目录不存在，跳过");
            return;
        }

        // 计算版本根路径（_targetModsPath 的父目录）
        var versionRootPath = Path.GetDirectoryName(_targetModsPath) ?? _targetModsPath;
        Log.Info($"[CurseforgeModpackDownload] 版本根路径: {versionRootPath}");

        int copiedFiles = 0;

        // 将所有 overrides 内容复制到版本根路径
        copiedFiles = CopyDirectoryContent(overridesRoot, versionRootPath, _cts.Token);

        Log.Info($"[CurseforgeModpackDownload] 已复制 {copiedFiles} 个覆盖文件到版本隔离目录");
    }

    /// <summary>
    /// 步骤 4: 获取整合包图标并创建实例配置
    /// </summary>
    private async Task FetchModpackIconAndCreateInstanceAsync(string apiKey)
    {
        StatusMessage = "正在获取整合包信息...";
        Progress = 92;

        Log.Info("[CurseforgeModpackDownload] 步骤 4: 获取整合包图标并创建实例");

        string? customIconPath = null;

        try
        {
            // 获取整合包详情（包含图标 URL）
            var modpackInfo = await CurseforgeApiService.GetModInfoAsync(_projectId);
            if (modpackInfo?.Logo != null)
            {
                // 确定要使用的图标 URL
                string iconUrl = modpackInfo.Logo.Url;

                // 如果原始 URL 是 GIF 格式，尝试使用缩略图
                if (!string.IsNullOrEmpty(modpackInfo.Logo.ThumbnailUrl) &&
                    modpackInfo.Logo.Url.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Info($"[CurseforgeModpackDownload] 检测到 GIF 图标，使用缩略图: {modpackInfo.Logo.ThumbnailUrl}");
                    iconUrl = modpackInfo.Logo.ThumbnailUrl;
                }
                else
                {
                    Log.Info($"[CurseforgeModpackDownload] 找到整合包图标: {iconUrl}");
                }

                // 下载并缓存图标
                var cachedIconPath = await ImageCacheService.DownloadAndCacheImageAsync(iconUrl);
                if (!string.IsNullOrEmpty(cachedIconPath) && File.Exists(cachedIconPath))
                {
                    customIconPath = SaveIconToVersionDirectory(cachedIconPath);
                    Log.Info($"[CurseforgeModpackDownload] 图标已保存: {customIconPath}");
                }
                else
                {
                    Log.Warn("[CurseforgeModpackDownload] 图标缓存失败，使用默认图标");
                }
            }
            else
            {
                Log.Warn("[CurseforgeModpackDownload] 未找到整合包图标");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("[CurseforgeModpackDownload] 获取整合包图标失败，使用默认图标", ex);
        }

        Progress = 95;
        StatusMessage = "正在创建实例配置...";

        // 创建实例配置
        try
        {
            // 获取游戏版本
            var gameVersion = GamePathService.GetGameVersion(_gameBasePath);

            // 创建新实例配置
            var newInstance = new GamePathInfo
            {
                Id = Guid.NewGuid().ToString(),
                Name = _instanceName,
                GamePath = _gameBasePath,
                Version = gameVersion,
                IsSMAPIInstance = true,  // 整合包包含 SMAPI
                SMAPIVersion = "未知",  // 可以从已安装的 SMAPI 检测
                HasSMAPIInstalled = true,
                EnableIsolation = true,
                CustomIcon = customIconPath  // 设置自定义图标
            };

            // 尝试获取实际安装的 SMAPI 版本
            try
            {
                var versionRootPath = Path.GetDirectoryName(_targetModsPath) ?? _targetModsPath;
                var actualSmapiVersion = SmapApiService.GetInstalledSmapiVersion(versionRootPath);
                if (!string.IsNullOrEmpty(actualSmapiVersion))
                {
                    newInstance.SMAPIVersion = actualSmapiVersion;
                    Log.Info($"[CurseforgeModpackDownload] 检测到 SMAPI 版本: {actualSmapiVersion}");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("[CurseforgeModpackDownload] 检测 SMAPI 版本失败", ex);
            }

            // 加载现有实例列表
            var existingInstances = SettingsService.LoadInstances();
            Log.Info($"[CurseforgeModpackDownload] 当前有 {existingInstances.Count} 个实例");

            // 检查是否已存在同名实例
            var existingInstance = existingInstances.FirstOrDefault(i => i.Name == _instanceName);
            if (existingInstance != null)
            {
                Log.Info($"[CurseforgeModpackDownload] 更新现有实例: {_instanceName}");
                // 更新现有实例的图标
                existingInstance.CustomIcon = customIconPath;
                existingInstance.IsSMAPIInstance = true;
                existingInstance.HasSMAPIInstalled = true;
                existingInstance.EnableIsolation = true;
            }
            else
            {
                Log.Info($"[CurseforgeModpackDownload] 添加新实例: {_instanceName}");
                // 添加新实例
                existingInstances.Add(newInstance);
            }

            // 保存回 instances.json
            SettingsService.SaveInstances(existingInstances);
            Log.Info($"[CurseforgeModpackDownload] ✓ 实例配置已保存到 instances.json");
        }
        catch (Exception ex)
        {
            Log.Warn("[CurseforgeModpackDownload] 保存实例配置失败", ex);
            // 不抛出异常，因为整合包已安装成功
        }
    }

    private string? SaveIconToVersionDirectory(string sourceIconPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourceIconPath) || !File.Exists(sourceIconPath))
                return null;

            var versionRootPath = Path.GetDirectoryName(_targetModsPath) ?? _targetModsPath;
            Directory.CreateDirectory(versionRootPath);

            var ext = Path.GetExtension(sourceIconPath);
            if (string.IsNullOrWhiteSpace(ext))
                ext = ".png";

            var iconPath = Path.Combine(versionRootPath, $".svl-instance-icon{ext}");
            File.Copy(sourceIconPath, iconPath, true);
            return iconPath;
        }
        catch (Exception ex)
        {
            Log.Warn($"[CurseforgeModpackDownload] 保存图标到版本目录失败: {ex.Message}");
            return sourceIconPath;
        }
    }

    /// <summary>
    /// 通过 Curseforge API 获取下载链接（带完整 Fallback 链）
    /// </summary>
    private async Task<string?> GetDownloadUrlFromApiAsync(string apiKey)
    {
        Log.Info($"[CurseforgeModpackDownload] 解析下载 URL: ProjectId={_projectId}, FileId={_fileId}");

        var downloadUrl = await CurseforgeApiService.ResolveFileDownloadUrlAsync(
            _projectId,
            _fileId,
            _fileName,
            _directDownloadUrl);

        if (!string.IsNullOrWhiteSpace(downloadUrl))
        {
            Log.Info($"[CurseforgeModpackDownload] 成功获取下载链接: {downloadUrl.Substring(0, Math.Min(100, downloadUrl.Length))}...");
            return downloadUrl;
        }

        throw new Exception("无法获取 Curseforge 下载链接");
    }

    /// <summary>
    /// 从 URL 下载文件并报告进度（支持速度和大小显示）
    /// </summary>
    private async Task DownloadFileFromUrlAsync(string downloadUrl, string targetPath)
    {
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("User-Agent", "SVL-StardewValleyLauncher/1.0");

        // 如果是 Curseforge URL，添加 API Key（与 ModDownloadTask 保持一致）
        if (downloadUrl.Contains("curseforge.com"))
        {
            var apiKey = CurseforgeApiService.GetApiKey();
            if (!string.IsNullOrEmpty(apiKey))
            {
                httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
                Log.Info("[CurseforgeModpackDownload] 已添加 Curseforge API Key 到下载请求");
            }
            else
            {
                Log.Warn("[CurseforgeModpackDownload] Curseforge API Key 未配置，下载可能失败");
            }
        }

        httpClient.Timeout = TimeSpan.FromMinutes(30);

        Log.Info($"[CurseforgeModpackDownload] 开始下载: {downloadUrl}");

        var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? 0;

        using var fs = new FileStream(targetPath, FileMode.Create);
        using var stream = await response.Content.ReadAsStreamAsync();

        var buffer = new byte[8192];
        int bytesRead;
        long totalRead = 0;

        // 速度计算
        var startTime = DateTime.UtcNow;
        var lastUpdateTime = startTime;
        var lastUpdateBytes = 0L;
        const int updateIntervalMs = 500; // 每 500ms 更新一次显示

        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, _cts.Token)) > 0)
        {
            await fs.WriteAsync(buffer, 0, bytesRead, _cts.Token);
            totalRead += bytesRead;

            var now = DateTime.UtcNow;
            var elapsedMs = (int)(now - lastUpdateTime).TotalMilliseconds;

            if (elapsedMs >= updateIntervalMs && totalBytes > 0)
            {
                // 计算下载速度
                var totalElapsedSec = (now - startTime).TotalSeconds;
                var speed = totalElapsedSec > 0 ? totalRead / totalElapsedSec : 0;

                // 计算进度
                var currentProgress = 10 + (int)((double)totalRead / totalBytes * 20); // 10-30%
                Progress = currentProgress;

                // 格式化显示：百分比 + 已下载 / 总大小 (速度)
                var progressPercent = (double)totalRead / totalBytes * 100;
                var downloadedMB = totalRead / (1024.0 * 1024.0);
                var totalMB = totalBytes / (1024.0 * 1024.0);
                var speedMB = speed / (1024.0 * 1024.0);

                StatusMessage = $"正在下载整合包...\n{progressPercent:F2}%\t{downloadedMB:F1} MB / {totalMB:F1} MB ({speedMB:F1} MB/s)";

                lastUpdateTime = now;
                lastUpdateBytes = totalRead;
            }
        }

        // 下载完成，显示最终状态
        var finalMB = totalRead / (1024.0 * 1024.0);
        StatusMessage = $"正在下载整合包...\n100.00%\t{finalMB:F1} MB / {finalMB:F1} MB (完成)";
    }

    /// <summary>
    /// 复制目录内容
    /// </summary>
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

    public override void Cancel()
    {
        try
        {
            Log.Info($"[CurseforgeModpackDownload] 开始取消任务: {_modpackName}");
            _cts.Cancel();
            Status = DownloadTaskStatus.Cancelled;
            StatusMessage = "正在取消...";

            // 等待所有子MOD任务完成
            Task[] childTasks;
            lock (_activeChildTasks)
            {
                childTasks = _activeChildTasks.ToArray();
            }

            if (childTasks.Length > 0)
            {
                Log.Info($"[CurseforgeModpackDownload] 等待 {childTasks.Length} 个子MOD任务完成...");

                // 等待所有子任务完成（最多等待30秒）
                try
                {
                    Task.WaitAll(childTasks, TimeSpan.FromSeconds(30));
                    Log.Info($"[CurseforgeModpackDownload] 所有子MOD任务已完成");
                }
                catch (AggregateException ae)
                {
                    // 处理 WaitAll 的超时异常
                    bool allCompleted = true;
                    foreach (var ex in ae.InnerExceptions)
                    {
                        if (ex is TimeoutException)
                        {
                            Log.Warn($"[CurseforgeModpackDownload] 等待子任务超时，部分任务可能仍在运行");
                            allCompleted = false;
                        }
                        else if (ex is System.OperationCanceledException or System.Threading.Tasks.TaskCanceledException)
                        {
                            // 任务取消是正常流程，不需要记录为错误
                            Log.Info($"[CurseforgeModpackDownload] 子任务已取消");
                            allCompleted = true;
                        }
                        else
                        {
                            Log.Error(ex, "[CurseforgeModpackDownload] 等待子任务时发生其他错误");
                        }
                    }

                    if (!allCompleted)
                    {
                        Log.Warn($"[CurseforgeModpackDownload] 部分子任务未能在30秒内完成，将继续执行清理");
                    }
                }

                // 额外等待500ms，确保所有文件操作都完成
                System.Threading.Thread.Sleep(500);
            }

            // 删除可能不完整的缓存文件
            try
            {
                var cacheKey = DownloadCacheService.GenerateCacheKey("modpack", $"{_projectId}_{_fileId}");
                DownloadCacheService.ClearCache(cacheKey);
                Log.Info($"[CurseforgeModpackDownload] 已清除缓存: {cacheKey}");
            }
            catch (Exception ex)
            {
                Log.Warn($"[CurseforgeModpackDownload] 清除缓存失败: {ex.Message}");
            }

            // 清理可能创建的空版本目录
            CleanupVersionRootDirectory();

            Log.Info($"[CurseforgeModpackDownload] 取消任务完成: {_modpackName}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CurseforgeModpackDownload] 取消任务失败");
        }
    }

    /// <summary>
    /// 清理版本根目录（取消时删除所有内容，不论是否为空）
    /// </summary>
    private void CleanupVersionRootDirectory()
    {
        if (string.IsNullOrWhiteSpace(_versionRootPath))
            return;

        // 只有在我们创建了新版本目录时才清理
        if (!_versionDirectoryCreated)
        {
            Log.Info($"[CurseforgeModpackDownload] 版本目录非本任务创建，跳过清理: {_versionRootPath}");
            _versionRootPath = null;
            return;
        }

        try
        {
            if (Directory.Exists(_versionRootPath))
            {
                // 尝试多次删除，处理文件被占用的情况
                bool deleted = false;
                for (int i = 0; i < 5; i++)
                {
                    // 在每次重试前检查 _versionRootPath 是否为 null
                    if (string.IsNullOrWhiteSpace(_versionRootPath))
                    {
                        Log.Info($"[CurseforgeModpackDownload] 版本路径已被清空，跳过后续删除尝试");
                        break;
                    }

                    try
                    {
                        // 先尝试递归删除
                        Directory.Delete(_versionRootPath, true);
                        deleted = true;
                        Log.Info($"[CurseforgeModpackDownload] 已删除版本目录: {_versionRootPath}");
                        break;  // 删除成功，立即退出循环
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[CurseforceModpackDownload] 删除版本目录失败（第 {i + 1} 次尝试）: {ex.Message}");

                        // 检查是否是因为路径为 null
                        if (string.IsNullOrWhiteSpace(_versionRootPath))
                        {
                            Log.Warn($"[CurseforgeModpackDownload] 版本路径为 null，停止删除尝试");
                            break;
                        }

                        if (i < 4)
                        {
                            // 等待一小段时间后重试（每次增加等待时间）
                            System.Threading.Thread.Sleep(200 * (i + 1));
                        }
                        else
                        {
                            // 最后一次尝试：逐个删除文件和子目录
                            try
                            {
                                if (string.IsNullOrWhiteSpace(_versionRootPath))
                                {
                                    Log.Warn($"[CurseforgeModpackDownload] 版本路径为 null，跳过强制删除");
                                    break;
                                }

                                ForceDeleteDirectory(_versionRootPath);
                                deleted = true;
                                Log.Info($"[CurseforgeModpackDownload] 已强制删除版本目录: {_versionRootPath}");
                            }
                            catch (Exception forceEx)
                            {
                                Log.Error($"[CurseforgeModpackDownload] 强制删除版本目录也失败: {forceEx.Message}");

                                // 最后检查：如果目录仍然存在，记录警告
                                if (!string.IsNullOrWhiteSpace(_versionRootPath) && Directory.Exists(_versionRootPath))
                                {
                                    Log.Error($"[CurseforgeModpackDownload] 版本目录仍然存在，可能被版本选择检测到: {_versionRootPath}");
                                }
                            }
                        }
                    }
                }

                if (!deleted && !string.IsNullOrWhiteSpace(_versionRootPath))
                {
                    Log.Warn($"[CurseforgeModpackDownload] 无法删除版本目录: {_versionRootPath}");

                    // 最后检查：如果目录仍然存在，记录警告
                    if (Directory.Exists(_versionRootPath))
                    {
                        Log.Error($"[CurseforgeModpackDownload] 版本目录仍然存在，可能被版本选择检测到: {_versionRootPath}");
                    }
                }
            }
            else
            {
                Log.Info($"[CurseforgeModpackDownload] 版本目录不存在，无需清理: {_versionRootPath}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[CurseforgeModpackDownload] 清理版本目录失败: {ex.Message}");

            // 最后检查：如果目录仍然存在，记录警告
            if (!string.IsNullOrWhiteSpace(_versionRootPath) && Directory.Exists(_versionRootPath))
            {
                Log.Error($"[CurseforgeModpackDownload] 版本目录仍然存在，可能被版本选择检测到: {_versionRootPath}");
            }
        }
        finally
        {
            _versionRootPath = null;  // 清除引用，避免重复处理
        }
    }

    /// <summary>
    /// 强制删除目录及其内容（逐个删除文件）
    /// </summary>
    private void ForceDeleteDirectory(string path)
    {
        try
        {
            // 先删除所有文件
            var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                try
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // 忽略单个文件删除失败，继续删除其他文件
                }
            }

            // 再删除所有子目录
            var dirs = Directory.GetDirectories(path, "*", SearchOption.AllDirectories);
            // 按深度排序，先删除深层目录
            var sortedDirs = dirs.OrderByDescending(d => d.Count(c => c == Path.DirectorySeparatorChar));
            foreach (var dir in sortedDirs)
            {
                try
                {
                    if (Directory.Exists(dir))
                    {
                        Directory.Delete(dir, false);
                    }
                }
                catch
                {
                    // 忽略单个目录删除失败
                }
            }

            // 最后删除根目录
            if (Directory.Exists(path))
            {
                Directory.Delete(path, false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[CurseforgeModpackDownload] 强制删除目录失败: {path}, {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 获取下载的文件路径（用于外部访问）
    /// </summary>
    public string? DownloadedFilePath => _downloadedFilePath;
}
