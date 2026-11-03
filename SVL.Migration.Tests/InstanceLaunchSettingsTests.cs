using System.Text.Json;
using Avalonia.Headless;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SVL.Avalonia.Models;
using SVL.Avalonia.Services;
using SVL.Avalonia.ViewModels;
using SVL.Core.Platform.Abstractions;
using SVL.Core.Platform.Services;

namespace SVL.Migration.Tests;

[TestClass]
public sealed class InstanceLaunchSettingsTests
{
    [TestMethod]
    public async Task Launch_ShouldUseTheSelectedInstancesArguments()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-instance-launch-" + Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        try
        {
            File.WriteAllText(Path.Combine(first, "Stardew Valley.exe"), string.Empty);
            File.WriteAllText(Path.Combine(second, "Stardew Valley.exe"), string.Empty);
            var settings = new AppUserSettingsStore(Path.Combine(root, "config"));
            settings.Save(new AppUserSettings { PreferredInstancePath = first, InstanceCustomLaunchArguments = "--global" });
            var instances = new InstanceLaunchSettingsStore(settings);
            instances.Save(first, new InstanceLaunchSettings { CustomArguments = "--first" });
            instances.Save(second, new InstanceLaunchSettings { CustomArguments = "--second" });
            var process = new FakeProcess();
            var localization = new LocalizationService(settings);
            var vm = new LaunchPageViewModel(new FakeLocator(), process, settings, localization, new ImageResourceService(localization));
            await vm.LaunchGameCommand.ExecuteAsync(null);
            Assert.AreEqual("--first", process.LastArguments);
            var app = settings.Load();
            app.PreferredInstancePath = second;
            settings.Save(app);
            vm.RefreshFromSettingsAndEnvironment();
            await vm.LaunchGameCommand.ExecuteAsync(null);
            Assert.AreEqual("--second", process.LastArguments);
            instances.Save(second, new InstanceLaunchSettings
            {
                CustomArguments = "--second", AutoConnectServer = true, SteamInviteCode = "109775241234567890"
            });
            await vm.LaunchGameCommand.ExecuteAsync(null);
            Assert.AreEqual("--second +connect_lobby 109775241234567890", process.LastArguments);
            instances.Save(second, new InstanceLaunchSettings
            {
                CustomArguments = "--second", AutoConnectServer = true, SteamInviteCode = "ABC-INVALID"
            });
            await vm.LaunchGameCommand.ExecuteAsync(null);
            Assert.AreEqual("--second", process.LastArguments);
            StringAssert.Contains(vm.ActionStatus, "手动加入", "不支持的邀请码不能被当成已经连接");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task Settings_ShouldAutosaveSeparatelyAndFlushBeforeSwitchingInstances()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-instance-settings-" + Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        try
        {
            var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AvaloniaUiSmokeTests).Assembly);
            await session.Dispatch(async () =>
            {
                var store = new AppUserSettingsStore(Path.Combine(root, "config"));
                store.Save(new AppUserSettings { PreferredInstancePath = first, GameWindowTitle = "global" });
                var vm = CreateViewModel(store);
                vm.SwitchToSettings();
                vm.GameWindowTitle = "first title";
                vm.InstanceCustomLaunchArguments = "--first";

                var app = store.Load();
                app.PreferredInstancePath = second;
                store.Save(app);
                vm.ReloadFromSettings();
                var instanceStore = new InstanceLaunchSettingsStore(store);
                Assert.AreEqual("--first", instanceStore.GetEffective(first, store.Load()).CustomArguments);
                Assert.AreEqual("global", vm.GameWindowTitle, "无独立设置的实例应保留旧全局设置作为回退");
                vm.GameWindowTitle = "second title";
                vm.InstanceCustomLaunchArguments = "--second";
                vm.OverrideSteamLaunchOptions = true;
                vm.SteamLaunchOptions = "second steam";
                vm.InstanceAutoConnectServer = true;
                vm.InstanceSteamInviteCode = "109775241234567890";
                await Task.Delay(1400);

                Assert.AreEqual("first title", instanceStore.GetEffective(first, store.Load()).WindowTitle);
                Assert.AreEqual("--second", instanceStore.GetEffective(second, store.Load()).CustomArguments);
                Assert.AreEqual("second steam", instanceStore.GetEffective(second, store.Load()).SteamLaunchOptions);
                Assert.AreEqual("global", store.Load().GameWindowTitle, "实例设置不能覆盖全局设置");
                Assert.IsTrue(instanceStore.GetEffective(second, store.Load()).OverrideSteamLaunchOptions);
                Assert.IsTrue(instanceStore.GetEffective(second, store.Load()).AutoConnectServer);
                Assert.AreEqual("109775241234567890", instanceStore.GetEffective(second, store.Load()).SteamInviteCode);
                return true;
            }, CancellationToken.None);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task SavingOverview_ShouldKeepInheritingGlobalLaunchSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-instance-overview-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(root, "game");
        Directory.CreateDirectory(instance);
        try
        {
            var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AvaloniaUiSmokeTests).Assembly);
            await session.Dispatch(() =>
            {
                var store = new AppUserSettingsStore(Path.Combine(root, "config"));
                store.Save(new AppUserSettings { PreferredInstancePath = instance, GameWindowTitle = "first global title" });
                var vm = CreateViewModel(store);
                vm.InstanceName = "Renamed";
                vm.SaveInstanceSettingsCommand.Execute(null);

                var instances = new InstanceLaunchSettingsStore(store);
                Assert.IsFalse(File.Exists(instances.GetSettingsPath()), "保存个性化信息不应固定继承的启动设置");
                var global = store.Load();
                global.GameWindowTitle = "updated global title";
                store.Save(global);
                Assert.AreEqual("updated global title", instances.GetEffective(instance, store.Load()).WindowTitle);
                return Task.FromResult(true);
            }, CancellationToken.None);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void LegacyMigration_ShouldImportEveryInstanceWithoutOverwritingCurrentOrNewerSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-instance-migrate-" + Guid.NewGuid().ToString("N"));
        var legacy = Path.Combine(root, "legacy");
        var current = Path.Combine(root, "current");
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        try
        {
            var legacyFile = Path.Combine(legacy, "instances.json");
            File.WriteAllText(legacyFile, JsonSerializer.Serialize(new object[]
            {
                new { Name = "First", GamePath = first, IsDefault = true, CustomArguments = "--old-first", WindowTitle = "old" },
                new { Name = "Second", GamePath = second, CustomArguments = "--old-second", SteamLaunchOptions = "old steam", OverrideSteamLaunchOptions = true }
            }));
            var store = new AppUserSettingsStore(current);
            store.Save(new AppUserSettings { PreferredInstancePath = first, GameWindowTitle = "current", InstanceCustomLaunchArguments = "--current" });
            File.WriteAllText(Path.Combine(current, ".legacy-migration-v1.json"),
                JsonSerializer.Serialize(new { CompletedAt = DateTimeOffset.UtcNow, Sources = new[] { legacyFile } }));
            var migration = new LegacyConfigurationMigrationService(store, new InstanceRegistryStore(current), legacy, legacyFile);
            Assert.IsTrue(migration.Migrate().Completed);
            var instances = new InstanceLaunchSettingsStore(store);
            Assert.AreEqual("--current", instances.GetEffective(first, store.Load()).CustomArguments);
            Assert.AreEqual("current", instances.GetEffective(first, store.Load()).WindowTitle);
            Assert.AreEqual("--old-second", instances.GetEffective(second, store.Load()).CustomArguments);
            Assert.IsTrue(instances.GetEffective(second, store.Load()).OverrideSteamLaunchOptions);

            instances.Save(second, new InstanceLaunchSettings { CustomArguments = "--edited" });
            File.SetLastWriteTimeUtc(legacyFile, DateTime.UtcNow.AddSeconds(5));
            Assert.IsTrue(migration.Migrate().Completed);
            Assert.AreEqual("--edited", instances.GetEffective(second, store.Load()).CustomArguments);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void CorruptInstanceSettings_ShouldNotBeOverwritten()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-instance-corrupt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var instances = new InstanceLaunchSettingsStore(new AppUserSettingsStore(root));
            File.WriteAllText(instances.GetSettingsPath(), "{invalid");
            Assert.ThrowsException<JsonException>(() => instances.Save(root, new InstanceLaunchSettings()));
            Assert.AreEqual("{invalid", File.ReadAllText(instances.GetSettingsPath()));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static VersionSettingsPageViewModel CreateViewModel(AppUserSettingsStore store)
    {
        var locator = new FakeLocator();
        var localization = new LocalizationService(store);
        var download = new HttpDownloadService(store);
        var parser = new NxmLinkParser();
        return new VersionSettingsPageViewModel(store, locator, localization, new ImageResourceService(localization),
            new DialogService(), new RemoteCatalogService(store), new SVL.Avalonia.Services.SmapiInstallService(),
            new SmapiDownloadService(download, parser), new CommunityLocalizationService(store));
    }

    private sealed class FakeLocator : IGameInstallPathLocator
    {
        public string TryLocateSteamStardewPath() => null;
        public string TryLocateGogStardewPath() => null;
    }

    private sealed class FakeProcess : IExternalProcessService
    {
        public string LastArguments { get; private set; } = string.Empty;
        public bool TryOpenUrl(string url) => true;
        public bool TryOpenPath(string path) => true;
        public bool TryLaunchProcess(string fileName, string arguments, string workingDirectory = null)
        {
            LastArguments = arguments;
            return true;
        }
        public int RunCommand(string fileName, string arguments, string workingDirectory = null) => 0;
    }
}
