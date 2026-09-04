using Avalonia;
using Avalonia.Controls;

namespace SVL.Avalonia.Views;

public partial class SplashScreen : UserControl
{
    public static readonly StyledProperty<string> AppNameProperty =
        AvaloniaProperty.Register<SplashScreen, string>(nameof(AppName), "Stardew Valley Launcher");

    public static readonly StyledProperty<string> StatusTextProperty =
        AvaloniaProperty.Register<SplashScreen, string>(nameof(StatusText), "正在初始化...");

    public static readonly StyledProperty<double> ProgressPercentProperty =
        AvaloniaProperty.Register<SplashScreen, double>(nameof(ProgressPercent), 0);

    public string AppName
    {
        get => GetValue(AppNameProperty);
        set => SetValue(AppNameProperty, value);
    }

    public string StatusText
    {
        get => GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public double ProgressPercent
    {
        get => GetValue(ProgressPercentProperty);
        set => SetValue(ProgressPercentProperty, value);
    }

    public SplashScreen()
    {
        InitializeComponent();
    }
}
