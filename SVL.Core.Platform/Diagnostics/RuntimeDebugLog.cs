using System.Diagnostics;
using System.Threading;

namespace SVL.Core.Platform.Diagnostics;

/// <summary>运行时可开关的诊断日志，Release 构建也可写入调试窗口。</summary>
public static class RuntimeDebugLog
{
    private static int s_enabled;

    public static bool IsEnabled => Volatile.Read(ref s_enabled) != 0;

    public static void SetEnabled(bool enabled) => Volatile.Write(ref s_enabled, enabled ? 1 : 0);

    public static void WriteLine(object? message)
    {
        if (IsEnabled)
        {
            Trace.WriteLine(message);
        }
    }
}
