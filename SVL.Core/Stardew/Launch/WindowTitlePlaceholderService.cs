using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using SVL.Core.Logging;

namespace SVL.Core.Stardew.Launch;

/// <summary>
/// 窗口标题 Placeholder 替换服务
/// </summary>
public static class WindowTitlePlaceholderService
{
    /// <summary>
    /// Placeholder 定义
    /// </summary>
    public static readonly Dictionary<string, PlaceholderInfo> Placeholders = new()
    {
        { "ver", new PlaceholderInfo("<ver>", "原版游戏版本（如 1.6.15）", "游戏版本号") },
        { "smver", new PlaceholderInfo("<smver>", "SMAPI 版本（如 4.3.2）", "SMAPI 版本号") },
        { "modscount", new PlaceholderInfo("<modscount>", "已加载的模组数量", "模组数量") },
        { "name", new PlaceholderInfo("<name>", "实例名称", "实例名") }
    };

    /// <summary>
    /// 获取游戏信息（从 EXE 文件和 Mods 文件夹）
    /// </summary>
    public static GameInfo GetGameInfo(string? gamePath = null)
    {
        var info = new GameInfo();

        if (!string.IsNullOrEmpty(gamePath))
        {
            // 从 EXE 文件读取版本信息
            var exeInfo = ReadVersionsFromExe(gamePath);

            info.GameVersion = exeInfo.GameVersion;
            info.SMAPIVersion = exeInfo.SMAPIVersion;

            // 从 Mods 文件夹统计模组数量
            info.ModsCount = CountModsFromFolder(gamePath);

            Logging.Log.Info($"[PlaceholderService] Game info: Game={info.GameVersion}, SMAPI={info.SMAPIVersion}, Mods={info.ModsCount}");
        }

        return info;
    }

    /// <summary>
    /// 从 SMAPI 输出中解析游戏信息（已废弃，保留用于兼容性）
    /// </summary>
    public static GameInfo ParseSMAPIOutput(string[] outputLines, string? gamePath = null)
    {
        // 直接调用新的方法，忽略输出
        return GetGameInfo(gamePath);
    }

    /// <summary>
    /// 从 EXE 文件读取版本信息
    /// </summary>
    public static GameInfo ReadVersionsFromExe(string gamePath)
    {
        var info = new GameInfo();

        try
        {
            // 读取原版游戏版本
            var gameExePath = Path.Combine(gamePath, "Stardew Valley.exe");
            if (File.Exists(gameExePath))
            {
                var gameVersion = FileVersionInfo.GetVersionInfo(gameExePath).FileVersion;
                if (!string.IsNullOrEmpty(gameVersion))
                {
                    // 提取主版本号（如 1.6.15.24356 -> 1.6.15）
                    var versionMatch = Regex.Match(gameVersion, @"^(\d+\.\d+\.\d+)");
                    info.GameVersion = versionMatch.Success ? versionMatch.Groups[1].Value : gameVersion;
                    Logging.Log.Info($"[PlaceholderService] ✓ Game EXE version: {info.GameVersion}");
                }
            }

            // 读取 SMAPI 版本
            var smapiExePath = Path.Combine(gamePath, "StardewModdingAPI.exe");
            if (File.Exists(smapiExePath))
            {
                var smapiVersion = FileVersionInfo.GetVersionInfo(smapiExePath).FileVersion;
                if (!string.IsNullOrEmpty(smapiVersion))
                {
                    // 提取主版本号（如 4.3.2.0 -> 4.3.2）
                    var versionMatch = Regex.Match(smapiVersion, @"^(\d+\.\d+\.\d+)");
                    info.SMAPIVersion = versionMatch.Success ? versionMatch.Groups[1].Value : smapiVersion;
                    Logging.Log.Info($"[PlaceholderService] ✓ SMAPI EXE version: {info.SMAPIVersion}");
                }
            }
        }
        catch (Exception ex)
        {
            Logging.Log.Error(ex, "[PlaceholderService] Error reading versions from EXE files");
        }

        return info;
    }

    /// <summary>
    /// 替换标题中的 placeholder
    /// </summary>
    public static string ReplacePlaceholders(string template, GameInfo info, string? instanceName = null)
    {
        var result = template;

        // 替换 <ver>
        result = result.Replace("<ver>", info.GameVersion ?? "未知版本");

        // 替换 <smver>
        result = result.Replace("<smver>", info.SMAPIVersion ?? "未知");

        // 替换 <modscount>
        result = result.Replace("<modscount>", info.ModsCount > 0 ? info.ModsCount.ToString() : "0");

        // 替换 <name>
        result = result.Replace("<name>", instanceName ?? "实例");

        Logging.Log.Debug($"[PlaceholderService] '{template}' -> '{result}'");
        return result;
    }

    /// <summary>
    /// 获取默认窗口标题模板
    /// </summary>
    public static string GetDefaultTitleTemplate(bool isSMAPI)
    {
        return isSMAPI
            ? "Stardew Valley <ver> - running SMAPI <smver> with <modscount> mods"
            : "Stardew Valley <ver>";
    }

    /// <summary>
    /// 从 Mods 文件夹统计模组数量
    /// </summary>
    public static int CountModsFromFolder(string gamePath)
    {
        try
        {
            var modsPath = Path.Combine(gamePath, "Mods");
            if (!Directory.Exists(modsPath))
            {
                Logging.Log.Info($"[PlaceholderService] Mods folder not found: {modsPath}");
                return 0;
            }

            // 统计包含 manifest.json 文件的文件夹数量
            var modDirectories = Directory.GetDirectories(modsPath);
            int count = 0;

            foreach (var dir in modDirectories)
            {
                var manifestPath = Path.Combine(dir, "manifest.json");
                if (File.Exists(manifestPath))
                {
                    count++;
                }
            }

            Logging.Log.Info($"[PlaceholderService] ✓ Counted {count} mods from Mods folder ({modDirectories.Length} total folders)");
            return count;
        }
        catch (Exception ex)
        {
            Logging.Log.Error(ex, "[PlaceholderService] Error counting mods from folder");
            return 0;
        }
    }

    /// <summary>
    /// 游戏信息
    /// </summary>
    public class GameInfo
    {
        public string? GameVersion { get; set; }
        public string? SMAPIVersion { get; set; }
        public int ModsCount { get; set; }

        public override string ToString()
        {
            return $"GameVersion={GameVersion}, SMAPIVersion={SMAPIVersion}, ModsCount={ModsCount}";
        }
    }

    /// <summary>
    /// Placeholder 信息
    /// </summary>
    public class PlaceholderInfo
    {
        public string Tag { get; }
        public string Description { get; }
        public string Label { get; }

        public PlaceholderInfo(string tag, string description, string label)
        {
            Tag = tag;
            Description = description;
            Label = label;
        }

        public override string ToString()
        {
            return $"{Tag} - {Description}";
        }
    }
}
