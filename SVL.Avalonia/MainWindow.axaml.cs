using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SVL.Avalonia.Models;
using SVL.Avalonia.Services;
using SVL.Avalonia.ViewModels;
using SVL.Core.Platform.Modpack;
using System.Linq;

namespace SVL.Avalonia;

public partial class MainWindow : Window
{
    private bool _hasBeenOpened;
    private bool _allowClose;

    private static readonly (string Key, byte TransparentAlpha)[] MainWindowSurfaceBrushes =
    [
        // 主窗口页面需要能明显透出 Acrylic/桌面；弹窗窗口不读取这组局部资源，
        // 因此降低主页面 Alpha 不会让 Debug/确认弹窗变成半透明。
        ("WindowBackgroundBrush", 0x66),
        ("HeaderBackgroundBrush", 0x99),
        ("PanelBackgroundBrush", 0x33),
        ("SurfaceBrush", 0xA6),
        ("CardBrush", 0xA6)
    ];

    /// <summary>本窗口关联的浮窗通知服务实例。静态门面 NotificationService.Show 委托到此实例。</summary>
    public NotificationService Notifications { get; }

    /// <summary>
    /// 关闭窗口时是否改为隐藏到系统托盘。由 App 注入托盘可用性和用户设置判断。
    /// </summary>
    public Func<bool>? ShouldMinimizeToTrayOnClose { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        UpdateMaximizeRestoreIcon();
        RefreshMainWindowThemeResources(ThemeService.BackgroundTransparencyPercent);
        ThemeService.ThemeChanged += OnThemeChanged;
        Closed += OnWindowClosed;

        if (OperatingSystem.IsWindows())
        {
            // Force custom chrome on Windows to match legacy WPF behavior.
            SystemDecorations = SystemDecorations.None;
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
            ExtendClientAreaTitleBarHeightHint = 48;
        }
        else
        {
            // Keep native title bar on non-Windows platforms.
            SystemDecorations = SystemDecorations.Full;
            ExtendClientAreaToDecorationsHint = false;
            ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.Default;
            ExtendClientAreaTitleBarHeightHint = -1;
        }

        try
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://SVL.Avalonia/Assets/Icons/icon.png")));
        }
        catch
        {
            // If icon loading fails on a platform, keep default icon to avoid startup interruption.
        }

        // 初始化浮窗通知服务：绑定 ItemsSource 并注册为静态门面宿主。
        Notifications = new NotificationService();
        NotificationContainer.ItemsSource = Notifications.ActiveNotifications;
        NotificationService.RegisterHost(Notifications);

