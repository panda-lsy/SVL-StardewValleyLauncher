using System.Diagnostics;

namespace SVL.Avalonia.Services;

/// <summary>
/// 清理版本删除过程中因文件锁而暂存的 .svl-delete-* 目录。
/// 只接受明确位于 gamePath/versions 下且带有启动器前缀的目录，避免把用户目录
/// 或其它版本误当成清理目标。
/// </summary>
public static class DeferredVersionDirectoryCleanup
{
    private const string DeferredPrefix = ".svl-delete-";

    public static int TryCleanup(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            return 0;
        }

        try
        {
            var versionsPath = Path.Combine(Path.GetFullPath(gamePath), "versions");
            if (!Directory.Exists(versionsPath))
            {
                return 0;
            }

            var cleaned = 0;
            foreach (var candidate in Directory.GetDirectories(versionsPath, DeferredPrefix + "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(candidate);
                if (string.IsNullOrWhiteSpace(name) ||
                    !name.StartsWith(DeferredPrefix, StringComparison.OrdinalIgnoreCase) ||
                    !IsDirectChildOf(candidate, versionsPath))
                {
                    continue;
                }

                if (TryDeleteDeferredDirectory(candidate))
                {
                    cleaned++;
                }
            }

            return cleaned;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DeferredCleanup] 扫描版本清理目录失败: {ex.Message}");
            return 0;
        }
    }

    private static bool TryDeleteDeferredDirectory(string path)
    {
        try
        {
            // 版本运行目录通常包含 game junction。先只移除 junction 本身，
            // 防止递归删除把 Base 游戏目录当成版本内容处理。
            foreach (var directory in Directory.GetDirectories(path, "*", SearchOption.TopDirectoryOnly))
            {
                if (!IsReparsePoint(directory))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(directory, recursive: false);
                }
                catch
                {
                    // 仍被占用时让最终删除失败并保留目录，下一次再试。
                }
            }

            ClearAttributes(path);
            if (!RecycleBinService.TryMoveToRecycleBin(path, out var recycleMessage))
            {
                Debug.WriteLine($"[DeferredCleanup] 暂时无法将版本目录移入回收站: {path}, {recycleMessage}");
                return false;
            }

            Debug.WriteLine($"[DeferredCleanup] 已将延后删除版本目录移入回收站: {path}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DeferredCleanup] 暂时无法清理 {path}: {ex.Message}");
            return false;
        }
    }

    private static void ClearAttributes(string root)
    {
        try
        {
            File.SetAttributes(root, FileAttributes.Directory);

            foreach (var file in Directory.GetFiles(root, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch
                {
                    // 单个文件失败不阻断其它清理项。
                }
            }

            foreach (var directory in Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly))
            {
                if (IsReparsePoint(directory))
                {
                    continue;
                }

                try
                {
                    File.SetAttributes(directory, FileAttributes.Directory);
                }
                catch
                {
                    // best-effort
                }

                ClearAttributes(directory);
            }
        }
        catch
        {
            // 最终 Directory.Delete 会再次给出准确结果。
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDirectChildOf(string path, string parent)
    {
        try
        {
            var fullPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullParent = Path.GetFullPath(parent)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(Path.GetDirectoryName(fullPath), fullParent, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
