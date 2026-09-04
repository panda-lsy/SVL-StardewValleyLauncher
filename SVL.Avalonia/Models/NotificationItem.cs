using System;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SVL.Avalonia.Services;

namespace SVL.Avalonia.Models;

/// <summary>
/// 浮窗通知数据模型，作为 ItemsControl 的数据项。
/// 入场/退场动画由 <see cref="IsClosing"/> 标志驱动视图层 Transitions。
/// </summary>
public partial class NotificationItem : ObservableObject
{
    /// <summary>标题</summary>
    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>消息内容</summary>
    [ObservableProperty]
    private string _message = string.Empty;

    /// <summary>通知类型</summary>
    [ObservableProperty]
    private NotificationType _type = NotificationType.Info;

    /// <summary>自动关闭时间（毫秒），0 表示不自动关闭</summary>
    public int AutoCloseDelay { get; init; } = 5000;

    /// <summary>关闭后回调</summary>
    public Action? OnClosed { get; init; }

    /// <summary>
    /// 是否正在退场。视图层监听该属性从 false→true 时触发退场动画，
    /// 动画完成后再从服务集合中移除。
    /// </summary>
    [ObservableProperty]
    private bool _isClosing;

    /// <summary>用户手动关闭命令</summary>
    public ICommand DismissCommand { get; }

    /// <summary>由服务端在退场动画完成后调用，从集合移除并触发 OnClosed</summary>
    public Action? RequestRemove { get; set; }

    public NotificationItem()
    {
        DismissCommand = new RelayCommand(() => RequestRemove?.Invoke());
    }
}
