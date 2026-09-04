using System.IO;
using Avalonia.Platform;

namespace SVL.Avalonia.Services;

public static class InstanceIconResolver
{
    private static readonly string[] IconExtensions =
    [
        ".png",
        ".jpg",
        ".jpeg",
        ".webp",
        ".bmp",
        ".gif"
    ];

    public static string ResolveIconPath(string? instancePath, bool isSmapiInstance = false)
    {
        if (string.IsNullOrWhiteSpace(instancePath) || !Directory.Exists(instancePath))
        {
            return string.Empty;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in EnumerateIconCandidates(instancePath, isSmapiInstance))
        {
            if (!seen.Add(candidate))
            {
                continue;
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    public static string ResolveStorageDirectory(string? instancePath)
    {
        if (string.IsNullOrWhiteSpace(instancePath) || !Directory.Exists(instancePath))
        {
            return string.Empty;
        }

        var directoryName = Path.GetFileName(instancePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.Equals(directoryName, "game", StringComparison.OrdinalIgnoreCase))
        {
            return instancePath;
        }

        var parent = Directory.GetParent(instancePath);
        return parent?.Exists == true ? parent.FullName : instancePath;
    }

    private static IEnumerable<string> EnumerateIconCandidates(string instancePath, bool isSmapiInstance)
    {
        var storageDirectory = ResolveStorageDirectory(instancePath);
        if (!string.IsNullOrWhiteSpace(storageDirectory))
        {
            foreach (var candidate in BuildCandidates(storageDirectory, isSmapiInstance))
            {
                yield return candidate;
            }
        }

        if (!string.Equals(storageDirectory, instancePath, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var candidate in BuildCandidates(instancePath, isSmapiInstance))
            {
                yield return candidate;
            }
        }
    }

    /// <summary>
    /// 构建图标候选路径列表。
    /// SMAPI 实例优先查找 .svl-instance-icon-smapi.{ext}，再回退到通用 .svl-instance-icon.{ext}；
    /// 原版实例只查找通用 .svl-instance-icon.{ext}。
    /// 这样 Base 原版与 Base SMAPI（共享同一物理路径）可以拥有各自独立的图标。
    /// </summary>
    private static IEnumerable<string> BuildCandidates(string directory, bool isSmapiInstance)
    {
        // SMAPI 实例优先使用独立图标文件
        if (isSmapiInstance)
        {
            yield return Path.Combine(directory, ".svl-instance-icon-smapi");

            foreach (var extension in IconExtensions)
            {
                yield return Path.Combine(directory, $".svl-instance-icon-smapi{extension}");
            }
        }

        // 通用图标文件（向后兼容：旧版本写入的 .svl-instance-icon.png 仍可命中）
        yield return Path.Combine(directory, ".svl-instance-icon");

        foreach (var extension in IconExtensions)
        {
            yield return Path.Combine(directory, $".svl-instance-icon{extension}");
        }
    }

    /// <summary>
    /// 解析默认预设图标路径（当无自定义图标时使用）。
    /// 【临时占位】以下预设图标为占位实现，后续有新的预设条件可随时更换：
    /// - SMAPI 实例 → Modded.png
    /// - 原版实例   → Vanilla.png
    /// - 异常状态   → Junimo2.png（路径无效/版本检测失败/游戏文件缺失）
    /// 注意：系统预设图标优先级低于自定义图标（由 ResolveIconPath 优先解析）。
    /// </summary>
    /// <param name="isSmapiInstance">是否为 SMAPI 实例</param>
    /// <param name="isAnomaly">是否为异常状态（路径无效/版本未知/文件缺失）</param>
    public static string ResolveDefaultPresetIcon(bool isSmapiInstance, bool isAnomaly)
    {
        if (isAnomaly)
        {
            return "avares://SVL.Avalonia/Assets/Icons/Junimo2.png";
        }

        return isSmapiInstance
            ? "avares://SVL.Avalonia/Assets/Icons/Modded.png"
            : "avares://SVL.Avalonia/Assets/Icons/Vanilla.png";
    }

    /// <summary>
    /// SMAPI 安装成功后写入预设图标（Modded.png），物化"此实例为 SMAPI"标识到磁盘。
    /// 仅当当前实例没有自定义图标时写入，避免覆盖用户通过 ChangeIcon 设置的个性化图标。
    /// 写入 .svl-instance-icon.png 后，ResolveIconPath 会优先命中，
    /// 彻底解决重启后 IsSmapiInstance 状态丢失导致图标回退的问题。
    /// </summary>
    /// <param name="instancePath">实例运行时路径（versionRoot 或 runtimePath）</param>
    /// <returns>true 表示写入成功或已有自定义图标；false 表示写入失败</returns>
    public static bool TryWriteDefaultSmapiIcon(string? instancePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(instancePath) || !Directory.Exists(instancePath))
            {
                System.Diagnostics.Debug.WriteLine($"[IconResolver] TryWriteDefaultSmapiIcon 跳过: instancePath 为空或不存在, path={instancePath}");
                return false;
            }

            // 已有自定义图标则不覆盖（传入 isSmapiInstance=true 检查 SMAPI 专属图标）
            if (!string.IsNullOrWhiteSpace(ResolveIconPath(instancePath, isSmapiInstance: true)))
            {
                System.Diagnostics.Debug.WriteLine($"[IconResolver] TryWriteDefaultSmapiIcon 跳过: 已有自定义图标, path={instancePath}");
                return true;
            }

            var iconStorageDir = ResolveStorageDirectory(instancePath);
            if (string.IsNullOrWhiteSpace(iconStorageDir))
            {
                System.Diagnostics.Debug.WriteLine($"[IconResolver] TryWriteDefaultSmapiIcon 跳过: iconStorageDir 为空, path={instancePath}");
                return false;
            }

            // SMAPI 实例使用独立图标文件名，避免与同路径下的原版实例共享图标
            var targetPath = Path.Combine(iconStorageDir, ".svl-instance-icon-smapi.png");
            Directory.CreateDirectory(iconStorageDir);

            using var stream = global::Avalonia.Platform.AssetLoader.Open(new Uri("avares://SVL.Avalonia/Assets/Icons/Modded.png", UriKind.Absolute));
            using var output = File.Create(targetPath);
            stream.CopyTo(output);
            System.Diagnostics.Debug.WriteLine($"[IconResolver] TryWriteDefaultSmapiIcon 成功: {targetPath}");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[IconResolver] TryWriteDefaultSmapiIcon 异常: {ex.Message}");
            return false;
        }
    }
}