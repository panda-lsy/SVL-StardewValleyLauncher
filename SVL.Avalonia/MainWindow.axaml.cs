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

    private static readonly (string Key, byte TransparentAlpha)[] MainWindowSurfaceBrushes =
    [
        ("WindowBackgroundBrush", 0x80),
        ("HeaderBackgroundBrush", 0xB3),
        ("PanelBackgroundBrush", 0x66),
        ("SurfaceBrush", 0xCC),
        ("CardBrush", 0xBF)
    ];

    /// <summary>本窗口关联的浮窗通知服务实例。静态门面 NotificationService.Show 委托到此实例。</summary>
    public NotificationService Notifications { get; }

    public MainWindow()
    {
        InitializeComponent();
        RefreshMainWindowThemeResources(ThemeService.TransparencyEnabled);
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
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TopLevel.ActualTransparencyLevelProperty)
        {
            if (_hasBeenOpened)
            {
                // ActualTransparencyLevel 可能在窗口打开后的首个属性变更中暂时为 None，
                // 不应因此把用户已经开启的半透明画刷切回不透明；后端不支持透明时，
                // TransparencyBackgroundFallback 会负责提供不透明回退色。
                RefreshMainWindowThemeResources(ThemeService.TransparencyEnabled);
            }

            LogWindowTransparencyState("平台透明级别已更新");
        }
    }

    private void OnWindowOpened(object? sender, System.EventArgs e)
    {
        _hasBeenOpened = true;
        // 不以 ActualTransparencyLevel 作为画刷开关。部分平台/窗口后端在 Opened
        // 事件时尚未报告最终透明级别，提前切回不透明会让主页面看起来没有透明效果。
        RefreshMainWindowThemeResources(ThemeService.TransparencyEnabled);
        LogWindowTransparencyState("主窗口已打开");
    }

    private void OnThemeChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            RefreshMainWindowThemeResources(ThemeService.TransparencyEnabled);
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            RefreshMainWindowThemeResources(ThemeService.TransparencyEnabled);
        });
    }

    private void OnWindowClosed(object? sender, System.EventArgs e)
    {
        ThemeService.ThemeChanged -= OnThemeChanged;
        Closed -= OnWindowClosed;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel vm)
        {
            vm.BringToFrontRequested += OnBringToFrontRequested;
            vm.LaunchPage.GameStartedRequested += OnGameStartedRequested;
            vm.LaunchPage.GameExitedRequested += OnGameExitedRequested;
            vm.SettingsPage.PropertyChanged += OnSettingsPropertyChanged;
            ApplyWindowTransparency(vm.SettingsPage.EnableTransparency);
        }
    }

    private void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(SettingsPageViewModel.EnableTransparency), StringComparison.Ordinal) &&
            sender is SettingsPageViewModel settings)
        {
            ApplyWindowTransparency(settings.EnableTransparency);
        }
    }

    private void ApplyWindowTransparency(bool enabled)
    {
        TransparencyLevelHint = enabled
            ? new[] { WindowTransparencyLevel.Transparent, WindowTransparencyLevel.AcrylicBlur }
            : new[] { WindowTransparencyLevel.None };

        // 透明设置决定主题画刷是否半透明；平台能力只决定窗口最终是否能透出桌面，
        // 不应让瞬时的 ActualTransparencyLevel 覆盖用户设置。
        RefreshMainWindowThemeResources(enabled);

        LogWindowTransparencyState(enabled ? "设置已开启" : "设置已关闭");
    }

    private void RefreshMainWindowThemeResources(bool enabled)
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
                    enabled ? transparentAlpha : (byte)0xFF,
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
                Close();
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
                Close();
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

    /// <summary>拖放释放时：优先识别 Mod，其次把整合包交给统一导入流程。</summary>
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
