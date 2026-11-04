using System.IO;
using Avalonia.Platform;

namespace SVL.Avalonia.Services;

public static class InstanceIconResolver
{
    /// <summary>
    /// 标记 SMAPI 安装流程写入的默认图标。
    /// 不能通过图片内容判断“默认图标”：玩家也可以主动选择同一个内置预设。
    /// </summary>
    internal const string GeneratedSmapiIconMarkerFileName = ".svl-instance-icon-smapi.generated";

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
                // SMAPI 安装流程生成的默认图标只是系统预设，不能遮住玩家在
                // 版本设置中选择的通用自定义图标。用户主动选择图标时会删除
                // 该标记，因此同一文件名仍可继续作为真正的自定义图标使用。
                if (isSmapiInstance &&
                    IsSmapiIconPath(candidate) &&
                    IsGeneratedSmapiIcon(instancePath) &&
                    HasUserCustomIcon(instancePath))
                {
                    continue;
                }

                // Base 路径的内置 Vanilla 图标只是“没有自定义图标”的占位，
                // 不能在切换到 SMAPI 变体时遮住 SMAPI 默认图标；用户通过版本设置
                // 保存的通用自定义图标则必须保留最高优先级。
                if (isSmapiInstance &&
                    !IsSmapiIconPath(candidate) &&
                    IsBundledIcon(candidate, "Vanilla.png"))
                {
                    continue;
                }

                return candidate;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 展示时将安装器生成的 SMAPI 图标视为默认预设，随启动器资源更新；
    /// 其他图标包括用户主动选择的预设和整合包图标都保留原有优先级。
    /// </summary>
    public static string ResolveDisplayIconPath(string? instancePath, bool isSmapiInstance = false)
    {
        var iconPath = ResolveIconPath(instancePath, isSmapiInstance);
        return isSmapiInstance && !string.IsNullOrWhiteSpace(iconPath) &&
               IsGeneratedSmapiIcon(instancePath) && IsSmapiIconPath(iconPath)
            ? string.Empty
            : iconPath;
    }

    private static bool HasUserCustomIcon(string instancePath)
    {
        foreach (var candidate in EnumerateIconCandidates(instancePath, isSmapiInstance: false))
        {
            if (File.Exists(candidate) && !IsBundledIcon(candidate, "Vanilla.png"))
            {
                return true;
            }
        }

        return false;
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

    /// <summary>
    /// 根据运行时标记判断实例是否包含 SMAPI。
    /// 同时兼容新布局（版本根目录直接放运行时文件）与旧布局（版本根目录/game）。
    /// </summary>
    public static bool IsSmapiRuntime(string? instancePath)
    {
        if (string.IsNullOrWhiteSpace(instancePath) || !Directory.Exists(instancePath))
        {
            return false;
        }

        var runtimeCandidates = new[]
        {
            instancePath,
            Path.Combine(instancePath, "game")
        };

        var markers = new[]
        {
            "StardewModdingAPI.exe",
            "StardewModdingAPI",
            "StardewModdingAPI.dll"
        };

        return runtimeCandidates
            .Where(Directory.Exists)
            .SelectMany(path => markers.Select(marker => Path.Combine(path, marker)))
            .Any(File.Exists);
    }

    /// <summary>
    /// 判断路径是否为 Base/versions/&lt;实例&gt; 下的隔离实例。
    /// 隔离实例的图标类型应以实际运行时文件为准，不能被全局启动模式误覆盖。
    /// </summary>
    public static bool IsVersionIsolatedInstance(string? instancePath)
    {
        if (string.IsNullOrWhiteSpace(instancePath) || !Directory.Exists(instancePath))
        {
            return false;
        }

        var current = new DirectoryInfo(instancePath);
        DirectoryInfo? child = null;
        while (current != null)
        {
            if (string.Equals(current.Name, "versions", StringComparison.OrdinalIgnoreCase))
            {
                return child != null;
            }

            child = current;
            current = current.Parent;
        }

        return false;
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

            // 兼容早期隔离实例：旧版本曾把个性化图标写成通用名称。
            // 仅对隔离实例回退读取，Base 路径仍严格区分原版/SMAPI 两个变体。
            if (isSmapiInstance && IsVersionIsolatedInstance(instancePath))
            {
                foreach (var candidate in BuildCandidates(storageDirectory, isSmapiInstance: false))
                {
                    yield return candidate;
                }
            }
        }

        if (!string.Equals(storageDirectory, instancePath, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var candidate in BuildCandidates(instancePath, isSmapiInstance))
            {
                yield return candidate;
            }

            if (isSmapiInstance && IsVersionIsolatedInstance(instancePath))
            {
                foreach (var candidate in BuildCandidates(instancePath, isSmapiInstance: false))
                {
                    yield return candidate;
                }
            }
        }
    }

