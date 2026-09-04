using SVL.Core.Platform.Abstractions;
using System.Runtime.InteropServices;

namespace SVL.Core.Platform.Services;

public sealed class FileExplorerService : IFileExplorerService
{
    private readonly IExternalProcessService _processService;

    public FileExplorerService(IExternalProcessService processService)
    {
        _processService = processService;
    }

    public bool TryOpenFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            return false;
        }

        return _processService.TryOpenPath(folderPath);
    }

    public bool TryRevealFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return _processService.RunCommand("explorer.exe", $"/select,\"{filePath}\"") == 0;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return _processService.RunCommand("open", $"-R \"{filePath}\"") == 0;
        }

        // Linux 下退化为打开文件所在目录。
        var directory = Path.GetDirectoryName(filePath);
        return !string.IsNullOrWhiteSpace(directory) && TryOpenFolder(directory);
    }
}
