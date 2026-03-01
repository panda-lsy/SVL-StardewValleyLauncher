using System.Diagnostics;
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
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
    }
}
