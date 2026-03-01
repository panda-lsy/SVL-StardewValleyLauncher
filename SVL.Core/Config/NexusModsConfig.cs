using System;
using System.IO;
using System.Text.Json;
using SVL.Core.Logging;
using SVL.Core.Stardew.ResourceProject.NexusMods;

namespace SVL.Core.Config;

/// <summary>
/// NexusMods API 配置服务
/// </summary>
public static class NexusModsConfig
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SVL", "nexusmods.json");

    private static string? _cachedApiKey;

    /// <summary>
    /// 获取 NexusMods API 密钥
    /// </summary>
    public static string? GetApiKey()
    {
        if (!string.IsNullOrEmpty(_cachedApiKey))
        {
            return _cachedApiKey;
        }

        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize<NexusModsConfigModel>(json);
                if (config != null && !string.IsNullOrEmpty(config.ApiKey))
                {
                    _cachedApiKey = config.ApiKey;
                    Log.Info("[NexusModsConfig] ✓ 已加载 NexusMods API 密钥");
                    return _cachedApiKey;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NexusModsConfig] 加载 NexusMods 配置失败");
        }

        return null;
    }

    /// <summary>
    /// 保存 NexusMods API 密钥
    /// </summary>
    public static bool SaveApiKey(string apiKey)
    {
        try
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                Log.Error("[NexusModsConfig] API 密钥为空");
                return false;
            }

            // 保存配置
            var config = new NexusModsConfigModel { ApiKey = apiKey };
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(config, options);

            var directory = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(ConfigPath, json);

            _cachedApiKey = apiKey;

            // 注意：NexusMods 现在使用 OAuth 认证，不再使用 API Key
            // API Key 配置已弃用，请使用 OAuth 登录

            Log.Info("[NexusModsConfig] ✓ 已保存 NexusMods API 密钥（已弃用，建议使用 OAuth）");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NexusModsConfig] 保存 NexusMods 配置失败");
            return false;
        }
    }

    /// <summary>
    /// 清除缓存的 API 密钥
    /// </summary>
    public static void ClearCache()
    {
        _cachedApiKey = null;
    }

    /// <summary>
    /// 初始化 NexusMods API（在应用启动时调用）
    /// 已弃用：请使用 ApplicationService.InitializeApiKeys() 从 AppConfig 统一加载
    /// </summary>
    public static void Initialize()
    {
        // 此方法已弃用，不再使用独立的配置文件
        // API Keys 统一通过 AppConfig 管理
        Log.Info("[NexusModsConfig] ⚠ Initialize() 已弃用，使用 AppConfig 统一管理 API Keys");
    }

    private class NexusModsConfigModel
    {
        public string ApiKey { get; set; } = string.Empty;
    }
}
