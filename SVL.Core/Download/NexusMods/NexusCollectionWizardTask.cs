using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.Zip;
using SVL.Core.IO;
using SVL.Core.Logging;
using SVL.Core.Stardew.Instance;
using SVL.Core.Stardew.Mod.SMAPI;
using SVL.Core.Stardew.ResourceProject.NexusMods;

namespace SVL.Core.Download.NexusMods;

/// <summary>
/// Nexus Collection 非Premium用户安装任务
/// 通过任务管理器界面引导用户逐个下载 Collection 中的 Mods
/// </summary>
public class NexusCollectionWizardTask : DownloadTask
{
    private string? _collectionFilePath;  // 直接提供的文件路径（可选）
    private readonly string? _downloadLink;         // NexusMods API 下载链接（可选）
    private readonly string? _accessToken;          // NexusMods API Token（可选）
    private readonly string _collectionSlug;        // Collection Slug（用于显示）
    private readonly string _instanceName;
    private readonly string _gameBasePath;
    private readonly string _targetModsPath;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _collectionPictureUrl;   // Collection 图片 URL（用于设置实例图标）

    // 解析结果
    private CollectionModListResult? _modListResult;
    private CollectionModDownloadItem? _currentMod;
    private string? _collectionExtractPath;  // Collection 解压后的路径（用于获取 bundled 和 patches）

    // 下载完成信号（用于等待 HandleNxmUrlAsync 完成）
    private TaskCompletionSource<bool>? _downloadCompletionTcs;

    // 失败的模组列表
    public List<FailedModInfo> FailedMods { get; } = new();

    // 标记当前是否在下载 SMAPI（用于区分下载目标路径）
    private bool _isDownloadingSMAPI = false;

    /// <summary>
    /// 构造函数（直接提供已下载的 Collection 文件）
    /// </summary>
    public NexusCollectionWizardTask(
        string collectionFilePath,
        string instanceName,
        string gameBasePath,
        string targetModsPath,
        string collectionPictureUrl = "")
    {
        _collectionFilePath = collectionFilePath;
        _collectionSlug = Path.GetFileNameWithoutExtension(collectionFilePath);
        _instanceName = instanceName;
        _gameBasePath = gameBasePath;
        // 规范化路径，移除末尾的分隔符
        _targetModsPath = targetModsPath?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _collectionPictureUrl = collectionPictureUrl;

        Type = DownloadTaskType.Modpack;
        Name = $"Collection 安装: {_instanceName}";
        Status = DownloadTaskStatus.Pending;
        StatusMessage = "初始化...";
        Progress = 0;
    }

    /// <summary>
    /// 构造函数（由任务下载 Collection 文件）
    /// </summary>
    public NexusCollectionWizardTask(
        string downloadLink,
        string accessToken,
        string collectionSlug,
        string instanceName,
        string gameBasePath,
        string targetModsPath,
        string collectionPictureUrl = "")
    {
        _downloadLink = downloadLink;
        _accessToken = accessToken;
        _collectionSlug = collectionSlug;
        _instanceName = instanceName;
        _gameBasePath = gameBasePath;
        // 规范化路径，移除末尾的分隔符
        _targetModsPath = targetModsPath?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _collectionPictureUrl = collectionPictureUrl;

        Type = DownloadTaskType.Modpack;
        Name = $"Collection 安装: {collectionSlug}";
        Status = DownloadTaskStatus.Pending;
        StatusMessage = "准备下载 Collection...";
        Progress = 0;
    }

    /// <summary>
    /// 当前需要下载的 Mod（用于 UI 显示）
    /// </summary>
    public CollectionModDownloadItem? CurrentMod => _currentMod;

    /// <summary>
    /// Mod 列表（用于 UI 显示）
    /// </summary>
    public CollectionModListResult? ModListResult => _modListResult;

