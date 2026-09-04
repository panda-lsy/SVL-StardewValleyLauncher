using ICSharpCode.SharpZipLib.Zip;
using System.IO.Compression;
using ZipFile = System.IO.Compression.ZipFile;

namespace SVL.Avalonia.Services;

/// <summary>
/// 通用 zip 解压工具。采用 fallback 策略（与旧架构一致）：
/// 1. 先尝试 System.IO.Compression.ZipFile（.NET 内置，兼容性最好）
/// 2. 失败后回退到 SharpZipLib（旧架构使用，对非标准 zip 兼容性更好）
/// </summary>
public static class ZipExtractor
{
    /// <summary>
    /// 解压 zip 文件到目标目录。先尝试 ZipFile，失败后回退到 SharpZipLib。
    /// </summary>
    /// <param name="zipPath">zip 文件路径</param>
    /// <param name="destinationDir">目标目录（需已存在）</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static Task ExtractToDirectoryAsync(string zipPath, string destinationDir, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExtractToDirectory(zipPath, destinationDir);
        }, cancellationToken);
    }

    /// <summary>同步解压 zip 文件到目标目录（用于替换 ZipFile.ExtractToDirectory）。</summary>
    public static void ExtractToDirectory(string zipPath, string destinationDir)
    {
        // .cfmodpack 实际是 zip 格式，.NET ZipFile 对非 .zip 扩展名可能拒绝，
        // 先复制为临时 .zip 文件再解压（参考旧架构做法）
        var effectivePath = zipPath;
        string? tempZipPath = null;
        if (string.Equals(Path.GetExtension(zipPath), ".cfmodpack", StringComparison.OrdinalIgnoreCase))
        {
            tempZipPath = Path.Combine(Path.GetTempPath(), $"svl_{Guid.NewGuid():N}.zip");
            File.Copy(zipPath, tempZipPath, true);
            effectivePath = tempZipPath;
        }

        try
        {
            ExtractInternal(effectivePath, destinationDir);
        }
        finally
        {
            if (tempZipPath != null && File.Exists(tempZipPath))
            {
                try { File.Delete(tempZipPath); } catch { /* best-effort */ }
            }
        }
    }

    private static void ExtractInternal(string zipPath, string destinationDir)
    {
        Exception? firstError = null;
        try
        {
            // 优先使用 .NET 内置 ZipFile（对标准 zip 兼容性最好）
            ZipFile.ExtractToDirectory(zipPath, destinationDir, true);
            return;
        }
        catch (Exception ex)
        {
            firstError = ex;
        }

        // 回退前清空目标目录（ZipFile 可能已解压部分文件）
        CleanDirectory(destinationDir);
        try
        {
            // 回退到 SharpZipLib（旧架构使用，对非标准 zip 兼容性更好）
            ExtractWithSharpZipLib(zipPath, destinationDir);
            return;
        }
        catch (Exception secondError)
        {
            throw new InvalidOperationException(
                $"解压失败（两种方式均失败）: ZipFile 错误=[{firstError.Message}], SharpZipLib 错误=[{secondError.Message}]",
                secondError);
        }
    }

    /// <summary>清空目录内容但保留目录本身（用于回退解压前清理半成品）。</summary>
    private static void CleanDirectory(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                if (Directory.Exists(entry))
                    Directory.Delete(entry, true);
                else
                    File.Delete(entry);
            }
        }
        catch
        {
            // best-effort 清理
        }
    }

    /// <summary>
    /// 使用 SharpZipLib 解压（旧架构的 ExtractZipToTemp 实现）。
    /// SharpZipLib 对非标准 zip 格式兼容性更好，支持 .cfmodpack 等变体。
    /// </summary>
    private static void ExtractWithSharpZipLib(string zipPath, string destinationDir)
    {
        var destinationRoot = Path.GetFullPath(destinationDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var zipFile = new ICSharpCode.SharpZipLib.Zip.ZipFile(zipPath);
        foreach (ICSharpCode.SharpZipLib.Zip.ZipEntry entry in zipFile)
        {
            if (entry.IsDirectory)
                continue;

            var relativePath = entry.Name.Replace('/', Path.DirectorySeparatorChar);
            var destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, relativePath));
            if (!destinationPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"压缩包包含越界路径: {entry.Name}");
            }

            var destinationDirForEntry = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationDirForEntry) && !Directory.Exists(destinationDirForEntry))
            {
                Directory.CreateDirectory(destinationDirForEntry);
            }

            using var stream = zipFile.GetInputStream(entry);
            using var fileStream = File.Create(destinationPath);
            stream.CopyTo(fileStream);
        }
    }
}
