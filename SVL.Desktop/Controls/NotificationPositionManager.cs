using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace SVL.Desktop.Controls;

/// <summary>
/// 通知位置管理器 - 自动计算通知位置避免重叠
/// </summary>
internal static class NotificationPositionManager
{
    /// <summary>
    /// 活动的通知列表（按显示时间排序）
    /// </summary>
    private static readonly List<FloatingNotificationControl> _activeNotifications = new();

    /// <summary>
    /// 通知之间的垂直间距
    /// </summary>
    private const double NotificationSpacing = 10;

    /// <summary>
    /// 顶部边距
    /// </summary>
    private const double TopMargin = 20;

    /// <summary>
    /// 最大通知数量（超过后将移除最早的通知）
    /// </summary>
    private const int MaxNotifications = 5;

    /// <summary>
    /// 注册通知并计算其位置
    /// </summary>
    /// <param name="notification">通知控件</param>
    /// <param name="containerWidth">容器宽度</param>
    /// <returns>计算后的位置 (X, Y)</returns>
    public static (double X, double Y) RegisterNotification(FloatingNotificationControl notification, double containerWidth)
    {
        // 如果通知数量超过限制，移除最早的通知
        while (_activeNotifications.Count >= MaxNotifications)
        {
            var oldestNotification = _activeNotifications[0];
            oldestNotification.Close();
            _activeNotifications.RemoveAt(0);
        }

        // 添加到活动列表
        _activeNotifications.Add(notification);

        // 测量通知大小
        notification.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var notificationWidth = notification.DesiredSize.Width;
        var notificationHeight = notification.DesiredSize.Height;

        // 计算水平居中位置
        var x = (containerWidth - notificationWidth) / 2;

        // 计算垂直位置（堆叠在现有通知下方）
        var y = TopMargin;
        foreach (var activeNotification in _activeNotifications)
        {
            if (activeNotification == notification)
                break;

            activeNotification.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            y += activeNotification.DesiredSize.Height + NotificationSpacing;
        }

        return (x, y);
    }

    /// <summary>
    /// 注销通知（当通知关闭时调用）
    /// </summary>
    /// <param name="notification">要移除的通知</param>
    public static void UnregisterNotification(FloatingNotificationControl notification)
    {
        _activeNotifications.Remove(notification);

        // 重新排列剩余通知的位置
        RearrangeNotifications();
    }

    /// <summary>
    /// 重新排列所有活动通知的位置
    /// </summary>
    private static void RearrangeNotifications()
    {
        var mainWindow = Application.Current.MainWindow;
        if (mainWindow == null)
            return;

        var notificationContainer = mainWindow.FindName("NotificationContainer") as Canvas;
        if (notificationContainer == null)
            return;

        var containerWidth = notificationContainer.ActualWidth;

        // 重新计算所有通知的位置
        var y = TopMargin;
        foreach (var notification in _activeNotifications)
        {
            notification.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var notificationWidth = notification.DesiredSize.Width;
            var x = (containerWidth - notificationWidth) / 2;

            // 动画移动到新位置
            var currentTop = Canvas.GetTop(notification);
            var targetTop = y;

            if (Math.Abs(currentTop - targetTop) > 0.1)
            {
                // 使用平滑动画
                AnimatePosition(notification, x, targetTop);
            }
            else
            {
                Canvas.SetLeft(notification, x);
                Canvas.SetTop(notification, targetTop);
            }

            y += notification.DesiredSize.Height + NotificationSpacing;
        }
    }

    /// <summary>
    /// 平滑移动通知到新位置
    /// </summary>
    private static void AnimatePosition(FloatingNotificationControl notification, double targetX, double targetY)
    {
        var currentX = Canvas.GetLeft(notification);
        var currentY = Canvas.GetTop(notification);

        var animationX = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = currentX,
            To = targetX,
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }
        };

        var animationY = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = currentY,
            To = targetY,
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }
        };

        notification.BeginMoveAnimation(animationX, animationY);
    }

    /// <summary>
    /// 获取当前活动通知数量
    /// </summary>
    public static int ActiveCount => _activeNotifications.Count;
}
