using System.IO.Pipes;
using System.Text;
using SVL.Core.Platform.Abstractions;

namespace SVL.Core.Platform.Services;

/// <summary>
/// 单实例服务实现。Windows 用全局 Mutex + 命名管道；非 Windows 平台不约束（IsSupported=false）。
/// 管道消息约定：文本行，前缀 "NXM " 表示 NXM 链接（与旧 SVL.Core 单实例兼容）。
/// </summary>
public sealed class SingleInstanceService : ISingleInstanceService
{
    private static readonly string MutexId = $"Global\\SVL-SingleInstance-{Environment.UserName}";
    private static readonly string PipeName = $"SVL-Pipe-{Environment.UserName}";

    private Mutex? _mutex;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public bool IsSupported => OperatingSystem.IsWindows();

    public bool TryAcquire()
    {
        if (!OperatingSystem.IsWindows())
        {
            // 非 Windows 不约束单实例，直接返回 true 表示"无需退出，继续运行"。
            return true;
        }

        try
        {
            _mutex = new Mutex(initiallyOwned: false, name: MutexId, out var createdNew);
            return createdNew;
        }
        catch
        {
            // 获取失败时按"首个实例"处理，避免误杀正常启动。
            return true;
        }
    }

    public bool ForwardToRunningInstance(string message)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(message))
        {
            return false;
        }

        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1000);
            if (!client.IsConnected)
            {
                return false;
            }

            using var writer = new StreamWriter(client, Encoding.UTF8);
            writer.WriteLine(message);
            writer.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void StartListening(Action<string> onMessageReceived)
    {
        if (!OperatingSystem.IsWindows() || _mutex is null || onMessageReceived is null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _listenTask = Task.Run(async () =>
        {
            // 编译器平台守卫分析不跨 lambda，需在此重复判断以消除 CA1416。
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Message,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token);

                    // StreamReader 默认会 dispose 底层 server；finally 的 Dispose 对已释放流是安全的。
                    using (var reader = new StreamReader(server, Encoding.UTF8))
                    {
                        var message = await reader.ReadLineAsync();
                        if (!string.IsNullOrEmpty(message))
                        {
                            onMessageReceived(message);
                        }
                    }
                    server = null; // 已由 reader 释放，避免 finally 重复 dispose
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // 单次连接失败不中断后续监听。
                }
                finally
                {
                    server?.Dispose();
                }
            }
        }, token);
    }

    public void Stop()
    {
        // 注意：TryAcquire 用 initiallyOwned:false 创建 Mutex，从未 WaitOne，因此不调用 ReleaseMutex。
        try { _cts?.Cancel(); } catch { /* 忽略取消异常 */ }
        try { _cts?.Dispose(); } catch { /* 忽略释放异常 */ }
        try { _mutex?.Dispose(); } catch { /* 忽略释放异常 */ }

        _cts = null;
        _mutex = null;
        _listenTask = null;
    }
}
