namespace SVL.Core.App;

/// <summary>
/// 生命周期状态枚举
/// </summary>
public enum LifecycleState
{
    BeforeLoading = 0,
    Loading = 1,
    WindowCreating = 2,
    Loaded = 3,
    BeforeStop = 4,
    Stopping = 5,
    Stopped = 6
}