    public override async Task ExecuteAsync()
    {
        try
        {
            // 步骤0: 如果需要，下载 Collection 文件
            if (string.IsNullOrEmpty(_collectionFilePath) && !string.IsNullOrEmpty(_downloadLink))
            {
                Status = DownloadTaskStatus.Downloading;
                StatusMessage = "正在下载 Collection 信息...";
                Progress = 1;

                var downloadedFilePath = await DownloadCollectionFileAsync(_downloadLink!, _accessToken!);
                if (string.IsNullOrEmpty(downloadedFilePath))
                {
                    Status = DownloadTaskStatus.Failed;
                    StatusMessage = "下载 Collection 文件失败";
                    CompletedTime = DateTime.Now;
                    return;
                }

                _collectionFilePath = downloadedFilePath;
            }

            // 步骤1: 解析 Collection
            Status = DownloadTaskStatus.Downloading;
            StatusMessage = "正在解析 Collection...";
            Progress = 5;

            _modListResult = await ParseCollectionAsync();

            if (_cts.IsCancellationRequested)
            {
                Status = DownloadTaskStatus.Cancelled;
                return;
            }

            // 步骤2: 优先安装 SMAPI（如果 Collection 包含）
            if (_modListResult.HasSMAPI)
            {
                var smapiMod = _modListResult.SmapiMod;
                if (smapiMod != null)
                {
                    Log.Info($"[CollectionWizard] 检测到 SMAPI，开始安装: {smapiMod.Name}");

                    Status = DownloadTaskStatus.Downloading;
                    StatusMessage = $"正在下载 SMAPI...";
                    Progress = 8;

                    var smapiInstalled = await InstallSMAPIAsync(smapiMod);

                    if (!smapiInstalled)
                    {
                        Status = DownloadTaskStatus.Failed;
                        StatusMessage = "SMAPI 安装失败";
                        Log.Warn("[CollectionWizard] SMAPI 安装失败，停止整个安装流程");
                        return;
                    }

                    // 标记 SMAPI 为已完成
                    smapiMod.Status = CollectionModDownloadStatus.Completed;
                    Log.Info("[CollectionWizard] SMAPI 安装成功");

                    Progress = 10;
                }
            }

            // 步骤3: 检查是否有需要下载的其他 Mod
            if (_modListResult.NexusMods.Count == 0)
            {
                Status = DownloadTaskStatus.Completed;
                StatusMessage = "Collection 安装完成";
                Progress = 100;
                FileDownloadProgress = 0; // 任务完成，重置文件下载进度
                CompletedTime = DateTime.Now;
                return;
            }

            // 步骤4: 进入下载循环（按 Phase 分阶段处理）
            var totalMods = _modListResult.NexusMods.Count;
            var completedMods = 0;
            var failedMods = 0;
            var bundledMods = new List<CollectionModDownloadItem>();  // 收集 bundle 类型的 Mod

            // 按 Phase 分组（参考 Vortex 的处理逻辑）
            var groupedByPhase = _modListResult.NexusMods
                .Where(m => !(_modListResult.HasSMAPI && _modListResult.SmapiMod != null && m.ModId == _modListResult.SmapiMod.ModId)) // 排除已安装的SMAPI
                .Where(m => m.SourceType != "bundle")  // 排除 bundle 类型，稍后统一处理
                .GroupBy(m => m.Phase > 0 ? m.Phase : 1)
                .OrderBy(g => g.Key);

            foreach (var phaseGroup in groupedByPhase)
            {
                var phaseNumber = phaseGroup.Key;
                var phaseMods = phaseGroup.OrderBy(m => m.Name).ToList();
                var phaseCount = phaseMods.Count;

                Log.Info($"[CollectionWizard] ========== 阶段 {phaseNumber}: {phaseCount} 个 Mod ==========");

                foreach (var mod in phaseMods)
                {
                    if (_cts.IsCancellationRequested)
                    {
                        Status = DownloadTaskStatus.Cancelled;
                        return;
                    }

                    // 跳过已完成的 mod（来自缓存），但需要将缓存文件复制到目标目录并安装
                    if (mod.Status == CollectionModDownloadStatus.Completed)
                    {
                        completedMods++;
                        Log.Info($"[CollectionWizard] [阶段{phaseNumber}] 跳过已缓存的 Mod: {mod.Name}");

                        // 将缓存的 ZIP 文件复制到目标目录，然后安装
                        var cachedPath = NexusModsCacheService.Get(mod.ModId, mod.FileId);
                        if (!string.IsNullOrEmpty(cachedPath) && File.Exists(cachedPath))
                        {
                            // 使用 Mod 的真实名称作为 ZIP 文件名（清理非法字符）
                            var safeModName = SVL.Core.IO.FileNameValidator.SanitizeFolderName(mod.Name);
                            var destFileName = $"{safeModName}.zip";
                            var destPath = Path.Combine(_targetModsPath, destFileName);

                            // 确保目标目录存在
                            if (!Directory.Exists(_targetModsPath))
                            {
                                Directory.CreateDirectory(_targetModsPath);
                            }

                            // 复制缓存文件到目标目录
                            File.Copy(cachedPath, destPath, overwrite: true);
                            Log.Info($"[CollectionWizard] 已复制缓存文件到目标目录: {destFileName}");

                            // 安装 Mod（解压、处理嵌套文件、写入源文件记录）
                            var (installSuccess, failedZipPath) = await InstallModAsync(mod);

                            if (!installSuccess)
                            {
                                // 安装失败处理（继续安装，不终止）
                                HandleDownloadFailure(mod, phaseNumber, ref failedMods, failedZipPath);
                            }
                        }

                        continue;
                    }

                    _currentMod = mod;
                    var currentProgress = 15 + (completedMods * 80 / totalMods);
                    Progress = currentProgress;
                    StatusMessage = $"[阶段{phaseNumber}] 等待下载 {_currentMod.Name}";

                    // 设置等待用户操作状态
                    Status = DownloadTaskStatus.WaitingConfirmation;

                    // 检查是否支持直链下载
                    if (mod.SupportsDirectDownload)
                    {
                        // 直链下载：直接从 URL 下载
                        Log.Info($"[CollectionWizard] [阶段{phaseNumber}] 直链下载: {mod.Name} from {mod.DirectDownloadUrl}");
                        StatusMessage = $"[阶段{phaseNumber}] 正在下载 {_currentMod.Name}...";
                        Status = DownloadTaskStatus.Downloading;
                        FileDownloadProgress = 0; // 重置文件下载进度
                        mod.Status = CollectionModDownloadStatus.Downloading;

                        var downloadSuccess = await DownloadModDirectAsync(mod);

                        if (downloadSuccess)
                        {
                            completedMods++;
                            mod.Status = CollectionModDownloadStatus.Completed;
                            Log.Info($"[CollectionWizard] [阶段{phaseNumber}] 直链下载成功: {mod.Name}");

                            // 立即安装 Mod
                            var (installSuccess, failedZipPath) = await InstallModAsync(mod);

                            if (!installSuccess)
                            {
                                // 安装失败处理（继续安装，不终止）
                                HandleDownloadFailure(mod, phaseNumber, ref failedMods, failedZipPath);
                            }
                        }
                        else
                        {
                            // 下载失败处理（继续安装，不终止）
                            HandleDownloadFailure(mod, phaseNumber, ref failedMods);
                        }
                    }
                    else
                    {
                        // Nexus 类型：自动打开浏览器
                        await OpenBrowserForModAsync(mod);

                        // 等待 NXM URL（通过外部方法设置）
                        // 这里会阻塞，直到 NXM URL 被处理并下载完成
                        var downloadSuccess = await WaitForDownloadAsync(mod);

                        if (downloadSuccess)
                        {
                            completedMods++;
                            mod.Status = CollectionModDownloadStatus.Completed;

                            // 立即安装 Mod
                            var (installSuccess, failedZipPath) = await InstallModAsync(mod);

                            if (!installSuccess)
                            {
                                // 安装失败处理（继续安装，不终止）
                                HandleDownloadFailure(mod, phaseNumber, ref failedMods, failedZipPath);
                            }
                        }
                        else
                        {
                            // 下载失败处理（继续安装，不终止）
                            HandleDownloadFailure(mod, phaseNumber, ref failedMods);
                        }
                    }
                }

                Log.Info($"[CollectionWizard] ========== 阶段 {phaseNumber} 完成 ==========");
            }

            // 步骤5: 处理 bundled 文件（替换现有 Mod 的文件）
            Status = DownloadTaskStatus.Installing;
            StatusMessage = "正在处理 bundled 文件...";
            Progress = 92;

            await ApplyBundledFilesAsync();

            // 步骤6: 应用 patches 补丁
            Status = DownloadTaskStatus.Installing;
            StatusMessage = "正在应用补丁...";
            Progress = 95;

            await ApplyPatchesAsync();

            // 步骤6: 完成安装并创建实例配置
            Progress = 100;
            FileDownloadProgress = 0; // 任务完成，重置文件下载进度

            // 创建实例配置
            await CreateInstanceConfigAsync();

            Status = DownloadTaskStatus.Completed;
            CompletedTime = DateTime.Now;
            StatusMessage = $"Collection 安装完成！已下载 {completedMods} 个 Mod" +
                           (FailedMods.Count > 0 ? $"，{FailedMods.Count} 个失败/跳过" : "");

            Log.Info($"[CollectionWizard] 安装完成: {_modListResult.CollectionName}");

            // 触发完成事件（如果有失败的 mod）
            if (FailedMods.Count > 0)
            {
                Log.Info($"[CollectionWizard] 有 {FailedMods.Count} 个 Mod 失败/跳过");
            }
        }
        catch (OperationCanceledException)
        {
            Status = DownloadTaskStatus.Cancelled;
            StatusMessage = "已取消";

            // 清理已创建的文件
            await CleanupOnCancelAsync();
        }
        catch (Exception ex)
        {
            Status = DownloadTaskStatus.Failed;
            StatusMessage = $"安装失败: {ex.Message}";
            CompletedTime = DateTime.Now;
            Log.Warn("[CollectionWizard] 安装失败", ex);
        }
    }

    /// <summary>
    /// 处理接收到的 NXM URL（由外部调用）
    /// </summary>
    public async Task<bool> HandleNxmUrlAsync(NxmUrl nxmUrl)
    {
        if (_currentMod == null || Status != DownloadTaskStatus.WaitingConfirmation)
            return false;

        // 验证 Mod ID 和 File ID
        if (nxmUrl.ModId != _currentMod.ModId || nxmUrl.FileId != _currentMod.FileId)
        {
            Log.Warn($"[CollectionWizard] NXM URL 不匹配: 期望 mod={_currentMod.ModId}, file={_currentMod.FileId}, 收到 mod={nxmUrl.ModId}, file={nxmUrl.FileId}");
            return false;
        }

        Log.Info($"[CollectionWizard] 收到匹配的 NXM URL: {_currentMod.Name}");

        // 标记为正在下载
        Status = DownloadTaskStatus.Downloading;
        FileDownloadProgress = 0; // 重置文件下载进度
        _currentMod.Status = CollectionModDownloadStatus.Downloading;
        StatusMessage = $"正在下载: {_currentMod.Name}...";

        // 启动下载并等待完成
        var success = await DownloadModAsync(nxmUrl);

        // 设置下载完成信号
        _downloadCompletionTcs?.TrySetResult(success);

        return success;
    }

