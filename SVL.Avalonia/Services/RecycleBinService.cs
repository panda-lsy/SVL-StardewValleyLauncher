using System.Runtime.InteropServices;

namespace SVL.Avalonia.Services;

/// <summary>
/// 将用户主动删除的文件/目录放入系统回收站。
///
/// 这是一个独立的基础服务，避免各个 ViewModel 直接调用 Directory.Delete。
/// Windows 使用 Shell 的 FOF_ALLOWUNDO，因此 Explorer 可以正常显示并恢复；
/// 其它平台没有统一的系统回收站时，退回到应用数据目录下的可恢复暂存区，
/// 绝不把用户的删除操作变成不可恢复的物理删除。
/// </summary>
public static class RecycleBinService
{
    private const uint FileOperationDelete = 0x0003;
    private const ushort AllowUndo = 0x0040;
    private const ushort NoConfirmation = 0x0010;
    private const ushort Silent = 0x0004;
    private const ushort NoErrorUi = 0x0400;

    public static bool TryMoveToRecycleBin(string path, out string message)
    {
        message = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            message = "删除路径为空";
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            message = "目标不存在";
            return true;
        }

        if (OperatingSystem.IsWindows())
        {
            return TryMoveWindows(fullPath, out message);
        }

        return TryMoveToRecoveryDirectory(fullPath, out message);
    }

    private static bool TryMoveWindows(string path, out string message)
    {
        message = string.Empty;
        try
        {
            // pFrom 必须是双 NUL 结尾的多字符串，即使这里只传一个路径也不能省略。
            var operation = new ShellFileOperationInfo
            {
                WindowHandle = IntPtr.Zero,
                Function = FileOperationDelete,
                From = path + "\0\0",
                To = string.Empty,
                Flags = (ushort)(AllowUndo | NoConfirmation | Silent | NoErrorUi)
            };

            var result = SHFileOperation(ref operation);
            if (result != 0 || File.Exists(path) || Directory.Exists(path))
            {
                message = result == 0
                    ? "系统回收站未接受该路径"
                    : $"系统回收站操作失败（错误码 {result}）";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    private static bool TryMoveToRecoveryDirectory(string path, out string message)
    {
        message = string.Empty;
        try
        {
            var recoveryRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SVL", "Avalonia", "RecycleBin");
            Directory.CreateDirectory(recoveryRoot);

            var leaf = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var destination = Path.Combine(recoveryRoot, $"{DateTime.Now:yyyyMMdd_HHmmss}_{leaf}_{Guid.NewGuid():N}");
            if (Directory.Exists(path))
            {
                Directory.Move(path, destination);
            }
            else
            {
                File.Move(path, destination);
            }

            message = $"已移入应用回收站：{destination}";
            return true;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileOperationInfo
    {
        public IntPtr WindowHandle;
        public uint Function;
        public string From;
        public string To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool AnyOperationAborted;
        public IntPtr NameMappings;
        public string ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShellFileOperationInfo operation);
}
