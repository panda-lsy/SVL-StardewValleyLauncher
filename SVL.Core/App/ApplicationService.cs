using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using SVL.Core.App.Configuration;
using SVL.Core.Config;
using SVL.Core.Logging;
using SVL.Core.Stardew;
using SVL.Core.Stardew.Instance;
using SVL.Core.Utils;

namespace SVL.Core.App;

[LifecycleService(LifecycleState.Loading, Priority = 1919810)]
[LifecycleScope("app", "应用核心")]
public sealed partial class ApplicationService
{
    private static readonly string _appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>
    /// 获取 SVL 基础路径（exe 所在目录下的 SVL 文件夹）
    /// </summary>
    private static string BasePath
    {
        get
        {
            try
            {
                // 尝试多种方式获取 exe 所在目录
                string exeDirectory = null;

                // 方法1：使用 Assembly.Location
                try
                {
                    var exePath = Assembly.GetExecutingAssembly().Location;
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        exeDirectory = Path.GetDirectoryName(exePath);
                    }
                }
                catch { }

                // 方法2：使用 AppDomain.CurrentDomain.BaseDirectory
                if (string.IsNullOrEmpty(exeDirectory))
                {
                    try
                    {
                        exeDirectory = AppDomain.CurrentDomain.BaseDirectory;
                    }
                    catch { }
                }

                // 如果所有方法都失败，使用 AppData
                if (string.IsNullOrEmpty(exeDirectory))
                {
                    Log.Info("[ApplicationService] Unable to determine exe directory, using AppData");
                    return Path.Combine(_appDataPath, "SVL");
                }

                // 使用 exe 所在目录下的 SVL 文件夹
                var svlPath = Path.Combine(exeDirectory, "SVL");
                return svlPath;
            }
            catch (Exception ex)
            {
                Log.Warn("Failed to get exe directory, falling back to AppData", ex);
                return Path.Combine(_appDataPath, "SVL");
            }
        }
    }

    [LifecycleStart]
    private static void Start()
    {
        Log.Info($"Application Service started. Base path: {BasePath}");

        // 初始化 API Keys（从统一的 AppConfig 读取，必须在最前面）
        InitializeApiKeys();

        // 检测管理员权限
        if (!AdminHelper.IsRunningAsAdmin())
        {
            Log.Warn("[ApplicationService] 未以管理员身份运行，符号链接和目录连接功能可能受限");
            Log.Warn("[ApplicationService] 建议右键点击应用 -> 以管理员身份运行");
        }
        else
        {
            Log.Info("[ApplicationService] ✓ 已具有管理员权限");
        }

        if (!Directory.Exists(BasePath))
        {
            Directory.CreateDirectory(BasePath);
        }

        var instancesPath = Path.Combine(BasePath, "instances");
        if (!Directory.Exists(instancesPath))
        {
            Directory.CreateDirectory(instancesPath);
        }
    }

    /// <summary>
    /// 初始化 API 相关运行时配置
    /// </summary>
    private static void InitializeApiKeys()
    {
        try
        {
            var settings = AppConfig.GetSettings();

            // 初始化通用搜索缓存配置
            try
            {
                SVL.Core.IO.SearchCacheService.IsEnabled = settings.EnableNexusModsSearchCache;
                var minutes = settings.CacheRetentionMinutes <= 0 ? 60 : settings.CacheRetentionMinutes;
                SVL.Core.IO.SearchCacheService.DefaultTtl = TimeSpan.FromMinutes(minutes);
                Log.Info($"[ApplicationService] 搜索缓存: {(SVL.Core.IO.SearchCacheService.IsEnabled ? "启用" : "禁用")}, TTL={minutes} 分钟");
            }
            catch (Exception ex)
            {
                Log.Warn("[ApplicationService] 初始化搜索缓存配置失败", ex);
            }

            // NexusMods 现在使用 OAuth 认证
            // OAuth Token 通过 NexusLoginDialogViewModel 管理
            // 不需要在启动时手动设置 API Key
            if (!string.IsNullOrEmpty(settings.NexusModsOAuthToken))
            {
                Log.Info("[ApplicationService] ✓ NexusMods OAuth Token 已配置");
            }
            else
            {
                Log.Info("[ApplicationService] ⚠ NexusMods OAuth Token 未配置（如需使用 NexusMods，请登录）");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[ApplicationService] 初始化 API Keys 失败");
        }
    }

    [LifecycleStop]
    private static void Stop()
    {
        Log.Info("Application Service stopped");
    }

    public static async Task InitializeAsync()
    {
        // 先启动 BeforeLoading 状态（单实例服务等）
        await Lifecycle.StartAsync(LifecycleState.BeforeLoading);
        Log.Info("BeforeLoading services initialized");

        // 再启动 Loading 状态
        await Lifecycle.StartAsync(LifecycleState.Loading);
        Log.Info("All services initialized");

        await LoadInstancesAsync();
    }

    public static async Task ShutdownAsync()
    {
        Log.Info("Shutting down application...");
        await Lifecycle.StopAsync(LifecycleState.Stopping);
        await Lifecycle.StopAsync(LifecycleState.BeforeStop);
        Log.Info("Application shutdown complete");
    }

    private static async Task LoadInstancesAsync()
    {
        try
        {
            await InstanceManager.LoadInstancesAsync(BasePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load instances");
        }
    }
}
