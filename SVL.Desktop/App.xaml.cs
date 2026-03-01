using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Threading.Tasks;
using SVL.Core.App;
using SVL.Core.Config;
using SVL.Core.Logging;
using SVL.Core.Download.NexusMods;
using WpfMessageBox = System.Windows.MessageBox;

namespace SVL.Desktop;

public partial class App : System.Windows.Application
{
    // NXM URL 事件：当收到 NXM 协议回调时触发
    public static event EventHandler<NxmUrlReceivedEventArgs>? NxmUrlReceived;

    // 存储启动时收到的 NXM URL（延迟处理）
    private static (NxmUrl url, string originalUrl)? _pendingNxmUrl;

    // 用于从管道接收 NXM URL 的同步锁
    private static readonly object _nxmLock = new();

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    public App()
    {
        // 在 Debug 模式下分配控制台以显示日志
        #if DEBUG
        if (AllocConsole())
        {
            Console.Title = "SVL Debug Console";
            Console.WriteLine("========================================");
            Console.WriteLine("SVL - Stardew Valley Launcher");
            Console.WriteLine("Debug mode - Console enabled");
            Console.WriteLine("========================================");
            Console.WriteLine();
        }
        #endif

        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                Log.Error(ex, "Unhandled exception");
            }
        };
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            // ===== 单实例检查（必须在最开始） =====
            // 首先检查命令行参数中是否有 NXM URL
            string? nxmUrlFromArgs = null;
            if (e.Args.Length > 0)
            {
                foreach (var arg in e.Args)
                {
                    if (arg.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
                    {
                        nxmUrlFromArgs = arg;
                        Log.Info($"[App] 从命令行检测到 NXM URL: {nxmUrlFromArgs}");
                        break;
                    }
                }
            }

            // 设置 NXM URL 回调（用于接收来自其他实例的 NXM URL）
            SingleInstanceService.SetNxmUrlCallback(OnNxmUrlFromPipe);

            // 执行单实例检查（如果有 NXM URL 则传递过去）
            SingleInstanceService.CheckSingleInstance(nxmUrlFromArgs);

            Log.Info("[App] OnStartup started");

            // 如果从命令行获取到了 NXM URL，解析并存储
            if (nxmUrlFromArgs != null)
            {
                try
                {
                    var parsedUrl = NxmUrl.Parse(nxmUrlFromArgs);
                    _pendingNxmUrl = (parsedUrl, nxmUrlFromArgs);
                    Log.Info($"[App] NXM URL 解析成功: GameId={parsedUrl.GameId}, ModId={parsedUrl.ModId}, FileId={parsedUrl.FileId}");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"[App] 解析 NXM URL 失败: {nxmUrlFromArgs}");
                }
            }

            // 先初始化服务
            await ApplicationService.InitializeAsync();
            Log.Info("[App] ApplicationService initialized");

            // 恢复上次保存的主题设置
            try
            {
                Services.ThemeService.RestoreFromConfig();
                Log.Info("[App] Theme restored from config");
            }
            catch (Exception themeEx)
            {
                Log.Warn($"[App] 恢复主题失败，使用默认主题: {themeEx.Message}");
            }

            var viewModel = new ViewModels.MainWindowViewModel();
            Log.Info("[App] ViewModel created");
            await viewModel.InitializeAsync();
            Log.Info("[App] ViewModel initialized");

            // 创建主窗口但不显示
            var mainWindow = new MainWindow(viewModel);
            Log.Info("[App] MainWindow created");

            // 显示启动画面（显示2秒后自动关闭并显示主窗口）
            var splashScreen = new Views.SplashScreen();
            splashScreen.ShowAndClose(mainWindow, 2000);

            Log.Info("[App] Startup completed");

            // 启动时检查更新（异步执行，不阻塞启动）
            _ = CheckForUpdatesOnStartupAsync();

            // 延迟处理 NXM URL，确保所有初始化都已完成
            if (_pendingNxmUrl != null)
            {
                Log.Info("[App] 延迟处理 NXM URL");

                await Task.Delay(100); // 短暂延迟确保事件订阅者已准备好

                var (url, originalUrl) = _pendingNxmUrl.Value;

                // 创建事件参数，通知需要置顶窗口
                var eventArgs = new NxmUrlReceivedEventArgs(
                    url,
                    shouldBringToFront: true,  // 从命令行启动的新实例需要置顶
                    originalUrl
                );

                NxmUrlReceived?.Invoke(this, eventArgs);
                _pendingNxmUrl = null;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start application");
            WpfMessageBox.Show($"启动失败: {ex.Message}\n\n{ex.StackTrace}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 处理来自管道（其他实例）的 NXM URL
    /// </summary>
    private static void OnNxmUrlFromPipe(string nxmUrl)
    {
        lock (_nxmLock)
        {
            try
            {
                Log.Info($"[App] 从管道收到 NXM URL: {nxmUrl}");

                var parsedUrl = NxmUrl.Parse(nxmUrl);
                Log.Info($"[App] NXM URL 解析成功: GameId={parsedUrl.GameId}, ModId={parsedUrl.ModId}, FileId={parsedUrl.FileId}");

                // 创建事件参数，通知需要置顶窗口
                var eventArgs = new NxmUrlReceivedEventArgs(
                    parsedUrl,
                    shouldBringToFront: true,  // 从其他实例接收需要置顶
                    nxmUrl
                );

                // 触发 NXM URL 接收事件
                NxmUrlReceived?.Invoke(null, eventArgs);
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"[App] 处理管道 NXM URL 失败: {nxmUrl}");
            }
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        await ApplicationService.ShutdownAsync();

        // 释放控制台
        #if DEBUG
        Console.WriteLine("========================================");
        Console.WriteLine("Press any key to exit...");
        Console.ReadKey();
        FreeConsole();
        #endif
    }

    /// <summary>
    /// 启动时检查更新（异步执行）
    /// </summary>
    private static async Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            // 检查是否启用了启动时检查更新
            var settings = AppConfig.GetSettings();
            if (!settings.AutoCheckUpdates)
            {
                Log.Info("[App] 启动时检查更新已禁用");
                return;
            }

            // 延迟 3 秒后检查更新，避免影响启动速度
            await Task.Delay(3000);

            Log.Info("[App] 开始检查启动器更新...");

            var preferGitee = settings.PreferredUpdateSource == 1;
            var result = await LauncherUpdateService.CheckForUpdateAsync(preferGitee);

            if (!result.Success)
            {
                Log.Warn($"[App] 检查更新失败: {result.ErrorMessage}");
                return;
            }

            if (!result.HasUpdate)
            {
                Log.Info("[App] 已是最新版本");
                return;
            }

            // 检查是否跳过了此版本
            if (!string.IsNullOrEmpty(settings.SkippedUpdateVersion))
            {
                if (result.LatestVersion.ToString() == settings.SkippedUpdateVersion)
                {
                    Log.Info($"[App] 用户已跳过版本 {result.LatestVersion} 的更新提醒");
                    return;
                }
            }

            Log.Info($"[App] 发现新版本 {result.LatestVersion}，显示更新对话框");

            // 在 UI 线程显示更新对话框
            await Current.Dispatcher.InvokeAsync(() =>
            {
                if (result.ReleaseInfo == null) return;

                var currentVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
                    ?? new Version(1, 1, 1, 0);

                var dialog = new Controls.UpdateDialog(currentVersion, result.ReleaseInfo)
                {
                    Owner = Current.MainWindow
                };

                dialog.ShowDialog();
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[App] 启动时检查更新失败");
        }
    }
}
