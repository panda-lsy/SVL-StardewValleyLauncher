using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ICSharpCode.SharpZipLib.Zip;
using SVL.Core.IO;
using SVL.Core.Logging;
using SVL.Core.Stardew.Instance;

namespace SVL.Core.Download;

/// <summary>
/// MOD 下载任务
/// </summary>
public class ModDownloadTask : DownloadTask
{
    private readonly string _modId;
    private readonly string _modName;
    private readonly string _fileName;
    private readonly string _downloadUrl;
    private readonly string? _gameBasePath;  // 游戏基础路径（可选）
    private readonly string? _targetModsPath;  // 目标 Mods 文件夹（可选，如果为空则使用另存为对话框）
    private readonly string? _localZipPath;  // 本地 zip 文件路径（已下载好的文件）
    private readonly bool _saveOnly;  // 只保存 ZIP 文件，不安装
    private readonly string? _sourcePlatform; // 来源平台（如 Curseforge/NexusMods）
    private readonly string? _sourceProjectId; // 来源项目 ID
    private readonly string? _sourceFileId; // 来源文件 ID
    private readonly bool _isModpack;  // 是否为整合包
    private readonly CancellationTokenSource _cts;
    private readonly CancellationToken _linkedToken;
    private readonly bool _isChildTask;  // 是否为整合包的子任务

    /// <summary>
    /// 创建的目标目录路径（用于取消或失败时删除空文件夹）
    /// </summary>
    private string? _createdTargetPath;

    /// <summary>
    /// 解压过程中创建的根目录列表（用于取消时清理）
    /// </summary>
    private readonly System.Collections.Generic.List<string> _extractedRootDirs = new();

    /// <summary>
    /// 临时解压目录（用于整合包MOD安装，确保取消时能完全清理）
    /// </summary>
    private string? _tempExtractPath;

    /// <summary>
    /// 构造函数：从网络下载 MOD 到指定路径
    /// </summary>
    /// <param name="modId">MOD ID</param>
    /// <param name="modName">MOD 名称</param>
    /// <param name="fileName">文件名</param>
    /// <param name="downloadUrl">下载 URL</param>
    /// <param name="gameBasePath">游戏基础路径（可选）</param>
    /// <param name="targetModsPath">目标 Mods 文件夹路径（如果为 null，则打开另存为对话框）</param>
    /// <param name="saveOnly">是否只保存 ZIP 文件而不安装（默认 false）</param>
    /// <param name="isModpack">是否为整合包（默认 false）</param>
    /// <param name="parentCancellationToken">父任务的取消令牌（可选）</param>
    public ModDownloadTask(
        string modId,
        string modName,
        string fileName,
        string downloadUrl,
        string? gameBasePath = null,
        string? targetModsPath = null,
        bool saveOnly = false,
        string? sourcePlatform = null,
        string? sourceProjectId = null,
        string? sourceFileId = null,
        bool isModpack = false,
        CancellationToken parentCancellationToken = default)
    {
        _modId = modId;
        _modName = modName;
        _fileName = fileName;
        _downloadUrl = downloadUrl;
        _gameBasePath = gameBasePath;
        _targetModsPath = targetModsPath;
        _localZipPath = null;
        _saveOnly = saveOnly;
        _sourcePlatform = sourcePlatform;
        _sourceProjectId = sourceProjectId;
        _sourceFileId = sourceFileId;
        _isModpack = isModpack;
        _isChildTask = parentCancellationToken.CanBeCanceled;

        // 创建取消令牌源，如果提供了父令牌则链接
        if (parentCancellationToken.CanBeCanceled)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(parentCancellationToken);
            _linkedToken = _cts.Token;
        }
        else
        {
            _cts = new CancellationTokenSource();
            _linkedToken = _cts.Token;
        }

