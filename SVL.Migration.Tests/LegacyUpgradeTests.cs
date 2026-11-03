using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SVL.Avalonia.Services;

namespace SVL.Migration.Tests;

[TestClass]
public sealed class LegacyUpgradeTests
{
    [TestMethod]
    public void RepeatedMigration_ShouldNotOverwriteNewPreferencesOrRestoreARevokedLegacyKey()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-upgrade-repeat-" + Guid.NewGuid().ToString("N"));
        var legacy = Path.Combine(root, "legacy");
        var current = Path.Combine(root, "current");
        try
        {
            Directory.CreateDirectory(legacy);
            var appPath = Path.Combine(legacy, "app.json");
            var nexusPath = Path.Combine(legacy, "nexusmods.json");
            File.WriteAllText(appPath, "{\"FontSize\":18,\"DebugMode\":true}");
            File.WriteAllText(nexusPath, "{\"ApiKey\":\"legacy-test-key\"}");
            var store = new AppUserSettingsStore(current);
            var migration = new LegacyConfigurationMigrationService(
                store, new InstanceRegistryStore(current), legacy, Path.Combine(legacy, "no-instances.json"));
            Assert.IsTrue(migration.Migrate().Completed);
            Assert.AreEqual(18, store.Load().FontSize);
            Assert.AreEqual("legacy-test-key", store.Load().NexusApiKey);

            var changed = store.Load();
            changed.FontSize = 14;
            changed.DebugMode = false;
            changed.NexusApiKey = string.Empty;
            store.Save(changed);
            File.WriteAllText(appPath, "{\"FontSize\":20,\"DebugMode\":true}");
            File.WriteAllText(nexusPath, "{\"ApiKey\":\"legacy-test-key\"}");
            File.SetLastWriteTimeUtc(appPath, DateTime.UtcNow.AddSeconds(5));
            File.SetLastWriteTimeUtc(nexusPath, DateTime.UtcNow.AddSeconds(5));

            Assert.IsTrue(migration.Migrate().Completed);
            Assert.AreEqual(14, store.Load().FontSize, "新版用户选择的默认值不能被旧配置覆盖");
            Assert.IsFalse(store.Load().DebugMode);
            Assert.AreEqual(string.Empty, store.Load().NexusApiKey, "退出登录后不能重新导入旧密钥");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Migration_ShouldSelectExplicitDefaultEvenWhenItIsInTheSecondBasePath()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-upgrade-" + Guid.NewGuid().ToString("N"));
        var legacy = Path.Combine(root, "legacy");
        var current = Path.Combine(root, "current");
        var firstBase = Path.Combine(root, "first-game");
        var secondBase = Path.Combine(root, "second-game");
        var selectedVersion = Path.Combine(secondBase, "versions", "Pack", "game");
        try
        {
            Directory.CreateDirectory(legacy);
            Directory.CreateDirectory(firstBase);
            Directory.CreateDirectory(selectedVersion);
            File.WriteAllText(Path.Combine(selectedVersion, "Stardew Valley.dll"), string.Empty);
            var instancesPath = Path.Combine(legacy, "instances.json");
            var original = JsonSerializer.Serialize(new object[]
            {
                new { Id = "first", Name = "First", GamePath = firstBase, EnableIsolation = false },
                new { Id = "chosen", Name = "Pack", GamePath = secondBase, EnableIsolation = true,
                    IsDefault = true, IsSMAPIInstance = true, WindowTitle = "Custom", CustomArguments = "--example" }
            });
            File.WriteAllText(instancesPath, original);
            File.WriteAllText(Path.Combine(legacy, "gamepath.json"), JsonSerializer.Serialize(new { GamePath = firstBase }));

            var store = new AppUserSettingsStore(current);
            var registry = new InstanceRegistryStore(current);
            var migration = new LegacyConfigurationMigrationService(store, registry, legacy, instancesPath);
            var result = migration.Migrate();

            Assert.IsTrue(result.Completed, string.Join("; ", result.Errors));
            Assert.AreEqual(selectedVersion, store.Load().PreferredInstancePath);
            Assert.AreEqual("--example", store.Load().InstanceCustomLaunchArguments);
            Assert.AreEqual(2, registry.LoadManualInstances().Count);
            Assert.AreEqual(original, File.ReadAllText(instancesPath), "升级不能修改旧实例列表");
            Assert.AreEqual(0, migration.Migrate().ImportedInstanceCount, "重复迁移不能重复添加 Base");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
