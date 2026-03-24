using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SVL.Core.Config;
using SVL.Core.Stardew.Instance;
using SVL.Core.Stardew.Mod.SMAPI;
using SVL.Core.Logging;
using SVL.Core.Download.NexusMods;
using SVL.Core.Stardew.ResourceProject.NexusMods;

namespace SVL.Core.Download;

/// <summary>
/// SMAPI 下载任务
/// </summary>
public class SmapiDownloadTask : DownloadTask
{
    private readonly string _gameBasePath;  // 游戏本体路径
    private readonly string _instanceName;   // 实例名称
    private string _smapiVersion;
    private readonly SmapiSource _source;    // SMAPI 来源
    private readonly long? _fileId;          // Curseforge/NexusMods 文件ID
    private readonly string? _downloadUrl;  // Curseforge 下载 URL
    private readonly string? _localZipPath;  // 本地zip文件路径（已下载好的文件）
    private readonly CancellationTokenSource _cts = new();
    private readonly bool _debugMode;       // Debug 模式
    private readonly bool _useIsolationInstall; // 是否安装到隔离目录（false 时直接安装到 Base 目录）

    // NexusMods 相关信息（用于 NXM 回调）
    private const long SMAPI_MOD_ID = 2400;
    private const string GAME_ID = "stardewvalley";

    // 用于等待 NXM 回调
    private TaskCompletionSource<string>? _nxmDownloadCompletionSource;
    private string? _pendingDownloadTempDir;

    /// <summary>
    /// 可选：用于任务管理器“重新打开页面”按钮。
    /// </summary>
    public string? BrowserOpenUrl { get; private set; }

    /// <summary>
    /// 是否为更新模式（更新现有实例而不是创建新实例）
    /// </summary>
    public bool IsUpdateMode { get; set; } = false;

    /// <summary>
    /// 构造函数：从网络下载SMAPI
    /// </summary>
    public SmapiDownloadTask(string gameBasePath, string instanceName, string smapiVersion, SmapiSource source = SmapiSource.GitHub, long? fileId = null, string? downloadUrl = null, bool debugMode = false, bool useIsolationInstall = true)
    {
        _gameBasePath = gameBasePath;
        _instanceName = instanceName;
        _smapiVersion = smapiVersion;
        _source = source;
        _fileId = fileId;
        _downloadUrl = downloadUrl;
        _debugMode = debugMode;
        _useIsolationInstall = useIsolationInstall;
        _localZipPath = null;

        Type = DownloadTaskType.SMAPI;
        Name = BuildTaskName(_smapiVersion, _instanceName);
        StatusMessage = "准备安装 SMAPI...";
    }

    /// <summary>
    /// 构造函数：从本地zip文件安装SMAPI（已下载好的文件）
    /// </summary>
    public SmapiDownloadTask(string gameBasePath, string instanceName, string localZipPath, SmapiSource source, bool debugMode = false, string? version = null, bool useIsolationInstall = true)
    {
        _gameBasePath = gameBasePath;
        _instanceName = instanceName;
        _localZipPath = localZipPath;
        _source = source;
        _fileId = null;
        _downloadUrl = null;
        _debugMode = debugMode;
        _useIsolationInstall = useIsolationInstall;

        // 优先使用传入的版本号，否则从文件名提取
        _smapiVersion = !string.IsNullOrEmpty(version) ? version : ExtractVersionFromPath(localZipPath);

        Type = DownloadTaskType.SMAPI;
        Name = BuildTaskName(_smapiVersion, _instanceName);
        StatusMessage = "准备安装 SMAPI...";
    }

    /// <summary>
    /// 构建规范的任务名称，避免 "SMAPI SMAPI 4.5.1" 双重前缀
    /// 格式："{version} - {instanceName}"，其中 version 已包含 SMAPI 前缀
    /// </summary>
    private static string BuildTaskName(string smapiVersion, string instanceName)
    {
        // 提取纯版本号（去掉 SMAPI 前缀）
        var pureVersion = smapiVersion;
        if (pureVersion.StartsWith("SMAPI ", StringComparison.OrdinalIgnoreCase))
            pureVersion = pureVersion.Substring(6).Trim();

        var displayVersion = string.IsNullOrEmpty(pureVersion) || pureVersion == "从文件安装"
            ? "SMAPI"
            : $"SMAPI {pureVersion}";

        return $"{displayVersion} - {instanceName}";
    }

