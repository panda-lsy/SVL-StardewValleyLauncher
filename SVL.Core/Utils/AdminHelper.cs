using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.ComponentModel;
using SVL.Core.Logging;

namespace SVL.Core.Utils;

/// <summary>
/// 管理员权限辅助工具
/// </summary>
public static class AdminHelper
{
    [DllImport("shell32.dll")]
    private static extern bool IsUserAnAdmin();

    /// <summary>
    /// 检查当前进程是否以管理员身份运行
    /// </summary>
    public static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            Log.Warn("[AdminHelper] 检查管理员权限失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 重启应用并提升到管理员权限
    /// </summary>
    /// <param name="args">命令行参数</param>
    public static void RestartAsAdmin(string[] args = null)
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule.FileName;
            var startInfo = new ProcessStartInfo
            {
                UseShellExecute = true,
                Verb = "runas",  // 请求提升权限
                FileName = exePath,
                Arguments = args != null && args.Length > 0 ? string.Join(" ", args) : ""
            };

            Log.Info($"[AdminHelper] 重启应用并提升权限: {exePath}");
            Process.Start(startInfo);
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[AdminHelper] 重启应用失败");
            throw new Exception("无法重启应用以提升权限", ex);
        }
    }

    /// <summary>
    /// 检查并请求管理员权限（如果需要）
    /// </summary>
    /// <param name="forceRequest">是否强制请求（即使不需要也提升）</param>
    /// <returns>是否拥有管理员权限</returns>
    public static bool EnsureAdminPrivileges(bool forceRequest = false)
    {
        if (IsRunningAsAdmin())
        {
            Log.Info("[AdminHelper] ✓ 已具有管理员权限");
            return true;
        }

        if (forceRequest)
        {
            Log.Info("[AdminHelper] 请求管理员权限并重启应用");
            RestartAsAdmin();
            // 此方法不会返回，因为应用会重启
        }

        return false;
    }

    /// <summary>
    /// 检查是否可以创建符号链接（需要管理员权限或开发者模式）
    /// </summary>
    public static bool CanCreateSymbolicLinks()
    {
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);

            var tempFile = Path.Combine(tempDir, "test.txt");
            File.WriteAllText(tempFile, "test");

            var linkFile = Path.Combine(tempDir, "link.txt");

            // 尝试创建文件符号链接
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c mklink \"{linkFile}\" \"{tempFile}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            process.Start();
            process.WaitForExit();

            // 清理
            try
            {
                if (File.Exists(linkFile))
                    File.Delete(linkFile);
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
                Directory.Delete(tempDir);
            }
            catch { }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
