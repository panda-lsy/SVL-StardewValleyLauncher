using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SVL.Core.Download.NexusMods;
using SVL.Core.Logging;
using SVL.Core.Stardew.Mod;

namespace SVL.Core.Download;

/// <summary>
/// MOD 批量更新项
/// </summary>
public class ModBatchUpdateItem
{
    /// <summary>
    /// MOD 名称
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 当前版本
    /// </summary>
    public string? CurrentVersion { get; set; }

    /// <summary>
    /// 最新版本
    /// </summary>
    public string? NewVersion { get; set; }

    /// <summary>
    /// 更新来源
    /// </summary>
    public string? Platform { get; set; }

    /// <summary>
    /// 更新状态
    /// </summary>
    public ModBatchUpdateStatus Status { get; set; } = ModBatchUpdateStatus.Pending;

    /// <summary>
    /// 错误信息
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 下载链接（用于浏览器下载）
    /// </summary>
    public string? DownloadUrl { get; set; }

    /// <summary>
    /// 原始 MOD 对象（内部使用）
    /// </summary>
    internal SdVMod? Mod { get; set; }
}

/// <summary>
/// MOD 批量更新状态
/// </summary>
public enum ModBatchUpdateStatus
{
    /// <summary>
    /// 等待更新
    /// </summary>
    Pending,

    /// <summary>
    /// 下载中
    /// </summary>
    Downloading,

    /// <summary>
    /// 等待浏览器下载
    /// </summary>
    WaitingBrowser,

    /// <summary>
    /// 安装中
    /// </summary>
    Installing,

    /// <summary>
    /// 成功
    /// </summary>
    Success,

    /// <summary>
    /// 失败
    /// </summary>
    Failed,

    /// <summary>
    /// 跳过
    /// </summary>
    Skipped
}

/// <summary>
/// MOD 批量更新任务
/// </summary>
public class ModBatchUpdateTask : DownloadTask
{
    private readonly List<SdVMod> _mods;
    private readonly string _modsPath;
    private readonly IModManager _modManager;
    private readonly CancellationTokenSource _cts = new();
    private ModBatchUpdateItem? _currentItem;  // 当前正在处理的 MOD

    /// <summary>
    /// 需要更新的 MOD 列表
    /// </summary>
    public List<ModBatchUpdateItem> ModList { get; } = new();

    /// <summary>
    /// 当前正在处理的 MOD
    /// </summary>
    public ModBatchUpdateItem? CurrentMod => _currentItem;

    public ModBatchUpdateTask(List<SdVMod> mods, string modsPath, IModManager modManager)
    {
        _mods = mods;
        _modsPath = modsPath;
        _modManager = modManager;

        Type = DownloadTaskType.Modpack; // 使用 Modpack 类型表示批量任务
        Name = "MOD 批量更新";
        Status = DownloadTaskStatus.Pending;
        StatusMessage = "准备更新...";
        Progress = 0;

        Log.Info($"[ModBatchUpdate] ========== 批量更新任务创建 ==========");
        Log.Info($"[ModBatchUpdate] 待检查 MOD 数量: {mods.Count}");
        Log.Info($"[ModBatchUpdate] MOD 路径: {modsPath}");
    }

    public override void Cancel()
    {
        Log.Warn("[ModBatchUpdate] 收到取消请求...");
        _cts.Cancel();
        Status = DownloadTaskStatus.Cancelled;
        StatusMessage = "已取消";
        Log.Warn("[ModBatchUpdate] 批量更新任务已取消");
    }

