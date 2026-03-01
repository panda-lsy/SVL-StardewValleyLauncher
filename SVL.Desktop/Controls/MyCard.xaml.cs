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
/// PCL2-CE 风格的卡片容器控件
/// </summary>
public partial class MyCard : Grid
{
    public MyCard()
    {
        InitializeComponent();
        MouseEnter += MyCard_MouseEnter;
        MouseLeave += MyCard_MouseLeave;
        Loaded += MyCard_Loaded;
    }

    #region Properties

    // Title Property
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(
            nameof(Title),
            typeof(string),
            typeof(MyCard),
            new PropertyMetadata(string.Empty, OnTitleChanged));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MyCard card)
        {
            card.TitleTextBlock.Text = e.NewValue as string ?? string.Empty;
            card.TitleVisibility = string.IsNullOrEmpty(card.Title) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    // TitleVisibility Property
    public static readonly DependencyProperty TitleVisibilityProperty =
        DependencyProperty.Register(
            nameof(TitleVisibility),
            typeof(Visibility),
            typeof(MyCard),
            new PropertyMetadata(Visibility.Collapsed));

    public Visibility TitleVisibility
    {
        get => (Visibility)GetValue(TitleVisibilityProperty);
        set => SetValue(TitleVisibilityProperty, value);
    }

    // CornerRadius Property
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(
            nameof(CornerRadius),
            typeof(CornerRadius),
            typeof(MyCard),
            new PropertyMetadata(new CornerRadius(5), OnCornerRadiusChanged));

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    private static void OnCornerRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MyCard card)
        {
            card.ShadowChrome.CornerRadius = (CornerRadius)e.NewValue;
            card.BackgroundBorder.CornerRadius = (CornerRadius)e.NewValue;
        }
    }

    // HasMouseAnimation Property
    public static readonly DependencyProperty HasMouseAnimationProperty =
        DependencyProperty.Register(
            nameof(HasMouseAnimation),
            typeof(bool),
            typeof(MyCard),
            new PropertyMetadata(false)); // 暂时禁用动画

    public bool HasMouseAnimation
    {
        get => (bool)GetValue(HasMouseAnimationProperty);
        set => SetValue(HasMouseAnimationProperty, value);
    }

    #endregion

    #region Fields

    private const double IdleShadowOpacity = 0.07;
    private const double HoverShadowOpacity = 0.4;

    #endregion

    #region Event Handlers

    private void MyCard_Loaded(object sender, RoutedEventArgs e)
    {
        // Update title visibility on load
        TitleVisibility = string.IsNullOrEmpty(Title) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void MyCard_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!HasMouseAnimation) return;

        try
        {
            var duration = TimeSpan.FromMilliseconds(90);

            // Animate title color
            if (TitleVisibility == Visibility.Visible)
            {
                AnimateColor(TitleTextBlock, "ColorBrush2", duration);
            }

            // Animate swap indicator
            if (SwapPath.Visibility == Visibility.Visible)
            {
                AnimatePathColor(SwapPath, "ColorBrush2", duration);
            }

            // Animate shadow
            AnimateShadowOpacity(HoverShadowOpacity, duration);
            AnimateShadowColor("ColorObject4", duration);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MyCard] MouseEnter animation failed: {ex.Message}");
        }
    }

    private void MyCard_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!HasMouseAnimation) return;

        try
        {
            var duration = TimeSpan.FromMilliseconds(90);

            // Animate title color back
            if (TitleVisibility == Visibility.Visible)
            {
                AnimateColor(TitleTextBlock, "ColorBrush1", duration);
            }

            // Animate swap indicator back
            if (SwapPath.Visibility == Visibility.Visible)
            {
                AnimatePathColor(SwapPath, "ColorBrush1", duration);
            }

            // Animate shadow back
            AnimateShadowOpacity(IdleShadowOpacity, duration);
            AnimateShadowColor("ColorObject1", duration);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MyCard] MouseLeave animation failed: {ex.Message}");
        }
    }

    #endregion

    #region Animation Methods

    private void AnimateColor(TextBlock target, string resourceKey, Duration duration)
    {
        if (TryFindResource(resourceKey) is SolidColorBrush brush)
        {
            // 如果当前画刷是冻结的，创建一个新的可修改副本
            var animatableBrush = target.Foreground as SolidColorBrush;
            if (animatableBrush == null || animatableBrush.IsFrozen)
            {
                animatableBrush = brush.Clone();
                target.Foreground = animatableBrush;
            }

            var animation = new ColorAnimation
            {
                To = brush.Color,
                Duration = duration,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            animatableBrush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
        }
    }

    private void AnimatePathColor(Path target, string resourceKey, Duration duration)
    {
        if (TryFindResource(resourceKey) is SolidColorBrush brush)
        {
            // 如果当前画刷是冻结的，创建一个新的可修改副本
            var animatableBrush = target.Fill as SolidColorBrush;
            if (animatableBrush == null || animatableBrush.IsFrozen)
            {
                animatableBrush = brush.Clone();
                target.Fill = animatableBrush;
            }

            var animation = new ColorAnimation
            {
                To = brush.Color,
                Duration = duration,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            animatableBrush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
        }
    }

    private void AnimateShadowOpacity(double targetOpacity, Duration duration)
    {
        var animation = new DoubleAnimation
        {
            To = targetOpacity,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        ShadowEffect.BeginAnimation(DropShadowEffect.OpacityProperty, animation);
    }

    private void AnimateShadowColor(string resourceKey, Duration duration)
    {
        if (TryFindResource(resourceKey) is SolidColorBrush brush)
        {
            var animation = new ColorAnimation
            {
                To = brush.Color,
                Duration = duration,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            ShadowEffect.BeginAnimation(DropShadowEffect.ColorProperty, animation);
        }
    }

    #endregion
}
