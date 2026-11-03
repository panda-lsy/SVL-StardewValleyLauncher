using Microsoft.VisualStudio.TestTools.UnitTesting;
using SVL.Avalonia.Models;
using SVL.Avalonia.Services;

namespace SVL.Migration.Tests;

[TestClass]
public sealed class TransparencySettingsTests
{
    [TestMethod]
    public void NewSettings_ShouldDefaultToFourteenPointFontAndTwentyFivePercentTransparency()
    {
        var settings = new AppUserSettings();

        Assert.AreEqual(14, settings.FontSize);
        Assert.AreEqual(25, settings.BackgroundTransparencyPercent);
        Assert.IsTrue(settings.EnableTransparency);
    }

    [TestMethod]
    public void SettingsStore_ShouldMigrateLegacyToggleAndPreferExplicitPercentage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "svl-transparency-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var store = new AppUserSettingsStore(directory);
            Assert.AreEqual(25, store.Load().BackgroundTransparencyPercent,
                "全新配置应使用新的透明度默认值");
            File.WriteAllText(store.GetSettingsPath(), "{}");
            Assert.AreEqual(25, store.Load().BackgroundTransparencyPercent,
                "没有旧透明开关的配置也应使用新的默认值");
            File.WriteAllText(store.GetSettingsPath(), "{\"EnableTransparency\":true}");

            var migratedEnabled = store.Load();
            Assert.AreEqual(100, migratedEnabled.BackgroundTransparencyPercent,
                "旧版开启状态应映射到原先的最大透明效果");

            File.WriteAllText(store.GetSettingsPath(), "{\"EnableTransparency\":false}");
            var migratedDisabled = store.Load();
            Assert.AreEqual(0, migratedDisabled.BackgroundTransparencyPercent,
                "旧版关闭状态应映射为完全不透明");

            File.WriteAllText(store.GetSettingsPath(),
                "{\"EnableTransparency\":false,\"BackgroundTransparencyPercent\":37}");
            var explicitValue = store.Load();
            Assert.AreEqual(37, explicitValue.BackgroundTransparencyPercent,
                "明确保存的百分比应优先于旧版布尔字段");
            Assert.IsTrue(explicitValue.EnableTransparency,
                "旧版兼容字段应与百分比保持一致");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void LegacyMigration_ShouldPreserveExplicitAvaloniaTransparency()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-transparency-migration-" + Guid.NewGuid().ToString("N"));
        var legacyDirectory = Path.Combine(root, "legacy");
        var currentDirectory = Path.Combine(root, "current");
        Directory.CreateDirectory(legacyDirectory);
        Directory.CreateDirectory(currentDirectory);

        try
        {
            File.WriteAllText(Path.Combine(legacyDirectory, "app.json"), "{\"EnableTransparency\":false}");
            var store = new AppUserSettingsStore(currentDirectory);
            store.Save(new AppUserSettings { BackgroundTransparencyPercent = 100, EnableTransparency = true });
            var migration = new LegacyConfigurationMigrationService(
                store, new InstanceRegistryStore(currentDirectory), legacyDirectory);

            migration.Migrate();

            Assert.AreEqual(100, store.Load().BackgroundTransparencyPercent,
                "已保存的 Avalonia 透明度不能被旧版布尔开关覆盖");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
