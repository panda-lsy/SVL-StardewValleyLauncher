using Avalonia;
using SVL.Core.Platform.Services;

namespace SVL.Avalonia;

internal static class Program
{
    /// <summary>
    /// 启动时从命令行提取的 NXM 链接（如有）。由 App 读取并路由到下载页。
    /// 浏览器调用 `SVL.Avalonia.exe "nxm://..."` 时，链接作为 args[0] 传入。
    /// </summary>
    public static string? PendingNxmUrl { get; private set; }

    /// <summary>单实例服务实例。App 在 OnFrameworkInitializationCompleted 中调用 StartListening 接收转发消息。</summary>
    public static SingleInstanceService SingleInstance { get; } = new();

    [STAThread]
    public static void Main(string[] args)
    {
        // 1. 扫描命令行参数，提取 nxm:// 链接（浏览器协议回调传入）。
        PendingNxmUrl = ExtractNxmUrl(args);

        // 2. 单实例检查：若已有实例运行，转发 NXM 链接后退出。
        if (SingleInstance.TryAcquire())
        {
            // 首个实例，继续启动。StartListening 由 App 初始化时调用。
        }
        else
        {
            // 已有实例运行，转发链接后立即退出。
            if (!string.IsNullOrEmpty(PendingNxmUrl))
            {
                SingleInstance.ForwardToRunningInstance($"NXM {PendingNxmUrl}");
            }
            SingleInstance.Stop();
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    /// <summary>从命令行参数中提取首个 nxm:// 开头的链接。</summary>
    private static string? ExtractNxmUrl(string[] args)
    {
        if (args is null || args.Length == 0)
        {
            return null;
        }

        foreach (var arg in args)
        {
            if (!string.IsNullOrEmpty(arg) &&
                arg.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
            {
                return arg;
            }
        }

        return null;
    }
}