    public override async Task ExecuteAsync()
    {
        try
        {
            Log.Info($"[ModBatchUpdate] ========== 开始执行批量更新 ==========");
            Status = DownloadTaskStatus.Downloading;
            StatusMessage = "正在检查更新...";
            Progress = 0;

            // 第一步：检查所有 MOD 的更新
            Log.Info($"[ModBatchUpdate] [步骤 1/2] 开始检查 {_mods.Count} 个 MOD 的更新...");
            await CheckUpdatesAsync();

            if (_cts.IsCancellationRequested)
            {
                Log.Warn("[ModBatchUpdate] 用户取消操作");
                Status = DownloadTaskStatus.Cancelled;
                StatusMessage = "已取消";
                return;
            }

            if (ModList.Count == 0)
            {
                Log.Info("[ModBatchUpdate] 检查完成: 所有 MOD 均为最新版本");
                Status = DownloadTaskStatus.Completed;
                StatusMessage = "没有需要更新的 MOD";
                Progress = 100;
                CompletedTime = DateTime.Now;
                return;
            }

            Log.Info($"[ModBatchUpdate] ========== 发现 {ModList.Count} 个 MOD 有更新 ==========");
            Log.Info($"[ModBatchUpdate] ModList 已填充，现在 UI 应该能看到模组列表");
            Log.Info($"[ModBatchUpdate] ModList 内容: {string.Join(", ", ModList.Select(m => m.Name))}");

            // 第二步：下载并安装更新（按序处理，类似 NexusCollectionWizardTask）
            Status = DownloadTaskStatus.WaitingConfirmation;
            StatusMessage = $"准备更新 MOD (0/{ModList.Count})";
            Progress = 10;
            Log.Info($"[ModBatchUpdate] 状态变更为 WaitingConfirmation，StatusMessage={StatusMessage}, Progress={Progress}");

            // 处理第一个 MOD
            await ProcessNextModAsync();

            // ProcessNextModAsync 会处理所有 MOD，直到全部完成
            // 这里等待所有处理完成（通过检查状态或使用信号量）
            // 注意：由于非 Premium 用户需要等待浏览器操作，这里使用超时循环来检查完成状态
            while (Status == DownloadTaskStatus.WaitingConfirmation ||
                   Status == DownloadTaskStatus.Downloading ||
                   Status == DownloadTaskStatus.Installing)
            {
                if (_cts.IsCancellationRequested)
                {
                    Log.Warn("[ModBatchUpdate] 用户取消操作");
                    Status = DownloadTaskStatus.Cancelled;
                    StatusMessage = "已取消";
                    return;
                }

                // 检查是否所有 MOD 都已处理完成
                var allProcessed = ModList.All(m =>
                    m.Status == ModBatchUpdateStatus.Success ||
                    m.Status == ModBatchUpdateStatus.Failed ||
                    m.Status == ModBatchUpdateStatus.Skipped);

                if (allProcessed)
                {
                    break;
                }

                await Task.Delay(500, _cts.Token);
            }

            // 计算最终结果
            var completedCount = ModList.Count(m => m.Status == ModBatchUpdateStatus.Success);
            var failedCount = ModList.Count(m => m.Status == ModBatchUpdateStatus.Failed);
            var skippedCount = ModList.Count(m => m.Status == ModBatchUpdateStatus.Skipped);

            Status = DownloadTaskStatus.Completed;
            StatusMessage = $"更新完成: {completedCount} 成功, {failedCount} 失败, {skippedCount} 跳过";
            Progress = 100;
            CompletedTime = DateTime.Now;

            Log.Info($"[ModBatchUpdate] ========== 批量更新完成 ==========");
            Log.Info($"[ModBatchUpdate] 总计: {ModList.Count} 个 MOD");
            Log.Info($"[ModBatchUpdate] 成功: {completedCount} 个");
            Log.Info($"[ModBatchUpdate] 失败: {failedCount} 个");
            Log.Info($"[ModBatchUpdate] 跳过: {skippedCount} 个");
        }
        catch (Exception ex)
        {
            Status = DownloadTaskStatus.Failed;
            StatusMessage = $"更新失败: {ex.Message}";
            Log.Error(ex, "[ModBatchUpdate] 批量更新执行失败");
        }
    }

