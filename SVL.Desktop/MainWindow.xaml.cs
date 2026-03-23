using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using SVL.Core.Config;
using SVL.Core.Modpack;
using SVL.Core.Stardew.Mod;
using SVL.Desktop.Controls;
using SVL.Desktop.ViewModels;
using SVL.Desktop.Views;

namespace SVL.Desktop;

public partial class MainWindow : Window
{
    private MainWindowViewModel _viewModel;
    private double _restoreLeft;
    private double _restoreTop;
    private double _restoreWidth;
    private double _restoreHeight;
    private DownloadManagerWindow? _downloadWindow;
    private bool _isFloatingButtonPointerDown;
    private bool _isDraggingFloatingButton;
    private bool _didDragFloatingButton;
    private Point _floatingDragStartPoint;
    private Point _floatingDragStartOffset;
    private const double FloatingButtonPadding = 24;
    private const double FloatingButtonDragThreshold = 3;

    // Windows API 用于拖放支持
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void DragAcceptFiles(IntPtr hWnd, bool fAccept);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, [Out] char[] lpszFile, uint cch);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void DragFinish(IntPtr hDrop);

    // 用于允许拖放消息通过 UIPI (User Interface Privilege Isolation)
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint msg, uint action, ref CHANGEFILTERSTRUCT changeInfo);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool ChangeWindowMessageFilter(uint msg, uint dwFlag);

    // 用于撤销 WPF 自动注册的 OLE 拖放目标（防止 OLE 优先拦截导致 UIPI 下拖放失败）
    [DllImport("ole32.dll")]
    private static extern int RevokeDragDrop(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct CHANGEFILTERSTRUCT
    {
        public uint cbSize;
        public uint ExtStatus;
    }

    private const int WM_DROPFILES = 0x233;
    private const int WM_COPYDATA = 0x004A;
    private const int WM_COPYGLOBALDATA = 0x0049;
    private const uint MSGFLT_ALLOW = 1;
    private const uint MSGFLT_ADD = 1;
    private HwndSource? _hwndSource;

    public MainWindow(MainWindowViewModel viewModel)
    {
        SVL.Core.Logging.Log.Info("[MainWindow] Constructor started");
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        this.MouseLeftButtonDown += MainWindow_MouseLeftButtonDown;
        this.Loaded += MainWindow_Loaded;
        this.Closing += MainWindow_Closing;
        this.SourceInitialized += MainWindow_SourceInitialized;
        this.SizeChanged += MainWindow_SizeChanged;

        // 订阅启动器配置更新事件
        LauncherConfigService.LauncherAppNameChanged += OnLauncherAppNameChanged;
        LauncherConfigService.LauncherTitleChanged += OnLauncherTitleChanged;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;

        SVL.Core.Logging.Log.Info("[MainWindow] Constructor completed");
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        // 在窗口句柄创建后立即启用 Windows API 拖放（绕过 WindowChrome 的限制）
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            // 允许拖放消息通过 UIPI
            var filterStruct = new CHANGEFILTERSTRUCT { cbSize = (uint)Marshal.SizeOf(typeof(CHANGEFILTERSTRUCT)) };

            // 尝试使用 ChangeWindowMessageFilterEx (Windows Vista+)
            try
            {
                ChangeWindowMessageFilterEx(hwnd, WM_DROPFILES, MSGFLT_ALLOW, ref filterStruct);
                ChangeWindowMessageFilterEx(hwnd, WM_COPYDATA, MSGFLT_ALLOW, ref filterStruct);
                ChangeWindowMessageFilterEx(hwnd, WM_COPYGLOBALDATA, MSGFLT_ALLOW, ref filterStruct);
            }
            catch
            {
                // 如果 ChangeWindowMessageFilterEx 失败，尝试使用旧版 API
                try
                {
                    ChangeWindowMessageFilter(WM_DROPFILES, MSGFLT_ADD);
                    ChangeWindowMessageFilter(WM_COPYDATA, MSGFLT_ADD);
                    ChangeWindowMessageFilter(WM_COPYGLOBALDATA, MSGFLT_ADD);
                }
                catch { }
            }

            // 撤销 WPF 自动注册的 OLE IDropTarget
            // 原因：应用以管理员权限运行（requireAdministrator），OLE 拖放受 UIPI 限制，
            // 非提升权限的 Explorer 拖放文件到提升权限窗口时 OLE 会被阻止，
            // 但 OLE 目标优先级高于 WM_DROPFILES，导致两个机制都无法工作。
            // 必须先撤销 OLE 再启用原生 WM_DROPFILES 机制。
            RevokeDragDrop(hwnd);

            DragAcceptFiles(hwnd, true);
            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(WndProc);
            SVL.Core.Logging.Log.Info("[MainWindow] Windows API 拖放已启用 (OLE 已撤销, 使用原生 WM_DROPFILES)");
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        SVL.Core.Logging.Log.Info("[MainWindow] Loaded event fired");

        // 加载启动器应用名称配置
        LoadLauncherAppName();

        // 确保窗口在前台显示
        this.Activate();
        this.Topmost = true;
        this.Topmost = false;
        this.Focus();

        // 初始化下载窗口
        _downloadWindow = new DownloadManagerWindow();
        _downloadWindow.Owner = this;

        // 设置 DownloadFloatingButton 的 DataContext 为 DownloadManagerViewModel
        // 这样按钮可以绑定到 ActiveTaskCount 属性
        if (DownloadFloatingButton != null)
        {
            DownloadFloatingButton.DataContext = DownloadManagerViewModel.Instance;
            ClampFloatingButtonOffset();
            SVL.Core.Logging.Log.Info("[MainWindow] DownloadFloatingButton DataContext set to DownloadManagerViewModel");
        }
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ClampFloatingButtonOffset();
    }

    private void DownloadFloatingButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var transform = GetFloatingButtonTransform();
        if (DownloadFloatingButton == null || transform == null)
            return;

        if (e.OriginalSource is DependencyObject source)
        {
            var button = FindParent<Button>(source);
            if (button != null && string.Equals(button.Name, "CloseButton", StringComparison.Ordinal))
            {
                return;
            }
        }

        _isFloatingButtonPointerDown = true;
        _isDraggingFloatingButton = false;
        _didDragFloatingButton = false;
        _floatingDragStartPoint = e.GetPosition(this);
        _floatingDragStartOffset = new Point(transform.X, transform.Y);
    }

    private void DownloadFloatingButton_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        var transform = GetFloatingButtonTransform();
        if ((!_isFloatingButtonPointerDown && !_isDraggingFloatingButton) || transform == null)
            return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            ReleaseFloatingButtonDrag();
            return;
        }

        var currentPoint = e.GetPosition(this);
        var deltaX = currentPoint.X - _floatingDragStartPoint.X;
        var deltaY = currentPoint.Y - _floatingDragStartPoint.Y;

        if (!_didDragFloatingButton &&
            (Math.Abs(deltaX) > FloatingButtonDragThreshold || Math.Abs(deltaY) > FloatingButtonDragThreshold))
        {
            _didDragFloatingButton = true;
            _isDraggingFloatingButton = true;
            if (DownloadFloatingButton?.IsMouseCaptured != true)
            {
                DownloadFloatingButton?.CaptureMouse();
            }
        }

        if (!_isDraggingFloatingButton)
            return;

        transform.X = _floatingDragStartOffset.X + deltaX;
        transform.Y = _floatingDragStartOffset.Y + deltaY;
        ClampFloatingButtonOffset();
    }

    private void DownloadFloatingButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_didDragFloatingButton && DownloadFloatingButton is DownloadFloatingButton floatingButton)
        {
            floatingButton.SuppressNextClick = true;
        }

        ReleaseFloatingButtonDrag();
    }

    private void ReleaseFloatingButtonDrag()
    {
        _isFloatingButtonPointerDown = false;

        _isDraggingFloatingButton = false;
        if (DownloadFloatingButton?.IsMouseCaptured == true)
        {
            DownloadFloatingButton.ReleaseMouseCapture();
        }
    }

    private void ClampFloatingButtonOffset()
    {
        var transform = GetFloatingButtonTransform();
        if (DownloadFloatingButton == null || transform == null)
            return;

        var parent = DownloadFloatingButton.Parent as FrameworkElement;
        if (parent == null)
            return;

        var containerWidth = parent.ActualWidth;
        var containerHeight = parent.ActualHeight;
        var buttonWidth = DownloadFloatingButton.ActualWidth > 0 ? DownloadFloatingButton.ActualWidth : DownloadFloatingButton.Width;
        var buttonHeight = DownloadFloatingButton.ActualHeight > 0 ? DownloadFloatingButton.ActualHeight : DownloadFloatingButton.Height;

        if (containerWidth <= 0 || containerHeight <= 0 || buttonWidth <= 0 || buttonHeight <= 0)
            return;

        var minX = -Math.Max(0, containerWidth - buttonWidth - (FloatingButtonPadding * 2));
        var minY = -Math.Max(0, containerHeight - buttonHeight - (FloatingButtonPadding * 2));

        transform.X = Clamp(transform.X, minX, 0);
        transform.Y = Clamp(transform.Y, minY, 0);
    }

    private TranslateTransform? GetFloatingButtonTransform()
    {
        return DownloadFloatingButton?.RenderTransform as TranslateTransform;
    }

    private static double Clamp(double value, double min, double max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    /// <summary>
    /// 处理 Windows 消息（用于拖放）
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DROPFILES)
        {
            handled = true;
            HandleDropFiles(wParam);
            return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// 处理拖放的文件
    /// </summary>
    private void HandleDropFiles(IntPtr hDrop)
    {
        try
        {
            // 获取文件数量
            uint fileCount = DragQueryFile(hDrop, 0xFFFFFFFF, null!, 0);
            SVL.Core.Logging.Log.Info($"[MainWindow] 拖放了 {fileCount} 个文件");

            var files = new System.Collections.Generic.List<string>();
            for (uint i = 0; i < fileCount; i++)
            {
                // 获取文件路径长度
                uint pathLength = DragQueryFile(hDrop, i, null!, 0);
                if (pathLength > 0)
                {
                    var buffer = new char[pathLength + 1];
                    DragQueryFile(hDrop, i, buffer, pathLength + 1);
                    var filePath = new string(buffer, 0, (int)pathLength);
                    files.Add(filePath);
                }
            }

            // 完成拖放
            DragFinish(hDrop);

            SVL.Core.Logging.Log.Info($"[MainWindow] 拖放的文件: {string.Join(", ", files)}");

            var smapiFiles = files.Where(ModArchiveDetector.LooksLikeSmapiInstallerSource).ToList();
            var modFiles = files.Where(ModArchiveDetector.LooksLikeModInstallSource).ToList();
            var supportedFiles = files
                .Where(f =>
                    !smapiFiles.Contains(f, StringComparer.OrdinalIgnoreCase)
                    && !modFiles.Contains(f, StringComparer.OrdinalIgnoreCase)
                    && ModpackTypeDetector.IsSupportedFile(f)
                    && ModpackTypeDetector.Detect(f).Type != ModpackType.Unknown)
                .ToList();

            if (smapiFiles.Count > 0)
            {
                SVL.Core.Logging.Log.Info($"[MainWindow] 识别到 {smapiFiles.Count} 个 SMAPI 安装包");
                Dispatcher.InvokeAsync(async () => await _viewModel.CreateSmapiInstanceFromLocalZipAsync(smapiFiles.First()));
                return;
            }

            if (modFiles.Count > 0)
            {
                SVL.Core.Logging.Log.Info($"[MainWindow] 识别到 {modFiles.Count} 个 Mod 安装源");
                Dispatcher.InvokeAsync(async () => await _viewModel.HandleModInstallDropAsync(modFiles));
                return;
            }

            if (supportedFiles.Count == 0)
            {
                Dispatcher.Invoke(() =>
                {
                    Controls.FloatingNotificationControl.Show(
                        title: "不支持的文件格式",
                        message: "请拖放包含 manifest.json 的 Mod 压缩包，或 .zip、.7z、.cfmodpack 格式的整合包文件",
                        autoCloseDelay: 5000,
                        notificationType: Controls.NotificationType.Warning);
                });
                return;
            }

            // 处理第一个支持的文件
            var supportedFilePath = supportedFiles.First();
            SVL.Core.Logging.Log.Info($"[MainWindow] 处理拖放文件: {supportedFilePath}");

            // 显示导入对话框
            Dispatcher.Invoke(() => _viewModel.HandleModpackDrop(supportedFilePath));
        }
        catch (Exception ex)
        {
            SVL.Core.Logging.Log.Error(ex, "[MainWindow] 处理拖放文件失败");
        }
    }

    /// <summary>
    /// 加载启动器应用名称配置
    /// </summary>
    private void LoadLauncherAppName()
    {
        try
        {
            var settings = SVL.Core.Config.AppConfig.GetSettings();
            if (!string.IsNullOrEmpty(settings.LauncherAppName))
            {
                LauncherAppNameText.Text = settings.LauncherAppName;
                SVL.Core.Logging.Log.Info($"[MainWindow] Launcher app name set to: {settings.LauncherAppName}");
            }
        }
        catch (Exception ex)
        {
            SVL.Core.Logging.Log.Error(ex, "[MainWindow] Failed to load launcher app name");
        }
    }

    /// <summary>
    /// 更新启动器应用名称（热重载）
    /// </summary>
    public void UpdateLauncherAppName(string appName)
    {
        try
        {
            if (!string.IsNullOrEmpty(appName))
            {
                LauncherAppNameText.Text = appName;
                SVL.Core.Logging.Log.Info($"[MainWindow] Launcher app name updated to: {appName}");
            }
        }
        catch (Exception ex)
        {
            SVL.Core.Logging.Log.Error(ex, "[MainWindow] Failed to update launcher app name");
        }
    }

    /// <summary>
    /// 更新窗口标题（热重载）
    /// </summary>
    public void UpdateWindowTitle(string title)
    {
        try
        {
            if (!string.IsNullOrEmpty(title))
            {
                this.Title = title;
                SVL.Core.Logging.Log.Info($"[MainWindow] Window title updated to: {title}");
            }
        }
        catch (Exception ex)
        {
            SVL.Core.Logging.Log.Error(ex, "[MainWindow] Failed to update window title");
        }
    }

    /// <summary>
    /// 处理启动器应用名称更新事件
    /// </summary>
    private void OnLauncherAppNameChanged(string appName)
    {
        UpdateLauncherAppName(appName);
    }

    /// <summary>
    /// 处理启动器标题更新事件
    /// </summary>
    private void OnLauncherTitleChanged(string title)
    {
        UpdateWindowTitle(title);
    }

    /// <summary>
    /// 显示下载管理器窗口
    /// </summary>
    public void ShowDownloadManager()
    {
        if (_downloadWindow != null)
        {
            _downloadWindow.ShowWindow();
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SVL.Core.Logging.Log.Info("[MainWindow] Closing event fired");
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.CurrentPage) ||
            e.PropertyName == nameof(MainWindowViewModel.CurrentDownloadSubPage) ||
            e.PropertyName == nameof(MainWindowViewModel.RightPanelContent))
        {
            RestoreModsSearchScrollOffset();
        }
    }

    private void RightContentScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_viewModel.CurrentPage == PageType.Download &&
            _viewModel.CurrentDownloadSubPage == DownloadSubPageType.Mods)
        {
            _viewModel.DownloadModsScrollOffset = e.VerticalOffset;
        }
    }

    private void RestoreModsSearchScrollOffset()
    {
        if (_viewModel.CurrentPage != PageType.Download ||
            _viewModel.CurrentDownloadSubPage != DownloadSubPageType.Mods ||
            RightContentScrollViewer == null)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_viewModel.CurrentPage == PageType.Download &&
                _viewModel.CurrentDownloadSubPage == DownloadSubPageType.Mods)
            {
                RightContentScrollViewer.ScrollToVerticalOffset(_viewModel.DownloadModsScrollOffset);
            }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void MainWindow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 如果点击的是标题栏区域，允许拖动（但不处理双击）
        if (e.ClickCount == 2) return; // 双击事件由 TitleBar_MouseLeftButtonDown 处理

        if (e.OriginalSource is FrameworkElement element)
        {
            // 检查是否在标题栏内（高度48）
            var position = e.GetPosition(this);
            if (position.Y <= 48 && e.LeftButton == MouseButtonState.Pressed)
            {
                // 确保不是在按钮或其他可点击元素上
                if (FindParent<Button>(e.OriginalSource as DependencyObject) == null)
                {
                    this.DragMove();
                }
            }
        }
    }

    // 辅助方法：查找父级元素
    private T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        if (child == null) return null;
        var parent = VisualTreeHelper.GetParent(child);
        if (parent == null) return null;
        if (parent is T result) return result;
        return FindParent<T>(parent);
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("[MainWindow] MinimizeButton_Click called");
        this.WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"[MainWindow] MaximizeButton_Click called, Current State: {this.WindowState}");

        if (this.WindowState == WindowState.Maximized)
        {
            // 还原窗口
            this.WindowState = WindowState.Normal;
            this.Left = _restoreLeft;
            this.Top = _restoreTop;
            this.Width = _restoreWidth;
            this.Height = _restoreHeight;
        }
        else
        {
            // 保存当前窗口状态
            _restoreLeft = this.Left;
            _restoreTop = this.Top;
            _restoreWidth = this.Width;
            _restoreHeight = this.Height;

            // 最大化窗口
            this.WindowState = WindowState.Maximized;
        }

        UpdateMaximizeButtonIcon();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 双击标题栏切换最大化/还原
        if (e.ClickCount == 2)
        {
            MaximizeButton_Click(sender, e);
            e.Handled = true;
        }
    }

    private void UpdateMaximizeButtonIcon()
    {
        // 根据窗口状态更新最大化按钮图标
        if (MaximizeButton.Template.FindName("MaximizePath", MaximizeButton) is System.Windows.Shapes.Path maximizePath &&
            MaximizeButton.Template.FindName("RestorePath", MaximizeButton) is System.Windows.Shapes.Path restorePath)
        {
            if (this.WindowState == WindowState.Maximized)
            {
                maximizePath.Visibility = Visibility.Collapsed;
                restorePath.Visibility = Visibility.Visible;
                MaximizeButton.ToolTip = "还原";
            }
            else
            {
                maximizePath.Visibility = Visibility.Visible;
                restorePath.Visibility = Visibility.Collapsed;
                MaximizeButton.ToolTip = "最大化";
            }
        }
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        UpdateMaximizeButtonIcon();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("[MainWindow] CloseButton_Click called");
        Application.Current.Shutdown();
    }

    private void ResizeTop_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (this.WindowState == WindowState.Maximized) return;
        this.Height -= e.VerticalChange;
    }

    private void ResizeBottom_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (this.WindowState == WindowState.Maximized) return;
        this.Height += e.VerticalChange;
    }

    private void ResizeLeft_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (this.WindowState == WindowState.Maximized) return;
        this.Left += e.HorizontalChange;
        this.Width -= e.HorizontalChange;
    }

    private void ResizeRight_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (this.WindowState == WindowState.Maximized) return;
        this.Width += e.HorizontalChange;
    }

    private void ResizeTopLeft_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (this.WindowState == WindowState.Maximized) return;
        this.Top += e.VerticalChange;
        this.Left += e.HorizontalChange;
        this.Height -= e.VerticalChange;
        this.Width -= e.HorizontalChange;
    }

    private void ResizeTopRight_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (this.WindowState == WindowState.Maximized) return;
        this.Top += e.VerticalChange;
        this.Height -= e.VerticalChange;
        this.Width += e.HorizontalChange;
    }

    private void ResizeBottomLeft_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (this.WindowState == WindowState.Maximized) return;
        this.Left += e.HorizontalChange;
        this.Height += e.VerticalChange;
        this.Width -= e.HorizontalChange;
    }

    private void ResizeBottomRight_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (this.WindowState == WindowState.Maximized) return;
        this.Height += e.VerticalChange;
        this.Width += e.HorizontalChange;
    }

    /// <summary>
    /// 应用模糊效果
    /// </summary>
    public void ApplyBlurEffect()
    {
        var blurEffect = new System.Windows.Media.Effects.BlurEffect
        {
            Radius = 10,
            KernelType = System.Windows.Media.Effects.KernelType.Gaussian
        };

        this.Effect = blurEffect;
        this.IsHitTestVisible = false;
    }

    /// <summary>
    /// 移除模糊效果
    /// </summary>
    public void RemoveBlurEffect()
    {
        this.Effect = null;
        this.IsHitTestVisible = true;
    }

    #region 拖放支持

    // 注意：拖放功能完全由原生 WM_DROPFILES 机制处理（见 WndProc 和 HandleDropFiles）
    // WPF 的 OLE DragDrop (PreviewDragEnter/PreviewDragOver/PreviewDrop) 已移除，
    // 因为应用以管理员权限运行时，UIPI 会阻止 OLE 拖放，而 OLE 注册会抢占 WM_DROPFILES。

    #endregion
}
