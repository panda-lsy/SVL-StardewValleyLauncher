using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using SVL.Avalonia.Models;

namespace SVL.Avalonia.Services;

/// <summary>
/// 浮窗通知服务实现。
/// 维护 <see cref="ActiveNotifications"/> 集合供 MainWindow 的 ItemsControl 绑定；
/// 提供静态门面 <see cref="Show"/> 供迁移期调用点直接使用（对应 WPF 的 FloatingNotificationControl.Show）。
/// </summary>
public sealed class NotificationService : INotificationService
{
    /// <summary>最大同时显示通知数，超过将移除最早的通知（对齐 WPF NotificationPositionManager.MaxNotifications）</summary>
    private const int MaxNotifications = 5;

    /// <summary>退场动画时长（毫秒），服务端在此时间后从集合移除并触发 OnClosed</summary>
    private const int HideAnimationMs = 320;

    /// <summary>当前注册的服务实例。由 MainWindow 在加载时通过 <see cref="RegisterHost"/> 设置。</summary>
    private static NotificationService? s_current;

    /// <summary>活动通知集合，UI 通过 ItemsControl 绑定渲染。</summary>
    public ObservableCollection<NotificationItem> ActiveNotifications { get; } = new();

    /// <summary>当前活动通知数量。</summary>
    public int ActiveCount => ActiveNotifications.Count;

    /// <summary>
    /// 注册当前实例为活动宿主，使静态门面 <see cref="Show"/> 能委托到实际服务。
    /// 通常在 MainWindow 加载完成时调用。
    /// </summary>
    public static void RegisterHost(NotificationService service)
    {
        s_current = service;
    }

    /// <summary>
    /// 静态门面：显示一条浮窗通知。委托给当前注册的实例。
    /// 若无宿主（启动早期或非 UI 上下文），调用会被安全丢弃并记录到 Debug 控制台。
    /// 该方法签名与 WPF FloatingNotificationControl.Show 一致，便于迁移期机械替换调用点。
    /// </summary>
    public static void Show(
        string title,
        string message,
        int autoCloseDelay = 5000,
        Action? onClosed = null,
        NotificationType notificationType = NotificationType.Success)
    {
        s_current?.ShowInternal(title, message, autoCloseDelay, onClosed, notificationType);
    }

    /// <summary>显式接口实现，委托给 <see cref="ShowInternal"/>。静态门面 <see cref="Show"/> 为主要 API。</summary>
    void INotificationService.Show(
        string title,
        string message,
        int autoCloseDelay,
        Action? onClosed,
        NotificationType notificationType)
    {
        ShowInternal(title, message, autoCloseDelay, onClosed, notificationType);
    }

    private void ShowInternal(
        string title,
        string message,
        int autoCloseDelay,
        Action? onClosed,
        NotificationType type)
    {
        // 确保在 UI 线程操作集合。
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ShowInternal(title, message, autoCloseDelay, onClosed, type));
            return;
        }

        // 超过最大数量时，关闭最早的通知（触发其退场动画）。
        while (ActiveNotifications.Count >= MaxNotifications)
        {
            Close(ActiveNotifications[0]);
        }

        NotificationItem? item = null;
        item = new NotificationItem
        {
            Title = title,
            Message = message,
            Type = type,
            AutoCloseDelay = autoCloseDelay,
            OnClosed = onClosed,
            RequestRemove = () => Close(item!)
        };

        ActiveNotifications.Add(item);

        // 自动关闭计时器（0 表示不自动关闭）。
        if (autoCloseDelay > 0)
        {
            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(autoCloseDelay)
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Close(item!);
            };
            timer.Start();
        }
    }

    /// <summary>
    /// 关闭一条通知：先标记 IsClosing 触发视图层退场动画，
    /// 等待动画时长后再从集合移除并触发 OnClosed 回调。
    /// </summary>
    public void Close(NotificationItem item)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Close(item));
            return;
        }

        if (!ActiveNotifications.Contains(item) || item.IsClosing)
        {
            return;
        }

        item.IsClosing = true;

        // 等待退场动画完成后移除。动画时长由视图层 Transitions 控制，此处与之一致。
        _ = Task.Delay(HideAnimationMs).ContinueWith(_ =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                ActiveNotifications.Remove(item);
                item.OnClosed?.Invoke();
            });
        });
    }
}
