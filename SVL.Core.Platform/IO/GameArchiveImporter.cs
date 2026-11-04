using System.IO.Compression;

namespace SVL.Core.Platform.IO;

/// <summary>将用户选择的 PC 游戏 ZIP 导入到隔离的实例目录。</summary>
public static class GameArchiveImporter
{
    private const long MaxArchiveBytes = 4L * 1024 * 1024 * 1024;
    private const long MaxExpandedBytes = 8L * 1024 * 1024 * 1024;
    private const int MaxEntries = 100_000;

    public static async Task<string> ImportZipAsync(
        Stream source, string instancesRoot, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(instancesRoot);

        Directory.CreateDirectory(instancesRoot);
        var id = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(instancesRoot, ".import-" + id);
        var archivePath = Path.Combine(staging, "source.zip");
        var unpacked = Path.Combine(staging, "game");
        var destination = Path.Combine(instancesRoot, "game-" + id);
        Directory.CreateDirectory(staging);

        try
        {
            progress?.Report("正在读取游戏压缩包…");
            await using (var temporaryArchive = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                await CopyBoundedAsync(source, temporaryArchive, MaxArchiveBytes, cancellationToken);
            }

            progress?.Report("正在检查游戏文件…");
            using var archive = ZipFile.OpenRead(archivePath);
            var entries = ValidateEntries(archive);
            var rootPrefix = FindGameRoot(entries);
            Directory.CreateDirectory(unpacked);
            long extractedBytes = 0;
            var selected = entries.Where(item => item.Path.StartsWith(rootPrefix, StringComparison.Ordinal)).ToList();
            for (var index = 0; index < selected.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (entry, path) = selected[index];
                var relative = path[rootPrefix.Length..].TrimEnd('/');
                if (relative.Length == 0) continue;

                var output = Path.Combine(unpacked, relative.Replace('/', Path.DirectorySeparatorChar));
                if (path.EndsWith('/'))
                {
                    Directory.CreateDirectory(output);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await using var input = entry.Open();
                await using var outputStream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
                var copied = await CopyBoundedAsync(input, outputStream, MaxExpandedBytes - extractedBytes, cancellationToken);
                if (copied != entry.Length) throw new InvalidDataException("压缩包中的文件不完整。");
                extractedBytes += copied;
                if ((index + 1) % 32 == 0 || index + 1 == selected.Count)
                    progress?.Report($"正在导入游戏文件：{index + 1}/{selected.Count}");
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(unpacked, destination);
            return destination;
        }
        finally
        {
            // 只清理由本次导入创建的临时目录；用户选择的源文件和已有实例不受影响。
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static List<(ZipArchiveEntry Entry, string Path)> ValidateEntries(ZipArchive archive)
    {
        if (archive.Entries.Count > MaxEntries) throw new InvalidDataException("压缩包中的文件数量过多。");
        var entries = new List<(ZipArchiveEntry, string)>(archive.Entries.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var entry in archive.Entries)
        {
            var path = entry.FullName.Replace('\\', '/');
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(part => part != ".").ToArray();
            if (path.StartsWith('/') || path.Contains(':') || parts.Any(part => part == ".." || part.Any(char.IsControl)))
                throw new InvalidDataException("压缩包包含无效或越界的文件路径。");
            if (parts.Length == 0) continue;
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("压缩包包含不支持的符号链接。");
            path = string.Join('/', parts) + (path.EndsWith('/') ? "/" : "");
            if (!seen.Add(path.TrimEnd('/'))) throw new InvalidDataException("压缩包包含重复的文件路径。");
            if (entry.Length > MaxExpandedBytes - totalBytes) throw new InvalidDataException("解压后的游戏文件超过大小限制。");
            totalBytes += entry.Length;
            entries.Add((entry, path));
        }
        return entries;
    }

    private static string FindGameRoot(List<(ZipArchiveEntry Entry, string Path)> entries)
    {
        const string gameDll = "Stardew Valley.dll";
        var paths = entries.Where(item => item.Entry.Length > 0).Select(item => item.Path).ToHashSet(StringComparer.Ordinal);
        var contentRoots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (entry, path) in entries)
        {
            if (path.EndsWith('/') || entry.Length == 0) continue;
            if (path.StartsWith("Content/", StringComparison.Ordinal)) contentRoots.Add(string.Empty);
            var position = 0;
            while ((position = path.IndexOf("/Content/", position, StringComparison.Ordinal)) >= 0)
            {
                contentRoots.Add(path[..(position + 1)]);
                position += "/Content/".Length;
            }
        }
        var candidates = entries
            .Where(item => item.Entry.Length > 0 && (item.Path.Equals(gameDll, StringComparison.Ordinal) ||
                           item.Path.EndsWith('/' + gameDll, StringComparison.Ordinal)))
            .Select(item => item.Path[..^gameDll.Length])
            .Where(prefix => paths.Contains(prefix + "StardewValley.GameData.dll") && contentRoots.Contains(prefix))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (candidates.Count != 1)
            throw new InvalidDataException(candidates.Count == 0 ? "未找到 PC 版游戏的必要文件。" : "压缩包包含多个游戏目录，请只保留一个游戏版本。");
        return candidates[0];
    }

    private static async Task<long> CopyBoundedAsync(Stream input, Stream output, long limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[131072];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            total += read;
            if (total > limit) throw new InvalidDataException("文件超过导入大小限制。");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return total;
    }
}
