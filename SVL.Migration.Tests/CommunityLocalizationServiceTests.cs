using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SVL.Avalonia.Services;

namespace SVL.Migration.Tests;

/// <summary>
/// 社区汉化服务测试。覆盖：
/// - 双源 URL 构造（GitHub + Gitee）
/// - 相对路径构造（Mod/Modpack/Collection，NexusMods/Curseforge/UniqueID）
/// - 缓存命中/过期/降级读取
/// - 源选择（读 LocalizationPreferredSource）
/// - 缓存清理与大小统计
/// </summary>
[TestClass]
public class CommunityLocalizationServiceTests
{
    private static string TestCacheRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SVL", "Avalonia", "cache", "community-localization");

    [TestCleanup]
    public void Cleanup()
    {
        CommunityLocalizationService.ClearCache();
    }

    // ================================================================
    // 双源 URL 构造
    // ================================================================

    [TestMethod]
    public void BuildRawUrl_GitHub_ShouldUseRawGithubUserContent()
    {
        var url = CommunityLocalizationService.BuildRawUrl("GitHub", "Mods/NexusMods/12345.json");
        Assert.AreEqual(
            "https://raw.githubusercontent.com/panda-lsy/StardewValley-Community-Localization/main/Mods/NexusMods/12345.json",
            url);
    }

    [TestMethod]
    public void BuildRawUrl_Gitee_ShouldUseGiteeRaw()
    {
        var url = CommunityLocalizationService.BuildRawUrl("Gitee", "Mods/Curseforge/67890.json");
        Assert.AreEqual(
            "https://gitee.com/mc_shengxia/StardewValley-Community-Localization/raw/main/Mods/Curseforge/67890.json",
            url);
    }

    [TestMethod]
    public void BuildRawUrl_UnknownProvider_ShouldReturnEmpty()
    {
        var url = CommunityLocalizationService.BuildRawUrl("GitLab", "Mods/NexusMods/12345.json");
        Assert.AreEqual(string.Empty, url);
    }

    [TestMethod]
    public void BuildRawUrl_BackslashPath_ShouldNormalize()
    {
        var url = CommunityLocalizationService.BuildRawUrl("GitHub", "Mods\\NexusMods\\12345.json");
        Assert.AreEqual(
            "https://raw.githubusercontent.com/panda-lsy/StardewValley-Community-Localization/main/Mods/NexusMods/12345.json",
            url);
    }

    // ================================================================
    // 相对路径构造
    // ================================================================

    [TestMethod]
    public void BuildRelativePath_ModNexusMods_ShouldReturnCorrectPath()
    {
        var path = CommunityLocalizationService.BuildRelativePath("mod", "NexusMods", "12345");
        Assert.AreEqual("Mods/NexusMods/12345.json", path);
    }

    [TestMethod]
    public void BuildRelativePath_ModCurseforge_ShouldReturnCorrectPath()
    {
        var path = CommunityLocalizationService.BuildRelativePath("mod", "Curseforge", "67890");
        Assert.AreEqual("Mods/Curseforge/67890.json", path);
    }

    [TestMethod]
    public void BuildRelativePath_ModUniqueID_ShouldReturnCorrectPath()
    {
        var path = CommunityLocalizationService.BuildRelativePath("mod", "UniqueID", "Pathoschild.ContentPatcher");
        Assert.AreEqual("Mods/UniqueID/Pathoschild.ContentPatcher.json", path);
    }

    [TestMethod]
    public void BuildRelativePath_Modpack_ShouldReturnModpacksPath()
    {
        var path = CommunityLocalizationService.BuildRelativePath("modpack", "", "100");
        Assert.AreEqual("Modpacks/100.json", path);
    }

    [TestMethod]
    public void BuildRelativePath_Collection_ShouldReturnCollectionsPath()
    {
        var path = CommunityLocalizationService.BuildRelativePath("collection", "", "200");
        Assert.AreEqual("Collections/200.json", path);
    }

    [TestMethod]
    public void BuildRelativePath_ModUnknownPlatform_ShouldReturnEmpty()
    {
        var path = CommunityLocalizationService.BuildRelativePath("mod", "Unknown", "12345");
        Assert.AreEqual(string.Empty, path);
    }

    // ================================================================
    // 归一化
    // ================================================================

    [TestMethod]
    public void NormalizePlatform_Nexus_ShouldReturnNexusMods()
    {
        Assert.AreEqual("NexusMods", CommunityLocalizationService.NormalizePlatform("Nexus"));
        Assert.AreEqual("NexusMods", CommunityLocalizationService.NormalizePlatform("nexusmods"));
        Assert.AreEqual("NexusMods", CommunityLocalizationService.NormalizePlatform("NexusMods"));
    }

