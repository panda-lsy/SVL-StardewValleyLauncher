using System;
using System.Diagnostics;
using Microsoft.Win32;
using SVL.Core.Logging;

namespace SVL.Core.App;

/// <summary>
/// NXM 协议注册服务
/// 负责在 Windows 注册表中注册 nxm:// 协议，使浏览器能够正确调用应用程序
/// </summary>
[LifecycleService(LifecycleState.Loading, Priority = 100)]
[LifecycleScope("nxm-protocol", "NXM 协议注册")]
public sealed partial class NxmProtocolService
{
    private const string PROTOCOL_NAME = "nxm";
    private const string PROTOCOL_LABEL = "URL:Nexus Mods Protocol";

    [LifecycleStart]
    private static void RegisterProtocol()
    {
        string? exePath = null;
        try
        {
            // 使用 try-catch 包裹整个方法，确保任何错误都能被捕获
            exePath = GetExecutablePath();

            if (string.IsNullOrEmpty(exePath))
            {
                // 如果 Log 还没准备好，使用 Debug.WriteLine
                try { Log.Warn("[NxmProtocolService] 无法获取可执行文件路径"); }
                catch { System.Diagnostics.Debug.WriteLine("[NxmProtocolService] 无法获取可执行文件路径"); }
                return;
            }

            try { Log.Info($"[NxmProtocolService] 正在注册 NXM 协议: {exePath}"); }
            catch { System.Diagnostics.Debug.WriteLine($"[NxmProtocolService] 正在注册 NXM 协议: {exePath}"); }

            using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{PROTOCOL_NAME}"))
            {
                if (key == null)
                {
                    try { Log.Error("[NxmProtocolService] 无法创建注册表键"); }
                    catch { System.Diagnostics.Debug.WriteLine("[NxmProtocolService] 无法创建注册表键"); }
                    return;
                }

                // 设置默认值和 URL Protocol 标记
                key.SetValue("", PROTOCOL_LABEL);
                key.SetValue("URL Protocol", "");

                // 设置默认图标
                using (var iconKey = key.CreateSubKey("DefaultIcon"))
                {
                    iconKey?.SetValue("", $"{exePath},0");
                }

                // 设置打开命令
                using (var cmdKey = key.CreateSubKey(@"shell\open\command"))
                {
                    cmdKey?.SetValue("", $"\"{exePath}\" \"%1\"");
                }
            }

            try { Log.Info("[NxmProtocolService] ✓ NXM 协议注册成功"); }
            catch { System.Diagnostics.Debug.WriteLine("[NxmProtocolService] ✓ NXM 协议注册成功"); }
        }
        catch (Exception ex)
        {
            // 双重保险：如果 Log 失败，使用 Debug.WriteLine
            try
            {
                Log.Error(ex, "[NxmProtocolService] 注册 NXM 协议失败");
            }
            catch
            {
                System.Diagnostics.Debug.WriteLine($"[NxmProtocolService] 注册 NXM 协议失败: {ex.Message}\n{ex.StackTrace}");
            }
        }
        finally
        {
            // 无论成功失败，都输出调试信息
            System.Diagnostics.Debug.WriteLine($"[NxmProtocolService] 注册流程完成, exePath={exePath}");
        }
    }

    [LifecycleStop]
    private static void UnregisterProtocol()
    {
        // 可选：在应用退出时卸载协议
        // 通常不需要，因为其他应用可能也使用此协议
    }

    /// <summary>
    /// 获取当前可执行文件的完整路径
    /// </summary>
    private static string? GetExecutablePath()
    {
        try
        {
            // 方法1：获取当前进程的主模块路径（最可靠）
            Process process = null;
            try
            {
                process = Process.GetCurrentProcess();
                var mainModule = process.MainModule;

                if (mainModule != null && !string.IsNullOrEmpty(mainModule.FileName))
                {
                    try { Log.Info($"[NxmProtocolService] 使用进程主模块路径: {mainModule.FileName}"); }
                    catch { System.Diagnostics.Debug.WriteLine($"[NxmProtocolService] 使用进程主模块路径: {mainModule.FileName}"); }
                    return mainModule.FileName;
                }
            }
            finally
            {
                process?.Dispose();
            }

            // 方法2：使用入口程序集的 location（可能是 Desktop 项目的 exe）
            var entryAssembly = System.Reflection.Assembly.GetEntryAssembly();
            if (entryAssembly != null && !string.IsNullOrEmpty(entryAssembly.Location))
            {
                try { Log.Info($"[NxmProtocolService] 使用入口程序集路径: {entryAssembly.Location}"); }
                catch { System.Diagnostics.Debug.WriteLine($"[NxmProtocolService] 使用入口程序集路径: {entryAssembly.Location}"); }
                return entryAssembly.Location;
            }

            // 方法3：使用执行中的程序集（Core 项目，不推荐）
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var location = assembly.Location;

            if (!string.IsNullOrEmpty(location))
            {
                try { Log.Warn($"[NxmProtocolService] 使用执行程序集路径（可能不正确）: {location}"); }
                catch { System.Diagnostics.Debug.WriteLine($"[NxmProtocolService] 使用执行程序集路径（可能不正确）: {location}"); }

                // 将 DLL 路径转换为可能的 exe 路径
                var exePath = location.Replace(".dll", ".exe");
                if (System.IO.File.Exists(exePath))
                {
                    try { Log.Info($"[NxmProtocolService] 找到对应的 exe: {exePath}"); }
                    catch { System.Diagnostics.Debug.WriteLine($"[NxmProtocolService] 找到对应的 exe: {exePath}"); }
                    return exePath;
                }
                return location;
            }

            try { Log.Error("[NxmProtocolService] 所有方法都无法获取可执行文件路径"); }
            catch { System.Diagnostics.Debug.WriteLine("[NxmProtocolService] 所有方法都无法获取可执行文件路径"); }

            return null;
        }
        catch (Exception ex)
        {
            try { Log.Error(ex, "[NxmProtocolService] 获取可执行文件路径失败"); }
            catch { System.Diagnostics.Debug.WriteLine($"[NxmProtocolService] 获取可执行文件路径失败: {ex.Message}"); }
            return null;
        }
    }

    /// <summary>
    /// 检查 NXM 协议是否已注册
    /// </summary>
    public static bool IsProtocolRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{PROTOCOL_NAME}");
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 获取当前注册的命令行
    /// </summary>
    public static string? GetRegisteredCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{PROTOCOL_NAME}\shell\open\command");
            var value = key?.GetValue("");
            return value?.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 手动注册 NXM 协议（可供外部调用）
    /// </summary>
    public static bool ManualRegister()
    {
        try
        {
            Log.Info("[NxmProtocolService] 手动注册 NXM 协议");
            RegisterProtocol();
            return IsProtocolRegistered();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NxmProtocolService] 手动注册失败");
            return false;
        }
    }

    /// <summary>
    /// 获取注册信息（用于调试）
    /// </summary>
    public static string GetRegistrationInfo()
    {
        try
        {
            var isRegistered = IsProtocolRegistered();
            var command = GetRegisteredCommand();

            return $"已注册: {isRegistered}\n命令行: {command ?? "(未设置)"}";
        }
        catch (Exception ex)
        {
            return $"错误: {ex.Message}";
        }
    }
}
