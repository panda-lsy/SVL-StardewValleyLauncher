using System.Buffers.Binary;
using System.Text;
using ICSharpCode.SharpZipLib.BZip2;

namespace SVL.Avalonia.Services;

/// <summary>Collection 补丁处理结果。</summary>
public sealed record CollectionPatchResult(
    bool IsSuccess,
    int AppliedCount,
    int SkippedCount,
    string Message);

/// <summary>
/// 应用 Nexus Collection 的 patches 目录中的 BSDiff 补丁。
///
/// Nexus Collection 的清单把补丁目标文件和源文件 CRC 写在 mod.patches 中，
/// 补丁文件位于 patches/&lt;Mod&gt;/ 下。实现同时兼容 Vortex 使用的 BSDIFF40
///（三个 BZip2 数据块）和旧 SVL 代码支持的未压缩 BSDiff 变体。
/// </summary>
public static class CollectionPatchService
{
    private const string PatchDirectoryName = "patches";
    private const int MaxPatchBytes = 256 * 1024 * 1024;
    private const int MaxOutputBytes = 1024 * 1024 * 1024;

    private static readonly uint[] CrcTable = BuildCrcTable();

    public static CollectionPatchResult ApplyPatches(
        string collectionExtractPath,
        string modInstallPath,
        string modName,
        IReadOnlyDictionary<string, string>? patches)
    {
        if (patches == null || patches.Count == 0)
        {
            return new CollectionPatchResult(true, 0, 0, "没有需要应用的补丁");
        }

        if (string.IsNullOrWhiteSpace(collectionExtractPath) ||
            string.IsNullOrWhiteSpace(modInstallPath) ||
            !Directory.Exists(modInstallPath))
        {
            return new CollectionPatchResult(false, 0, patches.Count, "补丁目标 Mod 目录不存在");
        }

        var patchRoot = FindPatchRoot(collectionExtractPath, modInstallPath, modName);
        if (patchRoot == null)
        {
            return new CollectionPatchResult(false, 0, patches.Count, "未找到该 Mod 的 patches 目录");
        }

        var applied = 0;
        var skipped = 0;
        var reasons = new List<string>();

        foreach (var patch in patches)
        {
            if (!TryNormalizeRelativePath(patch.Key, out var relativePath))
            {
                skipped++;
                reasons.Add($"路径无效: {patch.Key}");
                continue;
            }

            // 某些导出器把 Mod 目录名也写进 key，实际源文件则位于 Mod 根目录。
            var sourceRelativePath = RemoveModPrefix(relativePath, modInstallPath);
            if (!TryGetChildPath(modInstallPath, sourceRelativePath, out var sourcePath))
            {
                skipped++;
                reasons.Add($"源文件路径越界: {patch.Key}");
                continue;
            }

            var diffPath = FindDiffPath(patchRoot, relativePath, sourceRelativePath);
            if (diffPath == null)
            {
                skipped++;
                reasons.Add($"补丁文件不存在: {patch.Key}");
                continue;
            }

            try
            {
                if (!File.Exists(sourcePath))
                {
                    skipped++;
                    reasons.Add($"源文件不存在: {sourceRelativePath}");
                    continue;
                }

                var expectedCrc = NormalizeCrc(patch.Value);
                var actualCrc = CalculateCrc32(sourcePath);
                if (expectedCrc.Length > 0 && !string.Equals(expectedCrc, actualCrc, StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    reasons.Add($"CRC 不匹配: {sourceRelativePath}（期望 {expectedCrc}，实际 {actualCrc}）");
                    continue;
                }

                var diffInfo = new FileInfo(diffPath);
                if (!diffInfo.Exists || diffInfo.Length <= 0 || diffInfo.Length > MaxPatchBytes)
                {
                    skipped++;
                    reasons.Add($"补丁文件大小无效: {sourceRelativePath}");
                    continue;
                }

                var oldData = File.ReadAllBytes(sourcePath);
                var patchData = File.ReadAllBytes(diffPath);
                var newData = ApplyBsdiffPatch(oldData, patchData);
                WriteFileAtomically(sourcePath, newData);
                applied++;
                System.Diagnostics.Debug.WriteLine($"[CollectionPatch] 已应用: {modName}/{sourceRelativePath}");
            }
            catch (Exception ex)
            {
                skipped++;
                reasons.Add($"{sourceRelativePath}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[CollectionPatch] 应用失败: {modName}/{sourceRelativePath}: {ex.Message}");
            }
        }

        var message = applied > 0
            ? $"已应用 {applied} 个补丁，跳过 {skipped} 个" + FormatReasons(reasons)
            : "没有补丁成功应用" + FormatReasons(reasons);
        return new CollectionPatchResult(applied > 0, applied, skipped, message);
    }

    private static string? FindPatchRoot(string collectionExtractPath, string modInstallPath, string modName)
    {
        var patchBase = Path.Combine(collectionExtractPath, PatchDirectoryName);
        if (!Directory.Exists(patchBase))
        {
            return null;
        }

        var candidates = new[]
        {
            modName,
            Path.GetFileName(modInstallPath),
            Uri.UnescapeDataString(modName ?? string.Empty)
        }
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

        try
        {
            foreach (var directory in Directory.GetDirectories(patchBase))
            {
                var directoryName = Uri.UnescapeDataString(Path.GetFileName(directory));
                if (candidates.Any(candidate => string.Equals(directoryName, candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    return directory;
                }
            }
        }
        catch
        {
            // 目录枚举失败时让调用方显示清晰的“未找到补丁目录”。
        }

        return null;
    }

    private static string RemoveModPrefix(string relativePath, string modInstallPath)
    {
        var firstSeparator = relativePath.IndexOf('/');
        if (firstSeparator <= 0)
        {
            return relativePath;
        }

        var firstPart = relativePath[..firstSeparator];
        var installedName = Path.GetFileName(
            modInstallPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.Equals(firstPart, installedName, StringComparison.OrdinalIgnoreCase)
            ? relativePath[(firstSeparator + 1)..]
            : relativePath;
    }

    private static string? FindDiffPath(string patchRoot, string relativePath, string sourceRelativePath)
    {
        foreach (var candidateRelativePath in new[] { relativePath, sourceRelativePath }
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!TryGetChildPath(patchRoot, candidateRelativePath + ".diff", out var diffPath) ||
                !File.Exists(diffPath))
            {
                continue;
            }

            return diffPath;
        }

        return null;
    }

    private static bool TryGetChildPath(string root, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        try
        {
            var normalizedRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var normalizedRelative = relativePath.Replace('/', Path.DirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(normalizedRoot, normalizedRelative));
            if (!candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            fullPath = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] ApplyBsdiffPatch(byte[] oldData, byte[] patchData)
    {
        if (patchData.Length >= 8 && Encoding.ASCII.GetString(patchData, 0, 8) == "BSDIFF40")
        {
            return ApplyCompressedBsdiffPatch(oldData, patchData);
        }

        return ApplyLegacyRawBsdiffPatch(oldData, patchData);
    }

    private static byte[] ApplyCompressedBsdiffPatch(byte[] oldData, byte[] patchData)
    {
        if (patchData.Length < 32)
        {
            throw new InvalidDataException("BSDiff 头部不完整");
        }

        var controlLength = ReadBsdiffOffset(patchData, 8);
        var diffLength = ReadBsdiffOffset(patchData, 16);
        var newLength = ReadBsdiffOffset(patchData, 24);
        ValidateBlockLengths(controlLength, diffLength, newLength, patchData.Length - 32);

        var controlStart = 32L;
        var diffStart = checked(controlStart + controlLength);
        var extraStart = checked(diffStart + diffLength);
        var control = ReadBzipBlock(patchData, controlStart, controlLength);
        var diff = ReadBzipBlock(patchData, diffStart, diffLength);
        var extra = ReadBzipBlock(patchData, extraStart, patchData.Length - extraStart);
        return ApplyBsdiffStreams(oldData, control, diff, extra, newLength, canonicalOffsets: true);
    }

    private static byte[] ApplyLegacyRawBsdiffPatch(byte[] oldData, byte[] patchData)
    {
        if (patchData.Length < 24)
        {
            throw new InvalidDataException("BSDiff 补丁头部不完整");
        }

        var controlLength = ReadRawInt64(patchData, 0);
        var diffLength = ReadRawInt64(patchData, 8);
        var newLength = ReadRawInt64(patchData, 16);
        ValidateBlockLengths(controlLength, diffLength, newLength, patchData.Length - 24);

        var controlStart = 24L;
        var diffStart = checked(controlStart + controlLength);
        var extraStart = checked(diffStart + diffLength);
        var control = Slice(patchData, controlStart, controlLength);
        var diff = Slice(patchData, diffStart, diffLength);
        var extra = Slice(patchData, extraStart, patchData.Length - extraStart);
        return ApplyBsdiffStreams(oldData, control, diff, extra, newLength, canonicalOffsets: false);
    }

    private static byte[] ApplyBsdiffStreams(
        byte[] oldData,
        byte[] control,
        byte[] diff,
        byte[] extra,
        long newLength,
        bool canonicalOffsets)
    {
        if (newLength > MaxOutputBytes)
        {
            throw new InvalidDataException("BSDiff 输出文件过大");
        }

        var result = new byte[checked((int)newLength)];
        var controlOffset = 0;
        var diffOffset = 0;
        var extraOffset = 0;
        long oldOffset = 0;
        var newOffset = 0;

        while (newOffset < result.Length)
        {
            if (controlOffset + 24 > control.Length)
            {
                throw new InvalidDataException("BSDiff 控制块不完整");
            }

            var addLength = canonicalOffsets
                ? ReadBsdiffOffset(control, controlOffset)
                : ReadRawInt64(control, controlOffset);
            var copyLength = canonicalOffsets
                ? ReadBsdiffOffset(control, controlOffset + 8)
                : ReadRawInt64(control, controlOffset + 8);
            var seekLength = canonicalOffsets
                ? ReadBsdiffOffset(control, controlOffset + 16)
                : ReadRawInt64(control, controlOffset + 16);
            controlOffset += 24;

            if (addLength < 0 || copyLength < 0 ||
                addLength > result.Length - newOffset ||
                copyLength > result.Length - newOffset - addLength ||
                addLength > diff.Length - diffOffset ||
                copyLength > extra.Length - extraOffset)
            {
                throw new InvalidDataException("BSDiff 控制块超出数据范围");
            }

            for (var index = 0L; index < addLength; index++)
            {
                var oldIndex = oldOffset + index;
                var oldByte = oldIndex >= 0 && oldIndex < oldData.Length
                    ? oldData[oldIndex]
                    : (byte)0;
                result[newOffset++] = (byte)(oldByte + diff[diffOffset++]);
            }

            for (var index = 0L; index < copyLength; index++)
            {
                result[newOffset++] = extra[extraOffset++];
            }

            oldOffset = checked(oldOffset + addLength + seekLength);
        }

        return result;
    }

    private static byte[] ReadBzipBlock(byte[] data, long offset, long length)
    {
        if (length <= 0 || length > int.MaxValue || offset < 0 || offset + length > data.Length)
        {
            throw new InvalidDataException("BSDiff 压缩块范围无效");
        }

        using var input = new MemoryStream(data, checked((int)offset), checked((int)length), writable: false);
        using var bzip = new BZip2InputStream(input);
        using var output = new MemoryStream();
        bzip.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Slice(byte[] data, long offset, long length)
    {
        if (length < 0 || length > int.MaxValue || offset < 0 || offset + length > data.Length)
        {
            throw new InvalidDataException("BSDiff 数据块范围无效");
        }

        var result = new byte[checked((int)length)];
        Buffer.BlockCopy(data, checked((int)offset), result, 0, result.Length);
        return result;
    }

    private static void ValidateBlockLengths(long controlLength, long diffLength, long newLength, long remaining)
    {
        if (controlLength <= 0 || diffLength < 0 || newLength < 0 ||
            controlLength > remaining ||
            diffLength > remaining - controlLength ||
            newLength > MaxOutputBytes)
        {
            throw new InvalidDataException("BSDiff 数据块长度无效");
        }
    }

    private static long ReadBsdiffOffset(byte[] data, int offset)
    {
        var raw = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset, 8));
        var negative = (raw & long.MinValue) != 0;
        var magnitude = raw & long.MaxValue;
        return negative ? -magnitude : magnitude;
    }

    private static long ReadRawInt64(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset, 8));
    }

    private static string CalculateCrc32(string path)
    {
        var crc = 0xFFFFFFFFu;
        using var stream = File.OpenRead(path);
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var index = 0; index < read; index++)
            {
                crc = CrcTable[(crc ^ buffer[index]) & 0xFF] ^ (crc >> 8);
            }
        }

        return (crc ^ 0xFFFFFFFFu).ToString("X8");
    }

