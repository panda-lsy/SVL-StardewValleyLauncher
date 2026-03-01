using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SVL.Core.Logging;

namespace SVL.Core.Stardew.Launch;

public static class WindowTitleSetter
{
    [DllImport("user32.dll")]
    private static extern bool SetWindowText(IntPtr hWnd, string text);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>
    /// 异步设置游戏窗口标题，监听 SMAPI 输出并在游戏加载完成后设置标题
    /// </summary>
    public static async Task SetWindowTitleAsync(Process gameProcess, string titleTemplate, string instanceName, bool isSMAPI, string? gamePath, int timeoutSeconds = 60)
    {
        await Task.Run(async () =>
        {
            Logging.Log.Info($"[WindowTitleSetter] Waiting for game to load before setting title");
            Logging.Log.Info($"[WindowTitleSetter] Title template: {titleTemplate}");
            Logging.Log.Info($"[WindowTitleSetter] Instance name: {instanceName}");
            Logging.Log.Info($"[WindowTitleSetter] Is SMAPI: {isSMAPI}");
            Logging.Log.Info($"[WindowTitleSetter] Game path: {gamePath}");
            Logging.Log.Info($"[WindowTitleSetter] Game Process ID: {gameProcess.Id}");

            // 步骤1：等待游戏加载完成并解析游戏信息
            var gameInfo = await WaitForGameLoadAsync(gameProcess, gamePath, timeoutSeconds);

            Logging.Log.Info($"[WindowTitleSetter] Parsed game info: {gameInfo}");

            // 步骤2：替换 placeholder
            var finalTitle = WindowTitlePlaceholderService.ReplacePlaceholders(titleTemplate, gameInfo, instanceName);
            Logging.Log.Info($"[WindowTitleSetter] Final title: {finalTitle}");

            // 步骤3：查找并设置窗口标题
            var success = await FindAndSetWindowTitleAsync(gameProcess, finalTitle, timeoutSeconds);

            if (success)
            {
                Logging.Log.Info($"[WindowTitleSetter] ✓ Successfully set window title to: {finalTitle}");
            }
            else
            {
                Logging.Log.Warn($"[WindowTitleSetter] ✗ Failed to set window title after {timeoutSeconds}s");
            }
        });
    }

    /// <summary>
    /// 等待游戏窗口出现（不设置标题）
    /// </summary>
    public static async Task WaitForGameWindowAsync(Process gameProcess, string? gamePath = null, int timeoutSeconds = 30)
    {
        await Task.Run(async () =>
        {
            Logging.Log.Info($"[WindowTitleSetter] Waiting for game window to appear (PID: {gameProcess.Id})...");

            // 等待游戏加载完成
            await WaitForGameLoadAsync(gameProcess, gamePath, timeoutSeconds);

            // 等待窗口出现
            var found = false;
            var attemptCount = 0;
            var searchEndTime = DateTime.Now + TimeSpan.FromSeconds(10);

            while (DateTime.Now < searchEndTime && !found)
            {
                attemptCount++;

                EnumWindows((hWnd, lParam) =>
                {
                    _ = GetWindowThreadProcessId(hWnd, out uint windowProcessId);

                    if (windowProcessId == gameProcess.Id && IsWindowVisible(hWnd))
                    {
                        var builder = new StringBuilder(256);
                        GetWindowText(hWnd, builder, 256);
                        var title = builder.ToString();

                        if (!string.IsNullOrEmpty(title) && title.Contains("Stardew Valley"))
                        {
                            Logging.Log.Info($"[WindowTitleSetter] ✓ Game window found: {title}");
                            found = true;
                            return false;
                        }
                    }

                    return true;
                }, IntPtr.Zero);

                if (found)
                {
                    break;
                }

                await Task.Delay(500);
            }

            if (!found)
            {
                Logging.Log.Warn($"[WindowTitleSetter] ✗ Game window not found after {attemptCount} attempts");
            }
        });
    }

    /// <summary>
    /// 等待游戏加载完成（从文件系统读取游戏信息）
    /// </summary>
    private static async Task<WindowTitlePlaceholderService.GameInfo> WaitForGameLoadAsync(Process gameProcess, string? gamePath, int timeoutSeconds)
    {
        // 直接从文件系统读取游戏信息
        var gameInfo = WindowTitlePlaceholderService.GetGameInfo(gamePath);

        // 等待游戏进程启动并创建窗口
        try
        {
            Logging.Log.Info("[WindowTitleSetter] Waiting for game to start...");

            // 等待进程响应（检查进程是否仍在运行）
            var waitStart = DateTime.Now;
            var processResponded = false;

            while (DateTime.Now - waitStart < TimeSpan.FromSeconds(timeoutSeconds))
            {
                if (!gameProcess.HasExited)
                {
                    processResponded = true;
                    break;
                }
                await Task.Delay(100);
            }

            if (processResponded)
            {
                Logging.Log.Info("[WindowTitleSetter] Game process started, waiting for window creation...");
                // 等待窗口创建
                await Task.Delay(3000);
            }
            else
            {
                Logging.Log.Warn($"[WindowTitleSetter] Game process exited quickly, may have failed to start");
            }
        }
        catch (Exception ex)
        {
            Logging.Log.Error(ex, "[WindowTitleSetter] Error waiting for game process");
        }

        return gameInfo;
    }

    /// <summary>
    /// 查找并设置窗口标题
    /// </summary>
    private static async Task<bool> FindAndSetWindowTitleAsync(Process gameProcess, string customTitle, int timeoutSeconds)
    {
        var found = false;
        var attemptCount = 0;
        var searchEndTime = DateTime.Now + TimeSpan.FromSeconds(timeoutSeconds);

        while (DateTime.Now < searchEndTime && !found)
        {
            attemptCount++;

            if (attemptCount == 1 || attemptCount % 5 == 0)
            {
                Logging.Log.Info($"[WindowTitleSetter] Searching for game window... (Attempt #{attemptCount})");
            }

            EnumWindows((hWnd, lParam) =>
            {
                _ = GetWindowThreadProcessId(hWnd, out uint windowProcessId);

                // 只处理属于游戏进程的可见窗口
                if (windowProcessId == gameProcess.Id && IsWindowVisible(hWnd))
                {
                    var builder = new StringBuilder(256);
                    GetWindowText(hWnd, builder, 256);
                    var title = builder.ToString();

                    if (!string.IsNullOrEmpty(title))
                    {
                        Logging.Log.Info($"[WindowTitleSetter] Found window (PID: {windowProcessId}): {title}");

                        // 检查标题是否包含 "Stardew Valley"（兼容原版和 SMAPI）
                        if (title.Contains("Stardew Valley"))
                        {
                            Logging.Log.Info($"[WindowTitleSetter] → Setting window title: '{title}' -> '{customTitle}'");
                            var result = SetWindowText(hWnd, customTitle);
                            Logging.Log.Info($"[WindowTitleSetter] SetWindowText result: {result}");
                            found = true;
                            return false; // 停止枚举
                        }
                    }
                }

                return true; // 继续枚举
            }, IntPtr.Zero);

            if (found)
            {
                break;
            }

            await Task.Delay(1000); // 等待1秒后重试
        }

        return found;
    }
}
