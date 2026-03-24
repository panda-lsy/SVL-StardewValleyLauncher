using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Threading.Tasks;
using System.Windows.Media;
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

    // 标记本次启动是否已显示过更新弹窗（防止重复检测）
    private static bool _hasShownUpdateDialogThisSession = false;

    // 标记是否为更新而退出（用于跳过 Debug 控制台的等待按键）
    private static bool _isExitingForUpdate = false;

    // 标记是否刚完成更新（用于显示更新完成对话框）
    private static bool _justUpdated = false;

    /// <summary>
    /// 清理旧版本 EXE 文件
    /// </summary>
    private static void CleanupOldVersions()
    {
        try
        {
            var currentExePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(currentExePath))
                return;

            var currentDir = System.IO.Path.GetDirectoryName(currentExePath);
            if (string.IsNullOrEmpty(currentDir))
                return;

            var currentExeName = System.IO.Path.GetFileName(currentExePath);

            // 查找所有 SVL.Desktop_v*.exe 文件（旧版本）
            var oldVersions = System.IO.Directory.GetFiles(currentDir, "SVL.Desktop_v*.exe")
                .Where(f => !System.IO.Path.GetFileName(f).Equals(currentExeName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (oldVersions.Count == 0)
                return;

            Log.Info($"[App] 发现 {oldVersions.Count} 个旧版本文件，正在清理...");

            foreach (var oldFile in oldVersions)
            {
                try
                {
                    System.IO.File.Delete(oldFile);
                    Log.Info($"[App] 已删除旧版本: {System.IO.Path.GetFileName(oldFile)}");
                }
                catch (Exception ex)
                {
                    Log.Warn($"[App] 无法删除旧版本 {System.IO.Path.GetFileName(oldFile)}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[App] 清理旧版本失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 标记本次启动已显示过更新弹窗
    /// </summary>
    public static void MarkUpdateDialogShown()
    {
        _hasShownUpdateDialogThisSession = true;
    }

    /// <summary>
    /// 标记应用程序正在为更新而退出
    /// </summary>
    public static void MarkExitingForUpdate()
    {
        _isExitingForUpdate = true;
    }

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

                try
                {
                    var pageInfo = "unknown";
                    var vm = Current?.MainWindow?.DataContext as SVL.Desktop.ViewModels.MainWindowViewModel;
                    if (vm != null)
                    {
                        pageInfo = $"CurrentPage={vm.CurrentPage}, Left={vm.LeftPanelContent?.GetType().Name ?? "null"}, Right={vm.RightPanelContent?.GetType().Name ?? "null"}";
                    }

                    var focused = System.Windows.Input.Keyboard.FocusedElement as FrameworkElement;
                    var over = System.Windows.Input.Mouse.DirectlyOver as FrameworkElement;
                    var focusInfo = $"Focused={focused?.GetType().Name ?? "null"}, MouseOver={over?.GetType().Name ?? "null"}";

                    var overTree = "MouseOverTree=null";
                    if (over != null)
                    {
                        var cursor = over as DependencyObject;
                        var depth = 0;
                        var parts = new System.Collections.Generic.List<string>();
                        while (cursor != null && depth < 8)
                        {
                            if (cursor is FrameworkElement fe)
                            {
                                var name = string.IsNullOrEmpty(fe.Name) ? "(no-name)" : fe.Name;
                                parts.Add($"{fe.GetType().Name}#{name}");
                            }
                            else
                            {
                                parts.Add(cursor.GetType().Name);
                            }

                            cursor = VisualTreeHelper.GetParent(cursor);
                            depth++;
                        }

                        overTree = $"MouseOverTree={string.Join(" <- ", parts)}";
                    }

                    Log.Error($"[UnhandledContext] {pageInfo}; {focusInfo}; {overTree}");
                }
                catch
                {
                    // ignore context logging errors
                }
            }
        };
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            // ===== 单实例检查（必须在最开始） =====
            // 首先检查命令行参数中是否有 NXM URL 或 --updated 标记
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
                    else if (arg.Equals("--updated", StringComparison.OrdinalIgnoreCase))
                    {
                        _justUpdated = true;
                        Log.Info("[App] 检测到 --updated 参数，将在启动后显示更新完成对话框");
                        // 清理旧版本 EXE
                        CleanupOldVersions();
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

            // 匿名活跃统计（每日一次，失败静默，不阻塞启动）
            _ = Services.AnonymousUsageTelemetryService.ReportDailyActiveAsync();

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

            // 如果刚完成更新，显示更新完成对话框
            if (_justUpdated)
            {
                _ = ShowUpdateCompleteDialogAsync();
            }

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

        // 释放控制台（如果是更新退出，不等待按键）
        #if DEBUG
        if (!_isExitingForUpdate)
        {
            Console.WriteLine("========================================");
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }
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
            // 如果本次启动已显示过更新弹窗，不再重复检测
            if (_hasShownUpdateDialogThisSession)
            {
                Log.Info("[App] 本次启动已显示过更新弹窗，跳过自动检测");
                return;
            }

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
            var includePrerelease = settings.CheckPrereleaseUpdates;
            var result = await LauncherUpdateService.CheckForUpdateAsync(preferGitee, includePrerelease);

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

            // 标记本次启动已显示过更新弹窗
            _hasShownUpdateDialogThisSession = true;

            // 在 UI 线程显示更新对话框
            await Current.Dispatcher.InvokeAsync(() =>
            {
                if (result.ReleaseInfo == null) return;

                var currentVersion = LauncherUpdateService.CurrentVersion;

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

    /// <summary>
    /// 显示更新完成对话框
    /// </summary>
    private static async Task ShowUpdateCompleteDialogAsync()
    {
        try
        {
            // 等待主窗口完全加载并显示
            await Task.Delay(2500);

            // 等待主窗口可用
            await Current.Dispatcher.InvokeAsync(() => { });

            // 获取当前版本
            var currentVersion = LauncherUpdateService.CurrentVersion;
            var versionString = currentVersion.Revision > 0 
                ? $"v{currentVersion.Major}.{currentVersion.Minor}.{currentVersion.Build}.{currentVersion.Revision}"
                : $"v{currentVersion.Major}.{currentVersion.Minor}.{currentVersion.Build}";

            // 尝试从更新源获取最新版本的更新日志
            string updateLog;
            try
            {
                // 获取配置中的更新源偏好
                var settings = AppConfig.GetSettings();
                var preferGitee = settings.PreferredUpdateSource == 1;
                var includePrerelease = settings.CheckPrereleaseUpdates;

                // 检查更新（会使用缓存，不会频繁请求）
                var result = await LauncherUpdateService.CheckForUpdateAsync(preferGitee, includePrerelease);

                if (result.Success && result.ReleaseInfo != null)
                {
                    // 使用从服务器获取的更新日志
                    var release = result.ReleaseInfo;
                    
                    // 优先使用 UpdateLog（Update.txt），其次使用 Body（发布说明）
                    if (!string.IsNullOrWhiteSpace(release.UpdateLog))
                    {
                        updateLog = release.UpdateLog;
                    }
                    else if (!string.IsNullOrWhiteSpace(release.Body))
                    {
                        updateLog = release.Body;
                    }
                    else
                    {
                        updateLog = "暂无更新日志";
                    }

                    Log.Info($"[App] 从 {result.Source} 获取到更新日志，长度: {updateLog.Length}");
                }
                else
                {
                    updateLog = GetDefaultUpdateLog();
                    Log.Warn("[App] 无法获取更新日志，使用默认内容");
                }
            }
            catch (Exception ex)
            {
                updateLog = GetDefaultUpdateLog();
                Log.Warn($"[App] 获取更新日志失败，使用默认内容: {ex.Message}");
            }

            // 在主线程显示对话框
            await Current.Dispatcher.InvokeAsync(() =>
            {
                var mainWindow = Current.MainWindow as MainWindow;
                Controls.UpdateCompleteDialog.ShowDialog(mainWindow, versionString, updateLog);
            });

            Log.Info($"[App] 更新完成对话框已显示，当前版本: {versionString}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[App] 显示更新完成对话框失败");
        }
    }

    /// <summary>
    /// 获取默认更新日志（当无法从服务器获取时使用）
    /// </summary>
    private static string GetDefaultUpdateLog()
    {
                return @"v1.1.8.6 Release 更新内容

汉化与社区数据：

    新增汉化 Mod 指引支持
    更新社区数据模板，提升新数据兼容性

Tag 与依赖识别优化：

    新增 Tag 管理的 Tag 列表能力
    前置 Mod 获取更准确，原有算法独立为“相关 Mod”

下载与页面展示优化：

    优化 SMAPI 下载页面显示效果与加载速度
    优化整合包与 Mod 列表显示策略，减少错页问题
    下载页按钮文案统一为“重新打开浏览器”
    任务状态页复用原有“重新打开浏览器”入口，SMAPI 待下载任务也可直接重开
    统一 Placeholder/SMAPI/NexusBrowser 任务的浏览器地址识别逻辑

下载引擎与设置能力：

    多线程下载功能落地
    设置页支持调整线程数与接管下载

修复项：

    修复下载区域一处字体不统一问题
    修复浏览器下载引导弹窗显示异常
    修复任务管理器红点不消失问题

SMAPI 展示体验改进：

    SMAPI 来源卡片增加本地图标兜底（/Images/Modded.png）
    GitHub 来源恢复可显示 Card 图
    添加 SMAPI 任务“重新打开浏览器”按钮 Card";
    }
}
