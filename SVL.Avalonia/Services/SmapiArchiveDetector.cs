using System.IO.Compression;

namespace SVL.Avalonia.Services;

/// <summary>只读识别 SMAPI 安装包，不能改写用户拖入的原始归档。</summary>
internal static class SmapiArchiveDetector
{
    // 官方 double-zipped 包只有一层嵌套。限制解压后的内层大小，避免拖入异常归档时耗尽内存。
    private const long MaxNestedArchiveBytes = 128L * 1024 * 1024;

    internal static bool IsSmapiInstaller(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
            !string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            using var stream = File.OpenRead(path);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            if (HasInstallData(archive)) return true;

            var entries = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToList();
            if (entries.Count != 1 || !entries[0].Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                entries[0].Length <= 0 || entries[0].Length > MaxNestedArchiveBytes)
                return false;

            using var nestedStream = entries[0].Open();
            using var buffer = new MemoryStream((int)entries[0].Length);
            var chunk = new byte[81920];
            int count;
            while ((count = nestedStream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + count > MaxNestedArchiveBytes) return false;
                buffer.Write(chunk, 0, count);
            }
            if (buffer.Length != entries[0].Length) return false;
            buffer.Position = 0;
            using var nested = new ZipArchive(buffer, ZipArchiveMode.Read);
            return HasInstallData(nested);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool HasInstallData(ZipArchive archive) => archive.Entries.Any(entry =>
    {
        var parts = entry.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 &&
               parts[^1].Equals("install.dat", StringComparison.OrdinalIgnoreCase) &&
               parts.Take(parts.Length - 1).Any(part => part.Equals("internal", StringComparison.OrdinalIgnoreCase));
    });
}
