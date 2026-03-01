using System.Windows;

namespace SVL.Desktop.Views;

public partial class SplashScreen : Window
{
    public SplashScreen()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 显示启动画面并在指定时间后自动关闭，然后显示主窗口
    /// </summary>
    /// <param name="mainWindow">主窗口</param>
    /// <param name="durationMs">显示时长（毫秒）</param>
    public void ShowAndClose(Window mainWindow, int durationMs = 2000)
    {
        Show();
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = System.TimeSpan.FromMilliseconds(durationMs)
        };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            Close();
            // 启动画面关闭后显示主窗口
            mainWindow?.Show();
        };
        timer.Start();
    }

    /// <summary>
    /// 显示启动画面并在指定时间后自动关闭（旧方法，保持兼容）
    /// </summary>
    /// <param name="durationMs">显示时长（毫秒）</param>
    public void ShowAndClose(int durationMs = 2000)
    {
        ShowAndClose(null, durationMs);
    }
}
