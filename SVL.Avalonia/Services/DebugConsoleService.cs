using Avalonia.Threading;
using System.Collections.ObjectModel;

namespace SVL.Avalonia.Services;

/// <summary>调试日志级别（数值越大，优先级越高）。</summary>
public enum DebugLogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
    None = 4
}

/// <summary>单条日志条目，含级别用于着色和过滤。</summary>
public sealed class DebugLogEntry
{
    public string Text { get; init; } = string.Empty;
    public string Level { get; init; } = "Info";
}

public sealed class DebugConsoleService
{
    private const int MaxLines = 800;
    private readonly object _gate = new();
    private readonly Queue<DebugLogEntry> _buffer = new();

    public static DebugConsoleService Instance { get; } = new();

    public event Action<DebugLogEntry>? LineAdded;

    public event Action? Cleared;

    private DebugConsoleService()
    {
    }

    public IReadOnlyList<DebugLogEntry> Snapshot()
    {
        lock (_gate)
        {
            return _buffer.ToList();
        }
    }

    /// <summary>追加一条日志（自动推断级别）。全部保留，过滤由 ViewModel 负责。</summary>
    public void Append(string message)
    {
        Append(message, InferLevel(message));
    }

    /// <summary>追加一条带明确级别的日志。全部保留，过滤由 ViewModel 负责。</summary>
    public void Append(string message, DebugLogLevel level)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var levelTag = level switch
        {
            DebugLogLevel.Debug => "DEBUG",
            DebugLogLevel.Info => "INFO",
            DebugLogLevel.Warning => "WARN",
            DebugLogLevel.Error => "ERROR",
            _ => "INFO"
        };

        var line = $"[{DateTime.Now:HH:mm:ss}] [{levelTag}] {message}";
        var entry = new DebugLogEntry { Text = line, Level = levelTag };

        lock (_gate)
        {
            _buffer.Enqueue(entry);
            while (_buffer.Count > MaxLines)
            {
                _buffer.Dequeue();
            }
        }

        Dispatcher.UIThread.Post(() => LineAdded?.Invoke(entry));
    }

    public void Clear()
    {
        lock (_gate)
        {
            _buffer.Clear();
        }

        Dispatcher.UIThread.Post(() => Cleared?.Invoke());
    }

    /// <summary>从消息文本推断日志级别。优先匹配显式级别标记，其次匹配关键词。</summary>
    internal static DebugLogLevel InferLevel(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return DebugLogLevel.Info;
        }

        var span = message.AsSpan();

        // 优先匹配显式级别标记（如 SVL.Core.Logging.Log 输出的 "[ERROR]"、"[WARN]" 等）
        if (span.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase) ||
            span.Contains("[CRITICAL]", StringComparison.OrdinalIgnoreCase))
        {
            return DebugLogLevel.Error;
        }

        if (span.Contains("[WARN]", StringComparison.OrdinalIgnoreCase))
        {
            return DebugLogLevel.Warning;
        }

        if (span.Contains("[DEBUG]", StringComparison.OrdinalIgnoreCase) ||
            span.Contains("[TRACE]", StringComparison.OrdinalIgnoreCase))
        {
            return DebugLogLevel.Debug;
        }

        if (span.Contains("[INFO]", StringComparison.OrdinalIgnoreCase))
        {
            return DebugLogLevel.Info;
        }

        // 回退：关键词匹配
        if (span.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            span.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
            span.Contains("fail", StringComparison.OrdinalIgnoreCase))
        {
            return DebugLogLevel.Error;
        }

        if (span.Contains("warn", StringComparison.OrdinalIgnoreCase))
        {
            return DebugLogLevel.Warning;
        }

        if (span.Contains("debug", StringComparison.OrdinalIgnoreCase) ||
            span.Contains("trace", StringComparison.OrdinalIgnoreCase))
        {
            return DebugLogLevel.Debug;
        }

        return DebugLogLevel.Info;
    }
}

internal sealed class DebugTraceListener : System.Diagnostics.TraceListener
{
    private readonly object _gate = new();
    private readonly System.Text.StringBuilder _pending = new();

    public override void Write(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        lock (_gate)
        {
            _pending.Append(message);
        }
    }

    public override void WriteLine(string? message)
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(message))
            {
                _pending.Append(message);
            }

            var line = _pending.ToString();
            _pending.Clear();
            DebugConsoleService.Instance.Append(line);
        }
    }
}

public static class DebugTraceBootstrapper
{
    private static int _initialized;

    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
        {
            return;
        }

        System.Diagnostics.Trace.AutoFlush = true;
        System.Diagnostics.Trace.Listeners.Add(new DebugTraceListener());
    }
}
