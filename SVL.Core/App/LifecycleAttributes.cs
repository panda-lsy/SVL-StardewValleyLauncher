using System;

namespace SVL.Core.App;

/// <summary>
/// 生命周期属性标记
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class LifecycleServiceAttribute : Attribute
{
    /// <summary>
    /// 生命周期状态
    /// </summary>
    public LifecycleState State { get; }

    /// <summary>
    /// 服务启动优先级（数字越小越优先）
    /// </summary>
    public int Priority { get; set; }

    public LifecycleServiceAttribute(LifecycleState state, int priority = 0)
    {
        State = state;
        Priority = priority;
    }
}

/// <summary>
/// 生命周期作用域标记
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class LifecycleScopeAttribute : Attribute
{
    /// <summary>
    /// 作用域标识符
    /// </summary>
    public string Scope { get; }

    /// <summary>
    /// 作用域显示名称
    /// </summary>
    public string Name { get; }

    public LifecycleScopeAttribute(string scope, string name)
    {
        Scope = scope;
        Name = name;
    }
}

/// <summary>
/// 生命周期开始标记
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class LifecycleStartAttribute : Attribute
{
}

/// <summary>
/// 生命周期停止标记
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class LifecycleStopAttribute : Attribute
{
}
