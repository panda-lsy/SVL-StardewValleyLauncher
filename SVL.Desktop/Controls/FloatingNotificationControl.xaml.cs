using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SVL.Desktop.Controls;

/// <summary>
/// 浮窗通知控件
/// </summary>
public partial class FloatingNotificationControl : UserControl
{
    /// <summary>
    /// 位置动画 Storyboard
    /// </summary>
    private Storyboard? _moveStoryboard;

    /// <summary>
    /// 标题依赖属性
    /// </summary>
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(
            nameof(Title),
            typeof(string),
            typeof(FloatingNotificationControl),
            new PropertyMetadata("安装成功"));

    /// <summary>
    /// 消息依赖属性
    /// </summary>
    public static readonly DependencyProperty MessageProperty =
        DependencyProperty.Register(
            nameof(Message),
            typeof(string),
            typeof(FloatingNotificationControl),
            new PropertyMetadata(string.Empty));

    /// <summary>
    /// 通知类型依赖属性
    /// </summary>
    public static readonly DependencyProperty NotificationTypeProperty =
        DependencyProperty.Register(
            nameof(NotificationType),
            typeof(NotificationType),
            typeof(FloatingNotificationControl),
            new PropertyMetadata(NotificationType.Success, OnNotificationTypeChanged));

    /// <summary>
    /// 自动关闭时间（毫秒）
    /// </summary>
    public int AutoCloseDelay { get; set; } = 5000;

    private Action? _onClosed;

    public FloatingNotificationControl()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 标题
    /// </summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>
    /// 消息内容
    /// </summary>
    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    /// <summary>
    /// 通知类型
    /// </summary>
    public NotificationType NotificationType
    {
        get => (NotificationType)GetValue(NotificationTypeProperty);
        set => SetValue(NotificationTypeProperty, value);
    }