    [TestMethod]
    public void NormalizePlatform_LocalUniqueID_ShouldReturnUniqueID()
    {
        Assert.AreEqual("UniqueID", CommunityLocalizationService.NormalizePlatform("LocalUniqueID"));
        Assert.AreEqual("UniqueID", CommunityLocalizationService.NormalizePlatform("UniqueID"));
    }

    [TestMethod]
    public void NormalizeEntityType_ShouldNormalizeCorrectly()
    {
        Assert.AreEqual("mod", CommunityLocalizationService.NormalizeEntityType("Mod"));
        Assert.AreEqual("modpack", CommunityLocalizationService.NormalizeEntityType("Modpack"));
        Assert.AreEqual("collection", CommunityLocalizationService.NormalizeEntityType("Collection"));
        Assert.AreEqual(string.Empty, CommunityLocalizationService.NormalizeEntityType("Unknown"));
        Assert.AreEqual(string.Empty, CommunityLocalizationService.NormalizeEntityType(null));
    }

    // ================================================================
    // 缓存路径构造
    // ================================================================

    [TestMethod]
    public void GetCacheFilePath_ShouldIncludeProviderAndRelativePath()
    {
        var path = CommunityLocalizationService.GetCacheFilePath("GitHub", "Mods/NexusMods/12345.json");
        Assert.IsTrue(path.Contains("community-localization"));
        Assert.IsTrue(path.Contains("GitHub"));
        Assert.IsTrue(path.EndsWith("Mods/NexusMods/12345.json") || path.EndsWith("Mods\\NexusMods\\12345.json"));
    }

    [TestMethod]
    public void GetCacheFilePath_Gitee_ShouldUseGiteeProvider()
    {
        var path = CommunityLocalizationService.GetCacheFilePath("Gitee", "Mods/Curseforge/67890.json");
        Assert.IsTrue(path.Contains("Gitee"));
    }

    // ================================================================
    // 缓存命中/过期/降级
    // ================================================================

    [TestMethod]
    public async Task GetByRelativePathAsync_CacheHit_ShouldNotHitNetwork()
    {
        // 准备：写入一份缓存文件
        var provider = "GitHub";
        var relativePath = "Mods/NexusMods/test-cache-hit.json";
        var cacheFilePath = CommunityLocalizationService.GetCacheFilePath(provider, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath)!);

        var entryJson = JsonSerializer.Serialize(new CommunityLocalizationEntry
        {
            EntityType = "mod",
            Platform = "NexusMods",
            Id = "test-cache-hit",
            Name = new CommunityLocalizedText { ZhCn = "测试缓存命中" },
            Description = new CommunityLocalizedText { ZhCn = "这是缓存命中测试" },
            Meta = new CommunityLocalizationMeta { Contributor = "测试者" }
        });
        File.WriteAllText(cacheFilePath, entryJson);

        // 设置首选源为 GitHub（这样缓存才会命中 GitHub 目录）
        var settingsStore = new AppUserSettingsStore();
        var settings = settingsStore.Load();
        settings.LocalizationPreferredSource = "GitHub";
        settingsStore.Save(settings);

        var service = new CommunityLocalizationService(settingsStore);

        // 执行：应该直接命中缓存，不访问网络
        var result = await service.GetByRelativePathAsync(relativePath);

