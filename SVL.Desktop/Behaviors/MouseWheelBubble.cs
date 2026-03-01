using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SVL.Desktop.Behaviors;

/// <summary>
/// 鼠标滚轮事件冒泡行为
/// 用于让内部 ScrollViewer 的鼠标滚轮事件能够传递到父级 ScrollViewer
/// </summary>
public static class MouseWheelBubble
{
    /// <summary>
    /// 是否启用鼠标滚轮事件冒泡
    /// </summary>
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(MouseWheelBubble),
            new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject obj)
    {
        return (bool)obj.GetValue(EnabledProperty);
    }

    public static void SetEnabled(DependencyObject obj, bool value)
    {
        obj.SetValue(EnabledProperty, value);
    }

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer scrollViewer)
        {
            if ((bool)e.NewValue)
            {
                scrollViewer.PreviewMouseWheel += OnPreviewMouseWheel;
            }
            else
            {
                scrollViewer.PreviewMouseWheel -= OnPreviewMouseWheel;
            }
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
            return;

        // 检查是否还有滚动空间
        bool canScrollUp = scrollViewer.VerticalOffset > 0;
        bool canScrollDown = scrollViewer.VerticalOffset < scrollViewer.ScrollableHeight;

        // 如果向上滚动且已经到顶部，或者向下滚动且已经到底部
        // 则将事件传递给父级 ScrollViewer
        if ((e.Delta > 0 && !canScrollUp) || (e.Delta < 0 && !canScrollDown))
        {
            // 重新引发事件，使其向上冒泡
            e.Handled = true;

            var reraisedEventArgs = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = sender
            };

            scrollViewer.RaiseEvent(reraisedEventArgs);
        }
    }
}
