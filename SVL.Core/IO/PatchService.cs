using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SVL.Core.Logging;
using SVL.Core.Download.NexusMods;

namespace SVL.Core.IO;

/// <summary>
/// Collection 补丁应用服务
/// 参考 Vortex extensions/collections/src/util/binaryPatching.ts
/// </summary>
public static class CollectionPatchService
{
    private const string PatchesPath = "patches";
    private const int MaxPatchSizePercentage = 20;  // 补丁最大 20% 原文件大小
    private const int PatchOverhead = 130;           // 最小开销（字节）

    /// <summary>
    /// 应用 Mod 的补丁
    /// </summary>
    /// <param name="collectionExtractPath">Collection 解压路径</param>
    /// <param name="modInstallPath">Mod 安装路径</param>
    /// <param name="modName">Mod 名称（用于查找 patches）</param>
    /// <param name="patches">补丁字典 {文件路径: 期望的 CRC32}</param>
    public static bool ApplyPatches(string collectionExtractPath, string modInstallPath, string modName, Dictionary<string, string> patches)
    {
        if (patches == null || patches.Count == 0)
        {
            Log.Info($"[PatchService] Mod {modName} 没有补丁需要应用");
            return true;
        }

        Log.Info($"[PatchService] 开始为 {modName} 应用 {patches.Count} 个补丁");

        var collectionPatchesPath = Path.Combine(collectionExtractPath, PatchesPath, modName);
        if (!Directory.Exists(collectionPatchesPath))
        {
            Log.Warn($"[PatchService] 补丁目录不存在: {collectionPatchesPath}");
            return false;
        }

        int appliedCount = 0;
        int skippedCount = 0;

        foreach (var patch in patches)
        {
            var filePath = patch.Key;
            var expectedCrc = patch.Value;

            // collection.json 中的路径使用 \\，需要转换
            var normalizedPath = filePath.Replace("\\", Path.DirectorySeparatorChar.ToString());

            // 移除 Mod 名称前缀（如果路径以 Mod 名称开头）
            // 例如：PartOfTheCommunity\manifest.json -> manifest.json
            var pathParts = normalizedPath.Split(Path.DirectorySeparatorChar);
            var normalizedPathWithoutPrefix = normalizedPath;

            // 检查第一个部分是否是 Mod 名称（通常与 modInstallPath 的文件夹名匹配）
            if (pathParts.Length > 1)
            {
                var firstPart = pathParts[0];
                // 如果第一个部分与 Mod 安装目录的文件夹名相同，则移除它
                if (string.Equals(firstPart, Path.GetFileName(modInstallPath), StringComparison.OrdinalIgnoreCase))
                {
                    normalizedPathWithoutPrefix = string.Join(Path.DirectorySeparatorChar.ToString(), pathParts.Skip(1).ToArray());
                }
            }

            var srcFilePath = Path.Combine(modInstallPath, normalizedPathWithoutPrefix);
            var diffFilePath = Path.Combine(collectionPatchesPath, normalizedPath + ".diff");

            if (!File.Exists(srcFilePath))
            {
                Log.Warn($"[PatchService] 源文件不存在: {srcFilePath}");
                skippedCount++;
                continue;
            }

            if (!File.Exists(diffFilePath))
            {
                Log.Warn($"[PatchService] 补丁文件不存在: {diffFilePath}");
                skippedCount++;
                continue;
            }

            // 验证源文件 CRC32
            var actualCrc = CalculateCRC32(srcFilePath);
            if (!string.Equals(actualCrc, expectedCrc, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn($"[PatchService] CRC 不匹配: {filePath} (期望: {expectedCrc}, 实际: {actualCrc})");
                skippedCount++;
                continue;
            }

            // 验证补丁大小
            if (!ValidatePatchSize(srcFilePath, diffFilePath))
            {
                Log.Warn($"[PatchService] 补丁过大: {filePath}");
                skippedCount++;
                continue;
            }

            // 应用补丁
            if (ApplyPatch(srcFilePath, diffFilePath))
            {
                Log.Info($"[PatchService] ✓ 补丁应用成功: {filePath}");
                appliedCount++;
            }
            else
            {
                Log.Warn($"[PatchService] ✗ 补丁应用失败: {filePath}");
                skippedCount++;
            }
        }

        Log.Info($"[PatchService] 补丁应用完成: {appliedCount} 成功, {skippedCount} 跳过");
        return appliedCount > 0;
    }

    /// <summary>
    /// 计算 CRC32 哈希
    /// </summary>
    private static string CalculateCRC32(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var crc32 = new ICSharpCode.SharpZipLib.Checksum.Crc32();
        crc32.Reset();
        var buffer = new byte[8192];
        int bytesRead;

        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < bytesRead; i++)
            {
                crc32.Update(buffer[i]);
            }
        }