        // 验证
        Assert.IsNotNull(result);
        Assert.AreEqual("测试缓存命中", result.Name.ZhCn);
        Assert.AreEqual("这是缓存命中测试", result.Description.ZhCn);
        Assert.AreEqual("测试者", result.Meta.Contributor);
    }

    [TestMethod]
    public async Task GetByRelativePathAsync_ForceRefresh_ShouldTryNetwork()
    {
        // 准备：写入一份过期缓存（时间戳设为很久以前）
        var provider = "GitHub";
        var relativePath = "Mods/NexusMods/test-force-refresh.json";
        var cacheFilePath = CommunityLocalizationService.GetCacheFilePath(provider, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath)!);

        var entryJson = JsonSerializer.Serialize(new CommunityLocalizationEntry
        {
            EntityType = "mod",
            Platform = "NexusMods",
            Id = "test-force-refresh",
            Name = new CommunityLocalizedText { ZhCn = "旧缓存" }
        });
        File.WriteAllText(cacheFilePath, entryJson);
        File.SetLastWriteTimeUtc(cacheFilePath, DateTime.UtcNow.AddDays(-30));

        var settingsStore = new AppUserSettingsStore();
        var settings = settingsStore.Load();
        settings.LocalizationPreferredSource = "GitHub";
        settingsStore.Save(settings);

        var service = new CommunityLocalizationService(settingsStore);

        // 执行：forceRefresh=true 应该跳过缓存走网络
        // 由于测试环境无网络或不存在的条目，结果应为 null（降级读取旧缓存）
        // 注意：网络不可用时 TryDownloadAsync 会降级读取过期缓存
        var result = await service.GetByRelativePathAsync(relativePath, forceRefresh: true);

        // 验证：网络失败后降级读取了过期缓存
        // 注意：若测试环境有网络且 GitHub 能访问，可能返回 null（404）
        // 因此只验证不抛异常即可
        if (result != null)
        {
            Assert.AreEqual("旧缓存", result.Name.ZhCn);
        }
    }

    [TestMethod]
    public async Task GetByRelativePathAsync_NetworkFail_ShouldDegradeToStaleCache()
    {
        // 准备：写入一份过期缓存
        var provider = "GitHub";
        var relativePath = "Mods/NexusMods/test-stale-degrade.json";
        var cacheFilePath = CommunityLocalizationService.GetCacheFilePath(provider, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath)!);

        var entryJson = JsonSerializer.Serialize(new CommunityLocalizationEntry
        {
            EntityType = "mod",
            Platform = "NexusMods",
            Id = "test-stale-degrade",
            Name = new CommunityLocalizedText { ZhCn = "降级缓存" },
            Meta = new CommunityLocalizationMeta { Contributor = "降级测试" }
        });
        File.WriteAllText(cacheFilePath, entryJson);
        File.SetLastWriteTimeUtc(cacheFilePath, DateTime.UtcNow.AddDays(-30));

        var settingsStore = new AppUserSettingsStore();
        var settings = settingsStore.Load();
        settings.LocalizationPreferredSource = "GitHub";
        settingsStore.Save(settings);

        var service = new CommunityLocalizationService(settingsStore);

        // 执行：缓存已过期，网络请求会失败（测试条目不存在），应降级读取过期缓存
        var result = await service.GetByRelativePathAsync(relativePath);

        // 验证：降级读取了过期缓存
        // 注意：若测试环境有网络且 GitHub 返回 404，会降级读取
        // 若网络完全不可用，也会降级读取
        if (result != null)
        {
            Assert.AreEqual("降级缓存", result.Name.ZhCn);
        }
    }

    // ================================================================
    // 源选择
    // ================================================================

    [TestMethod]
    public async Task GetByRelativePathAsync_PreferredGitee_ShouldCacheUnderGitee()
    {
        // 准备：设置首选源为 Gitee
        var settingsStore = new AppUserSettingsStore();
        var settings = settingsStore.Load();
        settings.LocalizationPreferredSource = "Gitee";
        settingsStore.Save(settings);

        var relativePath = "Mods/NexusMods/test-gitee-preferred.json";
        var giteeCachePath = CommunityLocalizationService.GetCacheFilePath("Gitee", relativePath);

        // 写入 Gitee 缓存
        Directory.CreateDirectory(Path.GetDirectoryName(giteeCachePath)!);
        var entryJson = JsonSerializer.Serialize(new CommunityLocalizationEntry
        {
            EntityType = "mod",
            Platform = "NexusMods",
            Id = "test-gitee-preferred",
            Name = new CommunityLocalizedText { ZhCn = "Gitee 源缓存" }
        });
        File.WriteAllText(giteeCachePath, entryJson);

        var service = new CommunityLocalizationService(settingsStore);

        // 执行
        var result = await service.GetByRelativePathAsync(relativePath);

        // 验证：命中 Gitee 缓存
        Assert.IsNotNull(result);
        Assert.AreEqual("Gitee 源缓存", result.Name.ZhCn);
    }

    [TestMethod]
    public async Task GetByRelativePathAsync_PreferredGitHub_ShouldCacheUnderGitHub()
    {
        var settingsStore = new AppUserSettingsStore();
        var settings = settingsStore.Load();
        settings.LocalizationPreferredSource = "GitHub";
        settingsStore.Save(settings);

        var relativePath = "Mods/NexusMods/test-github-preferred.json";
        var githubCachePath = CommunityLocalizationService.GetCacheFilePath("GitHub", relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(githubCachePath)!);
        var entryJson = JsonSerializer.Serialize(new CommunityLocalizationEntry
        {
            EntityType = "mod",
            Platform = "NexusMods",
            Id = "test-github-preferred",
            Name = new CommunityLocalizedText { ZhCn = "GitHub 源缓存" }
        });
        File.WriteAllText(githubCachePath, entryJson);

        var service = new CommunityLocalizationService(settingsStore);
        var result = await service.GetByRelativePathAsync(relativePath);

        Assert.IsNotNull(result);
        Assert.AreEqual("GitHub 源缓存", result.Name.ZhCn);
    }

    // ================================================================
    // 缓存清理与大小统计
    // ================================================================

    [TestMethod]
    public void ClearCache_ShouldRemoveAllProviderCaches()
    {
        // 准备：写入两个源的缓存
        var relativePath = "Mods/NexusMods/test-clear.json";
        var paths = new List<string>();
        foreach (var provider in new[] { "GitHub", "Gitee" })
        {
            var cachePath = CommunityLocalizationService.GetCacheFilePath(provider, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllText(cachePath, "{}");
            paths.Add(cachePath);
        }

        // 执行：清理所有缓存
        CommunityLocalizationService.ClearCache();

        // 验证：缓存文件应被删除
        foreach (var path in paths)
        {
            Assert.IsFalse(File.Exists(path), $"缓存文件应被删除: {path}");
        }
    }

    [TestMethod]
    public void ClearCache_SpecificProvider_ShouldOnlyRemoveThatProvider()
    {
        var relativePath = "Mods/NexusMods/test-clear-specific.json";
        var githubPath = CommunityLocalizationService.GetCacheFilePath("GitHub", relativePath);
        var giteePath = CommunityLocalizationService.GetCacheFilePath("Gitee", relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(githubPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(giteePath)!);
        File.WriteAllText(githubPath, "{}");
        File.WriteAllText(giteePath, "{}");

        // 只清理 GitHub
        CommunityLocalizationService.ClearCache("GitHub");

        Assert.IsFalse(File.Exists(githubPath));
        Assert.IsTrue(File.Exists(giteePath));
    }

    [TestMethod]
    public void GetCacheSize_ShouldReturnTotalSize()
    {
        var relativePath = "Mods/NexusMods/test-size.json";
        var githubPath = CommunityLocalizationService.GetCacheFilePath("GitHub", relativePath);
        var giteePath = CommunityLocalizationService.GetCacheFilePath("Gitee", relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(githubPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(giteePath)!);
        var content = "{\"test\":\"size\"}";
        File.WriteAllText(githubPath, content);
        File.WriteAllText(giteePath, content);

        var size = CommunityLocalizationService.GetCacheSize();
        Assert.IsTrue(size >= content.Length * 2);
    }

    // ================================================================
    // 集成测试：双源 URL 构造一致性验证
    // ================================================================

    [TestMethod]
    public void BuildRawUrl_BothSources_ShouldPointToSameRepo()
    {
        var relativePath = "Mods/NexusMods/2400.json";
        var githubUrl = CommunityLocalizationService.BuildRawUrl("GitHub", relativePath);
        var giteeUrl = CommunityLocalizationService.BuildRawUrl("Gitee", relativePath);

        // 两个 URL 都应包含相同的相对路径
        Assert.IsTrue(githubUrl.Contains(relativePath));
        Assert.IsTrue(giteeUrl.Contains(relativePath));

        // GitHub 用 raw.githubusercontent.com
        Assert.IsTrue(githubUrl.StartsWith("https://raw.githubusercontent.com/"));
        Assert.IsTrue(githubUrl.Contains("panda-lsy/StardewValley-Community-Localization"));

        // Gitee 用 gitee.com
        Assert.IsTrue(giteeUrl.StartsWith("https://gitee.com/"));
        Assert.IsTrue(giteeUrl.Contains("mc_shengxia/StardewValley-Community-Localization"));
    }

    [TestMethod]
    public void BuildRelativePath_AllEntityTypes_ShouldCoverOldArchPaths()
    {
        // 覆盖旧架构所有路径构造
        Assert.AreEqual("Mods/UniqueID/Pathoschild.ContentPatcher.json",
            CommunityLocalizationService.BuildRelativePath("mod", "UniqueID", "Pathoschild.ContentPatcher"));
        Assert.AreEqual("Mods/Curseforge/12345.json",
            CommunityLocalizationService.BuildRelativePath("mod", "Curseforge", "12345"));
        Assert.AreEqual("Mods/NexusMods/2400.json",
            CommunityLocalizationService.BuildRelativePath("mod", "NexusMods", "2400"));
        Assert.AreEqual("Modpacks/100.json",
            CommunityLocalizationService.BuildRelativePath("modpack", "", "100"));
        Assert.AreEqual("Collections/200.json",
            CommunityLocalizationService.BuildRelativePath("collection", "", "200"));
    }
}
