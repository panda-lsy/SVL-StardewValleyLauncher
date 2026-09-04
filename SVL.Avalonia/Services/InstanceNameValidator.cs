using System.IO;

namespace SVL.Avalonia.Services;

/// <summary>
/// 实例名称（文件夹名）合法性验证工具。
/// 参考 SVL.Core.IO.FileNameValidator，迁移到 Avalonia 项目供 UI 层使用。
/// 防止非法字符（如 Unicode 特殊符号、emoji、Windows 保留名等）导致文件夹创建失败或显示异常。
/// </summary>
public static class InstanceNameValidator
{
    private static readonly char[] WindowsInvalidFileNameChars =
        ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    // Windows 保留设备名称
    private static readonly string[] ReservedDeviceNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// 验证实例名称是否合法。
    /// 检查：非空、长度 ≤ 80、无非法文件名字符、非 Windows 保留名、
    /// 不以点开头/结尾、不含控制字符或 Unicode 特殊符号（如数学字母、emoji 等）。
    /// </summary>
    /// <param name="name">实例名称</param>
    /// <returns>验证结果和错误消息</returns>
    public static (bool IsValid, string ErrorMessage) Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return (false, "实例名称不能为空");
        }

        var trimmed = name.Trim();

        // 长度限制（Windows 路径总长度 260，留出 versions/ 前缀和后续操作空间）
        if (trimmed.Length > 80)
        {
            return (false, "实例名称不能超过 80 个字符");
        }

        // 检查 Windows 非法文件名字符
        var invalidChars = Path.GetInvalidFileNameChars()
            .Concat(WindowsInvalidFileNameChars)
            .Distinct()
            .ToArray();
        var invalidIndex = trimmed.IndexOfAny(invalidChars);
        if (invalidIndex >= 0)
        {
            var ch = trimmed[invalidIndex];
            return (false, $"实例名称包含非法字符: '{(ch < 32 ? $"\\x{(int)ch:X2}" : ch)}'");
        }

        // 检查 Windows 保留设备名称（不区分大小写）；CON.txt、COM1.foo 等也属于保留名。
        var deviceName = trimmed.Split('.', 2)[0];
        if (Array.IndexOf(ReservedDeviceNames, deviceName.ToUpperInvariant()) >= 0)
        {
            return (false, $"\"{trimmed}\" 是系统保留名称，不能作为实例名称");
        }

        // 不以点开头或结尾（Windows 特殊限制）
        if (trimmed.StartsWith('.') || trimmed.EndsWith('.'))
        {
            return (false, "实例名称不能以点开头或结尾");
        }

        // 检查 Unicode 控制字符和特殊符号类别（如数学字母 𝒻𝑜𝓇、emoji 等）
        // 这些字符虽然不是 Windows 非法字符，但会导致显示和路径解析问题
        foreach (var c in trimmed)
        {
            var cat = char.GetUnicodeCategory(c);
            if (cat == System.Globalization.UnicodeCategory.Control ||
                cat == System.Globalization.UnicodeCategory.Format ||
                cat == System.Globalization.UnicodeCategory.Surrogate ||
                cat == System.Globalization.UnicodeCategory.PrivateUse ||
                cat == System.Globalization.UnicodeCategory.OtherNotAssigned)
            {
                return (false, $"实例名称包含不支持的 Unicode 字符（控制字符/特殊符号/私用区），请使用常规字符");
            }

            // 检查代理对（emoji 等）的第一个码元
            if (char.IsHighSurrogate(c))
            {
                return (false, "实例名称不能包含 emoji 或代理对字符");
            }
        }

        return (true, string.Empty);
    }

    /// <summary>快速验证实例名称是否合法（仅返回布尔值）。</summary>
    public static bool IsValid(string name) => Validate(name).IsValid;

    /// <summary>
    /// 清理实例名称，移除非法字符。用于自动生成实例名时的预处理。
    /// </summary>
    /// <param name="name">原始名称</param>
    /// <param name="replacement">替换字符（默认下划线）</param>
    /// <returns>清理后的名称</returns>
    public static string Sanitize(string name, char replacement = '_')
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "NewInstance";
        }

        var invalidChars = Path.GetInvalidFileNameChars()
            .Concat(WindowsInvalidFileNameChars)
            .Distinct()
            .ToArray();
        var sanitized = new string(name
            .Select(c => IsUnsupportedUnicode(c) || invalidChars.Contains(c) ? replacement : c)
            .ToArray());

        sanitized = sanitized.Trim().Trim('.');

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "NewInstance";
        }

        var deviceName = sanitized.Split('.', 2)[0];
        if (Array.IndexOf(ReservedDeviceNames, deviceName.ToUpperInvariant()) >= 0)
        {
            sanitized += "_Instance";
        }

        return sanitized;
    }

    private static bool IsUnsupportedUnicode(char c)
    {
        var cat = char.GetUnicodeCategory(c);
        return cat == System.Globalization.UnicodeCategory.Control ||
               cat == System.Globalization.UnicodeCategory.Format ||
               cat == System.Globalization.UnicodeCategory.Surrogate ||
               cat == System.Globalization.UnicodeCategory.PrivateUse ||
               cat == System.Globalization.UnicodeCategory.OtherNotAssigned ||
               char.IsHighSurrogate(c);
    }
}
