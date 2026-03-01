using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SVL.Core.App.Configuration;
using SVL.Core.Logging;

namespace SVL.Core.Stardew.Instance;

public class InstanceManager
{
    private static readonly List<IStardewInstance> _instances = [];
    private static readonly Dictionary<StardewInstanceCardType, List<IStardewInstance>> _uiDict = [];

    public List<IStardewInstance> Instances => _instances;
    public Dictionary<StardewInstanceCardType, List<IStardewInstance>> UiDict => _uiDict;

    /// <summary>
    /// instances.json 文件路径
    /// </summary>
    private static string GetInstancesJsonPath()
    {
        var settingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SVL"
        );
        return Path.Combine(settingsFolder, "instances.json");
    }

    /// <summary>
    /// 从所有游戏路径的 versions 文件夹刷新实例列表
    /// 这会在启动时调用，确保 instances.json 与实际的 versions 文件夹同步
    /// </summary>
    public static void RefreshInstancesFromVersions()
    {
        try
        {
            Log.Info("[InstanceManager] 开始从 versions 文件夹刷新实例列表");

            // 1. 加载现有的 instances.json
            var existingInstances = SettingsService.LoadInstances();
            if (existingInstances == null || existingInstances.Count == 0)
            {
                Log.Info("[InstanceManager] 没有现有实例，跳过刷新");
                return;
            }

            // 2. 使用复合键 (GamePath, Name) 处理重复的实例
            var existingDict = existingInstances
                .GroupBy(i => new InstanceKey(i.GamePath, i.Name))
                .ToDictionary(
                    g => g.Key,
                    g =>
                    {
                        var first = g.First();
                        if (g.Count() > 1)
                        {
                            Log.Warn($"[InstanceManager] 发现重复的实例: GamePath={g.Key.GamePath}, Name={g.Key.Name}，共 {g.Count()} 个记录，将使用第一个记录 (ID: {first.Id})");
                        }
                        return first;
                    }
                );

            // 3. 获取所有唯一的游戏路径
            var uniqueGamePaths = existingInstances
                .Select(i => i.GamePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            Log.Info($"[InstanceManager] 发现 {uniqueGamePaths.Count} 个唯一游戏路径");

            // 4. 扫描每个游戏路径
            var updatedInstances = new List<GamePathInfo>();

            foreach (var gamePath in uniqueGamePaths)
            {
                if (string.IsNullOrEmpty(gamePath) || !Directory.Exists(gamePath))
                {
                    Log.Warn($"[InstanceManager] 游戏路径不存在或为空: {gamePath}，跳过");
                    continue;
                }

                // 4.1 添加非版本隔离的原版实例（Base）
                var vanillaName = "Stardew Valley";
                var vanillaKey = new InstanceKey(gamePath, vanillaName);
                var gameVersion = SVL.Core.Stardew.Instance.GamePathService.GetGameVersion(gamePath);

                if (existingDict.TryGetValue(vanillaKey, out var existingVanilla))
                {
                    // 已存在，保留原记录
                    existingVanilla.EnableIsolation = false;  // 确保标记为非隔离
                    if (!existingVanilla.Tags.Contains("Base"))
                    {
                        existingVanilla.Tags.Insert(0, "Base");  // 添加 Base 标签
                    }
                    updatedInstances.Add(existingVanilla);
                    existingDict.Remove(vanillaKey);
                    Log.Info($"[InstanceManager] ✓ 保留原版实例: {vanillaName} @ {gamePath}");
                }
                else
                {
                    // 创建新的原版实例
                    var newVanilla = new GamePathInfo
                    {
                        Id = Guid.NewGuid().ToString(),
                        Name = vanillaName,
                        GamePath = gamePath,
                        Version = gameVersion,
                        EnableIsolation = false,
                        IsSMAPIInstance = false,
                        Tags = new List<string> { "Base" }
                    };
                    updatedInstances.Add(newVanilla);
                    Log.Info($"[InstanceManager] ✓ 创建原版实例: {vanillaName} @ {gamePath}");
                }

                // 4.2 添加非版本隔离的 SMAPI 实例（如果安装了 SMAPI）
                var hasSMAPI = SVL.Core.Stardew.Instance.GamePathService.CheckSMAPI(gamePath, out var smapiVersion);
                if (hasSMAPI)
                {
                    var smapiName = "Stardew Valley (SMAPI)";
                    var smapiKey = new InstanceKey(gamePath, smapiName);

                    if (existingDict.TryGetValue(smapiKey, out var existingSmapi))
                    {
                        // 已存在，保留原记录
                        existingSmapi.EnableIsolation = false;  // 确保标记为非隔离
                        updatedInstances.Add(existingSmapi);
                        existingDict.Remove(smapiKey);
                        Log.Info($"[InstanceManager] ✓ 保留 SMAPI 实例: {smapiName} @ {gamePath}");
                    }
                    else
                    {
                        // 创建新的 SMAPI 实例
                        var newSmapi = new GamePathInfo
                        {
                            Id = Guid.NewGuid().ToString(),
                            Name = smapiName,
                            GamePath = gamePath,
                            Version = gameVersion,
                            SMAPIVersion = smapiVersion,
                            EnableIsolation = false,
                            IsSMAPIInstance = true,
                            HasSMAPIInstalled = true
                        };
                        updatedInstances.Add(newSmapi);
                        Log.Info($"[InstanceManager] ✓ 创建 SMAPI 实例: {smapiName} @ {gamePath}");
                    }
                }

                // 4.3 扫描 versions 文件夹中的版本隔离实例
                var versionsPath = Path.Combine(gamePath, "versions");
                if (!Directory.Exists(versionsPath))
                {
                    Log.Info($"[InstanceManager] 路径 {gamePath} 没有 versions 文件夹，跳过版本隔离扫描");
                    continue;
                }

                var versionDirs = Directory.GetDirectories(versionsPath);
                Log.Info($"[InstanceManager] 扫描路径 {gamePath}，发现 {versionDirs.Length} 个版本文件夹");

                foreach (var versionDir in versionDirs)
                {
                    var versionName = Path.GetFileName(versionDir);
                    var instanceKey = new InstanceKey(gamePath, versionName);

                    // 检查是否已有记录
                    if (existingDict.TryGetValue(instanceKey, out var existingInstance))
                    {
                        // 已存在，保留原记录（包含用户配置）
                        existingInstance.EnableIsolation = true;  // 确保标记为隔离
                        updatedInstances.Add(existingInstance);
                        existingDict.Remove(instanceKey);
                        Log.Info($"[InstanceManager] ✓ 保留版本隔离实例: {versionName} @ {gamePath}");
                    }
                    else
                    {
                        // 新发现的版本，创建新记录
                        var newInstance = new GamePathInfo
                        {
                            Id = Guid.NewGuid().ToString(),
                            Name = versionName,
                            GamePath = gamePath,
                            EnableIsolation = true,
                            IsSMAPIInstance = true  // 假设 versions 文件夹中的都是 SMAPI 实例
                        };
                        updatedInstances.Add(newInstance);
                        Log.Info($"[InstanceManager] ✓ 发现新版本: {versionName} @ {gamePath}");
                    }
                }
            }

            // 5. 保存更新后的实例列表
            SettingsService.SaveInstances(updatedInstances);

            Log.Info($"[InstanceManager] ✓ 刷新完成：共 {updatedInstances.Count} 个实例记录");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[InstanceManager] 刷新实例列表失败");
        }
    }

    /// <summary>
    /// 实例复合键：用于唯一标识实例 (GamePath + Name)
    /// </summary>
    private class InstanceKey : IEquatable<InstanceKey>
    {
        public string GamePath { get; }
        public string Name { get; }

        public InstanceKey(string gamePath, string name)
        {
            GamePath = gamePath;
            Name = name;
        }

        public bool Equals(InstanceKey? other)
        {
            if (other is null)
                return false;

            return string.Equals(GamePath, other.GamePath, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as InstanceKey);
        }

        public override int GetHashCode()
        {
            var gamePathHash = GamePath?.ToUpperInvariant()?.GetHashCode() ?? 0;
            var nameHash = Name?.ToUpperInvariant()?.GetHashCode() ?? 0;
            return HashCode.Combine(gamePathHash, nameHash);
        }
    }

    public static async Task LoadInstancesAsync(string basePath, CancellationToken token = default)
    {
        _instances.Clear();

        // *** 启动时刷新：从 versions 文件夹同步实例列表 ***
        RefreshInstancesFromVersions();

        try
        {
            var instancesPath = Path.Combine(basePath, "instances");
            if (!Directory.Exists(instancesPath))
            {
                Directory.CreateDirectory(instancesPath);
                return;
            }

            foreach (var path in Directory.GetDirectories(instancesPath))
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    var instance = new StardewInstance(path, basePath);
                    instance.Load();

                    var configKey = System.IO.Path.GetFileName(path);
                    var isStarred = await IsStarredAsync(configKey);
                    var cardType = await GetCardTypeAsync(configKey);
                    var description = await LoadDescriptionAsync(configKey);

                    instance.CardType = cardType;
                    instance.IsStarred = isStarred;
                    instance.Description = description;

                    _instances.Add(instance);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"Failed to load instance: {path}");
                }
            }

            BuildUiDict();
            Log.Info($"Loaded {_instances.Count} instances");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load instances");
        }
    }

    public static IStardewInstance CreateInstance(string name, string basePath)
    {
        var instancePath = Path.Combine(basePath, "instances", name);
        if (Directory.Exists(instancePath))
        {
            throw new ArgumentException($"Instance already exists: {name}");
        }

        Directory.CreateDirectory(instancePath);
        Directory.CreateDirectory(Path.Combine(instancePath, "Mods"));

        var instance = new StardewInstance(instancePath, basePath);
        instance.CardType = StardewInstanceCardType.New;
        _instances.Add(instance);
        BuildUiDict();

        return instance;
    }

    private static void BuildUiDict()
    {
        _uiDict.Clear();
        _uiDict[StardewInstanceCardType.Starred] = _instances.Where(i => i.IsStarred).ToList();
        _uiDict[StardewInstanceCardType.Normal] = _instances.Where(i => !i.IsStarred).ToList();
        _uiDict[StardewInstanceCardType.Recent] = new List<IStardewInstance>();
    }

    private static async Task<bool> IsStarredAsync(string instanceId)
    {
        try
        {
            var provider = App.ConfigService.GetProvider(ConfigSource.Local);
            var config = await provider.LoadAsync<Dictionary<string, bool>>("starred");
            return config?.ContainsKey(instanceId) == true && config[instanceId];
        }
        catch
        {
            return false;
        }
    }

    private static async Task<StardewInstanceCardType> GetCardTypeAsync(string instanceId)
    {
        var recent = await LoadRecentListAsync();
        return recent.Contains(instanceId) ? StardewInstanceCardType.Recent : StardewInstanceCardType.Normal;
    }

    private static async Task<string> LoadDescriptionAsync(string instanceId)
    {
        try
        {
            var provider = App.ConfigService.GetProvider(ConfigSource.Local);
            var config = await provider.LoadAsync<Dictionary<string, string>>("descriptions");
            return config?.ContainsKey(instanceId) == true ? config[instanceId] : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<List<string>> LoadRecentListAsync()
    {
        try
        {
            var provider = App.ConfigService.GetProvider(ConfigSource.Local);
            var config = await provider.LoadAsync<RecentConfig>("recent");
            return config?.Recent ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    private class RecentConfig
    {
        public List<string> Recent { get; set; }
    }
}
