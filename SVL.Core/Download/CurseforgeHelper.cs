using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace SVL.Core.Download;

/// <summary>
/// Curseforge 辅助工具类
/// </summary>
public static class CurseforgeHelper
{
    /// <summary>
    /// 解析并清理 SMAPI 版本名（去除重复的前缀和后缀）
    /// 例如："SMAPI SMAPI 4.5.1" → "SMAPI 4.5.1"
    /// </summary>
    /// <param name="displayName">原始 displayName</param>
    /// <param name="fileName">文件名（用于提取版本号）</param>
    /// <returns>清理后的版本名</returns>
    public static string ParseSmapiDisplayName(string displayName, string fileName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return "SMAPI";

        // 1. 去除 .zip 后缀（如果有）
        var result = displayName.Replace(".zip", "").Replace(".ZIP", "").Trim();

        // 2. 提取版本号（x.y.z 格式）
        var versionMatch = Regex.Match(result, @"(\d+\.\d+(\.\d+)?)");
        string version = null;

        if (versionMatch.Success)
        {
            version = versionMatch.Groups[1].Value;
        }
        else if (!string.IsNullOrWhiteSpace(fileName))
        {
            // 从 fileName 提取版本号
            var fileVersionMatch = Regex.Match(fileName, @"(\d+\.\d+(\.\d+)?)");
            if (fileVersionMatch.Success)
            {
                version = fileVersionMatch.Groups[1].Value;
            }
        }

        // 3. 使用提取的版本号构造标准格式 "SMAPI x.y.z"
        if (!string.IsNullOrEmpty(version))
        {
            return $"SMAPI {version}";
        }

        // 4. 没有版本号时，只保留 SMAPI 前缀
        if (result.StartsWith("SMAPI ", StringComparison.OrdinalIgnoreCase))
        {
            var afterPrefix = result.Substring(6).Trim();
            if (afterPrefix.StartsWith("SMAPI", StringComparison.OrdinalIgnoreCase))
            {
                return "SMAPI";
            }
        }

        return result.Trim();
    }
}