    private static string NormalizeCrc(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        return uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var hex)
            ? hex.ToString("X8")
            : string.Empty;
    }

    private static void WriteFileAtomically(string path, byte[] contents)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".svl-patch.tmp";
        var originalAttributes = File.GetAttributes(path);
        try
        {
            File.SetAttributes(path, originalAttributes & ~FileAttributes.ReadOnly);
            File.WriteAllBytes(temporaryPath, contents);
            File.Move(temporaryPath, path, overwrite: true);
            File.SetAttributes(path, originalAttributes);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // 临时文件清理失败不应掩盖补丁结果。
            }
        }
    }

    private static bool TryNormalizeRelativePath(string? value, out string relativePath)
    {
        relativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            var decoded = Uri.UnescapeDataString(value.Trim()).Replace('\\', '/').Trim('/');
            if (decoded.Length == 0 || decoded.StartsWith('/') || Path.IsPathRooted(decoded))
            {
                return false;
            }

            var parts = decoded.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Any(part => part is "." or ".." || part.IndexOf('\0') >= 0))
            {
                return false;
            }

            relativePath = string.Join('/', parts);
            return relativePath.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string FormatReasons(IReadOnlyList<string> reasons)
    {
        return reasons.Count == 0
            ? string.Empty
            : $"（{string.Join("；", reasons.Take(3))}{(reasons.Count > 3 ? "；…" : string.Empty)}）";
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) == 1 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }
}