    /// <summary>
    /// 构建图标候选路径列表。
    /// SMAPI 实例优先查找 .svl-instance-icon-smapi.{ext}，随后兼容读取
    /// Base 路径上的通用自定义图标；原版实例只查找通用 .svl-instance-icon.{ext}。
    /// 这样既能区分同一路径下的 SMAPI/原版默认图标，也不会丢失用户在版本设置中
    /// 保存的通用自定义图标。
    /// </summary>
    private static IEnumerable<string> BuildCandidates(string directory, bool isSmapiInstance)
    {
        // SMAPI 实例使用独立图标文件，不读取通用图标。
        if (isSmapiInstance)
        {
            yield return Path.Combine(directory, ".svl-instance-icon-smapi");

            foreach (var extension in IconExtensions)
            {
                yield return Path.Combine(directory, $".svl-instance-icon-smapi{extension}");
            }

            // 通用图标是旧版/ Base 路径的用户自定义命名空间。ResolveIconPath
            // 会过滤内置 Vanilla.png，但会保留用户实际选择的图标。
            yield return Path.Combine(directory, ".svl-instance-icon");
            foreach (var extension in IconExtensions)
            {
                yield return Path.Combine(directory, $".svl-instance-icon{extension}");
            }

            yield break;
        }

        // Vanilla/旧版本通用图标文件
        yield return Path.Combine(directory, ".svl-instance-icon");

        foreach (var extension in IconExtensions)
        {
            yield return Path.Combine(directory, $".svl-instance-icon{extension}");
        }
    }

    private static bool IsSmapiIconPath(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.StartsWith(".svl-instance-icon-smapi", StringComparison.OrdinalIgnoreCase);
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
    /// 仅当当前实例没有 SMAPI 专属自定义图标时写入，避免覆盖用户通过 ChangeIcon 设置的个性化图标。
    /// 写入 .svl-instance-icon-smapi.png 后，启动时按实际 SMAPI 运行时判定即可优先命中，
    /// 避免同一 Base 路径上的 Vanilla 图标覆盖 SMAPI 图标。
    /// </summary>
    /// <param name="instancePath">实例运行时路径（versionRoot 或 runtimePath）</param>
    /// <returns>true 表示写入成功或已有自定义图标；false 表示写入失败</returns>
    public static bool TryWriteDefaultSmapiIcon(string? instancePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(instancePath) || !Directory.Exists(instancePath))
            {
                global::SVL.Core.Platform.Diagnostics.RuntimeDebugLog.WriteLine($"[IconResolver] TryWriteDefaultSmapiIcon 跳过: instancePath 为空或不存在, path={instancePath}");
                return false;
            }

            var iconStorageDir = ResolveStorageDirectory(instancePath);
            if (string.IsNullOrWhiteSpace(iconStorageDir))
            {
                global::SVL.Core.Platform.Diagnostics.RuntimeDebugLog.WriteLine($"[IconResolver] TryWriteDefaultSmapiIcon 跳过: iconStorageDir 为空, path={instancePath}");
                return false;
            }

            // SMAPI 实例使用独立图标文件名，避免与同路径下的原版实例共享图标
            var targetPath = Path.Combine(iconStorageDir, ".svl-instance-icon-smapi.png");
            var markerPath = Path.Combine(iconStorageDir, GeneratedSmapiIconMarkerFileName);
            Directory.CreateDirectory(iconStorageDir);

            // 只有启动器自己写入的默认图标可以在整合包导入时被包内图标替换。
            // 标记存在但图片丢失时，清掉孤立标记并重新生成默认图标。
            if (File.Exists(markerPath))
            {
                if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 0)
                {
                    return true;
                }

                TryDeleteGeneratedSmapiIconMarker(instancePath);
            }

            // 已有 SMAPI 专属图标则不覆盖。旧版本曾把隔离实例图标写成通用名称，
            // ResolveIconPath 会为隔离 SMAPI 实例兼容读取它；安装 SMAPI 时将其
            // 迁移到专用命名空间，避免后续原版图标解析覆盖 SMAPI 图标。
            var existingIconPath = ResolveIconPath(instancePath, isSmapiInstance: true);
            if (!string.IsNullOrWhiteSpace(existingIconPath))
            {
                // 内置 Vanilla 只是默认占位，不属于用户自定义图标；即使当前
                // 路径不是 versions/<instance> 隔离目录，也不能让它阻止 SMAPI
                // 专属图标生成，否则安装成功后仍会显示 Vanilla。
                if (IsBundledIcon(existingIconPath, "Vanilla.png"))
                {
                    global::SVL.Core.Platform.Diagnostics.RuntimeDebugLog.WriteLine(
                        $"[IconResolver] TryWriteDefaultSmapiIcon 忽略内置 Vanilla 预设: {existingIconPath}");
                    existingIconPath = string.Empty;
                }
                else if (!string.Equals(
                        Path.GetFullPath(existingIconPath),
                        Path.GetFullPath(targetPath),
                        StringComparison.OrdinalIgnoreCase) &&
                    IsVersionIsolatedInstance(instancePath) &&
                    File.Exists(existingIconPath))
                {
                    // 旧版本可能先给同一隔离目录写入了通用 Vanilla 预设图标。
                    // 这不是用户自定义图标，不能把它迁移成 SMAPI 图标，否则安装完成后
                    // 仍会显示 Vanilla。真正的用户图标仍然迁移并保留。
                    File.Copy(existingIconPath, targetPath, true);
                    global::SVL.Core.Platform.Diagnostics.RuntimeDebugLog.WriteLine(
                        $"[IconResolver] TryWriteDefaultSmapiIcon 已迁移旧版自定义图标: {existingIconPath} -> {targetPath}");
                }

                if (!string.IsNullOrWhiteSpace(existingIconPath))
                {
                    global::SVL.Core.Platform.Diagnostics.RuntimeDebugLog.WriteLine(
                        $"[IconResolver] TryWriteDefaultSmapiIcon 跳过: 已有自定义图标, path={instancePath}, resolved={ResolveIconPath(instancePath, true)}");
                    return File.Exists(targetPath) || File.Exists(existingIconPath);
                }
            }

