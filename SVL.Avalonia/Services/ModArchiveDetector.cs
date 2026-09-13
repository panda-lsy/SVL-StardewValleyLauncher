using ICSharpCode.SharpZipLib.Zip;

namespace SVL.Avalonia.Services;

/// <summary>
/// Avalonia 端的本地 Mod 来源检测器。
/// 只判断是否存在 manifest.json，不把 CurseForge/SVL 整合包的清单误当成 Mod；
/// 整合包类型由 SVL.Core.Modpack.ModpackTypeDetector 单独判断。
/// </summary>
public static class ModArchiveDetector
{
    public static bool LooksLikeModInstallSource(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (Directory.Exists(path))
        {
            try
            {
                return Directory.EnumerateFiles(path, "manifest.json", SearchOption.AllDirectories).Any();
            }
            catch
            {
                return false;
            }
        }

        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            if (string.Equals(Path.GetExtension(path), ".7z", StringComparison.OrdinalIgnoreCase))
            {
                using var sevenZip = SharpCompress.Archives.SevenZip.SevenZipArchive.OpenArchive(path);
                return sevenZip.Entries.Any(entry =>
                    !entry.IsDirectory &&
                    Path.GetFileName((entry.Key ?? string.Empty).Replace('\\', '/'))
                        .Equals("manifest.json", StringComparison.OrdinalIgnoreCase));
            }

            if (!string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            using var zipFile = new ZipFile(path);
            foreach (ZipEntry entry in zipFile)
            {
                if (entry.IsFile &&
                    entry.Name.Replace('\\', '/')
                        .EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // 损坏归档会由安装器给出明确的失败提示。
        }

        return false;
    }
}
