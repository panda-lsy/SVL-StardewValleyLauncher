using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SVL.Core.Logging;

namespace SVL.Core.App;

/// <summary>
/// 单实例应用服务
/// 防止应用多开，确保只有一个实例在运行
/// </summary>
[LifecycleService(LifecycleState.BeforeLoading, Priority = -2134567890)]
[LifecycleScope("single-instance", "单例")]
public sealed partial class SingleInstanceService
{
    private static Mutex? _mutex;
    private static readonly string _MutexId = $"Global\\SVL-SingleInstance-{Environment.UserName}";
    private static readonly string _PipeName = $"SVL-Pipe-{Environment.UserName}";
    private static NamedPipeServerStream? _pipeServer;
    private static CancellationTokenSource? _pipeServerCts;
    private static Action<string>? _onNxmUrlReceived;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const int SW_RESTORE = 9;

    /// <summary>
    /// 设置 NXM URL 接收回调
    /// </summary>
    public static void SetNxmUrlCallback(Action<string> callback)
    {
        _onNxmUrlReceived = callback;
    }

    [LifecycleStart]
    private static void Start()
    {
        CheckSingleInstance();
    }

    /// <summary>
    /// 检查单实例（可被外部直接调用）
    /// </summary>
    /// <param name="nxmUrl">可选的 NXM URL，如果检测到重复实例则转发</param>
    public static void CheckSingleInstance(string? nxmUrl = null)
    {
        try
        {
            // 尝试创建全局 Mutex
            // initiallyOwned: false（如果不存在则创建，但不拥有所有权）
            // createdNew 输出参数告诉我们是否是新创建的
            var createdNew = false;
            _mutex = new Mutex(false, _MutexId, out createdNew);

            if (!createdNew)
            {
                // Mutex 已存在，说明已有实例运行
                Log.Warn("[SingleInstanceService] 检测到已有实例运行");

                // 如果有 NXM URL，转发给已存在的实例
                if (!string.IsNullOrEmpty(nxmUrl))
                {
                    Log.Info($"[SingleInstanceService] 转发 NXM URL 到已存在实例: {nxmUrl}");
                    SendNxmUrlToExistingInstance(nxmUrl);
                }

                // 尝试激活已存在的窗口
                TryActivateExistingWindow();

                // 退出当前实例
                Log.Info("[SingleInstanceService] 退出当前实例");
                Environment.Exit(0);
            }
            else
            {
                var currentPid = Process.GetCurrentProcess().Id;
                Log.Info($"[SingleInstanceService] 未发现重复实例，当前进程 ID: {currentPid}");

                // 启动命名管道服务器，接收来自其他实例的消息
                StartPipeServer();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SingleInstanceService] 单实例检查失败");
        }
    }

    [LifecycleStop]
    private static void Stop()
    {
        try
        {
            // 停止管道服务器
            if (_pipeServerCts != null)
            {
                Log.Info("[SingleInstanceService] 停止管道服务器");
                _pipeServerCts.Cancel();
                _pipeServerCts.Dispose();
                _pipeServerCts = null;
            }

            if (_pipeServer != null)
            {
                _pipeServer.Dispose();
                _pipeServer = null;
            }

            if (_mutex != null)
            {
                Log.Info("[SingleInstanceService] 释放单实例 Mutex");
                _mutex.Dispose();
                _mutex = null;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("[SingleInstanceService] 释放资源失败", ex);
        }
    }

    /// <summary>
    /// 启动命名管道服务器，监听来自其他实例的 NXM URL
    /// </summary>
    private static void StartPipeServer()
    {
        try
        {
            _pipeServerCts = new CancellationTokenSource();
            var token = _pipeServerCts.Token;

            Task.Run(async () =>
            {
                Log.Info($"[SingleInstanceService] 启动管道服务器: {_PipeName}");

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(
                            _PipeName,
                            PipeDirection.In,
                            NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Message,
                            PipeOptions.Asynchronous);

                        // 等待客户端连接（带超时和取消支持）
                        var waitForConnectionTask = server.WaitForConnectionAsync(token);
                        var timeoutTask = Task.Delay(5000, token);

                        var completedTask = await Task.WhenAny(waitForConnectionTask, timeoutTask);

                        if (completedTask == timeoutTask)
                        {
                            // 超时，检查是否需要取消
                            if (token.IsCancellationRequested)
                            {
                                server.Dispose();
                                break;
                            }
                            continue;
                        }

                        Log.Info("[SingleInstanceService] 检测到其他实例尝试连接");

                        // 读取消息
                        using var reader = new StreamReader(server, Encoding.UTF8);
                        var message = await reader.ReadLineAsync();

                        if (!string.IsNullOrEmpty(message))
                        {
                            Log.Info($"[SingleInstanceService] 收到消息: {message}");

                            // 处理 NXM URL 消息
                            if (message.StartsWith("NXM ", StringComparison.Ordinal))
                            {
                                var nxmUrl = message.Substring(4); // 去掉 "NXM " 前缀
                                Log.Info($"[SingleInstanceService] 收到 NXM URL: {nxmUrl}");

                                // 触发回调
                                _onNxmUrlReceived?.Invoke(nxmUrl);
                            }
                            else if (message == "ACTIVATE")
                            {
                                // 激活窗口请求
                                Log.Info("[SingleInstanceService] 收到激活窗口请求");
                                TryActivateMainWindow();
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Log.Info("[SingleInstanceService] 管道服务器被取消");
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (!token.IsCancellationRequested)
                        {
                            Log.Warn("[SingleInstanceService] 管道服务器处理连接失败", ex);
                        }
                    }
                }

                Log.Info("[SingleInstanceService] 管道服务器已停止");
            }, token);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SingleInstanceService] 启动管道服务器失败");
        }
    }