    /// <summary>
    /// 从路径中提取版本号（用于显示）
    /// </summary>
    private string ExtractVersionFromPath(string path)
    {
        try
        {
            var fileName = Path.GetFileNameWithoutExtension(path);
            // 尝试从文件名中提取版本号（如 "smapi-4.1.10"）
            if (fileName.Contains("-"))
            {
                var parts = fileName.Split('-');
                if (parts.Length > 1)
                {
                    return parts[parts.Length - 1];
                }
            }
            return "从文件安装";
        }
        catch
        {
            return "从文件安装";
        }
    }

    public override async Task ExecuteAsync()
    {
        try
        {
            Status = DownloadTaskStatus.Downloading;
            StatusMessage = IsUpdateMode ? $"正在更新 SMAPI 到 {_smapiVersion}..." : $"正在准备安装 SMAPI {_smapiVersion}...";
            Progress = 0;  // 从0开始，作为动画演示

            Log.Info(IsUpdateMode
                ? $"[DownloadManager] 开始更新实例 {_instanceName}，SMAPI 版本: {_smapiVersion}"
                : $"[DownloadManager] 开始创建实例 {_instanceName}，SMAPI 版本: {_smapiVersion}");

            // 1. 获取游戏文件安装路径（隔离模式：versions；Base模式：根目录）
            var gameFilesPath = _useIsolationInstall
                ? InstanceIsolationService.GetVersionPath(_gameBasePath, _instanceName)
                : _gameBasePath;
            Log.Info($"[DownloadManager] 游戏文件安装路径: {gameFilesPath}");

            // Base 目录安装/更新时，若游戏仍在运行会导致部分文件无法覆盖，进而触发运行时异常。
            if (!_useIsolationInstall && TryGetRunningGameProcessInPath(_gameBasePath, out var runningProcessName))
            {
                throw new Exception($"检测到游戏进程仍在运行（{runningProcessName}），请先关闭游戏和 SMAPI 控制台后再安装/更新 Base SMAPI。");
            }

            // 2. 检查版本目录
            if (Directory.Exists(gameFilesPath))
            {
                if (IsUpdateMode)
                {
                    if (_useIsolationInstall)
                    {
                        // 更新模式：清理旧版本目录内容（保留目录本身）
                        Log.Info($"[DownloadManager] 更新模式：清理旧版本目录: {gameFilesPath}");
                        CleanupVersionDirectoryForUpdate(gameFilesPath);
                    }
                    else
                    {
                        // Base 更新模式：先移除旧 SMAPI 文件，避免运行时残留导致冲突。
                        Log.Info($"[DownloadManager] Base 更新模式：准备清理旧 SMAPI 文件: {gameFilesPath}");
                        var removed = SmapApiService.UninstallSmapiFromPath(gameFilesPath, out var uninstallError);
                        if (removed)
                        {
                            Log.Info("[DownloadManager] Base 更新模式：旧 SMAPI 文件清理完成");
                        }
                        else
                        {
                            Log.Warn($"[DownloadManager] Base 更新模式：清理旧 SMAPI 失败，将继续覆盖安装: {uninstallError}");
                        }
                    }
                }
                else
                {
                    if (_useIsolationInstall)
                    {
                        // 新建模式：版本名重复
                        Log.Error($"[DownloadManager] 版本目录已存在: {gameFilesPath}");
                        throw new Exception($"版本名称 '{_instanceName}' 已存在，请使用不同的名称");
                    }
                }
            }
            else
            {
                if (_useIsolationInstall)
                {
                    // 3. 创建隔离目录
                    Directory.CreateDirectory(gameFilesPath);
                    Log.Info($"[DownloadManager] 创建游戏文件目录: {gameFilesPath}");
                }
                else
                {
                    throw new Exception($"Base 游戏目录不存在: {gameFilesPath}");
                }
            }

            // *** 关键修改：不在安装前创建符号链接 ***
            // SMAPI installer 需要在干净的目录中工作，符号链接会导致检测失败
            // SMAPI 安装后会自动创建所需的目录结构

            Progress = 20;  // 快速跳到20%，0-20%作为演示动画

            // 4. 获取SMAPI zip文件路径
            string smapiZipPath;

            if (!string.IsNullOrEmpty(_localZipPath))
            {
                // 使用本地zip文件（已下载好的）
                smapiZipPath = _localZipPath;
                StatusMessage = "正在使用本地文件安装 SMAPI...";
                Progress = 50;  // 跳过下载阶段，直接到50%
                Log.Info($"[DownloadManager] 使用本地zip文件: {smapiZipPath}");

                if (!File.Exists(smapiZipPath))
                {
                    throw new Exception($"本地zip文件不存在: {smapiZipPath}");
                }
            }
            else if (_source == SmapiSource.NexusMods && _fileId.HasValue)
            {
                // NexusMods 下载
                var tempDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SVL",
                    "temp"
                );

                // 确保目录存在
                if (!Directory.Exists(tempDir))
                {
                    Directory.CreateDirectory(tempDir);
                }

                _pendingDownloadTempDir = tempDir;

                // 先检查缓存
                var cachedPath = NexusModsCacheService.Get(SMAPI_MOD_ID, _fileId.Value);
                if (!string.IsNullOrEmpty(cachedPath) && File.Exists(cachedPath))
                {
                    Log.Info($"[SmapiDownload] ✓ 使用缓存文件: {cachedPath}");
                    StatusMessage = "从缓存加载 SMAPI...";
                    Progress = 50;
                    smapiZipPath = cachedPath;
                }
                else
                {
                    // 尝试 API 下载
                    StatusMessage = "正在从 NexusMods 下载 SMAPI...";

                    try
                    {
                        // 创建进度报告
                        Action<NexusDownloadProgress> progress = p =>
                        {
                            // 更新文件下载进度（从 0% 开始）
                            if (p.TotalBytes > 0)
                            {
                                FileDownloadProgress = p.Percentage;
                                FileDownloadBytes = p.BytesReceived;
                                FileDownloadTotalBytes = p.TotalBytes;
                            }
                            Progress = 20 + (int)(p.Percentage * 0.3); // 20-50: 下载阶段 (30%范围)
                            StatusMessage = $"正在从 NexusMods 下载 SMAPI... {p.Percentage}%";
                            Log.Debug($"[SmapiDownload] NexusMods 下载进度: {p.Percentage}% - {p.Speed / 1024 / 1024:F2} MB/s");
                        };

                        var resultFile = await NexusDownloadWorkflow.DownloadZipAsync(
                            gameId: GAME_ID,
                            modId: SMAPI_MOD_ID,
                            fileId: _fileId.Value,
                            workingDirectory: tempDir,
                            progressCallback: progress,
                            cancellationToken: _cts.Token,
                            useCache: true);

                        Progress = 50;
                        smapiZipPath = resultFile;
                        Log.Info($"[SmapiDownload] NexusMods API 下载完成: {smapiZipPath}");
                    }
                    catch (NexusPremiumRequiredException ex)
                    {
                        // 非 Premium 用户需要浏览器下载
                        Log.Warn($"[SmapiDownload] 非 Premium 用户，切换到浏览器下载模式");
                        smapiZipPath = await WaitForBrowserDownloadAsync(ex.DownloadPageUrl);
                    }
                }
            }
            else if (_source == SmapiSource.Curseforge && !string.IsNullOrEmpty(_downloadUrl))
            {
                Log.Info($"[DownloadManager] 开始下载：{_downloadUrl}");
                StatusMessage = $"正在从 CurseForge 下载 SMAPI...";
                
                smapiZipPath = await DownloadSmapiWithProgressAsync(_downloadUrl, (progress) =>
                {
                    Progress = 20 + (int)(progress * 30); // 20-50: 下载阶段 (30%范围)
                    StatusMessage = $"正在从 CurseForge 下载 SMAPI... {progress * 100:F1}%";
                    Log.Debug($"[SmapiDownload] CurseForge 下载进度：{progress * 100:F1}%");
                });
            }
            else
            {
                // GitHub 下载也使用进度报告
                smapiZipPath = await SmapApiService.DownloadSmapiAsync(_smapiVersion, (progress, bytesRead, totalBytes) =>
                {
                    // 更新文件下载进度（从 0% 开始）
                    if (totalBytes > 0)
                    {
                        FileDownloadProgress = progress * 100;
                        FileDownloadBytes = bytesRead;
                        FileDownloadTotalBytes = totalBytes;
                    }
                    Progress = 20 + (int)(progress * 30); // 20-50: 下载阶段 (30%范围)
                });
            }

            if (string.IsNullOrEmpty(smapiZipPath) || !File.Exists(smapiZipPath))
            {
                throw new Exception("SMAPI 下载失败");
            }

            Status = DownloadTaskStatus.Installing;
            StatusMessage = "正在安装 SMAPI...";
            Progress = 50;

            bool success;
            if (_useIsolationInstall)
            {
                // 隔离实例：统一安装流程（SMAPI + 复制游戏文件）
                success = await SmapiInstallHelper.SetupIsolatedSmapiAsync(
                    smapiZipPath,
                    _gameBasePath,
                    gameFilesPath,
                    progressCallback: p =>
                    {
                        Progress = 50 + (int)(p * 42); // 50-92: 安装阶段
                        StatusMessage = $"正在安装 SMAPI... {(int)(p * 100)}%";
                    });
            }
            else
            {
                // Base 实例：直接覆盖安装到根目录，不进行游戏文件复制。
                success = await SmapApiService.InstallFromZipAsync(
                    smapiZipPath,
                    _gameBasePath,
                    progressCallback: p =>
                    {
                        Progress = 50 + (int)(p * 42);
                        StatusMessage = $"正在安装 SMAPI... {(int)(p * 100)}%";
                    },
                    enableIsolation: false);
            }

            if (_cts.Token.IsCancellationRequested)
            {
                Status = DownloadTaskStatus.Cancelled;
                StatusMessage = "已取消";
                if (_useIsolationInstall)
                {
                    CleanupVersionDirectory(gameFilesPath);
                }
                return;
            }

            if (success)
            {
                // 保存实例配置到 instances.json
                Log.Info($"[DownloadManager] 保存实例配置到 instances.json");

                // 获取游戏版本
                var gameVersion = GamePathService.GetGameVersion(_gameBasePath);

                // *** 获取实际安装的 SMAPI 版本 ***
                var actualSmapiVersion = SmapApiService.GetInstalledSmapiVersion(gameFilesPath);
                if (!string.IsNullOrEmpty(actualSmapiVersion))
                {
                    Log.Info($"[DownloadManager] 检测到实际 SMAPI 版本: {actualSmapiVersion}");
                    // 使用实际版本号覆盖估计的版本号
                    _smapiVersion = actualSmapiVersion;
                }
                else
                {
                    Log.Warn($"[DownloadManager] 无法检测实际 SMAPI 版本，使用估计值: {_smapiVersion}");
                }

                // 加载现有实例列表
                var existingInstances = SettingsService.LoadInstances();
                Log.Info($"[DownloadManager] 当前有 {existingInstances.Count} 个实例");

                if (IsUpdateMode)
                {
                    // 更新模式：查找并更新现有实例
                    var existingInstance = existingInstances.FirstOrDefault(i =>
                        i.Name == _instanceName && i.GamePath == _gameBasePath);

                    if (existingInstance != null)
                    {
                        // 更新现有实例的 SMAPI 版本
                        existingInstance.SMAPIVersion = _smapiVersion;
                        existingInstance.HasSMAPIInstalled = true;
                        existingInstance.Version = gameVersion;
                        existingInstance.IsSMAPIInstance = true;
                        Log.Info($"[DownloadManager] ✓ 更新现有实例配置: {_instanceName}, SMAPI 版本: {_smapiVersion}");
                    }
                    else
                    {
                        // 没找到现有实例，创建新的
                        Log.Warn($"[DownloadManager] 更新模式但未找到现有实例，创建新实例: {_instanceName}");
                        existingInstances.Add(new GamePathInfo
                        {
                            Id = Guid.NewGuid().ToString(),
                            Name = _instanceName,
                            GamePath = _gameBasePath,
                            Version = gameVersion,
                            IsSMAPIInstance = true,
                            SMAPIVersion = _smapiVersion,
                            HasSMAPIInstalled = true,
                            EnableIsolation = _useIsolationInstall
                        });
                    }
                }
                else
                {
                    // 新建模式：创建新实例配置
                    var newInstance = new GamePathInfo
                    {
                        Id = Guid.NewGuid().ToString(),
                        Name = _instanceName,
                        GamePath = _gameBasePath,
                        Version = gameVersion,
                        IsSMAPIInstance = true,
                        SMAPIVersion = _smapiVersion,  // 使用实际版本号
                        HasSMAPIInstalled = true,
                        EnableIsolation = _useIsolationInstall
                    };
                    existingInstances.Add(newInstance);
                    Log.Info($"[DownloadManager] ✓ 创建新实例配置: {_instanceName}");
                }

                // 保存回 instances.json
                SettingsService.SaveInstances(existingInstances);
                Log.Info($"[DownloadManager] ✓ 保存实例配置到 instances.json: {_instanceName}");

                Progress = 100;
                Status = DownloadTaskStatus.Completed;
                StatusMessage = IsUpdateMode ? $"✓ {_instanceName} SMAPI 更新完成！" : $"✓ {_instanceName} 安装完成！";
                CompletedTime = DateTime.Now;
                Log.Info(IsUpdateMode
                    ? $"[DownloadManager] ✓ 实例 {_instanceName} SMAPI 更新成功"
                    : $"[DownloadManager] ✓ 实例 {_instanceName} 创建成功");
                Log.Info($"[DownloadManager]   配置已保存到 instances.json");
                Log.Info($"[DownloadManager]   游戏文件: {gameFilesPath}");

                // *** 3秒后自动移除任务（给用户时间看到完成状态） ***
                Task.Delay(3000).ContinueWith(_ =>
                {
                    try
                    {
                        DownloadManager.Instance.RemoveTask(Id);
                        Log.Info($"[DownloadManager] 已自动移除已完成的任务: {Name}");
                    }
                    catch (Exception ex)
                    {
                        Logging.Log.Warn("[DownloadManager] 自动移除任务失败", ex);
                    }
                });
            }
            else
            {
                Status = DownloadTaskStatus.Failed;
                StatusMessage = "安装失败，请查看日志";
                var errorMsg = $"SMAPI {_smapiVersion} 安装失败";
                Log.Error($"[DownloadManager] {errorMsg}");

                // 清理创建的文件（Debug 模式下保留文件用于调试）
                if (!_debugMode)
                {
                    if (_useIsolationInstall)
                    {
                        CleanupVersionDirectory(gameFilesPath);
                        Log.Info($"[DownloadManager] 已清理版本目录");
                    }
                }
                else
                {
                    Log.Warn($"[DownloadManager] Debug 模式：保留版本文件用于调试");
                    Log.Warn($"[DownloadManager]   路径: {gameFilesPath}");
                }

                // 抛出异常以便 DownloadManager 捕获
                throw new Exception(errorMsg);
            }
        }
        catch (Exception ex)
        {
            Status = DownloadTaskStatus.Failed;
            StatusMessage = $"错误: {ex.Message}";
            CompletedTime = DateTime.Now;
            Log.Error(ex, $"[DownloadManager] SMAPI {_smapiVersion} 下载失败");

            // 清理创建的文件（更新模式下不清理，保留原有文件）
            if (!IsUpdateMode && !_debugMode)
            {
                try
                {
                    if (_useIsolationInstall)
                    {
                        var gameFilesPath = InstanceIsolationService.GetVersionPath(_gameBasePath, _instanceName);
                        CleanupVersionDirectory(gameFilesPath);
                    }
                }
                catch (Exception cleanupEx)
                {
                    Log.Warn("[DownloadManager] 清理文件失败", cleanupEx);
                }
            }
            else if (IsUpdateMode)
            {
                Log.Warn($"[DownloadManager] 更新模式：安装失败，保留原有版本文件");
            }
            else
            {
                var gameFilesPath = InstanceIsolationService.GetVersionPath(_gameBasePath, _instanceName);
                Log.Warn($"[DownloadManager] Debug 模式：保留版本文件用于调试");
                Log.Warn($"[DownloadManager]   路径: {gameFilesPath}");
            }

            // 抛出异常以便 DownloadManager 捕获并触发 TaskFailed 事件
            throw;
        }
    }

    /// <summary>
    /// 为更新清理版本目录（保留 Mods 文件夹中的用户模组，只删除 SMAPI 附带的模组）
    /// </summary>
    private void CleanupVersionDirectoryForUpdate(string versionPath)
    {
        try
        {
            if (!Directory.Exists(versionPath))
                return;

            Log.Info($"[DownloadManager] 为更新清理版本目录内容: {versionPath}");

            // 先删除 Content 目录连接（junction）
            var contentLinkPath = Path.Combine(versionPath, "Content");
            if (Directory.Exists(contentLinkPath))
            {
                try
                {
                    var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "cmd.exe",
                            Arguments = $"/c rmdir \"{contentLinkPath}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };
                    process.Start();
                    process.WaitForExit();
                    Log.Info($"[DownloadManager] ✓ 已删除 Content 目录连接: {contentLinkPath}");
                }
                catch (Exception ex)
                {
                    Log.Warn($"[DownloadManager] 删除 Content 目录连接失败: {contentLinkPath}", ex);
                }
            }

            // SMAPI 附带的模组（需要删除）
            var smapiBundledMods = new[] { "ConsoleCommands", "SaveBackup" };

            // 处理 Mods 目录：只删除 SMAPI 附带的模组，保留用户安装的模组
            var modsPath = Path.Combine(versionPath, "Mods");
            if (Directory.Exists(modsPath))
            {
                foreach (var bundledMod in smapiBundledMods)
                {
                    var modPath = Path.Combine(modsPath, bundledMod);
                    if (Directory.Exists(modPath))
                    {
                        try
                        {
                            Directory.Delete(modPath, recursive: true);
                            Log.Info($"[DownloadManager] ✓ 已删除 SMAPI 附带模组: {bundledMod}");
                        }
                        catch (Exception ex)
                        {
                            Log.Warn($"[DownloadManager] 删除 SMAPI 附带模组失败: {bundledMod}", ex);
                        }
                    }
                }
                Log.Info($"[DownloadManager] ✓ Mods 目录已处理，用户模组已保留");
            }

            // 删除目录中的所有文件（保留 Mods 目录）
            var dirInfo = new DirectoryInfo(versionPath);
            foreach (var file in dirInfo.GetFiles())
            {
                try
                {
                    file.Delete();
                }
                catch (Exception ex)
                {
                    Log.Warn($"[DownloadManager] 删除文件失败: {file.FullName}", ex);
                }
            }

            // 删除目录中的所有子目录（保留 Mods 目录）
            foreach (var dir in dirInfo.GetDirectories())
            {
                // 跳过 Mods 目录（已处理）
                if (dir.Name.Equals("Mods", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    dir.Delete(recursive: true);
                }
                catch (Exception ex)
                {
                    Log.Warn($"[DownloadManager] 删除目录失败: {dir.FullName}", ex);
                }
            }

            Log.Info($"[DownloadManager] ✓ 版本目录内容已清理（Mods 目录已保留）: {versionPath}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[DownloadManager] 清理版本目录失败: {versionPath}", ex);
        }
    }

    /// <summary>
    /// 清理版本目录
    /// </summary>
    private void CleanupVersionDirectory(string versionPath)
    {
        try
        {
            if (Directory.Exists(versionPath))
            {
                Log.Info($"[DownloadManager] 清理版本目录: {versionPath}");

                // 先删除 Content 目录连接（junction），否则会报"访问被拒绝"
                var contentLinkPath = System.IO.Path.Combine(versionPath, "Content");
                if (Directory.Exists(contentLinkPath))
                {
                    try
                    {
                        // 使用 cmd 删除目录连接
                        var process = new System.Diagnostics.Process
                        {
                            StartInfo = new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = "cmd.exe",
                                Arguments = $"/c rmdir \"{contentLinkPath}\"",
                                UseShellExecute = false,
                                CreateNoWindow = true,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true
                            }
                        };
                        process.Start();
                        process.WaitForExit();
                        Log.Info($"[DownloadManager] ✓ 已删除 Content 目录连接: {contentLinkPath}");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[DownloadManager] 删除 Content 目录连接失败: {contentLinkPath}", ex);
                    }
                }

                // *** 新架构：不再需要删除硬链接文件 ***
                // 游戏文件现在由 GameFilesListService.CopyGameFiles 复制，会在删除目录时自动清理

                // 最后删除整个目录
                Directory.Delete(versionPath, recursive: true);
                Log.Info($"[DownloadManager] ✓ 已删除版本目录: {versionPath}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[DownloadManager] 删除版本目录失败: {versionPath}", ex);
        }
    }

    private static bool TryGetRunningGameProcessInPath(string gamePath, out string processName)
    {
        processName = string.Empty;

        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
            return false;

        var normalizedGamePath = Path.GetFullPath(gamePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.HasExited)
                    continue;

                var name = process.ProcessName ?? string.Empty;
                if (!name.Contains("stardew", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("smapi", StringComparison.OrdinalIgnoreCase))
                    continue;

                var executablePath = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(executablePath))
                    continue;

                var processDir = Path.GetDirectoryName(executablePath);
                if (string.IsNullOrWhiteSpace(processDir))
                    continue;

                var normalizedProcessDir = Path.GetFullPath(processDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(normalizedProcessDir, normalizedGamePath, StringComparison.OrdinalIgnoreCase))
                {
                    processName = name;
                    return true;
                }
            }
            catch
            {
                // 某些系统进程读取 MainModule 会抛权限异常，忽略继续。
            }
        }

        return false;
    }

    /// <summary>
    /// 下载 SMAPI 并报告进度（支持缓存）
    /// </summary>
    private async Task<string> DownloadSmapiWithProgressAsync(string downloadUrl, Action<double> progressCallback)
    {
        try
        {
            var tempPath = Path.GetTempPath();
            // 提取纯版本号用于文件名，避免 "SMAPI-SMAPI 4.5.1.zip"
            var pureVersion = _smapiVersion;
            if (pureVersion.StartsWith("SMAPI ", StringComparison.OrdinalIgnoreCase))
                pureVersion = pureVersion.Substring(6).Trim();
            var zipFileName = $"SMAPI-{pureVersion}.zip";
            var zipFilePath = Path.Combine(tempPath, zipFileName);

            // *** 检查缓存 ***
            if (System.IO.File.Exists(zipFilePath))
            {
                Log.Info($"[DownloadManager] 找到缓存文件: {zipFilePath}");

                // 验证缓存文件完整性
                var fileInfo = new System.IO.FileInfo(zipFilePath);
                if (fileInfo.Length > 1024 * 1024)  // 至少 1MB
                {
                    Log.Info($"[DownloadManager] ✓ 使用缓存文件，大小: {fileInfo.Length} 字节");
                    // 缓存命中，更新进度
                    FileDownloadProgress = 100;
                    FileDownloadBytes = fileInfo.Length;
                    FileDownloadTotalBytes = fileInfo.Length;
                    progressCallback(1.0);  // 缓存命中，直接 100%
                    return zipFilePath;
                }
                else
                {
                    Log.Warn($"[DownloadManager] 缓存文件不完整（{fileInfo.Length} 字节），重新下载");
                    System.IO.File.Delete(zipFilePath);
                }
            }

            var settings = AppConfig.GetSettings();
            var threadCount = Math.Max(1, Math.Min(16, settings.DownloadSegmentThreads <= 0 ? 4 : settings.DownloadSegmentThreads));

            await HttpMultiThreadDownloader.DownloadAsync(
                downloadUrl,
                zipFilePath,
                threadCount,
                (percent, bytesRead, totalBytes, speed) =>
                {
                    var normalizedTotal = totalBytes > 0 ? totalBytes : Math.Max(bytesRead, 1);
                    FileDownloadProgress = percent;
                    FileDownloadBytes = bytesRead;
                    FileDownloadTotalBytes = normalizedTotal;

                    progressCallback(Math.Max(0, Math.Min(1, percent / 100.0)));
                },
                _cts.Token);

            var finalInfo = new FileInfo(zipFilePath);
            FileDownloadProgress = 100;
            FileDownloadBytes = finalInfo.Length;
            FileDownloadTotalBytes = finalInfo.Length;
            progressCallback(1.0);

            Log.Info($"[DownloadManager] 下载完成: {zipFilePath}");
            return zipFilePath;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DownloadManager] SMAPI 下载失败");
            throw;
        }
    }

    /// <summary>
    /// 等待用户通过浏览器下载文件
    /// </summary>
    private async Task<string> WaitForBrowserDownloadAsync(string downloadPageUrl)
    {
        Status = DownloadTaskStatus.WaitingConfirmation;
        StatusMessage = "需要打开浏览器下载（非 Premium 用户）";
        Progress = 5;

        Log.Info($"[SmapiDownload] 打开浏览器下载页面: {downloadPageUrl}");

        // 打开浏览器（添加 nmm=1 参数启用 NXM 协议）
        var urlWithNmm = downloadPageUrl;
        if (!downloadPageUrl.Contains("nmm="))
        {
            urlWithNmm = downloadPageUrl + (downloadPageUrl.Contains("?") ? "&" : "?") + "nmm=1";
        }
        BrowserOpenUrl = urlWithNmm;

        try
        {
            IO.ProcessEx.OpenUrl(urlWithNmm);
            Log.Info($"[SmapiDownload] ✓ 浏览器已打开");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SmapiDownload] 打开浏览器失败");
            throw new Exception($"打开浏览器失败: {ex.Message}");
        }

        StatusMessage = "请在浏览器中点击「Manual Download」，启动器将自动接管下载...";

        // 创建等待 NXM 回调的 TaskCompletionSource
        _nxmDownloadCompletionSource = new TaskCompletionSource<string>();

        // 注册取消令牌
        using var registration = _cts.Token.Register(() =>
        {
            _nxmDownloadCompletionSource?.TrySetCanceled();
        });

        try
        {
            // 等待 NXM 回调或超时（30分钟）
            var completedTask = await Task.WhenAny(
                _nxmDownloadCompletionSource.Task,
                Task.Delay(TimeSpan.FromMinutes(30), _cts.Token)
            );

            if (completedTask == _nxmDownloadCompletionSource.Task)
            {
                var result = await _nxmDownloadCompletionSource.Task;
                Progress = 50;
                return result;
            }
            else
            {
                throw new TimeoutException("等待浏览器下载超时（30分钟）");
            }
        }
        finally
        {
            _nxmDownloadCompletionSource = null;
        }
    }

    /// <summary>
    /// 处理 NXM URL 回调（从浏览器下载完成后调用）
    /// </summary>
    /// <param name="nxmUrl">NXM URL</param>
    /// <returns>是否成功处理</returns>
    public bool HandleNxmUrl(NxmUrl nxmUrl)
    {
        // 验证是否是等待的 NXM URL
        if (nxmUrl.ModId != SMAPI_MOD_ID || nxmUrl.FileId != _fileId)
        {
            Log.Debug($"[SmapiDownload] NXM URL 不匹配: 期望 ModId={SMAPI_MOD_ID}, FileId={_fileId}, 实际 ModId={nxmUrl.ModId}, FileId={nxmUrl.FileId}");
            return false;
        }

        if (_nxmDownloadCompletionSource == null)
        {
            Log.Warn($"[SmapiDownload] 接收到 NXM URL 但没有在等待下载");
            return false;
        }

        Log.Info($"[SmapiDownload] 接收到匹配的 NXM URL: {nxmUrl}");

        // 更新状态
        StatusMessage = "正在从 NXM 链接下载 SMAPI...";
        Progress = 25;

        // 异步下载文件
        _ = Task.Run(async () =>
        {
            try
            {
                var tempDir = _pendingDownloadTempDir ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SVL",
                    "temp"
                );

                // 使用 NexusModsService 下载（直接使用 NXM key，不尝试 API）
                var progressCallback = new Stardew.ResourceProject.NexusMods.NexusModsService.DownloadProgressCallback(
                    (progress, statusMessage, bytesRead, totalBytes) =>
                    {
                        // 更新文件下载进度（从 0% 开始）
                        if (totalBytes > 0)
                        {
                            FileDownloadProgress = bytesRead * 100.0 / totalBytes;
                            FileDownloadBytes = bytesRead;
                            FileDownloadTotalBytes = totalBytes;
                        }
                        Progress = 25 + (int)(progress * 0.25); // 25-50: 下载阶段
                        StatusMessage = $"正在下载 SMAPI... {progress:F0}%";
                    });

                var success = await Stardew.ResourceProject.NexusMods.NexusModsService.DownloadModAsync(
                    SMAPI_MOD_ID,
                    _fileId!.Value,
                    tempDir,
                    nxmUrl.Key ?? string.Empty,
                    nxmUrl.Expires?.ToString() ?? string.Empty,
                    progressCallback,
                    _cts.Token);

                if (success)
                {
                    // 查找下载的文件
                    var smapiFiles = Directory.GetFiles(tempDir, "SMAPI*.zip", SearchOption.TopDirectoryOnly)
                        .OrderByDescending(f => File.GetCreationTime(f))
                        .FirstOrDefault();

                    if (!string.IsNullOrEmpty(smapiFiles) && File.Exists(smapiFiles))
                    {
                        // 保存到缓存
                        await NexusModsCacheService.SaveAsync(smapiFiles, SMAPI_MOD_ID, _fileId!.Value);
                        Log.Info($"[SmapiDownload] NXM 下载成功: {smapiFiles}");
                        _nxmDownloadCompletionSource?.TrySetResult(smapiFiles);
                    }
                    else
                    {
                        // 尝试从缓存获取
                        var cachedPath = NexusModsCacheService.Get(SMAPI_MOD_ID, _fileId!.Value);
                        if (!string.IsNullOrEmpty(cachedPath) && File.Exists(cachedPath))
                        {
                            Log.Info($"[SmapiDownload] 从缓存获取: {cachedPath}");
                            _nxmDownloadCompletionSource?.TrySetResult(cachedPath);
                        }
                        else
                        {
                            _nxmDownloadCompletionSource?.TrySetException(new Exception("下载成功但找不到文件"));
                        }
                    }
                }
                else
                {
                    // NXM key 过期或下载失败，需要重新打开浏览器
                    Log.Warn("[SmapiDownload] NXM key 过期或下载失败，需要重新从浏览器下载");
                    StatusMessage = "下载失败，请重新在浏览器中点击「Manual Download」...";

                    // 不设置异常，让用户重新操作
                    // 等待下一次 NXM 回调
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[SmapiDownload] NXM 下载失败");
                // 不设置异常，让用户可以重试
                StatusMessage = $"下载失败: {ex.Message}，请重新在浏览器中点击「Manual Download」...";
            }
        }, _cts.Token);

        return true;
    }

    public override void Cancel()
    {
        try
        {
            _cts.Cancel();

            // 取消 NXM 等待
            _nxmDownloadCompletionSource?.TrySetCanceled();

            Status = DownloadTaskStatus.Cancelled;
            StatusMessage = "正在取消...";
            Log.Info($"[DownloadManager] SMAPI {_smapiVersion} 任务已取消");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DownloadManager] 取消任务失败");
        }
    }
}