        // 根据是否为整合包设置类型和消息
        if (isModpack)
        {
            Type = DownloadTaskType.Modpack;
            Name = $"整合包: {_modName} ({_fileName})";
            StatusMessage = saveOnly ? "准备下载整合包文件..." : "准备下载整合包...";
        }
        else
        {
            Type = DownloadTaskType.Mod;
            Name = saveOnly ? $"{_modName} ({_fileName}) [另存为]" : $"{_modName} ({_fileName})";
            StatusMessage = saveOnly ? "准备下载 MOD 文件..." : "准备下载 MOD...";
        }
    }

    /// <summary>
    /// 构造函数：从本地 zip 文件安装 MOD
    /// </summary>
    /// <param name="modId">MOD ID</param>
    /// <param name="modName">MOD 名称</param>
    /// <param name="fileName">文件名</param>
    /// <param name="localZipPath">本地 zip 文件路径</param>
    /// <param name="isLocalFile">标识这是本地文件（用于区分构造函数）</param>
    /// <param name="gameBasePath">游戏基础路径（可选）</param>
    /// <param name="targetModsPath">目标 Mods 文件夹路径</param>
    /// <param name="saveOnly">是否只保存 ZIP 文件而不安装（默认 false）</param>
    /// <param name="isModpack">是否为整合包（默认 false）</param>
    /// <param name="parentCancellationToken">父任务的取消令牌（可选）</param>
    public ModDownloadTask(
        string modId,
        string modName,
        string fileName,
        string localZipPath,
        bool isLocalFile,
        string? gameBasePath = null,
        string? targetModsPath = null,
        bool saveOnly = false,
        string? sourcePlatform = null,
        string? sourceProjectId = null,
        string? sourceFileId = null,
        bool isModpack = false,
        CancellationToken parentCancellationToken = default)
    {
        _modId = modId;
        _modName = modName;
        _fileName = fileName;
        _localZipPath = localZipPath;
        _gameBasePath = gameBasePath;
        _targetModsPath = targetModsPath;
        _downloadUrl = null;
        _saveOnly = saveOnly;
        _sourcePlatform = sourcePlatform;
        _sourceProjectId = sourceProjectId;
        _sourceFileId = sourceFileId;
        _isModpack = isModpack;
        _isChildTask = parentCancellationToken.CanBeCanceled;

        // 创建取消令牌源，如果提供了父令牌则链接
        if (parentCancellationToken.CanBeCanceled)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(parentCancellationToken);
            _linkedToken = _cts.Token;
        }
        else
        {
            _cts = new CancellationTokenSource();
            _linkedToken = _cts.Token;
        }

        // 根据是否为整合包设置类型和消息
        if (isModpack)
        {
            Type = DownloadTaskType.Modpack;
            Name = $"整合包: {_modName} ({_fileName})";
            StatusMessage = saveOnly ? "准备保存整合包文件..." : "准备安装整合包...";
        }
        else
        {
            Type = DownloadTaskType.Mod;
            Name = saveOnly ? $"{_modName} ({_fileName}) [另存为]" : $"{_modName} ({_fileName})";
            StatusMessage = saveOnly ? "准备保存 MOD 文件..." : "准备安装 MOD...";
        }
    }

    public override async Task ExecuteAsync()
    {
        try
        {
            Status = DownloadTaskStatus.Downloading;
            StatusMessage = $"正在准备下载 {_modName}...";
            Progress = 0;

            Log.Info($"[ModDownloadTask] 开始下载 MOD: {_modName}, 文件: {_fileName}");

            // 1. 确定目标路径
            string targetPath;
            if (string.IsNullOrEmpty(_targetModsPath))
            {
                // 打开另存为对话框（需要在 UI 线程上执行）
                Status = DownloadTaskStatus.WaitingConfirmation;
                StatusMessage = "请选择保存位置...";

                targetPath = await Task.Run(() => ShowSaveFileDialog());
                if (string.IsNullOrEmpty(targetPath))
                {
                    Status = DownloadTaskStatus.Cancelled;
                    StatusMessage = "用户已取消";
                    Log.Info("[ModDownloadTask] 用户已取消下载");
                    return;
                }
            }
            else
            {
                targetPath = _targetModsPath;
                // 确保目标目录存在
                if (!Directory.Exists(targetPath))
                {
                    Directory.CreateDirectory(targetPath);
                    _createdTargetPath = targetPath;  // 记录创建的目录路径
                    Log.Info($"[ModDownloadTask] 创建目标目录: {targetPath}");
                }
            }

            Status = DownloadTaskStatus.Downloading;
            Progress = 10;

            // 2. 下载 MOD（或使用本地文件）
            string zipFilePath;

            if (!string.IsNullOrEmpty(_localZipPath))
            {
                // 使用本地 zip 文件
                zipFilePath = _localZipPath;
                StatusMessage = "正在使用本地文件安装 MOD...";
                Progress = 50;

                Log.Info($"[ModDownloadTask] 使用本地文件: {zipFilePath}");

                if (!File.Exists(zipFilePath))
                {
                    throw new Exception($"本地文件不存在: {zipFilePath}");
                }
            }
            else
            {
                // 从网络下载
                zipFilePath = await DownloadModWithProgressAsync(_downloadUrl);
            }

            // 3. 根据模式处理 MOD
            Log.Info($"[ModDownloadTask] 处理 MOD 文件: {targetPath}, 只保存模式: {_saveOnly}");

            if (_saveOnly)
            {
                // 只保存 ZIP 文件，不安装
                Status = DownloadTaskStatus.Installing;
                StatusMessage = "正在保存 MOD 文件...";
                Progress = 60;

                // 如果目标 ZIP 文件路径已存在为目录（异常情况），先删除
                if (Directory.Exists(targetPath))
                {
                    Log.Info($"[ModDownloadTask] 检测到目标路径是目录，将删除: {targetPath}");
                    Directory.Delete(targetPath, recursive: true);
                }

                // 保存 ZIP 文件到用户选择的位置
                File.Copy(zipFilePath, targetPath, overwrite: true);
                Log.Info($"[ModDownloadTask] ZIP 文件已保存到: {targetPath}");

                Progress = 100;
                Status = DownloadTaskStatus.Completed;
                StatusMessage = $"✓ {_modName} 已保存到: {targetPath}";
                CompletedTime = DateTime.Now;

                Log.Info($"[ModDownloadTask] ✓ MOD 文件保存成功: {_modName}");
            }
            else
            {
                // 安装模式：解压 MOD
                Status = DownloadTaskStatus.Installing;
                StatusMessage = "正在安装 MOD...";
                Progress = 60;

                // 检查是否是 .zip 文件路径
                if (Path.GetExtension(targetPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    // 解压目标路径是 ZIP 文件所在的目录（即 Mods 文件夹）
                    var extractPath = Path.GetDirectoryName(targetPath) ?? targetPath;

                    Log.Info($"[ModDownloadTask] 将保存 ZIP 到: {targetPath}, 解压到: {extractPath}");

                    // 如果目标 ZIP 文件路径已存在为目录（异常情况），先删除
                    if (Directory.Exists(targetPath))
                    {
                        Log.Info($"[ModDownloadTask] 检测到目标路径是目录，将删除: {targetPath}");
                        Directory.Delete(targetPath, recursive: true);
                    }

                    // 先保存 ZIP 文件到用户选择的位置
                    File.Copy(zipFilePath, targetPath, overwrite: true);
                    Log.Info($"[ModDownloadTask] ZIP 文件已保存到: {targetPath}");

                    // 解压到 Mods 文件夹，ExtractModToModsFolderAsync 会自动检测并处理单一根目录
                    Log.Info($"[ModDownloadTask] 自动解压到: {extractPath}");
                    await ExtractModToModsFolderAsync(zipFilePath, extractPath);

                    StatusMessage = $"✓ {_modName} 已保存并解压完成！";

                    // 安装成功后默认删除 ZIP 文件（后续可在设置中配置）
                    try
                    {
                        if (File.Exists(targetPath))
                        {
                            File.Delete(targetPath);
                            Log.Info($"[ModDownloadTask] 已删除 ZIP 文件: {targetPath}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[ModDownloadTask] 删除 ZIP 文件失败: {ex.Message}");
                    }
                }
                else
                {
                    // 解压到指定的 Mods 文件夹
                    await ExtractModToModsFolderAsync(zipFilePath, targetPath);
                }

                Progress = 100;
                Status = DownloadTaskStatus.Completed;
                StatusMessage = $"✓ {_modName} 安装完成！";
                CompletedTime = DateTime.Now;

                Log.Info($"[ModDownloadTask] ✓ MOD 下载安装成功: {_modName}");
            }
        }
        catch (Exception ex)
        {
            Status = DownloadTaskStatus.Failed;
            StatusMessage = $"错误: {ex.Message}";
            CompletedTime = DateTime.Now;
            Log.Error(ex, $"[ModDownloadTask] MOD 下载失败: {_modName}");

            // 失败时清理创建的空目录
            CleanupCreatedTargetDirectory();

            throw;
        }
    }

    /// <summary>
    /// 显示另存为文件对话框
    /// </summary>
    private string? ShowSaveFileDialog()
    {
        try
        {
            // 需要在 UI 线程上执行
            string? result = null;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null)
            {
                dispatcher.Invoke(() =>
                {
                    var saveFileDialog = new Microsoft.Win32.SaveFileDialog
                    {
                        FileName = _fileName,
                        DefaultExt = ".zip",
                        Filter = "ZIP 文件 (*.zip)|*.zip|所有文件 (*.*)|*.*",
                        Title = "保存 MOD 文件"
                    };

                    if (saveFileDialog.ShowDialog() == true)
                    {
                        result = saveFileDialog.FileName;
                    }
                });
            }
            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[ModDownloadTask] 显示另存为对话框失败");
            return null;
        }
    }

    /// <summary>
    /// 下载 MOD 并报告进度（支持缓存和详细进度显示）
    /// </summary>
    private async Task<string> DownloadModWithProgressAsync(string downloadUrl)
    {
        try
        {
            // 生成缓存键（使用 ProjectId 和 FileId）
            var fileIdText = NormalizeId(_sourceFileId);
            // 根据是否为整合包使用不同的缓存前缀
            var cachePrefix = _isModpack ? "modpack" : "mod";
            var cacheKey = DownloadCacheService.GenerateCacheKey(cachePrefix, $"{_modId}_{fileIdText}");

            // 尝试从缓存获取
            var cachedPath = DownloadCacheService.GetCachedFile(cacheKey, minFileSize: 1024); // 至少 1KB
            if (cachedPath != null)
            {
                var cacheTypeLabel = _isModpack ? "整合包缓存" : "模组缓存";
                Log.Info($"[ModDownloadTask] 使用{cacheTypeLabel}: {cachedPath}");
                Progress = 50;
                var typeLabel = _isModpack ? "整合包" : "MOD";
                StatusMessage = $"正在使用{typeLabel}缓存...";
                return cachedPath;
            }

            // 缓存未命中，下载模组
            var effectiveDownloadUrl = await ResolvePlatformDownloadUrlAsync(downloadUrl);
            Log.Info($"[ModDownloadTask] 开始下载: {effectiveDownloadUrl}");

            // 使用自定义下载方法（支持详细进度显示）
            var targetPath = DownloadCacheService.GetCacheFilePath(cacheKey);

            // 下载文件到缓存位置
            await DownloadFileWithProgressAsync(effectiveDownloadUrl, targetPath);

            Progress = 50;
            Log.Info($"[ModDownloadTask] 下载完成: {targetPath}");
            return targetPath;
        }
        catch (Exception ex)
        {
            // 下载失败时删除可能不完整的缓存文件
            try
            {
                var fileIdText = NormalizeId(_sourceFileId);
                var cachePrefix = _isModpack ? "modpack" : "mod";
                var cacheKey = DownloadCacheService.GenerateCacheKey(cachePrefix, $"{_modId}_{fileIdText}");
                DownloadCacheService.ClearCache(cacheKey);
                Log.Info($"[ModDownloadTask] 下载失败，已清除缓存: {cacheKey}");
            }
            catch (Exception clearEx)
            {
                Log.Warn($"[ModDownloadTask] 清除缓存失败: {clearEx.Message}");
            }

            Log.Error(ex, "[ModDownloadTask] 下载失败");
            throw;
        }
    }

    /// <summary>
    /// 从 URL 下载文件并报告详细进度（速度、大小等）
    /// </summary>
    private async Task DownloadFileWithProgressAsync(string downloadUrl, string targetPath)
    {
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("User-Agent", "SVL-StardewValleyLauncher/1.0");

        if (downloadUrl.Contains("curseforge.com"))
        {
            var apiKey = CurseforgeApiService.GetApiKey();
            if (!string.IsNullOrEmpty(apiKey))
            {
                httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
                Log.Info("[ModDownloadTask] 已添加 Curseforge API Key");
            }
        }

        httpClient.Timeout = TimeSpan.FromMinutes(30);

        var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, _linkedToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? 0;
        Log.Info($"[ModDownloadTask] 文件大小: {totalBytes} 字节");

        using var fs = new FileStream(targetPath, FileMode.Create);
        using var stream = await response.Content.ReadAsStreamAsync();

        var buffer = new byte[8192];
        int bytesRead;
        long totalRead = 0;

        // 速度计算
        var startTime = DateTime.UtcNow;
        var lastUpdateTime = startTime;
        const int updateIntervalMs = 500; // 每 500ms 更新一次显示
        var typeLabel = _isModpack ? "整合包" : "MOD";

        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, _linkedToken)) > 0)
        {
            await fs.WriteAsync(buffer, 0, bytesRead, _linkedToken);
            totalRead += bytesRead;

            var now = DateTime.UtcNow;
            var elapsedMs = (int)(now - lastUpdateTime).TotalMilliseconds;

            if (elapsedMs >= updateIntervalMs && totalBytes > 0)
            {
                // 计算下载速度
                var totalElapsedSec = (now - startTime).TotalSeconds;
                var speed = totalElapsedSec > 0 ? totalRead / totalElapsedSec : 0;

                // 计算进度
                var currentProgress = (double)totalRead / totalBytes;
                var progressValue = 10 + (int)(currentProgress * 40);
                Progress = progressValue;

                // 格式化显示：百分比 + 已下载 / 总大小 (速度)
                var progressPercent = currentProgress * 100;
                var downloadedMB = totalRead / (1024.0 * 1024.0);
                var totalMB = totalBytes / (1024.0 * 1024.0);
                var speedMB = speed / (1024.0 * 1024.0);

                StatusMessage = $"正在下载{typeLabel}...\n{progressPercent:F2}%\t{downloadedMB:F1} MB / {totalMB:F1} MB ({speedMB:F1} MB/s)";

                lastUpdateTime = now;
            }
        }

        // 下载完成，显示最终状态
        var finalMB = totalRead / (1024.0 * 1024.0);
        StatusMessage = $"正在下载{typeLabel}...\n100.00%\t{finalMB:F1} MB / {finalMB:F1} MB (完成)";
    }

    private bool IsCurseforgeSource()
    {
        return string.Equals(_sourcePlatform, "Curseforge", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> ResolvePlatformDownloadUrlAsync(string downloadUrl)
    {
        if (!IsCurseforgeSource())
            return downloadUrl;

        var resolved = await ResolveCurseforgeDownloadUrlBySourceAsync();
        if (!string.IsNullOrWhiteSpace(resolved))
            return resolved;

        return downloadUrl;
    }

    private async Task<string?> ResolveCurseforgeDownloadUrlBySourceAsync()
    {
        var projectIdText = NormalizeId(_sourceProjectId);
        var fileIdText = NormalizeId(_sourceFileId);

        if (!int.TryParse(projectIdText, out var projectId) || projectId <= 0)
            return null;

        if (!int.TryParse(fileIdText, out var fileId) || fileId <= 0)
            return null;

        // 1. 尝试通过 API 获取下载链接
        var apiUrl = await CurseforgeApiService.GetFileDownloadUrlAsync(projectId, fileId);
        if (!string.IsNullOrWhiteSpace(apiUrl))
        {
            return apiUrl;
        }

        // 2. API 失败，尝试 CDN 硬解析作为 fallback
        Log.Warn($"[ModDownloadTask] API 返回空，尝试使用 CDN 硬解析: fileId={fileId}, fileName={_fileName}");
        try
        {
            return CurseforgeApiService.BuildCdnUrl(fileId, _fileName);
        }
        catch (Exception ex)
        {
            Log.Warn($"[ModDownloadTask] CDN 硬解析失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 尝试获取 Curseforge API 下载端点 URL（使用 API Key 认证）
    /// </summary>
    private string? TryGetCurseforgeApiDownloadUrl()
    {
        var fileIdText = NormalizeId(_sourceFileId);

        if (!int.TryParse(fileIdText, out var fileId) || fileId <= 0)
            return null;

        // 使用 Curseforge API 下载端点
        return CurseforgeApiService.GetFileDownloadUrl(fileId);
    }

    /// <summary>
    /// 解压 MOD 到 Mods 文件夹
    /// </summary>
    private async Task ExtractModToModsFolderAsync(string zipFilePath, string targetModsPath)
    {
        try
        {
            Log.Info($"[ModDownloadTask] 解压 MOD: {zipFilePath} -> {targetModsPath}");
            var extractedManifestDirs = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 检查 zip 文件结构
            using (var zipFile = new ZipFile(zipFilePath))
            {
                // 获取根目录结构
                var rootEntries = zipFile
                    .Cast<ZipEntry>()
                    .Select(e => GetRootDirectoryName(e.Name))
                    .Where(d => d != null)
                    .Distinct()
                    .ToList();

                Log.Info($"[ModDownloadTask] ZIP 根目录结构: {string.Join(", ", rootEntries)}");

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
                string? trackedRootDir = null;  // 跟踪解压的根目录（用于取消时清理）

                if (!string.IsNullOrEmpty(singleRootDir))
                {
                    // 如果有单一的根目录（如 "ContentPatcher/"），直接解压到 Mods 文件夹
                    // 保留根目录结构，生成 Mods/ContentPatcher/...
                    rootDirName = singleRootDir.TrimEnd('/');
                    extractPath = targetModsPath;
                    trackedRootDir = Path.Combine(targetModsPath, rootDirName);

                    // 如果根目录已存在，先删除（用于覆盖更新）
                    if (Directory.Exists(trackedRootDir))
                    {
                        Log.Info($"[ModDownloadTask] 检测到已存在的根目录，将删除: {trackedRootDir}");
                        Directory.Delete(trackedRootDir, recursive: true);
                    }

                    // 记录将要创建的根目录
                    _extractedRootDirs.Add(trackedRootDir);
                    Log.Info($"[ModDownloadTask] 检测到单一根目录: {singleRootDir}，将解压到 Mods 文件夹（保留目录结构）");
                }
                else
                {
                    // 如果没有单一的根目录，创建以 MOD 名称命名的子目录
                    var modFolderName = Path.GetFileNameWithoutExtension(_fileName);
                    extractPath = Path.Combine(targetModsPath, modFolderName);
                    trackedRootDir = extractPath;

                    // 如果目标文件夹已存在，先删除（用于覆盖更新）
                    if (Directory.Exists(extractPath))
                    {
                        Log.Info($"[ModDownloadTask] 检测到已存在的目录，将删除: {extractPath}");
                        Directory.Delete(extractPath, recursive: true);
                    }

                    Directory.CreateDirectory(extractPath);

                    // 记录创建的根目录
                    _extractedRootDirs.Add(extractPath);
                    Log.Info($"[ModDownloadTask] 无单一根目录，创建子目录: {modFolderName}");
                }

                Progress = 70;

                // 解压文件
                int extractedCount = 0;
                int skippedCount = 0;
                int totalEntries = (int)zipFile.Count;

                Log.Info($"[ModDownloadTask] 开始解压，共 {totalEntries} 个条目");

                foreach (ZipEntry entry in zipFile)
                {
                    if (_linkedToken.IsCancellationRequested)
                    {
                        Log.Info("[ModDownloadTask] 解压被取消");
                        return;
                    }

                    // 跳过目录条目
                    if (entry.IsDirectory)
                    {
                        Log.Debug($"[ModDownloadTask] 跳过目录: {entry.Name}");
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
                        Log.Debug($"[ModDownloadTask] 文件已存在，将覆盖: {destinationPath}");
                        File.Delete(destinationPath);
                    }

                    // 使用 using 确保流被正确释放
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

                    // 每解压 10 个文件记录一次
                    if (extractedCount % 10 == 0)
                    {
                        Log.Info($"[ModDownloadTask] 已解压 {extractedCount}/{totalEntries} 个文件");
                    }

                    // 更新进度（70-90%）
                    if (extractedCount % 10 == 0)
                    {
                        Progress = Math.Min(90, 70 + (extractedCount * 20 / totalEntries));
                    }
                }

                Log.Info($"[ModDownloadTask] 已解压 {extractedCount} 个文件，跳过 {skippedCount} 个文件到: {extractPath}");
            }

            Progress = 95;

            // 验证解压结果
            if (!Directory.Exists(targetModsPath))
            {
                throw new Exception("解压失败：目标目录不存在");
            }

            var extractedFolders = Directory.GetDirectories(targetModsPath);
            Log.Info($"[ModDownloadTask] 解压完成，共 {extractedFolders.Length} 个 MOD 文件夹");

            var normalizedManifestDirs = NormalizeExtractedModDirectories(targetModsPath, extractedManifestDirs);
            await WriteSourceCredentialFilesAsync(normalizedManifestDirs);

            // 解压成功，清空跟踪的根目录列表（不需要清理）
            _extractedRootDirs.Clear();

            Progress = 100;
            Log.Info($"[ModDownloadTask] ✓ MOD 解压成功");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[ModDownloadTask] MOD 解压失败");
            throw;
        }
    }

    private static System.Collections.Generic.IEnumerable<string> NormalizeExtractedModDirectories(
        string targetModsPath,
        System.Collections.Generic.IEnumerable<string> manifestDirs)
    {
        var normalized = new System.Collections.Generic.HashSet<string>(manifestDirs, StringComparer.OrdinalIgnoreCase);

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

                Log.Info($"[ModDownloadTask] 发现嵌套容器目录，准备展开: {topDir} ({childManifestDirs.Count} 个子MOD)");

                foreach (var childDir in childManifestDirs)
                {
                    // 检查源目录是否还存在（可能在之前的循环中已被处理）
                    if (!Directory.Exists(childDir))
                    {
                        Log.Debug($"[ModDownloadTask] 源目录已不存在，跳过: {childDir}");
                        normalized.Remove(childDir);
                        continue;
                    }

                    var childName = Path.GetFileName(childDir);
                    var destination = Path.Combine(targetModsPath, childName);

                    if (string.Equals(destination.TrimEnd('\\'), childDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    {
                        // 源目录已经在正确位置，添加到 normalized 集合
                        normalized.Add(destination);
                        continue;
                    }

                    // 移除旧路径（容器内的路径），后续会添加新路径
                    normalized.Remove(childDir);

                    // 如果目标目录存在，先删除
                    if (Directory.Exists(destination))
                    {
                        Log.Warn($"[ModDownloadTask] 展开嵌套时目标已存在，先删除: {destination}");

                        try
                        {
                            Directory.Delete(destination, true);

                            // 等待删除操作完成
                            for (int retry = 0; retry < 10; retry++)
                            {
                                if (!Directory.Exists(destination))
                                    break;
                                System.Threading.Thread.Sleep(50);
                            }

                            // 如果目标仍然存在，使用临时名称
                            if (Directory.Exists(destination))
                            {
                                var tempName = $"{destination}_temp_{Guid.NewGuid():N}";
                                Log.Warn($"[ModDownloadTask] 目标目录仍然存在，使用临时名称: {tempName}");
                                destination = tempName;
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Warn($"[ModDownloadTask] 删除目标目录失败: {destination}, 错误: {ex.Message}");
                            // 继续尝试移动，Windows 允许移动到已存在的目录
                        }
                    }

                    try
                    {
                        Directory.Move(childDir, destination);
                        Log.Info($"[ModDownloadTask] 已移动目录: {childDir} -> {destination}");

                        if (File.Exists(Path.Combine(destination, "manifest.json")))
                            normalized.Add(destination);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[ModDownloadTask] 移动目录失败: {childDir} -> {destination}, 错误: {ex.Message}");

                        // 如果移动失败，尝试复制后删除
                        try
                        {
                            Log.Info($"[ModDownloadTask] 尝试复制目录代替移动: {childDir} -> {destination}");

                            // 复制目录
                            CopyDirectoryRecursive(childDir, destination);

                            // 删除源目录
                            Directory.Delete(childDir, true);

                            if (File.Exists(Path.Combine(destination, "manifest.json")))
                                normalized.Add(destination);
                        }
                        catch (Exception copyEx)
                        {
                            Log.Warn($"[ModDownloadTask] 复制目录也失败: {copyEx.Message}");
                        }
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
            Log.Warn("[ModDownloadTask] 处理嵌套目录结构时发生错误", ex);
        }

        return normalized;
    }

    /// <summary>
    /// 递归复制目录
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

    private Task WriteSourceCredentialFilesAsync(System.Collections.Generic.IEnumerable<string> targetModDirs)
    {
        try
        {
            if (_saveOnly)
                return Task.CompletedTask;

            var normalizedPlatform = NormalizePlatform(_sourcePlatform);
            if (string.IsNullOrWhiteSpace(normalizedPlatform))
                return Task.CompletedTask;

            var normalizedProjectId = NormalizeId(_sourceProjectId);
            var normalizedFileId = NormalizeId(_sourceFileId);

            var payload = new
            {
                platform = normalizedPlatform,
                projectId = string.IsNullOrWhiteSpace(normalizedProjectId) ? (_sourceProjectId ?? string.Empty) : normalizedProjectId,
                fileId = string.IsNullOrWhiteSpace(normalizedFileId) ? (_sourceFileId ?? string.Empty) : normalizedFileId,
                modId = _modId,
                modName = _modName,
                fileName = _fileName,
                downloadUrl = _downloadUrl ?? string.Empty,
                installedAtUtc = DateTime.UtcNow.ToString("o"),
                schemaVersion = 1
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });

            foreach (var modDir in targetModDirs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!Directory.Exists(modDir))
                    {
                        Log.Warn($"[ModDownloadTask] 跳过写入来源凭证（目录不存在）: {modDir}");
                        continue;
                    }

                    var credentialPath = Path.Combine(modDir, "svl-source.json");
                    File.WriteAllText(credentialPath, json);
                    Log.Info($"[ModDownloadTask] 已写入来源凭证: {credentialPath}");
                }
                catch (Exception ex)
                {
                    Log.Warn($"[ModDownloadTask] 写入来源凭证失败: {modDir}", ex);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("[ModDownloadTask] 写入来源凭证时发生错误", ex);
        }

        return Task.CompletedTask;
    }

    private static string NormalizeId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var digits = new string(value.Where(char.IsDigit).ToArray());
        return digits;
    }

    private static string NormalizePlatform(string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
            return string.Empty;

        if (string.Equals(platform, "Curseforge", StringComparison.OrdinalIgnoreCase))
            return "Curseforge";

        if (string.Equals(platform, "NexusMods", StringComparison.OrdinalIgnoreCase)
            || string.Equals(platform, "Nexus", StringComparison.OrdinalIgnoreCase))
            return "NexusMods";

        return string.Empty;
    }

    /// <summary>
    /// 从路径中获取根目录名
    /// </summary>
    private string? GetRootDirectoryName(string path)
    {
        var parts = path.Split('/');
        if (parts.Length > 1)
        {
            return parts[0];
        }
        return null;
    }

    public override void Cancel()
    {
        try
        {
            _cts.Cancel();
            Status = DownloadTaskStatus.Cancelled;
            StatusMessage = "正在取消...";

            // 删除可能不完整的缓存文件
            try
            {
                var fileIdText = NormalizeId(_sourceFileId);
                var cachePrefix = _isModpack ? "modpack" : "mod";
                var cacheKey = DownloadCacheService.GenerateCacheKey(cachePrefix, $"{_modId}_{fileIdText}");
                DownloadCacheService.ClearCache(cacheKey);
                Log.Info($"[ModDownloadTask] 已清除缓存: {cacheKey}");
            }
            catch (Exception ex)
            {
                Log.Warn($"[ModDownloadTask] 清除缓存失败: {ex.Message}");
            }

            // 删除可能创建的空目标目录
            CleanupCreatedTargetDirectory();

            Log.Info($"[ModDownloadTask] {_modName} 下载已取消");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[ModDownloadTask] 取消任务失败");
        }
    }

    /// <summary>
    /// 清理创建的目标目录（取消时删除所有内容，不论是否为空）
    /// </summary>
    private void CleanupCreatedTargetDirectory()
    {
        // 首先清理解压过程中创建的根目录
        foreach (var rootDir in _extractedRootDirs)
        {
            try
            {
                if (Directory.Exists(rootDir))
                {
                    Directory.Delete(rootDir, recursive: true);
                    Log.Info($"[ModDownloadTask] 已删除解压根目录: {rootDir}");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[ModDownloadTask] 清理解压根目录失败: {rootDir}, {ex.Message}");
            }
        }
        _extractedRootDirs.Clear();

        // 然后清理创建的目标目录
        if (string.IsNullOrWhiteSpace(_createdTargetPath))
            return;

        try
        {
            if (Directory.Exists(_createdTargetPath))
            {
                // 取消任务时，删除整个目录及其内容（无论是否为空）
                Directory.Delete(_createdTargetPath, true);
                Log.Info($"[ModDownloadTask] 已删除目标目录: {_createdTargetPath}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[ModDownloadTask] 清理目标目录失败: {ex.Message}");
        }
        finally
        {
            _createdTargetPath = null;  // 清除引用，避免重复处理
        }
    }
}
