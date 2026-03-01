using System;

namespace SVL.Desktop.ViewModels;

/// <summary>
/// 全局事件管理器，用于跨 ViewModel 的通信
/// </summary>
public static class GlobalEvents
{
    /// <summary>
    /// 实例配置已更改事件（当实例的图标、名称等属性改变时触发）
    /// </summary>
    public static event EventHandler<InstanceChangedEventArgs>? InstanceChanged;

    /// <summary>
    /// 触发实例配置更改事件
    /// </summary>
    public static void OnInstanceChanged(string instanceId)
    {
        InstanceChanged?.Invoke(null, new InstanceChangedEventArgs(instanceId));
    }
}

/// <summary>
/// 实例更改事件参数
/// </summary>
public class InstanceChangedEventArgs : EventArgs
{
    public string InstanceId { get; }

    public InstanceChangedEventArgs(string instanceId)
    {
        InstanceId = instanceId;
    }
}
