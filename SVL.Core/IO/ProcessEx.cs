using System.Diagnostics;
using System;
using System.IO;
using System.Threading.Tasks;

namespace SVL.Core.IO
{
    /// <summary>
    /// .NET Framework Process 辅助类
    /// 提供 .NET Core+ 风格的异步 API
    /// </summary>
    public static class ProcessEx
    {
        public static Task WaitForExitAsync(this Process process)
        {
            var tcs = new TaskCompletionSource<bool>();
            process.EnableRaisingEvents = true;
            process.Exited += (s, e) => tcs.TrySetResult(true);
            if (process.HasExited)
            {
                tcs.TrySetResult(true);
            }
            return tcs.Task;
        }

        /// <summary>
        /// 使用系统默认浏览器打开 URL（或使用 Shell 打开任意路径/协议）
        /// </summary>
        public static void OpenUrl(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                var browserPaths = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mozilla Firefox", "firefox.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Mozilla Firefox", "firefox.exe")
                };

                foreach (var browserPath in browserPaths)
                {
                    if (!File.Exists(browserPath))
                        continue;

                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = browserPath,
                            Arguments = url,
                            UseShellExecute = false
                        });
                        return;
                    }
                    catch
                    {
                        // 尝试下一个浏览器
                    }
                }
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
    }
}
