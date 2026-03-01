using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace SVL.Desktop.Controls;

/// <summary>
/// PCL2-CE 风格的图标按钮控件
/// </summary>
public partial class MyIconButton : Border
{
    public MyIconButton()
    {
        InitializeComponent();
        Loaded += (s, e) => InitializeColors();
        MouseEnter += (s, e) => RefreshAnim();
        MouseLeave += (s, e) => { IsMouseDown = false; ResetScale(); RefreshAnim(); };

        // 添加调试：使用普通 MouseLeftButtonUp 事件
        MouseLeftButtonDown += (s, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[MyIconButton] MouseLeftButtonDown - Theme: {Theme}");
            IsMouseDown = true;
            Focus();
            AnimateScale(0.8);
        };

        MouseLeftButtonUp += (s, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[MyIconButton] MouseLeftButtonUp - IsMouseDown: {IsMouseDown}");
            if (IsMouseDown)
            {
                IsMouseDown = false;
                AnimateBounce();
                System.Diagnostics.Debug.WriteLine($"[MyIconButton] Raising ClickEvent");
                RaiseEvent(new RoutedEventArgs(ClickEvent, this));
                e.Handled = true;
                RefreshAnim();
            }
        };
    }

    private void InitializeColors()
    {
        // 初始化图标颜色
        if (PathIcon.Fill == null)
        {
            PathIcon.Fill = Theme switch
            {
                ThemeType.Red => new SolidColorBrush(Color.FromArgb(255, 255, 76, 76)),
                ThemeType.Black => new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                ThemeType.White => new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                _ => new SolidColorBrush(Color.FromArgb(255, 128, 128, 128))
            };
        }

        // 初始化背景为透明
        PanBack.Background = new SolidColorBrush(Color.FromArgb(0, 255, 255, 255));

        RefreshAnim();
    }

    #region Events

    public static readonly RoutedEvent ClickEvent = EventManager.RegisterRoutedEvent(
        nameof(Click),
        RoutingStrategy.Bubble,
        typeof(RoutedEventHandler),
        typeof(MyIconButton));

    public event RoutedEventHandler Click
    {
        add => AddHandler(ClickEvent, value);
        remove => RemoveHandler(ClickEvent, value);
    }

    #endregion

    #region Properties

    // Logo Property (Path Data)
    public static readonly DependencyProperty LogoProperty =
        DependencyProperty.Register(
            nameof(Logo),
            typeof(string),
            typeof(MyIconButton),
            new PropertyMetadata(string.Empty, OnLogoChanged));

    public string Logo
    {
        get => (string)GetValue(LogoProperty);
        set => SetValue(LogoProperty, value);
    }

