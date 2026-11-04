using System.Diagnostics;
using System.IO.Compression;
using System.Collections.Concurrent;
using SVL.Avalonia.Models;
using SVL.Core.Platform.Abstractions;

namespace SVL.Avalonia.Services;

/// <summary>
/// SMAPI 下载服务。负责从 GitHub/NexusMods 下载 SMAPI zip 包，支持：
/// - 本地缓存（按版本号文件名命中）
/// - 进度回调
/// - NXM 回调等待（NexusMods 非 Premium 用户浏览器下载后通过 NXM 协议回传 zip 路径）
/// 对齐旧 SVL.Core.Download.SmapiDownloadTask 的下载与 NXM 等待能力，但运行在 Avalonia 层。
/// </summary>
public sealed class SmapiDownloadService
{
    /// <summary>SMAPI 在 NexusMods 的 mod id（用于 NXM 回调匹配）。</summary>
    public const long SmapiModId = 2400;

    private const string GameId = "stardewvalley";

    private readonly HttpDownloadService _httpDownloadService;
    private readonly INxmLinkParser _nxmLinkParser;
    private readonly NexusModDownloadResolverService _nexusModDownloadResolverService;

    // 直接 SMAPI 安装入口按版本共用临时归档路径；并发点击安装或多个任务同时恢复时，
    // 必须串行化同一版本的缓存检查/归一化/写入，避免一个任务删除或覆盖另一个任务正在
    // 校验的 zip。不同版本仍可并行处理。
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DownloadLocks = new(StringComparer.OrdinalIgnoreCase);

    // NXM 回调等待状态按 FileID 隔离，避免并发 SMAPI 安装或迟到回调覆盖
    // 另一项等待。回调本身只携带 ModID/FileID，FileID 是可靠的关联键。
    private readonly ConcurrentDictionary<long, SmapiNxmWaiter> _nxmWaiters = new();

    public SmapiDownloadService(
        HttpDownloadService httpDownloadService,
        INxmLinkParser nxmLinkParser,
        NexusModDownloadResolverService? nexusModDownloadResolverService = null)
    {
        _httpDownloadService = httpDownloadService;
        _nxmLinkParser = nxmLinkParser;
        _nexusModDownloadResolverService = nexusModDownloadResolverService ?? new NexusModDownloadResolverService();
    }

    /// <summary>
    /// 下载 SMAPI zip 到临时目录。命中缓存则直接返回路径。
    /// </summary>
    /// <param name="version">SMAPI 版本信息（来自 remoteCatalog）</param>
    /// <param name="taskItem">可选任务项，用于进度回调</param>
    /// <param name="progressText">可选进度文本回调（UI 显示）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>zip 文件路径；失败返回 null</returns>
    public async Task<string?> DownloadZipAsync(
        SmapiVersionEntry version,
        DownloadTaskItem? taskItem = null,
        Action<string>? progressText = null,
        CancellationToken cancellationToken = default)
    {
        var lockKey = NormalizeVersion(version.Version);
        var downloadLock = DownloadLocks.GetOrAdd(lockKey, static _ => new SemaphoreSlim(1, 1));
        await downloadLock.WaitAsync(cancellationToken);
        try
        {
            return await DownloadZipCoreAsync(version, taskItem, progressText, cancellationToken);
        }
        finally
        {
            downloadLock.Release();
        }
    }

