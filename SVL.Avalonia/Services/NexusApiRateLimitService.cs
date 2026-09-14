using System.Net.Http.Headers;

namespace SVL.Avalonia.Services;

/// <summary>
/// Avalonia 侧 Nexus API 限额快照。
/// Nexus API 会通过 X-RL-* 响应头返回限额；所有请求入口都写入同一个快照，
/// 设置页因此不会只显示某一次登录验证请求的局部数据。
/// </summary>
public static class NexusApiRateLimitService
{
    private static readonly object SyncRoot = new();
    private static NexusApiRateLimitSnapshot _snapshot = new();

    public static event Action? SnapshotChanged;

    public static NexusApiRateLimitSnapshot GetSnapshot()
    {
        lock (SyncRoot)
        {
            return _snapshot with { };
        }
    }

    public static void Record(HttpResponseHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var hasValue = false;
        lock (SyncRoot)
        {
            var snapshot = _snapshot;
            if (TryRead(headers, "X-RL-Hourly-Limit", out var hourlyLimit))
            {
                snapshot = snapshot with { HourlyLimit = hourlyLimit };
                hasValue = true;
            }

            if (TryRead(headers, "X-RL-Hourly-Remaining", out var hourlyRemaining))
            {
                snapshot = snapshot with { HourlyRemaining = hourlyRemaining };
                hasValue = true;
            }

            if (TryRead(headers, "X-RL-Daily-Limit", out var dailyLimit))
            {
                snapshot = snapshot with { DailyLimit = dailyLimit };
                hasValue = true;
            }

            if (TryRead(headers, "X-RL-Daily-Remaining", out var dailyRemaining))
            {
                snapshot = snapshot with { DailyRemaining = dailyRemaining };
                hasValue = true;
            }

            if (!hasValue)
            {
                return;
            }

            _snapshot = snapshot with { LastUpdated = DateTimeOffset.UtcNow };
        }

        SnapshotChanged?.Invoke();
    }

    public static void Clear()
    {
        lock (SyncRoot)
        {
            _snapshot = new NexusApiRateLimitSnapshot();
        }

        SnapshotChanged?.Invoke();
    }

    private static bool TryRead(HttpResponseHeaders headers, string name, out int value)
    {
        value = 0;
        return headers.TryGetValues(name, out var values) &&
               int.TryParse(values.FirstOrDefault(), out value) &&
               value >= 0;
    }
}

public sealed record NexusApiRateLimitSnapshot(
    int HourlyLimit = 0,
    int HourlyRemaining = 0,
    int DailyLimit = 0,
    int DailyRemaining = 0,
    DateTimeOffset LastUpdated = default)
{
    public bool IsInitialized => LastUpdated != default;

    public string HourlyUsageText => FormatUsage(HourlyLimit, HourlyRemaining);

    public string DailyUsageText => FormatUsage(DailyLimit, DailyRemaining);

    public string StatusText
    {
        get
        {
            if (!IsInitialized)
            {
                return "未获取";
            }

            var status = $"小时剩余 {Math.Max(0, HourlyRemaining)}/{Math.Max(0, HourlyLimit)}";
            if (DailyLimit > 0)
            {
                status += $" | 每日剩余 {Math.Max(0, DailyRemaining)}/{DailyLimit}";
            }

            return status;
        }
    }

    private string FormatUsage(int limit, int remaining)
    {
        if (!IsInitialized || limit <= 0)
        {
            return "-";
        }

        var used = Math.Clamp(limit - remaining, 0, limit);
        return $"{used}/{limit}";
    }
}