    /// <summary>
    /// 打开浏览器下载页面
    /// </summary>
    private async Task OpenBrowserForModAsync(CollectionModDownloadItem mod)
    {
        try
        {
            var downloadUrl = mod.FilesPageUrl;
            Log.Info($"[CollectionWizard] 打开浏览器: {downloadUrl}");
            IO.ProcessEx.OpenUrl(downloadUrl);

            _currentMod.Status = CollectionModDownloadStatus.BrowserOpened;
            StatusMessage = $"等待下载 {_currentMod.Name}";
        }
        catch (Exception ex)
        {
            Log.Error("[CollectionWizard] 打开浏览器失败", ex);
            StatusMessage = $"打开浏览器失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 等待下载完成
    /// </summary>
    /// <param name="mod">正在等待的 Mod</param>
    /// <param name="disableTimeout">为 true 则禁用超时（SMAPI 等关键下载使用）</param>
    private async Task<bool> WaitForDownloadAsync(CollectionModDownloadItem mod, bool disableTimeout = false)
    {
        // 创建新的等待信号
        _downloadCompletionTcs = new TaskCompletionSource<bool>();

        try
        {
            // 等待下载完成（由 HandleNxmUrlAsync 设置信号）、取消或超时
            var waitTask = _downloadCompletionTcs.Task;

            // 取消任务：监听用户取消
            var cancellationTask = Task.Run(() =>
            {
                _cts.Token.WaitHandle.WaitOne();
                return true; // 返回 true 表示用户取消
            });

            Task completedTask;

            if (disableTimeout)
            {
                // 不设置超时（SMAPI 等关键下载）
                completedTask = await Task.WhenAny(waitTask, cancellationTask);
            }
            else
            {
                // 超时任务：30 分钟后超时
                var timeoutTask = Task.Delay(TimeSpan.FromMinutes(30));
                completedTask = await Task.WhenAny(waitTask, timeoutTask, cancellationTask);

                if (completedTask == timeoutTask)
                {
                    // 超时
                    _downloadCompletionTcs.TrySetCanceled();
                    Log.Warn($"[CollectionWizard] 等待下载超时: {mod.Name}");
                    return false;
                }
            }

            if (completedTask == cancellationTask)
            {
                // 用户取消 - 抛出异常以停止整个安装流程
                _downloadCompletionTcs.TrySetCanceled();
                Log.Info($"[CollectionWizard] 用户取消下载: {mod.Name}");
                throw new OperationCanceledException(_cts.Token);
            }

            // 下载完成，返回结果
            return await waitTask;
        }
        finally
        {
            _downloadCompletionTcs = null;
        }
    }

    /// <summary>
    /// 下载 Mod
    /// </summary>
    private async Task<bool> DownloadModAsync(NxmUrl nxmUrl)
    {
        if (_currentMod == null)
            return false;

        try
        {
            var progressCallback = new NexusModsService.DownloadProgressCallback((progress, statusMessage, bytesRead, totalBytes) =>
            {
                // 更新文件下载进度（仅表示当前文件的下载百分比，从 0% 开始）
                if (totalBytes > 0)
                {
                    FileDownloadProgress = bytesRead * 100.0 / totalBytes;
                    FileDownloadBytes = bytesRead;
                    FileDownloadTotalBytes = totalBytes;
                }
                StatusMessage = $"{statusMessage} ({_currentMod.Name})";
            });

            // SMAPI 下载到临时目录，避免提前创建版本目录
            string downloadPath;
            if (_isDownloadingSMAPI)
            {
                downloadPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SVL",
                    "temp"
                );
                Log.Info($"[CollectionWizard] SMAPI 下载到临时目录: {downloadPath}");
            }
            else
            {
                downloadPath = _targetModsPath;
            }

            var success = await NexusModsService.DownloadModAsync(
                nxmUrl.ModId,
                nxmUrl.FileId,
                downloadPath,
                nxmUrl.Key ?? string.Empty,
                nxmUrl.Expires?.ToString() ?? string.Empty,
                progressCallback
            );

            if (success)
            {
                Log.Info($"[CollectionWizard] 下载成功: {_currentMod.Name}");
                _currentMod.Status = CollectionModDownloadStatus.Completed;
                return true;
            }
            else
            {
                Log.Warn($"[CollectionWizard] 下载失败: {_currentMod.Name}");
                _currentMod.Status = CollectionModDownloadStatus.Failed;
                StatusMessage = $"下载失败: {_currentMod.Name}";
                return false;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[CollectionWizard] 下载失败: {_currentMod.Name}", ex);
            _currentMod.Status = CollectionModDownloadStatus.Failed;
            return false;
        }
    }

    /// <summary>
    /// 处理下载失败
    /// </summary>
    private void HandleDownloadFailure(CollectionModDownloadItem mod, int phaseNumber, ref int failedMods, string? zipFilePath = null)
    {
        if (mod.IsOptional)
        {
            Log.Info($"[CollectionWizard] [阶段{phaseNumber}] 可选 Mod 下载失败/跳过: {mod.Name}");
            mod.Status = CollectionModDownloadStatus.Skipped;

            // 添加到失败列表（跳过的可选 mod）
            FailedMods.Add(new FailedModInfo
            {
                Platform = "NexusMods",
                ModName = mod.Name,
                ProjectId = mod.ModId,
                FileId = mod.FileId,
                Error = "用户跳过",
                GameDomain = mod.GameDomain,
                ZipFilePath = zipFilePath
            });
        }
        else
        {
            Log.Warn($"[CollectionWizard] [阶段{phaseNumber}] 必需 Mod 下载失败: {mod.Name}");
            mod.Status = CollectionModDownloadStatus.Failed;
            failedMods++;

            // 添加到失败列表
            FailedMods.Add(new FailedModInfo
            {
                Platform = "NexusMods",
                ModName = mod.Name,
                ProjectId = mod.ModId,
                FileId = mod.FileId,
                Error = "安装失败",
                GameDomain = mod.GameDomain,
                ZipFilePath = zipFilePath
            });

        }
    }

    /// <summary>
    /// 直链下载 Mod（用于 browse/direct/manual 类型）
    /// </summary>
    private async Task<bool> DownloadModDirectAsync(CollectionModDownloadItem mod)
    {
        if (string.IsNullOrEmpty(mod.DirectDownloadUrl))
            return false;

        try
        {
            // 确保目标目录存在
            if (!Directory.Exists(_targetModsPath))
            {
                Directory.CreateDirectory(_targetModsPath);
            }

            // 使用 Mod 的真实名称作为 ZIP 文件名（清理非法字符）
            var safeModName = FileNameValidator.SanitizeFolderName(mod.Name);
            var zipFileName = $"{safeModName}.zip";
            var zipFilePath = Path.Combine(_targetModsPath, zipFileName);

            Log.Info($"[CollectionWizard] 直链下载: {mod.DirectDownloadUrl} -> {zipFilePath}");

            // 使用 HttpClient 下载
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(10); // 10 分钟超时

            // 发送请求
            var response = await httpClient.GetAsync(mod.DirectDownloadUrl);
            response.EnsureSuccessStatusCode();

            // 保存到文件
            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(zipFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            long bytesRead = 0;
            int read;

            // 带进度的下载
            while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, read);
                bytesRead += read;

                if (totalBytes > 0)
                {
                    var progress = (int)(bytesRead * 100 / totalBytes);
                    // 更新文件下载进度（仅表示当前文件的下载百分比）
                    FileDownloadProgress = progress;
                    StatusMessage = $"正在下载 {mod.Name}... {progress}%";
                }
            }

            Log.Info($"[CollectionWizard] 直链下载成功: {mod.Name} ({bytesRead} bytes)");
            mod.Status = CollectionModDownloadStatus.Completed;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[CollectionWizard] 直链下载失败: {mod.Name}", ex);
            mod.Status = CollectionModDownloadStatus.Failed;
            StatusMessage = $"下载失败: {mod.Name}";
            return false;
        }
    }

    /// <summary>
    /// 解析 Collection 文件
    /// </summary>
    private async Task<CollectionModListResult> ParseCollectionAsync()
    {
        return await Task.Run(() =>
        {
            // 解压 7z 文件
            var tempDir = ExtractCollectionFile(_collectionFilePath);

            // 保存解压路径（用于后续访问 bundled 和 patches 目录）
            _collectionExtractPath = tempDir;
            Log.Info($"[CollectionWizard] Collection 解压路径: {_collectionExtractPath}");

            // 读取并解析 collection.json
            var collectionJsonPath = Path.Combine(tempDir, "collection.json");
            var collectionJson = File.ReadAllText(collectionJsonPath);
            var collection = JsonSerializer.Deserialize<NexusCollectionJson>(
                collectionJson,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            if (collection?.Info == null)
                throw new InvalidOperationException("无法解析 Collection JSON");

            // 解析 Mod 列表（会自动检测和过滤 SMAPI）
            var result = CollectionModListParser.ParseModList(collection);

            // 注意：不再删除临时目录，在安装完成后清理

            return result;
        });
    }

    /// <summary>
    /// 解压 Collection 文件
    /// </summary>
    private string ExtractCollectionFile(string collectionFilePath)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SVL", "collections", Guid.NewGuid().ToString());
        var success = SevenZipService.Extract(collectionFilePath, tempDir);

        if (!success)
            throw new InvalidOperationException("无法解压 Collection 文件");

        // 验证 collection.json 是否存在
        var collectionJsonPath = Path.Combine(tempDir, "collection.json");
        if (!File.Exists(collectionJsonPath))
        {
            // 尝试在子目录中查找
            var subDirs = Directory.GetDirectories(tempDir);
            if (subDirs.Length > 0)
            {
                collectionJsonPath = Path.Combine(subDirs[0], "collection.json");
                if (File.Exists(collectionJsonPath))
                {
                    File.Copy(collectionJsonPath, Path.Combine(tempDir, "collection.json"), true);
                }
            }
        }

        if (!File.Exists(Path.Combine(tempDir, "collection.json")))
            throw new FileNotFoundException("Collection JSON 文件不存在");

        return tempDir;
    }

    public override void Cancel()
    {
        _cts.Cancel();
        Status = DownloadTaskStatus.Cancelled;
        StatusMessage = "正在取消...";
        // 清理由 ExecuteAsync 的 OperationCanceledException 处理块执行
    }

    /// <summary>
    /// 取消时清理已创建的文件
    /// </summary>
    private async Task CleanupOnCancelAsync()
    {
        try
        {
            await Task.Run(() =>
            {
                Log.Info("[CollectionWizard] 开始清理取消安装产生的文件...");

                // 1. 清理版本目录
                var versionPath = InstanceIsolationService.GetVersionPath(_gameBasePath, _instanceName);
                if (Directory.Exists(versionPath))
                {
                    try
                    {
                        Directory.Delete(versionPath, recursive: true);
                        Log.Info($"[CollectionWizard] 已删除版本目录: {versionPath}");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[CollectionWizard] 删除版本目录失败: {ex.Message}");
                    }
                }

                // 2. 清理 Collection 解压目录
                if (!string.IsNullOrEmpty(_collectionExtractPath) && Directory.Exists(_collectionExtractPath))
                {
                    try
                    {
                        Directory.Delete(_collectionExtractPath, recursive: true);
                        Log.Info($"[CollectionWizard] 已删除 Collection 解压目录: {_collectionExtractPath}");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[CollectionWizard] 删除 Collection 解压目录失败: {ex.Message}");
                    }
                }

                // 3. 清理临时目录中的 SMAPI 文件
                var tempPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SVL",
                    "temp"
                );
                if (Directory.Exists(tempPath))
                {
                    try
                    {
                        var smapiFiles = Directory.GetFiles(tempPath, "SMAPI*.zip", SearchOption.TopDirectoryOnly);
                        foreach (var file in smapiFiles)
                        {
                            try
                            {
                                File.Delete(file);
                                Log.Info($"[CollectionWizard] 已删除临时 SMAPI 文件: {Path.GetFileName(file)}");
                            }
                            catch (Exception ex)
                            {
                                Log.Warn($"[CollectionWizard] 删除临时文件失败: {ex.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[CollectionWizard] 清理临时目录失败: {ex.Message}");
                    }
                }

                Log.Info("[CollectionWizard] 清理完成");
            });
        }
        catch (Exception ex)
        {
            Log.Error("[CollectionWizard] 清理过程出错", ex);
        }
    }

    /// <summary>
    /// 跳过当前可选 Mod
    /// </summary>
    public void SkipCurrentMod()
    {
        if (_currentMod == null || !_currentMod.IsOptional)
            return;

        Log.Info($"[CollectionWizard] 跳过可选 Mod: {_currentMod.Name}");

        // 标记当前 Mod 为已跳过
        _currentMod.Status = CollectionModDownloadStatus.Skipped;

        // 设置下载完成信号，让任务继续下一个 Mod
        _downloadCompletionTcs?.TrySetResult(false);

        StatusMessage = $"已跳过: {_currentMod.Name}";
    }

    /// <summary>
    /// 下载 Collection 文件（先下载 JSON，再下载 7z 压缩包）
    /// </summary>
    private async Task<string?> DownloadCollectionFileAsync(string downloadLink, string accessToken)
    {
        try
        {
            // 步骤1: 下载 Collection JSON（包含 download_links）
            Status = DownloadTaskStatus.Downloading;
            StatusMessage = "正在下载 Collection 信息...";
            Progress = 2;

            var collectionJson = await DownloadCollectionJsonAsync(downloadLink, accessToken);
            if (string.IsNullOrEmpty(collectionJson))
            {
                Log.Warn("[CollectionWizard] 下载 Collection JSON 失败");
                return null;
            }

            Log.Info($"[CollectionWizard] Collection JSON 下载成功，大小: {collectionJson.Length} 字节");

            // 步骤2: 解析 JSON 获取 7z 下载链接
            var jsonDoc = JsonDocument.Parse(collectionJson);
            var downloadLinks = jsonDoc.RootElement.GetProperty("download_links");

            if (downloadLinks.GetArrayLength() == 0)
            {
                Log.Warn("[CollectionWizard] download_links 数组为空");
                return null;
            }

            // 直接使用第一个链接
            var firstLink = downloadLinks[0];
            var archiveUrl = firstLink.GetProperty("URI").GetString();

            if (string.IsNullOrEmpty(archiveUrl))
            {
                Log.Warn("[CollectionWizard] download_links 中没有有效的 URI");
                return null;
            }

            Log.Info($"[CollectionWizard] 下载 Collection 压缩包: {archiveUrl}");

            // 步骤3: 下载 7z 压缩包
            StatusMessage = "正在下载 Collection 文件...";
            Progress = 3;

            var archivePath = await DownloadCollectionArchiveAsync(archiveUrl, _collectionSlug);
            if (string.IsNullOrEmpty(archivePath))
            {
                Log.Warn("[CollectionWizard] 下载 Collection 压缩包失败");
                return null;
            }

            var fileInfo = new FileInfo(archivePath);
            Log.Info($"[CollectionWizard] Collection 压缩包下载成功: {archivePath}, 大小: {fileInfo.Length} 字节");

            Progress = 5;
            return archivePath;
        }
        catch (Exception ex)
        {
            Log.Error("[CollectionWizard] 下载 Collection 文件失败", ex);
            return null;
        }
    }

    /// <summary>
    /// 下载 Collection JSON
    /// </summary>
    private async Task<string?> DownloadCollectionJsonAsync(string downloadLink, string accessToken)
    {
        try
        {
            var fullUrl = downloadLink.StartsWith("http")
                ? downloadLink
                : $"https://api.nexusmods.com{downloadLink}";

            Log.Info($"[CollectionWizard] 下载 Collection JSON: {fullUrl}");

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
            client.DefaultRequestHeaders.Add("User-Agent", "SVL-StardewLauncher/1.0");
            client.Timeout = TimeSpan.FromSeconds(30);

            var response = await client.GetAsync(fullUrl, _cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"[CollectionWizard] 下载 Collection JSON 失败: {response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            Log.Info($"[CollectionWizard] Collection JSON 下载成功，大小: {json.Length} 字节");

            return json;
        }
        catch (Exception ex)
        {
            Log.Error("[CollectionWizard] 下载 Collection JSON 异常", ex);
            return null;
        }
    }

    /// <summary>
    /// 下载 Collection 7z 压缩包
    /// </summary>
    private async Task<string?> DownloadCollectionArchiveAsync(string archiveUrl, string collectionSlug)
    {
        try
        {
            Log.Info($"[CollectionWizard] 下载 Collection 压缩包: {archiveUrl}");

            // 创建临时目录
            var tempDir = Path.Combine(Path.GetTempPath(), "SVL", "collections");
            if (!Directory.Exists(tempDir))
            {
                Directory.CreateDirectory(tempDir);
            }

            // 生成文件名
            var fileName = $"{collectionSlug}-{Guid.NewGuid()}.7z";
            var filePath = Path.Combine(tempDir, fileName);

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "SVL-StardewLauncher/1.0");
            client.Timeout = TimeSpan.FromMinutes(10);

            // 下载文件
            var response = await client.GetAsync(archiveUrl, _cts.Token);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            using var fs = new FileStream(filePath, FileMode.Create);
            using var stream = await response.Content.ReadAsStreamAsync();

            var buffer = new byte[8192];
            int bytesRead;
            long totalRead = 0;

            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await fs.WriteAsync(buffer, 0, bytesRead);
                totalRead += bytesRead;

                // 更新进度
                if (totalBytes > 0)
                {
                    var progress = 3 + (totalRead / (double)totalBytes * 2); // 3-5%
                    Progress = (int)progress;
                }
            }

            Log.Info($"[CollectionWizard] Collection 压缩包下载成功: {filePath}, 大小: {totalRead} 字节");
            return filePath;
        }
        catch (Exception ex)
        {
            Log.Error("[CollectionWizard] 下载 Collection 压缩包异常", ex);
            return null;
        }
    }

    /// <summary>
    /// 安装 SMAPI（从 NexusMods 下载并安装）
    /// </summary>
    private async Task<bool> InstallSMAPIAsync(CollectionModDownloadItem smapiMod)
    {
        string? smapiZipPath = null;

        try
        {
            Log.Info($"[CollectionWizard] 开始安装 SMAPI: {smapiMod.Name}");

            // 临时目录用于下载 SMAPI
            var tempDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SVL",
                "temp"
            );

            if (!Directory.Exists(tempDir))
            {
                Directory.CreateDirectory(tempDir);
            }

            // 使用 NexusDownloadWorkflow 下载 SMAPI
            Action<NexusDownloadProgress> progress = p =>
            {
                Progress = 8 + (int)(p.Percentage * 2); // 8-10%
                FileDownloadProgress = p.Percentage; // 文件下载进度
                StatusMessage = $"正在下载 SMAPI... {p.Percentage}%";
            };

            smapiZipPath = await NexusDownloadWorkflow.DownloadZipAsync(
                gameId: smapiMod.GameDomain,
                modId: smapiMod.ModId,
                fileId: smapiMod.FileId,
                workingDirectory: tempDir,
                progressCallback: progress,
                cancellationToken: _cts.Token,
                useCache: true);

            if (string.IsNullOrEmpty(smapiZipPath) || !File.Exists(smapiZipPath))
            {
                Log.Warn("[CollectionWizard] SMAPI 下载失败");
                return false;
            }
        }
        catch (NexusPremiumRequiredException)
        {
            // 非 Premium 用户需要通过浏览器下载
            Log.Info("[CollectionWizard] 非 Premium 用户，需要通过浏览器下载 SMAPI");

            // 设置 SMAPI 下载标志（让 DownloadModAsync 使用临时目录）
            _isDownloadingSMAPI = true;

            try
            {
                // 设置当前 Mod 为 SMAPI
                _currentMod = smapiMod;
                smapiMod.Status = CollectionModDownloadStatus.BrowserOpened;
                FileDownloadProgress = 0; // 重置文件下载进度

                // 设置任务状态为等待确认，以便 HandleNxmUrlAsync 可以处理 NXM URL
                Status = DownloadTaskStatus.WaitingConfirmation;
                StatusMessage = $"等待下载 SMAPI...";

                // 打开浏览器（复用通用方法）
                await OpenBrowserForModAsync(smapiMod);

                // 等待 NXM URL（通过外部方法设置，不设超时）
                var downloadSuccess = await WaitForDownloadAsync(smapiMod, disableTimeout: true);

                if (!downloadSuccess)
                {
                    Log.Warn("[CollectionWizard] SMAPI 浏览器下载失败或被取消");
                    return false;
                }

                // 获取下载后的文件路径 - 优先从临时目录查找，然后从缓存查找
                var tempPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SVL",
                    "temp"
                );

                if (Directory.Exists(tempPath))
                {
                    // 查找最新的 SMAPI zip 文件
                    var smapiFiles = Directory.GetFiles(tempPath, "SMAPI*.zip", SearchOption.TopDirectoryOnly)
                        .OrderByDescending(f => File.GetCreationTime(f))
                        .FirstOrDefault();
                    if (!string.IsNullOrEmpty(smapiFiles) && File.Exists(smapiFiles))
                    {
                        smapiZipPath = smapiFiles;
                        Log.Info($"[CollectionWizard] 从临时目录获取 SMAPI: {smapiZipPath}");
                    }
                }

                // 如果临时目录没找到，从缓存查找
                if (string.IsNullOrEmpty(smapiZipPath) || !File.Exists(smapiZipPath))
                {
                    var smapiCachePath = NexusModsCacheService.GetCachePath(smapiMod.ModId, smapiMod.FileId);
                    if (!string.IsNullOrEmpty(smapiCachePath) && File.Exists(smapiCachePath))
                    {
                        smapiZipPath = smapiCachePath;
                        Log.Info($"[CollectionWizard] 从缓存获取 SMAPI: {smapiZipPath}");
                    }
                }

                if (string.IsNullOrEmpty(smapiZipPath) || !File.Exists(smapiZipPath))
                {
                    Log.Warn("[CollectionWizard] SMAPI 下载文件不存在");
                    return false;
                }
            }
            finally
            {
                // 重置 SMAPI 下载标志
                _isDownloadingSMAPI = false;
            }
        }
        catch (Exception ex)
        {
            Log.Error("[CollectionWizard] SMAPI 下载异常", ex);
            return false;
        }

