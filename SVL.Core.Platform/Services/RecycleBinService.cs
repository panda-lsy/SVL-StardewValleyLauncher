using System.Runtime.InteropServices;

namespace SVL.Core.Platform.Services;

/// <summary>
/// 将需由用户恢复的旧文件/目录移入系统回收站；没有平台回收站 API 时，
/// 移入 SVL 的可恢复目录。临时文件仍应由调用方直接清理。
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

        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                return true;
            }

            return OperatingSystem.IsWindows()
                ? TryMoveWindows(fullPath, out message)
                : TryMoveToRecoveryDirectory(fullPath, GetRecoveryRootPath(), out message);
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    internal static string GetRecoveryRootPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SVL", "Avalonia", "RecycleBin");
    }

    internal static bool TryMoveToRecoveryDirectory(string path, string recoveryRoot, out string message)
    {
        message = string.Empty;
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                return true;
            }

            Directory.CreateDirectory(recoveryRoot);
            var leaf = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var destination = Path.Combine(
                recoveryRoot,
                $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{leaf}_{Guid.NewGuid():N}");

            if (Directory.Exists(fullPath))
            {
                Directory.Move(fullPath, destination);
            }
            else
            {
                File.Move(fullPath, destination);
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

    private static bool TryMoveWindows(string path, out string message)
    {
        message = string.Empty;
        try
        {
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
