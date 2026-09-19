using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace SVL.Avalonia.Services;

/// <summary>
/// 管理启动器的系统托盘图标及其菜单。
/// 
/// 托盘是平台能力，不支持托盘的后端（例如无桌面会话的测试环境）应当安全回退，
/// 不能阻止主窗口启动。因此初始化失败只返回 false，不向上抛出异常。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Action _showMainWindow;
    private readonly Action _exitApplication;
    private TrayIcons? _trayIcons;
    private TrayIcon? _trayIcon;
    private NativeMenu? _menu;
    private bool _disposed;

    public TrayIconService(Action showMainWindow, Action exitApplication)
    {
        _showMainWindow = showMainWindow ?? throw new ArgumentNullException(nameof(showMainWindow));
        _exitApplication = exitApplication ?? throw new ArgumentNullException(nameof(exitApplication));
    }

    /// <summary>当前平台是否已经成功注册托盘图标。</summary>
    public bool IsAvailable => _trayIcon is not null && !_disposed;

    /// <summary>
    /// 尝试注册托盘图标。调用可重复，成功后不会重复创建图标。
    /// </summary>
    public bool TryInitialize()
    {
        if (IsAvailable)
        {
            return true;
        }

        if (_disposed || Application.Current is null)
        {
            return false;
        }

        try
        {
            var iconStream = AssetLoader.Open(new Uri("avares://SVL.Avalonia/Assets/Icons/icon.png"));
            var windowIcon = new WindowIcon(iconStream);

            var showItem = new NativeMenuItem("显示主窗口");
            showItem.Click += OnShowMainWindowClicked;

            var exitItem = new NativeMenuItem("退出");
            exitItem.Click += OnExitApplicationClicked;

            _menu = new NativeMenu();
            _menu.Items.Add(showItem);
            _menu.Items.Add(exitItem);

            _trayIcon = new TrayIcon
            {
                Icon = windowIcon,
                Menu = _menu,
                ToolTipText = "SVL 启动器"
            };
            _trayIcon.Clicked += OnTrayIconClicked;

            _trayIcons = new TrayIcons { _trayIcon };
            TrayIcon.SetIcons(Application.Current, _trayIcons);
            return true;
        }
        catch (Exception ex)
        {
            // 没有桌面托盘实现时（例如 CI/headless）由主窗口正常显示兜底。
            System.Diagnostics.Debug.WriteLine($"[Tray] 注册系统托盘失败，回退为窗口模式: {ex.Message}");
            DisposeResources();
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeResources();
        GC.SuppressFinalize(this);
    }

    private void OnTrayIconClicked(object? sender, EventArgs e)
    {
        _showMainWindow();
    }

    private void OnShowMainWindowClicked(object? sender, EventArgs e)
    {
        _showMainWindow();
    }

    private void OnExitApplicationClicked(object? sender, EventArgs e)
    {
        _exitApplication();
    }

    private void DisposeResources()
    {
        if (Application.Current is { } application)
        {
            try
            {
                // 先替换 Application 上的集合，避免已销毁的图标继续被平台访问。
                TrayIcon.SetIcons(application, new TrayIcons());
            }
            catch
            {
                // 某些平台在退出阶段已经释放了托盘后端；下面仍继续清理托管对象。
            }
        }

        if (_trayIcon is not null)
        {
            _trayIcon.Clicked -= OnTrayIconClicked;
            try
            {
                _trayIcon.Dispose();
            }
            catch
            {
                // best-effort cleanup
            }
        }

        _trayIcons?.Clear();
        _trayIcon = null;
        _trayIcons = null;
        _menu = null;
    }
}
