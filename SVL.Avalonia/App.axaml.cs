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
    private static TrayIconService? s_trayIconService;
    private static bool s_isExiting;
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
            var settingsStore = new AppUserSettingsStore();
            var instanceRegistryStore = new InstanceRegistryStore();
            LegacyConfigurationMigrationResult migration;
            try
            {
                migration = new LegacyConfigurationMigrationService(settingsStore, instanceRegistryStore).Migrate();
            }
            catch (Exception ex)
            {
                // 旧配置迁移属于增强功能；文件权限/损坏不应阻止 Avalonia 主界面启动。
                System.Diagnostics.Debug.WriteLine($"[Migration] 旧配置迁移失败，继续启动: {ex.Message}");
                migration = new LegacyConfigurationMigrationResult();
            }
            if (migration.SettingsChanged || migration.ImportedInstanceCount > 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Migration] 已迁移旧 WPF 配置：设置 {migration.ImportedSettingsCount} 项，实例 {migration.ImportedInstanceCount} 个");
            }
            foreach (var migrationError in migration.Errors)
            {
                System.Diagnostics.Debug.WriteLine($"[Migration] {migrationError}");
            }

            var settings = settingsStore.Load();
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

            s_trayIconService = new TrayIconService(
                () => ShowMainWindow(mainWindow),
                () => ExitApplication(desktop, mainWindow));
            s_trayIconService.TryInitialize();
            mainWindow.ShouldMinimizeToTrayOnClose = () =>
                s_trayIconService?.IsAvailable == true && mainVm.SettingsPage.MinimizeToTrayOnClose;
            mainWindow.Closed += (_, _) => OnMainWindowClosed(desktop);

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
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            mainWindow.Show();

            // 先 Show 再 Hide，确保平台窗口句柄已创建；无托盘支持时保持正常显示。
            if (s_mainVm?.SettingsPage.MinimizeToTrayOnStartup == true &&
                s_trayIconService?.IsAvailable == true)
            {
                mainWindow.Hide();
                DebugConsoleService.Instance.Append("已按设置启动到系统托盘。", DebugLogLevel.Debug);
            }
        });
        s_mainVm?.ResumePendingDownloadTasks();

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

    private static void ShowMainWindow(MainWindow mainWindow)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ShowMainWindow(mainWindow));
            return;
        }

        if (!mainWindow.IsVisible)
        {
            mainWindow.Show();
        }

        if (mainWindow.WindowState == WindowState.Minimized)
        {
            mainWindow.WindowState = WindowState.Normal;
        }

        mainWindow.Activate();
    }

    private static void ExitApplication(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow mainWindow)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ExitApplication(desktop, mainWindow));
            return;
        }

        if (s_isExiting)
        {
            return;
        }

        s_isExiting = true;
        s_trayIconService?.Dispose();
        s_trayIconService = null;
        mainWindow.CloseForExit();
        desktop.Shutdown();
    }

    private static void OnMainWindowClosed(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (!s_isExiting)
        {
            s_isExiting = true;
            s_trayIconService?.Dispose();
            s_trayIconService = null;
        }

        // ShutdownMode 保持 Explicit，避免隐藏到托盘时因 MainWindow 状态变化误退出；
        // 真正关闭主窗口后由这里完成应用进程收尾。
        desktop.Shutdown();
    }
}
