using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SVL.Core.IO;
using SVL.Core.Logging;
using SVL.Core.Stardew.Instance;
using SVL.Core.Stardew.ResourceProject.NexusMods;
using SVL.Core.Stardew.Mod.SMAPI;

namespace SVL.Core.Download.NexusMods;

/// <summary>
/// Nexus Collection 安装任务
/// </summary>
public class NexusCollectionInstallTask : DownloadTask
{
    private readonly string _collectionFilePath;
    private readonly string _instanceName;
    private readonly string _gameBasePath;
    private readonly string _targetModsPath;
    private readonly CancellationTokenSource _cts = new();

    public NexusCollectionInstallTask(
        string collectionFilePath,
        string instanceName,
        string gameBasePath,
        string targetModsPath)
    {
        _collectionFilePath = collectionFilePath;
        _instanceName = instanceName;
        _gameBasePath = gameBasePath;
        _targetModsPath = targetModsPath;

        Type = DownloadTaskType.Modpack;
        Name = $"Collection 安装: {Path.GetFileNameWithoutExtension(collectionFilePath)}";
        Status = DownloadTaskStatus.Pending;
        StatusMessage = "等待安装...";
        Progress = 0;
    }

    public override async Task ExecuteAsync()
    {
        string? extractDir = null;
        try
        {
            if (string.IsNullOrWhiteSpace(_collectionFilePath) || !File.Exists(_collectionFilePath))
            {
                throw new FileNotFoundException("Collection 文件不存在", _collectionFilePath);
            }

            Status = DownloadTaskStatus.Installing;
            StatusMessage = "正在解压 Collection...";
            Progress = 5;

            // 1. 解压 7z 文件
            extractDir = await ExtractCollectionAsync(_collectionFilePath);

            if (_cts.IsCancellationRequested)
            {
                Status = DownloadTaskStatus.Cancelled;
                StatusMessage = "已取消";
                return;
            }

            // 2. 读取 collection.json
            StatusMessage = "正在解析 Collection 配置...";
            Progress = 10;

            var collectionJsonPath = Path.Combine(extractDir, "collection.json");
            if (!File.Exists(collectionJsonPath))
            {
                throw new FileNotFoundException("Collection JSON 文件不存在", collectionJsonPath);
            }

            var collectionJson = File.ReadAllText(collectionJsonPath);
            var collection = JsonSerializer.Deserialize<NexusCollectionJson>(collectionJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (collection?.Info == null)
            {
                throw new InvalidOperationException("无法解析 Collection JSON");
            }

            Log.Info($"[CollectionInstall] Collection: {collection.Info.Name}, 作者: {collection.Info.Author}");
            var modCount = collection.Mods?.Length ?? 0;
            Log.Info($"[CollectionInstall] Mod 数量: {modCount}");

            // 3. 检测并安装 SMAPI（优先处理）
            StatusMessage = "正在检查 SMAPI...";
            Progress = 12;

            bool smapiInstalled = await CheckAndInstallSMAPIAsync(extractDir, collection, collection.Info.GameVersions);
            if (smapiInstalled)
            {
                Log.Info("[CollectionInstall] ✓ SMAPI 安装完成");
                Progress = 15;
            }
            else
            {
                Log.Warn("[CollectionInstall] SMAPI 未安装或跳过");
            }

            // 4. 按 Phase 分组安装 Mod
            if (collection.Mods != null && collection.Mods.Length > 0)
            {
                var groupedByPhase = collection.Mods
                    .GroupBy(m => m.Phase > 0 ? m.Phase : 1)
                    .OrderBy(g => g.Key)
                    .ToList();

                int totalMods = collection.Mods.Length;
                int installedMods = 0;

                foreach (var phaseGroup in groupedByPhase)
                {
                    if (_cts.IsCancellationRequested)
                    {
                        Status = DownloadTaskStatus.Cancelled;
                        StatusMessage = "已取消";
                        return;
                    }

                    var phaseCount = phaseGroup.Count();
                    Log.Info($"[CollectionInstall] Phase {phaseGroup.Key}: {phaseCount} 个 Mod");

                    foreach (var mod in phaseGroup)
                    {
                        if (_cts.IsCancellationRequested)
                        {
                            Status = DownloadTaskStatus.Cancelled;
                            StatusMessage = "已取消";
                            return;
                        }

                        // 跳过 SMAPI（已在步骤3中安装）
                        if (IsSMAPI(mod))
                        {
                            Log.Info($"[CollectionInstall] 跳过已安装的 SMAPI: {mod.Name}");
                            installedMods++;
                            continue;
                        }

                        await InstallModAsync(extractDir, mod, collection.Info.GameVersions);

                        installedMods++;
                        var modProgress = 15 + (installedMods * 70 / totalMods);
                        Progress = modProgress;
                        StatusMessage = $"正在安装 ({installedMods}/{totalMods}): {mod.Name}";
                    }
                }
            }

            // 5. 完成
            Progress = 100;
            Status = DownloadTaskStatus.Completed;
            CompletedTime = DateTime.Now;

            StatusMessage = $"✓ Collection 安装完成: {collection.Info.Name}（{modCount} 个 Mod）";

            Log.Info($"[CollectionInstall] 安装完成: {collection.Info.Name}, instance={_instanceName}");
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
            Log.Error("[CollectionInstall] 安装失败", ex);
            throw;
        }
        finally
        {
            // 清理临时目录
            if (extractDir != null && Directory.Exists(extractDir))
            {
                try
                {
                    Directory.Delete(extractDir, true);
                    Log.Info($"[CollectionInstall] 已清理临时目录: {extractDir}");
                }
                catch (Exception ex)
                {
                    Log.Warn("[CollectionInstall] 清理临时目录失败", ex);
                }
            }
        }
    }

    /// <summary>
    /// 解压 Collection 7z 文件
    /// </summary>
    private async Task<string> ExtractCollectionAsync(string collectionFilePath)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SVL", "collections", Guid.NewGuid().ToString());

        // 智能解压 - 支持 ZIP 和 7z 格式
        bool success = SevenZipService.Extract(collectionFilePath, tempDir);

        if (!success)
        {
            throw new InvalidOperationException("无法解压 Collection 文件。请确保已安装 7-Zip 或文件格式正确。");
        }

        // 验证 collection.json 是否存在
        var collectionJsonPath = Path.Combine(tempDir, "collection.json");
        if (!File.Exists(collectionJsonPath))
        {
            // 尝试在子目录中查找（7z 可能会解压到子目录）
            var subDirs = Directory.GetDirectories(tempDir);
            if (subDirs.Length > 0)
            {
                collectionJsonPath = Path.Combine(subDirs[0], "collection.json");
                if (File.Exists(collectionJsonPath))
                {
                    // 将文件移动到根目录
                    var targetPath = Path.Combine(tempDir, "collection.json");
                    File.Copy(collectionJsonPath, targetPath, true);

                    // 移动其他目录
                    foreach (var dir in Directory.GetDirectories(subDirs[0]))
                    {
                        var targetDir = Path.Combine(tempDir, Path.GetFileName(dir));
                        if (Directory.Exists(targetDir))
                        {
                            Directory.Delete(targetDir, true);
                        }
                        Directory.Move(dir, targetDir);
                    }

                    Log.Info($"[CollectionInstall] 整理解压结构完成");
                }
            }

            if (!File.Exists(Path.Combine(tempDir, "collection.json")))
            {
                throw new FileNotFoundException("Collection JSON 文件不存在，请确认文件是有效的 Nexus Collection");
            }
        }

        return tempDir;
    }

