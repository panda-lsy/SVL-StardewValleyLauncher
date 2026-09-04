namespace SVL.Core.Platform.Abstractions;

/// <summary>
/// 跨平台单实例约束服务。用于保证同一时刻只有一个启动器实例运行，
/// 并在检测到第二个实例时把命令行消息（如 NXM 链接）转发给已运行实例。
/// </summary>
/// <remarks>
/// 平台支持：Windows 通过全局 Mutex + 命名管道实现；非 Windows 平台 <see cref="IsSupported"/> 返回 false，
/// 不约束单实例（第二个实例会直接运行，由调用方决定行为）。
/// </remarks>
public interface ISingleInstanceService
{
    /// <summary>当前平台是否支持单实例约束。</summary>
    bool IsSupported { get; }

    /// <summary>
    /// 尝试获取单实例锁。成功表示当前是首个实例，应继续启动并调用 <see cref="StartListening"/>；
    /// 失败表示已有实例运行，应通过 <see cref="ForwardToRunningInstance"/> 转发参数后退出。
    /// </summary>
    bool TryAcquire();

    /// <summary>
    /// 向已运行的实例转发一条消息（如 "NXM &lt;url&gt;"）。
    /// 仅在 <see cref="TryAcquire"/> 返回 false（已有实例运行）时调用。
    /// </summary>
    /// <returns>转发是否成功。</returns>
    bool ForwardToRunningInstance(string message);

    /// <summary>
    /// 启动后台监听，接收来自其他实例转发的消息。
    /// 仅在 <see cref="TryAcquire"/> 返回 true（首个实例）时调用。
    /// </summary>
    /// <param name="onMessageReceived">收到消息时的回调（在后台线程触发，调用方需自行切回 UI 线程）。</param>
    void StartListening(Action<string> onMessageReceived);

    /// <summary>释放单实例锁并停止监听。应用退出时调用。</summary>
    void Stop();
}