    /// <summary>
    /// 当模板应用时更新样式
    /// 确保 NotificationCard、IconTextBlock、IconBackground 等模板子元素已准备好
    /// </summary>
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateNotificationStyle();
    }

    /// <summary>
    /// 当控件加载时更新样式（确保模板已应用）
    /// </summary>
    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        UpdateNotificationStyle();
    }

    /// <summary>
    /// 通知类型改变时更新样式
    /// </summary>
    private static void OnNotificationTypeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FloatingNotificationControl control)
        {
            control.UpdateNotificationStyle();
        }
    }

    /// <summary>
    /// 根据通知类型更新样式
    /// </summary>
    private void UpdateNotificationStyle()
    {
        // 使用 LogicalTreeHelper 查找元素（UserControl 使用这种方式而不是 GetTemplateChild）
        Border? card = null;
        TextBlock? iconText = null;
        Border? iconBg = null;

        // 遍历可视化树查找元素
        void FindElements(DependencyObject parent)
        {
            if (parent == null) return;

            var childrenCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childrenCount; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);

                if (child is Border border)
                {
                    if (border.Name == "NotificationCard")
                        card = border;
                    else if (border.Name == "IconBackground")
                        iconBg = border;
                }
                else if (child is TextBlock textBlock && textBlock.Name == "IconTextBlock")
                {
                    iconText = textBlock;
                }

                if (card != null && iconText != null && iconBg != null)
                    break;

                FindElements(child);
            }
        }

        FindElements(this);

        // 设置图标背景为白色
        if (iconBg != null)
        {
            iconBg.Background = new SolidColorBrush(Colors.White);
        }

        // 如果没有找到关键元素，跳过样式更新
        if (card == null || iconText == null)
        {
            System.Diagnostics.Debug.WriteLine($"[FloatingNotification] 未找到元素: card={card != null}, iconText={iconText != null}");
            return;
        }

        switch (NotificationType)
        {
            case NotificationType.Success:
                card.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2E7D32"));
                iconText.Text = "✓";
                iconText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2E7D32"));
                break;
            case NotificationType.Error:
                card.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D32F2F"));
                iconText.Text = "✗";
                iconText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D32F2F"));
                break;
            case NotificationType.Warning:
                card.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F57C00"));
                iconText.Text = "ⓘ";
                iconText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F57C00"));
                break;
            case NotificationType.Info:
                card.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1976D2"));
                iconText.Text = "i";
                iconText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1976D2"));
                break;
        }
    }

    /// <summary>
    /// 显示通知
    /// </summary>
    /// <param name="title">标题</param>
    /// <param name="message">消息内容</param>
    /// <param name="autoCloseDelay">自动关闭时间（毫秒），0表示不自动关闭</param>
    /// <param name="onClosed">关闭回调</param>
    /// <param name="notificationType">通知类型（默认成功）</param>
    /// <returns>通知控件实例</returns>
    public static FloatingNotificationControl Show(
        string title,
        string message,
        int autoCloseDelay = 5000,
        Action? onClosed = null,
        NotificationType notificationType = NotificationType.Success)
    {
        FloatingNotificationControl? notification = null;

        // 确保在 UI 线程执行
        Application.Current.Dispatcher.Invoke(() =>
        {
            notification = new FloatingNotificationControl
            {
                Title = title,
                Message = message,
                AutoCloseDelay = autoCloseDelay,
                _onClosed = onClosed,
                NotificationType = notificationType
            };

            // 获取主窗口
            var mainWindow = Application.Current.MainWindow;
            if (mainWindow != null)
            {
                // 查找通知容器
                var notificationContainer = mainWindow.FindName("NotificationContainer") as Canvas;
                if (notificationContainer != null)
                {
                    // 添加通知到容器
                    notification.IsHitTestVisible = true;  // 确保通知控件可以交互
                    notificationContainer.Children.Add(notification);

                    // 使用位置管理器计算位置
                    notification.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    notification.Arrange(new Rect(new Point(0, 0), notification.DesiredSize));

                    var containerWidth = notificationContainer.ActualWidth;
                    var (x, y) = NotificationPositionManager.RegisterNotification(notification, containerWidth);

                    Canvas.SetLeft(notification, x);
                    Canvas.SetTop(notification, y);

                    // 触发入场动画
                    var showAnimation = (Storyboard)notification.TryFindResource("ShowAnimation");
                    showAnimation?.Begin();

                    // 自动关闭计时器
                    if (autoCloseDelay > 0)
                    {
                        var timer = new System.Windows.Threading.DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(autoCloseDelay)
                        };
                        timer.Tick += (s, e) =>
                        {
                            timer.Stop();
                            notification.Close();
                        };
                        timer.Start();
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[FloatingNotification] 找不到通知容器");
                }
            }
        });

        return notification!;
    }

    /// <summary>
    /// 关闭通知
    /// </summary>
    public void Close()
    {
        var hideAnimation = (Storyboard)TryFindResource("HideAnimation");
        if (hideAnimation != null)
        {
            hideAnimation.Begin();
        }
        else
        {
            OnHideAnimationCompleted(null, null);
        }
    }

    /// <summary>
    /// 开始位置动画（用于重新排列）
    /// </summary>
    internal void BeginMoveAnimation(System.Windows.Media.Animation.DoubleAnimation animationX, System.Windows.Media.Animation.DoubleAnimation animationY)
    {
        // 停止之前的移动动画
        _moveStoryboard?.Stop();

        // 创建新的 Storyboard
        _moveStoryboard = new Storyboard();

        // 设置 X 动画
        Storyboard.SetTarget(animationX, this);
        Storyboard.SetTargetProperty(animationX, new PropertyPath("(Canvas.Left)"));
        _moveStoryboard.Children.Add(animationX);

        // 设置 Y 动画
        Storyboard.SetTarget(animationY, this);
        Storyboard.SetTargetProperty(animationY, new PropertyPath("(Canvas.Top)"));
        _moveStoryboard.Children.Add(animationY);

        // 开始动画
        _moveStoryboard.Begin();
    }

    /// <summary>
    /// 退场动画完成事件
    /// </summary>
    private void OnHideAnimationCompleted(object? sender, EventArgs e)
    {
        // 从通知容器中移除
        var mainWindow = Application.Current.MainWindow;
        if (mainWindow != null)
        {
            var notificationContainer = mainWindow.FindName("NotificationContainer") as Canvas;
            if (notificationContainer != null && notificationContainer.Children.Contains(this))
            {
                notificationContainer.Children.Remove(this);
            }
        }

        // 从位置管理器中移除
        NotificationPositionManager.UnregisterNotification(this);

        // 触发关闭回调
        _onClosed?.Invoke();
    }
}

/// <summary>
/// 通知装饰器
/// </summary>
internal class NotificationAdorner : Adorner
{
    private readonly FloatingNotificationControl _notification;

    public FloatingNotificationControl Notification => _notification;

    public NotificationAdorner(UIElement adornedElement, FloatingNotificationControl notification)
        : base(adornedElement)
    {
        _notification = notification;
        AddVisualChild(notification);
        IsHitTestVisible = true;
    }

    protected override Visual GetVisualChild(int index)
    {
        return _notification;
    }

    protected override int VisualChildrenCount => 1;

    protected override Size MeasureOverride(Size constraint)
    {
        _notification.Measure(constraint);
        return _notification.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // 将通知放在顶部中央
        var adornerWidth = AdornedElement.RenderSize.Width;
        var notificationWidth = _notification.DesiredSize.Width;
        var x = (adornerWidth - notificationWidth) / 2;
        _notification.Arrange(new Rect(new Point(x, 0), _notification.DesiredSize));
        return finalSize;
    }
}

/// <summary>
/// 通知类型
/// </summary>
public enum NotificationType
{
    /// <summary>
    /// 成功（绿色，✓图标）
    /// </summary>
    Success,

    /// <summary>
    /// 错误（红色，✗图标）
    /// </summary>
    Error,

    /// <summary>
    /// 警告（橙色，!图标）
    /// </summary>
    Warning,

    /// <summary>
    /// 信息（蓝色，i图标）
    /// </summary>
    Info
}