            using (var stream = OpenBundledIcon("Modded.png"))
            using (var output = new FileStream(
                       targetPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.Read,
                       bufferSize: 4096,
                       options: FileOptions.SequentialScan))
            {
                stream.CopyTo(output);
                output.Flush(flushToDisk: true);
            }

            // 图标写入和流关闭后再写标记，并立即验证实际解析路径。这样日志不会把
            // “写入流成功”误报成“界面可读取”，也能及时暴露路径解析到另一目录的问题。
            if (!File.Exists(targetPath) || new FileInfo(targetPath).Length <= 0)
            {
                TryDeleteGeneratedSmapiIconMarker(instancePath);
                global::SVL.Core.Platform.Diagnostics.RuntimeDebugLog.WriteLine(
                    $"[IconResolver] TryWriteDefaultSmapiIcon 校验失败: target={targetPath}, exists={File.Exists(targetPath)}");
                return false;
            }

            File.WriteAllText(markerPath, "generated-by-svl\n");
            var resolvedPath = ResolveIconPath(instancePath, isSmapiInstance: true);
            var isResolved = !string.IsNullOrWhiteSpace(resolvedPath) &&
                             File.Exists(resolvedPath) &&
                             string.Equals(
                                 Path.GetFullPath(resolvedPath),
                                 Path.GetFullPath(targetPath),
                                 StringComparison.OrdinalIgnoreCase);
            global::SVL.Core.Platform.Diagnostics.RuntimeDebugLog.WriteLine(
                $"[IconResolver] TryWriteDefaultSmapiIcon {(isResolved ? "成功" : "校验失败")}: " +
                $"target={targetPath}, exists={File.Exists(targetPath)}, resolved={resolvedPath}");
            return isResolved;
        }
        catch (Exception ex)
        {
            global::SVL.Core.Platform.Diagnostics.RuntimeDebugLog.WriteLine($"[IconResolver] TryWriteDefaultSmapiIcon 异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 判断 SMAPI 专属图标是否仍是安装流程生成的默认图标。
    /// </summary>
    internal static bool IsGeneratedSmapiIcon(string? instancePath)
    {
        var storageDirectory = ResolveStorageDirectory(instancePath);
        return !string.IsNullOrWhiteSpace(storageDirectory) &&
               File.Exists(Path.Combine(storageDirectory, GeneratedSmapiIconMarkerFileName));
    }

    /// <summary>
    /// 用户选择图标或整合包成功写入自定义图标后，移除默认图标标记。
    /// </summary>
    internal static void TryDeleteGeneratedSmapiIconMarker(string? instancePath)
    {
        try
        {
            var storageDirectory = ResolveStorageDirectory(instancePath);
            if (string.IsNullOrWhiteSpace(storageDirectory))
            {
                return;
            }

            var markerPath = Path.Combine(storageDirectory, GeneratedSmapiIconMarkerFileName);
            if (File.Exists(markerPath))
            {
                File.Delete(markerPath);
            }
        }
        catch
        {
            // 标记只是元数据，清理失败不应阻断图标保存或整合包安装。
        }
    }

    private static bool IsBundledIcon(string filePath, string assetFileName)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return false;
            }

            using var assetStream = OpenBundledIcon(assetFileName);
            var assetBytes = ReadStreamBytes(assetStream);
            var fileBytes = File.ReadAllBytes(filePath);
            return assetBytes.AsSpan().SequenceEqual(fileBytes);
        }
        catch
        {
            // 资源尚未初始化或文件不是可读取的图片时，按用户自定义图标处理，
            // 确保不会误覆盖用户文件。
            return false;
        }
    }

    private static byte[] ReadStreamBytes(Stream source)
    {
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static Stream OpenBundledIcon(string assetFileName)
    {
        var uri = new Uri($"avares://SVL.Avalonia/Assets/Icons/{assetFileName}", UriKind.Absolute);
        try
        {
            return AssetLoader.Open(uri);
        }
        catch (InvalidOperationException)
        {
            // 服务层也可能在 Avalonia 框架完成初始化前被调用（例如恢复任务/单元测试）。
            // 这时全局 AssetLoader 尚未注册，改用显式绑定当前程序集的标准加载器，
            // 保证默认 SMAPI 图标不会因为调用时机不同而写入失败。
            var loader = new StandardAssetLoader();
            loader.SetDefaultAssembly(typeof(InstanceIconResolver).Assembly);
            return loader.Open(uri, null);
        }
    }
}
