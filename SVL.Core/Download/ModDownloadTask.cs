using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ICSharpCode.SharpZipLib.Zip;
using SVL.Core.Config;
using SVL.Core.IO;
using SVL.Core.Logging;
using SVL.Core.Stardew.Instance;
using SVL.Core.Stardew.Localization;
using SVL.Core.Stardew.Mod;

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
    private readonly string? _updateTargetModPath;  // 更新时要就地替换的原始 Mod 文件夹
    private readonly string? _localZipPath;  // 本地 zip 文件路径（已下载好的文件）
    private readonly bool _saveOnly;  // 只保存 ZIP 文件，不安装
    private readonly string? _sourcePlatform; // 来源平台（如 Curseforge/NexusMods）
    private readonly string? _sourceProjectId; // 来源项目 ID
    private readonly string? _sourceFileId; // 来源文件 ID
    private readonly bool _isModpack;  // 是否为整合包
    private readonly string? _modpackIconUrl; // 整合包图标 URL（用于另存为时内嵌）
    private readonly string? _modpackIconLocalPath; // 整合包本地图标路径（用于另存为时内嵌）
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
    /// 临时下载的图标文件路径（用于 finally 清理）
    /// </summary>
    private string? _tempDownloadedIconPath;

    /// <summary>
    /// 构造函数：从网络下载 MOD 到指定路径
    /// </summary>
    /// <param name="modId">MOD ID</param>
    /// <param name="modName">MOD 名称</param>
    /// <param name="fileName">文件名</param>
    /// <param name="targetModsPath">目标 Mods 文件夹路径（如果为 null，则打开另存为对话框）</param>
    /// <param name="saveOnly">是否只保存 ZIP 文件而不安装（默认 false）</param>
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
        string? modpackIconUrl = null,
        string? modpackIconLocalPath = null,
        CancellationToken parentCancellationToken = default,
        string? updateTargetModPath = null)
    {
        _modId = modId;
        _modName = modName;
        _fileName = fileName;
        _downloadUrl = downloadUrl;
        _gameBasePath = gameBasePath;
        _targetModsPath = targetModsPath;
        _updateTargetModPath = updateTargetModPath;
        _localZipPath = null;
        _saveOnly = saveOnly;
        _sourcePlatform = sourcePlatform;
        _sourceProjectId = sourceProjectId;
        _sourceFileId = sourceFileId;
        _isModpack = isModpack;
        _modpackIconUrl = modpackIconUrl;
        _modpackIconLocalPath = modpackIconLocalPath;
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
        string? modpackIconUrl = null,
        string? modpackIconLocalPath = null,
        CancellationToken parentCancellationToken = default,
        string? updateTargetModPath = null)
    {
        _modId = modId;
        _modName = modName;
        _fileName = fileName;
        _localZipPath = localZipPath;
        _gameBasePath = gameBasePath;
        _targetModsPath = targetModsPath;
        _updateTargetModPath = updateTargetModPath;
        _downloadUrl = null;
        _saveOnly = saveOnly;
        _sourcePlatform = sourcePlatform;
        _sourceProjectId = sourceProjectId;
        _sourceFileId = sourceFileId;
        _isModpack = isModpack;
        _modpackIconUrl = modpackIconUrl;
        _modpackIconLocalPath = modpackIconLocalPath;
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

                // 整合包另存为：将图标写入压缩包内部（zip/cfmodpack）
                if (_isModpack)
                {
                    await TryEmbedModpackIconAsync(targetPath);
                    _linkedToken.ThrowIfCancellationRequested();
                }

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
                    if (!string.IsNullOrWhiteSpace(_updateTargetModPath))
                    {
                        await ExtractModToExistingModFolderAsync(zipFilePath, targetPath, _updateTargetModPath);
                    }
                    else
                    {
                        await ExtractModToModsFolderAsync(zipFilePath, targetPath);
                    }
                }

                Progress = 100;
                Status = DownloadTaskStatus.Completed;
                StatusMessage = $"✓ {_modName} 安装完成！";
                CompletedTime = DateTime.Now;

                Log.Info($"[ModDownloadTask] ✓ MOD 下载安装成功: {_modName}");
            }
        }
        catch (OperationCanceledException)
        {
            Status = DownloadTaskStatus.Cancelled;
            StatusMessage = "已取消";
            CompletedTime = DateTime.Now;
            CleanupCreatedTargetDirectory();
            throw;
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
        finally
        {
            CleanupTempIconFile();
        }
    }

    private async Task TryEmbedModpackIconAsync(string targetPath)
    {
        try
        {
            if (!_saveOnly || !_isModpack || string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath))
                return;

            var ext = Path.GetExtension(targetPath).ToLowerInvariant();
            if (ext != ".zip" && ext != ".cfmodpack")
            {
                Log.Info($"[ModDownloadTask] 非 ZIP 类整合包，跳过图标内嵌: {targetPath}");
                return;
            }

            var (iconPath, iconExt) = await ResolveModpackIconAsync();
            if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath))
                return;

            var tempPath = targetPath + ".svl.icon.tmp";
            ReplaceZipWithEmbeddedIcon(targetPath, tempPath, iconPath, iconExt);

            File.Copy(tempPath, targetPath, true);
            File.Delete(tempPath);

            Log.Info($"[ModDownloadTask] 已将整合包图标写入压缩包内部: {Path.GetFileName(targetPath)}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"[ModDownloadTask] 内嵌整合包图标失败: {ex.Message}");
        }
    }

    private async Task<(string? iconPath, string iconExt)> ResolveModpackIconAsync()
    {
        if (!string.IsNullOrWhiteSpace(_modpackIconLocalPath) && File.Exists(_modpackIconLocalPath))
        {
            var extLocal = NormalizeIconExtension(Path.GetExtension(_modpackIconLocalPath));
            return (_modpackIconLocalPath, extLocal);
        }

        if (string.IsNullOrWhiteSpace(_modpackIconUrl))
            return (null, ".png");

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _linkedToken.ThrowIfCancellationRequested();
        var bytes = await httpClient.GetByteArrayAsync(_modpackIconUrl);
        _linkedToken.ThrowIfCancellationRequested();

        if (bytes == null || bytes.Length == 0)
            return (null, ".png");

        var extFromUrl = ".png";
        try
        {
            extFromUrl = NormalizeIconExtension(Path.GetExtension(new Uri(_modpackIconUrl).AbsolutePath));
        }
        catch
        {
            extFromUrl = ".png";
        }

        var tempPath = Path.Combine(Path.GetTempPath(), "SVL", "modpack_icon", $"{Guid.NewGuid():N}{extFromUrl}");
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        File.WriteAllBytes(tempPath, bytes);
        _tempDownloadedIconPath = tempPath;

        return (tempPath, extFromUrl);
    }

    private static string NormalizeIconExtension(string? ext)
    {
        if (string.IsNullOrWhiteSpace(ext))
            return ".png";

        var normalized = ext.StartsWith(".", StringComparison.Ordinal) ? ext.ToLowerInvariant() : ".png";
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"
        };

        return allowed.Contains(normalized) ? normalized : ".png";
    }

    private static void ReplaceZipWithEmbeddedIcon(string sourceZipPath, string targetZipPath, string iconPath, string iconExt)
    {
        var iconEntryName = $"modpack-icon{iconExt}";
        var iconCandidateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "modpack-icon.png", "modpack-icon.jpg", "modpack-icon.jpeg", "modpack-icon.webp", "modpack-icon.bmp", "modpack-icon.gif",
            "pack-icon.png", "pack-icon.jpg", "pack-icon.jpeg", "pack-icon.webp", "pack-icon.bmp", "pack-icon.gif",
            "icon.png", "icon.jpg", "icon.jpeg", "icon.webp", "icon.bmp", "icon.gif",
            "logo.png", "logo.jpg", "logo.jpeg", "logo.webp", "logo.bmp", "logo.gif",
            "thumbnail.png", "thumbnail.jpg", "thumbnail.jpeg", "thumbnail.webp", "thumbnail.bmp", "thumbnail.gif",
            "cover.png", "cover.jpg", "cover.jpeg", "cover.webp", "cover.bmp", "cover.gif"
        };

        using var inputZip = new ZipFile(sourceZipPath);
        using var outputStream = File.Create(targetZipPath);
        using var outputZip = new ZipOutputStream(outputStream);
        outputZip.SetLevel(9);

        foreach (ZipEntry entry in inputZip)
        {
            if (entry.IsDirectory)
                continue;

            var fileName = Path.GetFileName(entry.Name);
            if (iconCandidateNames.Contains(fileName))
                continue;

            var newEntry = new ZipEntry(entry.Name)
            {
                DateTime = entry.DateTime
            };
            outputZip.PutNextEntry(newEntry);

            using (var entryStream = inputZip.GetInputStream(entry))
            {
                entryStream.CopyTo(outputZip);
            }

            outputZip.CloseEntry();
        }

        var iconEntry = new ZipEntry(iconEntryName)
        {
            DateTime = DateTime.Now,
            Size = new FileInfo(iconPath).Length
        };
        outputZip.PutNextEntry(iconEntry);
        using (var iconStream = File.OpenRead(iconPath))
        {
            iconStream.CopyTo(outputZip);
        }
        outputZip.CloseEntry();

        outputZip.Finish();
    }

    private void CleanupTempIconFile()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_tempDownloadedIconPath) && File.Exists(_tempDownloadedIconPath))
                File.Delete(_tempDownloadedIconPath);
        }
        catch (Exception ex)
        {
            Log.Warn($"[ModDownloadTask] 清理临时图标失败: {ex.Message}");
        }
        finally
        {
            _tempDownloadedIconPath = null;
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
        var candidateUrls = BuildCurseforgeDownloadCandidates(downloadUrl);
        Exception? lastException = null;
        string? lastFailureDetail = null;
        var typeLabel = _isModpack ? "整合包" : "MOD";

        var settings = AppConfig.GetSettings();
        var threadCount = Math.Max(1, Math.Min(16, settings.DownloadSegmentThreads <= 0 ? 4 : settings.DownloadSegmentThreads));

        for (var i = 0; i < candidateUrls.Count; i++)
        {
            var candidateUrl = candidateUrls[i];
            try
            {
                StatusMessage = $"正在下载{typeLabel}...\n尝试地址 {i + 1}/{candidateUrls.Count}（{threadCount} 线程）";

                if (i > 0)
                {
                    Log.Warn($"[ModDownloadTask] 尝试备用下载地址 ({i + 1}/{candidateUrls.Count}): {candidateUrl}");
                }

                await HttpMultiThreadDownloader.DownloadAsync(
                    candidateUrl,
                    targetPath,
                    threadCount,
                    (percent, bytesRead, totalBytes, speed) =>
                    {
                        var normalizedTotal = totalBytes > 0 ? totalBytes : Math.Max(bytesRead, 1);
                        Progress = 10 + (int)(Math.Max(0, Math.Min(100, percent)) * 0.4);

                        var downloadedMB = bytesRead / (1024.0 * 1024.0);
                        var totalMB = normalizedTotal / (1024.0 * 1024.0);
                        var speedMB = speed / (1024.0 * 1024.0);

                        StatusMessage = $"正在下载{typeLabel}...\n{percent:F2}%\t{downloadedMB:F1} MB / {totalMB:F1} MB ({speedMB:F1} MB/s)";
                    },
                    _linkedToken);

                var finalInfo = new FileInfo(targetPath);
                var finalMB = finalInfo.Length / (1024.0 * 1024.0);
                StatusMessage = $"正在下载{typeLabel}...\n100.00%\t{finalMB:F1} MB / {finalMB:F1} MB (完成)";
                return;
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (ex is OperationCanceledException)
                {
                    lastFailureDetail = _linkedToken.IsCancellationRequested
                        ? $"用户取消 ({candidateUrl})"
                        : $"请求超时 ({candidateUrl})";
                }
                else if (!string.IsNullOrWhiteSpace(ex.Message))
                {
                    lastFailureDetail = ex.Message;
                }

                Log.Warn($"[ModDownloadTask] 地址尝试失败 ({i + 1}/{candidateUrls.Count}): {candidateUrl}, {ex.GetType().Name}: {ex.Message}");
                try
                {
                    if (File.Exists(targetPath))
                    {
                        File.Delete(targetPath);
                    }
                }
                catch
                {
                    // 忽略清理失败。
                }
            }
        }

        var reasonText = string.IsNullOrWhiteSpace(lastFailureDetail)
            ? "未知原因"
            : lastFailureDetail;
        StatusMessage = $"下载失败：已尝试 {candidateUrls.Count} 个地址，均不可用（{reasonText}）";

        if (lastException != null)
            throw new HttpRequestException($"所有下载地址均失败：{reasonText}", lastException);

        throw new HttpRequestException("下载失败：未获取到可用下载地址");
    }

    private List<string> BuildCurseforgeDownloadCandidates(string primaryUrl)
    {
        var result = new List<string>();

        void Add(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;

            if (!result.Contains(url, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(url);
            }
        }

        Add(primaryUrl);

        if (!IsCurseforgeSource())
            return result;

        Add(SwapCdnHost(primaryUrl, "mediafilez.forgecdn.net"));
        Add(SwapCdnHost(primaryUrl, "media.forgecdn.net"));
        Add(SwapCdnHost(primaryUrl, "edge.forgecdn.net"));

        return result;
    }

    private static string? SwapCdnHost(string? url, string targetHost)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(targetHost))
            return null;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        if (!uri.Host.Contains("forgecdn.net", StringComparison.OrdinalIgnoreCase))
            return null;

        var builder = new UriBuilder(uri)
        {
            Host = targetHost,
            Port = -1
        };

        return builder.Uri.ToString();
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

        // 2. API 失败后，先尝试从文件列表拿到精确文件名（很多文件名与展示名不同）
        string cdnFileName = _fileName;
        try
        {
            var files = await CurseforgeApiService.GetModFilesAsync(projectId, index: 0, pageSize: 1000);
            var exact = files?.FirstOrDefault(f => f.Id == fileId);
            if (exact != null && !string.IsNullOrWhiteSpace(exact.FileName))
            {
                cdnFileName = exact.FileName;
                Log.Info($"[ModDownloadTask] 使用 Curseforge 文件列表中的精确文件名: {cdnFileName}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[ModDownloadTask] 获取 Curseforge 文件列表失败，继续使用任务文件名回退: {ex.Message}");
        }

        // 3. 构建 CDN 硬解析 URL 作为 fallback
        Log.Warn($"[ModDownloadTask] API 返回空，尝试使用 CDN 硬解析: fileId={fileId}, fileName={cdnFileName}");
        try
        {
            return CurseforgeApiService.BuildCdnUrl(fileId, cdnFileName);
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
                        ModBackupService.BackupDirectory(targetModsPath, trackedRootDir);
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
                        ModBackupService.BackupDirectory(targetModsPath, extractPath);
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

            var normalizedManifestDirs = extractedManifestDirs.Distinct(StringComparer.OrdinalIgnoreCase);
            await WriteSourceCredentialFilesAsync(targetModsPath, normalizedManifestDirs);

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

    /// <summary>
    /// 更新已安装 MOD：保留原文件夹名，备份旧内容后将新包内容回填到原目录。
    /// </summary>
    private async Task ExtractModToExistingModFolderAsync(string zipFilePath, string targetModsPath, string targetModPath)
    {
        string? preservedConfigTempPath = null;

        try
        {
            var normalizedModsPath = Path.GetFullPath(targetModsPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var normalizedTargetModPath = Path.GetFullPath(targetModPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            Log.Info($"[ModDownloadTask] 就地更新 MOD: {zipFilePath} -> {normalizedTargetModPath}");

            var extractedManifestDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (Directory.Exists(normalizedTargetModPath))
            {
                ModBackupService.BackupDirectory(normalizedModsPath, normalizedTargetModPath);
                preservedConfigTempPath = BackupConfigEntriesToTemp(normalizedTargetModPath);
                Directory.Delete(normalizedTargetModPath, recursive: true);
                Log.Info($"[ModDownloadTask] 已清理旧 MOD 目录: {normalizedTargetModPath}");
            }

            Directory.CreateDirectory(normalizedTargetModPath);
            _extractedRootDirs.Add(normalizedTargetModPath);

            using (var zipFile = new ZipFile(zipFilePath))
            {
                var rootEntries = zipFile
                    .Cast<ZipEntry>()
                    .Select(e => GetRootDirectoryName(e.Name))
                    .Where(d => d != null)
                    .Distinct()
                    .ToList();

                string? singleRootDir = rootEntries.Count == 1 ? rootEntries[0] : null;
                if (!string.IsNullOrEmpty(singleRootDir) && !singleRootDir.EndsWith("/"))
                {
                    singleRootDir += "/";
                }

                Progress = 70;

                var extractedCount = 0;
                var totalEntries = (int)zipFile.Count;

                foreach (ZipEntry entry in zipFile)
                {
                    if (_linkedToken.IsCancellationRequested)
                    {
                        Log.Info("[ModDownloadTask] 更新解压被取消");
                        return;
                    }

                    if (entry.IsDirectory)
                        continue;

                    var relativeEntryPath = GetRelativeEntryPathForUpdate(entry.Name, singleRootDir);
                    if (string.IsNullOrWhiteSpace(relativeEntryPath))
                        continue;

                    var destinationPath = GetValidatedDestinationPath(normalizedTargetModPath, relativeEntryPath);
                    var destinationDir = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(destinationDir) && !Directory.Exists(destinationDir))
                    {
                        Directory.CreateDirectory(destinationDir);
                    }

                    if (File.Exists(destinationPath))
                    {
                        File.Delete(destinationPath);
                    }

                    using (var stream = zipFile.GetInputStream(entry))
                    using (var fileStream = File.Create(destinationPath))
                    {
                        stream.CopyTo(fileStream);
                    }

                    if (Path.GetFileName(destinationPath).Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                    {
                        var manifestDir = Path.GetDirectoryName(destinationPath);
                        if (!string.IsNullOrWhiteSpace(manifestDir) && Directory.Exists(manifestDir))
                        {
                            extractedManifestDirs.Add(manifestDir);
                        }
                    }

                    extractedCount++;
                    if (extractedCount % 10 == 0)
                    {
                        Progress = Math.Min(90, 70 + (extractedCount * 20 / Math.Max(1, totalEntries)));
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(preservedConfigTempPath) && Directory.Exists(preservedConfigTempPath))
            {
                CopyDirectoryRecursive(preservedConfigTempPath, normalizedTargetModPath);
                Log.Info($"[ModDownloadTask] 已恢复保留的配置文件: {normalizedTargetModPath}");
            }

            Progress = 95;

            if (!Directory.Exists(normalizedTargetModPath))
            {
                throw new Exception("更新失败：目标目录不存在");
            }

            await WriteSourceCredentialFilesAsync(normalizedModsPath, extractedManifestDirs.Distinct(StringComparer.OrdinalIgnoreCase));

            _extractedRootDirs.Clear();
            Progress = 100;
            Log.Info($"[ModDownloadTask] ✓ MOD 就地更新成功: {normalizedTargetModPath}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[ModDownloadTask] MOD 就地更新失败");
            throw;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(preservedConfigTempPath) && Directory.Exists(preservedConfigTempPath))
            {
                try
                {
                    Directory.Delete(preservedConfigTempPath, recursive: true);
                }
                catch (Exception cleanupEx)
                {
                    Log.Warn($"[ModDownloadTask] 清理临时配置目录失败: {cleanupEx.Message}");
                }
            }
        }
    }

    // 不再对嵌套目录做展开；保留压缩包中的目录层级，让 SMAPI 自行加载。

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

    private static string? BackupConfigEntriesToTemp(string sourceModPath)
    {
        if (!Directory.Exists(sourceModPath))
            return null;

        var configDirs = Directory.GetDirectories(sourceModPath, "*", SearchOption.AllDirectories)
            .Where(dir => IsConfigDirectoryName(Path.GetFileName(dir)))
            .OrderBy(dir => dir.Length)
            .ToList();

        var configFiles = Directory.GetFiles(sourceModPath, "*", SearchOption.AllDirectories)
            .Where(file => IsSettingsFile(Path.GetFileName(file)))
            .Where(file => !configDirs.Any(dir => IsPathUnderDirectory(file, dir)))
            .ToList();

        if (configDirs.Count == 0 && configFiles.Count == 0)
            return null;

        var tempRoot = Path.Combine(Path.GetTempPath(), "SVL", "mod-update-config", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        foreach (var configDir in configDirs)
        {
            var relativePath = GetRelativePathPortable(sourceModPath, configDir);
            var targetDir = Path.Combine(tempRoot, relativePath);
            CopyDirectoryRecursive(configDir, targetDir);
        }

        foreach (var configFile in configFiles)
        {
            var relativePath = GetRelativePathPortable(sourceModPath, configFile);
            var targetFile = Path.Combine(tempRoot, relativePath);
            var targetDir = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrWhiteSpace(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            File.Copy(configFile, targetFile, overwrite: true);
        }

        Log.Info($"[ModDownloadTask] 已暂存配置文件: {sourceModPath} -> {tempRoot}");
        return tempRoot;
    }

    private static bool IsSettingsFile(string fileName)
    {
        return fileName.Equals("config.json", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("config.yaml", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("config.yml", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("settings.json", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".config.json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConfigDirectoryName(string? directoryName)
    {
        return string.Equals(directoryName, "config", StringComparison.OrdinalIgnoreCase)
            || string.Equals(directoryName, "configs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPathUnderDirectory(string path, string directory)
    {
        var normalizedDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return normalizedPath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRelativeEntryPathForUpdate(string entryName, string? singleRootDir)
    {
        var normalizedEntryName = entryName.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalizedEntryName))
            return string.Empty;

        if (!string.IsNullOrWhiteSpace(singleRootDir)
            && normalizedEntryName.StartsWith(singleRootDir, StringComparison.OrdinalIgnoreCase))
        {
            normalizedEntryName = normalizedEntryName.Substring(singleRootDir.Length);
        }

        return normalizedEntryName.TrimStart('/');
    }

    private static string GetValidatedDestinationPath(string baseDirectory, string relativePath)
    {
        var normalizedBaseDirectory = Path.GetFullPath(baseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var combinedPath = Path.GetFullPath(Path.Combine(normalizedBaseDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!combinedPath.StartsWith(normalizedBaseDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !PathsEqual(combinedPath, normalizedBaseDirectory))
        {
            throw new InvalidOperationException($"压缩包条目路径非法: {relativePath}");
        }

        return combinedPath;
    }

    private async Task WriteSourceCredentialFilesAsync(string targetModsPath, System.Collections.Generic.IEnumerable<string> targetModDirs)
    {
        try
        {
            if (_saveOnly)
                return;

            var normalizedPlatform = NormalizePlatform(_sourcePlatform);
            if (string.IsNullOrWhiteSpace(normalizedPlatform))
                return;

            var normalizedProjectId = NormalizeId(_sourceProjectId);
            var normalizedFileId = NormalizeId(_sourceFileId);

            var normalizedDirs = targetModDirs
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var metadata = new SvlSourceMetadata
            {
                Platform = normalizedPlatform,
                ProjectId = string.IsNullOrWhiteSpace(normalizedProjectId) ? (_sourceProjectId ?? string.Empty) : normalizedProjectId,
                FileId = string.IsNullOrWhiteSpace(normalizedFileId) ? (_sourceFileId ?? string.Empty) : normalizedFileId,
                ModId = _modId,
                ModName = _modName,
                FileName = _fileName,
                DownloadUrl = _downloadUrl ?? string.Empty,
                InstalledAtUtc = DateTime.UtcNow.ToString("o"),
                SchemaVersion = 3
            };

            metadata.Localization = await BuildLocalizationAsync(metadata).ConfigureAwait(false);

            var groupedByRoot = normalizedDirs
                .GroupBy(modDir => GetTopLevelRootDir(targetModsPath, modDir), StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var group in groupedByRoot)
            {
                var rootDir = group.Key;
                var manifestDirs = group.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var hasCompositeChildren = manifestDirs.Count > 1;
                var rootHasManifest = manifestDirs.Any(dir => PathsEqual(dir, rootDir));
                var parentDir = rootDir;
                var parentId = GetRelativePathPortable(targetModsPath, parentDir).Replace(Path.DirectorySeparatorChar, '/');
                var parentName = NormalizeFolderName(Path.GetFileName(parentDir));

                var childEntries = manifestDirs
                    .Where(dir => !rootHasManifest || !PathsEqual(dir, rootDir))
                    .Select(dir => new
                    {
                        id = GetRelativePathPortable(targetModsPath, dir).Replace(Path.DirectorySeparatorChar, '/'),
                        name = TryReadManifestName(dir) ?? NormalizeFolderName(Path.GetFileName(dir)),
                        relativePath = GetRelativePathPortable(targetModsPath, dir).Replace(Path.DirectorySeparatorChar, '/'),
                        uniqueId = TryReadManifestUniqueId(dir) ?? string.Empty
                    })
                    .ToList();

                if (hasCompositeChildren)
                {
                    var parentPayload = new SvlSourceMetadata
                    {
                        Platform = metadata.Platform,
                        ProjectId = metadata.ProjectId,
                        FileId = metadata.FileId,
                        ModId = metadata.ModId,
                        ModName = metadata.ModName,
                        FileName = metadata.FileName,
                        DownloadUrl = metadata.DownloadUrl,
                        InstalledAtUtc = metadata.InstalledAtUtc,
                        SchemaVersion = metadata.SchemaVersion,
                        Localization = metadata.Localization,
                        IsParentMod = true,
                        ChildMods = childEntries.Select(child => new SvlChildModReference
                        {
                            Id = child.id,
                            Name = child.name,
                            RelativePath = child.relativePath,
                            UniqueId = child.uniqueId
                        }).ToList()
                    };

                    WriteSourceCredentialFile(parentDir, parentPayload);

                    foreach (var childDir in manifestDirs)
                    {
                        var childPayload = new SvlSourceMetadata
                        {
                            Platform = metadata.Platform,
                            ProjectId = metadata.ProjectId,
                            FileId = metadata.FileId,
                            ModId = metadata.ModId,
                            ModName = metadata.ModName,
                            FileName = metadata.FileName,
                            DownloadUrl = metadata.DownloadUrl,
                            InstalledAtUtc = metadata.InstalledAtUtc,
                            SchemaVersion = metadata.SchemaVersion,
                            Localization = metadata.Localization,
                            ParentMod = new SvlParentModReference
                            {
                                Id = parentId,
                                Name = parentName,
                                RelativePath = GetRelativePathPortable(targetModsPath, parentDir).Replace(Path.DirectorySeparatorChar, '/')
                            }
                        };

                        WriteSourceCredentialFile(childDir, childPayload);
                    }

                    continue;
                }

                foreach (var modDir in manifestDirs)
                {
                    WriteSourceCredentialFile(modDir, metadata);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("[ModDownloadTask] 写入来源凭证时发生错误", ex);
        }

        return;
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

    private static void WriteSourceCredentialFile(string modDir, SvlSourceMetadata payload)
    {
        try
        {
            if (!Directory.Exists(modDir))
            {
                Log.Warn($"[ModDownloadTask] 跳过写入来源凭证（目录不存在）: {modDir}");
                return;
            }

            var credentialPath = SvlSourceMetadataStore.GetFilePath(modDir);
            File.WriteAllText(credentialPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            Log.Info($"[ModDownloadTask] 已写入来源凭证: {credentialPath}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[ModDownloadTask] 写入来源凭证失败: {modDir}", ex);
        }
    }

    private static async Task<SvlSourceLocalization?> BuildLocalizationAsync(SvlSourceMetadata metadata)
    {
        if (metadata == null)
            return null;

        if (string.IsNullOrWhiteSpace(metadata.ProjectId) || string.IsNullOrWhiteSpace(metadata.Platform))
            return null;

        var localization = await CommunityLocalizationService.GetAsync("mod", metadata.Platform, metadata.ProjectId).ConfigureAwait(false);
        if (localization == null)
            return null;

        return new SvlSourceLocalization
        {
            EntityType = localization.EntityType ?? "mod",
            Platform = localization.Platform ?? metadata.Platform,
            Id = localization.Id ?? metadata.ProjectId,
            NameZhCn = localization.Name?.ZhCn ?? string.Empty,
            NameSource = localization.Name?.Source ?? metadata.ModName,
            DescriptionZhCn = localization.Description?.ZhCn ?? string.Empty,
            DescriptionSource = localization.Description?.Source ?? string.Empty,
            SourceUrl = localization.Meta?.SourceUrl ?? string.Empty,
            UpdatedAt = localization.Meta?.UpdatedAt ?? string.Empty,
            Contributor = localization.Meta?.Contributor ?? string.Empty
        };
    }

    private static string GetTopLevelRootDir(string targetModsPath, string modDir)
    {
        var relative = GetRelativePathPortable(targetModsPath, modDir);
        var firstSegment = relative
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(firstSegment)
            ? modDir
            : Path.Combine(targetModsPath, firstSegment);
    }

    private static string GetRelativePathPortable(string basePath, string fullPath)
    {
        if (string.IsNullOrWhiteSpace(basePath) || string.IsNullOrWhiteSpace(fullPath))
            return fullPath;

        var normalizedBase = basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedFull = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!normalizedFull.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase))
            return fullPath;

        var relative = normalizedFull.Substring(normalizedBase.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.IsNullOrWhiteSpace(relative) ? Path.GetFileName(normalizedFull) : relative;
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        return string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeFolderName(string? value)
    {
        return ModFolderNaming.NormalizeFolderName(value);
    }

    private static string? TryReadManifestName(string modDir)
    {
        try
        {
            var manifestPath = Path.Combine(modDir, "manifest.json");
            if (!File.Exists(manifestPath))
                return null;

            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return document.RootElement.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadManifestUniqueId(string modDir)
    {
        try
        {
            var manifestPath = Path.Combine(modDir, "manifest.json");
            if (!File.Exists(manifestPath))
                return null;

            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return document.RootElement.TryGetProperty("UniqueId", out var uniqueIdProp) ? uniqueIdProp.GetString() : null;
        }
        catch
        {
            return null;
        }
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