    private static void OnLogoChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MyIconButton button && e.NewValue is string logo)
        {
            try
            {
                var converter = new GeometryConverter();
                button.PathIcon.Data = converter.ConvertFromString(logo) as Geometry;
            }
            catch
            {
                // Invalid geometry string, ignore
            }
        }
    }

    // LogoScale Property
    public static readonly DependencyProperty LogoScaleProperty =
        DependencyProperty.Register(
            nameof(LogoScale),
            typeof(double),
            typeof(MyIconButton),
            new PropertyMetadata(1.0, OnLogoScaleChanged));

    public double LogoScale
    {
        get => (double)GetValue(LogoScaleProperty);
        set => SetValue(LogoScaleProperty, value);
    }

    private static void OnLogoScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MyIconButton button && button.PathIcon.RenderTransform is ScaleTransform transform)
        {
            transform.ScaleX = button.LogoScale;
            transform.ScaleY = button.LogoScale;
        }
    }

    // Theme Property
    public static readonly DependencyProperty ThemeProperty =
        DependencyProperty.Register(
            nameof(Theme),
            typeof(ThemeType),
            typeof(MyIconButton),
            new PropertyMetadata(ThemeType.Color));

    public ThemeType Theme
    {
        get => (ThemeType)GetValue(ThemeProperty);
        set => SetValue(ThemeProperty, value);
    }

    // CustomForeground Property
    public static readonly DependencyProperty CustomForegroundProperty =
        DependencyProperty.Register(
            nameof(CustomForeground),
            typeof(Brush),
            typeof(MyIconButton),
            new PropertyMetadata(null));

    public Brush? CustomForeground
    {
        get => (Brush?)GetValue(CustomForegroundProperty);
        set => SetValue(CustomForegroundProperty, value);
    }

    #endregion

    #region Enums

    public enum ThemeType
    {
        Color,
        White,
        Black,
        Red,
        Custom
    }

    #endregion

    #region Fields

    private bool IsMouseDown = false;
    private const int AnimationColorIn = 120;
    private const int AnimationColorOut = 150;

    #endregion

    #region Animation

    private void AnimateScale(double targetScale)
    {
        var duration = TimeSpan.FromMilliseconds(400);
        var animation = new DoubleAnimation
        {
            To = targetScale,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        if (PanBack.RenderTransform is ScaleTransform transform)
        {
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }
    }

    private void AnimateBounce()
    {
        var duration1 = TimeSpan.FromMilliseconds(250);
        var duration2 = TimeSpan.FromMilliseconds(250);

        var bounce1 = new DoubleAnimation
        {
            To = 1.05,
            Duration = duration1,
            EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut }
        };

        var bounce2 = new DoubleAnimation
        {
            To = -0.05,
            Duration = duration2,
            BeginTime = duration1,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        if (PanBack.RenderTransform is ScaleTransform transform)
        {
            var storyboard = new Storyboard();
            storyboard.Children.Add(bounce1);
            storyboard.Children.Add(bounce2);

            Storyboard.SetTarget(bounce1, transform);
            Storyboard.SetTarget(bounce2, transform);
            Storyboard.SetTargetProperty(bounce1, new PropertyPath("ScaleX"));
            Storyboard.SetTargetProperty(bounce2, new PropertyPath("ScaleX"));

            storyboard.Begin();
        }
    }

    private void ResetScale()
    {
        AnimateScale(1.0);
    }

    #endregion

    #region Color Animation

    public void RefreshAnim()
    {
        if (!IsLoaded) return;

        // Initialize colors if needed
        if (PanBack.Background == null)
        {
            PanBack.Background = new SolidColorBrush(Color.FromArgb(0, 255, 255, 255));
        }

        if (PathIcon.Fill == null && Theme != ThemeType.Custom)
        {
            PathIcon.Fill = Theme switch
            {
                ThemeType.Red => new SolidColorBrush(Color.FromArgb(160, 255, 76, 76)),
                ThemeType.Black => new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)),
                _ => new SolidColorBrush(Color.FromArgb(160, 128, 128, 128))
            };
        }

        if (IsMouseOver)
        {
            AnimateHover();
        }
        else
        {
            AnimateNormal();
        }
    }

    private void AnimateHover()
    {
        var duration = TimeSpan.FromMilliseconds(AnimationColorIn);

        switch (Theme)
        {
            case ThemeType.Color:
                AnimatePathColor("ColorBrush3", duration);
                break;
            case ThemeType.White:
                AnimateBackgroundColor(PanBack, Color.FromArgb(50, 255, 255, 255), duration);
                break;
            case ThemeType.Red:
                AnimatePathColor(Color.FromArgb(255, 255, 76, 76), duration);
                break;
            case ThemeType.Black:
                AnimatePathColor(Color.FromArgb(230, 255, 255, 255), duration);
                break;
            case ThemeType.Custom when CustomForeground != null:
                var customColor = ((SolidColorBrush)CustomForeground).Color;
                AnimatePathColor(Color.FromArgb(255, customColor.R, customColor.G, customColor.B), duration);
                break;
        }
    }

    private void AnimateNormal()
    {
        var duration = TimeSpan.FromMilliseconds(AnimationColorOut);

        switch (Theme)
        {
            case ThemeType.Color:
                AnimatePathColor("ColorBrush4", duration);
                AnimateBackgroundColor(PanBack, Color.FromArgb(0, 255, 255, 255), duration);
                break;
            case ThemeType.White:
                AnimatePathColor("ColorBrushWhite", duration);
                AnimateBackgroundColor(PanBack, Color.FromArgb(0, 255, 255, 255), duration);
                break;
            case ThemeType.Red:
                AnimatePathColor(Color.FromArgb(255, 255, 76, 76), duration);
                AnimateBackgroundColor(PanBack, Color.FromArgb(0, 255, 255, 255), duration);
                break;
            case ThemeType.Black:
                AnimatePathColor(Color.FromArgb(255, 255, 255, 255), duration);
                AnimateBackgroundColor(PanBack, Color.FromArgb(0, 255, 255, 255), duration);
                break;
            case ThemeType.Custom when CustomForeground != null:
                var customColor = ((SolidColorBrush)CustomForeground).Color;
                AnimatePathColor(Color.FromArgb(255, customColor.R, customColor.G, customColor.B), duration);
                AnimateBackgroundColor(PanBack, Color.FromArgb(0, 255, 255, 255), duration);
                break;
        }
    }

    private void AnimatePathColor(string resourceKey, Duration duration)
    {
        if (TryFindResource(resourceKey) is SolidColorBrush brush)
        {
            AnimatePathColor(brush.Color, duration);
        }
    }

    private void AnimatePathColor(Color color, Duration duration)
    {
        if (PathIcon.Fill is SolidColorBrush solidBrush)
        {
            var animation = new ColorAnimation
            {
                To = color,
                Duration = duration,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            solidBrush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
        }
        else
        {
            PathIcon.Fill = new SolidColorBrush(color);
        }
    }

    private void AnimateBackgroundColor(Border target, Color color, Duration duration)
    {
        if (target.Background is SolidColorBrush solidBrush)
        {
            var animation = new ColorAnimation
            {
                To = color,
                Duration = duration,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            solidBrush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
        }
        else
        {
            target.Background = new SolidColorBrush(color);
        }
    }

    #endregion
}