        // DataContext 由 App 的对象初始化器在构造函数后设置，故用事件订阅置顶请求。
        DataContextChanged += OnDataContextChanged;
        Opened += OnWindowOpened;
        Closing += OnWindowClosing;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == Window.WindowStateProperty)
        {
            UpdateMaximizeRestoreIcon();
        }

        if (change.Property == TopLevel.ActualTransparencyLevelProperty)
        {
            if (_hasBeenOpened)
            {
                // ActualTransparencyLevel 可能在窗口打开后的首个属性变更中暂时为 None，
                // 不应因此把用户已经开启的半透明画刷切回不透明；后端不支持透明时，
                // TransparencyBackgroundFallback 会负责提供不透明回退色。
                RefreshMainWindowThemeResources(ThemeService.BackgroundTransparencyPercent);
            }

            LogWindowTransparencyState("平台透明级别已更新");
        }
    }

    private void OnWindowOpened(object? sender, System.EventArgs e)
    {
        _hasBeenOpened = true;
        // 不以 ActualTransparencyLevel 作为画刷开关。部分平台/窗口后端在 Opened
        // 事件时尚未报告最终透明级别，提前切回不透明会让主页面看起来没有透明效果。
        RefreshMainWindowThemeResources(ThemeService.BackgroundTransparencyPercent);
        LogWindowTransparencyState("主窗口已打开");
    }

    private void OnThemeChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            RefreshMainWindowThemeResources(ThemeService.BackgroundTransparencyPercent);
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            RefreshMainWindowThemeResources(ThemeService.BackgroundTransparencyPercent);
        });
    }

    private void OnWindowClosed(object? sender, System.EventArgs e)
    {
        ThemeService.ThemeChanged -= OnThemeChanged;
        Closed -= OnWindowClosed;
        Closing -= OnWindowClosing;
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // 程序主动退出、系统关机和应用 Shutdown 不应被“最小化到托盘”拦截。
        if (!_allowClose &&
            e.CloseReason is not (WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown) &&
            ShouldMinimizeToTrayOnClose?.Invoke() == true)
        {
            e.Cancel = true;
            Hide();
            DebugConsoleService.Instance.Append("主窗口已隐藏到系统托盘。", DebugLogLevel.Debug);
            return;
        }

        if (DataContext is MainWindowViewModel vm)
        {
            var instanceSaved = vm.VersionSettingsPage.FlushPendingInstanceSettings();
            var globalSaved = vm.SettingsPage.FlushPendingSettings();
            if (!instanceSaved || !globalSaved)
                DebugConsoleService.Instance.Append("退出时保存设置失败，请检查配置目录权限。", DebugLogLevel.Error);
        }
    }

    /// <summary>绕过托盘设置关闭主窗口，供“退出”、启动游戏和生命周期收尾使用。</summary>
    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel vm)
        {
            vm.BringToFrontRequested += OnBringToFrontRequested;
            vm.LaunchPage.GameStartedRequested += OnGameStartedRequested;
            vm.LaunchPage.GameExitedRequested += OnGameExitedRequested;
            vm.SettingsPage.PropertyChanged += OnSettingsPropertyChanged;
            Notifications.IsEnabled = vm.SettingsPage.ShowNotifications;
            ApplyWindowTransparency((int)Math.Round(vm.SettingsPage.BackgroundTransparencyPercent));
        }
    }

    private void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(SettingsPageViewModel.ShowNotifications), StringComparison.Ordinal) &&
            sender is SettingsPageViewModel notificationSettings)
        {
            Notifications.IsEnabled = notificationSettings.ShowNotifications;
        }

        if (string.Equals(e.PropertyName, nameof(SettingsPageViewModel.BackgroundTransparencyPercent), StringComparison.Ordinal) &&
            sender is SettingsPageViewModel settings)
        {
            ApplyWindowTransparency((int)Math.Round(settings.BackgroundTransparencyPercent));
        }
    }

    private void ApplyWindowTransparency(int transparencyPercent)
    {
        transparencyPercent = Math.Clamp(transparencyPercent, 0, 100);
        TransparencyLevelHint = transparencyPercent > 0
            // AcrylicBlur 必须优先于 Transparent：Windows 会按顺序选择第一个可用级别，
            // 否则 Transparent 一旦可用，后面的 AcrylicBlur 永远不会生效，主页面看起来
            // 只有普通半透明铺层而没有 Debug 页面那样明显的桌面材质效果。
            ? new[] { WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Transparent }
            : new[] { WindowTransparencyLevel.None };

        // 透明设置决定主题画刷是否半透明；平台能力只决定窗口最终是否能透出桌面，
        // 不应让瞬时的 ActualTransparencyLevel 覆盖用户设置。
        RefreshMainWindowThemeResources(transparencyPercent);

        LogWindowTransparencyState($"背景透明度 {transparencyPercent}%");
    }

    private void RefreshMainWindowThemeResources(int transparencyPercent)
    {
        var appResources = Application.Current?.Resources;
        if (appResources is null)
        {
            return;
        }

        foreach (var (key, transparentAlpha) in MainWindowSurfaceBrushes)
        {
            if (appResources.TryGetValue(key, out var value) && value is ISolidColorBrush source)
            {
                var color = source.Color;
                Resources[key] = new SolidColorBrush(Color.FromArgb(
                    ThemeService.GetSurfaceAlphaForTransparency(transparentAlpha, transparencyPercent),
                    color.R,
                    color.G,
                    color.B));
            }
        }

        if (appResources.TryGetValue("WindowTransparencyFallbackBrush", out var fallbackValue) &&
            fallbackValue is ISolidColorBrush fallback)
        {
            var color = fallback.Color;
            Resources["WindowTransparencyFallbackBrush"] = new SolidColorBrush(
                Color.FromArgb(0xFF, color.R, color.G, color.B));
        }
    }

    private void LogWindowTransparencyState(string reason)
    {
        var requested = string.Join(", ", TransparencyLevelHint.Select(level => level.ToString()));
        DebugConsoleService.Instance.Append(
            $"[WindowTransparency] {reason}: requested=[{requested}], actual={ActualTransparencyLevel}",
            DebugLogLevel.Debug);
    }

    private void OnGameStartedRequested(LauncherVisibilityBehavior behavior)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnGameStartedRequested(behavior));
            return;
        }

        switch (behavior)
        {
            case LauncherVisibilityBehavior.CloseImmediately:
                CloseForExit();
                break;
            case LauncherVisibilityBehavior.HideAndCloseOnExit:
            case LauncherVisibilityBehavior.HideAndRestoreOnExit:
                if (IsVisible)
                {
                    Hide();
                }
                break;
            case LauncherVisibilityBehavior.Minimize:
                WindowState = WindowState.Minimized;
                break;
            case LauncherVisibilityBehavior.KeepUnchanged:
            default:
                break;
        }
    }

    private void OnGameExitedRequested(LauncherVisibilityBehavior behavior)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnGameExitedRequested(behavior));
            return;
        }

        switch (behavior)
        {
            case LauncherVisibilityBehavior.HideAndCloseOnExit:
                CloseForExit();
                break;
            case LauncherVisibilityBehavior.HideAndRestoreOnExit:
                Show();
                Activate();
                break;
        }
    }

    private void OnBringToFrontRequested()
    {
        // 收到外部 NXM 链接时激活窗口：恢复最小化并置顶。
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
    }

    /// <summary>拖拽悬停时：仅当数据包含文件时允许 Copy 效果，否则拒绝。</summary>
    private void MainWindow_DragOver(object? sender, DragEventArgs e)
    {
        var files = e.Data.GetFiles();
        if (files != null && files.Any())
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    /// <summary>拖放释放时先识别 SMAPI，再区分 Mod 和整合包。</summary>
    private async void MainWindow_Drop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        var files = e.Data.GetFiles();
        if (files == null)
        {
            return;
        }

        var paths = files
            .Select(file => file.Path.IsAbsoluteUri
                ? Uri.UnescapeDataString(file.Path.LocalPath)
                : file.Path.ToString())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 只读取 ZIP 目录，不改写用户文件；双层 ZIP 的检测可能耗时，应放到后台。
        var smapiPath = await Task.Run(() => paths.FirstOrDefault(SmapiArchiveDetector.IsSmapiInstaller));
        if (!string.IsNullOrWhiteSpace(smapiPath) && DataContext is MainWindowViewModel smapiVm)
        {
            await smapiVm.HandleSmapiInstallDropAsync(smapiPath);
            return;
        }

        var modPaths = paths
            .Where(path => ModArchiveDetector.LooksLikeModInstallSource(path) &&
                           !IsRecognizedModpack(path))
            .ToList();
        if (DataContext is MainWindowViewModel modVm && modPaths.Count > 0)
        {
            await modVm.HandleModInstallDropAsync(modPaths);
            return;
        }

        string? modpackPath = null;
        foreach (var path in paths)
        {
            if (ModpackTypeDetector.IsSupportedFile(path))
            {
                modpackPath = path;
                break;
            }
        }

        if (string.IsNullOrEmpty(modpackPath))
        {
            return;
        }

        if (DataContext is MainWindowViewModel vm)
        {
            await vm.HandleModpackDropAsync(modpackPath);
        }
    }

    private static bool IsRecognizedModpack(string path)
    {
        if (!ModpackTypeDetector.IsSupportedFile(path))
        {
            return false;
        }

        var detection = ModpackTypeDetector.Detect(path);
        if (!string.IsNullOrWhiteSpace(detection.TempExtractPath))
        {
            ModpackTypeDetector.CleanupTempDirectory(detection.TempExtractPath);
        }

        return detection.Type != ModpackType.Unknown;
    }

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void UpdateMaximizeRestoreIcon()
    {
        if (MaximizeButtonIcon is null || RestoreButtonIcon is null)
        {
            return;
        }

        var isMaximized = WindowState == WindowState.Maximized;
        MaximizeButtonIcon.IsVisible = !isMaximized;
        RestoreButtonIcon.IsVisible = isMaximized;
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Control sourceControl &&
            (sourceControl is Button || sourceControl.GetVisualAncestors().Any(visual => visual is Button)))
        {
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
