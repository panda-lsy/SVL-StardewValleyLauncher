using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using SVL.Avalonia.Services;
using SVL.Avalonia.ViewModels;
using SVL.Core.Platform.Modpack;
using System.Linq;

namespace SVL.Avalonia;

public partial class MainWindow : Window
{
    /// <summary>本窗口关联的浮窗通知服务实例。静态门面 NotificationService.Show 委托到此实例。</summary>
    public NotificationService Notifications { get; }

    public MainWindow()
    {
        InitializeComponent();

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
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel vm)
        {
            vm.BringToFrontRequested += OnBringToFrontRequested;
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

    /// <summary>拖放释放时：取首个受支持的整合包文件路径交给 ViewModel 处理。</summary>
    private async void MainWindow_Drop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        var files = e.Data.GetFiles();
        if (files == null)
        {
            return;
        }

        string? modpackPath = null;
        foreach (var file in files)
        {
            var path = file.Path.IsAbsoluteUri
                ? Uri.UnescapeDataString(file.Path.LocalPath)
                : file.Path.ToString();
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