    /// <summary>
    /// 检查所有 MOD 的更新
    /// </summary>
    private async Task CheckUpdatesAsync()
    {
        Log.Info($"[ModBatchUpdate] 调用 ModManager.CheckModUpdatesAsync 检查 {_mods.Count} 个 MOD...");

        await _modManager.CheckModUpdatesAsync(_mods);

        Log.Info($"[ModBatchUpdate] 更新检查完成，开始收集需要更新的 MOD...");

        // 收集有更新的 MOD
        int skippedCount = 0;
        foreach (var mod in _mods)
        {
            if (mod.HasUpdate && !string.IsNullOrEmpty(mod.LatestVersion))
            {
                var updateItem = new ModBatchUpdateItem
                {
                    Mod = mod,
                    Name = mod.Name,
                    CurrentVersion = mod.Version,
                    NewVersion = mod.LatestVersion,
                    Platform = mod.UpdateSource,
                    DownloadUrl = mod.UpdateUrl,
                    Status = ModBatchUpdateStatus.Pending
                };
                ModList.Add(updateItem);

                Log.Debug($"[ModBatchUpdate] 发现更新: {mod.Name}");
                Log.Debug($"[ModBatchUpdate]   当前版本: {mod.Version} -> 最新版本: {mod.LatestVersion}");
                Log.Debug($"[ModBatchUpdate]   更新来源: {mod.UpdateSource}");
                if (!string.IsNullOrEmpty(mod.UpdateUrl))
                {
                    Log.Debug($"[ModBatchUpdate]   更新链接: {mod.UpdateUrl}");
                }
            }
            else
            {
                skippedCount++;
                Log.Debug($"[ModBatchUpdate] 无更新: {mod.Name} (版本: {mod.Version})");
            }
        }

        Log.Info($"[ModBatchUpdate] 检查完成: {ModList.Count} 个 MOD 有更新, {skippedCount} 个 MOD 已是最新版本");
        if (ModList.Count > 0)
        {
            Log.Info($"[ModBatchUpdate] ModList 填充完成，包含 {ModList.Count} 个更新:");
            foreach (var item in ModList)
            {
                Log.Info($"[ModBatchUpdate]   - {item.Name}: {item.CurrentVersion} -> {item.NewVersion} ({item.Platform})");
            }
        }
    }