    /// <summary>
    /// 安装单个 Mod
    /// </summary>
    private async Task InstallModAsync(string extractDir, NexusCollectionJsonMod mod, string[]? gameVersions)
    {
        if (mod.Source == null)
        {
            Log.Warn($"[CollectionInstall] Mod {mod.Name} 没有源信息，跳过");
            return;
        }

        Log.Info($"[CollectionInstall] 安装 Mod: {mod.Name}, 类型: {mod.Source.Type}");

        switch (mod.Source.Type)
        {
            case "nexus":
                await InstallNexusModAsync(mod);
                break;

            case "bundle":
                await InstallBundledModAsync(extractDir, mod);
                break;

            case "manual":
                Log.Warn($"[CollectionInstall] Mod {mod.Name} 需要手动下载，跳过");
                // TODO: 显示手动下载提示
                break;

            default:
                Log.Warn($"[CollectionInstall] Mod {mod.Name} 源类型不支持: {mod.Source.Type}");
                break;
        }

        // 应用补丁（如果有）
        if (CollectionPatchService.HasPatches(mod))
        {
            var modInstallPath = Path.Combine(_targetModsPath, mod.Name ?? $"Mod_{mod.Source.ModId}");
            if (Directory.Exists(modInstallPath))
            {
                Log.Info($"[CollectionInstall] 应用补丁: {mod.Name}");
                CollectionPatchService.ApplyPatches(extractDir, modInstallPath, mod.Name, mod.Patches);
            }
        }
    }

