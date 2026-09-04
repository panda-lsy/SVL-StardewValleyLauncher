using SVL.Core.Platform.Abstractions;

namespace SVL.Avalonia.Services;

/// <summary>
/// 通用浏览器下载回退服务：非 Premium 用户在浏览器点击 Manual Download / Add collection 后，
/// 通过 NXM 协议回传下载凭据，本服务等待匹配的 NXM 回调并返回可用下载信息。
/// 支持多个 mod/file id 并发等待，也支持 Collection 类型的回调等待。
/// </summary>
public sealed class BrowserDownloadFallbackService
{
    private readonly INxmLinkParser _nxmLinkParser;
    private readonly IExternalProcessService _externalProcessService;
    private readonly object _waitersLock = new();
    private readonly Dictionary<(long ModId, long FileId), NxmWaitEntry> _waiters = new();
    private readonly Dictionary<(string Slug, int Revision), NxmWaitEntry> _collectionWaiters = new(CollectionKeyComparer.Instance);

    public BrowserDownloadFallbackService(
        INxmLinkParser nxmLinkParser,
        IExternalProcessService externalProcessService)
    {
        _nxmLinkParser = nxmLinkParser;
        _externalProcessService = externalProcessService;
    }

    /// <summary>
    /// 打开浏览器到指定 Nexus 页面，并等待匹配的 NXM 回调（30 分钟超时）。
    /// 返回 NXM 链接原始字符串（调用方可用于解析 key/expires 后直接下载）。
    /// </summary>
    /// <param name="modId">Nexus mod id</param>
    /// <param name="fileId">Nexus file id</param>
    /// <param name="browserUrl">要在浏览器打开的页面 URL（通常含 ?nmm=1）</param>
    /// <param name="onWaiting">进入等待状态时的回调（UI 提示）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>匹配到的 NXM 链接原始字符串；超时或取消返回 null</returns>
    public async Task<string?> WaitForNxmCallbackAsync(
        long modId,
        long fileId,
        string browserUrl,
        Action<string>? onWaiting = null,
        CancellationToken cancellationToken = default)
    {
        if (modId <= 0 || fileId <= 0)
        {
            return null;
        }

        var entry = new NxmWaitEntry
        {
            Source = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously),
            Cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
        };
        entry.Cts.Token.Register(() => entry.Source.TrySetCanceled());

        lock (_waitersLock)
        {
            // 若已有同 mod/file 的等待，替换并取消旧的
            if (_waiters.TryGetValue((modId, fileId), out var existing))
            {
                existing.Source.TrySetCanceled();
                existing.Cts.Dispose();
                _waiters.Remove((modId, fileId));
            }

            _waiters[(modId, fileId)] = entry;
        }

        // 打开浏览器
        if (!string.IsNullOrWhiteSpace(browserUrl))
        {
            _externalProcessService.TryOpenPath(browserUrl);
        }

        onWaiting?.Invoke("请在浏览器中点击 Manual Download，启动器将自动接管...");

        try
        {
            var completed = await Task.WhenAny(
                entry.Source.Task,
                Task.Delay(TimeSpan.FromMinutes(30), entry.Cts.Token));

            if (completed == entry.Source.Task)
            {
                return await entry.Source.Task;
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            lock (_waitersLock)
            {
                _waiters.Remove((modId, fileId));
            }
            entry.Cts.Dispose();
        }
    }

    /// <summary>
    /// 打开浏览器到指定 Nexus Collection 页面，并等待匹配的 Collection NXM 回调（30 分钟超时）。
    /// 用户在浏览器点击 "Add collection" 后，NXM 协议回传带 key/expires 的链接，本方法捕获后返回。
    /// </summary>
    /// <param name="collectionSlug">Collection slug（URL 标识符）</param>
    /// <param name="revision">Collection 修订号（-1 表示最新）</param>
    /// <param name="browserUrl">要在浏览器打开的 Collection 页面 URL</param>
    /// <param name="onWaiting">进入等待状态时的回调（UI 提示）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>匹配到的 NXM 链接原始字符串；超时或取消返回 null</returns>
    public async Task<string?> WaitForCollectionNxmCallbackAsync(
        string collectionSlug,
        int revision,
        string browserUrl,
        Action<string>? onWaiting = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(collectionSlug))
        {
            return null;
        }

