using System;

namespace SVL.Core.Config;

/// <summary>
/// 启动器配置更新事件
/// </summary>
public static class LauncherConfigService
{
    /// <summary>
    /// 启动器应用名称更新事件
    /// </summary>
    public static event Action<string>? LauncherAppNameChanged;

    /// <summary>
    /// 启动器标题更新事件
    /// </summary>
    public static event Action<string>? LauncherTitleChanged;

    /// <summary>
    /// 触发启动器应用名称更新
    /// </summary>
    public static void NotifyLauncherAppNameChanged(string appName)
    {
        LauncherAppNameChanged?.Invoke(appName);
    }

    /// <summary>
    /// 触发启动器标题更新
    /// </summary>
    public static void NotifyLauncherTitleChanged(string title)
    {
        LauncherTitleChanged?.Invoke(title);
    }
}