    /// <summary>
    /// 安装 Nexus Mod（使用 NexusModsService，支持非 Premium 用户和缓存）
    /// </summary>
    private async Task InstallNexusModAsync(NexusCollectionJsonMod mod)
    {
        if (mod.Source == null)
            throw new ArgumentException("Mod 源信息为空", nameof(mod));

        // 使用 NexusModsService.DownloadModAsync（与向导相同的下载方法）
        var progressCallback = new Stardew.ResourceProject.NexusMods.NexusModsService.DownloadProgressCallback((progress, statusMessage, bytesRead, totalBytes) =>
        {
            // 更新进度（可选）
        });

        Log.Info($"[CollectionInstall] 开始下载 Mod: {mod.Name} (ModId={mod.Source.ModId}, FileId={mod.Source.FileId})");

        var success = await Stardew.ResourceProject.NexusMods.NexusModsService.DownloadModAsync(
            mod.Source.ModId,
            mod.Source.FileId,
            _targetModsPath,
            string.Empty, // NXM key（如果有的话，从缓存或之前下载中获取）
            string.Empty, // NXM expires
            progressCallback
        );

        if (!success)
        {
            throw new Exception($"下载 Mod 失败: {mod.Name}");
        }

        Log.Info($"[CollectionInstall] ✓ Mod 安装成功: {mod.Name}");
    }

