using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using SVL.Avalonia.Models;
using SVL.Avalonia.Services;
using SVL.Avalonia.ViewModels;
using SVL.Avalonia.Views;
using SVL.Core.Platform.Services;

namespace SVL.Avalonia;

public partial class App : Application
{
    private static DebugConsoleWindow? s_debugConsoleWindow;
    private static MainWindowViewModel? s_mainVm;
    private static readonly List<string> s_pendingPipeNxmUrls = new();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private static readonly string CrashLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SVL", "Avalonia", "logs", "crash.log");

    private static void LogUnhandled(string kind, string? message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{kind}]\n{message}\n\n");
        }
        catch
        {
            // best-effort
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 全局异常捕获：把未处理异常/未观察任务异常写到崩溃日志，便于定位登录闪退等。
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogUnhandled("Unhandled", args.ExceptionObject?.ToString());
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogUnhandled("UnobservedTask", args.Exception?.ToString());
            args.SetObserved();
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = new AppUserSettingsStore().Load();
            var enableDebugConsole = settings.DebugMode;

#if DEBUG
            enableDebugConsole = true;
#endif

            if (enableDebugConsole)
            {
                DebugTraceBootstrapper.Initialize();
            }

            // 启动时按设置开关注册 NXM 协议（Windows 写 HKCU 注册表，其他平台为空操作）。
            TryRegisterNxmProtocolOnStartup(settings);

            // 启动单实例管道监听：接收来自第二个实例转发的 NXM 链接。
            Program.SingleInstance.StartListening(OnPipeMessageReceived);

            // 匿名统计上报（每日一次，fire-and-forget，失败静默，不影响启动器使用）。
            // 可通过环境变量 SVL_DISABLE_ANON_TELEMETRY=1 关闭。
            _ = AnonymousUsageTelemetryService.ReportDailyActiveAsync();

            var mainVm = new MainWindowViewModel();
            s_mainVm = mainVm;

            var mainWindow = new MainWindow
            {
                DataContext = mainVm
            };

            desktop.MainWindow = mainWindow;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _ = ShowSplashThenMainAsync(desktop, mainWindow, enableDebugConsole);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>按设置开关在启动时注册 NXM 协议。失败不阻塞启动。</summary>
    private static void TryRegisterNxmProtocolOnStartup(AppUserSettings settings)
    {
        if (!settings.RegisterNxmProtocolOnStartup)
        {
            return;
        }

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return;
        }

        try
        {
            var service = new NxmProtocolRegistrationService();
            service.TryRegister(exePath);
        }
        catch
        {
            // 注册失败不阻塞启动，用户可在设置页手动重试。
        }
    }

    /// <summary>单实例管道消息回调（后台线程触发）。解析 "NXM &lt;url&gt;" 后切到 UI 线程处理。</summary>
    private static void OnPipeMessageReceived(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        string? nxmUrl = null;
        if (message.StartsWith("NXM ", StringComparison.Ordinal))
        {
            nxmUrl = message.Substring(4);
        }

        if (string.IsNullOrEmpty(nxmUrl))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (s_mainVm is null)
            {
                // ViewModel 尚未就绪，缓存待 DispatchPendingNxmLinksAsync 处理。
                s_pendingPipeNxmUrls.Add(nxmUrl);
                return;
            }

            _ = s_mainVm.HandleExternalNxmLinkAsync(nxmUrl);
        });
    }

    private static async Task ShowSplashThenMainAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        Window mainWindow,
        bool autoOpenDebugConsole)
    {
        var splash = new SplashWindow();

        try
        {
            desktop.MainWindow = splash;
            splash.Show();
            await Task.Delay(2000);
        }
        finally
        {
            if (splash.IsVisible)
            {
                splash.Close();
            }
        }

        desktop.MainWindow = mainWindow;
        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        await Dispatcher.UIThread.InvokeAsync(mainWindow.Show);

        if (autoOpenDebugConsole)
        {
            await Dispatcher.UIThread.InvokeAsync(() => OpenDebugConsole(mainWindow));
        }

        // 处理启动时收到的 NXM 链接：命令行传入的 + 单实例管道缓存的。
        await DispatchPendingNxmLinksAsync();
    }

    /// <summary>分派启动期间累积的 NXM 链接（命令行 + 管道缓存）到下载页。</summary>
    private static async Task DispatchPendingNxmLinksAsync()
    {
        var links = new List<string>();
        if (!string.IsNullOrEmpty(Program.PendingNxmUrl))
        {
            links.Add(Program.PendingNxmUrl);
        }

        links.AddRange(s_pendingPipeNxmUrls);
        s_pendingPipeNxmUrls.Clear();

        if (links.Count == 0 || s_mainVm is null)
        {
            return;
        }

        // 短暂延迟确保主窗口与下载页完成布局。
        await Task.Delay(100);

        foreach (var link in links)
        {
            await s_mainVm.HandleExternalNxmLinkAsync(link);
        }
    }

    private static void OpenDebugConsole(Window owner)
    {
        if (s_debugConsoleWindow is { IsVisible: true })
        {
            s_debugConsoleWindow.Activate();
            return;
        }

        var window = new DebugConsoleWindow
        {
            DataContext = new DebugConsoleViewModel(DebugConsoleService.Instance)
        };

        s_debugConsoleWindow = window;
        window.Closed += (_, _) => s_debugConsoleWindow = null;
        window.Show();
        DebugConsoleService.Instance.Append("Debug console auto-opened at startup.", DebugLogLevel.Info);
    }
}
