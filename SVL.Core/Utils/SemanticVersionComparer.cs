using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SVL.Core.Utils;

/// <summary>
/// 语义化版本比较器，支持版本号的正确排序（如 1.6.15 > 1.6.8）
/// </summary>
/// <example>
/// 使用示例：
/// <code>
/// var versions = new List&lt;string&gt; { "1.6.8", "1.6.15", "1.5.6" };
/// versions.Sort(SemanticVersionComparer.Instance);
/// // 结果: ["1.5.6", "1.6.8", "1.6.15"]
/// </code>
/// </example>
public sealed class SemanticVersionComparer : IComparer<string>, IComparer<string?>
{
    /// <summary>
    /// 单例实例
    /// </summary>
    public static readonly SemanticVersionComparer Instance = new();

    /// <summary>
    /// 私有构造函数，强制使用单例
    /// </summary>
    private SemanticVersionComparer() { }

    /// <summary>
    /// 比较两个版本字符串
    /// </summary>
    /// <param name="x">版本字符串 x</param>
    /// <param name="y">版本字符串 y</param>
    /// <returns>
    /// 负数：x &lt; y
    /// 0：x == y
    /// 正数：x &gt; y
    /// </returns>
    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
            return 0;

        if (x == null)
            return -1;

        if (y == null)
            return 1;

        // 处理特殊版本标签（如 "全部"、"未知" 等）
        if (IsSpecialVersion(x) && IsSpecialVersion(y))
            return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);

        if (IsSpecialVersion(x))
            return 1; // 特殊版本排在后面

        if (IsSpecialVersion(y))
            return -1; // 特殊版本排在后面

        // 解析版本号并比较
        var versionX = ParseVersion(x);
        var versionY = ParseVersion(y);

        return CompareVersionParts(versionX, versionY);
    }

    /// <summary>
    /// 判断是否为特殊版本标签
    /// </summary>
    private static bool IsSpecialVersion(string version)
    {
        return string.Equals(version, "全部", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(version, "未知", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(version, "All", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 解析版本字符串，移除非数字部分并提取版本号数组
    /// </summary>
    /// <param name="version">版本字符串（如 "1.6.15", "1.6.15+", "1.6.15-beta"）</param>
    /// <returns>版本号数组（如 [1, 6, 15]）</returns>
    private static List<int> ParseVersion(string version)
    {
        var result = new List<int>();

        if (string.IsNullOrWhiteSpace(version))
            return result;

        // 移除版本号前缀（如 "v1.6.15" -> "1.6.15"）
        var normalized = version.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            normalized = normalized.Substring(1);

        // 移除后缀标记（如 "+"、"-beta" 等）
        var plusIndex = normalized.IndexOf('+');
        if (plusIndex >= 0)
            normalized = normalized.Substring(0, plusIndex);

        var dashIndex = normalized.IndexOf('-');
        if (dashIndex >= 0)
            normalized = normalized.Substring(0, dashIndex);

        // 提取所有数字部分
        var matches = Regex.Matches(normalized, @"\d+");
        foreach (Match match in matches)
        {
            if (int.TryParse(match.Value, out var number))
                result.Add(number);
        }

        return result;
    }

    /// <summary>
    /// 逐个比较版本号部分
    /// </summary>
    /// <param name="x">版本号数组 x</param>
    /// <param name="y">版本号数组 y</param>
    /// <returns>
    /// 负数：x &lt; y
    /// 0：x == y
    /// 正数：x &gt; y
    /// </returns>
    private static int CompareVersionParts(List<int> x, List<int> y)
    {
        var maxLength = Math.Max(x.Count, y.Count);

        for (int i = 0; i < maxLength; i++)
        {
            var partX = i < x.Count ? x[i] : 0;
            var partY = i < y.Count ? y[i] : 0;

            if (partX != partY)
                return partX.CompareTo(partY);
        }

        return 0;
    }

    /// <summary>
    /// 获取用于排序的版本键（适用于 LINQ OrderBy/OrderByDescending）
    /// </summary>
    /// <param name="version">版本字符串</param>
    /// <returns>可排序的版本键对象</returns>
    public static VersionKey GetSortKey(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return VersionKey.MinValue;

        if (IsSpecialVersion(version))
            return VersionKey.MaxValue;

        var parts = ParseVersion(version);
        return new VersionKey(parts);
    }

    /// <summary>
    /// 版本键结构，用于 LINQ 排序
    /// </summary>
    public readonly struct VersionKey : IComparable<VersionKey>, IEquatable<VersionKey>
    {
        private readonly List<int> _parts;

        public static readonly VersionKey MinValue = new(new List<int> { int.MinValue });
        public static readonly VersionKey MaxValue = new(new List<int> { int.MaxValue });

        public VersionKey(List<int> parts)
        {
            _parts = parts ?? new List<int>();
        }

        public int CompareTo(VersionKey other)
        {
            return CompareVersionParts(_parts, other._parts);
        }

        public bool Equals(VersionKey other)
        {
            return CompareTo(other) == 0;
        }

        public override bool Equals(object? obj)
        {
            return obj is VersionKey key && Equals(key);
        }

        public override int GetHashCode()
        {
            if (_parts.Count == 0)
                return 0;

            var hash = 17;
            unchecked
            {
                foreach (var part in _parts)
                {
                    hash = hash * 31 + part.GetHashCode();
                }
            }
            return hash;
        }
    }
}
