using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SVL.Core.IO;

/// <summary>
/// 文件夹名称验证工具
/// </summary>
public static class FileNameValidator
{
    // Windows 非法文件名字符（排除空格，因为空格可以作为文件夹名称）
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars()
        .Where(c => c != ' ')
        .ToArray();

    // Windows 保留设备名称
    private static readonly string[] ReservedDeviceNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    // 额外的非法字符（Windows 特殊字符）
    private static readonly char[] AdditionalInvalidChars = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    /// <summary>
    /// 验证文件夹名称是否合法
    /// </summary>
    /// <param name="folderName">文件夹名称</param>
    /// <returns>验证结果和错误消息</returns>
    public static (bool IsValid, string ErrorMessage) ValidateFolderName(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return (false, "文件夹名称不能为空");
        }

        // 检查长度
        if (folderName.Length > 255)
        {
            return (false, "文件夹名称不能超过255个字符");
        }

        // 检查是否包含非法字符
        if (folderName.IndexOfAny(AdditionalInvalidChars) >= 0)
        {
            var invalidChar = folderName.First(c => AdditionalInvalidChars.Contains(c));
            return (false, $"文件夹名称不能包含字符: {invalidChar}");
        }

        if (folderName.IndexOfAny(InvalidFileNameChars) >= 0)
        {
            var invalidChar = folderName.First(c => InvalidFileNameChars.Contains(c));
            return (false, $"文件夹名称包含非法字符: {invalidChar}");
        }

        // 检查是否是保留设备名称（不区分大小写）
        var upperName = folderName.ToUpperInvariant();
        if (ReservedDeviceNames.Contains(upperName))
        {
            return (false, $"\"{folderName}\" 是系统保留名称，不能作为文件夹名称");
        }

        // 检查是否以点开头或结尾（Windows 特殊限制）
        if (folderName.StartsWith(".") || folderName.EndsWith("."))
        {
            return (false, "文件夹名称不能以点开头或结尾");
        }

        return (true, string.Empty);
    }

    /// <summary>
    /// 快速验证文件夹名称是否合法（仅返回布尔值）
    /// </summary>
    /// <param name="folderName">文件夹名称</param>
    /// <returns>是否合法</returns>
    public static bool IsValidFolderName(string folderName)
    {
        return ValidateFolderName(folderName).IsValid;
    }

    /// <summary>
    /// 清理文件夹名称，移除非法字符
    /// </summary>
    /// <param name="folderName">原始文件夹名称</param>
    /// <param name="replacement">替换字符（默认为下划线）</param>
    /// <returns>清理后的文件夹名称</returns>
    public static string SanitizeFolderName(string folderName, char replacement = '_')
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return "NewFolder";
        }

        // 移除非法字符
        var sanitized = new string(folderName
            .Where(c => !AdditionalInvalidChars.Contains(c) && !InvalidFileNameChars.Contains(c))
            .ToArray());

        // 如果结果为空，返回默认名称
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "NewFolder";
        }

        // 移除开头和结尾的空格和点
        sanitized = sanitized.Trim().Trim('.');

        // 如果是保留名称，添加后缀
        var upperName = sanitized.ToUpperInvariant();
        if (ReservedDeviceNames.Contains(upperName))
        {
            sanitized += "_Instance";
        }

        return sanitized;
    }
}
