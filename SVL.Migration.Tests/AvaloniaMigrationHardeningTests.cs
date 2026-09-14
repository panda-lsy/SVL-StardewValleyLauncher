using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpCompress.Common;
using SharpCompress.Writers.SevenZip;
using System.IO.Compression;
using System.Text.Json;
using SVL.Avalonia.Models;
using SVL.Avalonia.Services;
using SVL.Avalonia.ViewModels;
using SVL.Core.Platform.Abstractions;
using SVL.Core.Platform.IO;
using SVL.Core.Platform.Modpack;
using SVL.Core.Platform.Services;

namespace SVL.Migration.Tests;

[TestClass]
public sealed class AvaloniaMigrationHardeningTests
{
    [TestMethod]
    public void ModInstallTargetOptions_ShouldListAllSmapiTargetsWithFullPaths()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("该断言使用 Windows 驱动器路径，仅在 Windows 上验证完整路径语义。");
        }

        var options = ModInstallTargetOptions.Build(
        [
            new ModInstallTarget("SMAPI 4.5.2", @"D:\\Games\\Stardew Valley\\versions\\SMAPI 4.5.2", @"D:\\Games\\Stardew Valley", false, "4.5.2"),
            new ModInstallTarget("SMAPI 4.5.1", @"D:\\Games\\Stardew Valley\\versions\\SMAPI 4.5.1", @"D:\\Games\\Stardew Valley", false, "4.5.1"),
            // 同一路径可能同时来自手动注册和自动探测，只在对话框保留一项。
            new ModInstallTarget("旧名称", @"D:\\Games\\Stardew Valley\\versions\\SMAPI 4.5.2", @"D:\\Games\\Stardew Valley", false, "4.5.2")
        ]);

        Assert.AreEqual(2, options.Count);
        Assert.AreEqual("SMAPI 4.5.2", options[0].DisplayName);
        Assert.AreEqual(
            @"D:\\Games\\Stardew Valley\\versions\\SMAPI 4.5.2",
            options[0].TargetPath);
        Assert.AreEqual("SMAPI 4.5.1", options[1].DisplayName);
        Assert.IsTrue(options.All(option => Path.IsPathFullyQualified(option.TargetPath)));
    }

    [TestMethod]
    public void CacheManagement_ShouldExposeSharedRemoteImageCacheWithoutChangingLegacyPath()
    {
        Assert.AreEqual("图片/图标缓存", CacheManagementService.GetCategoryDisplayName(CacheCategory.Images));
        Assert.AreEqual("smapi-icon-cache", Path.GetFileName(CacheManagementService.GetCachePath(CacheCategory.Images)));

        var legacyPath = SVL.Avalonia.Converters.AssetImageConverter.GetLegacyIconCachePath(
            "https://example.com/icon.png");
        Assert.AreEqual("images", Path.GetFileName(Path.GetDirectoryName(legacyPath)));
    }

    [TestMethod]
    public async Task InstanceRegistry_ConcurrentUpsert_ShouldPreserveAllInstances()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-registry-concurrency-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new InstanceRegistryStore(root);
            var tasks = Enumerable.Range(0, 32)
                .Select(index => Task.Run(() => store.UpsertManualInstance(
                    $"Pack {index}",
                    Path.Combine(root, "versions", $"Pack {index}"))))
                .ToArray();

            await Task.WhenAll(tasks);

            var records = store.LoadManualInstances();
            Assert.AreEqual(32, records.Count);
            CollectionAssert.AreEquivalent(
                Enumerable.Range(0, 32).Select(index => $"Pack {index}").ToList(),
                records.Select(record => record.Name).ToList());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void InstanceRegistry_TransactionalPathOperations_ShouldAddRenameAndRemoveAtomically()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-registry-transaction-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new InstanceRegistryStore(root);
            var path = Path.Combine(root, "versions", "Pack");

            Assert.IsTrue(store.TryAddManualInstance("旧名称", path));
            Assert.IsFalse(store.TryAddManualInstance("重复名称", path));
            Assert.IsTrue(store.RenameManualInstancesByPath(path, "新名称"));
            Assert.AreEqual("新名称", store.LoadManualInstances().Single().Name);
            Assert.IsTrue(store.RemoveManualInstancesByPath(path));
            Assert.IsFalse(store.RemoveManualInstancesByPath(path));
            Assert.AreEqual(0, store.LoadManualInstances().Count);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void SearchPages_ShouldExposeWpfFiltersAndConfiguredDefaultSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-search-page-filter-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settingsStore = new AppUserSettingsStore(root);
            settingsStore.Save(new AppUserSettings { DefaultModSource = "Curseforge" });
            var catalog = new RemoteCatalogService(settingsStore);

            var modPage = new ModSearchPageViewModel(catalog);
            Assert.AreEqual("Curseforge", modPage.SelectedSource);
            CollectionAssert.Contains(modPage.GameVersions.ToList(), "全部");
            CollectionAssert.Contains(modPage.ModTypes.ToList(), "游戏内容");

            modPage.SelectedModType = "游戏内容";
            modPage.SelectedSource = "全部";
            Assert.IsFalse(modPage.IsModTypeFilterEnabled);
            Assert.AreEqual("全部", modPage.SelectedModType);
            StringAssert.Contains(modPage.ModTypeFilterHint, "无法按类型筛选");

            var modpackPage = new ModpackSearchPageViewModel(catalog);
            Assert.AreEqual("Curseforge", modpackPage.SelectedSource);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                try
                {
                    File.SetAttributes(Path.Combine(root, "version"), FileAttributes.Normal);
                }
                catch { }
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void SvlSourceEntries_ShouldMergeIncompleteSourcesWithManifestEntries()
    {
        using var sourcesDocument = JsonDocument.Parse(
            "[{\"displayName\":\"Content Patcher\",\"directoryName\":\"ContentPatcher\",\"source\":\"NexusMods\"}," +
            "{\"id\":\"Generic.ModConfigMenu\",\"directoryName\":\"GenericModConfigMenu\",\"source\":\"NexusMods\"}]",
            new JsonDocumentOptions { AllowTrailingCommas = true });
        using var manifestDocument = JsonDocument.Parse(
            "[{\"name\":\"Content Patcher\",\"source\":{\"platform\":\"NexusMods\",\"modId\":1915,\"fileId\":146548}}," +
            "{\"name\":\"Generic Mod Config Menu\",\"uniqueId\":\"Generic.ModConfigMenu\",\"source\":{\"platform\":\"NexusMods\",\"modId\":5098,\"fileId\":145906}}]");

        var mergeMethod = typeof(ModpackInstallService).GetMethod(
            "MergeSourceEntriesWithManifest",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(mergeMethod);

        var merged = (List<JsonElement>)mergeMethod!.Invoke(
            null,
            [
                sourcesDocument.RootElement.EnumerateArray().Select(item => item.Clone()).ToList(),
                manifestDocument.RootElement.EnumerateArray().Select(item => item.Clone()).ToList()
            ])!;

        Assert.AreEqual(2, merged.Count, "应补入 sources.json 遗漏但有完整来源的 Mod");

        var contentPatcher = merged.Single(entry =>
            entry.GetProperty("name").GetString() == "Content Patcher");
        var contentSource = contentPatcher.GetProperty("source");
        Assert.AreEqual(JsonValueKind.Object, contentSource.ValueKind);
        Assert.AreEqual(1915, contentSource.GetProperty("modId").GetInt64());
        Assert.AreEqual(146548, contentSource.GetProperty("fileId").GetInt64());
        Assert.AreEqual("NexusMods", contentSource.GetProperty("platform").GetString());

        var genericMenu = merged.Single(entry =>
            entry.GetProperty("name").GetString() == "Generic Mod Config Menu");
        Assert.AreEqual(145906, genericMenu.GetProperty("source").GetProperty("fileId").GetInt64());
    }

    [TestMethod]
    public void ExportSourceIdentity_ShouldRecoverLegacyNxmAndCurseforgeTokens()
    {
        var infer = typeof(VersionSettingsPageViewModel).GetMethod(
            "InferExportSourceIdentity",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(infer);

        var nxmArguments = new object[]
        {
            "未知",
            string.Empty,
            string.Empty,
            new[] { "nxm://stardewvalley/mods/29868/files/7448774?key=secret" }
        };
        infer!.Invoke(null, nxmArguments);

        Assert.AreEqual("NexusMods", nxmArguments[0]);
        Assert.AreEqual("29868", nxmArguments[1]);
        Assert.AreEqual("7448774", nxmArguments[2]);

        var curseforgeArguments = new object[]
        {
            string.Empty,
            string.Empty,
            string.Empty,
            new[] { "cf-1012214-5312529" }
        };
        infer.Invoke(null, curseforgeArguments);

        Assert.AreEqual("Curseforge", curseforgeArguments[0]);
        Assert.AreEqual("1012214", curseforgeArguments[1]);
        Assert.AreEqual("5312529", curseforgeArguments[2]);

        var explicitCurseforgeArguments = new object[]
        {
            "Curseforge",
            "1012214",
            "5312529",
            new[]
            {
                "https://www.nexusmods.com/stardewvalley/mods/29868?tab=files&file_id=7448774",
                "https://edge.forgecdn.net/files/5312/529/ContentPatcher.zip"
            }
        };
        infer.Invoke(null, explicitCurseforgeArguments);

        Assert.AreEqual("Curseforge", explicitCurseforgeArguments[0]);
        Assert.AreEqual("1012214", explicitCurseforgeArguments[1]);
        Assert.AreEqual("5312529", explicitCurseforgeArguments[2]);

        var zeroIdArguments = new object[]
        {
            "Unknown",
            "0",
            "0",
            new[] { "nxm://stardewvalley/mods/29868/files/7448774?key=secret" }
        };
        infer.Invoke(null, zeroIdArguments);

        Assert.AreEqual("NexusMods", zeroIdArguments[0]);
        Assert.AreEqual("29868", zeroIdArguments[1]);
        Assert.AreEqual("7448774", zeroIdArguments[2]);
    }

    [TestMethod]
    public void SourceProjectIdNormalization_ShouldKeepCurseforgeProjectAndRejectZero()
    {
        var normalizeProject = typeof(VersionSettingsPageViewModel).GetMethod(
            "NormalizeProjectId",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        var normalizePositive = typeof(VersionSettingsPageViewModel).GetMethod(
            "NormalizePositiveId",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(normalizeProject);
        Assert.IsNotNull(normalizePositive);

        Assert.AreEqual("1012214", normalizeProject!.Invoke(null, ["cf-1012214-5312529"]));
        Assert.AreEqual("1012214", normalizeProject.Invoke(
            null,
            ["https://www.curseforge.com/stardewvalley/mods/1012214/files/5312529"]));
        Assert.AreEqual(string.Empty, normalizePositive!.Invoke(null, ["0"]));
        Assert.AreEqual("5312529", normalizePositive.Invoke(null, ["5312529"]));
    }

    [TestMethod]
    public void InstanceNameValidator_RejectsReservedDeviceNamesWithExtensions()
    {
        Assert.IsFalse(InstanceNameValidator.IsValid("CON.txt"));
        Assert.IsFalse(InstanceNameValidator.IsValid("COM1.profile"));
        Assert.AreEqual("CON.foo_Instance", InstanceNameValidator.Sanitize("CON.foo"));
    }

    [TestMethod]
    public void InstanceRuntimePathResolver_DistinguishesLegacyAndCurrentLayouts()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-runtime-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            Assert.AreEqual(root, InstanceRuntimePathResolver.Resolve(root));

            var legacyRuntime = Path.Combine(root, "game");
            Directory.CreateDirectory(legacyRuntime);
            File.WriteAllText(Path.Combine(legacyRuntime, "Stardew Valley.dll"), string.Empty);

            Assert.AreEqual(legacyRuntime, InstanceRuntimePathResolver.Resolve(root));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void WindowTitleService_ReplacesSupportedPlaceholders()
    {
        var title = WindowTitleService.ReplacePlaceholders(
            "<name> | <ver> | <smver> | <modscount>",
            "1.6.15.24354",
            "4.5.2.0",
            43,
            "测试实例");

        Assert.AreEqual("测试实例 | 1.6.15.24354 | 4.5.2.0 | 43", title);
    }

    [TestMethod]
    public void SmapiExternalCallback_ShouldRemainConsumedBrieflyAfterWorkflowCompletes()
    {
        var viewModel = (DownloadPageViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
            typeof(DownloadPageViewModel));
        SetPrivateField(viewModel, "_nxmLinkParser", new NxmLinkParser());

        var active = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(
            StringComparer.OrdinalIgnoreCase);
        var recent = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["2400:12000"] = DateTimeOffset.UtcNow,
            ["2400:12001"] = DateTimeOffset.UtcNow.AddMinutes(-3)
        };
        SetPrivateField(viewModel, "_activeSmapiExternalWorkflows", active);
        SetPrivateField(viewModel, "_recentSmapiExternalCallbacks", recent);

        Assert.IsTrue(viewModel.IsActiveSmapiExternalCallback(
            "nxm://stardewvalley/mods/2400/files/12000?key=test"));
        Assert.IsFalse(viewModel.IsActiveSmapiExternalCallback(
            "nxm://stardewvalley/mods/2400/files/12001?key=test"));
    }

    [TestMethod]
    public void ModManageItem_ShouldToggleBetweenSourceAndLocalizedText()
    {
        var item = new ModManageItem
        {
            DisplayName = "Content Patcher",
            Description = "Content Patcher description"
        };

        item.SetLocalizationData(
            "Content Patcher",
            "内容补丁",
            "Content Patcher description",
            "内容补丁说明",
            useLocalizedText: true);

        Assert.AreEqual("内容补丁", item.DisplayName);
        Assert.AreEqual("内容补丁说明", item.Description);
        Assert.AreEqual("EN", item.LocalizationToggleButtonText);

        item.SetLocalizationLanguage(useLocalizedText: false);
        Assert.AreEqual("Content Patcher", item.DisplayName);
        Assert.AreEqual("Content Patcher description", item.Description);
        Assert.AreEqual("中", item.LocalizationToggleButtonText);
    }

    [TestMethod]
    public void InstalledSmapiPackageBuilder_ShouldCreateReusablePackageFromExistingInstance()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-installed-smapi-builder-" + Guid.NewGuid().ToString("N"));
        var basePath = Path.Combine(root, "Stardew Valley");
        var runtimePath = Path.Combine(basePath, "versions", "SMAPI 4.5.2", "game");
        var outputPath = Path.Combine(root, "cache");
        try
        {
            Directory.CreateDirectory(Path.Combine(runtimePath, "Mods"));
            File.WriteAllText(Path.Combine(runtimePath, "Stardew Valley.dll"), "game");
            File.WriteAllText(Path.Combine(runtimePath, "StardewModdingAPI.dll"), "smapi");
            File.WriteAllText(Path.Combine(runtimePath, "StardewModdingAPI.runtimeconfig.json"), "{}");
            File.WriteAllText(Path.Combine(runtimePath, "Mods", "should-not-be-copied.txt"), "mod");

            var builder = typeof(ModpackInstallService).Assembly.GetType(
                "SVL.Avalonia.Services.InstalledSmapiPackageBuilder");
            Assert.IsNotNull(builder);
            var method = builder!.GetMethod("TryCreate", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            Assert.IsNotNull(method);

            var arguments = new object[] { basePath, "4.5.2", null!, outputPath, "", "", "" };
            var created = (bool)method!.Invoke(null, arguments)!;

            Assert.IsTrue(created);
            var packagePath = (string)arguments[4]!;
            Assert.AreEqual(runtimePath, arguments[5]);
            Assert.AreEqual("4.5.2", arguments[6]);
            using var archive = System.IO.Compression.ZipFile.OpenRead(packagePath);
            Assert.IsTrue(archive.Entries.Any(entry =>
                entry.FullName.EndsWith("/install.dat", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void InstanceRuntimePathResolver_NormalizesVersionPathToOwningBase()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-base-path-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var versionRoot = Path.Combine(root, "versions", "My Pack");
            var legacyRuntime = Path.Combine(versionRoot, "game");
            Directory.CreateDirectory(legacyRuntime);

            Assert.AreEqual(root, InstanceRuntimePathResolver.ResolveBasePath(root));
            Assert.AreEqual(root, InstanceRuntimePathResolver.ResolveBasePath(versionRoot));
            Assert.AreEqual(root, InstanceRuntimePathResolver.ResolveBasePath(legacyRuntime));
            Assert.AreEqual(root, InstanceRuntimePathResolver.ResolveBasePath(Path.Combine(versionRoot, "Mods")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void DownloadTaskStateStore_PersistsStateAndNexusSourceIds()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-task-state-test-" + Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(root, "tasks.json.gz");
        try
        {
            Directory.CreateDirectory(root);
            var task = new DownloadTaskItem
            {
                Name = "test-mod",
                SourceModId = 2400,
                SourceFileId = 898372,
                TaskState = DownloadTaskState.Failed,
                Status = "下载失败（可重试）",
                InstalledDirectory = Path.Combine(root, "instance"),
                CollectionSlug = "cached-collection",
                CollectionRevision = 12,
                SpeedText = "2.3 MB/s",
                EtaText = "约 10 秒",
                TotalSizeText = "45.2 MB",
                DownloadedSizeText = "12.8 MB",
                SubProgressText = "3/8 已完成",
                SubProgress = 37,
                SkipConflictPrompt = true
            };
            task.SyncCollectionModProgress(
                "Content Patcher",
                phase: 1,
                optional: false,
                state: CollectionModTaskState.Installed,
                message: "已安装");
            task.SyncCollectionModProgress(
                "Generic Mod Config Menu",
                phase: 2,
                optional: true,
                state: CollectionModTaskState.Failed,
                message: "需要手动下载",
                sourceUrl: "https://example.com/manual-mod",
                requiresManualAction: true);

            var store = new DownloadTaskStateStore();
            store.Save(statePath, [task]);
            var records = store.Load(statePath, out var brokenBackupPath);

            Assert.IsTrue(string.IsNullOrEmpty(brokenBackupPath));
            Assert.AreEqual(1, records.Count);
            Assert.AreEqual(DownloadTaskState.Failed, records[0].TaskState);
            Assert.AreEqual(2400, records[0].SourceModId);
            Assert.AreEqual(898372, records[0].SourceFileId);
            Assert.AreEqual(task.CollectionSlug, records[0].CollectionSlug);
            Assert.AreEqual(task.CollectionRevision, records[0].CollectionRevision);
            Assert.AreEqual(task.InstalledDirectory, records[0].InstalledDirectory);
            Assert.AreEqual(task.SpeedText, records[0].SpeedText);
            Assert.AreEqual(task.EtaText, records[0].EtaText);
            Assert.AreEqual(task.TotalSizeText, records[0].TotalSizeText);
            Assert.AreEqual(task.DownloadedSizeText, records[0].DownloadedSizeText);
            Assert.AreEqual(task.SubProgressText, records[0].SubProgressText);
            Assert.AreEqual(task.SubProgress, records[0].SubProgress);
            Assert.IsTrue(records[0].SkipConflictPrompt);
            Assert.AreEqual(2, records[0].CollectionModItems.Count);
            Assert.AreEqual(CollectionModTaskState.Installed, records[0].CollectionModItems[0].State);
            Assert.AreEqual("需要手动下载", records[0].CollectionModItems[1].Message);
            Assert.AreEqual("https://example.com/manual-mod", records[0].CollectionModItems[1].SourceUrl);
            Assert.IsTrue(records[0].CollectionModItems[1].RequiresManualAction);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void LegacyConfigurationMigration_ImportsWpfSettingsAndInstancesIdempotently()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-legacy-migration-test-" + Guid.NewGuid().ToString("N"));
        var legacyRoot = Path.Combine(root, "legacy");
        var avaloniaRoot = Path.Combine(root, "avalonia");
        var gamePath = Path.Combine(root, "Stardew Valley");
        var isolatedPath = Path.Combine(gamePath, "versions", "我的整合包", "game");
        try
        {
            Directory.CreateDirectory(legacyRoot);
            Directory.CreateDirectory(avaloniaRoot);
            Directory.CreateDirectory(gamePath);
            Directory.CreateDirectory(isolatedPath);
            File.WriteAllText(Path.Combine(gamePath, "Stardew Valley.dll"), string.Empty);
            File.WriteAllText(Path.Combine(isolatedPath, "Stardew Valley.dll"), string.Empty);
            File.WriteAllText(Path.Combine(isolatedPath, "StardewModdingAPI.dll"), string.Empty);

            File.WriteAllText(
                Path.Combine(legacyRoot, "app.json"),
                """
                {
                  "LauncherTitle": "旧 WPF 启动器",
                  "LauncherVisibility": 2,
                  "FontSize": 18,
                  "EnableTransparency": false,
                  "ThemeMode": 1,
                  "Language": "en-US",
                  "MaxConcurrentModDownloads": 10,
                  "DownloadSegmentThreads": 8,
                  "NexusModsOAuthAvatarUrl": "https://example.com/avatar.png",
                  "NexusModsOAuthAvatarLocalPath": "C:\\Users\\test\\avatar.png"
                }
                """);
            File.WriteAllText(
                Path.Combine(legacyRoot, "nexusmods.json"),
                "{\"ApiKey\":\"legacy-nexus-api-key\"}",
                System.Text.Encoding.Unicode);
            File.WriteAllText(
                Path.Combine(legacyRoot, "instances.json"),
                $$"""
                [
                  {
                    "Id": "legacy-default",
                    "Name": "旧实例",
                    "GamePath": "{{gamePath.Replace("\\", "\\\\")}}",
                    "IsSMAPIInstance": true,
                    "IsDefault": true,
                    "IsFavorite": true,
                    "EnableIsolation": false,
                    "Description": "旧实例说明",
                    "WindowTitle": "旧游戏窗口",
                    "CustomArguments": "--old-arg",
                    "AutoConnectServer": true,
                    "ServerAddress": "127.0.0.1:24642",
                    "SteamInviteCode": "invite-code",
                    "OverrideSteamLaunchOptions": true,
                    "SteamLaunchOptions": "-applaunch 413150",
                    "Tags": ["Base"]
                  },
                  {
                    "Id": "legacy-pack",
                    "Name": "我的整合包",
                    "GamePath": "{{gamePath.Replace("\\", "\\\\")}}",
                    "IsSMAPIInstance": true,
                    "IsDefault": false,
                    "IsFavorite": true,
                    "EnableIsolation": true,
                    "Tags": []
                  }
                ]
                """, System.Text.Encoding.Unicode);

            var settingsStore = new AppUserSettingsStore(avaloniaRoot);
            var registryStore = new InstanceRegistryStore(avaloniaRoot);
            var migration = new LegacyConfigurationMigrationService(
                settingsStore,
                registryStore,
                legacyRoot,
                Path.Combine(legacyRoot, "instances.json"));

            var first = migration.Migrate();
            var settings = settingsStore.Load();
            var instances = registryStore.LoadManualInstances();

            Assert.IsTrue(first.Completed);
            Assert.IsTrue(first.HasSources);
            Assert.AreEqual("旧 WPF 启动器", settings.LauncherTitle);
            Assert.AreEqual(2, settings.LauncherVisibility);
            Assert.AreEqual(18, settings.FontSize);
            Assert.IsFalse(settings.EnableTransparency);
            Assert.AreEqual("深色", settings.ThemeMode);
            Assert.AreEqual("en-US", settings.UiLanguage);
            Assert.AreEqual(10, settings.CollectionDownloadParallelism);
            Assert.AreEqual(8, settings.DownloadSegmentThreads);
            Assert.AreEqual("https://example.com/avatar.png", settings.NexusOAuthAvatarUrl);
            Assert.AreEqual(@"C:\Users\test\avatar.png", settings.NexusOAuthAvatarLocalPath);
            Assert.AreEqual("legacy-nexus-api-key", settings.NexusApiKey);
            Assert.AreEqual(gamePath, settings.PreferredInstancePath);
            Assert.AreEqual("旧实例", settings.InstanceName);
            Assert.AreEqual("旧实例说明", settings.InstanceDescription);
            Assert.AreEqual("旧游戏窗口", settings.GameWindowTitle);
            Assert.AreEqual("--old-arg", settings.InstanceCustomLaunchArguments);
            Assert.IsTrue(settings.InstanceAutoConnectServer);
            Assert.AreEqual("127.0.0.1:24642", settings.InstanceServerAddress);
            Assert.AreEqual("invite-code", settings.InstanceSteamInviteCode);
            Assert.IsTrue(settings.OverrideSteamLaunchOptions);
            Assert.AreEqual("-applaunch 413150", settings.SteamLaunchOptions);
            Assert.AreEqual(1, instances.Count);
            Assert.AreEqual(gamePath, instances[0].Path);
            Assert.IsTrue(settings.FavoriteInstanceKeys.Any(key => key.EndsWith("|SMAPI", StringComparison.OrdinalIgnoreCase)));

            // 默认实例仍是旧 WPF 标记的 Base 实例，但隔离实例的收藏键必须落到实际运行目录。
            Assert.IsTrue(settings.FavoriteInstanceKeys.Any(key =>
                key.StartsWith(isolatedPath, StringComparison.OrdinalIgnoreCase) &&
                key.EndsWith("|SMAPI", StringComparison.OrdinalIgnoreCase)));

            // 使用独立目标目录验证：如果默认记录是隔离实例，首选路径应还原到
            // versions/<name>/game，而不是继续指向 Base。
            var isolatedOnlyRoot = Path.Combine(root, "isolated-only");
            var isolatedSettingsStore = new AppUserSettingsStore(isolatedOnlyRoot);
            var isolatedRegistryStore = new InstanceRegistryStore(isolatedOnlyRoot);
            var isolatedLegacyPath = Path.Combine(root, "isolated-only.json");
            File.WriteAllText(
                isolatedLegacyPath,
                $$"""
                [
                  {
                    "Id": "legacy-pack",
                    "Name": "我的整合包",
                    "GamePath": "{{gamePath.Replace("\\", "\\\\")}}",
                    "IsSMAPIInstance": true,
                    "IsDefault": true,
                    "IsFavorite": false,
                    "EnableIsolation": true,
                    "Tags": []
                  }
                ]
                """);
            var isolatedMigration = new LegacyConfigurationMigrationService(
                isolatedSettingsStore,
                isolatedRegistryStore,
                legacyRoot,
                isolatedLegacyPath);
            var isolatedFirst = isolatedMigration.Migrate();
            Assert.IsTrue(isolatedFirst.Completed);
            Assert.AreEqual(isolatedPath, isolatedSettingsStore.Load().PreferredInstancePath);

            var second = migration.Migrate();
            Assert.IsTrue(second.Completed);
            Assert.AreEqual(0, second.ImportedSettingsCount);
            Assert.AreEqual(0, second.ImportedInstanceCount);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void LegacyConfigurationMigration_HandlesStringEnumsAndExistingDefaultSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-legacy-string-settings-" + Guid.NewGuid().ToString("N"));
        var legacyRoot = Path.Combine(root, "legacy");
        var avaloniaRoot = Path.Combine(root, "avalonia");
        var gamePath = Path.Combine(root, "Stardew Valley");
        try
        {
            Directory.CreateDirectory(legacyRoot);
            Directory.CreateDirectory(gamePath);
            File.WriteAllText(Path.Combine(gamePath, "Stardew Valley.dll"), string.Empty);

            // 模拟 WPF 配置被某个 JSON 工具以字符串形式写出；同时预先创建
            // Avalonia 默认 settings.json，覆盖“已有配置但仍是默认值”的启动场景。
            var settingsStore = new AppUserSettingsStore(avaloniaRoot);
            settingsStore.Save(new AppUserSettings());
            File.WriteAllText(
                Path.Combine(legacyRoot, "app.json"),
                """
                {
                  "GameWindowTitle": "自定义游戏标题",
                  "LauncherVisibility": "HideAndCloseOnExit",
                  "WindowSizeMode": "Maximized",
                  "ThemeMode": "Dark",
                  "PrimaryColor": "#abc",
                  "CheckPrereleaseUpdates": true,
                  "PreferredUpdateSource": "Gitee",
                  "AutoDownloadUpdate": true,
                  "ShowUpdateNotification": false,
                  "MaxConcurrentModLocalizationChecks": 12,
                  "ShowModTypeFilterDisabledNotice": false
                }
                """, System.Text.Encoding.Unicode);
            File.WriteAllText(
                Path.Combine(legacyRoot, "gamepath.json"),
                JsonSerializer.Serialize(gamePath),
                System.Text.Encoding.Unicode);

            var migration = new LegacyConfigurationMigrationService(
                settingsStore,
                new InstanceRegistryStore(avaloniaRoot),
                legacyRoot,
                Path.Combine(legacyRoot, "instances.json"));

            var result = migration.Migrate();
            var settings = settingsStore.Load();

            Assert.IsTrue(result.Completed);
            Assert.AreEqual("自定义游戏标题", settings.GameWindowTitle);
            Assert.AreEqual(1, settings.LauncherVisibility);
            Assert.AreEqual("最大化", settings.WindowSizeMode);
            Assert.AreEqual("深色", settings.ThemeMode);
            Assert.AreEqual("#AABBCC", settings.PrimaryColor);
            Assert.AreEqual("预览版", settings.UpdateChannel);
            Assert.AreEqual("Gitee (国内加速)", settings.PreferredUpdateSource);
            Assert.IsTrue(settings.AutoDownloadUpdate);
            Assert.IsFalse(settings.ShowUpdateNotification);
            Assert.AreEqual(12, settings.MaxConcurrentModLocalizationChecks);
            Assert.IsFalse(settings.ShowModTypeFilterDisabledNotice);
            Assert.AreEqual(gamePath, settings.PreferredInstancePath);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void LegacyConfigurationMigration_ShouldRetryWhenInstanceSourceAppearsLater()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-legacy-late-source-" + Guid.NewGuid().ToString("N"));
        var legacyRoot = Path.Combine(root, "legacy");
        var avaloniaRoot = Path.Combine(root, "avalonia");
        var gamePath = Path.Combine(root, "Stardew Valley");
        try
        {
            Directory.CreateDirectory(legacyRoot);
            Directory.CreateDirectory(avaloniaRoot);
            Directory.CreateDirectory(gamePath);
            File.WriteAllText(Path.Combine(gamePath, "Stardew Valley.dll"), string.Empty);
            File.WriteAllText(Path.Combine(legacyRoot, "app.json"), "{\"LauncherTitle\":\"旧配置\"}");

            var settingsStore = new AppUserSettingsStore(avaloniaRoot);
            var registryStore = new InstanceRegistryStore(avaloniaRoot);
            var migration = new LegacyConfigurationMigrationService(
                settingsStore,
                registryStore,
                legacyRoot,
                Path.Combine(legacyRoot, "instances.json"));
            Assert.IsTrue(migration.Migrate().Completed);

            // 模拟旧程序目录中的 instances.json 在首次启动后才出现。
            File.WriteAllText(
                Path.Combine(legacyRoot, "instances.json"),
                $$"""
                [{
                  "Id": "late-instance",
                  "Name": "后出现的实例",
                  "GamePath": "{{gamePath.Replace("\\", "\\\\")}}",
                  "IsDefault": true,
                  "Tags": ["Base"]
                }]
                """
            );

            var second = migration.Migrate();
            Assert.IsTrue(second.Completed);
            Assert.AreEqual(1, second.ImportedInstanceCount);
            Assert.AreEqual(gamePath, registryStore.LoadManualInstances().Single().Path);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void LegacyConfigurationMigration_ShouldRecoverWhenMigratedTargetsAreRemoved()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-legacy-target-recovery-" + Guid.NewGuid().ToString("N"));
        var legacyRoot = Path.Combine(root, "legacy");
        var avaloniaRoot = Path.Combine(root, "avalonia");
        var gamePath = Path.Combine(root, "Stardew Valley");
        try
        {
            Directory.CreateDirectory(legacyRoot);
            Directory.CreateDirectory(gamePath);
            File.WriteAllText(Path.Combine(gamePath, "Stardew Valley.dll"), string.Empty);
            File.WriteAllText(Path.Combine(legacyRoot, "app.json"), "{\"LauncherTitle\":\"旧配置\"}");
            File.WriteAllText(
                Path.Combine(legacyRoot, "instances.json"),
                $$"""
                [{
                  "Id": "recoverable-instance",
                  "Name": "可恢复实例",
                  "GamePath": "{{gamePath.Replace("\\", "\\\\")}}",
                  "IsDefault": true
                }]
                """
            );

            var settingsStore = new AppUserSettingsStore(avaloniaRoot);
            var registryStore = new InstanceRegistryStore(avaloniaRoot);
            var migration = new LegacyConfigurationMigrationService(
                settingsStore,
                registryStore,
                legacyRoot,
                Path.Combine(legacyRoot, "instances.json"));

            var first = migration.Migrate();
            Assert.IsTrue(first.Completed);
            Assert.AreEqual("旧配置", settingsStore.Load().LauncherTitle);
            Assert.AreEqual(1, registryStore.LoadManualInstances().Count);

            // 模拟用户清理 Avalonia 配置或升级过程中目标文件丢失；迁移标记仍保留。
            File.Delete(settingsStore.GetSettingsPath());
            File.Delete(registryStore.GetRegistryPath());

            var recovered = migration.Migrate();
            Assert.IsTrue(recovered.Completed);
            Assert.IsTrue(recovered.ImportedSettingsCount > 0);
            Assert.AreEqual("旧配置", settingsStore.Load().LauncherTitle);
            Assert.AreEqual(gamePath, registryStore.LoadManualInstances().Single().Path);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void LegacyConfigurationMigration_ShouldFindInstancesUnderSiblingWpfDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-legacy-sibling-migration-" + Guid.NewGuid().ToString("N"));
        var legacyRoot = Path.Combine(root, "AvaloniaConfig");
        var wpfInstancesPath = Path.Combine(root, "SVL.Desktop", "SVL", "instances.json");
        var avaloniaRoot = Path.Combine(root, "AvaloniaData");
        var gamePath = Path.Combine(root, "Stardew Valley");

        try
        {
            Directory.CreateDirectory(legacyRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(wpfInstancesPath)!);
            Directory.CreateDirectory(gamePath);
            File.WriteAllText(Path.Combine(gamePath, "Stardew Valley.dll"), string.Empty);
            File.WriteAllText(
                wpfInstancesPath,
                $$"""
                [{
                  "Id": "sibling-wpf-instance",
                  "Name": "并排 WPF 实例",
                  "GamePath": "{{gamePath.Replace("\\", "\\\\")}}",
                  "IsDefault": true,
                  "Tags": ["Base"]
                }]
                """);

            var registryStore = new InstanceRegistryStore(avaloniaRoot);
            var migration = new LegacyConfigurationMigrationService(
                new AppUserSettingsStore(avaloniaRoot),
                registryStore,
                legacyRoot);

            var result = migration.Migrate();
            var instances = registryStore.LoadManualInstances();

            Assert.IsTrue(result.Completed);
            Assert.IsTrue(result.ImportedInstanceCount >= 1);
            Assert.IsTrue(instances.Any(instance =>
                string.Equals(instance.Path, gamePath, StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModpackTypeDetector_RecognizesAndExtractsSevenZipCollection()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-7z-detection-test-" + Guid.NewGuid().ToString("N"));
        var sourceDirectory = Path.Combine(root, "source");
        var archivePath = Path.Combine(root, "collection.7z");
        try
        {
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllText(
                Path.Combine(sourceDirectory, "collection.json"),
                "{\"info\":{\"name\":\"测试 Collection\",\"author\":\"SVL\"},\"mods\":[]}");

            using (var writer = SevenZipWriter.OpenWriter(
                       archivePath,
                       new SevenZipWriterOptions(CompressionType.LZMA2)))
            using (var stream = File.OpenRead(Path.Combine(sourceDirectory, "collection.json")))
            {
                writer.Write("collection.json", stream, null);
            }

            Assert.IsTrue(ModpackTypeDetector.IsSupportedFile(archivePath));
            var detection = ModpackTypeDetector.Detect(archivePath);
            try
            {
                Assert.AreEqual(ModpackType.NexusCollection, detection.Type);
                Assert.AreEqual("测试 Collection", detection.ModpackName);
                Assert.AreEqual(0, detection.ModCount);
                Assert.IsTrue(File.Exists(Path.Combine(detection.TempExtractPath, "collection.json")));
            }
            finally
            {
                ModpackTypeDetector.CleanupTempDirectory(detection.TempExtractPath);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void CurseforgeManifestParser_ShouldAcceptStringProjectAndFileIds()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-string-cf-ids-" + Guid.NewGuid().ToString("N"));
        var manifestPath = Path.Combine(root, "manifest.json");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(
                manifestPath,
                "{\"name\":\"字符串 ID 包\",\"version\":\"1.0.0\",\"files\":[{\"projectID\":\"1012214\",\"fileID\":\"5312529\"}]}");

            var manifest = SVL.Core.Platform.Modpack.CurseforgeModpackParser.ParseFromJsonFile(manifestPath);
            Assert.AreEqual(1, manifest.Files.Count);
            Assert.AreEqual(1012214L, manifest.Files[0].ProjectId);
            Assert.AreEqual(5312529L, manifest.Files[0].FileId);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModpackIconImport_ShouldReplaceGeneratedSmapiPresetButKeepCustomIconOnRetry()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-modpack-icon-import-" + Guid.NewGuid().ToString("N"));
        var packageRoot = Path.Combine(root, "package");
        var versionRoot = Path.Combine(root, "version");
        var customIcon = Path.Combine(packageRoot, "icon.png");
        try
        {
            Directory.CreateDirectory(packageRoot);
            Directory.CreateDirectory(versionRoot);
            File.WriteAllBytes(customIcon, [1, 2, 3, 4, 5]);
            var generatedIconPath = Path.Combine(versionRoot, ".svl-instance-icon-smapi.png");
            var generatedMarkerPath = Path.Combine(
                versionRoot,
                ".svl-instance-icon-smapi.generated");
            File.WriteAllBytes(generatedIconPath, [0]);
            File.WriteAllText(generatedMarkerPath, "generated-by-svl\n");

            var extract = typeof(ModpackInstallService).GetMethod(
                "ExtractPackIcon",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(extract);

            var importedPath = extract!.Invoke(null, [packageRoot, versionRoot, null, packageRoot]) as string;
            Assert.AreEqual(generatedIconPath, importedPath);
            CollectionAssert.AreEqual(File.ReadAllBytes(customIcon), File.ReadAllBytes(importedPath!));
            Assert.IsFalse(File.Exists(generatedMarkerPath));

            // 再次导入/部分失败重试时，玩家自定义图标不能被包内图标覆盖。
            File.WriteAllBytes(customIcon, [9, 8, 7]);
            extract.Invoke(null, [packageRoot, versionRoot, null, packageRoot]);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5 }, File.ReadAllBytes(importedPath!));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void CollectionIconImport_ShouldReplaceGeneratedSmapiPresetButKeepCustomIconOnRetry()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-collection-icon-import-" + Guid.NewGuid().ToString("N"));
        var extractRoot = Path.Combine(root, "collection");
        var versionRoot = Path.Combine(root, "version");
        var importedIcon = Path.Combine(versionRoot, ".svl-instance-icon-smapi.png");
        var generatedMarker = Path.Combine(versionRoot, ".svl-instance-icon-smapi.generated");
        try
        {
            Directory.CreateDirectory(extractRoot);
            Directory.CreateDirectory(versionRoot);
            File.WriteAllBytes(Path.Combine(extractRoot, "collection-icon.png"), [4, 5, 6, 7]);
            File.WriteAllBytes(importedIcon, [0]);
            File.WriteAllText(generatedMarker, "generated-by-svl\n");

            var extract = typeof(CollectionInstallService).GetMethod(
                "ExtractCollectionIcon",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(extract);

            var importedPath = extract!.Invoke(null, [extractRoot, versionRoot, null, null]) as string;
            Assert.AreEqual(importedIcon, importedPath);
            CollectionAssert.AreEqual(new byte[] { 4, 5, 6, 7 }, File.ReadAllBytes(importedIcon));
            Assert.IsFalse(File.Exists(generatedMarker));

            // 部分失败重试时，已由玩家选择的图标不能被包内图标覆盖。
            File.WriteAllBytes(Path.Combine(extractRoot, "collection-icon.png"), [8, 9]);
            extract.Invoke(null, [extractRoot, versionRoot, null, null]);
            CollectionAssert.AreEqual(new byte[] { 4, 5, 6, 7 }, File.ReadAllBytes(importedIcon));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void CollectionIconImport_ShouldPreferCollectionRootIconOverNestedModIcon()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-collection-icon-priority-" + Guid.NewGuid().ToString("N"));
        var extractRoot = Path.Combine(root, "extracted");
        var collectionRoot = Path.Combine(extractRoot, "Collection");
        var nestedModRoot = Path.Combine(collectionRoot, "bundled", "SomeMod");
        var versionRoot = Path.Combine(root, "version");
        var targetIcon = Path.Combine(versionRoot, ".svl-instance-icon-smapi.png");
        try
        {
            Directory.CreateDirectory(nestedModRoot);
            Directory.CreateDirectory(versionRoot);
            File.WriteAllBytes(Path.Combine(collectionRoot, "icon.png"), [10, 11]);
            File.WriteAllBytes(Path.Combine(nestedModRoot, "icon.png"), [20, 21]);
            File.WriteAllBytes(targetIcon, [0]);
            File.WriteAllText(
                Path.Combine(versionRoot, ".svl-instance-icon-smapi.generated"),
                "generated-by-svl\n");

            var extract = typeof(CollectionInstallService).GetMethod(
                "ExtractCollectionIcon",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(extract);

            extract!.Invoke(null, [collectionRoot, versionRoot, null, extractRoot]);
            CollectionAssert.AreEqual(new byte[] { 10, 11 }, File.ReadAllBytes(targetIcon));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ExportIcon_ShouldIgnoreOnlyGeneratedSmapiPreset()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-export-icon-marker-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var iconPath = Path.Combine(root, ".svl-instance-icon-smapi.png");
            File.WriteAllBytes(iconPath, [1, 2, 3]);

            var viewModel = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                typeof(VersionSettingsPageViewModel));
            SetPrivateField(viewModel, "_isSmapiInstance", true);

            var resolver = typeof(VersionSettingsPageViewModel).GetMethod(
                "ResolveExportCustomIconPath",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(resolver);

            File.WriteAllText(Path.Combine(root, ".svl-instance-icon-smapi.generated"), "generated-by-svl\n");
            var generatedResult = resolver!.Invoke(viewModel, [root]) as string;
            Assert.IsTrue(string.IsNullOrEmpty(generatedResult));

            // 生成的 SMAPI 默认图标存在时，版本设置中的通用自定义图标仍必须
            // 被导出；不能仅凭目录里存在 generated 标记就把它一并过滤掉。
            var customPath = Path.Combine(root, ".svl-instance-icon.png");
            File.WriteAllBytes(customPath, [9, 8, 7]);
            var customResult = resolver.Invoke(viewModel, [root]) as string;
            Assert.AreEqual(customPath, customResult);

            // 用户主动选择同一内置预设时没有生成标记，仍应作为用户图标导出。
            File.Delete(Path.Combine(root, ".svl-instance-icon-smapi.generated"));
            var userSelectedResult = resolver.Invoke(viewModel, [root]) as string;
            Assert.AreEqual(iconPath, userSelectedResult);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ExportSource_ShouldNotTreatNexusFilePageAsDirectArchive()
    {
        var item = new ExportModSelectionItem
        {
            SourcePlatform = "NexusMods",
            SourceProjectId = "29868",
            SourceDownloadUrl = "https://www.nexusmods.com/stardewvalley/mods/29868?tab=files&nmm=1"
        };

        Assert.IsFalse(item.HasDirectSourceUrl);
        Assert.IsFalse(item.HasCompleteSourceCredential);
        StringAssert.Contains(item.SourceDescription, "FileID 缺失");
    }

    [TestMethod]
    public void ExportPackageSource_ShouldUseSamePageUrlRuleAsSelection()
    {
        var itemType = typeof(VersionSettingsPageViewModel).Assembly.GetType(
            "SVL.Avalonia.ViewModels.ExportModPackageItem");
        Assert.IsNotNull(itemType);

        var item = Activator.CreateInstance(itemType!, nonPublic: true);
        Assert.IsNotNull(item);
        SetProperty(item!, "SourcePlatform", "CurseForge");
        SetProperty(item!, "SourceProjectId", "1012214");
        SetProperty(item!, "SourceDownloadUrl", "https://www.curseforge.com/stardewvalley/mods/1012214/files/5312529");

        var hasDirect = (bool)itemType!.GetProperty("HasDirectSourceUrl")!.GetValue(item)!;
        var hasComplete = (bool)itemType.GetProperty("HasCompleteSourceCredential")!.GetValue(item)!;

        Assert.IsFalse(hasDirect);
        Assert.IsFalse(hasComplete);
    }

    [TestMethod]
    public void DeleteVersionDirectory_ShouldClearReadOnlyFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-delete-readonly-test-" + Guid.NewGuid().ToString("N"));
        var versionRoot = Path.Combine(root, "versions", "ReadOnlyInstance");
        var lockedByAttribute = Path.Combine(versionRoot, ".svl-instance-icon-smapi.png");
        try
        {
            Directory.CreateDirectory(versionRoot);
            File.WriteAllBytes(lockedByAttribute, [1, 2, 3]);
            File.SetAttributes(lockedByAttribute, FileAttributes.ReadOnly);

            var deleteMethod = typeof(VersionSettingsPageViewModel).GetMethod(
                "DeleteVersionDirectorySafe",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(deleteMethod);
            deleteMethod!.Invoke(null, [versionRoot]);

            Assert.IsFalse(Directory.Exists(versionRoot));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                File.SetAttributes(root, FileAttributes.Normal);
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModManifestParser_ShouldSkipEmptyAliasAndReadFollowingVersion()
    {
        using var document = JsonDocument.Parse(
            "{\"Version\":\"\",\"releaseVersion\":\" 1.6.15 \"}");
        var parser = typeof(VersionSettingsPageViewModel).GetMethod(
            "GetJsonStringFlexibleByCandidates",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        var value = parser!.Invoke(null, [document.RootElement, new[] { "Version", "releaseVersion" }]) as string;
        Assert.AreEqual("1.6.15", value);
    }

    [TestMethod]
    public void ModManifestReader_ShouldDecodeBomlessUtf16AndNestedVersion()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-manifest-encoding-test-" + Guid.NewGuid().ToString("N"));
        var manifestPath = Path.Combine(root, "manifest.json");
        try
        {
            Directory.CreateDirectory(root);
            const string json = "{\"metadata\":{\"version\":\"3.2.1\"}}";
            var utf16WithoutBom = new System.Text.UnicodeEncoding(bigEndian: false, byteOrderMark: false)
                .GetBytes(json);
            File.WriteAllBytes(manifestPath, utf16WithoutBom);

            Assert.AreEqual(json, ManifestTextReader.ReadAllText(manifestPath));
            using var document = JsonDocument.Parse(ManifestTextReader.ReadAllText(manifestPath));
            var parser = typeof(VersionSettingsPageViewModel).GetMethod(
                "GetManifestVersion",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(parser);
            Assert.AreEqual("3.2.1", parser!.Invoke(null, [document.RootElement]));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void SmapiCacheInspector_ShouldReadVersionFromArchiveRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-smapi-cache-version-test-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "2400_7448774.zip");
        try
        {
            Directory.CreateDirectory(root);
            using (var archive = System.IO.Compression.ZipFile.Open(
                       archivePath,
                       System.IO.Compression.ZipArchiveMode.Create))
            {
                WriteArchiveText(
                    archive,
                    "SMAPI 4.5.2 installer/internal/windows/install.dat",
                    "install");
            }

            var inspectorType = typeof(ModpackInstallService).Assembly.GetType(
                "SVL.Avalonia.Services.SmapiPackageVersionInspector");
            Assert.IsNotNull(inspectorType);
            var readVersion = inspectorType!.GetMethod(
                "TryReadVersion",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            var isCompatible = inspectorType.GetMethod(
                "IsCompatible",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            Assert.IsNotNull(readVersion);
            Assert.IsNotNull(isCompatible);

            Assert.AreEqual("4.5.2", readVersion!.Invoke(null, [archivePath]));
            Assert.IsTrue((bool)isCompatible!.Invoke(null, ["4.5.2", archivePath]));
            Assert.IsFalse((bool)isCompatible.Invoke(null, ["4.5.3", archivePath]));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void SmapiCacheCompatibility_ShouldRejectUnknownVersionWhenTargetIsKnown()
    {
        var modpackService = typeof(ModpackInstallService);
        var collectionService = typeof(CollectionInstallService);
        var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;

        var modpackMethod = modpackService.GetMethod("IsCompatibleSmapiVersion", flags);
        var collectionMethod = collectionService.GetMethod("IsCompatibleSmapiVersion", flags);
        Assert.IsNotNull(modpackMethod);
        Assert.IsNotNull(collectionMethod);

        Assert.IsFalse((bool)modpackMethod!.Invoke(null, ["4.5.2", ""] )!);
        Assert.IsFalse((bool)collectionMethod!.Invoke(null, ["SMAPI 4.5.2", "latest"] )!);
        Assert.IsTrue((bool)modpackMethod.Invoke(null, ["4.5.2", "4.5.3"])!);
        Assert.IsTrue((bool)collectionMethod.Invoke(null, [null, "unknown"])!);
    }

    [TestMethod]
    public void SmapiOfficialReleaseFallback_ShouldBuildVersionedInstallerUrl()
    {
        var method = typeof(ModpackInstallService).GetMethod(
            "BuildOfficialSmapiReleaseUrl",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(method);

        var url = method!.Invoke(null, ["4.5.2"]) as string;
        Assert.AreEqual(
            "https://github.com/Pathoschild/SMAPI/releases/download/4.5.2/SMAPI-4.5.2-installer.zip",
            url);

        var collectionMethod = typeof(CollectionInstallService).GetMethod(
            "BuildOfficialSmapiReleaseUrl",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(collectionMethod);
        Assert.AreEqual(url, collectionMethod!.Invoke(null, ["4.5.2"]));

        var candidatesMethod = typeof(ModpackInstallService).GetMethod(
            "GetOfficialSmapiReleaseVersionCandidates",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(candidatesMethod);
        var candidates = ((IEnumerable<string>)candidatesMethod!.Invoke(null, ["4.5.1.0"])!).ToArray();
        CollectionAssert.AreEqual(new[] { "4.5.1.0", "4.5.1" }, candidates);
    }

    [TestMethod]
    public void ModpackTypeDetector_RecognizesSevenZipWithoutExtension()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-7z-no-extension-test-" + Guid.NewGuid().ToString("N"));
        var sourceDirectory = Path.Combine(root, "source");
        var archivePath = Path.Combine(root, "collection-download");
        try
        {
            Directory.CreateDirectory(sourceDirectory);
            var collectionPath = Path.Combine(sourceDirectory, "collection.json");
            File.WriteAllText(collectionPath, "{\"info\":{\"name\":\"无扩展名 Collection\"},\"mods\":[]}");

            using (var writer = SevenZipWriter.OpenWriter(
                       archivePath,
                       new SevenZipWriterOptions(CompressionType.LZMA2)))
            using (var stream = File.OpenRead(collectionPath))
            {
                writer.Write("collection.json", stream, null);
            }

            Assert.IsTrue(ModpackTypeDetector.IsSupportedFile(archivePath));
            var detection = ModpackTypeDetector.Detect(archivePath);
            try
            {
                Assert.AreEqual(ModpackType.NexusCollection, detection.Type);
                Assert.AreEqual("无扩展名 Collection", detection.ModpackName);
            }
            finally
            {
                ModpackTypeDetector.CleanupTempDirectory(detection.TempExtractPath);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModpackInstall_UsesSevenZipForPackageArchive()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-7z-install-archive-test-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "modpack.7z");
        var destination = Path.Combine(root, "extracted");
        try
        {
            Directory.CreateDirectory(root);
            var manifest = Path.Combine(root, "modpack.json");
            File.WriteAllText(manifest, "{\"name\":\"测试整合包\"}");

            using (var writer = SevenZipWriter.OpenWriter(
                       archivePath,
                       new SevenZipWriterOptions(CompressionType.LZMA2)))
            using (var stream = File.OpenRead(manifest))
            {
                writer.Write("modpack.json", stream, null);
            }

            var extractor = typeof(ModpackInstallService).GetMethod(
                "ExtractPackageArchive",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(extractor);

            extractor.Invoke(null, [archivePath, destination]);

            Assert.IsTrue(File.Exists(Path.Combine(destination, "modpack.json")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void LocalModImport_UsesSevenZipAndFlattensNestedDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-local-mod-7z-test-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "ContentPatcher.7z");
        var modsPath = Path.Combine(root, "Mods");
        try
        {
            Directory.CreateDirectory(root);
            var manifestPath = Path.Combine(root, "manifest.json");
            var contentPath = Path.Combine(root, "content.json");
            File.WriteAllText(manifestPath, "{\"Name\":\"Content Patcher\",\"UniqueID\":\"Pathoschild.ContentPatcher\"}");
            File.WriteAllText(contentPath, "{}");

            using (var writer = SevenZipWriter.OpenWriter(
                       archivePath,
                       new SevenZipWriterOptions(CompressionType.LZMA2)))
            using (var manifest = File.OpenRead(manifestPath))
            using (var content = File.OpenRead(contentPath))
            {
                writer.Write("Release/ContentPatcher/manifest.json", manifest, null);
                writer.Write("Release/ContentPatcher/content.json", content, null);
            }

            var importer = typeof(VersionSettingsPageViewModel).GetMethod(
                "ImportModsFromLocalSource",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(importer);

            var imported = (int)importer.Invoke(null, [archivePath, modsPath, true])!;

            Assert.AreEqual(1, imported);
            Assert.IsTrue(File.Exists(Path.Combine(modsPath, "ContentPatcher", "manifest.json")));
            Assert.IsTrue(File.Exists(Path.Combine(modsPath, "ContentPatcher", "content.json")));
            Assert.IsFalse(Directory.Exists(Path.Combine(modsPath, "ContentPatcher", "Release")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModpackInstall_ReusesCurseforgeSourceCredential()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-cf-source-reuse-test-" + Guid.NewGuid().ToString("N"));
        var modsPath = Path.Combine(root, "Mods");
        var modDirectory = Path.Combine(modsPath, "Content Patcher");
        try
        {
            Directory.CreateDirectory(modDirectory);
            File.WriteAllText(
                Path.Combine(modDirectory, "manifest.json"),
                "{\"Name\":\"Content Patcher\",\"UniqueID\":\"Pathoschild.ContentPatcher\",\"Version\":\"2.9.0\"}");
            File.WriteAllText(
                Path.Combine(modDirectory, "svl-source.json"),
                "{\"platform\":\"Curseforge\",\"projectId\":\"1012214\",\"fileId\":\"5312529\"}");

            var finder = typeof(ModpackInstallService).GetMethod(
                "FindInstalledModDirectories",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(finder);

            var result = finder.Invoke(
                null,
                [modsPath, null, null, null, "Curseforge", "1012214", "5312529"])
                as IReadOnlyList<string>;

            Assert.IsNotNull(result);
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(modDirectory, result[0]);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModpackSourceParser_ShouldRecoverCanonicalCurseforgeIds()
    {
        using var document = JsonDocument.Parse(
            "{\"name\":\"Content Patcher\",\"source\":\"cf-1012214-5312529\"}");
        var parser = typeof(ModpackInstallService).GetMethod(
            "TryGetModSourceDescriptor",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        var arguments = new object[] { document.RootElement, null! };
        var parsed = (bool)parser.Invoke(null, arguments)!;
        Assert.IsTrue(parsed);

        var descriptor = arguments[1];
        Assert.IsNotNull(descriptor);
        Assert.AreEqual("Curseforge", descriptor!.GetType().GetProperty("Platform")?.GetValue(descriptor));
        Assert.AreEqual("1012214", descriptor.GetType().GetProperty("ProjectId")?.GetValue(descriptor));
        Assert.AreEqual("5312529", descriptor.GetType().GetProperty("FileId")?.GetValue(descriptor));
    }

    [TestMethod]
    public void ModpackSourceParser_ShouldReplaceEmptyIdsFromCanonicalToken()
    {
        using var document = JsonDocument.Parse(
            "{\"name\":\"Content Patcher\",\"projectId\":\"\",\"fileId\":\"\",\"source\":\"cf-1012214-5312529\"}");
        var parser = typeof(ModpackInstallService).GetMethod(
            "TryGetModSourceDescriptor",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        var arguments = new object[] { document.RootElement, null! };
        var parsed = (bool)parser.Invoke(null, arguments)!;
        Assert.IsTrue(parsed);

        var descriptor = arguments[1];
        Assert.IsNotNull(descriptor);
        Assert.AreEqual("Curseforge", descriptor!.GetType().GetProperty("Platform")?.GetValue(descriptor));
        Assert.AreEqual("1012214", descriptor.GetType().GetProperty("ProjectId")?.GetValue(descriptor));
        Assert.AreEqual("5312529", descriptor.GetType().GetProperty("FileId")?.GetValue(descriptor));
    }

    [TestMethod]
    [DataRow("{\"source\":{\"site\":\"CurseForge\",\"project\":1012214,\"file\":5312529}}")]
    [DataRow("{\"source\":{\"platform\":\"\",\"site\":\"CurseForge\",\"projectId\":\" \",\"project\":1012214,\"fileId\":\"\",\"file\":5312529}}")]
    public void ModpackSourceParser_ShouldReadCommonCurseforgeProjectAliases(string json)
    {
        using var document = JsonDocument.Parse(json);
        var parser = typeof(ModpackInstallService).GetMethod(
            "TryGetModSourceDescriptor",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        var arguments = new object[] { document.RootElement, null! };
        var parsed = (bool)parser.Invoke(null, arguments)!;
        Assert.IsTrue(parsed);

        var descriptor = arguments[1];
        Assert.IsNotNull(descriptor);
        Assert.AreEqual("CurseForge", descriptor!.GetType().GetProperty("Platform")?.GetValue(descriptor));
        Assert.AreEqual("1012214", descriptor.GetType().GetProperty("ProjectId")?.GetValue(descriptor));
        Assert.AreEqual("5312529", descriptor.GetType().GetProperty("FileId")?.GetValue(descriptor));
    }

    [TestMethod]
    public void ModpackInstall_ShouldPersistDirectSourceUrlForReExport()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-direct-source-credential-test-" + Guid.NewGuid().ToString("N"));
        var modsPath = Path.Combine(root, "Mods");
        var modDirectory = Path.Combine(modsPath, "Content Patcher");
        try
        {
            Directory.CreateDirectory(modDirectory);
            var writer = typeof(ModpackInstallService).GetMethod(
                "WriteModpackSourceCredentials",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(writer);

            writer!.Invoke(
                null,
                [
                    modsPath,
                    new[] { "Content Patcher" },
                    null,
                    0L,
                    0L,
                    "https://example.invalid/content-patcher.zip",
                    "Content Patcher 2.9.0.zip"
                ]);

            var sourcePath = Path.Combine(modDirectory, "svl-source.json");
            Assert.IsTrue(File.Exists(sourcePath));
            var sourceJson = File.ReadAllText(sourcePath);
            StringAssert.Contains(sourceJson, "https://example.invalid/content-patcher.zip");
            StringAssert.Contains(sourceJson, "Content Patcher 2.9.0.zip");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void CollectionInstall_ShouldPersistDirectSourceUrlForReExport()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-collection-direct-source-test-" + Guid.NewGuid().ToString("N"));
        var modsPath = Path.Combine(root, "Mods");
        var modDirectory = Path.Combine(modsPath, "Content Patcher");
        try
        {
            Directory.CreateDirectory(modDirectory);
            var writer = typeof(ModpackInstallService).GetMethod(
                "WriteSourceCredentialForInstalledMod",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(writer);

            writer!.Invoke(
                null,
                [
                    modsPath,
                    new[] { "Content Patcher" },
                    null,
                    null,
                    null,
                    "https://example.invalid/content-patcher.zip",
                    "Content Patcher 2.9.0.zip"
                ]);

            var sourcePath = Path.Combine(modDirectory, "svl-source.json");
            Assert.IsTrue(File.Exists(sourcePath));
            var sourceJson = File.ReadAllText(sourcePath);
            StringAssert.Contains(sourceJson, "https://example.invalid/content-patcher.zip");
            StringAssert.Contains(sourceJson, "Content Patcher 2.9.0.zip");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModpackInstall_ShouldWriteCredentialForLegacyModNameField()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-modname-source-credential-test-" + Guid.NewGuid().ToString("N"));
        var modsPath = Path.Combine(root, "Mods");
        var modDirectory = Path.Combine(modsPath, "ContentPatcher");
        try
        {
            Directory.CreateDirectory(modDirectory);
            using var document = JsonDocument.Parse(
                "[{\"modName\":\"Content Patcher\",\"directoryName\":\"ContentPatcher\",\"source\":{\"platform\":\"Curseforge\",\"projectId\":1012214,\"fileId\":5312529}}]");
            var writer = typeof(ModpackInstallService).GetMethod(
                "WriteSourceCredentials",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(writer);

            var entries = document.RootElement.EnumerateArray().Select(item => item.Clone()).ToList();
            writer!.Invoke(null, [entries, modsPath]);

            var sourcePath = Path.Combine(modDirectory, "svl-source.json");
            Assert.IsTrue(File.Exists(sourcePath));
            StringAssert.Contains(File.ReadAllText(sourcePath), "5312529");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModpackInstall_ShouldPersistBundledModpackProvenanceWithoutUpdateSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-bundled-modpack-source-test-" + Guid.NewGuid().ToString("N"));
        var modDirectory = Path.Combine(root, "Mods", "Bundled Mod");
        try
        {
            Directory.CreateDirectory(modDirectory);
            File.WriteAllText(
                Path.Combine(modDirectory, "manifest.json"),
                "{\"Name\":\"Bundled Mod\",\"UniqueID\":\"Example.Bundled\"}");

            var writer = typeof(ModpackInstallService).GetMethod(
                "WriteBundledModpackSourceCredentials",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(writer);

            writer!.Invoke(
                null,
                [new[] { modDirectory }, "Blissful Valley", "1.2.0"]);

            using var document = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(modDirectory, "svl-source.json")));
            var source = document.RootElement;
            Assert.AreEqual("modpack-bundled", source.GetProperty("sourceKind").GetString());
            Assert.AreEqual("Blissful Valley", source.GetProperty("modpack").GetProperty("name").GetString());
            Assert.AreEqual("1.2.0", source.GetProperty("modpack").GetProperty("version").GetString());
            Assert.IsFalse(source.GetProperty("hasUpdate").GetBoolean());
            Assert.AreEqual("整合包内置", source.GetProperty("updateStatus").GetString());
            Assert.IsFalse(source.TryGetProperty("platform", out _));
            Assert.IsFalse(source.TryGetProperty("projectId", out _));
            Assert.IsFalse(source.TryGetProperty("fileId", out _));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModpackInstall_ShouldPreserveIndependentSourceWhenWritingBundledProvenance()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-bundled-source-preserve-test-" + Guid.NewGuid().ToString("N"));
        var modDirectory = Path.Combine(root, "Mods", "Downloaded Mod");
        try
        {
            Directory.CreateDirectory(modDirectory);
            File.WriteAllText(
                Path.Combine(modDirectory, "manifest.json"),
                "{\"Name\":\"Downloaded Mod\",\"UniqueID\":\"Example.Downloaded\"}");
            File.WriteAllText(
                Path.Combine(modDirectory, "svl-source.json"),
                "{\"platform\":\"Curseforge\",\"projectId\":\"1012214\",\"fileId\":\"5312529\",\"sourceKind\":\"modpack-entry\"}");

            var writer = typeof(ModpackInstallService).GetMethod(
                "WriteBundledModpackSourceCredentials",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(writer);

            writer!.Invoke(
                null,
                [new[] { modDirectory }, "Blissful Valley", "1.2.0"]);

            using var document = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(modDirectory, "svl-source.json")));
            var source = document.RootElement;
            Assert.AreEqual("modpack-entry", source.GetProperty("sourceKind").GetString());
            Assert.AreEqual("1012214", source.GetProperty("projectId").GetString());
            Assert.AreEqual("5312529", source.GetProperty("fileId").GetString());
            Assert.IsFalse(source.TryGetProperty("modpack", out _));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModpackInstall_ShouldBuildParentTreeForBundledSiblingContentPacks()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-bundled-composite-source-test-" + Guid.NewGuid().ToString("N"));
        var modsPath = Path.Combine(root, "Mods");
        var parentDirectory = Path.Combine(modsPath, "Parent Mod");
        var childOneDirectory = Path.Combine(modsPath, "[CP] Child One");
        var childTwoDirectory = Path.Combine(modsPath, "[FTM] Child Two");
        try
        {
            Directory.CreateDirectory(parentDirectory);
            Directory.CreateDirectory(childOneDirectory);
            Directory.CreateDirectory(childTwoDirectory);
            File.WriteAllText(
                Path.Combine(parentDirectory, "manifest.json"),
                "{\"Name\":\"Parent Mod\",\"UniqueID\":\"Example.Parent\",\"EntryDll\":\"Parent.dll\"}");
            File.WriteAllText(
                Path.Combine(childOneDirectory, "manifest.json"),
                "{\"Name\":\"Child One\",\"UniqueID\":\"Example.Parent.ChildOne\",\"ContentPackFor\":{\"UniqueID\":\"Example.Parent\"}}");
            File.WriteAllText(
                Path.Combine(childTwoDirectory, "manifest.json"),
                "{\"Name\":\"Child Two\",\"UniqueID\":\"Example.Parent.ChildTwo\",\"ContentPackFor\":{\"UniqueID\":\"Example.Parent\"}}");

            var writer = typeof(ModpackInstallService).GetMethod(
                "WriteBundledModpackSourceCredentials",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(writer);

            writer!.Invoke(
                null,
                [new[] { parentDirectory, childOneDirectory, childTwoDirectory }, "Blissful Valley", "1.2.0"]);

            using var parentDocument = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(parentDirectory, "svl-source.json")));
            var parent = parentDocument.RootElement;
            Assert.AreEqual("modpack-bundled", parent.GetProperty("sourceKind").GetString());
            Assert.IsTrue(parent.GetProperty("isParentMod").GetBoolean());
            Assert.AreEqual(2, parent.GetProperty("childMods").GetArrayLength());
            using var childDocument = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(childOneDirectory, "svl-source.json")));
            var child = childDocument.RootElement;
            Assert.AreEqual("parent-inherited", child.GetProperty("sourceKind").GetString());
            Assert.AreEqual("Parent Mod", child.GetProperty("parentMod").GetProperty("name").GetString());
            Assert.IsFalse(child.TryGetProperty("projectId", out _));
            Assert.IsFalse(child.TryGetProperty("fileId", out _));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void BackupComparison_ShouldIgnoreBackupAndUpdateChainMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-backup-comparison-test-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(root, "backup");
        var current = Path.Combine(root, "current");
        try
        {
            Directory.CreateDirectory(backup);
            Directory.CreateDirectory(current);
            File.WriteAllText(Path.Combine(backup, "manifest.json"), "same");
            File.WriteAllText(Path.Combine(current, "manifest.json"), "same");
            File.WriteAllText(Path.Combine(backup, ".svl-backup.json"), "backup metadata");
            File.WriteAllText(Path.Combine(backup, ".svl-update-chain.json"), "update metadata");

            var compare = typeof(VersionSettingsPageViewModel).GetMethod(
                "CompareBackupDirectories",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(compare);
            var result = compare!.Invoke(null, [backup, current]);
            Assert.IsNotNull(result);
            Assert.IsFalse((bool)result!.GetType().GetProperty("HasDifferences")!.GetValue(result)!);
            Assert.AreEqual(
                "相同 1 个，内容不同 0 个，仅备份中 0 个，仅原有 Mod 中 0 个。",
                result.GetType().GetProperty("Summary")!.GetValue(result));

            File.WriteAllText(Path.Combine(current, "content.json"), "new file");
            result = compare.Invoke(null, [backup, current]);
            Assert.IsTrue((bool)result!.GetType().GetProperty("HasDifferences")!.GetValue(result)!);
            StringAssert.Contains(
                (string)result.GetType().GetProperty("Summary")!.GetValue(result)!,
                "仅原有 Mod 中 1 个");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void SvlModpackImport_ShouldWriteInheritedCredentialForChildReference()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-child-source-import-test-" + Guid.NewGuid().ToString("N"));
        var modsPath = Path.Combine(root, "Mods");
        var parentPath = Path.Combine(modsPath, "Parent Mod");
        var childPath = Path.Combine(parentPath, "Child Mod");
        try
        {
            Directory.CreateDirectory(childPath);
            File.WriteAllText(
                Path.Combine(parentPath, "manifest.json"),
                "{\"Name\":\"Parent Mod\",\"UniqueID\":\"Example.Parent\"}");
            File.WriteAllText(
                Path.Combine(childPath, "manifest.json"),
                "{\"Name\":\"Child Mod\",\"UniqueID\":\"Example.Child\"}");
            // 模拟旧版本曾把子 Mod 当成独立来源写入；新规则应由父级
            // childMods 关系覆盖，而不是继续让它参与独立更新。
            File.WriteAllText(
                Path.Combine(childPath, "svl-source.json"),
                "{\"platform\":\"Curseforge\",\"projectId\":\"111\",\"fileId\":\"222\"}");

            using var document = JsonDocument.Parse(
                "[{\"name\":\"Parent Mod\",\"directoryName\":\"Parent Mod\",\"isParentMod\":true," +
                "\"childMods\":[{\"name\":\"Child Mod\",\"relativePath\":\"Old Parent/Child Mod\"}]," +
                "\"source\":{\"platform\":\"Curseforge\",\"projectId\":994458,\"fileId\":8390242}}]");
            var writer = typeof(ModpackInstallService).GetMethod(
                "WriteSourceCredentials",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(writer);

            writer!.Invoke(null, [document.RootElement.EnumerateArray().Select(item => item.Clone()).ToList(), modsPath]);

            using var childDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(childPath, "svl-source.json")));
            var child = childDocument.RootElement;
            using var parentDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(parentPath, "svl-source.json")));
            var parent = parentDocument.RootElement;
            Assert.AreEqual("modpack-entry", parent.GetProperty("sourceKind").GetString());
            Assert.AreEqual("parent-inherited", child.GetProperty("sourceKind").GetString());
            Assert.AreEqual("Parent Mod", child.GetProperty("parentMod").GetProperty("name").GetString());
            Assert.AreEqual("Parent Mod", child.GetProperty("parentMod").GetProperty("relativePath").GetString());
            Assert.IsFalse(child.TryGetProperty("projectId", out _));
            Assert.IsFalse(child.TryGetProperty("fileId", out _));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void SvlModpackImport_ShouldNotOverwriteInheritedChildWithPerEntrySource()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-child-source-order-test-" + Guid.NewGuid().ToString("N"));
        var modsPath = Path.Combine(root, "Mods");
        var parentPath = Path.Combine(modsPath, "Parent Mod");
        var childPath = Path.Combine(modsPath, "Child Mod");
        try
        {
            Directory.CreateDirectory(parentPath);
            Directory.CreateDirectory(childPath);
            File.WriteAllText(
                Path.Combine(parentPath, "manifest.json"),
                "{\"Name\":\"Parent Mod\",\"UniqueID\":\"Example.Parent\"}");
            File.WriteAllText(
                Path.Combine(childPath, "manifest.json"),
                "{\"Name\":\"Child Mod\",\"UniqueID\":\"Example.Child\",\"ContentPackFor\":{\"UniqueID\":\"Pathoschild.ContentPatcher\"}}");

            // 子条目故意放在父条目前面，验证写入器不会受 sources.json 顺序影响。
            using var document = JsonDocument.Parse(
                "[{\"name\":\"Child Mod\",\"directoryName\":\"Child Mod\"," +
                "\"source\":{\"platform\":\"Curseforge\",\"projectId\":111,\"fileId\":222}}," +
                "{\"name\":\"Parent Mod\",\"directoryName\":\"Parent Mod\",\"isParentMod\":true," +
                "\"childMods\":[{\"name\":\"Child Mod\",\"relativePath\":\"Old Parent/Child Mod\"}]," +
                "\"source\":{\"platform\":\"Curseforge\",\"projectId\":994458,\"fileId\":8390242}}]");
            var writer = typeof(ModpackInstallService).GetMethod(
                "WriteSourceCredentials",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(writer);

            writer!.Invoke(null, [document.RootElement.EnumerateArray().Select(item => item.Clone()).ToList(), modsPath]);

            using var childDocument = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(childPath, "svl-source.json")));
            var child = childDocument.RootElement;
            Assert.AreEqual("parent-inherited", child.GetProperty("sourceKind").GetString());
            Assert.AreEqual("Parent Mod", child.GetProperty("parentMod").GetProperty("relativePath").GetString());
            Assert.IsFalse(child.TryGetProperty("projectId", out _));
            Assert.IsFalse(child.TryGetProperty("fileId", out _));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ExistingCompositeSources_ShouldRepairMixedFileIdsOnReload()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-existing-composite-repair-test-" + Guid.NewGuid().ToString("N"));
        var modsPath = Path.Combine(root, "Mods");
        var parentPath = Path.Combine(modsPath, "Parent Mod");
        var childPaths = new[]
        {
            Path.Combine(modsPath, "Child One"),
            Path.Combine(modsPath, "Child Two"),
            Path.Combine(modsPath, "Child Three"),
            Path.Combine(modsPath, "Child Four"),
            Path.Combine(modsPath, "Child Five"),
            Path.Combine(modsPath, "Child Six")
        };
        try
        {
            Directory.CreateDirectory(parentPath);
            foreach (var childPath in childPaths)
            {
                Directory.CreateDirectory(childPath);
            }
            File.WriteAllText(
                Path.Combine(parentPath, "manifest.json"),
                "{\"Name\":\"Parent Mod\",\"UniqueID\":\"Example.Parent\",\"EntryDll\":\"Parent.dll\"}");
            for (var index = 0; index < childPaths.Length; index++)
            {
                File.WriteAllText(
                    Path.Combine(childPaths[index], "manifest.json"),
                    $"{{\"Name\":\"Child {index + 1}\",\"UniqueID\":\"Example.Child{index + 1}\",\"ContentPackFor\":{{\"UniqueID\":\"Pathoschild.ContentPatcher\"}}}}");
            }

            File.WriteAllText(
                Path.Combine(parentPath, "svl-source.json"),
                "{\"platform\":\"Curseforge\",\"projectId\":\"994458\",\"fileId\":\"8390242\",\"sourceKind\":\"modpack-entry\"}");
            foreach (var childPath in childPaths)
            {
                File.WriteAllText(
                    Path.Combine(childPath, "svl-source.json"),
                    "{\"platform\":\"Curseforge\",\"projectId\":\"994458\",\"fileId\":\"5276101\",\"sourceKind\":\"modpack-entry\",\"hasUpdate\":true,\"latestVersion\":\"6.7.1\"}");
            }

            var repair = typeof(ModpackInstallService).GetMethod(
                "RepairCompositeSourceCredentials",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(repair);
            repair!.Invoke(null, [modsPath]);

            using var parentDocument = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(parentPath, "svl-source.json")));
            var parent = parentDocument.RootElement;
            Assert.AreEqual("modpack-entry", parent.GetProperty("sourceKind").GetString());
            Assert.IsTrue(parent.GetProperty("isParentMod").GetBoolean());
            Assert.AreEqual(childPaths.Length, parent.GetProperty("childMods").GetArrayLength());

            foreach (var childPath in childPaths)
            {
                using var childDocument = JsonDocument.Parse(
                    File.ReadAllText(Path.Combine(childPath, "svl-source.json")));
                var child = childDocument.RootElement;
                Assert.AreEqual("parent-inherited", child.GetProperty("sourceKind").GetString());
                Assert.IsFalse(child.TryGetProperty("projectId", out _));
                Assert.IsFalse(child.TryGetProperty("fileId", out _));
                Assert.IsFalse(child.GetProperty("hasUpdate").GetBoolean());
                Assert.AreEqual("未检查", child.GetProperty("updateStatus").GetString());
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ExistingMarketTownCompositeSources_ShouldRepairRealFolderNames()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-market-town-composite-repair-test-" + Guid.NewGuid().ToString("N"));
        var modsPath = Path.Combine(root, "Mods");
        var parentPath = Path.Combine(modsPath, "[] MarketTown");
        var childPath = Path.Combine(modsPath, "[CP] CloneNPC_RSV");
        try
        {
            Directory.CreateDirectory(parentPath);
            Directory.CreateDirectory(childPath);
            File.WriteAllText(
                Path.Combine(parentPath, "manifest.json"),
                "{\"Name\":\"Market Town\",\"UniqueID\":\"d5a1lamdtd.MarketTown\",\"Version\":\"6.7.1\",\"EntryDll\":\"MarketTown.dll\"}");
            File.WriteAllText(
                Path.Combine(childPath, "manifest.json"),
                "{\"Name\":\"MarketTown - Cloned NPC RSV\",\"UniqueID\":\"d5a1lamdtd.MarketTown.CloneNPC_RSV\",\"Version\":\"5.0.0\",\"ContentPackFor\":{\"UniqueID\":\"Pathoschild.ContentPatcher\"}}");

            File.WriteAllText(
                Path.Combine(parentPath, "svl-source.json"),
                "{\"platform\":\"Curseforge\",\"projectId\":\"994458\",\"fileId\":\"8390242\",\"sourceKind\":\"modpack-entry\"}");
            File.WriteAllText(
                Path.Combine(childPath, "svl-source.json"),
                "{\"platform\":\"Curseforge\",\"projectId\":\"994458\",\"fileId\":\"5276101\",\"sourceKind\":\"modpack-entry\",\"hasUpdate\":true,\"latestVersion\":\"6.7.1\",\"updateStatus\":\"可更新 -> 6.7.1\"}");

            var repair = typeof(ModpackInstallService).GetMethod(
                "RepairCompositeSourceCredentials",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(repair);
            repair!.Invoke(null, [modsPath]);

            using var parentDocument = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(parentPath, "svl-source.json")));
            var parent = parentDocument.RootElement;
            Assert.AreEqual("modpack-entry", parent.GetProperty("sourceKind").GetString());
            Assert.IsTrue(parent.GetProperty("isParentMod").GetBoolean());
            Assert.AreEqual("[CP] CloneNPC_RSV", parent.GetProperty("childMods")[0].GetProperty("relativePath").GetString());

            using var childDocument = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(childPath, "svl-source.json")));
            var child = childDocument.RootElement;
            Assert.AreEqual("parent-inherited", child.GetProperty("sourceKind").GetString());
            Assert.AreEqual("[] MarketTown", child.GetProperty("parentMod").GetProperty("relativePath").GetString());
            Assert.IsFalse(child.TryGetProperty("projectId", out _));
            Assert.IsFalse(child.TryGetProperty("fileId", out _));
            Assert.IsFalse(child.GetProperty("hasUpdate").GetBoolean());
            Assert.AreEqual("未检查", child.GetProperty("updateStatus").GetString());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ExistingCompositeSources_ShouldRecoverChildWithoutSourceCredentialByManifestNamespace()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-composite-missing-child-source-test-" + Guid.NewGuid().ToString("N"));
        var modsPath = Path.Combine(root, "Mods");
        var parentPath = Path.Combine(modsPath, "Parent Mod");
        var childPath = Path.Combine(parentPath, "Child Pack");
        var unrelatedPath = Path.Combine(modsPath, "Unrelated Pack");
        try
        {
            foreach (var directory in new[] { parentPath, childPath, unrelatedPath })
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                Path.Combine(parentPath, "manifest.json"),
                "{\"Name\":\"Parent Mod\",\"UniqueID\":\"Example.Parent\",\"EntryDll\":\"Parent.dll\"}");
            File.WriteAllText(
                Path.Combine(childPath, "manifest.json"),
                "{\"Name\":\"Parent Mod - Child Pack\",\"UniqueID\":\"Example.Parent.Child\",\"ContentPackFor\":{\"UniqueID\":\"Pathoschild.ContentPatcher\"}}");
            File.WriteAllText(
                Path.Combine(unrelatedPath, "manifest.json"),
                "{\"Name\":\"Unrelated Pack\",\"UniqueID\":\"Example.Unrelated\",\"ContentPackFor\":{\"UniqueID\":\"Pathoschild.ContentPatcher\"}}");
            File.WriteAllText(
                Path.Combine(parentPath, "svl-source.json"),
                "{\"platform\":\"Curseforge\",\"projectId\":\"994458\",\"fileId\":\"8390242\"}");

            var repair = typeof(ModpackInstallService).GetMethod(
                "RepairCompositeSourceCredentials",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(repair);
            repair!.Invoke(null, [modsPath]);

            using var childDocument = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(childPath, "svl-source.json")));
            var child = childDocument.RootElement;
            Assert.AreEqual("parent-inherited", child.GetProperty("sourceKind").GetString());
            Assert.AreEqual("Parent Mod", child.GetProperty("parentMod").GetProperty("name").GetString());
            Assert.IsFalse(child.TryGetProperty("projectId", out _));
            Assert.IsFalse(child.TryGetProperty("fileId", out _));
            Assert.IsFalse(File.Exists(Path.Combine(unrelatedPath, "svl-source.json")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void VersionSettings_ShouldKeepStandaloneModpackEntryActionable()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-modpack-source-guard-test-" + Guid.NewGuid().ToString("N"));
        var modDirectory = Path.Combine(root, "[CP] CloneNPC_RSV");
        try
        {
            Directory.CreateDirectory(modDirectory);
            File.WriteAllText(
                Path.Combine(modDirectory, "manifest.json"),
                "{\"Name\":\"MarketTown - Cloned NPC RSV\",\"UniqueID\":\"d5a1lamdtd.MarketTown.CloneNPC_RSV\",\"Version\":\"5.0.0\",\"ContentPackFor\":{\"UniqueID\":\"Pathoschild.ContentPatcher\",\"MinimumVersion\":\"2.0.0\"}}");
            File.WriteAllText(
                Path.Combine(modDirectory, "svl-source.json"),
                "{\"platform\":\"Curseforge\",\"projectId\":\"994458\",\"modId\":\"994458\",\"fileId\":\"5276101\",\"sourceKind\":\"modpack-entry\",\"hasUpdate\":true,\"latestVersion\":\"6.7.1\",\"updateUrl\":\"https://edge.forgecdn.net/files/8390/242/MarketTown.zip\",\"updateFileId\":\"8390242\"}");

            var reader = typeof(VersionSettingsPageViewModel).GetMethod(
                "TryReadSourceCredential",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            var guard = typeof(VersionSettingsPageViewModel).GetMethod(
                "IsInheritedModpackSource",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(reader);
            Assert.IsNotNull(guard);

            var credential = reader!.Invoke(null, [modDirectory]);
            Assert.IsNotNull(credential);
            Assert.IsFalse((bool)guard!.Invoke(null, [modDirectory, credential])!);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void VersionSettings_ShouldNotInferParentFromLegacyModpackEntry()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-legacy-modpack-entry-content-pack-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            // 这对应实际旧数据：子 Mod 误写成 modpack-entry，并复制了整合包
            // 的 project/file ID，但没有自己的文件名或下载地址。
            File.WriteAllText(
                Path.Combine(root, "manifest.json"),
                "{\n" +
                "  \"Name\": \"MarketTown - Cloned NPC RSV\",\n" +
                "  \"UniqueID\": \"d5a1lamdtd.MarketTown.CloneNPC_RSV\",\n" +
                "  \"Version\": \"5.0.0\",\n" +
                "  \"ContentPackFor\": { \"UniqueID\": \"Pathoschild.ContentPatcher\" },\n" +
                "}");
            File.WriteAllText(
                Path.Combine(root, "svl-source.json"),
                "{\"platform\":\"Curseforge\",\"projectId\":\"994458\",\"fileId\":\"5276101\",\"sourceKind\":\"modpack-entry\",\"hasUpdate\":true,\"latestVersion\":\"6.7.1\"}");

            var reader = typeof(VersionSettingsPageViewModel).GetMethod(
                "TryReadSourceCredential",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            var guard = typeof(VersionSettingsPageViewModel).GetMethod(
                "IsInheritedModpackSource",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(reader);
            Assert.IsNotNull(guard);

            var credential = reader!.Invoke(null, [root]);
            Assert.IsNotNull(credential);
            Assert.IsFalse((bool)guard!.Invoke(null, [root, credential])!);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void VersionSettings_ShouldRecognizeInheritedNestedModRegardlessOfPlatform()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-nested-source-platform-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(
                Path.Combine(root, "manifest.json"),
                "{\"Name\":\"Nested Content Pack\",\"UniqueID\":\"Example.Nested\",\"ContentPackFor\":{\"UniqueID\":\"Pathoschild.ContentPatcher\"}} ");
            File.WriteAllText(
                Path.Combine(root, "svl-source.json"),
                "{\"platform\":\"NexusMods\",\"sourceKind\":\"parent-inherited\",\"parentMod\":{\"name\":\"Parent Mod\",\"relativePath\":\"Parent Mod\"}} ");

            var reader = typeof(VersionSettingsPageViewModel).GetMethod(
                "TryReadSourceCredential",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            var guard = typeof(VersionSettingsPageViewModel).GetMethod(
                "IsInheritedModpackSource",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(reader);
            Assert.IsNotNull(guard);

            var credential = reader!.Invoke(null, [root]);
            Assert.IsNotNull(credential);
            Assert.IsTrue((bool)guard!.Invoke(null, [root, credential])!);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ExportSourceCredential_ShouldTreatHttpUrlAsCompleteSource()
    {
        var item = new ExportModSelectionItem
        {
            SourcePlatform = "未知",
            SourceDownloadUrl = "https://example.invalid/content-patcher.zip"
        };

        Assert.IsTrue(item.HasSourceCredential);
        Assert.IsTrue(item.HasCompleteSourceCredential);
        Assert.AreEqual("直链", item.SourceDescription);
    }

    [TestMethod]
    public void ModDetails_ShouldNotTreatInformationalDownloadTextAsInstallable()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-details-download-option-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = new RemoteCatalogService(new AppUserSettingsStore(root));
            var details = new ModDetailsPageViewModel(catalog, new DialogService());
            details.SetResource("[NexusMods#29868] Content Patcher", "");

            details.DownloadOptions.Add("暂无可下载文件");
            details.SelectedDownloadOption = "暂无可下载文件";
            Assert.IsFalse(details.CanQueueDownload);
            Assert.IsFalse(details.CanInstallSelectedDownloadOption);
            Assert.AreEqual(0, details.VersionDownloadGroups.Count);

            details.DownloadOptions.Clear();
            details.DownloadOptions.Add("File 7448774: Content Patcher 2.9.0");
            details.SelectedDownloadOption = details.DownloadOptions[0];
            Assert.IsTrue(details.CanQueueDownload);
            Assert.IsTrue(details.CanInstallSelectedDownloadOption);
            Assert.AreEqual(1, details.VersionDownloadGroups.SelectMany(group => group.Files).Count());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task ModDetails_ShouldExplainWhenResourceSourceIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-details-missing-source-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = new RemoteCatalogService(new AppUserSettingsStore(root));
            var details = new ModDetailsPageViewModel(catalog, new DialogService());

            await details.LoadDetailsAsync(new CatalogResourceIdentity(
                0,
                "未知资源",
                CatalogSource.Unknown,
                false,
                string.Empty));

            StringAssert.Contains(details.DetailsStatus, "未识别资源来源");
            Assert.IsFalse(details.CanQueueDownload);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModDetails_ShouldStripDownloadOptionMetadataBeforeResolvingUrl()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-details-url-option-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = new RemoteCatalogService(new AppUserSettingsStore(root));
            var details = new ModDetailsPageViewModel(catalog, new DialogService());
            details.SetResource("[Curseforge#1012214] Content Patcher", "");

            details.DownloadOptions.Add(
                "File 5312529: Content Patcher 2.9.0 | https://example.invalid/content-patcher.zip ~~channel=Release;gamever=1.6");
            details.SelectedDownloadOption = details.DownloadOptions[0];

            Assert.IsTrue(details.CanQueueDownload);
            Assert.IsTrue(details.CanOpenSelectedDownloadOptionInBrowser);

            details.DownloadOptions.Clear();
            details.DownloadOptions.Add(
                "File 5312529: Content Patcher 2.9.0 | https://example.invalid/content-patcher.zip ~~ channel=Release");
            details.SelectedDownloadOption = details.DownloadOptions[0];
            Assert.IsTrue(details.CanOpenSelectedDownloadOptionInBrowser);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModpackSourceParser_ShouldRecoverNexusFileIdFromFileName()
    {
        using var document = JsonDocument.Parse(
            "{\"source\":{\"platform\":\"NexusMods\",\"projectId\":\"29868\",\"fileName\":\"File 7448774_ Content Patcher 2.9.0 2.9.0.zip\"}}");
        var parser = typeof(ModpackInstallService).GetMethod(
            "TryGetModSourceDescriptor",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        var arguments = new object[] { document.RootElement, null! };
        var parsed = (bool)parser.Invoke(null, arguments)!;
        Assert.IsTrue(parsed);

        var descriptor = arguments[1];
        Assert.IsNotNull(descriptor);
        Assert.AreEqual("29868", descriptor!.GetType().GetProperty("ProjectId")?.GetValue(descriptor));
        Assert.AreEqual("7448774", descriptor.GetType().GetProperty("FileId")?.GetValue(descriptor));
    }

    [TestMethod]
    public void ModpackSourceParser_ShouldRecoverNexusFileIdFromLegacyFileField()
    {
        using var document = JsonDocument.Parse(
            "{\"source\":{\"platform\":\"NexusMods\",\"projectId\":\"29868\",\"file\":\"File 7448774_ Content Patcher 2.9.0.zip\"}}");
        var parser = typeof(ModpackInstallService).GetMethod(
            "TryGetModSourceDescriptor",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        var arguments = new object[] { document.RootElement, null! };
        Assert.IsTrue((bool)parser!.Invoke(null, arguments)!);

        var descriptor = arguments[1];
        Assert.IsNotNull(descriptor);
        Assert.AreEqual("NexusMods", descriptor!.GetType().GetProperty("Platform")?.GetValue(descriptor));
        Assert.AreEqual("29868", descriptor.GetType().GetProperty("ProjectId")?.GetValue(descriptor));
        Assert.AreEqual("7448774", descriptor.GetType().GetProperty("FileId")?.GetValue(descriptor));
    }

    [TestMethod]
    public void LocalSourceMetadata_ShouldTreatLogicalFilenameAsFileNameAndFileIdHint()
    {
        var parser = typeof(VersionSettingsPageViewModel).GetMethod(
            "TryReadSvlSourceMetadata",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        var root = Path.Combine(
            Path.GetTempPath(),
            "svl-logical-source-metadata-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(
                Path.Combine(root, "svl-source.json"),
                "{\"platform\":\"NexusMods\",\"projectId\":\"29868\",\"logicalFilename\":\"File 7448774_ Content Patcher 2.9.0.zip\"}");

            var metadata = parser!.Invoke(null, [root]);
            Assert.IsNotNull(metadata);
            Assert.AreEqual(
                "File 7448774_ Content Patcher 2.9.0.zip",
                metadata!.GetType().GetProperty("FileName")?.GetValue(metadata));
            Assert.AreEqual(
                "7448774",
                metadata.GetType().GetProperty("FileId")?.GetValue(metadata));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModpackSourceParser_ShouldRecoverCurseforgeIdsFromLegacyFileToken()
    {
        using var document = JsonDocument.Parse(
            "{\"name\":\"Content Patcher\",\"file\":\"cf-1012214-5312529.zip\"}");
        var parser = typeof(ModpackInstallService).GetMethod(
            "TryGetModSourceDescriptor",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        var arguments = new object[] { document.RootElement, null! };
        Assert.IsTrue((bool)parser!.Invoke(null, arguments)!);

        var descriptor = arguments[1];
        Assert.IsNotNull(descriptor);
        Assert.AreEqual("Curseforge", descriptor!.GetType().GetProperty("Platform")?.GetValue(descriptor));
        Assert.AreEqual("1012214", descriptor.GetType().GetProperty("ProjectId")?.GetValue(descriptor));
        Assert.AreEqual("5312529", descriptor.GetType().GetProperty("FileId")?.GetValue(descriptor));
    }

    [TestMethod]
    public void ModpackSourceParser_ShouldRecoverNexusIdsFromPageUrlWithEmptyFields()
    {
        using var document = JsonDocument.Parse(
            "{\"source\":{\"platform\":\"NexusMods\",\"projectId\":\"\",\"fileId\":\"\",\"downloadUrl\":\"https://www.nexusmods.com/stardewvalley/mods/29868?tab=files&file_id=7448774&nmm=1\"}}");
        var parser = typeof(ModpackInstallService).GetMethod(
            "TryGetModSourceDescriptor",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        var arguments = new object[] { document.RootElement, null! };
        Assert.IsTrue((bool)parser!.Invoke(null, arguments)!);

        var descriptor = arguments[1];
        Assert.IsNotNull(descriptor);
        Assert.AreEqual("NexusMods", descriptor!.GetType().GetProperty("Platform")?.GetValue(descriptor));
        Assert.AreEqual("29868", descriptor.GetType().GetProperty("ProjectId")?.GetValue(descriptor));
        Assert.AreEqual("7448774", descriptor.GetType().GetProperty("FileId")?.GetValue(descriptor));
    }

    [TestMethod]
    public void ModpackSourceParser_ShouldInferNexusPlatformBeforeDirectDownloadBranch()
    {
        using var document = JsonDocument.Parse(
            "{\"projectId\":\"29868\",\"fileId\":\"7448774\",\"downloadUrl\":\"https://example.invalid/files/content-patcher.zip\"}");
        var parser = typeof(ModpackInstallService).GetMethod(
            "TryGetModSourceDescriptor",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        var arguments = new object[] { document.RootElement, null! };
        Assert.IsTrue((bool)parser!.Invoke(null, arguments)!);

        var descriptor = arguments[1];
        Assert.IsNotNull(descriptor);
        Assert.AreEqual("NexusMods", descriptor!.GetType().GetProperty("Platform")?.GetValue(descriptor));
        Assert.AreEqual("29868", descriptor.GetType().GetProperty("ProjectId")?.GetValue(descriptor));
        Assert.AreEqual("7448774", descriptor.GetType().GetProperty("FileId")?.GetValue(descriptor));
    }

    [TestMethod]
    public void ModpackSourceParser_ShouldRecoverCurseforgeFileIdFromCdnUrl()
    {
        var parser = typeof(ModpackInstallService).GetMethod(
            "TryParseCurseforgeFileIdFromCdnUrl",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        var arguments = new object[]
        {
            "https://edge.forgecdn.net/files/5312/529/ContentPatcher-2.9.0.zip",
            0L
        };
        var parsed = (bool)parser.Invoke(null, arguments)!;
        Assert.IsTrue(parsed);
        Assert.AreEqual(5312529L, arguments[1]);
    }

    [TestMethod]
    public void ModpackSourceParser_ShouldPersistCdnFileIdWhenSourceEntryOmitsIt()
    {
        using var document = JsonDocument.Parse("""
            {
              "name": "Content Patcher",
              "source": {
                "platform": "Curseforge",
                "projectId": "1012214",
                "downloadUrl": "https://edge.forgecdn.net/files/5312/529/ContentPatcher-2.9.0.zip"
              }
            }
            """);

        var parser = typeof(ModpackInstallService).GetMethod(
            "TryGetModSourceDescriptor",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        var arguments = new object[] { document.RootElement, null! };
        Assert.IsTrue((bool)parser!.Invoke(null, arguments)!);

        var descriptor = arguments[1];
        Assert.IsNotNull(descriptor);
        Assert.AreEqual("Curseforge", descriptor!.GetType().GetProperty("Platform")?.GetValue(descriptor));
        Assert.AreEqual("1012214", descriptor.GetType().GetProperty("ProjectId")?.GetValue(descriptor));
        Assert.AreEqual("5312529", descriptor.GetType().GetProperty("FileId")?.GetValue(descriptor));
    }

    [TestMethod]
    public void ExportSourceMetadata_ShouldRecoverFullCurseforgeCdnFileId()
    {
        var parser = typeof(VersionSettingsPageViewModel).GetMethod(
            "TryExtractSourceFileIdFromLocalMetadata",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        var fileId = parser!.Invoke(
            null,
            [
                "Curseforge",
                "1012214",
                new[] { "https://edge.forgecdn.net/files/5312/529/ContentPatcher-2.9.0.zip" }
            ]);

        Assert.AreEqual("5312529", fileId);
    }

    [TestMethod]
    public void ExportSourceFileId_ShouldRecoverFromNexusCacheWhenLocalMetadataLacksFileId()
    {
        var parser = typeof(VersionSettingsPageViewModel).GetMethod(
            "TryExtractSourceFileIdFromLocalMetadata",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        var root = Path.Combine(Path.GetTempPath(), "svl-export-cache-file-id-test-" + Guid.NewGuid().ToString("N"));
        var sourcePath = Path.Combine(root, "source.zip");
        var modId = 930_000_000L + Random.Shared.Next(1_000_000);
        var fileId = 930_000_000L + Random.Shared.Next(1_000_000);
        var cachePath = NexusDownloadCache.GetCachePath(modId, fileId);

        try
        {
            Directory.CreateDirectory(root);
            using (var archive = ZipFile.Open(sourcePath, ZipArchiveMode.Create))
            {
                WriteArchiveText(archive, "Actual Mod/manifest.json", "{\"Name\":\"Actual Mod\",\"UniqueID\":\"Actual.Mod\",\"Version\":\"1.0.0\"}");
                WriteArchiveText(archive, "content.txt", "cached nexus file");
            }

            NexusDownloadCache.Save(modId, fileId, sourcePath);

            var result = parser!.Invoke(null, [
                "NexusMods",
                modId.ToString(),
                new string[] { "", null, "Content Patcher" }
            ]) as string;

            Assert.AreEqual(fileId.ToString(), result);
        }
        finally
        {
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void CollectionSourceConverter_ShouldAcceptLegacyStringAndUrlForms()
    {
        var sourceType = typeof(CollectionInstallService).Assembly.GetType(
            "SVL.Avalonia.Services.NexusCollectionJsonModSource");
        Assert.IsNotNull(sourceType);
        var optionsField = typeof(CollectionInstallService).GetField(
            "CollectionJsonOptions",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(optionsField);
        var options = (JsonSerializerOptions)optionsField!.GetValue(null)!;

        var source = JsonSerializer.Deserialize(
            "\"cf-1012214-5312529\"",
            sourceType!,
            options);
        Assert.IsNotNull(source);
        Assert.AreEqual("Curseforge", sourceType!.GetProperty("Type")?.GetValue(source));
        Assert.AreEqual(1012214L, sourceType.GetProperty("ModId")?.GetValue(source));
        Assert.AreEqual(5312529L, sourceType.GetProperty("FileId")?.GetValue(source));

        var nexusSource = JsonSerializer.Deserialize(
            "{\"url\":\"https://www.nexusmods.com/stardewvalley/mods/29868?tab=files&file_id=7448774\",\"projectId\":\"29868\"}",
            sourceType,
            options);
        Assert.IsNotNull(nexusSource);
        Assert.AreEqual("NexusMods", sourceType.GetProperty("Type")?.GetValue(nexusSource));
        Assert.AreEqual(29868L, sourceType.GetProperty("ModId")?.GetValue(nexusSource));
        Assert.AreEqual(7448774L, sourceType.GetProperty("FileId")?.GetValue(nexusSource));

        var nexusPageWithLogicalFilename = JsonSerializer.Deserialize(
            "{\"url\":\"https://www.nexusmods.com/stardewvalley/mods/29868?tab=files&nmm=1\",\"projectId\":29868,\"logicalFilename\":\"File 7448774_ Content Patcher 2.9.0.zip\"}",
            sourceType,
            options);
        Assert.IsNotNull(nexusPageWithLogicalFilename);
        Assert.AreEqual("NexusMods", sourceType.GetProperty("Type")?.GetValue(nexusPageWithLogicalFilename));
        Assert.AreEqual(29868L, sourceType.GetProperty("ModId")?.GetValue(nexusPageWithLogicalFilename));
        Assert.AreEqual(7448774L, sourceType.GetProperty("FileId")?.GetValue(nexusPageWithLogicalFilename));

        var curseforgeAliases = JsonSerializer.Deserialize(
            "{\"site\":\"CurseForge\",\"project\":1012214,\"file\":5312529}",
            sourceType,
            options);
        Assert.IsNotNull(curseforgeAliases);
        Assert.AreEqual("CurseForge", sourceType.GetProperty("Type")?.GetValue(curseforgeAliases));
        Assert.AreEqual(1012214L, sourceType.GetProperty("ModId")?.GetValue(curseforgeAliases));
        Assert.AreEqual(5312529L, sourceType.GetProperty("FileId")?.GetValue(curseforgeAliases));

        var emptyOuterFields = JsonSerializer.Deserialize(
            "{\"type\":\"\",\"url\":\"\",\"source\":{\"type\":\"Curseforge\",\"url\":\"https://edge.forgecdn.net/files/5312/529/ContentPatcher.zip\"}}",
            sourceType,
            options);
        Assert.IsNotNull(emptyOuterFields);
        Assert.AreEqual("Curseforge", sourceType.GetProperty("Type")?.GetValue(emptyOuterFields));
        Assert.AreEqual(
            "https://edge.forgecdn.net/files/5312/529/ContentPatcher.zip",
            sourceType.GetProperty("Url")?.GetValue(emptyOuterFields));
    }

    [TestMethod]
    public void CollectionModConverter_ShouldNormalizeTopLevelSourceFields()
    {
        var assembly = typeof(CollectionInstallService).Assembly;
        var modType = assembly.GetType("SVL.Avalonia.Services.NexusCollectionJsonMod");
        Assert.IsNotNull(modType);

        var optionsField = typeof(CollectionInstallService).GetField(
            "CollectionJsonOptions",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(optionsField);
        var options = (JsonSerializerOptions)optionsField!.GetValue(null)!;

        var mod = JsonSerializer.Deserialize(
            "{\"name\":\"Content Patcher\",\"version\":\"2.9.0\",\"type\":\"nexus\",\"modId\":1915,\"fileId\":7448774}",
            modType!,
            options);
        Assert.IsNotNull(mod);

        var source = modType!.GetProperty("Source")?.GetValue(mod);
        Assert.IsNotNull(source);
        var sourceType = source!.GetType();
        Assert.AreEqual("nexus", sourceType.GetProperty("Type")?.GetValue(source));
        Assert.AreEqual(1915L, sourceType.GetProperty("ModId")?.GetValue(source));
        Assert.AreEqual(7448774L, sourceType.GetProperty("FileId")?.GetValue(source));

        var mixed = JsonSerializer.Deserialize(
            "{\"name\":\"Generic Mod Config Menu\",\"source\":{\"type\":\"nexus\",\"modId\":5098},\"fileId\":123456}",
            modType,
            options);
        Assert.IsNotNull(mixed);
        var mixedSource = modType.GetProperty("Source")?.GetValue(mixed);
        Assert.IsNotNull(mixedSource);
        Assert.AreEqual(5098L, sourceType.GetProperty("ModId")?.GetValue(mixedSource));
        Assert.AreEqual(123456L, sourceType.GetProperty("FileId")?.GetValue(mixedSource));
    }

    [TestMethod]
    public void ModpackInstall_SourceReuseSupportsNexusCredential()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-nexus-source-reuse-test-" + Guid.NewGuid().ToString("N"));
        var modsPath = Path.Combine(root, "Mods");
        var modDirectory = Path.Combine(modsPath, "Generic Mod");
        try
        {
            Directory.CreateDirectory(modDirectory);
            File.WriteAllText(
                Path.Combine(modDirectory, "manifest.json"),
                "{\"Name\":\"Generic Mod\",\"UniqueID\":\"Test.GenericMod\",\"Version\":\"1.0.0\"}");
            File.WriteAllText(
                Path.Combine(modDirectory, "svl-source.json"),
                "{\"platform\":\"NexusMods\",\"projectId\":\"29868\",\"fileId\":\"7448774\"}");

            var finder = typeof(ModpackInstallService).GetMethod(
                "FindInstalledModDirectoriesBySource",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(finder);

            var result = finder.Invoke(
                null,
                [modsPath, "NexusMods", 29868L, 7448774L])
                as IReadOnlyList<string>;

            Assert.IsNotNull(result);
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(modDirectory, result[0]);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModpackInstall_ShouldNotReuseSourceOnlyDirectory()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "svl-source-only-directory-test-" + Guid.NewGuid().ToString("N"));
        var modsPath = Path.Combine(root, "Mods");
        var modDirectory = Path.Combine(modsPath, "ContentPatcher");

        try
        {
            Directory.CreateDirectory(modDirectory);
            File.WriteAllText(
                Path.Combine(modDirectory, "svl-source.json"),
                "{\"platform\":\"NexusMods\",\"projectId\":\"1915\",\"fileId\":\"7448774\"}");

            var finder = typeof(ModpackInstallService).GetMethod(
                "FindInstalledModDirectoriesBySource",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(finder);

            var result = finder!.Invoke(
                null,
                [modsPath, "NexusMods", 1915L, 7448774L]) as IReadOnlyList<string>;

            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Count);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModpackInstall_ShouldPreserveNxmDownloadCredentials()
    {
        var parser = new NxmLinkParser();
        var ok = parser.TryParse(
            "nxm://stardewvalley/mods/29868/files/7448774?key=test-key&expires=1910000000&user_id=123",
            out var parsed,
            out var error);
        Assert.IsTrue(ok, error);

        var builder = typeof(ModpackInstallService).GetMethod(
            "BuildNexusDownloadInfo",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(builder);

        var result = builder!.Invoke(null, [29868L, 7448774L, parsed]) as NxmLinkInfo;
        Assert.IsNotNull(result);
        Assert.AreEqual("test-key", result!.Key);
        Assert.AreEqual(1910000000L, result.Expires);
        Assert.AreEqual(123L, result.UserId);
    }

    [TestMethod]
    public void SmapiResolveLock_ShouldBeSharedByModpackAndCollection()
    {
        var lockFactory = typeof(ModpackInstallService).GetMethod(
            "GetSmapiResolveLock",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(lockFactory);

        var modpackLock = lockFactory!.Invoke(
            null,
            [Path.Combine("C:\\svl-lock-test", "game-a"), "SMAPI 4.5.2"]) as SemaphoreSlim;
        var collectionLock = lockFactory.Invoke(
            null,
            [Path.Combine("C:\\svl-lock-test", "game-b"), "4.5.2"]) as SemaphoreSlim;

        Assert.IsNotNull(modpackLock);
        Assert.IsNotNull(collectionLock);
        Assert.AreSame(modpackLock, collectionLock);
    }

    [TestMethod]
    public void SmapiGithubAssets_ShouldPreferSingleInstallerZip()
    {
        using var document = JsonDocument.Parse(
            "[{\"name\":\"SMAPI-4.5.2-installer-double-zipped.zip\",\"browser_download_url\":\"https://example.test/double.zip\"}," +
            "{\"name\":\"SMAPI-4.5.2-installer.zip\",\"browser_download_url\":\"https://example.test/installer.zip\"}]");
        var selector = typeof(RemoteCatalogService).GetMethod(
            "SelectSmapiInstallerDownloadUrl",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(selector);

        var result = selector!.Invoke(null, [document.RootElement]) as string;
        Assert.AreEqual("https://example.test/installer.zip", result);
    }

    [TestMethod]
    public void SmapiGithubAssets_ShouldAcceptCaseInsensitiveReleaseFields()
    {
        using var document = JsonDocument.Parse(
            "[{\"Name\":\"SMAPI-4.5.2-installer-double-zipped.zip\",\"Browser_Download_URL\":\"https://example.test/double.zip\"}," +
            "{\"NAME\":\"SMAPI-4.5.2-installer.zip\",\"BROWSER_DOWNLOAD_URL\":\"https://example.test/installer.zip\"}]");
        var selector = typeof(RemoteCatalogService).GetMethod(
            "SelectSmapiInstallerDownloadUrl",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(selector);

        var result = selector!.Invoke(null, [document.RootElement]) as string;
        Assert.AreEqual("https://example.test/installer.zip", result);
    }

    [TestMethod]
    public async Task SmapiCatalog_ShouldFallbackToLatestWhenReleaseListIsEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-smapi-latest-fallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var handler = new FixtureHttpMessageHandler(request =>
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.EndsWith("/releases", StringComparison.OrdinalIgnoreCase))
                {
                    // 模拟 GitHub 发布列表被代理截断为空，但 latest 端点仍可用。
                    return JsonResponse("[]");
                }

                if (path.EndsWith("/releases/latest", StringComparison.OrdinalIgnoreCase))
                {
                    // 使用不同大小写字段，覆盖 GitHub/代理返回字段大小写变化。
                    return JsonResponse(
                        "{\"TAG_NAME\":\"v4.5.2\",\"NAME\":\"SMAPI 4.5.2\",\"PUBLISHED_AT\":\"2024-01-01T00:00:00Z\",\"ASSETS\":[" +
                        "{\"NAME\":\"SMAPI-4.5.2-installer.zip\",\"BROWSER_DOWNLOAD_URL\":\"https://example.test/smapi.zip\"}]}" );
                }

                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            });
            using var client = new HttpClient(handler);
            var service = new RemoteCatalogService(new AppUserSettingsStore(root), client);

            var entries = await service.GetSmapiVersionEntriesAsync(page: 1, perPage: 10);

            Assert.AreEqual(1, entries.Count);
            Assert.AreEqual("4.5.2", entries[0].Version);
            Assert.AreEqual("GitHub", entries[0].Source);
            Assert.AreEqual("https://example.test/smapi.zip", entries[0].DownloadUrl);
            CollectionAssert.Contains(
                handler.Requests.Select(request => request.AbsolutePath).ToList(),
                "/repos/Pathoschild/SMAPI/releases/latest");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task SmapiCatalog_ShouldHonorCancellationBeforeNetworkFallbacks()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-smapi-cancel-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = new RemoteCatalogService(new AppUserSettingsStore(root));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsExceptionAsync<OperationCanceledException>(
                () => catalog.GetSmapiVersionEntriesAsync(cancellationToken: cancellation.Token));
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(
                () => catalog.GetLatestSmapiVersionEntryAsync(cancellation.Token));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void SmapiDownload_ShouldUnwrapDoubleZippedInstaller()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-smapi-double-zip-test-" + Guid.NewGuid().ToString("N"));
        var innerPath = Path.Combine(root, "inner.zip");
        var outerPath = Path.Combine(root, "outer.zip");
        try
        {
            Directory.CreateDirectory(root);
            using (var inner = ZipFile.Open(innerPath, ZipArchiveMode.Create))
            {
                WriteArchiveText(inner, "installer/internal/windows/install.dat", "smapi");
            }

            using (var outer = ZipFile.Open(outerPath, ZipArchiveMode.Create))
            using (var source = File.OpenRead(innerPath))
            using (var target = outer.CreateEntry("SMAPI-4.5.2-installer.zip").Open())
            {
                source.CopyTo(target);
            }

            var unwrap = typeof(ModpackInstallService).GetMethod(
                "TryUnwrapDoubleZipped",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(unwrap);
            unwrap!.Invoke(null, [outerPath]);

            using var result = ZipFile.OpenRead(outerPath);
            Assert.IsNotNull(result.GetEntry("installer/internal/windows/install.dat"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void SmapiCacheNormalizer_ShouldUnwrapDoubleZippedNexusCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-smapi-cache-double-zip-test-" + Guid.NewGuid().ToString("N"));
        var innerPath = Path.Combine(root, "inner.zip");
        var outerPath = Path.Combine(root, "2400_7448774.zip");
        try
        {
            Directory.CreateDirectory(root);
            using (var inner = ZipFile.Open(innerPath, ZipArchiveMode.Create))
            {
                WriteArchiveText(
                    inner,
                    "SMAPI 4.5.2 installer/internal/windows/install.dat",
                    "smapi");
            }

            using (var outer = ZipFile.Open(outerPath, ZipArchiveMode.Create))
            using (var source = File.OpenRead(innerPath))
            using (var target = outer.CreateEntry("SMAPI-4.5.2-installer.zip").Open())
            {
                source.CopyTo(target);
            }

            var normalizer = typeof(ModpackInstallService).GetMethod(
                "TryNormalizeSmapiArchive",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(normalizer);

            Assert.IsTrue((bool)normalizer!.Invoke(null, [outerPath])!);
            using var normalized = ZipFile.OpenRead(outerPath);
            Assert.IsNotNull(normalized.GetEntry("SMAPI 4.5.2 installer/internal/windows/install.dat"));

            var inspectorType = typeof(ModpackInstallService).Assembly.GetType(
                "SVL.Avalonia.Services.SmapiPackageVersionInspector");
            var readVersion = inspectorType?.GetMethod(
                "TryReadVersion",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            Assert.IsNotNull(readVersion);
            Assert.AreEqual("4.5.2", readVersion!.Invoke(null, [outerPath]));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModpackInstall_FlattensNestedSevenZipModDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-7z-mod-flatten-test-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "cf-1012214-5312529.7z");
        var modsPath = Path.Combine(root, "Mods");
        try
        {
            Directory.CreateDirectory(root);
            var manifestPath = Path.Combine(root, "manifest.json");
            var contentPath = Path.Combine(root, "content.json");
            File.WriteAllText(manifestPath, "{\"Name\":\"Content Patcher\",\"UniqueID\":\"Pathoschild.ContentPatcher\"}");
            File.WriteAllText(contentPath, "{}");

            using (var writer = SevenZipWriter.OpenWriter(
                       archivePath,
                       new SevenZipWriterOptions(CompressionType.LZMA2)))
            using (var manifest = File.OpenRead(manifestPath))
            using (var content = File.OpenRead(contentPath))
            {
                writer.Write("Release/ActualMod/manifest.json", manifest, null);
                writer.Write("Release/ActualMod/content.json", content, null);
            }

            var installer = typeof(ModpackInstallService).GetMethod(
                "InstallDownloadedModArchive",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(installer);

            var arguments = new object[] { archivePath, modsPath, "cf-1012214-5312529", null };
            var success = (bool)installer.Invoke(null, arguments)!;
            var installedNames = arguments[3] as List<string>;

            Assert.IsTrue(success);
            Assert.IsNotNull(installedNames);
            Assert.AreEqual("ActualMod", installedNames.Single());
            Assert.IsTrue(File.Exists(Path.Combine(modsPath, "ActualMod", "manifest.json")));
            Assert.IsTrue(File.Exists(Path.Combine(modsPath, "ActualMod", "content.json")));
            Assert.IsFalse(Directory.Exists(Path.Combine(modsPath, "ActualMod", "Release")));
            Assert.IsFalse(Directory.Exists(Path.Combine(modsPath, "cf-1012214-5312529")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModpackInstall_UsesManifestNameForGeneratedCurseforgeDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-generated-mod-directory-test-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "cf-1012214-5312529.zip");
        var modsPath = Path.Combine(root, "Mods");
        try
        {
            Directory.CreateDirectory(root);
            using (var archive = System.IO.Compression.ZipFile.Open(
                       archivePath,
                       System.IO.Compression.ZipArchiveMode.Create))
            {
                WriteArchiveText(
                    archive,
                    "cf-1012214-5312529/manifest.json",
                    "{\"Name\":\"Content Patcher\",\"UniqueID\":\"Pathoschild.ContentPatcher\"}");
                WriteArchiveText(archive, "cf-1012214-5312529/content.json", "{}");
            }

            var installer = typeof(ModpackInstallService).GetMethod(
                "InstallDownloadedModArchive",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(installer);

            var arguments = new object[] { archivePath, modsPath, "cf-1012214-5312529", null };
            var success = (bool)installer.Invoke(null, arguments)!;
            var installedNames = arguments[3] as List<string>;

            Assert.IsTrue(success);
            Assert.IsNotNull(installedNames);
            Assert.AreEqual("Content Patcher", installedNames.Single());
            Assert.IsTrue(File.Exists(Path.Combine(modsPath, "Content Patcher", "manifest.json")));
            Assert.IsFalse(Directory.Exists(Path.Combine(modsPath, "cf-1012214-5312529")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModpackInstall_ShouldWritePerEntrySourceAndNestedParentRelation()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-modpack-source-tree-test-" + Guid.NewGuid().ToString("N"));
        var modsPath = Path.Combine(root, "Mods");
        var parentPath = Path.Combine(modsPath, "[CP] Parent Mod");
        var childPath = Path.Combine(parentPath, "Child Mod");
        try
        {
            Directory.CreateDirectory(childPath);
            File.WriteAllText(
                Path.Combine(parentPath, "manifest.json"),
                "{\"Name\":\"Parent Mod\",\"UniqueID\":\"Example.Parent\",\"Version\":\"1.0.0\"}");
            File.WriteAllText(
                Path.Combine(childPath, "manifest.json"),
                "{\"Name\":\"Child Mod\",\"UniqueID\":\"Example.Child\",\"Version\":\"1.0.0\",\"ContentPackFor\":{\"UniqueID\":\"Example.Parent\"}} ");

            var writer = typeof(ModpackInstallService).GetMethod(
                "WriteCurseforgeModpackSourceCredentials",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(writer);

            writer!.Invoke(null, [modsPath, new List<string> { "[CP] Parent Mod" }, "Curseforge", 994458L, 8390242L]);

            using var parentDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(parentPath, "svl-source.json")));
            var parent = parentDocument.RootElement;
            Assert.AreEqual("Curseforge", parent.GetProperty("platform").GetString());
            Assert.AreEqual("994458", parent.GetProperty("projectId").GetString());
            Assert.AreEqual("8390242", parent.GetProperty("fileId").GetString());
            Assert.AreEqual("modpack-entry", parent.GetProperty("sourceKind").GetString());
            Assert.IsTrue(parent.GetProperty("isParentMod").GetBoolean());
            Assert.AreEqual(1, parent.GetProperty("childMods").GetArrayLength());

            using var childDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(childPath, "svl-source.json")));
            var child = childDocument.RootElement;
            Assert.AreEqual("parent-inherited", child.GetProperty("sourceKind").GetString());
            Assert.AreEqual("Parent Mod", child.GetProperty("parentMod").GetProperty("name").GetString());
            Assert.AreEqual("[CP] Parent Mod", child.GetProperty("parentMod").GetProperty("relativePath").GetString());
            Assert.IsFalse(child.TryGetProperty("projectId", out _));
            Assert.IsFalse(child.TryGetProperty("fileId", out _));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModpackInstall_ShouldGroupFlattenedArchiveContentPacksUnderParent()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-flattened-composite-source-test-" + Guid.NewGuid().ToString("N"));
        var modsPath = Path.Combine(root, "Mods");
        var parentPath = Path.Combine(modsPath, "[] MarketTown");
        var childOnePath = Path.Combine(modsPath, "[CP] CloneNPC_RSV");
        var childTwoPath = Path.Combine(modsPath, "[CP] CloneNPC_SVE");
        try
        {
            foreach (var directory in new[] { parentPath, childOnePath, childTwoPath })
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                Path.Combine(parentPath, "manifest.json"),
                "{\"Name\":\"Market Town\",\"UniqueID\":\"d5a1lamdtd.MarketTown\",\"Version\":\"6.7.1\"}");
            File.WriteAllText(
                Path.Combine(childOnePath, "manifest.json"),
                "{\"Name\":\"MarketTown - Cloned NPC RSV\",\"UniqueID\":\"d5a1lamdtd.MarketTown.CloneNPC_RSV\",\"Version\":\"5.0.0\",\"ContentPackFor\":{\"UniqueID\":\"Pathoschild.ContentPatcher\"}}");
            File.WriteAllText(
                Path.Combine(childTwoPath, "manifest.json"),
                "{\"Name\":\"MarketTown - Cloned NPC SVE\",\"UniqueID\":\"d5a1lamdtd.MarketTown.CloneNPC_SVE\",\"Version\":\"5.0.0\",\"ContentPackFor\":{\"UniqueID\":\"Pathoschild.ContentPatcher\"}}");
            File.WriteAllText(
                Path.Combine(childOnePath, "svl-source.json"),
                "{\"platform\":\"Curseforge\",\"projectId\":\"994458\",\"fileId\":\"5276101\",\"hasUpdate\":true,\"latestVersion\":\"6.7.1\"}");
            File.WriteAllText(
                Path.Combine(childTwoPath, "svl-source.json"),
                "{\"platform\":\"Curseforge\",\"projectId\":\"994458\",\"fileId\":\"5276101\",\"hasUpdate\":true,\"latestVersion\":\"6.7.1\"}");

            var writer = typeof(ModpackInstallService).GetMethod(
                "WriteCurseforgeModpackSourceCredentials",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(writer);

            writer!.Invoke(
                null,
                [
                    modsPath,
                    new List<string> { "[] MarketTown", "[CP] CloneNPC_RSV", "[CP] CloneNPC_SVE" },
                    "Curseforge",
                    994458L,
                    5276101L
                ]);

            using var parentDocument = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(parentPath, "svl-source.json")));
            var parent = parentDocument.RootElement;
            Assert.AreEqual("modpack-entry", parent.GetProperty("sourceKind").GetString());
            Assert.IsTrue(parent.GetProperty("isParentMod").GetBoolean());
            Assert.AreEqual(2, parent.GetProperty("childMods").GetArrayLength());
            Assert.AreEqual("未检查", parent.GetProperty("updateStatus").GetString());

            foreach (var childPath in new[] { childOnePath, childTwoPath })
            {
                using var childDocument = JsonDocument.Parse(
                    File.ReadAllText(Path.Combine(childPath, "svl-source.json")));
                var child = childDocument.RootElement;
                Assert.AreEqual("parent-inherited", child.GetProperty("sourceKind").GetString());
                Assert.AreEqual("[] MarketTown", child.GetProperty("parentMod").GetProperty("relativePath").GetString());
                Assert.IsFalse(child.TryGetProperty("projectId", out _));
                Assert.IsFalse(child.TryGetProperty("fileId", out _));
                Assert.IsFalse(child.GetProperty("hasUpdate").GetBoolean());
                Assert.AreEqual("未检查", child.GetProperty("updateStatus").GetString());
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void DirectModDownload_ShouldUseTheSameNestedSourceCredentialTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-direct-nested-source-test-" + Guid.NewGuid().ToString("N"));
        var modsPath = Path.Combine(root, "Mods");
        var parentPath = Path.Combine(modsPath, "Parent Mod");
        var childPath = Path.Combine(modsPath, "Child Pack");
        try
        {
            Directory.CreateDirectory(parentPath);
            Directory.CreateDirectory(childPath);
            File.WriteAllText(
                Path.Combine(parentPath, "manifest.json"),
                "{\"Name\":\"Parent Mod\",\"UniqueID\":\"Example.Parent\",\"Version\":\"1.0.0\"}");
            File.WriteAllText(
                Path.Combine(childPath, "manifest.json"),
                "{\"Name\":\"Child Pack\",\"UniqueID\":\"Example.Child\",\"Version\":\"1.0.0\",\"ContentPackFor\":{\"UniqueID\":\"Pathoschild.ContentPatcher\"}}");

            var writer = typeof(DownloadInstallService).GetMethod(
                "WriteSourceCredentials",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(writer);

            writer!.Invoke(
                null,
                [
                    modsPath,
                    new List<string> { "Parent Mod", "Child Pack" },
                    "Curseforge",
                    994458L,
                    8390242L,
                    null,
                    "parent-pack.zip",
                    null
                ]);

            using var childDocument = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(childPath, "svl-source.json")));
            var child = childDocument.RootElement;
            Assert.AreEqual("parent-inherited", child.GetProperty("sourceKind").GetString());
            Assert.AreEqual("Parent Mod", child.GetProperty("parentMod").GetProperty("relativePath").GetString());
            Assert.IsFalse(child.TryGetProperty("projectId", out _));
            Assert.IsFalse(child.TryGetProperty("fileId", out _));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModpackSourceKind_ShouldTreatLegacyPerEntryAsActionable()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-modpack-entry-status-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(
                Path.Combine(root, "manifest.json"),
                "{\"Name\":\"Entry Mod\",\"UniqueID\":\"Example.Entry\",\"ContentPackFor\":{\"UniqueID\":\"Pathoschild.ContentPatcher\"}}");
            File.WriteAllText(
                Path.Combine(root, "svl-source.json"),
                "{\"platform\":\"Curseforge\",\"projectId\":\"994458\",\"fileId\":\"8390242\",\"sourceKind\":\"modpack\"}");

            var reader = typeof(VersionSettingsPageViewModel).GetMethod(
                "TryReadSourceCredential",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            var guard = typeof(VersionSettingsPageViewModel).GetMethod(
                "IsInheritedModpackSource",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(reader);
            Assert.IsNotNull(guard);

            var credential = reader!.Invoke(null, [root]);
            Assert.IsNotNull(credential);
            Assert.IsFalse((bool)guard!.Invoke(null, [root, credential])!);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void SvlModpackImport_ShouldResolveFlattenedSiblingChildReference()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-flattened-child-import-test-" + Guid.NewGuid().ToString("N"));
        var modsPath = Path.Combine(root, "Mods");
        var parentPath = Path.Combine(modsPath, "[] MarketTown");
        var childPath = Path.Combine(modsPath, "[CP] CloneNPC_RSV");
        try
        {
            Directory.CreateDirectory(parentPath);
            Directory.CreateDirectory(childPath);
            File.WriteAllText(
                Path.Combine(parentPath, "manifest.json"),
                "{\"Name\":\"Market Town\",\"UniqueID\":\"d5a1lamdtd.MarketTown\"}");
            File.WriteAllText(
                Path.Combine(childPath, "manifest.json"),
                "{\"Name\":\"MarketTown - Cloned NPC RSV\",\"UniqueID\":\"d5a1lamdtd.MarketTown.CloneNPC_RSV\",\"ContentPackFor\":{\"UniqueID\":\"Pathoschild.ContentPatcher\"}}");

            using var document = JsonDocument.Parse(
                "[{\"name\":\"Market Town\",\"directoryName\":\"[] MarketTown\",\"isParentMod\":true," +
                "\"childMods\":[{\"name\":\"MarketTown - Cloned NPC RSV\",\"uniqueId\":\"d5a1lamdtd.MarketTown.CloneNPC_RSV\",\"relativePath\":\"Old Parent/[CP] CloneNPC_RSV\"}]," +
                "\"source\":{\"platform\":\"Curseforge\",\"projectId\":994458,\"fileId\":8390242}}]");
            var writer = typeof(ModpackInstallService).GetMethod(
                "WriteSourceCredentials",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(writer);

            writer!.Invoke(
                null,
                [document.RootElement.EnumerateArray().Select(item => item.Clone()).ToList(), modsPath]);

            using var childDocument = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(childPath, "svl-source.json")));
            var child = childDocument.RootElement;
            Assert.AreEqual("parent-inherited", child.GetProperty("sourceKind").GetString());
            Assert.AreEqual("[] MarketTown", child.GetProperty("parentMod").GetProperty("relativePath").GetString());
            Assert.IsFalse(child.TryGetProperty("projectId", out _));
            Assert.IsFalse(child.TryGetProperty("fileId", out _));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ArchiveExtractor_DetectsSevenZipBySignatureWithoutExtension()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-7z-signature-test-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "cdn-download");
        var extractPath = Path.Combine(root, "extract");
        var manifestPath = Path.Combine(root, "manifest.json");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(
                manifestPath,
                "{\"Name\":\"Signature Mod\",\"UniqueID\":\"Example.Signature\"}");

            using (var writer = SevenZipWriter.OpenWriter(
                       archivePath,
                       new SevenZipWriterOptions(CompressionType.LZMA2)))
            using (var manifest = File.OpenRead(manifestPath))
            {
                writer.Write("SignatureMod/manifest.json", manifest, null);
            }

            Assert.IsTrue(ArchiveExtractor.IsSevenZip(archivePath));
            ArchiveExtractor.ExtractSevenZipToDirectory(archivePath, extractPath);
            Assert.IsTrue(File.Exists(Path.Combine(extractPath, "SignatureMod", "manifest.json")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ArchiveExtractor_DetectsZipBySignatureWithoutExtension()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-zip-signature-test-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "cdn-download");
        var extractPath = Path.Combine(root, "extract");
        try
        {
            Directory.CreateDirectory(root);
            using (var archive = System.IO.Compression.ZipFile.Open(
                       archivePath,
                       System.IO.Compression.ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("ContentPatcher/manifest.json").Open()))
            {
                writer.Write("{\"Name\":\"Signature ZIP Mod\",\"UniqueID\":\"Example.SignatureZip\"}");
            }

            Assert.IsTrue(ArchiveExtractor.IsZip(archivePath));
            Assert.IsTrue(ModpackTypeDetector.IsSupportedFile(archivePath));
            using (var archive = System.IO.Compression.ZipFile.OpenRead(archivePath))
            {
                Directory.CreateDirectory(extractPath);
                foreach (var entry in archive.Entries)
                {
                    var target = Path.Combine(extractPath, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var source = entry.Open();
                    using var destination = File.Create(target);
                    source.CopyTo(destination);
                }
            }

            Assert.IsTrue(File.Exists(Path.Combine(extractPath, "ContentPatcher", "manifest.json")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModpackInstall_ShouldRejectCorruptedManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-corrupt-manifest-test-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "corrupt.zip");
        var modsPath = Path.Combine(root, "Mods");
        try
        {
            Directory.CreateDirectory(root);
            using (var archive = System.IO.Compression.ZipFile.Open(
                       archivePath,
                       System.IO.Compression.ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(archive.CreateEntry("Release/ActualMod/manifest.json").Open());
                writer.Write("{not valid json");
            }

            var installer = typeof(ModpackInstallService).GetMethod(
                "InstallDownloadedModArchive",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(installer);

            var arguments = new object[] { archivePath, modsPath, "ActualMod", null };
            var success = (bool)installer.Invoke(null, arguments)!;

            Assert.IsFalse(success);
            Assert.IsFalse(Directory.Exists(Path.Combine(modsPath, "ActualMod")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModpackInstall_ShouldIgnoreNonModOuterManifestAndInstallInnerMod()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-invalid-outer-manifest-test-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "wrapped-mod.zip");
        var modsPath = Path.Combine(root, "Mods");
        try
        {
            Directory.CreateDirectory(root);
            using (var archive = System.IO.Compression.ZipFile.Open(
                       archivePath,
                       System.IO.Compression.ZipArchiveMode.Create))
            {
                WriteArchiveText(archive, "Release/manifest.json", "{\"formatVersion\":1,\"files\":[]}");
                WriteArchiveText(
                    archive,
                    "Release/ActualMod/manifest.json",
                    "{\"Name\":\"Actual Mod\",\"UniqueID\":\"Example.Actual\",\"Version\":\"1.0.0\"}");
                WriteArchiveText(archive, "Release/ActualMod/content.json", "{}");
            }

            var installer = typeof(ModpackInstallService).GetMethod(
                "InstallDownloadedModArchive",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(installer);

            var arguments = new object[] { archivePath, modsPath, "wrapped-mod", null };
            var success = (bool)installer.Invoke(null, arguments)!;
            var installedNames = arguments[3] as List<string>;

            Assert.IsTrue(success);
            Assert.IsNotNull(installedNames);
            CollectionAssert.AreEqual(new[] { "ActualMod" }, installedNames);
            Assert.IsTrue(File.Exists(Path.Combine(modsPath, "ActualMod", "manifest.json")));
            Assert.IsTrue(File.Exists(Path.Combine(modsPath, "ActualMod", "content.json")));
            Assert.IsFalse(Directory.Exists(Path.Combine(modsPath, "Release")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModpackInstall_ShouldIgnoreNameOnlyOuterManifestAndInstallInnerMod()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-name-only-outer-manifest-test-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "wrapped-mod.zip");
        var modsPath = Path.Combine(root, "Mods");
        try
        {
            Directory.CreateDirectory(root);
            using (var archive = System.IO.Compression.ZipFile.Open(
                       archivePath,
                       System.IO.Compression.ZipArchiveMode.Create))
            {
                // 一些 Nexus/CurseForge 发布包的外层清单只有 Name/Version，
                // 没有 formatVersion/files 等明显的整合包字段。
                WriteArchiveText(archive, "Release/manifest.json", "{\"Name\":\"Release Wrapper\",\"Version\":\"1.0.0\"}");
                WriteArchiveText(
                    archive,
                    "Release/ActualMod/manifest.json",
                    "{\"Name\":\"Actual Mod\",\"UniqueID\":\"Example.Actual\",\"Version\":\"1.0.0\"}");
                WriteArchiveText(archive, "Release/ActualMod/content.json", "{}");
            }

            var installer = typeof(ModpackInstallService).GetMethod(
                "InstallDownloadedModArchive",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(installer);

            var arguments = new object[] { archivePath, modsPath, "wrapped-mod", null };
            var success = (bool)installer.Invoke(null, arguments)!;
            var installedNames = arguments[3] as List<string>;

            Assert.IsTrue(success);
            Assert.IsNotNull(installedNames);
            CollectionAssert.AreEqual(new[] { "ActualMod" }, installedNames);
            Assert.IsTrue(File.Exists(Path.Combine(modsPath, "ActualMod", "manifest.json")));
            Assert.IsTrue(File.Exists(Path.Combine(modsPath, "ActualMod", "content.json")));
            Assert.IsFalse(Directory.Exists(Path.Combine(modsPath, "Release")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModpackInstall_ShouldReuseExistingDirectoryForMultiManifestArchive()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-multi-manifest-reuse-test-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "distant-lands.zip");
        var modsPath = Path.Combine(root, "Mods");
        var existingName = "AimonsWitchSwampOverhaulPatches";
        try
        {
            Directory.CreateDirectory(Path.Combine(modsPath, existingName));
            File.WriteAllText(
                Path.Combine(modsPath, existingName, "manifest.json"),
                "{\"Name\":\"AimonsWitchSwampOverhaulPatches\",\"UniqueID\":\"AimonsWitchSwampOverhaulPatches\",\"Version\":\"2.0.7\"}");

            Directory.CreateDirectory(root);
            using (var archive = System.IO.Compression.ZipFile.Open(
                       archivePath,
                       System.IO.Compression.ZipArchiveMode.Create))
            {
                WriteArchiveText(
                    archive,
                    "Distant Lands - Witch Swamp Overhaul/Distant Lands - Witch Swamp Overhaul/manifest.json",
                    "{\"Name\":\"AimonsWitchSwampOverhaulPatches\",\"UniqueID\":\"AimonsWitchSwampOverhaulPatches\",\"Version\":\"2.2.9\"}");
                WriteArchiveText(
                    archive,
                    "Distant Lands - Witch Swamp Overhaul/[CP] Distant Lands - Witch Swamp Overhaul/manifest.json",
                    "{\"Name\":\"Distant Lands - Witch Swamp Overhaul\",\"UniqueID\":\"Aimon111.WitchSwampOverhaulCP\",\"Version\":\"2.2.9\"}");
            }

            var installer = typeof(ModpackInstallService).GetMethod(
                "InstallDownloadedModArchive",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(installer);

            var arguments = new object[] { archivePath, modsPath, "cf-1010281-7942677", null };
            var success = (bool)installer.Invoke(null, arguments)!;
            var installedNames = arguments[3] as List<string>;

            Assert.IsTrue(success);
            Assert.IsNotNull(installedNames);
            CollectionAssert.Contains(installedNames, existingName);
            StringAssert.Contains(
                File.ReadAllText(Path.Combine(modsPath, existingName, "manifest.json")),
                "2.2.9");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModpackInstall_ShouldAcceptManifestWithCommentsAndTrailingComma()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-compatible-manifest-test-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "content-patcher.zip");
        var modsPath = Path.Combine(root, "Mods");
        try
        {
            Directory.CreateDirectory(root);
            using (var archive = System.IO.Compression.ZipFile.Open(
                       archivePath,
                       System.IO.Compression.ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(
                    archive.CreateEntry("Release/ContentPatcher/manifest.json").Open(),
                    new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                writer.Write("{\n  // SMAPI manifest files in older packages may contain comments.\n  \"Name\": \"Content Patcher\",\n  \"UniqueID\": \"Pathoschild.ContentPatcher\",\n}");
            }

            var installer = typeof(ModpackInstallService).GetMethod(
                "InstallDownloadedModArchive",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(installer);

            var arguments = new object[] { archivePath, modsPath, "cf-1012214-5312529", null };
            var success = (bool)installer.Invoke(null, arguments)!;
            var installedNames = arguments[3] as List<string>;

            Assert.IsTrue(success);
            Assert.IsNotNull(installedNames);
            Assert.AreEqual("ContentPatcher", installedNames.Single());
            Assert.IsTrue(File.Exists(Path.Combine(modsPath, "ContentPatcher", "manifest.json")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void VersionSettingsManifestReader_ShouldFallbackWhenVersionIsEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-manifest-version-fallback-" + Guid.NewGuid().ToString("N"));
        var modDirectory = Path.Combine(root, "Content Patcher 2.9.0");
        try
        {
            Directory.CreateDirectory(modDirectory);
            File.WriteAllText(
                Path.Combine(modDirectory, "manifest.json"),
                "{\"Name\":\"Content Patcher\",\"Version\":\"\"}");

            var reader = typeof(ModDetailsPageViewModel).GetMethod(
                "TryReadManifestVersion",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(reader);

            var version = reader.Invoke(null, [modDirectory]) as string;
            Assert.AreEqual("2.9.0", version);

            File.WriteAllText(
                Path.Combine(modDirectory, "manifest.json"),
                "{\"Name\":\"Content Patcher\",\"VersionString\":\"3.0.1-beta\"}");
            version = reader.Invoke(null, [modDirectory]) as string;
            Assert.AreEqual("3.0.1-beta", version);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModManifestDiscovery_ShouldIgnoreOuterPackManifestAndUseNestedModManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-nested-manifest-discovery-" + Guid.NewGuid().ToString("N"));
        var wrapperDirectory = Path.Combine(root, "Wrapper");
        var modDirectory = Path.Combine(wrapperDirectory, "Actual Mod");
        try
        {
            Directory.CreateDirectory(modDirectory);
            File.WriteAllText(
                Path.Combine(wrapperDirectory, "manifest.json"),
                "{\"name\":\"Exported Pack\",\"version\":\"1.0.0\"}");
            File.WriteAllText(
                Path.Combine(modDirectory, "manifest.json"),
                "{\"Name\":\"Actual Mod\",\"UniqueID\":\"Example.ActualMod\",\"Version\":\"1.2.3\"}");

            var findManifest = typeof(VersionSettingsPageViewModel).GetMethod(
                "FindManifestPath",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(findManifest);
            Assert.IsNull(findManifest.Invoke(null, [wrapperDirectory]));
            Assert.AreEqual(
                Path.Combine(modDirectory, "manifest.json"),
                findManifest.Invoke(null, [modDirectory]));

            var enumerateVersionSettings = typeof(VersionSettingsPageViewModel).GetMethod(
                "EnumerateCandidateModDirectories",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(enumerateVersionSettings);
            var versionSettingsCandidates = ((IEnumerable<string>)enumerateVersionSettings.Invoke(null, [root])!).ToList();
            CollectionAssert.Contains(versionSettingsCandidates, Path.GetFullPath(modDirectory));
            CollectionAssert.DoesNotContain(versionSettingsCandidates, Path.GetFullPath(wrapperDirectory));

            var enumerateDetails = typeof(ModDetailsPageViewModel).GetMethod(
                "EnumerateCandidateModDirectories",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(enumerateDetails);
            var detailsCandidates = ((IEnumerable<string>)enumerateDetails.Invoke(null, [root])!).ToList();
            CollectionAssert.Contains(detailsCandidates, Path.GetFullPath(modDirectory));
            CollectionAssert.DoesNotContain(detailsCandidates, Path.GetFullPath(wrapperDirectory));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModManifestDiscovery_ShouldRejectPackageShapedOuterManifestWithVersion()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-package-shaped-manifest-discovery-" + Guid.NewGuid().ToString("N"));
        var wrapperDirectory = Path.Combine(root, "Release");
        var modDirectory = Path.Combine(wrapperDirectory, "Actual Mod");
        try
        {
            Directory.CreateDirectory(modDirectory);
            File.WriteAllText(
                Path.Combine(wrapperDirectory, "manifest.json"),
                "{\"name\":\"Exported Pack\",\"version\":\"1.0.0\",\"formatVersion\":1}");
            File.WriteAllText(
                Path.Combine(modDirectory, "manifest.json"),
                "{\"Name\":\"Actual Mod\",\"UniqueID\":\"Example.ActualMod\",\"Version\":\"1.2.3\"}");

            foreach (var viewModelType in new[]
                     {
                         typeof(VersionSettingsPageViewModel),
                         typeof(ModDetailsPageViewModel)
                     })
            {
                var findManifest = viewModelType.GetMethod(
                    "FindManifestPath",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                Assert.IsNotNull(findManifest);
                Assert.IsNull(findManifest.Invoke(null, [wrapperDirectory]));

                var enumerateCandidates = viewModelType.GetMethod(
                    "EnumerateCandidateModDirectories",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                Assert.IsNotNull(enumerateCandidates);
                var candidates = ((IEnumerable<string>)enumerateCandidates.Invoke(null, [root])!).ToList();
                CollectionAssert.Contains(candidates, Path.GetFullPath(modDirectory));
                CollectionAssert.DoesNotContain(candidates, Path.GetFullPath(wrapperDirectory));
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModpackInstallProgress_ShouldKeepFailedDownloadStageBelowFull()
    {
        var calculate = typeof(ModpackInstallService).GetMethod(
            "CalculateFinalSubProgress",
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Public);
        Assert.IsNotNull(calculate);

        Assert.AreEqual(99, calculate.Invoke(null, [2, 1]));
        Assert.AreEqual(100, calculate.Invoke(null, [2, 0]));
        Assert.AreEqual(-1, calculate.Invoke(null, [0, 1]));
    }

    [TestMethod]
    public void ExportSourceCredential_ShouldReadNumericProjectAndFileIds()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-numeric-source-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(
                Path.Combine(root, "svl-source.json"),
                "{\"platform\":\"Curseforge\",\"projectId\":1012214,\"fileId\":5312529,\"fileName\":\"Content Patcher.zip\"}");

            var reader = typeof(VersionSettingsPageViewModel).GetMethod(
                "TryReadSvlSourceMetadata",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(reader);
            var metadata = reader.Invoke(null, [root]);
            Assert.IsNotNull(metadata);

            var metadataType = metadata.GetType();
            Assert.AreEqual("Curseforge", metadataType.GetProperty("Platform")?.GetValue(metadata));
            Assert.AreEqual("1012214", metadataType.GetProperty("ProjectId")?.GetValue(metadata));
            Assert.AreEqual("5312529", metadataType.GetProperty("FileId")?.GetValue(metadata));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModpackTypeDetector_CleansNestedSevenZipPackageWithOuterTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-nested-7z-detection-test-" + Guid.NewGuid().ToString("N"));
        var innerArchivePath = Path.Combine(root, "modpack.7z");
        var outerArchivePath = Path.Combine(root, "launcher-package.zip");
        try
        {
            Directory.CreateDirectory(root);
            var manifestPath = Path.Combine(root, "modpack.json");
            var iconPath = Path.Combine(root, "icon.png");
            File.WriteAllText(manifestPath, "{\"name\":\"嵌套 7z 整合包\",\"mods\":[]}");
            File.WriteAllBytes(iconPath, [1, 2, 3, 4]);

            using (var writer = SevenZipWriter.OpenWriter(
                       innerArchivePath,
                       new SevenZipWriterOptions(CompressionType.LZMA2)))
            using (var manifest = File.OpenRead(manifestPath))
            using (var icon = File.OpenRead(iconPath))
            {
                writer.Write("modpack.json", manifest, null);
                writer.Write("icon.png", icon, null);
            }

            using (var outerArchive = System.IO.Compression.ZipFile.Open(
                       outerArchivePath,
                       System.IO.Compression.ZipArchiveMode.Create))
            using (var inner = File.OpenRead(innerArchivePath))
            using (var output = outerArchive.CreateEntry("modpack.7z").Open())
            {
                inner.CopyTo(output);
            }

            var detection = ModpackTypeDetector.Detect(outerArchivePath);
            try
            {
                Assert.AreEqual(ModpackType.SVL, detection.Type);
                Assert.AreEqual("嵌套 7z 整合包", detection.ModpackName);
                Assert.IsTrue(File.Exists(detection.ModpackIconPath));
                Assert.IsTrue(detection.ModpackIconPath.StartsWith(
                    detection.TempExtractPath,
                    StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                ModpackTypeDetector.CleanupTempDirectory(detection.TempExtractPath);
            }

            Assert.IsFalse(Directory.Exists(detection.TempExtractPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task SvlModpackInstall_ShouldInstallSevenZipPackageAndFlattenBundledMod()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-7z-install-test-" + Guid.NewGuid().ToString("N"));
        var gamePath = Path.Combine(root, "game");
        var archivePath = Path.Combine(root, "pack.7z");
        const string instanceName = "7z Import";
        var registry = new InstanceRegistryStore();

        try
        {
            Directory.CreateDirectory(Path.Combine(gamePath, "versions", "Existing SMAPI 4.5.2"));
            File.WriteAllText(
                Path.Combine(gamePath, "versions", "Existing SMAPI 4.5.2", "StardewModdingAPI.dll"),
                "existing-smapi");

            using (var writer = SevenZipWriter.OpenWriter(
                       archivePath,
                       new SevenZipWriterOptions(CompressionType.LZMA2)))
            {
                // 模拟真实导出包常见的外层目录 + 多层发行包目录布局。
                WriteSevenZipText(
                    writer,
                    "SVL Pack/modpack.json",
                    "{\"name\":\"7z Pack\",\"version\":\"1.0.0\",\"smapi_version\":\"4.5.2\",\"mods\":[]}");
                WriteSevenZipText(
                    writer,
                    "SVL Pack/mods/Release/ActualMod/manifest.json",
                    "{\"Name\":\"7z Bundled Mod\",\"UniqueID\":\"SVL.SevenZipMod\",\"Version\":\"1.0.0\"}");
                WriteSevenZipText(writer, "SVL Pack/mods/Release/ActualMod/content.json", "{}");
            }

            var settingsStore = new AppUserSettingsStore(Path.Combine(root, "settings"));
            var service = new ModpackInstallService(
                new RoundTripGameInstallPathLocator(gamePath),
                new RoundTripSmapiInstallService(),
                new HttpDownloadService(settingsStore),
                new RemoteCatalogService(settingsStore),
                settingsStore,
                new NexusModDownloadResolverService(),
                new SVL.Core.Platform.Abstractions.NxmLinkParser());

            var result = await service.InstallSvlModpackAsync(
                archivePath,
                instanceName,
                gamePath,
                onProgress: null);

            var installedModPath = Path.Combine(
                gamePath,
                "versions",
                instanceName,
                "Mods",
                "ActualMod");
            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual(1, result.InstalledMods.Count);
            Assert.AreEqual(0, result.FailedMods.Count);
            Assert.IsTrue(File.Exists(Path.Combine(installedModPath, "manifest.json")));
            Assert.IsTrue(File.Exists(Path.Combine(installedModPath, "content.json")));
            Assert.IsFalse(
                Directory.Exists(Path.Combine(
                    gamePath,
                    "versions",
                    instanceName,
                    "Mods",
                    "Release")),
                "7z 导入不应把外部发行包目录保留在 Mods 下");
        }
        finally
        {
            var records = registry.LoadManualInstances();
            records.RemoveAll(record =>
                string.Equals(record.Name, instanceName, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(record.Path) &&
                record.Path.StartsWith(gamePath, StringComparison.OrdinalIgnoreCase));
            registry.SaveManualInstances(records);

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModpackTypeDetector_RecognizesUtf16CurseforgeManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-utf16-cf-detection-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "pack.zip");
        try
        {
            Directory.CreateDirectory(root);
            using (var archive = System.IO.Compression.ZipFile.Open(
                       archivePath,
                       System.IO.Compression.ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("manifest.json");
                using var stream = entry.Open();
                using var writer = new StreamWriter(stream, System.Text.Encoding.Unicode);
                writer.Write("{\"manifestVersion\":1,\"minecraft\":{\"version\":\"1.0\"},\"files\":[]}");
            }

            var detection = ModpackTypeDetector.Detect(archivePath);
            try
            {
                Assert.AreEqual(ModpackType.Curseforge, detection.Type);
            }
            finally
            {
                ModpackTypeDetector.CleanupTempDirectory(detection.TempExtractPath);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModpackTypeDetector_RecognizesCurseforgeManifestWithNullFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-null-files-cf-detection-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "overrides-only.zip");
        try
        {
            Directory.CreateDirectory(root);
            using (var archive = System.IO.Compression.ZipFile.Open(
                       archivePath,
                       System.IO.Compression.ZipArchiveMode.Create))
            {
                WriteArchiveText(
                    archive,
                    "manifest.json",
                    "{\"name\":\"仅 overrides 包\",\"version\":\"1.0.0\",\"manifestVersion\":1,\"files\":null,\"overrides\":\"overrides\"}");
            }

            var detection = ModpackTypeDetector.Detect(archivePath);
            try
            {
                Assert.AreEqual(ModpackType.Curseforge, detection.Type);
                Assert.AreEqual("仅 overrides 包", detection.ModpackName);
                Assert.AreEqual(0, detection.ModCount);
                Assert.IsNotNull(detection.CurseforgeManifest);
                Assert.AreEqual(0, detection.CurseforgeManifest!.Files.Count);
            }
            finally
            {
                ModpackTypeDetector.CleanupTempDirectory(detection.TempExtractPath);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModpackTypeDetector_ShouldSkipNonModOuterManifestAndUseInnerCurseforgeManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-inner-cf-manifest-test-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "wrapped-pack.zip");
        try
        {
            Directory.CreateDirectory(root);
            using (var archive = System.IO.Compression.ZipFile.Open(
                       archivePath,
                       System.IO.Compression.ZipArchiveMode.Create))
            {
                // 这是合法 JSON，但属于外层发布工具的元数据，不是 CurseForge manifest。
                WriteArchiveText(archive, "Release/manifest.json", "{\"formatVersion\":1,\"files\":[]}");
                WriteArchiveText(
                    archive,
                    "Release/ActualPack/manifest.json",
                    "{\"name\":\"内层 CurseForge 包\",\"version\":\"1.0.0\",\"manifestVersion\":1,\"files\":[{\"projectID\":1012214,\"fileID\":5312529}]}");
            }

            var detection = ModpackTypeDetector.Detect(archivePath);
            try
            {
                Assert.AreEqual(ModpackType.Curseforge, detection.Type);
                Assert.AreEqual("内层 CurseForge 包", detection.ModpackName);
                Assert.IsNotNull(detection.CurseforgeManifest);
                Assert.AreEqual(1, detection.CurseforgeManifest!.Files.Count);
                Assert.AreEqual(5312529L, detection.CurseforgeManifest.Files[0].FileId);
            }
            finally
            {
                ModpackTypeDetector.CleanupTempDirectory(detection.TempExtractPath);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModpackTypeDetector_ShouldSkipNonCollectionOuterJsonAndUseInnerCollection()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-inner-collection-manifest-test-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "wrapped-collection.7z");
        try
        {
            Directory.CreateDirectory(root);
            using (var writer = SevenZipWriter.OpenWriter(
                       archivePath,
                       new SevenZipWriterOptions(CompressionType.LZMA2)))
            {
                WriteSevenZipText(writer, "Release/collection.json", "{\"formatVersion\":1,\"files\":[]}");
                WriteSevenZipText(
                    writer,
                    "Release/ActualCollection/collection.json",
                    "{\"info\":{\"name\":\"内层 Collection\"},\"mods\":[]}");
            }

            var detection = ModpackTypeDetector.Detect(archivePath);
            try
            {
                Assert.AreEqual(ModpackType.NexusCollection, detection.Type);
                Assert.AreEqual("内层 Collection", detection.ModpackName);
                Assert.AreEqual(0, detection.ModCount);
            }
            finally
            {
                ModpackTypeDetector.CleanupTempDirectory(detection.TempExtractPath);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void CurseforgeManifestParser_ShouldAcceptStringManifestVersion()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-string-cf-manifest-version-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "pack.zip");
        try
        {
            Directory.CreateDirectory(root);
            using (var archive = System.IO.Compression.ZipFile.Open(
                       archivePath,
                       System.IO.Compression.ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("manifest.json");
                using var stream = entry.Open();
                using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8);
                writer.Write("{\"name\":\"字符串版本包\",\"version\":\"1.0\",\"manifestVersion\":\"1\",\"files\":[{\"projectID\":\"1012214\",\"fileID\":\"5312529\"}]}");
            }

            var detection = ModpackTypeDetector.Detect(archivePath);
            try
            {
                Assert.AreEqual(ModpackType.Curseforge, detection.Type);
                Assert.IsNotNull(detection.CurseforgeManifest);
                Assert.AreEqual(1, detection.CurseforgeManifest!.ManifestVersion);
                Assert.AreEqual(1, detection.CurseforgeManifest.Files.Count);
            }
            finally
            {
                ModpackTypeDetector.CleanupTempDirectory(detection.TempExtractPath);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void SvlSourceParser_ShouldAcceptWrappedAndSingleSourceObjects()
    {
        var parser = typeof(ModpackInstallService).GetMethod(
            "ParseSourceEntries",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        using (var aliases = JsonDocument.Parse(
                   "{\"sources\":{\"Content Patcher\":{\"site\":\"CurseForge\",\"project\":1012214,\"file\":5312529}}}"))
        {
            var entries = (IReadOnlyList<JsonElement>)parser!.Invoke(null, [aliases.RootElement])!;
            Assert.AreEqual(1, entries.Count);
            Assert.AreEqual("Content Patcher", entries[0].GetProperty("name").GetString());
            Assert.AreEqual(5312529, entries[0].GetProperty("file").GetInt32());
        }

        using (var wrapped = JsonDocument.Parse(
                   "{\"mods\":[{\"name\":\"Content Patcher\",\"source\":{\"platform\":\"Curseforge\",\"projectId\":\"1\",\"fileId\":\"2\"}}]}"))
        {
            var entries = (IReadOnlyList<JsonElement>)parser!.Invoke(null, [wrapped.RootElement])!;
            Assert.AreEqual(1, entries.Count);
            Assert.AreEqual("Content Patcher", entries[0].GetProperty("name").GetString());
        }

        using (var single = JsonDocument.Parse(
                   "{\"platform\":\"NexusMods\",\"projectId\":\"10\",\"fileId\":\"20\"}"))
        {
            var entries = (IReadOnlyList<JsonElement>)parser!.Invoke(null, [single.RootElement])!;
            Assert.AreEqual(1, entries.Count);
            Assert.AreEqual("NexusMods", entries[0].GetProperty("platform").GetString());
        }

        using (var map = JsonDocument.Parse(
                   "{\"sources\":{\"Content Patcher\":{\"platform\":\"Curseforge\",\"projectId\":\"1\",\"fileId\":\"2\"},\"Generic Mod\":\"https://example.test/generic.zip\"}}"))
        {
            var entries = (IReadOnlyList<JsonElement>)parser!.Invoke(null, [map.RootElement])!;
            Assert.AreEqual(2, entries.Count);
            Assert.AreEqual("Content Patcher", entries[0].GetProperty("name").GetString());
            Assert.AreEqual("Generic Mod", entries[1].GetProperty("name").GetString());
            Assert.AreEqual("https://example.test/generic.zip", entries[1].GetProperty("source").GetString());
        }
    }

    [TestMethod]
    public void DeferredVersionDirectoryCleanup_ShouldOnlyRemoveLauncherTempDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-deferred-version-cleanup-test-" + Guid.NewGuid().ToString("N"));
        var versions = Path.Combine(root, "versions");
        var deferred = Path.Combine(versions, ".svl-delete-old-instance");
        var userDirectory = Path.Combine(versions, "keep-instance");
        try
        {
            Directory.CreateDirectory(deferred);
            Directory.CreateDirectory(userDirectory);
            File.WriteAllText(Path.Combine(deferred, "marker.txt"), "remove");
            File.WriteAllText(Path.Combine(userDirectory, "marker.txt"), "keep");

            var cleaned = DeferredVersionDirectoryCleanup.TryCleanup(root);

            Assert.AreEqual(1, cleaned);
            Assert.IsFalse(Directory.Exists(deferred));
            Assert.IsTrue(File.Exists(Path.Combine(userDirectory, "marker.txt")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModpackTypeDetector_ShouldReadCollectionVersionWhenGameVersionsIsString()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-string-collection-version-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "collection.zip");
        try
        {
            Directory.CreateDirectory(root);
            using (var archive = System.IO.Compression.ZipFile.Open(
                       archivePath,
                       System.IO.Compression.ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("collection.json");
                using var stream = entry.Open();
                using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8);
                writer.Write("{\"info\":{\"name\":\"字符串版本 Collection\",\"gameVersions\":\"1.6.15\"},\"mods\":[]}");
            }

            var detection = ModpackTypeDetector.Detect(archivePath);
            try
            {
                Assert.AreEqual(ModpackType.NexusCollection, detection.Type);
                Assert.AreEqual("字符串版本 Collection", detection.ModpackName);
                Assert.AreEqual("1.6.15", detection.ModpackVersion);
            }
            finally
            {
                ModpackTypeDetector.CleanupTempDirectory(detection.TempExtractPath);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void DownloadCatalogItem_ShouldKeepStructuredIdentityForDetails()
    {
        var parser = typeof(DownloadPageViewModel).GetMethod(
            "ParseCatalogItem",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        var item = parser!.Invoke(
            null,
            ["[CurseforgePack#12345] 测试整合包 | slug=test-pack | metric=10"])
            as DownloadCatalogItem;

        Assert.IsNotNull(item);
        Assert.AreEqual(12345L, item!.Identity.ResourceId);
        Assert.AreEqual(CatalogSource.Curseforge, item.Identity.Source);
        Assert.IsTrue(item.Identity.IsModpack);
        Assert.AreEqual("test-pack", item.Identity.CollectionSlug);
    }

    [TestMethod]
    public void DownloadPageSteamCmdInput_ShouldBindToGeneratedCommand()
    {
        Assert.IsNotNull(typeof(DownloadPageViewModel).GetProperty("SendSteamCmdInputCommand"));
        Assert.IsNull(typeof(DownloadPageViewModel).GetProperty("SteamCmdInputCommand"));

        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace != null &&
               !File.Exists(Path.Combine(workspace.FullName, "SVL.Avalonia", "SVL.Avalonia.csproj")))
        {
            workspace = workspace.Parent;
        }

        Assert.IsNotNull(workspace, "无法定位工作区根目录");
        var viewPath = Path.Combine(workspace!.FullName, "SVL.Avalonia", "Views", "DownloadPageView.axaml");
        Assert.IsTrue(File.Exists(viewPath), $"找不到视图文件: {viewPath}");
        var viewText = File.ReadAllText(viewPath);
        StringAssert.Contains(viewText, "Command=\"{Binding SendSteamCmdInputCommand}\"");
        Assert.IsFalse(viewText.Contains("Command=\"{Binding SteamCmdInputCommand}\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AvaloniaChromeAndContextMenu_ShouldUseStableLayoutAndThemeResources()
    {
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace != null &&
               !File.Exists(Path.Combine(workspace.FullName, "SVL.Avalonia", "SVL.Avalonia.csproj")))
        {
            workspace = workspace.Parent;
        }

        Assert.IsNotNull(workspace, "无法定位工作区根目录");
        var root = workspace!.FullName;
        var mainWindowText = File.ReadAllText(Path.Combine(root, "SVL.Avalonia", "MainWindow.axaml"));
        var themeText = File.ReadAllText(Path.Combine(root, "SVL.Avalonia", "Resources", "Theme.axaml"));
        var instancesViewText = File.ReadAllText(Path.Combine(root, "SVL.Avalonia", "Views", "InstancesPageView.axaml"));
        var instancesCodeText = File.ReadAllText(Path.Combine(root, "SVL.Avalonia", "Views", "InstancesPageView.axaml.cs"));

        StringAssert.Contains(mainWindowText, "UseLayoutRounding=\"True\"");
        StringAssert.Contains(mainWindowText, "Width=\"120\" Height=\"48\" RowDefinitions=\"48\" ColumnDefinitions=\"40,40,40\"");
        Assert.AreEqual(3, CountOccurrences(mainWindowText, "Classes=\"winCtrl"));
         // 窗口控制区仍使用统一尺寸的矢量画布；具体图形恢复为原有
         // Icons.axaml 资源，避免生成式 PNG 透明边界再次影响视觉中心。
         Assert.AreEqual(3, CountOccurrences(mainWindowText, "Width=\"24\" Height=\"24\""));
         Assert.AreEqual(3, CountOccurrences(mainWindowText, "Grid.Row=\"0\" Grid.Column="));
         StringAssert.Contains(mainWindowText, "Data=\"{StaticResource Icon.Minus}\"");
         StringAssert.Contains(mainWindowText, "Data=\"{StaticResource Icon.Maximize}\"");
         StringAssert.Contains(mainWindowText, "Data=\"{StaticResource Icon.X}\"");
         var generatedIconReferences = Directory
             .EnumerateFiles(Path.Combine(root, "SVL.Avalonia"), "*.axaml", SearchOption.AllDirectories)
             .Where(path => !path.Contains("bin", StringComparison.OrdinalIgnoreCase) &&
                            !path.Contains("obj", StringComparison.OrdinalIgnoreCase))
             .SelectMany(File.ReadLines)
             .Where(line => line.Contains("Assets/Icons/Generated", StringComparison.Ordinal));
         Assert.IsFalse(generatedIconReferences.Any(), "界面不应重新引用已撤回的生成式 PNG 图标");
         StringAssert.Contains(themeText, "<Style Selector=\"ContextMenu\">");
        StringAssert.Contains(themeText, "<Style Selector=\"MenuFlyoutPresenter\">");
        StringAssert.Contains(themeText, "<Style Selector=\"ContextMenu MenuItem:pointerover\">");
        StringAssert.Contains(instancesViewText, "PointerPressed=\"PathEntry_PointerPressed\"");
        StringAssert.Contains(instancesViewText, "Opening=\"PathContextMenu_Opening\"");
        StringAssert.Contains(instancesCodeText, "menu.Open(control)");
        StringAssert.Contains(instancesCodeText, "ResolvePathEntryFromMenuSender");
    }

    [TestMethod]
    public void ModpackSourceFailureRetry_ShouldRetryTransientFailuresOnly()
    {
        var method = typeof(ModpackInstallService).GetMethod(
            "ShouldRetryModSourceFailure",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(method);

        static bool Invoke(System.Reflection.MethodInfo method, string message) =>
            (bool)method.Invoke(null, [message])!;

        Assert.IsTrue(Invoke(method!, null!));
        Assert.IsTrue(Invoke(method!, "Nexus 文件下载或 Mod 解压校验失败"));
        Assert.IsTrue(Invoke(method!, "CurseForge 文件下载地址解析失败"));
        Assert.IsFalse(Invoke(method!, "缺少可用下载来源（请补充平台、项目 ID/FileID 或直链）"));
        Assert.IsFalse(Invoke(method!, "未收到有效的 Nexus NXM 文件回调"));
        Assert.IsFalse(Invoke(method!, "Nexus 下载地址刷新失败，未收到浏览器 NXM 回调"));
        Assert.IsFalse(Invoke(method!, "需要登录 Nexus 后才能下载"));
        Assert.IsFalse(Invoke(method!, "已取消"));
    }

    private static int CountOccurrences(string text, string value)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(value))
        {
            return 0;
        }

        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    [TestMethod]
    public async Task ExportedSvlModpack_ShouldRoundTripThroughImport()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-export-roundtrip-test-" + Guid.NewGuid().ToString("N"));
        var sourceInstance = Path.Combine(root, "source-instance");
        var sourceMod = Path.Combine(sourceInstance, "Mods", "ActualMod");
        var outputPath = Path.Combine(root, "Round Trip Pack.zip");
        var modArchivePath = Path.Combine(root, "cached-mod.zip");
        var targetBase = Path.Combine(root, "target-base");
        const long projectId = 987654321;
        const long fileId = 123456789;
        var cachePath = NexusDownloadCache.GetCachePath(projectId, fileId);
        var registry = new InstanceRegistryStore();
        byte[] existingCache = null;

        try
        {
            Directory.CreateDirectory(Path.Combine(sourceMod, "config"));
            File.WriteAllText(
                Path.Combine(sourceMod, "manifest.json"),
                "{\"Name\":\"Actual Mod\",\"UniqueID\":\"Example.ActualMod\",\"Version\":\"1.2.3\"}");
            File.WriteAllText(Path.Combine(sourceMod, "config", "config.json"), "{\"enabled\":true}");
            File.WriteAllBytes(Path.Combine(sourceInstance, ".svl-instance-icon-smapi.png"), [7, 8, 9, 10]);

            using (var archive = System.IO.Compression.ZipFile.Open(
                       modArchivePath,
                       System.IO.Compression.ZipArchiveMode.Create))
            {
                WriteArchiveText(
                    archive,
                    "Release/ActualMod/manifest.json",
                    "{\"Name\":\"Actual Mod\",\"UniqueID\":\"Example.ActualMod\",\"Version\":\"1.2.3\"}");
                WriteArchiveText(archive, "Release/ActualMod/content.json", "{}");
            }

            if (File.Exists(cachePath))
            {
                existingCache = File.ReadAllBytes(cachePath);
            }
            NexusDownloadCache.Save(projectId, fileId, modArchivePath);

            // 直接调用导出页的实际打包方法，避免把“导出包格式”另写一套测试实现。
            var viewModel = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                typeof(VersionSettingsPageViewModel));
            SetPrivateField(viewModel, "_modpackName", "Round Trip Pack");
            SetPrivateField(viewModel, "_modpackVersion", "1.0.0");
            SetPrivateField(viewModel, "_modpackAuthor", "SVL Test");
            SetPrivateField(viewModel, "_smapiVersionText", "4.5.2");
            SetPrivateField(viewModel, "_includeMods", true);
            SetPrivateField(viewModel, "_includeModSettings", true);
            SetPrivateField(viewModel, "_includeSvlLauncher", false);
            SetPrivateField(viewModel, "_isSmapiInstance", true);

            var itemType = typeof(VersionSettingsPageViewModel).Assembly.GetType(
                "SVL.Avalonia.ViewModels.ExportModPackageItem");
            Assert.IsNotNull(itemType);
            var item = Activator.CreateInstance(itemType!, nonPublic: true);
            Assert.IsNotNull(item);
            SetProperty(item!, "Name", "Actual Mod");
            SetProperty(item!, "UniqueId", "Example.ActualMod");
            SetProperty(item!, "Version", "1.2.3");
            SetProperty(item!, "Author", "SVL Test");
            SetProperty(item!, "ModPath", sourceMod);
            SetProperty(item!, "DirectoryName", "ActualMod");
            SetProperty(item!, "SourcePlatform", "NexusMods");
            SetProperty(item!, "SourceProjectId", projectId.ToString());
            SetProperty(item!, "SourceFileId", fileId.ToString());
            SetProperty(item!, "SourceFileName", "File 123456789_ Actual Mod 1.2.3.zip");
            SetProperty(item!, "SourceDownloadUrl", $"nxm://stardewvalley/mods/{projectId}/files/{fileId}");

            var itemListType = typeof(List<>).MakeGenericType(itemType!);
            var itemList = Activator.CreateInstance(itemListType)!;
            itemListType.GetMethod("Add")!.Invoke(itemList, [item]);

            var exportMethod = typeof(VersionSettingsPageViewModel).GetMethod(
                "BuildVersionSettingsExportPackage",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(exportMethod);
            exportMethod!.Invoke(viewModel, [outputPath, sourceInstance, itemList]);

            Assert.IsTrue(File.Exists(outputPath));
            using (var exported = System.IO.Compression.ZipFile.OpenRead(outputPath))
            {
                Assert.IsNotNull(exported.GetEntry("modpack.json"));
                Assert.IsNotNull(exported.GetEntry("sources.json"));
                Assert.IsNotNull(exported.GetEntry("icon.png"));
                Assert.IsNotNull(exported.GetEntry("settings/Mods/ActualMod/config/config.json"));

                using var sourcesReader = new StreamReader(exported.GetEntry("sources.json")!.Open());
                var sourcesJson = await sourcesReader.ReadToEndAsync();
                StringAssert.Contains(sourcesJson, projectId.ToString());
                StringAssert.Contains(sourcesJson, fileId.ToString());
                StringAssert.Contains(sourcesJson, "File 123456789_");
                StringAssert.Contains(sourcesJson, "nxm://stardewvalley");
            }

            // 模拟导出包里混入了没有 manifest.json 的内置目录：它不能被静默
            // 当作“已安装”，而应出现在导入结果的失败列表中。
            using (var exported = System.IO.Compression.ZipFile.Open(
                       outputPath,
                       System.IO.Compression.ZipArchiveMode.Update))
            {
                WriteArchiveText(exported, "mods/BrokenBundledMod/readme.txt", "missing manifest");
            }

            Directory.CreateDirectory(Path.Combine(targetBase, "versions", "SMAPI 4.5.2"));
            File.WriteAllText(
                Path.Combine(targetBase, "versions", "SMAPI 4.5.2", "StardewModdingAPI.dll"),
                "existing-smapi");

            var settingsStore = new AppUserSettingsStore(Path.Combine(root, "settings"));
            var installService = new ModpackInstallService(
                new RoundTripGameInstallPathLocator(targetBase),
                new RoundTripSmapiInstallService(),
                new HttpDownloadService(settingsStore),
                new RemoteCatalogService(settingsStore),
                settingsStore,
                new NexusModDownloadResolverService(),
                new SVL.Core.Platform.Abstractions.NxmLinkParser());

            var result = await installService.InstallSvlModpackAsync(
                outputPath,
                "Imported Round Trip",
                targetBase,
                onProgress: null);

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual(1, result.InstalledMods.Count);
            Assert.AreEqual(1, result.FailedMods.Count);
            StringAssert.Contains(result.FailedMods[0], "BrokenBundledMod");
            var importedRoot = Path.Combine(targetBase, "versions", "Imported Round Trip");
            Assert.IsTrue(File.Exists(Path.Combine(importedRoot, "Mods", "ActualMod", "manifest.json")));
            Assert.IsTrue(File.Exists(Path.Combine(importedRoot, "Mods", "ActualMod", "content.json")));
            Assert.AreEqual(
                "{\"enabled\":true}",
                File.ReadAllText(Path.Combine(importedRoot, "Mods", "ActualMod", "config", "config.json")));
            Assert.IsFalse(Directory.Exists(Path.Combine(importedRoot, "Mods", "Actual Mod")));
            CollectionAssert.AreEqual(
                new byte[] { 7, 8, 9, 10 },
                File.ReadAllBytes(Path.Combine(importedRoot, ".svl-instance-icon-smapi.png")));
            Assert.IsTrue(File.Exists(Path.Combine(importedRoot, "Mods", "ActualMod", "svl-source.json")));
            StringAssert.Contains(
                File.ReadAllText(Path.Combine(importedRoot, "Mods", "ActualMod", "svl-source.json")),
                fileId.ToString());
            StringAssert.Contains(
                File.ReadAllText(Path.Combine(importedRoot, "Mods", "ActualMod", "svl-source.json")),
                "File 123456789_");

            // 第二次导入使用同一 Nexus ModID/FileID。它必须直接复用稳定缓存，
            // 不重新打开文件页或等待浏览器回调；同时仍要保留整合包的失败项报告。
            var secondResult = await installService.InstallSvlModpackAsync(
                outputPath,
                "Imported Round Trip Again",
                targetBase,
                onProgress: null);

            Assert.IsTrue(secondResult.IsSuccess, secondResult.Message);
            Assert.AreEqual(1, secondResult.InstalledMods.Count);
            Assert.AreEqual(1, secondResult.FailedMods.Count);
            Assert.IsTrue(File.Exists(Path.Combine(
                targetBase,
                "versions",
                "Imported Round Trip Again",
                "Mods",
                "ActualMod",
                "manifest.json")));
        }
        finally
        {
            if (existingCache == null)
            {
                try
                {
                    if (File.Exists(cachePath))
                    {
                        File.Delete(cachePath);
                    }
                }
                catch { }
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                File.WriteAllBytes(cachePath, existingCache);
            }

            // 导入服务会把实例写入真实的 Avalonia 注册表；回归测试使用临时 Base，
            // 清理时只移除本测试创建的那条记录，不能把用户的同名实例一并删除。
            var records = registry.LoadManualInstances();
            records.RemoveAll(record =>
                string.Equals(record.Name, "Imported Round Trip", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(record.Path) &&
                record.Path.StartsWith(targetBase, StringComparison.OrdinalIgnoreCase));
            registry.SaveManualInstances(records);

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task SvlModpackInstall_ShouldRejectMissingManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-missing-modpack-manifest-test-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "not-svl-pack.zip");
        var gamePath = Path.Combine(root, "game");
        try
        {
            Directory.CreateDirectory(gamePath);
            using (var archive = System.IO.Compression.ZipFile.Open(
                       archivePath,
                       System.IO.Compression.ZipArchiveMode.Create))
            {
                WriteArchiveText(archive, "readme.txt", "not an SVL modpack");
            }

            var settingsStore = new AppUserSettingsStore(Path.Combine(root, "settings"));
            var service = new ModpackInstallService(
                new RoundTripGameInstallPathLocator(gamePath),
                new RoundTripSmapiInstallService(),
                new HttpDownloadService(settingsStore),
                new RemoteCatalogService(settingsStore),
                settingsStore,
                new NexusModDownloadResolverService(),
                new SVL.Core.Platform.Abstractions.NxmLinkParser());

            var result = await service.InstallSvlModpackAsync(
                archivePath,
                "Missing Manifest",
                gamePath,
                onProgress: null);

            Assert.IsFalse(result.IsSuccess);
            StringAssert.Contains(result.Message, "modpack.json");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ConfigurationStores_ShouldReplaceFilesAtomically()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "svl-atomic-store-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settingsStore = new AppUserSettingsStore(Path.Combine(root, "settings"));
            settingsStore.Save(new AppUserSettings
            {
                LauncherTitle = "第一次保存",
                ThemeMode = "深色"
            });
            settingsStore.Save(new AppUserSettings
            {
                LauncherTitle = "第二次保存",
                ThemeMode = "浅色",
                PreferredInstancePath = "D:\\Applications\\Stardew Valley"
            });

            var loadedSettings = settingsStore.Load();
            Assert.AreEqual("第二次保存", loadedSettings.LauncherTitle);
            Assert.AreEqual("浅色", loadedSettings.ThemeMode);
            Assert.AreEqual(
                "D:\\Applications\\Stardew Valley",
                loadedSettings.PreferredInstancePath);

            var registryStore = new InstanceRegistryStore(Path.Combine(root, "instances"));
            registryStore.SaveManualInstances(
            [
                new ManualInstanceRecord
                {
                    Name = "测试实例",
                    Path = "D:\\Applications\\Stardew Valley\\versions\\测试实例"
                }
            ]);
            registryStore.SaveManualInstances(
            [
                new ManualInstanceRecord
                {
                    Name = "更新后的实例",
                    Path = "D:\\Applications\\Stardew Valley\\versions\\更新后的实例"
                }
            ]);

            var loadedInstances = registryStore.LoadManualInstances();
            Assert.AreEqual(1, loadedInstances.Count);
            Assert.AreEqual("更新后的实例", loadedInstances[0].Name);
            Assert.AreEqual(
                "D:\\Applications\\Stardew Valley\\versions\\更新后的实例",
                loadedInstances[0].Path);

            Assert.IsFalse(
                Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(),
                "原子写入完成后不应留下临时文件");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ModConflictAnalyzer_ShouldDetectStructuralDependencyConflictsWithoutScanningFiles()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "svl-mod-conflict-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var firstPath = CreateModFolder(root, "First", "shared/content.json", "shared/icon.png");
            var secondPath = CreateModFolder(root, "Second", "shared/content.json", "shared/icon.png");
            var duplicateOnePath = CreateModFolder(root, "DuplicateOne", "one.json");
            var duplicateTwoPath = CreateModFolder(root, "DuplicateTwo", "two.json");
            var disabledPath = CreateModFolder(root, "DisabledDependency", "disabled.json");

            var first = new ModManageItem
            {
                DisplayName = "First Mod",
                DirectoryName = "First",
                FullPath = firstPath,
                UniqueId = "Author.First",
                Version = "1.0.0",
                IsEnabled = true
            };
            first.DisplayDependencies.Add(new ModDependencyDisplayItem
            {
                UniqueId = "Author.Second",
                DisplayName = "Second Mod",
                MinimumVersion = "2.0.0",
                IsRequired = true,
                IsInstalled = true,
                IsInstalledAndEnabled = true
            });
            first.DisplayDependencies.Add(new ModDependencyDisplayItem
            {
                UniqueId = "Author.Missing",
                DisplayName = "Missing Mod",
                MinimumVersion = "1.0.0",
                IsRequired = true
            });
            first.DisplayDependencies.Add(new ModDependencyDisplayItem
            {
                UniqueId = "Author.Disabled",
                DisplayName = "Disabled Dependency",
                IsRequired = true,
                IsInstalled = true,
                IsInstalledButDisabled = true
            });

            var second = new ModManageItem
            {
                DisplayName = "Second Mod",
                DirectoryName = "Second",
                FullPath = secondPath,
                UniqueId = "Author.Second",
                Version = "1.0.0",
                IsEnabled = true
            };
            second.DisplayDependencies.Add(new ModDependencyDisplayItem
            {
                UniqueId = "Author.First",
                DisplayName = "First Mod",
                IsRequired = true,
                IsInstalled = true,
                IsInstalledAndEnabled = true
            });
            var duplicateOne = new ModManageItem
            {
                DisplayName = "Duplicate One",
                DirectoryName = "DuplicateOne",
                FullPath = duplicateOnePath,
                UniqueId = "Author.Duplicate",
                IsEnabled = true
            };
            var duplicateTwo = new ModManageItem
            {
                DisplayName = "Duplicate Two",
                DirectoryName = "DuplicateTwo",
                FullPath = duplicateTwoPath,
                UniqueId = "author.duplicate",
                IsEnabled = true
            };
            var disabled = new ModManageItem
            {
                DisplayName = "Disabled Dependency",
                DirectoryName = "DisabledDependency",
                FullPath = disabledPath,
                UniqueId = "Author.Disabled",
                Version = "1.0.0",
                IsEnabled = false
            };

            var conflicts = ModConflictAnalyzer.Analyze(
                [first, second, duplicateOne, duplicateTwo, disabled]);

            CollectionAssert.Contains(
                conflicts.Select(item => item.Kind).ToList(),
                ModConflictKind.DuplicateId);
            CollectionAssert.Contains(
                conflicts.Select(item => item.Kind).ToList(),
                ModConflictKind.MissingDependency);
            CollectionAssert.Contains(
                conflicts.Select(item => item.Kind).ToList(),
                ModConflictKind.DisabledDependency);
            CollectionAssert.Contains(
                conflicts.Select(item => item.Kind).ToList(),
                ModConflictKind.VersionMismatch);
            var cycleConflicts = conflicts
                .Where(item => item.Kind == ModConflictKind.CircularDependency)
                .ToList();
            Assert.AreEqual(1, cycleConflicts.Count, "同一个循环依赖应只展示一条结果");
            StringAssert.Contains(cycleConflicts[0].Description, "First Mod");
            StringAssert.Contains(cycleConflicts[0].Description, "Second Mod");
            Assert.IsFalse(
                conflicts.Any(item => item.Kind == ModConflictKind.FileConflict),
                "冲突检测不应扫描 Mod 文件或把文件路径当作冲突字段");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void CurseforgeModpackGameVersionParsing_ShouldIgnorePackVersionFallback()
    {
        using var document = JsonDocument.Parse(
            "{\"gameVersions\":[\"1.6.8\"],\"displayName\":\"Blissful Valley 1.2.0\",\"fileName\":\"Blissful-Valley-1.0.0.zip\"}");
        var parser = typeof(RemoteCatalogService).GetMethod(
            "ExtractCurseforgeFileGameVersions",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        var result = (List<string>)parser!.Invoke(
            null,
            [document.RootElement, false])!;

        CollectionAssert.AreEqual(new[] { "1.6.8" }, result);

        using var missingGameVersionsDocument = JsonDocument.Parse(
            "{\"displayName\":\"Blissful Valley 1.2.0\",\"fileName\":\"Blissful-Valley-1.0.0.zip\"}");
        var emptyResult = (List<string>)parser.Invoke(
            null,
            [missingGameVersionsDocument.RootElement, false])!;
        Assert.AreEqual(0, emptyResult.Count, "没有 API 游戏版本时不应从整合包文件名推断版本");
    }

    [TestMethod]
    public void CurseforgeSearchGameVersionParsing_ShouldNotUseModpackTextVersion()
    {
        using var document = JsonDocument.Parse(
            "{\"latestFilesIndexes\":[{\"gameVersion\":\"1.6.8\"}]," +
            "\"displayName\":\"Blissful Valley 1.2.0\",\"fileName\":\"Blissful-Valley-1.0.0.zip\"}");
        var parser = typeof(RemoteCatalogService).GetMethod(
            "ResolveCurseforgeGameVersions",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        var modpackResult = (List<string>)parser!.Invoke(
            null,
            [document.RootElement, "Blissful Valley 1.2.0", "Blissful Valley", false])!;

        CollectionAssert.AreEqual(
            new[] { "1.6.8" },
            modpackResult,
            "Modpack 搜索只能使用 API 的 gameVersion，不能把整合包版本加入游戏版本筛选");

        using var ordinaryModDocument = JsonDocument.Parse(
            "{\"displayName\":\"Example Mod\"}");
        var ordinaryModResult = (List<string>)parser.Invoke(
            null,
            [ordinaryModDocument.RootElement, "Compatible with Stardew Valley 1.6.8", "Example Mod", true])!;
        CollectionAssert.Contains(ordinaryModResult, "1.6.8");
    }

    [TestMethod]
    public async Task RemoteCatalogService_ShouldUseApiGameVersionForCurseforgeModpackSearchAndDetails()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "svl-curse-modpack-game-version-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var handler = new FixtureHttpMessageHandler(request =>
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.EndsWith("/mods/search", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(
                        "{\"data\":[{" +
                        "\"id\":1012878,\"name\":\"Blissful Valley\",\"summary\":\"Cozy pack 1.2.0\"," +
                        "\"downloadCount\":10,\"dateModified\":\"2024-05-16T00:00:00Z\"," +
                        "\"classId\":6771,\"latestFilesIndexes\":[{\"gameVersion\":\"1.6.8\"}]" +
                        "}]}" );
                }

                if (path.EndsWith("/mods/1012878", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(
                        "{\"data\":{\"id\":1012878,\"name\":\"Blissful Valley\",\"summary\":\"Cozy pack\"}}" );
                }

                if (path.EndsWith("/mods/1012878/files", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(
                        "{\"data\":[{" +
                        "\"id\":5346378,\"displayName\":\"Blissful Valley 1.2.0\"," +
                        "\"fileName\":\"Blissful-Valley-1.2.0.cfmodpack\"," +
                        "\"downloadUrl\":\"https://edge.example/blissful.cfmodpack\"," +
                        "\"gameVersions\":[\"1.6.8\"]" +
                        "}]}" );
                }

                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            });
            using var client = new HttpClient(handler);
            var service = new RemoteCatalogService(new AppUserSettingsStore(root), client);

            var search = await service.SearchModpacksAsync("Blissful", "Curseforge");
            Assert.AreEqual(1, search.Count);
            Assert.AreEqual("1.6.8", search[0].GameVersionTag);

            var details = await service.GetResourceDetailsAsync(
                new CatalogResourceIdentity(1012878, "Blissful Valley", CatalogSource.Curseforge, true, string.Empty));

            var compatibilityHeaders = details.VersionOptions
                .Where(item => item.StartsWith("兼容游戏版本：", StringComparison.Ordinal))
                .ToList();
            CollectionAssert.AreEqual(new[] { "兼容游戏版本：1.6.8" }, compatibilityHeaders);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ModConflictAnalyzer_ShouldUseCommunityRelationsOnly()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "svl-community-conflict-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var firstPath = CreateModFolder(root, "First", "svl-source.json");
            var secondPath = CreateModFolder(root, "Second", "svl-source.json");
            var first = new ModManageItem
            {
                DisplayName = "First Mod",
                DirectoryName = "First",
                FullPath = firstPath,
                UniqueId = "Author.First",
                IsEnabled = true
            };
            var second = new ModManageItem
            {
                DisplayName = "Second Mod",
                DirectoryName = "Second",
                FullPath = secondPath,
                UniqueId = "Author.Second",
                IsEnabled = true
            };

            var noCommunityConflicts = ModConflictAnalyzer.AnalyzeCommunity(
                [
                    new ModCommunityConflictSource(first, new CommunityLocalizationEntry()),
                    new ModCommunityConflictSource(second, new CommunityLocalizationEntry())
                ]);
            Assert.AreEqual(0, noCommunityConflicts.Count,
                "仅共享 svl-source.json 不应被视为冲突");

            var communityEntry = new CommunityLocalizationEntry();
            communityEntry.HardConflicts =
            [
                new CommunityLocalizationRelation
                {
                    Id = "Author.Second",
                    Name = "Second Mod",
                    Reason = "社区标注测试冲突"
                }
            ];
            var conflicts = ModConflictAnalyzer.AnalyzeCommunity(
                [
                    new ModCommunityConflictSource(first, communityEntry),
                    new ModCommunityConflictSource(second, new CommunityLocalizationEntry())
                ]);

            Assert.AreEqual(1, conflicts.Count);
            Assert.AreEqual(ModConflictKind.CommunityHardConflict, conflicts[0].Kind);
            StringAssert.Contains(conflicts[0].Description, "社区标注测试冲突");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task RemoteCatalogService_ShouldRouteCurseforgeSearchAndDetailsThroughHttpFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-remote-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var handler = new FixtureHttpMessageHandler(request =>
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.EndsWith("/search", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse("{\"data\":[{" +
                        "\"id\":101,\"name\":\"Content Patcher\",\"summary\":\"A mod\"," +
                        "\"downloadCount\":123,\"dateModified\":\"2024-01-01T00:00:00Z\"," +
                        "\"logo\":{\"url\":\"https://cdn.example/content.png\",\"thumbnailUrl\":\"https://cdn.example/content-small.png\"}" +
                        "}]}");
                }

                if (path.EndsWith("/mods/101/files", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse("{\"data\":[{" +
                        "\"id\":7448774,\"displayName\":\"Content Patcher 2.9.0\"," +
                        "\"fileName\":\"File 7448774_ Content Patcher 2.9.0.zip\"," +
                        "\"downloadUrl\":\"https://edge.example/files/content-patcher.zip\"," +
                        "\"fileLength\":1234,\"downloadCount\":12," +
                        "\"fileDate\":\"2024-01-01T00:00:00Z\",\"releaseType\":1," +
                        "\"gameVersions\":[\"1.6.15\"]" +
                        "}]}");
                }

                if (path.EndsWith("/mods/101", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse("{\"data\":{\"id\":101,\"name\":\"Content Patcher\"," +
                        "\"summary\":\"A mod\",\"logo\":{\"url\":\"https://cdn.example/content.png\"}}}");
                }

                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            });
            using var client = new HttpClient(handler);
            var service = new RemoteCatalogService(new AppUserSettingsStore(root), client);

            var search = await service.SearchModsAdvancedPagedAsync(
                "Content",
                "Curseforge",
                useCommunityLocalization: false,
                page: 1,
                pageSize: 10);

            Assert.AreEqual(1, search.Items.Count);
            Assert.AreEqual(101, search.Items[0].Identity.ResourceId);
            Assert.AreEqual(CatalogSource.Curseforge, search.Items[0].Identity.Source);
            Assert.IsFalse(search.HasMore);

            var details = await service.GetResourceDetailsAsync(
                new CatalogResourceIdentity(101, "Content Patcher", CatalogSource.Curseforge, false, string.Empty));
            Assert.AreEqual("Content Patcher", details.Name);
            Assert.AreEqual(1, details.DownloadOptions.Count);
            StringAssert.Contains(details.DownloadOptions[0], "7448774");
            StringAssert.Contains(details.DownloadOptions[0], "https://edge.example/files/content-patcher.zip");
            StringAssert.Contains(details.VersionOptions[0], "1.6.15");

            var requests = handler.Requests.Select(request => request.AbsolutePath).ToList();
            CollectionAssert.Contains(requests, "/v1/mods/search");
            CollectionAssert.Contains(requests, "/v1/mods/101");
            CollectionAssert.Contains(requests, "/v1/mods/101/files");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task RemoteCatalogService_ShouldKeepCurseforgeSmapiFileId()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-smapi-curseforge-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var handler = new FixtureHttpMessageHandler(request =>
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.EndsWith("/mods/898372/files", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(
                        "{\"data\":[{" +
                        "\"id\":\"5312529\",\"displayName\":\"SMAPI 4.5.2\"," +
                        "\"fileName\":\"SMAPI-4.5.2.zip\",\"fileDate\":\"2024-01-01T00:00:00Z\"" +
                        "}]}" );
                }

                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            });
            using var client = new HttpClient(handler);
            var service = new RemoteCatalogService(new AppUserSettingsStore(root), client);

            var entries = await service.GetSmapiVersionEntriesFromCurseForgeAsync(1, 5);

            Assert.AreEqual(1, entries.Count);
            Assert.AreEqual("Curseforge", entries[0].Source);
            Assert.AreEqual(5312529L, entries[0].FileId);
            StringAssert.Contains(entries[0].DownloadUrl, "edge.forgecdn.net/files/5312/529");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task RemoteCatalogService_ShouldSendNexusCredentialAndParseGraphQlFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-nexus-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var handler = new FixtureHttpMessageHandler(_ => JsonResponse(
                "{\"data\":{\"mods\":{\"nodes\":[{" +
                "\"modId\":29868,\"name\":\"Content Patcher\",\"summary\":\"A Nexus mod\"," +
                "\"description\":\"A Nexus mod\",\"pictureUrl\":\"https://cdn.example/nexus.png\"," +
                "\"downloads\":456,\"category\":\"utility\",\"updatedAt\":\"2024-01-02T00:00:00Z\"" +
                "}]}}}"));
            using var client = new HttpClient(handler);
            var store = new AppUserSettingsStore(root);
            store.Save(new AppUserSettings { NexusApiKey = "fixture-api-key" });
            var service = new RemoteCatalogService(store, client);

            var result = await service.SearchModsAdvancedPagedAsync(
                "Content",
                "NexusMods",
                useCommunityLocalization: false,
                page: 1,
                pageSize: 10);

            Assert.AreEqual(1, result.Items.Count);
            Assert.AreEqual(29868, result.Items[0].Identity.ResourceId);
            Assert.AreEqual(CatalogSource.NexusMods, result.Items[0].Identity.Source);
            Assert.AreEqual("Content Patcher", result.Items[0].Name);
            Assert.IsFalse(result.HasMore);
            CollectionAssert.Contains(handler.ApiKeys, "fixture-api-key");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task RemoteCatalogService_ShouldExposeCurseforgeNoFileFixtureAsInformational()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-no-file-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var handler = new FixtureHttpMessageHandler(request =>
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.EndsWith("/mods/202", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(
                        "{\"data\":{\"id\":202,\"name\":\"No File Mod\",\"summary\":\"No release yet\"}}");
                }

                if (path.EndsWith("/mods/202/files", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse("{\"data\":[]}");
                }

                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            });
            using var client = new HttpClient(handler);
            var service = new RemoteCatalogService(new AppUserSettingsStore(root), client);

            var details = await service.GetResourceDetailsAsync(
                new CatalogResourceIdentity(202, "No File Mod", CatalogSource.Curseforge, false, string.Empty));

            CollectionAssert.AreEqual(
                new[] { "暂无可下载文件" },
                details.DownloadOptions);
            Assert.IsTrue(details.VersionOptions.Count == 0);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task RemoteCatalogService_ShouldCheckGithubReleaseAndIgnorePrerelease()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-github-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var handler = new FixtureHttpMessageHandler(request =>
            {
                Assert.AreEqual(
                    "/repos/example/ExampleMod/releases",
                    request.RequestUri?.AbsolutePath);
                return JsonResponse("[{" +
                    "\"tag_name\":\"v9.0.0-beta\",\"name\":\"beta\",\"prerelease\":true," +
                    "\"published_at\":\"2026-09-12T00:00:00Z\",\"assets\":[]" +
                    "},{" +
                    "\"tag_name\":\"v1.4.0\",\"name\":\"Example Mod 1.4.0\",\"prerelease\":false," +
                    "\"published_at\":\"2026-09-01T00:00:00Z\",\"assets\":[{" +
                    "\"name\":\"ExampleMod-1.4.0.zip\",\"browser_download_url\":\"https://github.com/example/ExampleMod/releases/download/v1.4.0/ExampleMod-1.4.0.zip\"" +
                    "}]}]");
            });
            using var client = new HttpClient(handler);
            var service = new RemoteCatalogService(new AppUserSettingsStore(root), client);

            var result = await service.CheckGitHubModUpdateAsync("github:example/ExampleMod.git", "1.3.0");

            Assert.IsTrue(result.IsChecked);
            Assert.IsTrue(result.HasUpdate);
            Assert.AreEqual("example/ExampleMod", result.Repository);
            Assert.AreEqual("1.4.0", result.LatestVersion);
            Assert.AreEqual(
                "ExampleMod-1.4.0.zip",
                result.FileName);
            StringAssert.Contains(result.DownloadUrl, "/ExampleMod-1.4.0.zip");

            Assert.IsTrue(RemoteCatalogService.TryNormalizeGitHubRepository(
                "https://github.com/example/ExampleMod/releases", out var normalized));
            Assert.AreEqual("example/ExampleMod", normalized);
            Assert.IsFalse(RemoteCatalogService.TryNormalizeGitHubRepository("example", out _));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void DownloadTaskStateStore_ShouldPersistGithubRepositoryIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-github-task-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var statePath = Path.Combine(root, "tasks.json.gz");
            var store = new DownloadTaskStateStore();
            store.Save(statePath,
            [
                new DownloadTaskItem
                {
                    Name = "ExampleMod-1.4.0.zip",
                    SourcePlatform = "GitHub",
                    SourceRepository = "example/ExampleMod",
                    SourceUrl = "https://github.com/example/ExampleMod/releases/download/v1.4.0/ExampleMod-1.4.0.zip"
                }
            ]);

            var records = store.Load(statePath, out var brokenPath);

            Assert.AreEqual(string.Empty, brokenPath);
            Assert.AreEqual(1, records.Count);
            Assert.AreEqual("GitHub", records[0].SourcePlatform);
            Assert.AreEqual("example/ExampleMod", records[0].SourceRepository);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void CollectionPatchService_ShouldParsePatchesAndApplyLegacyBsdiffAtomically()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-collection-patch-" + Guid.NewGuid().ToString("N"));
        var extractRoot = Path.Combine(root, "collection");
        var patchRoot = Path.Combine(extractRoot, "patches", "Example Mod");
        var modRoot = Path.Combine(root, "Mods", "ActualMod");
        try
        {
            Directory.CreateDirectory(patchRoot);
            Directory.CreateDirectory(modRoot);

            var oldBytes = System.Text.Encoding.ASCII.GetBytes("old");
            var newBytes = System.Text.Encoding.ASCII.GetBytes("new");
            var sourcePath = Path.Combine(modRoot, "data.bin");
            File.WriteAllBytes(sourcePath, oldBytes);

            // 未压缩兼容格式：[controlLen][diffLen][newLen][control][diff][extra]。
            // 这里用 3 个 add 字节验证差分结果和源 CRC 校验。
            var patchBytes = new byte[24 + 24 + 3];
            WriteLittleEndianInt64(patchBytes, 0, 24);
            WriteLittleEndianInt64(patchBytes, 8, 3);
            WriteLittleEndianInt64(patchBytes, 16, 3);
            WriteLittleEndianInt64(patchBytes, 24, 3);
            WriteLittleEndianInt64(patchBytes, 32, 0);
            WriteLittleEndianInt64(patchBytes, 40, 0);
            for (var index = 0; index < newBytes.Length; index++)
            {
                patchBytes[48 + index] = (byte)(newBytes[index] - oldBytes[index]);
            }

            File.WriteAllBytes(Path.Combine(patchRoot, "data.bin.diff"), patchBytes);
            var crc = CalculateTestCrc32(oldBytes);
            var result = CollectionPatchService.ApplyPatches(
                extractRoot,
                modRoot,
                "Example Mod",
                new Dictionary<string, string> { ["data.bin"] = crc });

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual(1, result.AppliedCount);
            CollectionAssert.AreEqual(newBytes, File.ReadAllBytes(sourcePath));

            var assembly = typeof(CollectionInstallService).Assembly;
            var modType = assembly.GetType("SVL.Avalonia.Services.NexusCollectionJsonMod");
            Assert.IsNotNull(modType);
            var optionsField = typeof(CollectionInstallService).GetField(
                "CollectionJsonOptions",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(optionsField);
            var options = (JsonSerializerOptions)optionsField!.GetValue(null)!;
            var parsed = JsonSerializer.Deserialize(
                "{\"name\":\"Example Mod\",\"patches\":{\"data.bin\":\"" + crc + "\"}}",
                modType!,
                options);
            Assert.IsNotNull(parsed);
            var patches = (Dictionary<string, string>)modType!.GetProperty("Patches")!.GetValue(parsed);
            Assert.IsNotNull(patches);
            Assert.AreEqual(crc, patches["data.bin"]);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void WriteLittleEndianInt64(byte[] data, int offset, long value)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(offset, 8), value);
    }

    private static string CalculateTestCrc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        for (var index = 0; index < data.Length; index++)
        {
            crc ^= data[index];
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) == 1 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return (crc ^ 0xFFFFFFFFu).ToString("X8");
    }

    private static string CreateModFolder(string root, string name, params string[] files)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        foreach (var file in files)
        {
            var filePath = Path.Combine(path, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, name);
        }

        return path;
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
    }

    private sealed class FixtureHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public List<string> ApiKeys { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            if (request.Headers.TryGetValues("apikey", out var apiKeys))
            {
                ApiKeys.AddRange(apiKeys);
            }

            return Task.FromResult(responder(request));
        }
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(
            fieldName,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(field, $"未找到字段 {fieldName}");
        field!.SetValue(target, value);
    }

    private static void SetProperty(object target, string propertyName, object value)
    {
        var property = target.GetType().GetProperty(
            propertyName,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(property, $"未找到属性 {propertyName}");
        property!.SetValue(target, value);
    }

    private static void WriteArchiveText(
        System.IO.Compression.ZipArchive archive,
        string entryName,
        string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(entryName).Open());
        writer.Write(content);
    }

    private static void WriteSevenZipText(
        SharpCompress.Writers.IWriter writer,
        string entryName,
        string content)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        writer.Write(entryName, stream, null);
    }

    private sealed class RoundTripGameInstallPathLocator(string gamePath)
        : SVL.Core.Platform.Abstractions.IGameInstallPathLocator
    {
        public string TryLocateSteamStardewPath() => gamePath;

        public string TryLocateGogStardewPath() => null;
    }

    private sealed class RoundTripSmapiInstallService
        : SVL.Core.Platform.Abstractions.ISmapiInstallService
    {
        public Task<SVL.Core.Platform.Abstractions.SmapiInstallResult> InstallFromZipAsync(
            string zipFilePath,
            string gameBasePath,
            string instanceName,
            CancellationToken cancellationToken = default,
            Action<string> logger = null,
            Func<string, string, CancellationToken, Task> zipExtractor = null,
            bool updateExisting = false)
        {
            var versionRoot = Path.Combine(gameBasePath, "versions", instanceName);
            Directory.CreateDirectory(versionRoot);
            File.WriteAllText(Path.Combine(versionRoot, "StardewModdingAPI.dll"), "smapi");
            return Task.FromResult(
                SVL.Core.Platform.Abstractions.SmapiInstallResult.Success(versionRoot, versionRoot));
        }
    }
}