    /// <summary>
    /// 检查 Mod 是否为 SMAPI
    /// </summary>
    private bool IsSMAPI(NexusCollectionJsonMod mod)
    {
        if (mod.Source == null || string.IsNullOrEmpty(mod.Name))
            return false;

        var smapiVariants = new[] { "SMAPI", "smapi", "SMAPIInstaller" };
        return smapiVariants.Any(variant => mod.Name.IndexOf(variant, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>
    /// 检测并安装 SMAPI
    /// </summary>
    private async Task<bool> CheckAndInstallSMAPIAsync(string extractDir, NexusCollectionJson collection, string[]? gameVersions)
    {
        try
        {
            // 从 Collection 中查找 SMAPI Mod
            var smapiMod = collection.Mods?.FirstOrDefault(m => IsSMAPI(m));

            if (smapiMod != null && smapiMod.Source != null)
            {
                Log.Info($"[CollectionInstall] 找到 Collection 中的 SMAPI: {smapiMod.Name} (ModId={smapiMod.Source.ModId}, FileId={smapiMod.Source.FileId})");

                // 使用标准 Mod 安装流程安装 SMAPI
                await InstallNexusModAsync(smapiMod);
                return true;
            }

            // 如果 Collection 中没有 SMAPI，从 NexusMods 下载最新版本
            Log.Info("[CollectionInstall] Collection 中未找到 SMAPI，从 NexusMods 获取最新版本");

            // 使用缓存服务下载 SMAPI
            var smapiZipPath = await DownloadCachedSMAPIAsync();
            if (smapiZipPath == null)
            {
                Log.Warn("[CollectionInstall] SMAPI 下载失败，跳过 SMAPI 安装");
                return false;
            }

            // 安装 SMAPI
            await InstallSMAPIFromZipAsync(smapiZipPath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("[CollectionInstall] SMAPI 检测或安装失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 使用缓存下载 SMAPI
    /// </summary>
    private async Task<string?> DownloadCachedSMAPIAsync()
    {
        try
        {
            Log.Info("[CollectionInstall] 正在获取 SMAPI 最新版本信息...");

            // 从 GitHub 获取最新 SMAPI 版本
            var smapiVersions = await SmapApiService.GetAllVersionsAsync(1, 1);
            if (smapiVersions == null || smapiVersions.Count == 0)
            {
                Log.Warn("[CollectionInstall] 无法获取 SMAPI 版本信息");
                return null;
            }

            var smapiRelease = smapiVersions[0];
            var version = smapiRelease.Version;

            // 生成缓存键
            var cacheKey = DownloadCacheService.GenerateCacheKey("smapi", "latest", version);
            var minFileSize = 5 * 1024 * 1024; // 5MB

            // 检查缓存
            var cachedPath = DownloadCacheService.GetCachedFile(cacheKey, minFileSize);
            if (cachedPath != null)
            {
                Log.Info($"[CollectionInstall] 使用缓存的 SMAPI: {cachedPath}");
                return cachedPath;
            }

            Log.Info($"[CollectionInstall] 下载 SMAPI {version}...");

            // 下载 SMAPI
            var downloadPath = Path.Combine(
                Path.GetTempPath(),
                "SVL",
                "cache",
                "downloads",
                $"SMAPI-{version}.zip"
            );

            // 确保目录存在
            Directory.CreateDirectory(Path.GetDirectoryName(downloadPath)!);

            // 下载文件
            using (var httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Add("User-Agent", "SVL-StardewValleyLauncher/1.0");
                var response = await httpClient.GetAsync(smapiRelease.DownloadUrl);
                response.EnsureSuccessStatusCode();

                var fileData = await response.Content.ReadAsByteArrayAsync();
                File.WriteAllBytes(downloadPath, fileData);

                Log.Info($"[CollectionInstall] SMAPI 下载完成: {downloadPath} ({fileData.Length} 字节)");
            }

            // 保存到缓存
            await DownloadCacheService.SaveToCacheAsync(cacheKey, downloadPath);
            Log.Info($"[CollectionInstall] SMAPI 已缓存: {cacheKey}");

            return downloadPath;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CollectionInstall] SMAPI 下载失败");
            return null;
        }
    }

    /// <summary>
    /// 从 ZIP 文件安装 SMAPI（使用 SmapiInstallHelper 统一安装流程）
    /// </summary>
    private async Task InstallSMAPIFromZipAsync(string smapiZipPath)
    {
        if (!File.Exists(smapiZipPath))
            throw new FileNotFoundException("SMAPI 文件不存在", smapiZipPath);

        Log.Info($"[CollectionInstall] 正在安装 SMAPI: {smapiZipPath}");

        // 获取版本隔离路径
        var gameFilesPath = InstanceIsolationService.GetVersionPath(_gameBasePath, _instanceName);

        // 使用 SmapiInstallHelper 统一安装（Content 链接 → SMAPI 安装 → 游戏文件复制 → Mods 目录）
        var success = await SmapiInstallHelper.SetupIsolatedSmapiAsync(
            smapiZipPath,
            _gameBasePath,
            gameFilesPath,
            modsPath: _targetModsPath,
            progressCallback: p =>
            {
                StatusMessage = $"正在安装 SMAPI... {(int)(p * 100)}%";
            });

        if (!success)
        {
            Log.Warn("[CollectionInstall] SMAPI 安装失败");
            throw new Exception("SMAPI 安装失败");
        }

        Log.Info($"[CollectionInstall] ✓ SMAPI 安装完成");
    }

    /// <summary>
    /// 安装 Bundled Mod
    /// </summary>
    private async Task InstallBundledModAsync(string extractDir, NexusCollectionJsonMod mod)
    {
        var bundledPath = Path.Combine(extractDir, "bundled");
        if (!Directory.Exists(bundledPath))
        {
            throw new DirectoryNotFoundException($"bundled 目录不存在: {bundledPath}");
        }

        // 查找匹配的 bundled Mod 目录
        var bundledDirs = Directory.GetDirectories(bundledPath);
        var matchingDir = bundledDirs.FirstOrDefault(d =>
        {
            var dirName = Path.GetFileName(d);
            return dirName.Contains($"Bundled - {mod.Name}") || dirName.Contains(mod.Name);
        });

        if (matchingDir == null)
        {
            Log.Warn($"[CollectionInstall] 未找到 bundled Mod: {mod.Name}");
            return;
        }

        // 复制到 Mods 目录
        var destPath = Path.Combine(_targetModsPath, mod.Name ?? Path.GetFileName(matchingDir));

        if (Directory.Exists(destPath))
        {
            Directory.Delete(destPath, true);
        }

        CopyDirectory(matchingDir, destPath);

        Log.Info($"[CollectionInstall] ✓ Bundled Mod 安装成功: {mod.Name}");
    }

    /// <summary>
    /// 递归复制目录
    /// </summary>
    private void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, true);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(destDir, Path.GetFileName(subDir));
            CopyDirectory(subDir, destSubDir);
        }
    }

    public override void Cancel()
    {
        _cts.Cancel();
        Status = DownloadTaskStatus.Cancelled;
        StatusMessage = "正在取消...";
    }
}
