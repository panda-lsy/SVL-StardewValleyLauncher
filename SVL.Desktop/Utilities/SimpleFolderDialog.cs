using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace SVL.Desktop.Utilities;

/// <summary>
/// 支持直接输入路径的文件夹选择对话框
/// 使用 SHBrowseForFolder 实现，Vista+ 支持直接输入路径
/// </summary>
public class SimpleFolderDialog : IDisposable
{
    [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SHBrowseForFolder(ref BROWSEINFO bi);

    [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SHGetPathFromIDList(IntPtr pidl, StringBuilder pszPath);

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SHGetMalloc([Out] out IMalloc malloc);

    [ComImport]
    [Guid("00000002-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMalloc
    {
        void Alloc(IntPtr cb);
        void Free(IntPtr pv);
        void DidAlloc(IntPtr pv);
        void HeapMinimize();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct BROWSEINFO
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public IntPtr pszDisplayName;
        public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    // BROWSEINFO.ulFlags 标志
    private const int BIF_RETURNONLYFSDIRS = 0x0001;  // 只返回文件系统目录
    private const int BIF_DONTGOBELOWDOMAIN = 0x0002; // 不包含域级别网络文件夹
    private const int BIF_EDITBOX = 0x0010;           // 显示编辑框，允许用户输入路径
    private const int BIF_NEWDIALOGSTYLE = 0x0040;    // 新式对话框（Vista+）
    private const int BIF_NONEWFOLDERBUTTON = 0x0200; // 不显示"新建文件夹"按钮

    private string _title = "选择文件夹";
    private string _selectedPath = string.Empty;

    public string Title
    {
        get => _title;
        set => _title = value ?? "选择文件夹";
    }

    public string SelectedPath => _selectedPath;

    /// <summary>
    /// 显示文件夹选择对话框
    /// </summary>
    public bool ShowDialog()
    {
        var bi = new BROWSEINFO();
        var displayName = new StringBuilder(260);
        IntPtr pidlRet = IntPtr.Zero;

        try
        {
            bi.hwndOwner = IntPtr.Zero;
            bi.pidlRoot = IntPtr.Zero;
            bi.pszDisplayName = Marshal.AllocHGlobal(260 * Marshal.SystemDefaultCharSize);
            bi.lpszTitle = _title;
            // 使用新式对话框样式 + 编辑框，支持直接输入路径
            bi.ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE | BIF_EDITBOX;
            bi.lpfn = IntPtr.Zero;
            bi.lParam = IntPtr.Zero;
            bi.iImage = 0;

            // 显示对话框
            pidlRet = SHBrowseForFolder(ref bi);

            if (pidlRet != IntPtr.Zero)
            {
                var path = new StringBuilder(260);
                if (SHGetPathFromIDList(pidlRet, path))
                {
                    _selectedPath = path.ToString();
                    return true;
                }
            }
            return false;
        }
        finally
        {
            // 释放分配的内存
            if (bi.pszDisplayName != IntPtr.Zero)
                Marshal.FreeHGlobal(bi.pszDisplayName);

            if (pidlRet != IntPtr.Zero)
            {
                // 获取 IMalloc 接口并释放 PIDL
                SHGetMalloc(out var malloc);
                malloc.Free(pidlRet);
            }
        }
    }

    public void Dispose()
    {
        // 没有需要持久化的非托管资源
    }
}