    /// <summary>
    /// 处理单个 MOD 的更新（支持按序下载流程，类似 NexusCollectionWizardTask）
    /// </summary>
    private async Task<bool> ProcessModUpdateAsync(ModBatchUpdateItem item)
    {
        DownloadTask? downloadTask = null;
        try
        {
            var mod = item.Mod!;
            Log.Info($"[ModBatchUpdate] [{item.Name}] 开始处理更新...");

            // 根据 UpdateSource 确定下载方式
            if (item.Platform == "Curseforge")
            {
                // Curseforge 直接下载
                if (string.IsNullOrEmpty(mod.UpdateUrl))
                {
                    Log.Error($"[ModBatchUpdate] [{item.Name}] Curseforge UpdateUrl 为空");
                    item.ErrorMessage = "无法获取下载链接";
                    return false;
                }

                Log.Info($"[ModBatchUpdate] [{item.Name}] Curseforge 直接下载");

                var fileId = ExtractCurseforgeFileId(mod.UpdateUrl);
                if (string.IsNullOrEmpty(fileId))
                {
                    Log.Error($"[ModBatchUpdate] [{item.Name}] 无法从 UpdateUrl 提取 FileId");
                    item.ErrorMessage = "无法提取文件ID";
                    return false;
                }

                var fileName = $"{item.Name}-{item.NewVersion}.zip";

                item.Status = ModBatchUpdateStatus.Downloading;
                Status = DownloadTaskStatus.Downloading;
                StatusMessage = $"正在下载 {item.Name}...";

                downloadTask = new ModDownloadTask(
                    modId: mod.CurseforgeProjectId ?? "unknown",
                    modName: item.Name,
                    fileName: fileName,
                    downloadUrl: mod.UpdateUrl,
                    gameBasePath: null,
                    targetModsPath: _modsPath,
                    saveOnly: false,
                    sourcePlatform: "Curseforge",
                    sourceProjectId: mod.CurseforgeProjectId,
                    sourceFileId: fileId,
                    isModpack: false,
                    parentCancellationToken: _cts.Token
                );

                await DownloadManager.Instance.ExecuteInternalTaskAsync(downloadTask);
                await WaitForTaskAsync(downloadTask);

                if (downloadTask.Status != DownloadTaskStatus.Completed)
                {
                    item.ErrorMessage = downloadTask.StatusMessage ?? "下载失败";
                    return false;
                }

                Log.Info($"[ModBatchUpdate] [{mod.Name}] Curseforge 下载完成");
                return true;
            }
            else if (item.Platform == "NexusMods")
            {
                // NexusMods 需要检查是否支持直接下载
                if (string.IsNullOrEmpty(mod.UpdateUrl))
                {
                    Log.Error($"[ModBatchUpdate] [{item.Name}] NexusMods UpdateUrl 为空");
                    item.ErrorMessage = "无法获取下载链接";
                    return false;
                }

                var (projectId, fileId) = ParseNexusModsUrl(mod.UpdateUrl);
                if (projectId == null || fileId == null || !long.TryParse(projectId, out var modId) || !long.TryParse(fileId, out var fileIdLong))
                {
                    Log.Error($"[ModBatchUpdate] [{item.Name}] 无法解析 NexusMods URL");
                    item.ErrorMessage = "无法解析下载链接";
                    return false;
                }

                Log.Info($"[ModBatchUpdate] [{item.Name}] NexusMods: ProjectId={projectId}, FileId={fileId}");

                // 尝试直接下载
                var gameId = "stardewvalley";
                downloadTask = new NexusModsModDownloadTask(
                    gameId: gameId,
                    modId: modId,
                    fileId: fileIdLong,
                    modName: item.Name,
                    downloadDirectory: _modsPath
                );

                item.Status = ModBatchUpdateStatus.Downloading;
                Status = DownloadTaskStatus.Downloading;
                StatusMessage = $"正在下载 {item.Name}...";

                await DownloadManager.Instance.ExecuteInternalTaskAsync(downloadTask);
                await WaitForTaskAsync(downloadTask);

                if (downloadTask.Status == DownloadTaskStatus.Completed)
                {
                    // Premium 用户下载成功，需要安装
                    Log.Info($"[ModBatchUpdate] [{item.Name}] NexusMods 下载完成，开始安装...");

                    var downloadedFile = FindLatestDownloadedFile(_modsPath, item.Name, item.NewVersion);
                    if (downloadedFile == null)
                    {
                        Log.Error($"[ModBatchUpdate] [{item.Name}] 无法找到下载的文件");
                        item.ErrorMessage = "无法找到下载的文件";
                        return false;
                    }

                    Log.Info($"[ModBatchUpdate] [{item.Name}] 找到下载文件: {downloadedFile}");

                    var fileName = Path.GetFileName(downloadedFile);
                    var installTask = new ModDownloadTask(
                        modId: mod.NexusModsProjectId ?? "unknown",
                        modName: item.Name,
                        fileName: fileName,
                        localZipPath: downloadedFile,
                        isLocalFile: true,
                        gameBasePath: null,
                        targetModsPath: _modsPath,
                        saveOnly: false,
                        sourcePlatform: "NexusMods",
                        sourceProjectId: mod.NexusModsProjectId,
                        sourceFileId: null,
                        isModpack: false,
                        parentCancellationToken: _cts.Token
                    );

                    await DownloadManager.Instance.ExecuteInternalTaskAsync(installTask);
                    await WaitForTaskAsync(installTask);

                    return installTask.Status == DownloadTaskStatus.Completed;
                }
                else if (downloadTask.Status == DownloadTaskStatus.WaitingConfirmation)
                {
                    // 非 Premium 用户，NexusModsModDownloadTask 已经打开了浏览器
                    // 保持 WaitingConfirmation 状态，等待用户操作
                    Log.Warn($"[ModBatchUpdate] [{item.Name}] 等待用户在浏览器中下载文件...");

                    item.Status = ModBatchUpdateStatus.WaitingBrowser;
                    Status = DownloadTaskStatus.WaitingConfirmation;
                    StatusMessage = $"请在浏览器中下载 {item.Name}，然后点击「跳过」继续";

                    // 返回 false 表示未完成，等待用户操作后通过 ContinueInstallAfterDownloadAsync 继续
                    return false;
                }
                else
                {
                    // 下载失败（如 401 错误），设置为等待确认状态，允许用户重试或跳过
                    item.Status = ModBatchUpdateStatus.Failed;
                    item.ErrorMessage = downloadTask.StatusMessage ?? "下载失败";
                    Status = DownloadTaskStatus.WaitingConfirmation;
                    StatusMessage = $"下载失败: {item.Name} - {item.ErrorMessage}";
                    return false;
                }
            }
            else
            {
                Log.Error($"[ModBatchUpdate] [{item.Name}] 不支持的更新来源: {item.Platform}");
                item.Status = ModBatchUpdateStatus.Failed;
                item.ErrorMessage = $"不支持的更新来源: {item.Platform}";
                Status = DownloadTaskStatus.WaitingConfirmation;
                StatusMessage = $"不支持的来源: {item.Name}";
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            Log.Warn($"[ModBatchUpdate] [{item.Name}] 操作已取消");
            item.Status = ModBatchUpdateStatus.Failed;
            item.ErrorMessage = "操作已取消";
            downloadTask?.Cancel();
            Status = DownloadTaskStatus.WaitingConfirmation;
            StatusMessage = $"已取消: {item.Name}";
            return false;
        }
        catch (Exception ex)
        {
            item.Status = ModBatchUpdateStatus.Failed;
            item.ErrorMessage = ex.Message;
            Status = DownloadTaskStatus.WaitingConfirmation;
            StatusMessage = $"更新失败: {item.Name} - {ex.Message}";
            Log.Error(ex, $"[ModBatchUpdate] 更新失败: {item.Name}");
            return false;
        }
    }

    /// <summary>
    /// 等待任务完成
    /// </summary>
    private async Task WaitForTaskAsync(DownloadTask task)
    {
        while (task.Status == DownloadTaskStatus.Pending ||
               task.Status == DownloadTaskStatus.Downloading ||
               task.Status == DownloadTaskStatus.Installing)
        {
            if (_cts.IsCancellationRequested)
            {
                Log.Warn($"[ModBatchUpdate] 用户取消，正在取消任务...");
                task.Cancel();
                return;
            }

            await Task.Delay(500, _cts.Token);
        }
    }

    /// <summary>
    /// 当用户通过 NXM URL 下载文件时调用（类似 NexusCollectionWizardTask.HandleNxmUrl）
    /// </summary>
    public bool HandleNxmUrl(NxmUrl nxmUrl)
    {
        if (_currentItem == null || Status != DownloadTaskStatus.WaitingConfirmation)
            return false;

        var mod = _currentItem.Mod!;
        var (projectId, fileId) = ParseNexusModsUrl(mod.UpdateUrl);
        if (projectId == null || fileId == null)
            return false;

        // 验证 Mod ID 和 File ID
        if (nxmUrl.ModId.ToString() != projectId || nxmUrl.FileId.ToString() != fileId)
        {
            Log.Warn($"[ModBatchUpdate] NXM URL 不匹配: 期望 mod={projectId}, file={fileId}, 收到 mod={nxmUrl.ModId}, file={nxmUrl.FileId}");
            return false;
        }

        Log.Info($"[ModBatchUpdate] 收到匹配的 NXM URL: {mod.Name}");

        // 使用 NXM URL 参数下载文件，然后安装
        _ = Task.Run(async () => await DownloadAndInstallFromNxmUrlAsync(nxmUrl));

        return true;
    }

    /// <summary>
    /// 使用 NXM URL 参数下载并安装文件
    /// </summary>
    private async Task DownloadAndInstallFromNxmUrlAsync(NxmUrl nxmUrl)
    {
        if (_currentItem == null)
            return;

        var mod = _currentItem.Mod!;
        var item = _currentItem;

        try
        {
            Status = DownloadTaskStatus.Downloading;
            StatusMessage = $"正在下载 {item.Name}...";
            item.Status = ModBatchUpdateStatus.Downloading;

            // 使用 NXM URL 参数下载文件
            var progressCallback = new Stardew.ResourceProject.NexusMods.NexusModsService.DownloadProgressCallback((progress, statusMessage, bytesRead, totalBytes) =>
            {
                // 更新文件下载进度（从 0% 开始）
                if (totalBytes > 0)
                {
                    FileDownloadProgress = bytesRead * 100.0 / totalBytes;
                    FileDownloadBytes = bytesRead;
                    FileDownloadTotalBytes = totalBytes;
                }
                StatusMessage = $"{statusMessage} ({item.Name})";
            });

            var success = await Stardew.ResourceProject.NexusMods.NexusModsService.DownloadModAsync(
                nxmUrl.ModId,
                nxmUrl.FileId,
                _modsPath,
                nxmUrl.Key ?? string.Empty,
                nxmUrl.Expires?.ToString() ?? string.Empty,
                progressCallback,
                _cts.Token
            );

            if (!success)
            {
                Log.Error($"[ModBatchUpdate] [{item.Name}] NXM URL 下载失败");
                item.Status = ModBatchUpdateStatus.Failed;
                item.ErrorMessage = "下载失败";
                Status = DownloadTaskStatus.WaitingConfirmation;
                StatusMessage = $"下载失败: {item.Name}";
                return;
            }

            Log.Info($"[ModBatchUpdate] [{item.Name}] NXM URL 下载成功，开始安装...");

            // 从缓存获取下载的文件
            var cachedPath = Stardew.ResourceProject.NexusMods.NexusModsCacheService.Get(nxmUrl.ModId, nxmUrl.FileId);
            string? downloadedFile = null;

            if (!string.IsNullOrEmpty(cachedPath) && File.Exists(cachedPath))
            {
                // 从缓存复制到目标目录
                var cacheFileName = Path.GetFileName(cachedPath);
                downloadedFile = Path.Combine(_modsPath, cacheFileName);
                File.Copy(cachedPath, downloadedFile, overwrite: true);
                Log.Info($"[ModBatchUpdate] [{item.Name}] 从缓存获取文件: {cacheFileName}");
            }
            else
            {
                // 缓存中没有，查找刚下载的文件
                downloadedFile = FindLatestDownloadedFile(_modsPath, item.Name, item.NewVersion);
            }

            if (string.IsNullOrEmpty(downloadedFile) || !File.Exists(downloadedFile))
            {
                Log.Error($"[ModBatchUpdate] [{item.Name}] 无法找到下载的文件");
                item.Status = ModBatchUpdateStatus.Failed;
                item.ErrorMessage = "无法找到下载的文件";
                Status = DownloadTaskStatus.WaitingConfirmation;
                StatusMessage = $"无法找到文件: {item.Name}";
                return;
            }

            Log.Info($"[ModBatchUpdate] [{item.Name}] 找到下载文件: {downloadedFile}");

            // 安装 Mod
            Status = DownloadTaskStatus.Installing;
            StatusMessage = $"正在安装 {item.Name}...";
            item.Status = ModBatchUpdateStatus.Installing;

            var fileName = Path.GetFileName(downloadedFile);
            var installTask = new ModDownloadTask(
                modId: mod.NexusModsProjectId ?? "unknown",
                modName: mod.Name,
                fileName: fileName,
                localZipPath: downloadedFile,
                isLocalFile: true,
                gameBasePath: null,
                targetModsPath: _modsPath,
                saveOnly: false,
                sourcePlatform: "NexusMods",
                sourceProjectId: mod.NexusModsProjectId,
                sourceFileId: nxmUrl.FileId.ToString(),
                isModpack: false,
                parentCancellationToken: _cts.Token
            );

            await DownloadManager.Instance.ExecuteInternalTaskAsync(installTask);
            await WaitForTaskAsync(installTask);

            if (installTask.Status == DownloadTaskStatus.Completed)
            {
                item.Status = ModBatchUpdateStatus.Success;
                Log.Info($"[ModBatchUpdate] ✓ [{item.Name}] 安装成功");
            }
            else
            {
                item.Status = ModBatchUpdateStatus.Failed;
                item.ErrorMessage = installTask.StatusMessage ?? "安装失败";
                Log.Error($"[ModBatchUpdate] [{item.Name}] 安装失败: {installTask.StatusMessage}");
            }

            // 继续下一个 MOD
            await ProcessNextModAsync();
        }
        catch (OperationCanceledException)
        {
            Log.Warn($"[ModBatchUpdate] [{item.Name}] 操作已取消");
            item.Status = ModBatchUpdateStatus.Failed;
            item.ErrorMessage = "操作已取消";
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[ModBatchUpdate] [{item.Name}] NXM URL 下载安装失败");
            item.Status = ModBatchUpdateStatus.Failed;
            item.ErrorMessage = ex.Message;
            Status = DownloadTaskStatus.WaitingConfirmation;
        }
    }

    /// <summary>
    /// 用户点击「文件已下载」按钮时调用
    /// </summary>
    public async Task OnFileDownloadedAsync()
    {
        if (_currentItem == null || Status != DownloadTaskStatus.WaitingConfirmation)
            return;

        Log.Info($"[ModBatchUpdate] 用户报告文件已下载: {_currentItem.Name}");

        await ContinueInstallAfterDownloadAsync();
    }

    /// <summary>
    /// 下载完成后继续安装流程
    /// </summary>
    private async Task ContinueInstallAfterDownloadAsync()
    {
        if (_currentItem == null)
            return;

        var mod = _currentItem.Mod!;
        var item = _currentItem;

        try
        {
            Status = DownloadTaskStatus.Installing;
            StatusMessage = $"正在安装 {_currentItem.Name}...";

            var downloadedFile = FindLatestDownloadedFile(_modsPath, item.Name, item.NewVersion);
            if (downloadedFile == null)
            {
                Log.Error($"[ModBatchUpdate] [{item.Name}] 无法找到下载的文件");
                item.Status = ModBatchUpdateStatus.Failed;
                item.ErrorMessage = "无法找到下载的文件";
                Status = DownloadTaskStatus.WaitingConfirmation;
                StatusMessage = $"无法找到文件，请重新下载 {item.Name}";
                return;
            }

            Log.Info($"[ModBatchUpdate] [{item.Name}] 找到下载文件: {downloadedFile}");

            var fileName = Path.GetFileName(downloadedFile);
            var installTask = new ModDownloadTask(
                modId: mod.NexusModsProjectId ?? "unknown",
                modName: mod.Name,
                fileName: fileName,
                localZipPath: downloadedFile,
                isLocalFile: true,
                gameBasePath: null,
                targetModsPath: _modsPath,
                saveOnly: false,
                sourcePlatform: "NexusMods",
                sourceProjectId: mod.NexusModsProjectId,
                sourceFileId: null,
                isModpack: false,
                parentCancellationToken: _cts.Token
            );

            await DownloadManager.Instance.AddTaskAsync(installTask);
            await WaitForTaskAsync(installTask);

            if (installTask.Status == DownloadTaskStatus.Completed)
            {
                item.Status = ModBatchUpdateStatus.Success;
                Log.Info($"[ModBatchUpdate] ✓ [{item.Name}] 安装成功");

                // 继续下一个 MOD
                _ = Task.Run(async () => await ProcessNextModAsync());
            }
            else
            {
                item.Status = ModBatchUpdateStatus.Failed;
                item.ErrorMessage = installTask.StatusMessage ?? "安装失败";
                Status = DownloadTaskStatus.WaitingConfirmation;
                StatusMessage = $"安装失败: {installTask.StatusMessage}";
                Log.Error($"[ModBatchUpdate] [{item.Name}] 安装失败");
            }
        }
        catch (Exception ex)
        {
            item.Status = ModBatchUpdateStatus.Failed;
            item.ErrorMessage = ex.Message;
            Log.Error(ex, $"[ModBatchUpdate] [{item.Name}] 安装失败");
            Status = DownloadTaskStatus.WaitingConfirmation;
        }
    }

    /// <summary>
    /// 处理下一个 MOD（在当前 MOD 完成后调用）
    /// </summary>
    private async Task ProcessNextModAsync()
    {
        try
        {
            // 检查是否有正在等待浏览器下载的 MOD
            var waitingBrowserItem = ModList.FirstOrDefault(m => m.Status == ModBatchUpdateStatus.WaitingBrowser);
            if (waitingBrowserItem != null)
            {
                // 有 MOD 正在等待浏览器下载，不继续处理下一个
                Log.Info($"[ModBatchUpdate] 等待用户下载: {waitingBrowserItem.Name}");
                return;
            }

            // 找到下一个待处理的 MOD
            var nextItem = ModList.FirstOrDefault(m => m.Status == ModBatchUpdateStatus.Pending);
            if (nextItem == null)
            {
                // 所有 MOD 都已处理完成
                var finalCompletedCount = ModList.Count(m => m.Status == ModBatchUpdateStatus.Success);
                var finalFailedCount = ModList.Count(m => m.Status == ModBatchUpdateStatus.Failed);
                var finalSkippedCount = ModList.Count(m => m.Status == ModBatchUpdateStatus.Skipped);

                Status = DownloadTaskStatus.Completed;
                StatusMessage = $"更新完成: {finalCompletedCount} 成功, {finalFailedCount} 失败, {finalSkippedCount} 跳过";
                Progress = 100;
                CompletedTime = DateTime.Now;

                Log.Info($"[ModBatchUpdate] ========== 批量更新完成 ==========");
                Log.Info($"[ModBatchUpdate] 总计: {ModList.Count} 个 MOD");
                Log.Info($"[ModBatchUpdate] 成功: {finalCompletedCount} 个");
                Log.Info($"[ModBatchUpdate] 失败: {finalFailedCount} 个");
                Log.Info($"[ModBatchUpdate] 跳过: {finalSkippedCount} 个");
                return;
            }

            _currentItem = nextItem;
            nextItem.Status = ModBatchUpdateStatus.Pending;
            var completedCount = ModList.Count(m => m.Status == ModBatchUpdateStatus.Success) +
                                 ModList.Count(m => m.Status == ModBatchUpdateStatus.Failed) +
                                 ModList.Count(m => m.Status == ModBatchUpdateStatus.Skipped);
            StatusMessage = $"正在处理: {nextItem.Name} ({completedCount + 1}/{ModList.Count})";
            Progress = 10 + (completedCount * 80 / ModList.Count);

            // 处理下一个 MOD
            var success = await ProcessModUpdateAsync(nextItem);

            // 更新进度
            completedCount = ModList.Count(m => m.Status == ModBatchUpdateStatus.Success) +
                           ModList.Count(m => m.Status == ModBatchUpdateStatus.Failed) +
                           ModList.Count(m => m.Status == ModBatchUpdateStatus.Skipped);
            Progress = 10 + (completedCount * 80 / ModList.Count);
            StatusMessage = $"正在更新 MOD ({completedCount}/{ModList.Count})...";

            // 如果成功或跳过（非 Premium 用户等待浏览器），继续处理下一个
            if (success || nextItem.Status == ModBatchUpdateStatus.WaitingBrowser)
            {
                if (nextItem.Status != ModBatchUpdateStatus.WaitingBrowser)
                {
                    // 非等待状态，继续下一个
                    await ProcessNextModAsync();
                }
                // 等待状态，不继续，等待用户操作后通过 ContinueInstallAfterDownloadAsync 调用 ProcessNextModAsync
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[ModBatchUpdate] 处理下一个 MOD 时出错");
        }
    }

    /// <summary>
    /// 查找最新下载的文件
    /// </summary>
    private static string? FindLatestDownloadedFile(string modsPath, string modName, string version)
    {
        try
        {
            if (!Directory.Exists(modsPath))
                return null;

            // 查找最近修改的 zip 文件
            var files = Directory.GetFiles(modsPath, "*.zip", SearchOption.TopDirectoryOnly);
            if (files.Length == 0)
                return null;

            // 返回最新的文件
            return files.OrderByDescending(f => File.GetCreationTime(f)).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从 Curseforge URL 提取 FileId
    /// </summary>
    private static string? ExtractCurseforgeFileId(string url)
    {
        try
        {
            // Curseforge API URL 格式: https://api.cloudflare.com/#... 或直接包含 fileId
            // 尝试从 URL 中提取文件ID
            var match = Regex.Match(url, @"file[\\/](\d+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            // 尝试匹配其他可能的格式
            match = Regex.Match(url, @"fileId[=:](\d+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 解析 NexusMods URL，提取 ProjectId 和 FileId
    /// </summary>
    private static (string? ProjectId, string? FileId) ParseNexusModsUrl(string url)
    {
        try
        {
            // NexusMods URL 格式: https://www.nexusmods.com/stardewvalley/mods/{modId}?tab=files&file_id={fileId}
            var modMatch = Regex.Match(url, @"/mods/(\d+)", RegexOptions.IgnoreCase);
            if (!modMatch.Success)
                return (null, null);

            var projectId = modMatch.Groups[1].Value;

            var fileMatch = Regex.Match(url, @"file_id[=:](\d+)", RegexOptions.IgnoreCase);
            if (!fileMatch.Success)
                return (null, null);

            var fileId = fileMatch.Groups[1].Value;

            return (projectId, fileId);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>
    /// 跳过当前模组，继续处理下一个
    /// </summary>
    public async Task SkipCurrentModAsync()
    {
        if (_currentItem == null)
            return;

        Log.Info($"[ModBatchUpdate] 用户跳过模组: {_currentItem.Name}");
        _currentItem.Status = ModBatchUpdateStatus.Skipped;
        _currentItem.ErrorMessage = "用户跳过";

        // 重置文件下载进度
        FileDownloadProgress = 0;
        FileDownloadBytes = 0;
        FileDownloadTotalBytes = 0;

        // 继续处理下一个模组
        Status = DownloadTaskStatus.Downloading;
        StatusMessage = $"跳过 {_currentItem.Name}，继续处理下一个...";

        _ = Task.Run(async () => await ProcessNextModAsync());
    }

}
