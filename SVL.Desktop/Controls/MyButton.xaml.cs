using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SVL.Desktop.Controls;

/// <summary>
/// PCL2-CE 风格的自定义按钮控件
/// </summary>
public partial class MyButton : Border
{
    public MyButton()
    {
        InitializeComponent();
        MouseEnter += (s, e) => RefreshColor();
        MouseLeave += (s, e) => RefreshColor();
        Loaded += (s, e) => RefreshColor();
        IsEnabledChanged += (s, e) => RefreshColor();
        MouseLeftButtonUp += (s, e) => OnClick();
    }

    #region Events

    public event MouseButtonEventHandler? Click;

    protected virtual void OnClick()
    {
        Click?.Invoke(this, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left));
    }

    #endregion

    #region Properties

    // Text Property
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(MyButton),
            new PropertyMetadata(string.Empty, OnTextChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MyButton button)
        {
            button.LabText.Text = e.NewValue as string ?? string.Empty;
        }
    }

    // TextPadding Property
    public static readonly DependencyProperty TextPaddingProperty =
        DependencyProperty.Register(
            nameof(TextPadding),
            typeof(Thickness),
            typeof(MyButton),
            new PropertyMetadata(default(Thickness), OnTextPaddingChanged));

    public Thickness TextPadding
    {
        get => (Thickness)GetValue(TextPaddingProperty);
        set => SetValue(TextPaddingProperty, value);
    }

    private static void OnTextPaddingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MyButton button)
        {
            button.LabText.Padding = (Thickness)e.NewValue;
        }
    }

    // ColorType Property
    public static readonly DependencyProperty ColorTypeProperty =
        DependencyProperty.Register(
            nameof(ColorType),
            typeof(ColorState),
            typeof(MyButton),
            new PropertyMetadata(ColorState.Normal, OnColorTypeChanged));

    public ColorState ColorType
    {
        get => (ColorState)GetValue(ColorTypeProperty);
        set => SetValue(ColorTypeProperty, value);
    }

    private static void OnColorTypeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MyButton button)
        {
            button.RefreshColor();
        }
    }

    // Padding Property (pass-through)
    public static readonly DependencyProperty PaddingProperty =
        DependencyProperty.Register(
            nameof(Padding),
            typeof(Thickness),
            typeof(MyButton),
            new PropertyMetadata(default(Thickness), OnPaddingChanged));

    public new Thickness Padding
    {
        get => (Thickness)GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    private static void OnPaddingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MyButton button)
        {
            button.PanFore.Padding = (Thickness)e.NewValue;
        }
    }

    // RealRenderTransform Property
    public static readonly DependencyProperty RealRenderTransformProperty =
        DependencyProperty.Register(
            nameof(RealRenderTransform),
            typeof(Transform),
            typeof(MyButton),
            new PropertyMetadata(null, OnRealRenderTransformChanged));

    public Transform RealRenderTransform
    {
        get => (Transform)GetValue(RealRenderTransformProperty);
        set => SetValue(RealRenderTransformProperty, value);
    }

    private static void OnRealRenderTransformChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MyButton button)
        {
            button.PanFore.RenderTransform = (Transform)e.NewValue;
        }
    }

    #endregion

    #region Enums

    public enum ColorState
    {
        Normal = 0,
        Highlight = 1,
        Red = 2
    }

    #endregion

    #region Color Animation

    private const int AnimationColorIn = 100;
    private const int AnimationColorOut = 200;

    private void RefreshColor()
    {
        if (!IsLoaded) return;

        var targetBrush = IsEnabled ? GetTargetBrush() : GetDisabledBrush();
        PanFore.BorderBrush = (Brush)FindResource(targetBrush);
    }

    private string GetTargetBrush()
    {
        if (!IsMouseOver)
        {
            return ColorType switch
            {
                ColorState.Normal => "ColorBrush1",
                ColorState.Highlight => "ColorBrush2",
                ColorState.Red => "ColorBrushRedDark",
                _ => "ColorBrush1"
            };
        }
        else
        {
            return ColorType switch
            {
                ColorState.Normal => "ColorBrush3",
                ColorState.Highlight => "ColorBrush3",
                ColorState.Red => "ColorBrushRedLight",
                _ => "ColorBrush3"
            };
        }
    }

    private string GetDisabledBrush()
    {
        return "ColorBrushGray3";
    }

    #endregion
}
