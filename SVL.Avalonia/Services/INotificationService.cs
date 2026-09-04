using System;

namespace SVL.Avalonia.Services;

/// <summary>
/// 浮窗通知类型，对应 WPF 的 NotificationType 枚举。
/// </summary>
public enum NotificationType
{
    /// <summary>成功（绿色，✓ 图标）</summary>
    Success,

    /// <summary>错误（红色，✗ 图标）</summary>
    Error,

    /// <summary>警告（橙色，! 图标）</summary>
    Warning,

    /// <summary>信息（蓝色，i 图标）</summary>
    Info
}

/// <summary>
/// 浮窗通知服务接口。门面 <see cref="NotificationService.Show"/> 委托给当前实现实例。
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// 显示一条浮窗通知。
    /// </summary>
    /// <param name="title">标题</param>
    /// <param name="message">消息内容</param>
    /// <param name="autoCloseDelay">自动关闭时间（毫秒），0 表示不自动关闭</param>
    /// <param name="onClosed">关闭后回调（在 UI 线程触发）</param>
    /// <param name="notificationType">通知类型，默认 Success</param>
    void Show(
        string title,
        string message,
        int autoCloseDelay = 5000,
        Action? onClosed = null,
        NotificationType notificationType = NotificationType.Success);
}