    private async Task<string?> DownloadZipCoreAsync(
        SmapiVersionEntry version,
        DownloadTaskItem? taskItem,
        Action<string>? progressText,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tempDir = Path.Combine(Path.GetTempPath(), "SVL", "smapi");
        Directory.CreateDirectory(tempDir);

        var pureVersion = NormalizeVersion(version.Version);
        var zipPath = Path.Combine(tempDir, $"SMAPI-{pureVersion}.zip");

        // CurseForge 的版本条目带有稳定的 ProjectID/FileID。优先使用该缓存，
        // 避免同版本的临时文件、CDN 地址变化或网络波动导致重复下载。
        if (IsCurseforgeVersion(version) &&
            CurseforgeDownloadCache.TryGet(
                CurseforgeSmapiProjectId,
                version.FileId!.Value,
                out var curseforgeCachePath,
                IsValidSmapiArchive) &&
            TryCopyValidatedArchive(curseforgeCachePath, zipPath))
        {
            taskItem?.Let(t =>
            {
                t.SetState(DownloadTaskState.Downloading, "下载中 (CurseForge 缓存)");
                t.Progress = 50;
            });
            progressText?.Invoke("使用 CurseForge 缓存文件...");
            return zipPath;
        }

        // 缓存命中（由 SMAPI 安装包结构校验决定，不用文件大小猜测有效性；
        // 这样小型测试包或未来压缩包变小后仍可正常复用）
        if (File.Exists(zipPath))
        {
            // 历史缓存可能来自 GitHub 的 double-zipped 资产；先还原为真正
            // 的安装包，再交给调用方，避免把外层 zip 当成 SMAPI 安装器。
            ModpackInstallService.TryUnwrapDoubleZipped(zipPath);
            if (IsValidSmapiArchive(zipPath))
            {
                TrySaveSourceCache(version, zipPath);
                taskItem?.Let(t =>
                {
                    t.SetState(DownloadTaskState.Downloading, "下载中 (缓存)");
                    t.Progress = 50;
                });
                progressText?.Invoke("使用缓存文件...");
                return zipPath;
            }

            // 缓存文件损坏，删除后走重新下载流程
            global::SVL.Core.Platform.Diagnostics.RuntimeDebugLog.WriteLine("[SmapiDownloadService] 缓存文件损坏，删除后重新下载");
            TryDeleteFile(zipPath);
        }

        // 版本号缓存可能因为清理、改名或不同来源而不存在；Nexus 的稳定键是
        // ModID/FileID，必须在进入浏览器回退前优先命中它。
        if (IsNexusVersion(version) &&
            NexusDownloadCache.TryGet(SmapiModId, version.FileId!.Value, out var nexusCachePath, IsValidSmapiArchive) &&
            IsValidSmapiArchive(nexusCachePath))
        {
            try
            {
                File.Copy(nexusCachePath, zipPath, overwrite: true);
                ModpackInstallService.TryUnwrapDoubleZipped(zipPath);
                if (IsValidSmapiArchive(zipPath))
                {
                    taskItem?.Let(t =>
                    {
                        t.SetState(DownloadTaskState.Downloading, "下载中 (Nexus 缓存)");
                        t.Progress = 50;
                    });
                    progressText?.Invoke("使用 Nexus 缓存文件...");
                    return zipPath;
                }
            }
            catch (Exception ex)
            {
                global::SVL.Core.Platform.Diagnostics.RuntimeDebugLog.WriteLine($"[SmapiDownloadService] 复制 Nexus 缓存失败: {ex.Message}");
                TryDeleteFile(zipPath);
            }
        }

        // NexusMods 来源：若是非 Premium 浏览器下载，等待 NXM 回调
        if (IsNexusVersion(version))
        {
            var nxmZipPath = await TryDownloadViaNxmCallbackAsync(version, taskItem, progressText, cancellationToken);
            if (nxmZipPath != null)
            {
                // 浏览器回调下载的临时文件也要归档到按版本命名的缓存，
                // 否则下一次安装无法命中缓存，仍会重复打开浏览器。
                if (!string.Equals(nxmZipPath, zipPath, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        File.Copy(nxmZipPath, zipPath, overwrite: true);
                        if (IsValidSmapiArchive(zipPath))
                        {
                            TrySaveSourceCache(version, zipPath);
                            TryDeleteFile(nxmZipPath);
                            return zipPath;
                        }

                        TryDeleteFile(zipPath);
                    }
                    catch (Exception ex)
                    {
                        global::SVL.Core.Platform.Diagnostics.RuntimeDebugLog.WriteLine($"[SmapiDownloadService] 归档 NXM 缓存失败: {ex.Message}");
                    }
                }

                TrySaveSourceCache(version, nxmZipPath);
                return nxmZipPath;
            }
            // NXM 路径失败则回退到直接 HTTP 下载（若有 DownloadUrl）
        }

        // 直接 HTTP 下载（GitHub / CurseForge / NexusMods 直链）
        if (string.IsNullOrWhiteSpace(version.DownloadUrl))
        {
            return null;
        }

        taskItem?.Let(t =>
        {
            t.Status = "下载中";
            t.Progress = 5;
        });

        try
        {
            await _httpDownloadService.DownloadAsync(
                version.DownloadUrl,
                zipPath,
                snapshot =>
                {
                    var percent = (int)snapshot.Percent;
                    progressText?.Invoke($"正在下载... {percent}%");
                    taskItem?.Let(t =>
                    {
                        t.Progress = Math.Min(55, 5 + (percent / 2));
                        t.SetState(DownloadTaskState.Downloading, $"下载中 {percent}%");
                    });
                },
                cancellationToken,
                cacheValidator: IsValidSmapiArchive);

            ModpackInstallService.TryUnwrapDoubleZipped(zipPath);

            // 下载完成后校验 zip 完整性，损坏文件删除且不缓存（避免下次命中损坏缓存）
            if (!IsValidSmapiArchive(zipPath))
            {
                global::SVL.Core.Platform.Diagnostics.RuntimeDebugLog.WriteLine("[SmapiDownloadService] 下载的 zip 文件损坏，已删除");
                TryDeleteFile(zipPath);
                return null;
            }

            TrySaveSourceCache(version, zipPath);
            return zipPath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            global::SVL.Core.Platform.Diagnostics.RuntimeDebugLog.WriteLine($"[SmapiDownloadService] 下载失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 处理外部 NXM 回调（来自 P0-2 的通用 NXM 入队路由）。
    /// 若 NXM 链接匹配当前等待的 SMAPI mod/file id，则下载 zip 并完成 TCS。
    /// </summary>
    /// <returns>是否匹配并处理了该 NXM 链接</returns>
    public async Task<bool> HandleNxmCallbackAsync(string nxmLink)
    {
        if (!_nxmLinkParser.TryParse(nxmLink, out var info, out _))
        {
            return false;
        }

        if (info.ResourceType != NxmResourceType.ModFile ||
            info.ModId != SmapiModId ||
            !_nxmWaiters.TryGetValue(info.FileId, out var waiter))
        {
            return false;
        }

        // 同一个 NXM 可能同时由协议回调和单实例管道各投递一次；每个等待项
        // 只允许一个回调启动 CDN 下载，后续重复回调视为已消费。
        if (Interlocked.Exchange(ref waiter.CallbackClaimed, 1) != 0)
        {
            return true;
        }

        // 匹配成功，开始下载
        var tempDir = Path.Combine(Path.GetTempPath(), "SVL", "smapi");
        Directory.CreateDirectory(tempDir);
        var zipPath = Path.Combine(tempDir, $"SMAPI-nxm-{info.FileId}.zip");

        try
        {
            // 浏览器回传的 NXM 链接带有一次性 key/expires/user_id；使用这些凭据
            // 解析 CDN 地址，不依赖本地 API Key，也不能只返回一个尚未生成的路径。
            var resolved = await _nexusModDownloadResolverService.ResolveDownloadUrlAsync(
                info,
                apiKey: string.Empty,
                accessToken: string.Empty,
                waiter.CancellationToken);
            if (!resolved.IsSuccess || string.IsNullOrWhiteSpace(resolved.DownloadUrl))
            {
                throw new InvalidOperationException(resolved.Message);
            }

            await _httpDownloadService.DownloadAsync(
                resolved.DownloadUrl,
                zipPath,
                onProgress: null,
                cancellationToken: waiter.CancellationToken,
                cacheValidator: IsValidSmapiArchive);

            ModpackInstallService.TryUnwrapDoubleZipped(zipPath);

            if (!IsValidSmapiArchive(zipPath))
            {
                TryDeleteFile(zipPath);
                throw new InvalidDataException("Nexus 返回的 SMAPI 压缩包校验失败");
            }

            waiter.Completion.TrySetResult(zipPath);
            return true;
        }
        catch (Exception ex)
        {
            TryDeleteFile(zipPath);
            waiter.Completion.TrySetException(ex);
            return true;
        }
    }

    /// <summary>取消全部 SMAPI NXM 等待（兼容旧的无参数调用入口）。</summary>
    public void CancelNxmWait()
    {
        foreach (var waiter in _nxmWaiters.Values)
        {
            waiter.Cancel();
        }
    }

    private async Task<string?> TryDownloadViaNxmCallbackAsync(
        SmapiVersionEntry version,
        DownloadTaskItem? taskItem,
        Action<string>? progressText,
        CancellationToken cancellationToken)
    {
        if (version.FileId is not > 0)
        {
            return null;
        }

        var waiter = new SmapiNxmWaiter(version.FileId.Value, cancellationToken);
        if (!_nxmWaiters.TryAdd(waiter.FileId, waiter))
        {
            // 同一 FileID 已有浏览器回退在等待；不覆盖原等待器，当前调用
            // 仍可继续尝试自己的 DownloadUrl，避免结果串到另一项任务。
            waiter.Dispose();
            progressText?.Invoke("该 SMAPI 文件已有浏览器下载等待，当前任务将尝试直链...");
            return null;
        }

        taskItem?.Let(t => t.SetState(DownloadTaskState.Pending, "等待浏览器下载（非 Premium）"));
        progressText?.Invoke("请在浏览器中点击 Manual Download，启动器将自动接管...");

        try
        {
            // 等待 NXM 回调或超时（30 分钟，对齐旧行为）
            var completed = await Task.WhenAny(
                waiter.Completion.Task,
                Task.Delay(TimeSpan.FromMinutes(30), waiter.CancellationToken));

            // WhenAny 不会传播已取消任务的异常；无论哪个任务先返回，都先
            // 检查等待项的取消状态，防止用户取消后仍回退到 HTTP 下载。
            waiter.CancellationToken.ThrowIfCancellationRequested();

            if (completed == waiter.Completion.Task)
            {
                try
                {
                    return await waiter.Completion.Task;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 回调已匹配但 CDN 解析/下载失败时，让 DownloadZipAsync 继续尝试
                    // 可用的 DownloadUrl，而不是把等待异常直接升级为 UI 崩溃。
                    global::SVL.Core.Platform.Diagnostics.RuntimeDebugLog.WriteLine($"[SmapiDownloadService] NXM 回调下载失败: {ex.Message}");
                    return null;
                }
            }

            return null;
        }
        finally
        {
            if (_nxmWaiters.TryGetValue(waiter.FileId, out var current) &&
                ReferenceEquals(current, waiter))
            {
                _nxmWaiters.TryRemove(waiter.FileId, out _);
            }

            waiter.Dispose();
        }
    }

    private sealed class SmapiNxmWaiter : IDisposable
    {
        public SmapiNxmWaiter(long fileId, CancellationToken cancellationToken)
        {
            FileId = fileId;
            Completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationRegistration = CancellationSource.Token.Register(
                () => Completion.TrySetCanceled(CancellationSource.Token));
        }

        public long FileId { get; }

        public TaskCompletionSource<string> Completion { get; }

        public CancellationTokenSource CancellationSource { get; }

        public CancellationToken CancellationToken => CancellationSource.Token;

        public int CallbackClaimed;

        private CancellationTokenRegistration CancellationRegistration { get; }

        public void Cancel()
        {
            try
            {
                CancellationSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 清理与取消并发时，等待项已经结束。
            }
        }

        public void Dispose()
        {
            CancellationRegistration.Dispose();
            CancellationSource.Dispose();
        }
    }

    private static string NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return "latest";
        }

        var v = version.Trim();
        if (v.StartsWith("SMAPI ", StringComparison.OrdinalIgnoreCase))
        {
            v = v[6..].Trim();
        }
        return v;
    }

    private static bool IsNexusVersion(SmapiVersionEntry version)
    {
        return version != null &&
               version.Source?.Equals("NexusMods", StringComparison.OrdinalIgnoreCase) == true &&
               version.FileId is > 0;
    }

    private const long CurseforgeSmapiProjectId = 898372;

    private static bool IsCurseforgeVersion(SmapiVersionEntry version)
    {
        return version != null &&
               version.Source?.Equals("Curseforge", StringComparison.OrdinalIgnoreCase) == true &&
               version.FileId is > 0;
    }

    private static void TrySaveSourceCache(SmapiVersionEntry version, string zipPath)
    {
        TrySaveNexusCache(version, zipPath);
        if (IsCurseforgeVersion(version))
        {
            CurseforgeDownloadCache.Save(
                CurseforgeSmapiProjectId,
                version.FileId!.Value,
                zipPath,
                IsValidSmapiArchive);
        }
    }

    private static void TrySaveNexusCache(SmapiVersionEntry version, string zipPath)
    {
        if (IsNexusVersion(version))
        {
            NexusDownloadCache.Save(SmapiModId, version.FileId!.Value, zipPath, IsValidSmapiArchive);
        }
    }

    private static bool TryCopyValidatedArchive(string sourcePath, string destinationPath)
    {
        try
        {
            File.Copy(sourcePath, destinationPath, overwrite: true);
            ModpackInstallService.TryUnwrapDoubleZipped(destinationPath);
            if (IsValidSmapiArchive(destinationPath))
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            global::SVL.Core.Platform.Diagnostics.RuntimeDebugLog.WriteLine($"[SmapiDownloadService] 复制来源缓存失败: {ex.Message}");
        }

        TryDeleteFile(destinationPath);
        return false;
    }

    /// <summary>
    /// 校验 SMAPI 安装包：先处理历史双层 ZIP，再确认存在 install.dat。
    /// 不能只检查 ZIP 完整性，否则普通 Mod/错误归档也可能污染 SMAPI 缓存。
    /// </summary>
    private static bool IsValidSmapiArchive(string path)
    {
        try
        {
            return ModpackInstallService.TryNormalizeSmapiArchive(path);
        }
        catch (Exception ex)
        {
            global::SVL.Core.Platform.Diagnostics.RuntimeDebugLog.WriteLine($"[SmapiDownloadService] SMAPI 安装包校验失败: {ex.Message}");
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            global::SVL.Core.Platform.Diagnostics.RuntimeDebugLog.WriteLine($"[SmapiDownloadService] 删除文件失败: {ex.Message}");
        }
    }
}

/// <summary>局部扩展：let 风格的 null 安全调用（避免与 System 命名冲突）。</summary>
internal static class LetExtensions
{
    internal static void Let<T>(this T? value, Action<T> action) where T : class
    {
        if (value != null)
        {
            action(value);
        }
    }
}
