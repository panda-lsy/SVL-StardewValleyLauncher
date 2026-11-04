using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Button = Avalonia.Controls.Button;

namespace SVL.Android;

public sealed partial class MobileMainView : UserControl
{
    private readonly CinderboxBridge _bridge;
    private TextBlock RuntimeStatusControl => this.FindControl<TextBlock>("RuntimeStatus")!;
    private TextBlock ActionStatusControl => this.FindControl<TextBlock>("ActionStatus")!;

    public MobileMainView() : this(new CinderboxBridge()) { }

    internal MobileMainView(CinderboxBridge bridge)
    {
        _bridge = bridge;
        AvaloniaXamlLoader.Load(this);
        this.FindControl<Button>("OpenRunnerButton")!.Click += OpenRunner;
        this.FindControl<Button>("ReleaseButton")!.Click += OpenRelease;
        this.FindControl<Button>("GuideButton")!.Click += OpenGuide;
        RefreshRuntimeStatus();
    }

    internal void RefreshRuntimeStatus()
    {
        var available = _bridge.IsAvailable();
        RuntimeStatusControl.Text = available ? "已检测到 Cinderbox，可以打开运行器。" : "未检测到 Cinderbox，请先从其官方发布页下载安装。";
        this.FindControl<Button>("OpenRunnerButton")!.IsEnabled = available;
    }

    private void OpenRunner(object? sender, RoutedEventArgs args)
    {
        if (_bridge.TryOpenRunner())
        {
            ActionStatusControl.Text = string.Empty;
        }
        else
        {
            ActionStatusControl.Text = "无法打开运行器，请确认已安装且未被系统停用。";
            RefreshRuntimeStatus();
        }
    }

    private void OpenRelease(object? sender, RoutedEventArgs args)
    {
        ActionStatusControl.Text = _bridge.TryOpenReleasePage() ? string.Empty : "无法打开浏览器，请检查设备上是否安装浏览器。";
    }

    private void OpenGuide(object? sender, RoutedEventArgs args)
    {
        ActionStatusControl.Text = _bridge.TryOpenGuide() ? string.Empty : "无法打开浏览器，请检查设备上是否安装浏览器。";
    }
}