        try
        {
            Log.Info($"[CollectionWizard] SMAPI 下载成功: {smapiZipPath}");

            // 安装 SMAPI（使用 SmapApiService）
            Progress = 10;
            StatusMessage = "正在安装 SMAPI...";

            // 获取游戏文件安装路径
            var gameFilesPath = InstanceIsolationService.GetVersionPath(_gameBasePath, _instanceName);

            // 检查版本目录是否存在
            if (Directory.Exists(gameFilesPath))
            {
                // 检查目录是否为空（或只有空子目录）
                if (IsDirectoryEmpty(gameFilesPath))
                {
                    Log.Info($"[CollectionWizard] 版本目录存在但为空，清理后继续: {gameFilesPath}");
                    try
                    {
                        Directory.Delete(gameFilesPath, recursive: true);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[CollectionWizard] 清理空目录失败: {ex.Message}");
                    }
                }
                else
                {
                    Log.Warn($"[CollectionWizard] 版本目录已存在且不为空: {gameFilesPath}");
                    Status = DownloadTaskStatus.Failed;
                    StatusMessage = $"版本目录 '{_instanceName}' 已存在且不为空，请先手动删除或使用不同的名称";
                    return false;
                }
            }

            // 确保目标目录存在
            if (!Directory.Exists(gameFilesPath))
            {
                Directory.CreateDirectory(gameFilesPath);
            }

            // 使用 SmapiInstallHelper 统一安装（Content 链接 → SMAPI 安装 → 游戏文件复制 → Mods 目录）
            var success = await SmapiInstallHelper.SetupIsolatedSmapiAsync(
                smapiZipPath,
                _gameBasePath,
                gameFilesPath,
                progressCallback: p =>
                {
                    Progress = 10 + (int)(p * 5); // 10-15%
                    StatusMessage = $"正在安装 SMAPI... {(int)(p * 100)}%";
                });

            if (!success)
            {
                Log.Warn("[CollectionWizard] SMAPI 安装失败");
                return false;
            }

            Log.Info($"[CollectionWizard] ✓ SMAPI 安装成功");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("[CollectionWizard] SMAPI 安装异常", ex);
            return false;
        }
    }

    /// <summary>
    /// 检查目录是否为空（或只包含空子目录）
    /// </summary>
    private static bool IsDirectoryEmpty(string path)
    {
        if (!Directory.Exists(path))
            return true;

        // 检查是否有任何文件
        if (Directory.GetFiles(path).Length > 0)
            return false;

        // 递归检查子目录
        foreach (var subDir in Directory.GetDirectories(path))
        {
            if (!IsDirectoryEmpty(subDir))
                return false;
        }

        return true;
    }

    /// <summary>
    /// 递归复制目录（用于跨卷移动）
    /// </summary>
    private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var targetFile = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, targetFile, true);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var subTargetDir = Path.Combine(targetDir, Path.GetFileName(subDir));
            CopyDirectoryRecursive(subDir, subTargetDir);
        }
    }

    /// <summary>
    /// 创建实例配置并下载 Collection 图标
    /// </summary>
    private async Task CreateInstanceConfigAsync()
    {
        try
        {
            Log.Info($"[CollectionWizard] 创建实例配置: {_instanceName}");

            // 下载 Collection 图片并设置为实例图标（使用传入的图片 URL）
            string? customIcon = null;
            if (!string.IsNullOrEmpty(_collectionPictureUrl))
            {
                try
                {
                    Log.Info($"[CollectionWizard] 下载 Collection 图标: {_collectionPictureUrl}");

                    // 使用 ImageCacheService 下载并缓存图片
                    customIcon = await ImageCacheService.DownloadAndCacheImageAsync(_collectionPictureUrl);

                    if (!string.IsNullOrEmpty(customIcon))
                    {
                        Log.Info($"[CollectionWizard] ✓ Collection 图标下载成功: {customIcon}");
                    }
                    else
                    {
                        Log.Warn($"[CollectionWizard] Collection 图标下载失败，将使用默认图标");
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("[CollectionWizard] Collection 图标下载失败", ex);
                }
            }

            // 获取游戏版本
            var gameVersion = GamePathService.GetGameVersion(_gameBasePath);

            // 获取 SMAPI 版本
            var smapiVersion = SmapApiService.GetInstalledSmapiVersion(
                InstanceIsolationService.GetVersionPath(_gameBasePath, _instanceName));

            // 创建新实例配置
            var newInstance = new GamePathInfo
            {
                Id = Guid.NewGuid().ToString(),
                Name = _instanceName,
                GamePath = _gameBasePath,
                Version = gameVersion,
                IsSMAPIInstance = true,
                SMAPIVersion = smapiVersion ?? "Unknown",
                HasSMAPIInstalled = true,
                EnableIsolation = true,
                CustomIcon = customIcon ?? ""  // 设置自定义图标
            };

            // 加载现有实例列表
            var existingInstances = SettingsService.LoadInstances();
            Log.Info($"[CollectionWizard] 当前有 {existingInstances.Count} 个实例");

            // 添加新实例
            existingInstances.Add(newInstance);

            // 保存回 instances.json
            SettingsService.SaveInstances(existingInstances);
            Log.Info($"[CollectionWizard] ✓ 保存实例配置到 instances.json: {_instanceName}");
        }
        catch (Exception ex)
        {
            Log.Warn("[CollectionWizard] 创建实例配置失败", ex);
            // 不抛出异常，图标下载失败不应阻止安装完成
        }
    }

    /// <summary>
    /// 安装单个 Mod（复用 ModDownloadTask 的逻辑，包括嵌套处理和源文件记录）
    /// </summary>
    private async Task<(bool Success, string? ZipFilePath)> InstallModAsync(CollectionModDownloadItem mod)
    {
        string? zipFilePath = null;
        try
        {
            Log.Info($"[CollectionWizard] 开始安装 Mod: {mod.Name}");

            // 确保目标目录存在
            if (!Directory.Exists(_targetModsPath))
            {
                Directory.CreateDirectory(_targetModsPath);
            }

            // 优先从缓存获取（使用 modId + fileId 精确匹配）
            var cachedPath = NexusModsCacheService.Get(mod.ModId, mod.FileId);
            if (!string.IsNullOrEmpty(cachedPath) && File.Exists(cachedPath))
            {
                // 从缓存复制到目标目录
                var cacheFileName = Path.GetFileName(cachedPath);
                zipFilePath = Path.Combine(_targetModsPath, cacheFileName);
                File.Copy(cachedPath, zipFilePath, overwrite: true);
                Log.Info($"[CollectionWizard] 从缓存复制 ZIP 文件: {cacheFileName}");
            }
            else
            {
                // 缓存中没有，查找本地已下载的文件
                var safeModName = FileNameValidator.SanitizeFolderName(mod.Name);
                var expectedZipFileName = $"{safeModName}.zip";
                var expectedZipPath = Path.Combine(_targetModsPath, expectedZipFileName);

                if (File.Exists(expectedZipPath))
                {
                    zipFilePath = expectedZipPath;
                    Log.Info($"[CollectionWizard] 找到本地 ZIP 文件: {expectedZipFileName}");
                }
                else
                {
                    Log.Warn($"[CollectionWizard] ZIP 文件不存在: modId={mod.ModId}, fileId={mod.FileId}, 预期路径={expectedZipPath}");
                    return (false, expectedZipPath);
                }
            }

            // 验证 ZIP 文件完整性
            if (!ValidateZipFile(zipFilePath))
            {
                Log.Warn($"[CollectionWizard] ZIP 文件损坏，安装失败: {zipFilePath}");
                // 不删除 ZIP 文件，保留用于手动解压
                return (false, zipFilePath);
            }

            // 解压并安装 Mod（成功时删除 ZIP）
            await ExtractModToModsFolderAsync(zipFilePath, _targetModsPath, mod, deleteZipOnSuccess: true);

            Log.Info($"[CollectionWizard] ✓ Mod 安装成功: {mod.Name}");
            return (true, null);
        }
        catch (Exception ex)
        {
            Log.Error($"[CollectionWizard] Mod 安装失败: {mod.Name}", ex);
            // 不删除 ZIP 文件，保留用于手动解压
            return (false, zipFilePath);
        }
    }

    /// <summary>
    /// 验证 ZIP 文件完整性
    /// </summary>
    private bool ValidateZipFile(string zipFilePath)
    {
        try
        {
            // 尝试打开 ZIP 文件来验证完整性
            using (var zipFile = new ZipFile(zipFilePath))
            {
                // 访问条目数来触发中央目录读取
                var count = zipFile.Count;
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[CollectionWizard] ZIP 文件验证失败: {Path.GetFileName(zipFilePath)}, 错误: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 解压 MOD 到 Mods 文件夹（复用 ModDownloadTask 的逻辑）
    /// </summary>
    private async Task ExtractModToModsFolderAsync(string zipFilePath, string targetModsPath, CollectionModDownloadItem mod, bool deleteZipOnSuccess = true)
    {
        var extractedManifestDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string>? rootEntries = null;
        string? tempExtractPath = null;
        bool isMultiRoot = false;  // 标记是否为多根目录

        // 检查 zip 文件结构
        using (var zipFile = new ZipFile(zipFilePath))
        {
            // 获取根目录结构
            rootEntries = zipFile
                .Cast<ZipEntry>()
                .Select(e => GetRootDirectoryName(e.Name))
                .Where(d => d != null)
                .Distinct()
                .ToList()!;

            Log.Info($"[CollectionWizard] ZIP 根目录结构: {string.Join(", ", rootEntries)}");

            // 判断是否有单一的根目录
            string? singleRootDir = rootEntries.Count == 1 ? rootEntries[0] : null;

            // 确保根目录名以斜杠结尾（用于正确移除前缀）
            if (!string.IsNullOrEmpty(singleRootDir) && !singleRootDir.EndsWith("/"))
            {
                singleRootDir += "/";
            }

            // 确定解压目标路径
            string extractPath;
            string? rootDirName = null;

            if (!string.IsNullOrEmpty(singleRootDir))
            {
                // 如果有单一的根目录，直接解压到 Mods 文件夹
                rootDirName = singleRootDir.TrimEnd('/');
                extractPath = targetModsPath;

                // 如果根目录已存在，先删除（用于覆盖更新）
                var existingDir = Path.Combine(targetModsPath, rootDirName);
                if (Directory.Exists(existingDir))
                {
                    Log.Info($"[CollectionWizard] 检测到已存在的根目录，将删除: {existingDir}");
                    Directory.Delete(existingDir, recursive: true);
                }

                Log.Info($"[CollectionWizard] 检测到单一根目录: {singleRootDir}，将解压到 Mods 文件夹");
            }
            else if (rootEntries.Count > 1)
            {
                // 多根目录：解压到临时目录，然后合并到 Mods 文件夹
                isMultiRoot = true;
                tempExtractPath = Path.Combine(Path.GetTempPath(), "SVL", "multi-root-extract", Guid.NewGuid().ToString());
                Directory.CreateDirectory(tempExtractPath);
                extractPath = tempExtractPath;

                Log.Info($"[CollectionWizard] 检测到多根目录 ({rootEntries.Count} 个): {string.Join(", ", rootEntries)}，将解压到临时目录后合并");
            }
            else
            {
                // 如果没有单一的根目录，创建以 MOD 名称命名的子目录
                var modFolderName = FileNameValidator.SanitizeFolderName(mod.Name);
                extractPath = Path.Combine(targetModsPath, modFolderName);

                // 如果目标文件夹已存在，先删除
                if (Directory.Exists(extractPath))
                {
                    Log.Info($"[CollectionWizard] 检测到已存在的目录，将删除: {extractPath}");
                    Directory.Delete(extractPath, recursive: true);
                }

                Directory.CreateDirectory(extractPath);
                Log.Info($"[CollectionWizard] 无单一根目录，创建子目录: {modFolderName}");
            }

            // 解压文件
            int extractedCount = 0;
            int totalEntries = (int)zipFile.Count;

            Log.Info($"[CollectionWizard] 开始解压，共 {totalEntries} 个条目");

            foreach (ZipEntry entry in zipFile)
            {
                // 跳过目录条目
                if (entry.IsDirectory)
                {
                    continue;
                }

                // 确定目标路径（保留完整的相对路径结构）
                var destinationPath = Path.Combine(extractPath, entry.Name);

                // 确保目标目录存在
                var destinationDir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(destinationDir) && !Directory.Exists(destinationDir))
                {
                    Directory.CreateDirectory(destinationDir);
                }

                // 解压文件（如果文件已存在则覆盖）
                if (File.Exists(destinationPath))
                {
                    File.Delete(destinationPath);
                }

                // 解压文件
                using (var stream = zipFile.GetInputStream(entry))
                using (var fileStream = File.Create(destinationPath))
                {
                    stream.CopyTo(fileStream);
                }

                if (entry.Name.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    var manifestDir = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrWhiteSpace(manifestDir) && Directory.Exists(manifestDir))
                    {
                        extractedManifestDirs.Add(manifestDir);
                    }
                }

                extractedCount++;
            }

            Log.Info($"[CollectionWizard] 已解压 {extractedCount} 个文件到: {extractPath}");
        }

        // 如果是多根目录模式，将临时目录中的文件合并到 Mods 文件夹
        if (isMultiRoot && !string.IsNullOrEmpty(tempExtractPath))
        {
            Log.Info($"[CollectionWizard] 开始合并多根目录文件到 Mods 文件夹...");
            int mergedCount = 0;

            foreach (var rootEntry in rootEntries)
            {
                var sourceDir = Path.Combine(tempExtractPath, rootEntry);

                if (!Directory.Exists(sourceDir))
                    continue;

                // 检查根目录下是否只有一个子目录（处理如 Ridgeside Village/RidgesideVillage 的情况）
                var subDirs = Directory.GetDirectories(sourceDir);
                string actualSourceDir = sourceDir;
                string targetDirName = rootEntry;

                if (subDirs.Length == 1)
                {
                    // 只有一个子目录，检查是否没有文件直接在根目录下
                    var filesInRoot = Directory.GetFiles(sourceDir);
                    if (filesInRoot.Length == 0)
                    {
                        // 根目录只有文件，使用子目录作为实际源
                        var subDirName = Path.GetFileName(subDirs[0]);
                        actualSourceDir = subDirs[0];
                        targetDirName = subDirName;
                        Log.Info($"[CollectionWizard] 检测到单子目录结构: {rootEntry}/{subDirName}，使用子目录");
                    }
                }

                var targetDir = Path.Combine(targetModsPath, targetDirName);
                Log.Info($"[CollectionWizard] 合并目录: {targetDirName}");
                CopyDirectoryRecursive(actualSourceDir, targetDir);
                mergedCount++;
            }

            Log.Info($"[CollectionWizard] ✓ 合并了 {mergedCount} 个根目录");

            // 清理临时目录
            try
            {
                Directory.Delete(tempExtractPath, recursive: true);
                Log.Info($"[CollectionWizard] 已清理临时目录: {tempExtractPath}");
            }
            catch (Exception ex)
            {
                Log.Warn($"[CollectionWizard] 清理临时目录失败: {ex.Message}");
            }
        }

        // 处理嵌套模组（内部会调用 WriteSourceCredentialFilesAsync）
        await NormalizeExtractedModDirectoriesAsync(targetModsPath, extractedManifestDirs, mod);

        // 仅在成功时删除 ZIP 文件
        if (deleteZipOnSuccess)
        {
            try
            {
                if (File.Exists(zipFilePath))
                {
                    File.Delete(zipFilePath);
                    Log.Info($"[CollectionWizard] 已删除 ZIP 文件: {Path.GetFileName(zipFilePath)}");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[CollectionWizard] 删除 ZIP 文件失败: {ex.Message}");
            }
        }
        else
        {
            Log.Info($"[CollectionWizard] 保留 ZIP 文件用于手动解压: {Path.GetFileName(zipFilePath)}");
        }

        Log.Info($"[CollectionWizard] ✓ Mod 解压成功: {mod.Name}");
    }

    /// <summary>
    /// 获取 ZIP 条目的根目录名
    /// </summary>
    private static string? GetRootDirectoryName(string entryPath)
    {
        if (string.IsNullOrEmpty(entryPath))
            return null;

        var parts = entryPath.Split('/');
        if (parts.Length > 1)
            return parts[0];

        return null;
    }

    /// <summary>
    /// 处理嵌套模组（复用 ModDownloadTask 的逻辑）
    /// </summary>
    private async Task NormalizeExtractedModDirectoriesAsync(string targetModsPath, HashSet<string> manifestDirs, CollectionModDownloadItem mod)
    {
        await Task.Run(() =>
        {
            var normalized = new HashSet<string>(manifestDirs, StringComparer.OrdinalIgnoreCase);

            try
            {
                var topDirs = Directory.GetDirectories(targetModsPath);
                foreach (var topDir in topDirs)
                {
                    var topManifest = Path.Combine(topDir, "manifest.json");
                    if (File.Exists(topManifest))
                        continue;

                    var childDirs = Directory.GetDirectories(topDir);
                    if (childDirs.Length == 0)
                        continue;

                    var childManifestDirs = childDirs
                        .Where(d => File.Exists(Path.Combine(d, "manifest.json")))
                        .ToList();

                    if (childManifestDirs.Count == 0)
                        continue;

                    Log.Info($"[CollectionWizard] 发现嵌套容器目录，准备展开: {topDir} ({childManifestDirs.Count} 个子MOD)");

                    foreach (var childDir in childManifestDirs)
                    {
                        if (!Directory.Exists(childDir))
                        {
                            continue;
                        }

                        var childName = Path.GetFileName(childDir);
                        var destination = Path.Combine(targetModsPath, childName);

                        if (string.Equals(destination.TrimEnd('\\'), childDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        {
                            normalized.Add(destination);
                            continue;
                        }

                        // 如果目标目录存在，先删除
                        if (Directory.Exists(destination))
                        {
                            Log.Warn($"[CollectionWizard] 展开嵌套时目标已存在，先删除: {destination}");
                            Directory.Delete(destination, true);
                        }

                        try
                        {
                            Directory.Move(childDir, destination);
                            normalized.Add(destination);
                            Log.Info($"[CollectionWizard] 展开嵌套: {childDir} -> {destination}");
                        }
                        catch (IOException ex)
                        {
                            Log.Warn($"[CollectionWizard] 移动失败，尝试复制: {ex.Message}");
                            CopyDirectoryRecursive(childDir, destination);
                            Directory.Delete(childDir, true);
                            if (File.Exists(Path.Combine(destination, "manifest.json")))
                                normalized.Add(destination);
                        }
                    }

                    // 清理空的父目录
                    if (Directory.Exists(topDir) && !Directory.EnumerateFileSystemEntries(topDir).Any())
                    {
                        Directory.Delete(topDir, false);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("[CollectionWizard] 处理嵌套目录结构时发生错误", ex);
            }

            // 写入源凭证文件
            WriteSourceCredentialFilesAsync(normalized, mod);
        });
    }

    /// <summary>
    /// 写入源凭证文件（记录 Mod 来源，使用 svl-source.json 格式）
    /// </summary>
    private void WriteSourceCredentialFilesAsync(IEnumerable<string> modDirs, CollectionModDownloadItem mod)
    {
        try
        {
            // 根据 SourceType 确定平台标识
            var platform = mod.SourceType?.ToLower() switch
            {
                "nexus" => "NexusMods",
                "browse" => "Browse",
                "direct" => "Direct",
                "manual" => "Manual",
                "bundle" => "Bundle",
                _ => "NexusMods"
            };

            var payload = new
            {
                platform,
                projectId = mod.ModId > 0 ? mod.ModId.ToString() : string.Empty,
                fileId = mod.FileId > 0 ? mod.FileId.ToString() : string.Empty,
                modId = mod.ModId > 0 ? mod.ModId.ToString() : string.Empty,
                modName = mod.Name ?? string.Empty,
                fileName = mod.LogicalFilename ?? string.Empty,
                downloadUrl = mod.DirectDownloadUrl ?? string.Empty,
                installedAtUtc = DateTime.UtcNow.ToString("o"),
                schemaVersion = 1
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            foreach (var modDir in modDirs)
            {
                if (!Directory.Exists(modDir))
                    continue;

                try
                {
                    var sourceFile = Path.Combine(modDir, "svl-source.json");
                    File.WriteAllText(sourceFile, json);
                    Log.Info($"[CollectionWizard] 已写入来源凭证: {sourceFile}");
                }
                catch (Exception ex)
                {
                    Log.Warn($"[CollectionWizard] 写入来源凭证失败: {modDir}", ex);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("[CollectionWizard] 写入源凭证文件失败", ex);
        }
    }

    /// <summary>
    /// 安装 Bundled Mod（从 bundled 目录查找并复制）
    /// </summary>
    private async Task<bool> InstallBundledModAsync(CollectionModDownloadItem mod)
    {
        try
        {
            if (string.IsNullOrEmpty(_collectionExtractPath))
            {
                Log.Warn($"[CollectionWizard] Collection 解压路径为空，无法安装 Bundled Mod: {mod.Name}");
                return false;
            }

            var bundledPath = Path.Combine(_collectionExtractPath, "bundled");
            if (!Directory.Exists(bundledPath))
            {
                Log.Warn($"[CollectionWizard] bundled 目录不存在: {bundledPath}");
                return false;
            }

            // 查找匹配的 bundled 目录
            var bundledDirs = Directory.GetDirectories(bundledPath);
            var matchingDir = bundledDirs.FirstOrDefault(d =>
                Path.GetFileName(d).Contains(mod.Name) ||
                Path.GetFileName(d).IndexOf(mod.Name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase) >= 0);

            if (matchingDir == null)
            {
                // 尝试模糊匹配
                matchingDir = bundledDirs.FirstOrDefault(d =>
                {
                    var dirName = Path.GetFileName(d).Replace(" ", "").Replace("-", "").ToLower();
                    var modName = mod.Name.Replace(" ", "").Replace("-", "").ToLower();
                    return dirName.Contains(modName) || modName.Contains(dirName);
                });
            }

            if (matchingDir == null)
            {
                Log.Warn($"[CollectionWizard] 未找到匹配的 bundled 目录: {mod.Name}");
                return false;
            }

            Log.Info($"[CollectionWizard] 找到 bundled 目录: {matchingDir}");

            // Bundled 目录中应该包含要替换的文件/文件夹
            // 例如: bundled/Bundled - ItsStardewTime Manifest Patch/ItsStardewTime/...
            // 需要将 ItsStardewTime 文件夹的内容复制到 Mods/ItsStardewTime/
            var subDirs = Directory.GetDirectories(matchingDir);
            if (subDirs.Length == 0)
            {
                Log.Warn($"[CollectionWizard] bundled 目录为空: {matchingDir}");
                return false;
            }

            // 处理每个子目录（通常只有一个）
            int replacedCount = 0;
            foreach (var subDir in subDirs)
            {
                var subDirName = Path.GetFileName(subDir);
                var destDir = Path.Combine(_targetModsPath, subDirName);

                Log.Info($"[CollectionWizard] 替换 bundled 文件: {subDirName}");

                // 如果目标目录存在，先删除
                if (Directory.Exists(destDir))
                {
                    Log.Debug($"[CollectionWizard] 删除现有目录: {destDir}");
                    Directory.Delete(destDir, true);
                }

                // 复制目录
                await Task.Run(() => CopyDirectoryRecursive(subDir, destDir));
                replacedCount++;
            }

            Log.Info($"[CollectionWizard] ✓ Bundled Mod 替换成功: {mod.Name}, 替换了 {replacedCount} 个目录");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[CollectionWizard] Bundled Mod 安装失败: {mod.Name}", ex);
            return false;
        }
    }

    /// <summary>
    /// 应用 bundled 文件（替换现有 Mod 的文件）
    /// </summary>
    private async Task ApplyBundledFilesAsync()
    {
        await Task.Run(async () =>
        {
            try
            {
                if (string.IsNullOrEmpty(_collectionExtractPath))
                {
                    Log.Info("[CollectionWizard] Collection 解压路径为空，跳过 bundled 文件处理");
                    return;
                }

                var bundledPath = Path.Combine(_collectionExtractPath, "bundled");
                if (!Directory.Exists(bundledPath))
                {
                    Log.Info("[CollectionWizard] bundled 目录不存在，跳过 bundled 文件处理");
                    return;
                }

                Log.Info("[CollectionWizard] 开始处理 bundled 文件...");

                // 直接遍历 bundled 目录中的所有文件夹
                var bundledDirs = Directory.GetDirectories(bundledPath);

                if (bundledDirs.Length == 0)
                {
                    Log.Info("[CollectionWizard] bundled 目录为空");
                    return;
                }

                Log.Info($"[CollectionWizard] 找到 {bundledDirs.Length} 个 bundled 文件夹");

                int replacedCount = 0;
                int skippedCount = 0;

                foreach (var bundledDir in bundledDirs)
                {
                    var bundledDirName = Path.GetFileName(bundledDir);
                    Log.Info($"[CollectionWizard] 处理 bundled 文件夹: {bundledDirName}");

                    // 获取 bundled 文件夹内的所有子文件夹
                    var subDirs = Directory.GetDirectories(bundledDir);

                    if (subDirs.Length == 0)
                    {
                        Log.Warn($"[CollectionWizard] bundled 文件夹为空: {bundledDirName}");
                        skippedCount++;
                        continue;
                    }

                    // 处理每个子文件夹（通常只有一个）
                    foreach (var subDir in subDirs)
                    {
                        var subDirName = Path.GetFileName(subDir);
                        var destDir = Path.Combine(_targetModsPath, subDirName);

                        Log.Info($"[CollectionWizard] 合并文件: {subDirName} -> Mods");

                        // 直接合并目录（会自动替换重复文件）
                        CopyDirectoryRecursive(subDir, destDir);
                        replacedCount++;
                        Log.Info($"[CollectionWizard] ✓ 合并成功: {subDirName}");
                    }
                }

                Log.Info($"[CollectionWizard] bundled 文件处理完成: {replacedCount} 成功, {skippedCount} 跳过");
            }
            catch (Exception ex)
            {
                Log.Error("[CollectionWizard] 处理 bundled 文件失败", ex);
            }
        });
    }

    /// <summary>
    /// 应用 patches 补丁
    /// </summary>
    private async Task ApplyPatchesAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                if (string.IsNullOrEmpty(_collectionExtractPath))
                {
                    Log.Info("[CollectionWizard] Collection 解压路径为空，跳过 patches 处理");
                    return;
                }

                var patchesPath = Path.Combine(_collectionExtractPath, "patches");
                if (!Directory.Exists(patchesPath))
                {
                    Log.Info("[CollectionWizard] patches 目录不存在，跳过补丁处理");
                    return;
                }

                Log.Info("[CollectionWizard] 开始处理 patches 补丁...");

                // 检查 Collection 中是否有需要补丁的 Mod
                var modsWithPatches = _modListResult?.NexusMods
                    .Where(m => m.Patches != null && m.Patches.Count > 0)
                    .ToList();

                if (modsWithPatches == null || modsWithPatches.Count == 0)
                {
                    Log.Info("[CollectionWizard] 没有需要打补丁的 Mod");
                    return;
                }

                Log.Info($"[CollectionWizard] 找到 {modsWithPatches.Count} 个需要打补丁的 Mod");

                int appliedCount = 0;
                int skippedCount = 0;

                foreach (var mod in modsWithPatches)
                {
                    // 查找 Mod 的安装路径（使用智能匹配）
                    var modInstallPath = FindModDirectory(mod.Name);

                    if (modInstallPath == null || !Directory.Exists(modInstallPath))
                    {
                        Log.Warn($"[CollectionWizard] Mod 目录不存在，跳过补丁: {mod.Name}");
                        skippedCount++;
                        continue;
                    }

                    Log.Info($"[CollectionWizard] 找到 Mod 目录: {Path.GetFileName(modInstallPath)} (原始名称: {mod.Name})");

                    // 应用补丁
                    var success = CollectionPatchService.ApplyPatches(
                        _collectionExtractPath,
                        modInstallPath,
                        mod.Name,
                        mod.Patches);

                    if (success)
                    {
                        appliedCount++;
                        Log.Info($"[CollectionWizard] ✓ 补丁应用成功: {mod.Name}");
                    }
                    else
                    {
                        skippedCount++;
                        Log.Warn($"[CollectionWizard] ✗ 补丁应用失败或跳过: {mod.Name}");
                    }
                }

                Log.Info($"[CollectionWizard] patches 处理完成: {appliedCount} 成功, {skippedCount} 跳过");
            }
            catch (Exception ex)
            {
                Log.Error("[CollectionWizard] 应用 patches 失败", ex);
            }
        });
    }

    /// <summary>
    /// 智能查找 Mod 目录（支持模糊匹配）
    /// </summary>
    private string? FindModDirectory(string modName)
    {
        if (!Directory.Exists(_targetModsPath))
            return null;

        // 1. 首先尝试精确匹配（清理后的名称）
        var safeModName = FileNameValidator.SanitizeFolderName(modName);
        var exactPath = Path.Combine(_targetModsPath, safeModName);
        if (Directory.Exists(exactPath))
            return exactPath;

        // 2. 遍历所有子目录，查找包含 manifest.json 的目录
        var allModDirs = Directory.GetDirectories(_targetModsPath);
        foreach (var dir in allModDirs)
        {
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath))
                continue;

            // 读取 manifest.json 获取 Mod 名称
            try
            {
                var manifest = JsonSerializer.Deserialize<ManifestJson>(File.ReadAllText(manifestPath));
                if (manifest != null && !string.IsNullOrEmpty(manifest.Name))
                {
                    // 检查名称是否匹配
                    if (manifest.Name.Equals(modName, StringComparison.OrdinalIgnoreCase) ||
                        manifest.Name.IndexOf(modName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        modName.IndexOf(manifest.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return dir;
                    }
                }
            }
            catch
            {
                // 忽略解析错误
            }

            // 检查目录名是否包含 Mod 名称的部分
            var dirName = Path.GetFileName(dir);
            var cleanDirName = dirName.Replace(" ", "").Replace("-", "").Replace("_", "").ToLower();
            var cleanModName = modName.Replace(" ", "").Replace("-", "").Replace("_", "").ToLower();

            if (cleanDirName.Contains(cleanModName) || cleanModName.Contains(cleanDirName))
            {
                return dir;
            }
        }

        return null;
    }

    /// <summary>
    /// manifest.json 结构
    /// </summary>
    private class ManifestJson
    {
        public string? Name { get; set; }
    }
}
