using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace SVL.Core.Stardew.ResourceProject.NexusMods;

/// <summary>
/// NexusMods API 速率限制信息
/// </summary>
public class NexusRateLimit : INotifyPropertyChanged
{
    private int _hourlyLimit;
    private int _hourlyRemaining;
    private int _dailyLimit;
    private int _dailyRemaining;
    private DateTime _lastUpdated;

    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>
    /// 每小时请求限制
    /// </summary>
    public int HourlyLimit
    {
        get => _hourlyLimit;
        set
        {
            if (_hourlyLimit != value)
            {
                _hourlyLimit = value;
                OnPropertyChanged(nameof(HourlyLimit));
                OnPropertyChanged(nameof(HourlyUsagePercent));
            }
        }
    }

    /// <summary>
    /// 每小时剩余请求次数
    /// </summary>
    public int HourlyRemaining
    {
        get => _hourlyRemaining;
        set
        {
            if (_hourlyRemaining != value)
            {
                _hourlyRemaining = value;
                OnPropertyChanged(nameof(HourlyRemaining));
                OnPropertyChanged(nameof(HourlyUsagePercent));
            }
        }
    }

    /// <summary>
    /// 每天请求限制
    /// </summary>
    public int DailyLimit
    {
        get => _dailyLimit;
        set
        {
            if (_dailyLimit != value)
            {
                _dailyLimit = value;
                OnPropertyChanged(nameof(DailyLimit));
                OnPropertyChanged(nameof(DailyUsagePercent));
            }
        }
    }

    /// <summary>
    /// 每天剩余请求次数
    /// </summary>
    public int DailyRemaining
    {
        get => _dailyRemaining;
        set
        {
            if (_dailyRemaining != value)
            {
                _dailyRemaining = value;
                OnPropertyChanged(nameof(DailyRemaining));
                OnPropertyChanged(nameof(DailyUsagePercent));
            }
        }
    }

    /// <summary>
    /// 最后更新时间
    /// </summary>
    public DateTime LastUpdated
    {
        get => _lastUpdated;
        set
        {
            if (_lastUpdated != value)
            {
                _lastUpdated = value;
                OnPropertyChanged(nameof(LastUpdated));
                OnPropertyChanged(nameof(IsInitialized));
            }
        }
    }

    /// <summary>
    /// 是否已初始化
    /// </summary>
    public bool IsInitialized => LastUpdated > DateTime.MinValue;

    /// <summary>
    /// 每小时使用百分比
    /// </summary>
    public double HourlyUsagePercent
    {
        get
        {
            if (_hourlyLimit == 0) return 0;
            return 100.0 - (_hourlyRemaining * 100.0 / _hourlyLimit);
        }
    }

    /// <summary>
    /// 每天使用百分比
    /// </summary>
    public double DailyUsagePercent
    {
        get
        {
            if (_dailyLimit == 0) return 0;
            return 100.0 - (_dailyRemaining * 100.0 / _dailyLimit);
        }
    }

    /// <summary>
    /// 从 HTTP 响应头更新速率限制信息
    /// </summary>
    public void UpdateFromHeaders(System.Net.Http.Headers.HttpResponseHeaders headers)
    {
        if (headers == null) return;

        // 解析每小时限制
        if (headers.TryGetValues("X-RL-Hourly-Limit", out var hourlyLimitValues) &&
            int.TryParse(hourlyLimitValues?.FirstOrDefault(), out var hourlyLimit))
        {
            HourlyLimit = hourlyLimit;
        }

        // 解析每小时剩余
        if (headers.TryGetValues("X-RL-Hourly-Remaining", out var hourlyRemainingValues) &&
            int.TryParse(hourlyRemainingValues?.FirstOrDefault(), out var hourlyRemaining))
        {
            HourlyRemaining = hourlyRemaining;
        }

        // 解析每天限制
        if (headers.TryGetValues("X-RL-Daily-Limit", out var dailyLimitValues) &&
            int.TryParse(dailyLimitValues?.FirstOrDefault(), out var dailyLimit))
        {
            DailyLimit = dailyLimit;
        }

        // 解析每天剩余
        if (headers.TryGetValues("X-RL-Daily-Remaining", out var dailyRemainingValues) &&
            int.TryParse(dailyRemainingValues?.FirstOrDefault(), out var dailyRemaining))
        {
            DailyRemaining = dailyRemaining;
        }

        LastUpdated = DateTime.Now;
    }

    /// <summary>
    /// 获取状态描述
    /// </summary>
    public string GetStatusText()
    {
        if (!IsInitialized)
            return "未获取";

        var status = $"小时: {HourlyRemaining}/{HourlyLimit}";
        if (DailyLimit > 0)
        {
            status += $" | 天: {DailyRemaining}/{DailyLimit}";
        }
        return status;
    }

    /// <summary>
    /// 检查是否接近限制
    /// </summary>
    public bool IsNearLimit(int threshold = 10)
    {
        return HourlyRemaining <= threshold || (DailyLimit > 0 && DailyRemaining <= threshold);
    }

    /// <summary>
    /// 获取警告消息
    /// </summary>
    public string? GetWarningMessage()
    {
        if (!IsInitialized) return null;

        var warnings = new List<string>();

        if (HourlyRemaining <= 5)
        {
            warnings.Add($"每小时请求即将用尽（剩余 {HourlyRemaining} 次）");
        }
        else if (HourlyRemaining <= 10)
        {
            warnings.Add($"每小时请求较少（剩余 {HourlyRemaining} 次）");
        }

        if (DailyLimit > 0 && DailyRemaining <= 50)
        {
            warnings.Add($"每天请求即将用尽（剩余 {DailyRemaining} 次）");
        }

        return warnings.Count > 0 ? string.Join("\n", warnings) : null;
    }

    /// <summary>
    /// 触发属性更改通知
    /// </summary>
    protected void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
