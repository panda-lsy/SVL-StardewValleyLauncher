using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SVL.Core.Logging;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Readers;

namespace SVL.Core.IO;

/// <summary>
/// 7z 压缩服务 - 使用命令行 7z 工具解压文件
/// </summary>
public static class SevenZipService
{
    private const string SevenZipExecutable = "7z.exe";

    /// <summary>
    /// 检查系统是否安装了 7-Zip
    /// </summary>
    public static bool IsSevenZipInstalled()
    {
        try
        {
            // 检查常见安装路径
            var possiblePaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe")
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    return true;
                }
            }

            // 检查 PATH 环境变量
            try
            {
                var processStartInfo = new ProcessStartInfo
                {
                    FileName = SevenZipExecutable,
                    Arguments = "-h",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(processStartInfo);
                if (process != null)
                {
                    process.WaitForExit();
                    return process.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 解压 7z 文件到指定目录
    /// </summary>
    /// <param name="archivePath">7z 文件路径</param>
    /// <param name="extractDir">解压目标目录</param>
    /// <returns>解压是否成功</returns>
    public static bool ExtractArchive(string archivePath, string extractDir)
    {
        try
        {
            if (!File.Exists(archivePath))
            {
                Log.Warn($"[SevenZip] 文件不存在: {archivePath}");
                return false;
            }

            // 确保目标目录存在
            Directory.CreateDirectory(extractDir);

            // 查找 7z.exe 路径
            var sevenZipPath = FindSevenZipExecutable();
            if (string.IsNullOrEmpty(sevenZipPath))
            {
                Log.Warn("[SevenZip] 未找到 7z.exe");
                return false;
            }

            Log.Info($"[SevenZip] 开始解压: {archivePath} -> {extractDir}");

            var processStartInfo = new ProcessStartInfo
            {
                FileName = sevenZipPath,
                Arguments = $"x \"{archivePath}\" -o\"{extractDir}\" -y",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process();
            process.StartInfo = processStartInfo;
            process.Start();

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();

            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                Log.Warn($"[SevenZip] 解压失败 (退出码: {process.ExitCode})");
                Log.Warn($"[SevenZip] 错误输出: {error}");
                return false;
            }

            Log.Info($"[SevenZip] 解压成功: {extractDir}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SevenZip] 解压异常");
            return false;
        }
    }

    /// <summary>
    /// 获取 7z.exe 的完整路径
    /// </summary>
    private static string? FindSevenZipExecutable()
    {
        // 检查常见安装路径
        var possiblePaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe"),
            "7z.exe"  // 从 PATH 环境变量查找
        };

        foreach (var path in possiblePaths)
        {
            if (File.Exists(path))
            {
                return path;
            }

            // 对于相对路径，尝试从环境变量查找
            if (path == SevenZipExecutable)
            {
                try
                {
                    var processStartInfo = new ProcessStartInfo
                    {
                        FileName = "where",
                        Arguments = SevenZipExecutable,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(processStartInfo);
                    if (process != null)
                    {
                        var output = process.StandardOutput.ReadToEnd();
                        process.WaitForExit();

                        if (!string.IsNullOrWhiteSpace(output) && process.ExitCode == 0)
                        {
                            var firstLine = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                            if (!string.IsNullOrEmpty(firstLine) && File.Exists(firstLine))
                            {
                                return firstLine;
                            }
                        }
                    }
                }
                catch
                {
                    // 忽略错误
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 尝试作为 ZIP 文件解压（使用 SharpZipLib）
    /// </summary>
    public static bool TryExtractAsZip(string archivePath, string extractDir)
    {
        try
        {
            Log.Info($"[SevenZip] 尝试作为 ZIP 解压: {archivePath}");

            using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read);
            using var zipStream = new ICSharpCode.SharpZipLib.Zip.ZipInputStream(stream);

            ICSharpCode.SharpZipLib.Zip.ZipEntry entry;
            while ((entry = zipStream.GetNextEntry()) != null)
            {
                if (!TryGetSafeExtractionPath(extractDir, entry.Name, out var entryPath))
                {
                    Log.Warn($"[SevenZip] ZIP 包含越界路径: {entry.Name}");
                    return false;
                }

                if (entry.IsDirectory)
                {
                    Directory.CreateDirectory(entryPath);
                }
                else
                {
                    var entryDir = Path.GetDirectoryName(entryPath);
                    if (!string.IsNullOrEmpty(entryDir) && !Directory.Exists(entryDir))
                    {
                        Directory.CreateDirectory(entryDir);
                    }

                    using var fs = new FileStream(entryPath, FileMode.Create, FileAccess.Write);
                    var buffer = new byte[4096];
                    int bytesRead;
                    while ((bytesRead = zipStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        fs.Write(buffer, 0, bytesRead);
                    }
                }
            }

            Log.Info($"[SevenZip] ZIP 解压成功: {extractDir}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug($"[SevenZip] ZIP 解压失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 尝试作为 7z 文件解压（使用 SharpCompress - 内置支持）
    /// </summary>
    public static bool TryExtractAs7z(string archivePath, string extractDir)
    {
        try
        {
            Log.Info($"[SevenZip] 尝试作为 7z 解压: {archivePath}");

            // 确保目标目录存在
            Directory.CreateDirectory(extractDir);

            // SharpCompress 0.48+ 将 Open 重命名为 OpenArchive，并要求显式
            // 传入 ReaderOptions；保留这里的手动边界检查，避免归档路径越界。
            using (var archive = SevenZipArchive.OpenArchive(archivePath, new ReaderOptions()))
            {
                foreach (var entry in archive.Entries)
                {
                    if (!TryGetSafeExtractionPath(extractDir, entry.Key, out var entryPath))
                    {
                        Log.Warn($"[SevenZip] 7z 包含越界路径: {entry.Key}");
                        return false;
                    }

                    if (entry.IsDirectory)
                    {
                        Directory.CreateDirectory(entryPath);
                    }
                    else
                    {
                        var entryDir = Path.GetDirectoryName(entryPath);
                        if (!string.IsNullOrEmpty(entryDir) && !Directory.Exists(entryDir))
                        {
                            Directory.CreateDirectory(entryDir);
                        }

                        // 写入文件
                        entry.WriteToFile(entryPath);
                    }
                }
            }

            Log.Info($"[SevenZip] 7z 解压成功: {extractDir}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug($"[SevenZip] 7z 解压失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 智能解压 - 自动尝试 SharpCompress 7z、ZIP 格式，最后尝试命令行 7z.exe
    /// </summary>
    public static bool Extract(string archivePath, string extractDir)
    {
        Log.Info($"[SevenZip] 智能解压: {archivePath}");

        // 首先尝试 7z 格式（使用内置 SharpCompress）
        if (TryExtractAs7z(archivePath, extractDir))
        {
            return true;
        }

        // 如果 7z 失败，尝试 ZIP 格式
        if (TryExtractAsZip(archivePath, extractDir))
        {
            return true;
        }

        // 如果 SharpCompress 全部失败，尝试使用命令行 7z.exe
        Log.Info("[SevenZip] SharpCompress 解压失败，尝试使用命令行 7z.exe...");
        if (ExtractArchive(archivePath, extractDir))
        {
            return true;
        }

        Log.Error("[SevenZip] 所有解压方式都失败");
        return false;
    }

    /// <summary>
    /// 将归档条目解析到目标目录内，拒绝绝对路径和 .. 越界路径。
    /// 旧 WPF 解压链不能依赖 SharpCompress 的 WriteToDirectory 内部保护，
    /// 因为这里使用的是逐条目写入。
    /// </summary>
    private static bool TryGetSafeExtractionPath(
        string extractDir,
        string? entryName,
        out string entryPath)
    {
        entryPath = string.Empty;
        if (string.IsNullOrWhiteSpace(entryName))
        {
            return false;
        }

        try
        {
            var destinationRoot = Path.GetFullPath(extractDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var normalizedEntryName = entryName
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(destinationRoot, normalizedEntryName));
            if (!candidate.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            entryPath = candidate;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"[SevenZip] 无法解析归档路径 {entryName}: {ex.Message}");
            return false;
        }
    }
}