        var key = (collectionSlug, revision);
        var entry = new NxmWaitEntry
        {
            Source = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously),
            Cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
        };
        entry.Cts.Token.Register(() => entry.Source.TrySetCanceled());

        lock (_waitersLock)
        {
            // 若已有同 slug/revision 的等待，替换并取消旧的
            if (_collectionWaiters.TryGetValue(key, out var existing))
            {
                existing.Source.TrySetCanceled();
                existing.Cts.Dispose();
                _collectionWaiters.Remove(key);
            }

            _collectionWaiters[key] = entry;
        }

        // 打开浏览器
        if (!string.IsNullOrWhiteSpace(browserUrl))
        {
            _externalProcessService.TryOpenPath(browserUrl);
        }

        onWaiting?.Invoke("请在浏览器中点击 Add collection，启动器将自动接管...");

        try
        {
            var completed = await Task.WhenAny(
                entry.Source.Task,
                Task.Delay(TimeSpan.FromMinutes(30), entry.Cts.Token));

            if (completed == entry.Source.Task)
            {
                return await entry.Source.Task;
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            lock (_waitersLock)
            {
                _collectionWaiters.Remove(key);
            }
            entry.Cts.Dispose();
        }
    }

    /// <summary>
    /// 处理外部 NXM 回调。若链接匹配某个等待中的 mod/file id 或 Collection slug/revision，
    /// 完成其 TCS 并返回 true。
    /// </summary>
    public bool HandleNxmCallback(string nxmLink)
    {
        if (string.IsNullOrWhiteSpace(nxmLink) ||
            !_nxmLinkParser.TryParse(nxmLink, out var info, out _))
        {
            return false;
        }

        // Collection 类型回调
        if (info.ResourceType == NxmResourceType.Collection &&
            !string.IsNullOrWhiteSpace(info.CollectionSlug))
        {
            lock (_waitersLock)
            {
                // 先尝试精确匹配 revision
                if (_collectionWaiters.TryGetValue((info.CollectionSlug, info.RevisionNumber), out var entry))
                {
                    return entry.Source.TrySetResult(nxmLink);
                }

                // revision 为 -1（最新）时，匹配任意 revision 的等待（取第一个匹配的）
                foreach (var kvp in _collectionWaiters)
                {
                    if (string.Equals(kvp.Key.Slug, info.CollectionSlug, StringComparison.OrdinalIgnoreCase))
                    {
                        return kvp.Value.Source.TrySetResult(nxmLink);
                    }
                }
            }

            return false;
        }

        // ModFile 类型回调
        if (info.ResourceType != NxmResourceType.ModFile ||
            info.ModId <= 0 || info.FileId <= 0)
        {
            return false;
        }

        lock (_waitersLock)
        {
            if (_waiters.TryGetValue((info.ModId, info.FileId), out var entry))
            {
                return entry.Source.TrySetResult(nxmLink);
            }
        }

        return false;
    }

    /// <summary>取消所有等待中的 NXM 回调（ModFile + Collection）。</summary>
    public void CancelAllWaits()
    {
        lock (_waitersLock)
        {
            foreach (var entry in _waiters.Values)
            {
                entry.Source.TrySetCanceled();
                entry.Cts.Dispose();
            }

            _waiters.Clear();

            foreach (var entry in _collectionWaiters.Values)
            {
                entry.Source.TrySetCanceled();
                entry.Cts.Dispose();
            }

            _collectionWaiters.Clear();
        }
    }

    private sealed class NxmWaitEntry
    {
        public TaskCompletionSource<string> Source { get; init; } = null!;
        public CancellationTokenSource Cts { get; init; } = null!;
    }

    /// <summary>Collection waiter 键比较器：slug 不区分大小写，revision 精确匹配。</summary>
    private sealed class CollectionKeyComparer : IEqualityComparer<(string Slug, int Revision)>
    {
        public static readonly CollectionKeyComparer Instance = new();

        public bool Equals((string Slug, int Revision) x, (string Slug, int Revision) y)
        {
            return string.Equals(x.Slug, y.Slug, StringComparison.OrdinalIgnoreCase) &&
                   x.Revision == y.Revision;
        }

        public int GetHashCode((string Slug, int Revision) obj)
        {
            return StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Slug) ^ obj.Revision.GetHashCode();
        }
    }
}
