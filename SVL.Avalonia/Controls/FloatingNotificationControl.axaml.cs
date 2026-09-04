using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using SVL.Avalonia.Models;
using SVL.Avalonia.Services;

namespace SVL.Avalonia.Controls;

/// <summary>
/// 浮窗通知卡片视图。作为 ItemsControl 的 DataTemplate 内容渲染单条 <see cref="NotificationItem"/>。
/// 样式（卡片背景色 + 图标字符）按 <see cref="NotificationType"/> 在 code-behind 设置，
/// 对齐 WPF 的 UpdateNotificationStyle 行为。
/// 入场动画在 Loaded 时由 Y=-150/Opacity=0 过渡到 Y=0/Opacity=1（由 Border.Transitions 驱动）。
/// 退场动画由 NotificationItem.IsClosing=true 触发，过渡回 Y=-150/Opacity=0。
/// </summary>
public partial class FloatingNotificationControl : UserControl
{
    private NotificationItem? _item;

    public FloatingNotificationControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        // 解绑旧的
        if (_item is { } oldItem)
        {
            oldItem.PropertyChanged -= OnItemPropertyChanged;
        }

        _item = DataContext as NotificationItem;

        if (_item is { } newItem)
        {
            ApplyStyle(newItem.Type);
            newItem.PropertyChanged += OnItemPropertyChanged;
        }
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not NotificationItem item)
        {
            return;
        }

        if (e.PropertyName == nameof(NotificationItem.Type))
        {
            ApplyStyle(item.Type);
        }
        else if (e.PropertyName == nameof(NotificationItem.IsClosing))
        {
            if (item.IsClosing)
            {
                TriggerHide();
            }
        }
    }

    private void OnLoaded(object? sender, System.EventArgs e)
    {
        // 入场：初始状态已在 XAML 设为 Y=-150。Loaded 后切到目标态触发 Transition。
        // 用 Post 确保控件完成布局后再触发动画。
        Dispatcher.UIThread.Post(() =>
        {
            Card.Opacity = 1;
            if (Card.RenderTransform is TranslateTransform t)
            {
                t.Y = 0;
            }
        });
    }

    private void TriggerHide()
    {
        // 退场：仅 Opacity 过渡到 0（Y 轴无 Transition，瞬移会突兀故保留原位淡出）。完成后由服务端从集合移除。
        Card.Opacity = 0;
    }

    /// <summary>按通知类型设置卡片背景色、图标字符、图标前景色（对齐 WPF UpdateNotificationStyle）。</summary>
    private void ApplyStyle(NotificationType type)
    {
        // 卡片初始透明度为 0，入场动画在 OnLoaded 触发到 1。
        Card.Opacity = 0;

        switch (type)
        {
            case NotificationType.Success:
                Card.Background = new SolidColorBrush(Color.Parse("#2E7D32"));
                IconText.Text = "✓";
                IconText.Foreground = new SolidColorBrush(Color.Parse("#2E7D32"));
                break;
            case NotificationType.Error:
                Card.Background = new SolidColorBrush(Color.Parse("#D32F2F"));
                IconText.Text = "✗";
                IconText.Foreground = new SolidColorBrush(Color.Parse("#D32F2F"));
                break;
            case NotificationType.Warning:
                Card.Background = new SolidColorBrush(Color.Parse("#F57C00"));
                IconText.Text = "ⓘ";
                IconText.Foreground = new SolidColorBrush(Color.Parse("#F57C00"));
                break;
            case NotificationType.Info:
                Card.Background = new SolidColorBrush(Color.Parse("#1976D2"));
                IconText.Text = "i";
                IconText.Foreground = new SolidColorBrush(Color.Parse("#1976D2"));
                break;
        }
    }
}