        return crc32.Value.ToString("X8");
    }

    /// <summary>
    /// 验证补丁大小是否合理
    /// </summary>
    private static bool ValidatePatchSize(string srcFilePath, string patchFilePath)
    {
        try
        {
            var srcSize = new FileInfo(srcFilePath).Length;
            var patchSize = new FileInfo(patchFilePath).Length;

            // 补丁大小不能超过原文件大小的 20% + 130 字节
            var maxSize = (long)(srcSize * MaxPatchSizePercentage / 100.0) + PatchOverhead;
            var actualPatchSize = patchSize - PatchOverhead;

            if (actualPatchSize > maxSize)
            {
                Log.Warn($"[PatchService] 补丁过大: 原文件 {srcSize} 字节, 补丁 {patchSize} 字节, 最大 {maxSize} 字节");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[PatchService] 验证补丁大小失败");
            return false;
        }
    }

    /// <summary>
    /// 应用单个补丁
    /// </summary>
    private static bool ApplyPatch(string srcFilePath, string diffFilePath)
    {
        try
        {
            Log.Debug($"[PatchService] 应用补丁: {srcFilePath} <- {diffFilePath}");

            // 读取源文件和补丁文件
            var srcData = File.ReadAllBytes(srcFilePath);
            var patchData = File.ReadAllBytes(diffFilePath);

            // 应用 BSDiff 补丁
            var patchedData = ApplyBsdiffPatch(srcData, patchData);

            // 写回源文件
            File.WriteAllBytes(srcFilePath, patchedData);

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[PatchService] 应用补丁失败: {srcFilePath}");
            return false;
        }
    }

    /// <summary>
    /// 应用 BSDiff 补丁
    /// 参考 BSDiff 算法和 Vortex 实现
    /// </summary>
    /// <param name="oldData">原始数据</param>
    /// <param name="patchData">补丁数据</param>
    /// <returns>补丁后的数据</returns>
    private static byte[] ApplyBsdiffPatch(byte[] oldData, byte[] patchData)
    {
        using var ms = new MemoryStream(patchData);
        using var br = new BinaryReader(ms);

        // 读取补丁头部
        // BSDiff 格式: [control length][diff length][new length][control block][diff block][extra block]
        var controlLen = ReadBigInt64(br);
        var diffLen = ReadBigInt64(br);
        var newLen = ReadBigInt64(br);

        // 计算各块的起始位置
        var diffStart = 24 + (int)controlLen;  // control block 从第 24 字节开始
        var extraStart = diffStart + (int)diffLen;

        var result = new byte[newLen];
        int resultPos = 0;
        int oldPos = 0;
        int diffPos = diffStart;  // diff 数据的当前位置

        // 读取控制块
        for (int i = 0; i < controlLen; i += 24)
        {
            // 每个 control 条目是 24 字节: [add][copy][seek] (各 8 字节)
            var add = ReadBigInt64(br);
            var copy = ReadBigInt64(br);
            var seek = ReadBigInt64(br);

            // 1. 添加 diff 数据（顺序读取）
            for (int j = 0; j < add; j++)
            {
                if (resultPos < result.Length && oldPos < oldData.Length && diffPos < extraStart)
                {
                    var diff = patchData[diffPos++];
                    result[resultPos++] = (byte)((oldData[oldPos++] + diff) & 0xFF);
                }
            }

            // 2. 跳过（seek）
            oldPos += (int)seek;

            // 3. 复制 extra 数据（从 extraStart 开始，按 seek 偏移）
            for (int j = 0; j < copy; j++)
            {
                if (resultPos < result.Length)
                {
                    var extraPos = extraStart + (int)(resultPos - seek);
                    if (extraPos < patchData.Length)
                    {
                        result[resultPos++] = patchData[extraPos];
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 读取 64 位大整数（小端序）
    /// </summary>
    private static long ReadBigInt64(BinaryReader br)
    {
        byte[] bytes = br.ReadBytes(8);
        if (bytes.Length != 8)
            throw new EndOfStreamException("无法读取 8 字节整数");

        // BSDiff 使用小端序
        long value = 0;
        for (int i = 0; i < 8; i++)
        {
            value |= ((long)bytes[i]) << (i * 8);
        }
        return value;
    }

    /// <summary>
    /// 检查 Mod 是否需要应用补丁
    /// </summary>
    public static bool HasPatches(NexusCollectionJsonMod mod)
    {
        return mod.Patches != null && mod.Patches.Count > 0;
    }
}
