using System;
using System.IO;
using System.Text.Json;
using SVL.Core.Logging;
using SVL.Core.Stardew.Instance;

namespace SVL.Core.Config;

/// <summary>
/// 游戏路径配置服务
/// </summary>
public static class GamePathConfig
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SVL", "gamepath.json");

    private static string? _cachedGamePath;

    /// <summary>
    /// 获取游戏本体路径
    /// </summary>
    public static string? GetGamePath()
    {
        if (!string.IsNullOrEmpty(_cachedGamePath))
        {
            return _cachedGamePath;
        }

        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize<GamePathConfigModel>(json);
                if (config != null && !string.IsNullOrEmpty(config.GamePath))
                {
                    // 验证路径是否仍然有效
                    if (GamePathService.IsValidGamePath(config.GamePath))
                    {
                        _cachedGamePath = config.GamePath;
                        Log.Info($"[GamePathConfig] 加载游戏路径: {_cachedGamePath}");
                        return _cachedGamePath;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[GamePathConfig] 加载游戏路径配置失败");
        }

        return null;
    }

    /// <summary>
    /// 保存游戏本体路径
    /// </summary>
    public static bool SaveGamePath(string gamePath)
    {
        try
        {
            // 验证路径
            if (!Directory.Exists(gamePath))
            {
                Log.Error($"[GamePathConfig] 游戏路径不存在: {gamePath}");
                return false;
            }

            if (!GamePathService.IsValidGamePath(gamePath))
            {
                Log.Error($"[GamePathConfig] 游戏路径不包含可识别的游戏核心文件: {gamePath}");
                return false;
            }

            // 保存配置
            var config = new GamePathConfigModel { GamePath = gamePath };
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(config, options);

            var directory = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(ConfigPath, json);

            _cachedGamePath = gamePath;
            Log.Info($"[GamePathConfig] ✓ 保存游戏路径: {gamePath}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[GamePathConfig] 保存游戏路径配置失败");
            return false;
        }
    }

    /// <summary>
    /// 清除缓存的路径
    /// </summary>
    public static void ClearCache()
    {
        _cachedGamePath = null;
    }

    private class GamePathConfigModel
    {
        public string GamePath { get; set; } = string.Empty;
    }
}
