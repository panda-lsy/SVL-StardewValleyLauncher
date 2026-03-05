using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.Zip;
using SVL.Core.Config;
using SVL.Core.Download.NexusMods;
using SVL.Core.Logging;
using SVL.Core.Stardew.Instance;
using SVL.Core.Stardew.Mod.SMAPI;
using SVL.Core.Stardew.ResourceProject.Modpack;
using SVL.Core.Stardew.ResourceProject.NexusMods;

namespace SVL.Core.Download;

/// <summary>
/// SVL 整合包 Mod 下载状态
/// </summary>
public enum SvlModpackModStatus
{
    Pending,
    Downloading,
    Extracting,
    Completed,
    Failed,
    Skipped
}

/// <summary>
/// SVL 整合包 Mod 下载项（用于 UI 显示）
/// </summary>
public class SvlModpackModItem
{
    public string Name { get; set; } = string.Empty;
    public string UniqueId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public SvlModpackModStatus Status { get; set; } = SvlModpackModStatus.Pending;
    /// <summary>来源平台 (NexusMods / Curseforge / 本地)</summary>
    public string Platform { get; set; } = string.Empty;
    /// <summary>是否已打包在整合包 ZIP 中（不需要下载）</summary>
    public bool IsBundled { get; set; }
}

/// <summary>
/// SVL 整合包安装任务
/// 支持两种格式：
///   1. 直接包含 modpack.json（纯整合包）
///   2. 包含 SVL.exe + modpack.zip（捆绑启动器的整合包）
///
/// 安装流程：
///   1. 读取清单 (modpack.json)
///   2. 安装对应版本的 SMAPI（创建版本隔离目录、Content 链接、游戏文件）
///   3. 解压 mods/ 条目（优先使用打包的文件）
///   4. 从 sources.json 下载未打包的 Mod（跳过已解压的 Mod）
///   5. 应用 settings/ 条目（最后覆盖，确保配置与导出时一致）
///   6. 保存实例配置
/// </summary>
public class SvlModpackInstallTask : DownloadTask
{
    private readonly string _modpackFilePath;
    private readonly string _instanceName;
    private readonly string _gameBasePath;
    private readonly string _targetModsPath;
    private readonly CancellationTokenSource _cts = new();
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>
    /// 解压整合包时应跳过的 SMAPI 相关目录名（这些由 SMAPI 安装步骤单独处理）
    /// </summary>
    private static readonly HashSet<string> SmapiRelatedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "SMAPI Installer",
        "SMAPI",
        "SMAPIInstaller",
        "SMAPI.ConsoleCommands", "ConsoleCommands",
        "SMAPI.SaveBackup", "SaveBackup",
        "SMAPI.ErrorHandler", "ErrorHandler"
    };

    // NXM 浏览器回退（403 非 Premium 用户）
    private TaskCompletionSource<string>? _nxmDownloadCompletionSource;
    private long _pendingNexusModId;
    private long _pendingNexusFileId;
    private string? _pendingDownloadDisplayName;

    /// <summary>当前等待浏览器下载的 NexusMods Mod ID</summary>
    public long PendingNexusModId => _pendingNexusModId;

    /// <summary>当前等待浏览器下载的 NexusMods File ID</summary>
    public long PendingNexusFileId => _pendingNexusFileId;

    public string CurrentMod { get; private set; }
    public int TotalMods { get; private set; }
    public int InstalledMods { get; private set; }

    /// <summary>
    /// 模组列表（用于 UI 显示）
    /// </summary>
    public ObservableCollection<SvlModpackModItem> ModList { get; } = new();

    /// <summary>
    /// 当前正在处理的 Mod 项
    /// </summary>
    public SvlModpackModItem? CurrentModItem { get; private set; }

    /// <summary>
    /// NexusMods Token 过期事件，外部（Desktop 层）可订阅以显示通知
    /// 参数为触发场景描述
    /// </summary>
    public event Action<string>? NexusTokenExpired;

    /// <summary>
    /// NexusMods 非 Premium 用户（403）事件，外部可订阅以显示通知
    /// 参数为触发场景描述
    /// </summary>
    public event Action<string>? NexusPremiumRequired;

    public SvlModpackInstallTask(string modpackFilePath, string instanceName, string gameBasePath, string targetModsPath)
    {
        _modpackFilePath = modpackFilePath;
        _instanceName = instanceName;
        _gameBasePath = gameBasePath;
        _targetModsPath = targetModsPath;

        Type = DownloadTaskType.Modpack;
        Name = $"SVL 整合包安装: {Path.GetFileNameWithoutExtension(modpackFilePath)}";
        Status = DownloadTaskStatus.Pending;
        StatusMessage = "等待安装...";
        Progress = 0;
    }

    public override async Task ExecuteAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_modpackFilePath) || !File.Exists(_modpackFilePath))
                throw new FileNotFoundException("整合包文件不存在", _modpackFilePath);

            Status = DownloadTaskStatus.Installing;
            StatusMessage = "正在解析整合包...";
            Progress = 5;

            // 打开外层 zip
            using var fs = new FileStream(_modpackFilePath, FileMode.Open, FileAccess.Read);
            using var zipFile = new ZipFile(fs);

            // 判断是否为嵌套结构（含 modpack.zip）
            var nestedEntry = zipFile.GetEntry("modpack.zip");
            ZipFile modpackZip;
            Stream modpackStream;
            string tempModpackPath = null;

            if (nestedEntry != null)
            {
                // 嵌套结构：先解压 modpack.zip 到临时文件
                StatusMessage = "正在解压内部整合包...";
                Progress = 10;

                tempModpackPath = Path.Combine(Path.GetTempPath(), "SVL", "svl_install", Guid.NewGuid().ToString(), "modpack.zip");
                Directory.CreateDirectory(Path.GetDirectoryName(tempModpackPath));

                using (var entryStream = zipFile.GetInputStream(nestedEntry))
                using (var tempFile = File.Create(tempModpackPath))
                {
                    await entryStream.CopyToAsync(tempFile);
                }

                modpackStream = new FileStream(tempModpackPath, FileMode.Open, FileAccess.Read);
                modpackZip = new ZipFile(modpackStream);
            }
            else
            {
                // 直接结构：外层 zip 就是整合包
                modpackZip = zipFile;
                modpackStream = null;
            }

            try
            {
                await InstallFromZipAsync(modpackZip);
            }
            finally
            {
                if (modpackStream != null)
                {
                    modpackZip.Close();
                    modpackStream.Dispose();
                }

                // 清理临时文件
                if (tempModpackPath != null)
                {
                    try
                    {
                        var tempDir = Path.GetDirectoryName(tempModpackPath);
                        if (Directory.Exists(tempDir))
                            Directory.Delete(tempDir, true);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("[SvlModpackInstallTask] 清理临时文件失败", ex);
                    }
                }
            }

            await Task.CompletedTask;
        }
        catch (OperationCanceledException)
        {
            Status = DownloadTaskStatus.Cancelled;
            StatusMessage = "已取消";
            await CleanupVersionDirectoryAsync();
        }
        catch (Exception ex)
        {
            Status = DownloadTaskStatus.Failed;
            StatusMessage = $"安装失败: {ex.Message}";
            CompletedTime = DateTime.Now;
            Log.Error(ex, "[SvlModpackInstallTask] 安装失败");
            await CleanupVersionDirectoryAsync();
            throw;
        }
    }

    /// <summary>
    /// 从 zip 文件安装整合包内容（完整流程）
    /// </summary>
    private async Task InstallFromZipAsync(ZipFile zip)
    {
        // ========== 第 1 步：读取清单 (modpack.json) ==========
        StatusMessage = "正在读取整合包清单...";
        Progress = 5;

        var manifestEntry = zip.GetEntry(ModpackManager.ManifestFileName);
        if (manifestEntry == null)
            throw new InvalidOperationException("整合包中未找到 modpack.json");

        ModpackManifest manifest;
        using (var stream = zip.GetInputStream(manifestEntry))
        using (var reader = new StreamReader(stream))
        {
            var json = await reader.ReadToEndAsync();
            manifest = JsonSerializer.Deserialize<ModpackManifest>(json);
        }

        if (manifest == null)
            throw new InvalidOperationException("无法解析 modpack.json");

        TotalMods = manifest.Mods?.Count ?? 0;
        Log.Info($"[SvlModpackInstallTask] 清单: {manifest.Name} v{manifest.Version}, SMAPI={manifest.SmapiVersion}, Game={manifest.GameVersion}, Mods={TotalMods}");

        _cts.Token.ThrowIfCancellationRequested();

        // 预读 sources.json（后续多个步骤需要）
        List<JsonElement> sourcesList = null;
        var sourcesEntry = zip.GetEntry("sources.json");
        if (sourcesEntry != null)
        {
            using var sStream = zip.GetInputStream(sourcesEntry);
            using var sReader = new StreamReader(sStream);
            var sourcesJson = await sReader.ReadToEndAsync();
            var sourcesArray = JsonSerializer.Deserialize<JsonElement>(sourcesJson);
            if (sourcesArray.ValueKind == JsonValueKind.Array)
                sourcesList = sourcesArray.EnumerateArray().ToList();
        }

        // 填充 ModList（用于 UI 显示，标记已打包的 Mod）
        PopulateModList(manifest, sourcesList, zip);

        // ========== 第 2 步：安装 SMAPI ==========
        var smapiVersion = manifest.SmapiVersion;
        var gameFilesPath = InstanceIsolationService.GetVersionPath(_gameBasePath, _instanceName);

        if (!string.IsNullOrEmpty(smapiVersion))
        {
            StatusMessage = $"正在安装 SMAPI {smapiVersion}...";
            Progress = 8;
            Log.Info($"[SvlModpackInstallTask] 步骤 2：安装 SMAPI {smapiVersion}");

            await InstallSmapiAsync(smapiVersion, gameFilesPath);
        }
        else
        {
            // 没有 SMAPI 版本信息，仅创建基本目录
            Log.Info("[SvlModpackInstallTask] 清单未指定 SMAPI 版本，跳过 SMAPI 安装");
            Directory.CreateDirectory(gameFilesPath);
            Directory.CreateDirectory(_targetModsPath);
        }

        _cts.Token.ThrowIfCancellationRequested();

        // ========== 第 3 步：解压 mods/ 条目（优先使用打包文件） ==========
        StatusMessage = "正在解压 Mod 文件...";
        Progress = 30;
        Log.Info("[SvlModpackInstallTask] 步骤 3：解压 mods/ 条目（打包文件优先）");

        // 确保目标目录存在
        Directory.CreateDirectory(_targetModsPath);

        var (extractedFiles, modsExtracted) = await ExtractModEntriesAsync(zip);

        _cts.Token.ThrowIfCancellationRequested();

        // ========== 第 4 步：从 sources.json 下载未打包的 Mod ==========
        int downloadedMods = 0;
        if (sourcesList != null && sourcesList.Count > 0)
        {
            // 收集已从 ZIP 解压的 Mod 目录名，用于跳过它们的下载
            var extractedModNames = GetExtractedModNames(zip);

            StatusMessage = "正在从来源下载缺失的 Mod...";
            Progress = 55;
            Log.Info($"[SvlModpackInstallTask] 步骤 4：检查 {sourcesList.Count} 个 Mod 的下载来源（已打包 {extractedModNames.Count} 个）");

            downloadedMods = await DownloadModsFromSourcesAsync(sourcesList, extractedModNames);
            Log.Info($"[SvlModpackInstallTask] 来源下载完成: {downloadedMods} 个 Mod 已下载");
        }
        else
        {
            Log.Info("[SvlModpackInstallTask] 无 sources.json 或为空，跳过 Mod 下载");
        }

        _cts.Token.ThrowIfCancellationRequested();

        // ========== 第 5 步：应用 settings/ 条目（最后覆盖） ==========
        int settingsApplied = await ApplySettingsEntriesAsync(zip);

        _cts.Token.ThrowIfCancellationRequested();

        // ========== 第 6 步：写入来源信息 & 保存实例配置 ==========
        StatusMessage = "正在保存配置...";
        Progress = 90;

        // 写入 svl-source.json 到各 mod 目录
        if (sourcesList != null)
        {
            WriteSourceCredentials(sourcesList);
        }

        // 保存整合包图标到版本目录（如果包内提供）
        var versionIconPath = ExtractPackIconToVersionDirectory(zip, gameFilesPath);

        // 保存实例配置到 instances.json
        await SaveInstanceConfigAsync(manifest, gameFilesPath, versionIconPath);

        // ========== 完成 ==========
        Progress = 95;
        StatusMessage = "正在收尾...";

        CompletedTime = DateTime.Now;
        Status = DownloadTaskStatus.Completed;

        if (modsExtracted == 0 && downloadedMods == 0 && TotalMods > 0)
        {
            StatusMessage = $"已完成：{manifest.Name}（仅导入元数据和配置，未包含 Mod 文件）";
            Log.Warn($"[SvlModpackInstallTask] 整合包未包含 Mod 文件，仅有 {extractedFiles} 个设置文件。清单中列出了 {TotalMods} 个 Mod");
        }
        else
        {
            StatusMessage = $"已完成：{manifest.Name}（{modsExtracted} 个 Mod 解压，{downloadedMods} 个 Mod 下载，{settingsApplied} 个设置文件）";
        }
        Progress = 100;

        Log.Info($"[SvlModpackInstallTask] 安装完成: instance={_instanceName}, extracted={modsExtracted}, downloaded={downloadedMods}, settings={settingsApplied}, target={_targetModsPath}");
    }

    #region 步骤 2：安装 SMAPI

    /// <summary>
    /// 安装 SMAPI：多源下载（NexusMods → GitHub → Curseforge）→ 创建版本隔离目录 → Content 链接 → 安装 SMAPI → 复制游戏文件。
    /// 所有源均失败时直接抛出异常，导致任务失败。
    /// 使用 SmapiInstallHelper 共享方法。
    /// </summary>
    private async Task InstallSmapiAsync(string smapiVersion, string gameFilesPath)
    {
        // 2a. 检查版本目录
        if (Directory.Exists(gameFilesPath))
        {
            Log.Warn($"[SvlModpackInstallTask] 版本目录已存在: {gameFilesPath}，将覆盖安装");
        }
        else
        {
            Directory.CreateDirectory(gameFilesPath);
            Log.Info($"[SvlModpackInstallTask] 创建版本目录: {gameFilesPath}");
        }

        var normalizedVersion = SmapiInstallHelper.NormalizeSmapiVersion(smapiVersion);

        // 2b. 多源下载 SMAPI
        Progress = 10;
        FileDownloadProgress = 0;
        FileDownloadBytes = 0;
        FileDownloadTotalBytes = 0;

        StatusMessage = $"正在下载 SMAPI {normalizedVersion}...";
        var downloadResult = await SmapiInstallHelper.DownloadSmapiZipAsync(
            smapiVersion,
            progressCallback: p =>
            {
                FileDownloadProgress = p.Percentage;
                FileDownloadBytes = p.BytesReceived;
                FileDownloadTotalBytes = p.TotalBytes;
                Progress = 10 + (int)(p.Percentage * 0.08); // 10-18
                StatusMessage = p.Message;
            },
            onPremiumRequired: async pex =>
            {
                // 403 非 Premium：立即打开浏览器等待 NXM 回调
                Log.Info("[SvlModpackInstallTask] NexusMods 非 Premium，打开浏览器回退下载 SMAPI...");
                var result = await WaitForBrowserDownloadAsync(pex, $"SMAPI {normalizedVersion}");
                // 将 Status 恢复为 Installing（WaitForBrowserDownloadAsync 会设置为 Downloading）
                Status = DownloadTaskStatus.Installing;
                return result;
            },
            cancellationToken: _cts.Token);

        // 触发 UI 通知事件
        if (downloadResult.TokenExpired)
            NexusTokenExpired?.Invoke("InstallSmapi");

        if (!downloadResult.Success)
        {
            var errorDetail = string.Join("；", downloadResult.Errors);
            var message = $"SMAPI {smapiVersion} 下载失败（所有来源均不可用）。\n尝试的来源: {errorDetail}";
            Log.Error($"[SvlModpackInstallTask] {message}");
            throw new Exception(message);
        }

        Log.Info($"[SvlModpackInstallTask] SMAPI 下载成功（{downloadResult.SuccessSource}）: {downloadResult.ZipPath}");

        // 重置文件下载进度
        FileDownloadProgress = 100;
        FileDownloadBytes = FileDownloadTotalBytes;

        // 2c-2e. 安装 SMAPI（Content 链接 + SMAPI 文件 + 游戏文件复制 + Mods 目录）
        StatusMessage = "正在安装 SMAPI...";
        Progress = 19;

        var success = await SmapiInstallHelper.SetupIsolatedSmapiAsync(
            downloadResult.ZipPath,
            _gameBasePath,
            gameFilesPath,
            modsPath: _targetModsPath,
            progressCallback: p =>
            {
                Progress = 19 + (int)(p * 9); // 19-28
                if (p < 0.15) StatusMessage = "正在创建 Content 目录链接...";
                else if (p < 0.70) StatusMessage = "正在安装 SMAPI...";
                else if (p < 0.95) StatusMessage = "正在复制游戏文件...";
                else StatusMessage = "正在完成 SMAPI 安装...";
            });

        if (!success)
        {
            Log.Warn("[SvlModpackInstallTask] SMAPI 安装失败，将继续安装 Mod");
            // 重置文件下载进度，避免 SMAPI 的数值残留到后续步骤
            FileDownloadProgress = 0;
            FileDownloadBytes = 0;
            FileDownloadTotalBytes = 0;
            return;
        }

        Progress = 28;
        // 重置文件下载进度，避免 SMAPI 的数值残留到后续步骤
        FileDownloadProgress = 0;
        FileDownloadBytes = 0;
        FileDownloadTotalBytes = 0;
        Log.Info($"[SvlModpackInstallTask] ✓ SMAPI {smapiVersion} 安装完成");
    }

    #endregion

    #region ModList 辅助方法

    /// <summary>
    /// 根据清单和 sources.json 填充 ModList，同时标记 ZIP 中已打包的 Mod
    /// </summary>
    private void PopulateModList(ModpackManifest manifest, List<JsonElement>? sourcesList, ZipFile zip)
    {
        ModList.Clear();

        // 预扫描 ZIP 中已打包的 Mod 目录名
        var bundledModNames = GetExtractedModNames(zip);

        // 构建 sources 查找字典：按 name 和 uniqueId 双重索引
        // manifest.Mods 里的 Id 是文件夹名，sources 里的 name 是显示名，二者不同
        // 需要通过 uniqueId 关联
        var sourcesByName = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var sourcesByUniqueId = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (sourcesList != null)
        {
            foreach (var src in sourcesList)
            {
                if (src.ValueKind != JsonValueKind.Object) continue;

                var name = src.TryGetProperty("name", out var n) ? n.GetString() : null;
                var uniqueId = src.TryGetProperty("uniqueId", out var u) ? u.GetString() : null;

                if (!string.IsNullOrEmpty(name) && !sourcesByName.ContainsKey(name))
                    sourcesByName[name] = src;
                if (!string.IsNullOrEmpty(uniqueId) && !sourcesByUniqueId.ContainsKey(uniqueId))
                    sourcesByUniqueId[uniqueId] = src;
            }
        }

        if (manifest.Mods != null)
        {
            foreach (var mod in manifest.Mods)
            {
                var item = new SvlModpackModItem
                {
                    Name = mod.Id ?? mod.UniqueId ?? "Unknown",
                    UniqueId = mod.UniqueId ?? string.Empty,
                    Version = mod.Version ?? string.Empty,
                    Status = SvlModpackModStatus.Pending,
                    IsBundled = bundledModNames.Contains(mod.Id ?? mod.UniqueId ?? "")
                };

                // 从 sources 中获取平台信息（优先按 UniqueId 查找，回退按 Name 查找）
                JsonElement srcEl = default;
                bool found = false;
                if (!string.IsNullOrEmpty(item.UniqueId) && sourcesByUniqueId.TryGetValue(item.UniqueId, out srcEl))
                    found = true;
                else if (sourcesByName.TryGetValue(item.Name, out srcEl))
                    found = true;

                if (found
                    && srcEl.TryGetProperty("source", out var srcProp)
                    && srcProp.ValueKind == JsonValueKind.Object
                    && srcProp.TryGetProperty("platform", out var platProp))
                {
                    item.Platform = platProp.GetString() ?? string.Empty;
                }

                ModList.Add(item);
            }
        }

        // 补充 sources 中有但 manifest 没有的（按 name 和 uniqueId 双重去重）
        if (sourcesList != null)
        {
            var existingNames = new HashSet<string>(ModList.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
            var existingUniqueIds = new HashSet<string>(
                ModList.Where(m => !string.IsNullOrEmpty(m.UniqueId)).Select(m => m.UniqueId),
                StringComparer.OrdinalIgnoreCase);

            foreach (var src in sourcesList)
            {
                if (src.ValueKind != JsonValueKind.Object) continue;

                var name = src.TryGetProperty("name", out var n) ? n.GetString() : null;
                var uniqueId = src.TryGetProperty("uniqueId", out var u) ? u.GetString() : null;

                if (string.IsNullOrEmpty(name))
                    continue;

                // 按 name 或 uniqueId 任一匹配则视为已存在
                if (existingNames.Contains(name))
                    continue;
                if (!string.IsNullOrEmpty(uniqueId) && existingUniqueIds.Contains(uniqueId))
                    continue;

                var platform = string.Empty;
                if (src.TryGetProperty("source", out var srcProp)
                    && srcProp.ValueKind == JsonValueKind.Object
                    && srcProp.TryGetProperty("platform", out var platProp))
                    platform = platProp.GetString() ?? string.Empty;

                var version = src.TryGetProperty("version", out var v) ? v.GetString() ?? string.Empty : string.Empty;

                ModList.Add(new SvlModpackModItem
                {
                    Name = name,
                    UniqueId = uniqueId ?? string.Empty,
                    Version = version,
                    Status = SvlModpackModStatus.Pending,
                    Platform = platform,
                    IsBundled = bundledModNames.Contains(name)
                });

                // 更新索引以防后续重复
                existingNames.Add(name);
                if (!string.IsNullOrEmpty(uniqueId))
                    existingUniqueIds.Add(uniqueId);
            }
        }

        var bundledCount = ModList.Count(m => m.IsBundled);
        Log.Info($"[SvlModpackInstallTask] ModList 已填充: {ModList.Count} 项（其中 {bundledCount} 个已打包）");
    }

    /// <summary>
    /// 根据名称查找 ModList 中的项（模糊匹配：完全匹配 Name 或 UniqueId）
    /// </summary>
    private SvlModpackModItem? FindModListItem(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        return ModList.FirstOrDefault(m =>
            string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.UniqueId, name, StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region 步骤 4：从 sources.json 下载未打包的 Mod

    /// <summary>
    /// 从 sources.json 中的来源信息下载 Mod
    /// 跳过已从 ZIP 解压的 Mod（bundledModNames）；仅下载缺失的 Mod
    /// </summary>
    private async Task<int> DownloadModsFromSourcesAsync(List<JsonElement> sourcesList, HashSet<string> bundledModNames)
    {
        int downloaded = 0;
        int total = sourcesList.Count;

        for (int i = 0; i < sourcesList.Count; i++)
        {
            _cts.Token.ThrowIfCancellationRequested();

            var sourceItem = sourcesList[i];

            // 跳过 null 元素
            if (sourceItem.ValueKind != JsonValueKind.Object) continue;

            // 获取 mod 基本信息
            var modName = sourceItem.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            var enabled = !sourceItem.TryGetProperty("enabled", out var enabledProp) || enabledProp.GetBoolean();

            if (string.IsNullOrEmpty(modName))
                continue;

            // 优先使用打包文件：如果该 Mod 已从 ZIP 解压，则跳过下载
            if (bundledModNames.Contains(modName))
            {
                Log.Info($"[SvlModpackInstallTask] {modName}: 已从整合包解压，跳过下载");
                var bundledItem = FindModListItem(modName);
                if (bundledItem != null && bundledItem.Status != SvlModpackModStatus.Completed)
                    bundledItem.Status = SvlModpackModStatus.Completed;
                continue;
            }

            // 获取来源凭据
            if (!sourceItem.TryGetProperty("source", out var sourceProp) || sourceProp.ValueKind == JsonValueKind.Null)
            {
                Log.Debug($"[SvlModpackInstallTask] {modName}: 无来源信息，跳过下载");
                continue;
            }

            var platform = sourceProp.TryGetProperty("platform", out var platProp) ? platProp.GetString() : null;
            var downloadUrl = sourceProp.TryGetProperty("downloadUrl", out var urlProp) ? urlProp.GetString() : null;
            var fileName = sourceProp.TryGetProperty("fileName", out var fnProp) ? fnProp.GetString() : null;
            var modId = sourceProp.TryGetProperty("modId", out var midProp) ? midProp.GetString() : null;
            var projectId = sourceProp.TryGetProperty("projectId", out var pidProp) ? pidProp.GetString() : null;
            var fileId = sourceProp.TryGetProperty("fileId", out var fidProp) ? fidProp.GetString() : null;

            // ---- 缓存优先：检查 NexusMods 缓存和下载缓存 ----
            // 如果是 NexusMods 来源且有 projectId + fileId，优先从 NexusModsCacheService 取
            if (string.Equals(platform, "NexusMods", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(projectId) && long.TryParse(projectId, out var cacheModId)
                && !string.IsNullOrEmpty(fileId) && long.TryParse(fileId, out var cacheFileId)
                && cacheModId > 0 && cacheFileId > 0)
            {
                var cachedPath = NexusModsCacheService.Get(cacheModId, cacheFileId);
                if (!string.IsNullOrEmpty(cachedPath) && File.Exists(cachedPath))
                {
                    Log.Info($"[SvlModpackInstallTask] 缓存命中 (NexusMods): {modName} (modId={cacheModId}, fileId={cacheFileId})");

                    var modListItemCached = FindModListItem(modName);
                    if (modListItemCached != null)
                    {
                        modListItemCached.Status = SvlModpackModStatus.Downloading;
                        CurrentModItem = modListItemCached;
                    }

                    var percentCached = 55 + (int)(i * 25.0 / Math.Max(total, 1));
                    Progress = Math.Min(percentCached, 80);
                    StatusMessage = $"正在使用缓存: {modName} ({i + 1}/{total})";
                    CurrentMod = modName;

                    try
                    {
                        var cacheTask = new ModDownloadTask(
                            modId: cacheModId.ToString(),
                            modName: modName,
                            fileName: fileName ?? Path.GetFileName(cachedPath),
                            localZipPath: cachedPath,
                            isLocalFile: true,
                            gameBasePath: _gameBasePath,
                            targetModsPath: _targetModsPath,
                            saveOnly: false,
                            sourcePlatform: platform,
                            sourceProjectId: projectId,
                            sourceFileId: fileId,
                            parentCancellationToken: _cts.Token);

                        await cacheTask.ExecuteAsync();

                        if (cacheTask.Status == DownloadTaskStatus.Completed)
                        {
                            downloaded++;
                            InstalledMods = downloaded;
                            if (modListItemCached != null) modListItemCached.Status = SvlModpackModStatus.Completed;
                            Log.Info($"[SvlModpackInstallTask] ✓ 缓存安装成功: {modName}");
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[SvlModpackInstallTask] 缓存安装失败: {modName} - {ex.Message}，将尝试下载");
                    }
                }
            }

            // 只有具备有效下载 URL 的才尝试直接下载
            if (string.IsNullOrEmpty(downloadUrl))
            {
                // NexusMods 平台：通过 API 查找最新文件并下载
                if (string.Equals(platform, "NexusMods", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(projectId)
                    && long.TryParse(projectId, out var nexusModId))
                {
                    Log.Info($"[SvlModpackInstallTask] {modName}: 无下载 URL，尝试通过 NexusMods API 下载 (modId={nexusModId})");

                    // 更新 ModList 状态
                    var modListItem0 = FindModListItem(modName);
                    if (modListItem0 != null)
                    {
                        modListItem0.Status = SvlModpackModStatus.Downloading;
                        CurrentModItem = modListItem0;
                    }

                    var percent0 = 55 + (int)(i * 25.0 / Math.Max(total, 1));
                    Progress = Math.Min(percent0, 80);
                    StatusMessage = $"正在下载: {modName} ({i + 1}/{total})";
                    CurrentMod = modName;

                    try
                    {
                        var nexusResult = await DownloadModFromNexusApiAsync(nexusModId, fileId, modName, fileName);
                        if (nexusResult != null)
                        {
                            downloaded++;
                            InstalledMods = downloaded;
                            if (modListItem0 != null) modListItem0.Status = SvlModpackModStatus.Completed;
                            Log.Info($"[SvlModpackInstallTask] ✓ NexusMods API 下载成功: {modName}");
                        }
                        else
                        {
                            if (modListItem0 != null) modListItem0.Status = SvlModpackModStatus.Failed;
                            Log.Warn($"[SvlModpackInstallTask] NexusMods API 下载失败: {modName}（将使用打包的文件）");
                        }
                    }
                    catch (Exception ex)
                    {
                        if (modListItem0 != null) modListItem0.Status = SvlModpackModStatus.Failed;
                        Log.Warn($"[SvlModpackInstallTask] NexusMods API 下载失败: {modName} - {ex.Message}（将使用打包的文件）");
                    }
                    continue;
                }

                Log.Debug($"[SvlModpackInstallTask] {modName}: 无下载 URL，跳过下载（将使用打包的文件）");
                continue;
            }

            // 更新 ModList 状态
            var modListItem = FindModListItem(modName);
            if (modListItem != null)
            {
                modListItem.Status = SvlModpackModStatus.Downloading;
                CurrentModItem = modListItem;
            }

            var percent = 55 + (int)(i * 25.0 / Math.Max(total, 1));
            Progress = Math.Min(percent, 80);
            StatusMessage = $"正在下载: {modName} ({i + 1}/{total})";
            CurrentMod = modName;

            try
            {
                var task = new ModDownloadTask(
                    modId: modId ?? string.Empty,
                    modName: modName,
                    fileName: fileName ?? $"{modName}.zip",
                    downloadUrl: downloadUrl,
                    gameBasePath: _gameBasePath,
                    targetModsPath: _targetModsPath,
                    saveOnly: false,
                    sourcePlatform: platform,
                    sourceProjectId: projectId,
                    sourceFileId: fileId,
                    isModpack: false,
                    parentCancellationToken: _cts.Token);

                await task.ExecuteAsync();

                if (task.Status == DownloadTaskStatus.Completed)
                {
                    downloaded++;
                    InstalledMods = downloaded;
                    if (modListItem != null) modListItem.Status = SvlModpackModStatus.Completed;
                    Log.Info($"[SvlModpackInstallTask] ✓ 下载成功: {modName}");
                }
                else
                {
                    if (modListItem != null) modListItem.Status = SvlModpackModStatus.Failed;
                    Log.Warn($"[SvlModpackInstallTask] 下载未完成: {modName}, 状态={task.Status}");
                }
            }
            catch (Exception ex)
            {
                if (modListItem != null) modListItem.Status = SvlModpackModStatus.Failed;
                Log.Warn($"[SvlModpackInstallTask] 下载失败: {modName} - {ex.Message}（将使用打包的文件）");
            }
        }

        return downloaded;
    }

    /// <summary>
    /// 通过 NexusMods API 下载 Mod（用于 sources 中只有 platform+projectId 的情况）
    /// 1. 查询文件列表找到最佳文件
    /// 2. 使用 NexusDownloadWorkflow 下载
    /// 3. 遇到 403 则走浏览器回退
    /// </summary>
    private async Task<string?> DownloadModFromNexusApiAsync(long nexusModId, string? fileIdStr, string modName, string? fileName)
    {
        _cts.Token.ThrowIfCancellationRequested();

        long nexusFileId = 0;

        // 如果已有 fileId，直接使用
        if (!string.IsNullOrEmpty(fileIdStr) && long.TryParse(fileIdStr, out var parsedFileId) && parsedFileId > 0)
        {
            nexusFileId = parsedFileId;
            Log.Info($"[SvlModpackInstallTask] 使用已有 fileId={nexusFileId} 下载 {modName}");
        }
        else
        {
            // 通过 API 获取文件列表，选择最佳文件
            Log.Info($"[SvlModpackInstallTask] 查询 NexusMods 文件列表: modId={nexusModId}");
            var files = await NexusModsService.GetModFilesAsync(nexusModId);

            if (files == null || files.Count == 0)
            {
                Log.Warn($"[SvlModpackInstallTask] NexusMods API 未返回文件列表: modId={nexusModId}");
                return null;
            }

            // 优先选择 MAIN 类别的文件，其次选择最新上传的文件
            var mainFile = files
                .Where(f => f.GetFileIdLong() > 0)
                .OrderByDescending(f => string.Equals(f.CategoryName, "MAIN", StringComparison.OrdinalIgnoreCase)
                                     || f.CategoryId == 1
                                     || f.IsPrimary)
                .ThenByDescending(f => f.UploadedTime)
                .ThenByDescending(f => f.GetFileIdLong())
                .FirstOrDefault();

            if (mainFile == null)
            {
                Log.Warn($"[SvlModpackInstallTask] 无法从文件列表中选择文件: modId={nexusModId}");
                return null;
            }

            nexusFileId = mainFile.GetFileIdLong();
            Log.Info($"[SvlModpackInstallTask] 选择文件: {mainFile.Name} (fileId={nexusFileId}, category={mainFile.CategoryName})");
        }

        // 尝试通过 Premium API 下载
        var tempDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SVL", "temp", "modpack_nexus", $"{nexusModId}_{nexusFileId}");
        Directory.CreateDirectory(tempDir);

        string zipPath;
        try
        {
            zipPath = await NexusDownloadWorkflow.DownloadZipAsync(
                gameId: "stardewvalley",
                modId: nexusModId,
                fileId: nexusFileId,
                workingDirectory: tempDir,
                progressCallback: p =>
                {
                    if (p.TotalBytes > 0)
                    {
                        FileDownloadProgress = p.Percentage;
                        FileDownloadBytes = p.BytesReceived;
                        FileDownloadTotalBytes = p.TotalBytes;
                    }
                },
                cancellationToken: _cts.Token,
                useCache: true);
        }
        catch (NexusPremiumRequiredException pex)
        {
            // 非 Premium 用户：走浏览器回退
            Log.Warn($"[SvlModpackInstallTask] 非 Premium 用户，浏览器回退: {modName} (modId={nexusModId}, fileId={nexusFileId})");
            zipPath = await WaitForBrowserDownloadAsync(pex, modName);
            // 将 Status 恢复为 Installing（WaitForBrowserDownloadAsync 会设置为 Downloading）
            Status = DownloadTaskStatus.Installing;
        }

        if (string.IsNullOrEmpty(zipPath) || !File.Exists(zipPath))
        {
            Log.Warn($"[SvlModpackInstallTask] 文件下载后未找到: {modName}");
            return null;
        }

        // 使用 ModDownloadTask 安装
        var installTask = new ModDownloadTask(
            modId: nexusModId.ToString(),
            modName: modName,
            fileName: fileName ?? Path.GetFileName(zipPath),
            localZipPath: zipPath,
            isLocalFile: true,
            gameBasePath: _gameBasePath,
            targetModsPath: _targetModsPath,
            saveOnly: false,
            sourcePlatform: "NexusMods",
            sourceProjectId: nexusModId.ToString(),
            sourceFileId: nexusFileId.ToString(),
            parentCancellationToken: _cts.Token);

        await installTask.ExecuteAsync();

        if (installTask.Status == DownloadTaskStatus.Completed)
        {
            return zipPath;
        }

        Log.Warn($"[SvlModpackInstallTask] Mod 安装未完成: {modName}, 状态={installTask.Status}");
        return null;
    }

    #endregion

    #region 步骤 3：解压 mods/ 条目

    /// <summary>
    /// 预扫描 ZIP 中 mods/ 下已打包的 Mod 目录名集合
    /// </summary>
    private static HashSet<string> GetExtractedModNames(ZipFile zip)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipEntry entry in zip)
        {
            if (entry.IsDirectory || string.IsNullOrEmpty(entry.Name))
                continue;
            if (entry.Name.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
            {
                var relativePath = entry.Name.Substring("mods/".Length);
                if (!string.IsNullOrEmpty(relativePath))
                {
                    var modDirName = relativePath.Split('/')[0];
                    if (!string.IsNullOrEmpty(modDirName))
                        names.Add(modDirName);
                }
            }
        }
        return names;
    }

    /// <summary>
    /// 解压 zip 中的 mods/ 条目到目标 Mods 路径
    /// 同时兼容旧格式（无 mods/ 前缀）
    /// </summary>
    private async Task<(int extractedFiles, int modsExtracted)> ExtractModEntriesAsync(ZipFile zip)
    {
        // 诊断：统计 zip 中各前缀的条目数量
        int totalEntries = 0, modsEntries = 0, settingsEntriesCount = 0, legacyEntries = 0;
        var metadataFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "modpack.json", "sources.json", "files.json"
        };
        bool hasModsPrefix = false;

        foreach (ZipEntry scanEntry in zip)
        {
            if (scanEntry.IsDirectory || string.IsNullOrEmpty(scanEntry.Name))
                continue;
            totalEntries++;
            if (scanEntry.Name.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
            {
                modsEntries++;
                hasModsPrefix = true;
            }
            else if (scanEntry.Name.StartsWith("settings/", StringComparison.OrdinalIgnoreCase))
                settingsEntriesCount++;
            else if (!metadataFiles.Contains(scanEntry.Name))
                legacyEntries++;
        }

        Log.Info($"[SvlModpackInstallTask] ZIP 内容分析: total={totalEntries}, mods/={modsEntries}, settings/={settingsEntriesCount}, legacy={legacyEntries}");

        int extractedFiles = 0;
        int modsExtracted = 0;
        var processedModDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ZipEntry entry in zip)
        {
            if (entry.IsDirectory || string.IsNullOrEmpty(entry.Name))
                continue;

            _cts.Token.ThrowIfCancellationRequested();

            var entryName = entry.Name;

            // mods/ 前缀的文件 → 解压到目标 Mods 路径
            if (entryName.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
            {
                var relativePath = entryName.Substring("mods/".Length);
                if (string.IsNullOrEmpty(relativePath))
                    continue;

                var modDirName = relativePath.Split('/')[0];

                // 跳过 SMAPI 相关目录（由 SMAPI 安装步骤单独处理）
                if (SmapiRelatedDirs.Contains(modDirName)
                    || modDirName.StartsWith("SMAPI ", StringComparison.OrdinalIgnoreCase))
                {
                    if (processedModDirs.Add(modDirName))
                        Log.Info($"[SvlModpackInstallTask] 跳过 SMAPI 相关目录: {modDirName}");
                    continue;
                }

                if (!string.IsNullOrEmpty(modDirName) && processedModDirs.Add(modDirName))
                {
                    // 标记上一个 mod 为完成
                    if (CurrentModItem != null && CurrentModItem.Status == SvlModpackModStatus.Extracting)
                        CurrentModItem.Status = SvlModpackModStatus.Completed;

                    modsExtracted++;
                    CurrentMod = modDirName;
                    InstalledMods = modsExtracted;
                    var percent = 30 + (int)(modsExtracted * 25.0 / Math.Max(TotalMods, 1));
                    Progress = Math.Min(percent, 54);
                    StatusMessage = $"正在解压: {modDirName} ({modsExtracted}/{TotalMods})";

                    // 更新 ModList 状态
                    var modListItem = FindModListItem(modDirName);
                    if (modListItem != null)
                    {
                        modListItem.Status = SvlModpackModStatus.Extracting;
                        CurrentModItem = modListItem;
                    }
                }

                var destPath = Path.Combine(_targetModsPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
                var destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                using (var entryStream = zip.GetInputStream(entry))
                using (var fileStream = File.Create(destPath))
                {
                    await entryStream.CopyToAsync(fileStream);
                }

                extractedFiles++;
            }
            // 兼容旧格式：没有 mods/ 前缀，且不是元数据文件或 settings/ → 直接解压到 Mods 路径
            else if (!hasModsPrefix
                && !metadataFiles.Contains(entryName)
                && !entryName.StartsWith("settings/", StringComparison.OrdinalIgnoreCase))
            {
                var modDirName = entryName.Split('/')[0];

                // 跳过 SMAPI 相关目录
                if (SmapiRelatedDirs.Contains(modDirName)
                    || modDirName.StartsWith("SMAPI ", StringComparison.OrdinalIgnoreCase))
                {
                    if (processedModDirs.Add(modDirName))
                        Log.Info($"[SvlModpackInstallTask] 跳过 SMAPI 相关目录: {modDirName}");
                    continue;
                }

                if (!string.IsNullOrEmpty(modDirName) && processedModDirs.Add(modDirName))
                {
                    // 标记上一个 mod 为完成
                    if (CurrentModItem != null && CurrentModItem.Status == SvlModpackModStatus.Extracting)
                        CurrentModItem.Status = SvlModpackModStatus.Completed;

                    modsExtracted++;
                    CurrentMod = modDirName;
                    InstalledMods = modsExtracted;
                    var percent = 30 + (int)(modsExtracted * 25.0 / Math.Max(TotalMods, 1));
                    Progress = Math.Min(percent, 54);
                    StatusMessage = $"正在解压: {modDirName} ({modsExtracted}/{TotalMods})";

                    // 更新 ModList 状态
                    var modListItem = FindModListItem(modDirName);
                    if (modListItem != null)
                    {
                        modListItem.Status = SvlModpackModStatus.Extracting;
                        CurrentModItem = modListItem;
                    }
                }

                var destPath = Path.Combine(_targetModsPath, entryName.Replace('/', Path.DirectorySeparatorChar));
                var destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                using (var entryStream = zip.GetInputStream(entry))
                using (var fileStream = File.Create(destPath))
                {
                    await entryStream.CopyToAsync(fileStream);
                }

                extractedFiles++;
            }
        }

        // 标记最后一个解压的 mod 为完成
        if (CurrentModItem != null && CurrentModItem.Status == SvlModpackModStatus.Extracting)
            CurrentModItem.Status = SvlModpackModStatus.Completed;

        Log.Info($"[SvlModpackInstallTask] 解压完成: {modsExtracted} 个 Mod, {extractedFiles} 个文件");
        return (extractedFiles, modsExtracted);
    }

    #endregion

    #region 步骤 5：应用 settings/ 条目

    /// <summary>
    /// 应用 settings/ 条目（最后覆盖 Mod 自带的默认配置）
    /// </summary>
    private async Task<int> ApplySettingsEntriesAsync(ZipFile zip)
    {
        var settingsEntries = new List<ZipEntry>();

        foreach (ZipEntry entry in zip)
        {
            if (!entry.IsDirectory && entry.Name.StartsWith("settings/", StringComparison.OrdinalIgnoreCase))
                settingsEntries.Add(entry);
        }

        if (settingsEntries.Count == 0)
            return 0;

        Progress = 82;
        StatusMessage = "正在应用设置文件...";
        Log.Info($"[SvlModpackInstallTask] 步骤 5：应用 {settingsEntries.Count} 个设置文件");

        int applied = 0;
        foreach (var entry in settingsEntries)
        {
            _cts.Token.ThrowIfCancellationRequested();

            var relativePath = entry.Name.Substring("settings/".Length);
            if (string.IsNullOrEmpty(relativePath))
                continue;

            var destPath = Path.Combine(_targetModsPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            using (var entryStream = zip.GetInputStream(entry))
            using (var fileStream = File.Create(destPath))
            {
                await entryStream.CopyToAsync(fileStream);
            }

            applied++;
            Log.Debug($"[SvlModpackInstallTask] 已覆盖设置: {relativePath}");
        }

        Log.Info($"[SvlModpackInstallTask] 设置应用完成: {applied} 个文件");
        return applied;
    }

    #endregion

    #region 步骤 6：写入来源凭据 & 保存实例配置

    /// <summary>
    /// 将 sources.json 中的来源信息写入各 mod 目录的 svl-source.json
    /// </summary>
    private void WriteSourceCredentials(List<JsonElement> sourcesList)
    {
        try
        {
            foreach (var sourceItem in sourcesList)
            {
                if (sourceItem.ValueKind != JsonValueKind.Object) continue;

                if (!sourceItem.TryGetProperty("name", out var nameProp))
                    continue;
                if (!sourceItem.TryGetProperty("source", out var sourceProp) || sourceProp.ValueKind == JsonValueKind.Null)
                    continue;

                var modName = nameProp.GetString();
                if (string.IsNullOrEmpty(modName))
                    continue;

                var modDir = Path.Combine(_targetModsPath, modName);
                if (Directory.Exists(modDir))
                {
                    var sourceFilePath = Path.Combine(modDir, "svl-source.json");
                    var sourceJson = JsonSerializer.Serialize(sourceProp, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(sourceFilePath, sourceJson);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[SvlModpackInstallTask] 写入来源信息失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 保存实例配置到 instances.json
    /// </summary>
    private async Task SaveInstanceConfigAsync(ModpackManifest manifest, string gameFilesPath, string? versionIconPath)
    {
        try
        {
            // 获取游戏版本
            var gameVersion = GamePathService.GetGameVersion(_gameBasePath);

            // 获取实际安装的 SMAPI 版本
            var actualSmapiVersion = SmapApiService.GetInstalledSmapiVersion(gameFilesPath);
            var smapiVersion = !string.IsNullOrEmpty(actualSmapiVersion)
                ? actualSmapiVersion
                : manifest.SmapiVersion;

            var existingInstances = SettingsService.LoadInstances();

            // 检查是否已存在同名实例
            var existing = existingInstances.FirstOrDefault(i =>
                i.Name == _instanceName && i.GamePath == _gameBasePath);

            if (existing != null)
            {
                // 更新现有实例
                existing.SMAPIVersion = smapiVersion ?? string.Empty;
                existing.HasSMAPIInstalled = !string.IsNullOrEmpty(smapiVersion);
                existing.Version = gameVersion;
                existing.IsSMAPIInstance = !string.IsNullOrEmpty(smapiVersion);
                existing.EnableIsolation = true;
                if (!string.IsNullOrEmpty(versionIconPath))
                {
                    existing.CustomIcon = versionIconPath;
                }
                Log.Info($"[SvlModpackInstallTask] 更新现有实例配置: {_instanceName}");
            }
            else
            {
                // 创建新实例
                var newInstance = new GamePathInfo
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = _instanceName,
                    GamePath = _gameBasePath,
                    Version = gameVersion,
                    IsSMAPIInstance = !string.IsNullOrEmpty(smapiVersion),
                    SMAPIVersion = smapiVersion ?? string.Empty,
                    HasSMAPIInstalled = !string.IsNullOrEmpty(smapiVersion),
                    EnableIsolation = true,
                    CustomIcon = versionIconPath
                };
                existingInstances.Add(newInstance);
                Log.Info($"[SvlModpackInstallTask] 创建新实例配置: {_instanceName}");
            }

            SettingsService.SaveInstances(existingInstances);
            Log.Info($"[SvlModpackInstallTask] ✓ 实例配置已保存到 instances.json");
        }
        catch (Exception ex)
        {
            Log.Warn($"[SvlModpackInstallTask] 保存实例配置失败: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    private string? ExtractPackIconToVersionDirectory(ZipFile zip, string versionRootPath)
    {
        try
        {
            var candidates = new[]
            {
                "modpack-icon.png", "modpack-icon.jpg", "modpack-icon.jpeg", "modpack-icon.gif",
                "icon.png", "icon.jpg", "icon.jpeg", "icon.gif",
                "logo.png", "logo.jpg", "logo.jpeg", "logo.gif",
                "thumbnail.png", "thumbnail.jpg", "thumbnail.jpeg", "thumbnail.gif",
                "cover.png", "cover.jpg", "cover.jpeg", "cover.gif",
                "pack-icon.png", "pack-icon.jpg", "pack-icon.jpeg", "pack-icon.gif"
            };

            ZipEntry iconEntry = null;

            foreach (var candidate in candidates)
            {
                iconEntry = zip.GetEntry(candidate);
                if (iconEntry != null)
                    break;
            }

            if (iconEntry == null)
            {
                foreach (ZipEntry entry in zip)
                {
                    if (entry.IsDirectory)
                        continue;

                    var fileName = Path.GetFileName(entry.Name).ToLowerInvariant();
                    if (candidates.Contains(fileName))
                    {
                        iconEntry = entry;
                        break;
                    }
                }
            }

            if (iconEntry == null)
                return null;

            Directory.CreateDirectory(versionRootPath);
            var ext = Path.GetExtension(iconEntry.Name);
            if (string.IsNullOrWhiteSpace(ext))
                ext = ".png";

            var targetPath = Path.Combine(versionRootPath, $".svl-instance-icon{ext}");
            using var iconStream = zip.GetInputStream(iconEntry);
            using var outFile = File.Create(targetPath);
            iconStream.CopyTo(outFile);

            Log.Info($"[SvlModpackInstallTask] 已保存整合包图标到版本目录: {targetPath}");
            return targetPath;
        }
        catch (Exception ex)
        {
            Log.Warn($"[SvlModpackInstallTask] 提取整合包图标失败: {ex.Message}");
            return null;
        }
    }

    #endregion

    public override void Cancel()
    {
        _cts.Cancel();
        _nxmDownloadCompletionSource?.TrySetCanceled();
        Status = DownloadTaskStatus.Cancelled;
        StatusMessage = "正在取消...";
    }

    #region 清理 & NXM 浏览器回退

    /// <summary>
    /// 清理版本目录（失败/取消时调用）
    /// </summary>
    private async Task CleanupVersionDirectoryAsync()
    {
        try
        {
            await Task.Run(() =>
            {
                var versionPath = InstanceIsolationService.GetVersionPath(_gameBasePath, _instanceName);
                if (!Directory.Exists(versionPath))
                    return;

                Log.Info($"[SvlModpackInstallTask] 清理版本目录: {versionPath}");

                // 先删除 Content 目录连接（junction），否则 Directory.Delete 会跟随连接删除源目录内容
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
                                CreateNoWindow = true,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true
                            }
                        };
                        process.Start();
                        process.WaitForExit();
                        Log.Info($"[SvlModpackInstallTask] ✓ 已删除 Content 目录连接");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[SvlModpackInstallTask] 删除 Content 连接失败: {ex.Message}");
                    }
                }

                // 删除整个版本目录
                try
                {
                    Directory.Delete(versionPath, recursive: true);
                    Log.Info($"[SvlModpackInstallTask] ✓ 已删除版本目录: {versionPath}");
                }
                catch (Exception ex)
                {
                    Log.Warn($"[SvlModpackInstallTask] 删除版本目录失败: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warn("[SvlModpackInstallTask] 清理版本目录过程出错", ex);
        }
    }

    /// <summary>
    /// 等待用户通过浏览器下载 SMAPI（非 Premium 用户 403 回退）。
    /// 打开 NexusMods 下载页面，等待 NXM 回调。
    /// </summary>
    private async Task<string> WaitForBrowserDownloadAsync(NexusPremiumRequiredException pex, string displayName = "SMAPI")
    {
        var previousStatus = Status;
        Status = DownloadTaskStatus.WaitingConfirmation;
        _pendingDownloadDisplayName = displayName;
        StatusMessage = $"需要从浏览器下载 {displayName}（非 Premium 用户）";
        _pendingNexusModId = pex.ModId;
        _pendingNexusFileId = pex.FileId;

        // 添加nmm=1参数启用 NXM 协议
        var downloadUrl = pex.DownloadPageUrl;
        var urlWithNmm = downloadUrl + (downloadUrl.Contains("?") ? "&" : "?") + "nmm=1";

        Log.Info($"[SvlModpackInstallTask] 打开浏览器下载 SMAPI: {urlWithNmm}");
        try
        {
            IO.ProcessEx.OpenUrl(urlWithNmm);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SvlModpackInstallTask] 打开浏览器失败");
            throw new Exception($"打开浏览器失败: {ex.Message}");
        }

        StatusMessage = "请在浏览器中点击「Manual Download」，启动器将自动接管下载...";
        _nxmDownloadCompletionSource = new TaskCompletionSource<string>();

        using var registration = _cts.Token.Register(() =>
        {
            _nxmDownloadCompletionSource?.TrySetCanceled();
        });

        try
        {
            var completedTask = await Task.WhenAny(
                _nxmDownloadCompletionSource.Task,
                Task.Delay(TimeSpan.FromMinutes(30), _cts.Token));

            if (completedTask == _nxmDownloadCompletionSource.Task)
            {
                var result = await _nxmDownloadCompletionSource.Task;
                Status = DownloadTaskStatus.Downloading;
                return result;
            }
            else
            {
                throw new TimeoutException("等待浏览器下载 SMAPI 超时（30分钟）");
            }
        }
        finally
        {
            _nxmDownloadCompletionSource = null;
        }
    }

    /// <summary>
    /// 处理 NXM URL 回调（浏览器下载完成后调用）
    /// </summary>
    public bool HandleNxmUrl(NxmUrl nxmUrl)
    {
        if (nxmUrl.ModId != _pendingNexusModId || nxmUrl.FileId != _pendingNexusFileId)
        {
            Log.Debug($"[SvlModpackInstallTask] NXM URL 不匹配: 期望 ModId={_pendingNexusModId}, FileId={_pendingNexusFileId}, 实际 ModId={nxmUrl.ModId}, FileId={nxmUrl.FileId}");
            return false;
        }

        if (_nxmDownloadCompletionSource == null)
        {
            Log.Warn("[SvlModpackInstallTask] 接收到 NXM URL 但没有在等待下载");
            return false;
        }

        Log.Info($"[SvlModpackInstallTask] 接收到匹配的 NXM URL");
        var dlName = _pendingDownloadDisplayName ?? "SMAPI";
        StatusMessage = $"正在从 NXM 链接下载 {dlName}...";

        _ = Task.Run(async () =>
        {
            try
            {
                var tempDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SVL", "temp");
                Directory.CreateDirectory(tempDir);

                var progressCallback = new NexusModsService.DownloadProgressCallback(
                    (progress, statusMessage, bytesRead, totalBytes) =>
                    {
                        if (totalBytes > 0)
                        {
                            FileDownloadProgress = bytesRead * 100.0 / totalBytes;
                            FileDownloadBytes = bytesRead;
                            FileDownloadTotalBytes = totalBytes;
                        }
                        StatusMessage = $"正在下载 {dlName}... {progress:F0}%";
                    });

                var success = await NexusModsService.DownloadModAsync(
                    _pendingNexusModId,
                    _pendingNexusFileId,
                    tempDir,
                    nxmUrl.Key ?? string.Empty,
                    nxmUrl.Expires?.ToString() ?? string.Empty,
                    progressCallback,
                    _cts.Token);

                if (success)
                {
                    // DownloadModAsync 已正确将文件保存到缓存，优先从缓存获取
                    // 注意：不要使用 SMAPI*.zip glob，因为共享临时目录中可能残留旧的 SMAPI 文件
                    // 导致非 SMAPI mod 的缓存被错误覆盖
                    var cached = NexusModsCacheService.Get(_pendingNexusModId, _pendingNexusFileId);
                    if (!string.IsNullOrEmpty(cached) && File.Exists(cached))
                    {
                        Log.Info($"[SvlModpackInstallTask] NXM 下载完成，从缓存获取: {cached}");
                        _nxmDownloadCompletionSource?.TrySetResult(cached);
                    }
                    else
                    {
                        // 缓存未命中时，查找临时目录中最新的 zip 文件
                        var newestZip = Directory.GetFiles(tempDir, "*.zip", SearchOption.TopDirectoryOnly)
                            .OrderByDescending(f => File.GetCreationTime(f))
                            .FirstOrDefault();

                        if (!string.IsNullOrEmpty(newestZip) && File.Exists(newestZip))
                        {
                            Log.Info($"[SvlModpackInstallTask] NXM 下载完成（缓存未命中，使用临时文件）: {newestZip}");
                            _nxmDownloadCompletionSource?.TrySetResult(newestZip);
                        }
                        else
                        {
                            _nxmDownloadCompletionSource?.TrySetException(new Exception("下载成功但找不到文件"));
                        }
                    }
                }
                else
                {
                    StatusMessage = "下载失败，请重新在浏览器中点击「Manual Download」...";
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[SvlModpackInstallTask] NXM 下载失败");
                StatusMessage = $"下载失败: {ex.Message}，请重新在浏览器中点击「Manual Download」...";
            }
        }, _cts.Token);

        return true;
    }

    #endregion
}