    /// <summary>
    /// 将 NXM URL 发送给已存在的实例
    /// </summary>
    private static void SendNxmUrlToExistingInstance(string nxmUrl)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _PipeName, PipeDirection.Out);
            client.Connect(1000); // 1秒超时

            if (client.IsConnected)
            {
                using var writer = new StreamWriter(client, Encoding.UTF8);
                writer.WriteLine($"NXM {nxmUrl}");
                writer.Flush();

                Log.Info("[SingleInstanceService] NXM URL 已发送到已存在实例");
            }
            else
            {
                Log.Warn("[SingleInstanceService] 无法连接到已存在实例的管道");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("[SingleInstanceService] 发送 NXM URL 到已存在实例失败", ex);
        }
    }

    /// <summary>
    /// 尝试激活主窗口
    /// </summary>
    private static void TryActivateMainWindow()
    {
        try
        {
            var currentProcess = Process.GetCurrentProcess();
            var processes = Process.GetProcessesByName("SVL.Desktop");

            foreach (var process in processes)
            {
                try
                {
                    if (process.Id != currentProcess.Id)
                    {
                        var mainWindowHandle = process.MainWindowHandle;

                        if (mainWindowHandle != IntPtr.Zero)
                        {
                            ShowWindow(mainWindowHandle, SW_RESTORE);
                            SetForegroundWindow(mainWindowHandle);

                            Log.Info($"[SingleInstanceService] 已激活进程 {process.Id} 的主窗口");
                            process.Dispose();
                            return;
                        }
                    }
                    process.Dispose();
                }
                catch
                {
                    process.Dispose();
                }
            }

            Log.Warn("[SingleInstanceService] 未找到可激活的窗口");
        }
        catch (Exception ex)
        {
            Log.Warn("[SingleInstanceService] 激活主窗口失败", ex);
        }
    }

    /// <summary>
    /// 尝试激活已存在的窗口（用于第二个实例调用）
    /// </summary>
    private static void TryActivateExistingWindow()
    {
        try
        {
            // 发送激活请求到第一个实例
            using var client = new NamedPipeClientStream(".", _PipeName, PipeDirection.Out);
            client.Connect(1000); // 1秒超时

            if (client.IsConnected)
            {
                using var writer = new StreamWriter(client, Encoding.UTF8);
                writer.WriteLine("ACTIVATE");
                writer.Flush();

                Log.Info("[SingleInstanceService] 已发送激活窗口请求");
            }
            else
            {
                Log.Warn("[SingleInstanceService] 无法连接到已存在实例的管道");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("[SingleInstanceService] 发送激活窗口请求失败", ex);
        }
    }

    /// <summary>
    /// 检查是否为单实例模式
    /// </summary>
    public static bool IsSingleInstance => _mutex != null;
}
