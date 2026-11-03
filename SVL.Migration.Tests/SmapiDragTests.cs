using System.IO.Compression;
using System.Security.Cryptography;
using Avalonia.Headless;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SVL.Avalonia.Models;
using SVL.Avalonia.Services;
using SVL.Avalonia.ViewModels;
using SVL.Core.Platform.Abstractions;
using SVL.Core.Platform.Services;

namespace SVL.Migration.Tests;

[TestClass]
public sealed class SmapiDragTests
{
    [TestMethod]
    public void Detector_ShouldRecognizeInstallerAndDoubleZipWithoutChangingTheSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-smapi-drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var normal = Path.Combine(root, "SMAPI.zip");
            using (var zip = ZipFile.Open(normal, ZipArchiveMode.Create))
            using (var stream = zip.CreateEntry("SMAPI installer/internal/windows/install.dat").Open())
                stream.WriteByte(1);
            var before = SHA256.HashData(File.ReadAllBytes(normal));

            var doubleZip = Path.Combine(root, "SMAPI-double.zip");
            using (var outer = ZipFile.Open(doubleZip, ZipArchiveMode.Create))
            using (var stream = outer.CreateEntry("SMAPI-installer.zip").Open())
            using (var input = File.OpenRead(normal))
                input.CopyTo(stream);
            var doubleBefore = SHA256.HashData(File.ReadAllBytes(doubleZip));

            var mod = Path.Combine(root, "mod.zip");
            using (var zip = ZipFile.Open(mod, ZipArchiveMode.Create))
            using (var stream = zip.CreateEntry("Example/manifest.json").Open())
                stream.WriteByte(1);
            var decoy = Path.Combine(root, "decoy.zip");
            using (var zip = ZipFile.Open(decoy, ZipArchiveMode.Create))
            using (var stream = zip.CreateEntry("Example/install.dat").Open())
                stream.WriteByte(1);

            Assert.IsTrue(SmapiArchiveDetector.IsSmapiInstaller(normal));
            Assert.IsTrue(SmapiArchiveDetector.IsSmapiInstaller(doubleZip));
            Assert.IsFalse(SmapiArchiveDetector.IsSmapiInstaller(mod));
            Assert.IsFalse(SmapiArchiveDetector.IsSmapiInstaller(decoy));
            CollectionAssert.AreEqual(before, SHA256.HashData(File.ReadAllBytes(normal)));
            CollectionAssert.AreEqual(doubleBefore, SHA256.HashData(File.ReadAllBytes(doubleZip)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void LocalSmapiTask_ShouldPreserveArchiveAndSelectedTargetAcrossRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-smapi-drop-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var archive = Path.Combine(root, "SMAPI 4.5.2.zip");
            var gameBase = Path.Combine(root, "Game");
            var task = DownloadPageViewModel.CreateLocalSmapiTask(archive, gameBase, "My SMAPI");
            var store = new DownloadTaskStateStore();
            var state = Path.Combine(root, "tasks.json.gz");
            store.Save(state, [task]);
            var saved = store.Load(state, out _).Single();
            Assert.AreEqual(DownloadTaskAction.InstallSmapi, saved.TaskAction);
            Assert.AreEqual(string.Empty, saved.SourceUrl);
            Assert.AreEqual(archive, saved.OutputFilePath);
            Assert.AreEqual(gameBase, saved.TargetGamePath);
            Assert.AreEqual("My SMAPI", saved.TargetInstanceName);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task LocalSmapiTask_ShouldRunInstallerWithoutDownloadingOrChangingTheArchive()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-smapi-drop-install-" + Guid.NewGuid().ToString("N"));
        var gameBase = Path.Combine(root, "Game");
        Directory.CreateDirectory(gameBase);
        try
        {
            File.WriteAllText(Path.Combine(gameBase, "Stardew Valley.exe"), "isolated fake game");
            File.WriteAllText(Path.Combine(gameBase, "Stardew Valley.deps.json"), "{}");
            var archivePath = Path.Combine(root, "SMAPI.zip");
            using (var payload = new MemoryStream())
            {
                using (var zip = new ZipArchive(payload, ZipArchiveMode.Create, leaveOpen: true))
                using (var stream = zip.CreateEntry("StardewModdingAPI.dll").Open())
                    stream.WriteByte(1);
                using var outer = ZipFile.Open(archivePath, ZipArchiveMode.Create);
                using var output = outer.CreateEntry("SMAPI installer/internal/windows/install.dat").Open();
                output.Write(payload.ToArray());
            }
            var before = SHA256.HashData(File.ReadAllBytes(archivePath));

            var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AvaloniaUiSmokeTests).Assembly);
            await session.Dispatch(async () =>
            {
                var store = new AppUserSettingsStore(Path.Combine(root, "settings"));
                var localization = new LocalizationService(store);
                var images = new ImageResourceService(localization);
                var locator = new FixedGameLocator(gameBase);
                var parser = new NxmLinkParser();
                var http = new HttpDownloadService(store);
                var nexus = new NexusModDownloadResolverService();
                var installer = new SVL.Avalonia.Services.SmapiInstallService();
                var downloadInstaller = new DownloadInstallService(locator);
                var browser = new BrowserDownloadFallbackService(parser, new ExternalProcessService());
                var catalog = new RemoteCatalogService(store);
                var modpack = new ModpackInstallService(locator, installer, http, catalog, store, nexus, parser, browser);
                var collection = new CollectionInstallService(locator, installer, http, catalog, store, nexus, parser, browser, modpack, downloadInstaller);
                var vm = new DownloadPageViewModel(localization, images, parser, locator, store, new DialogService(), http,
                    nexus, downloadInstaller, installer, browser, catalog, new DownloadTaskStateStore(),
                    new RetryDiffReportService(), modpack, collection, new InstanceRegistryStore(Path.Combine(root, "registry")),
                    Path.Combine(root, "app-data"));
                var task = DownloadPageViewModel.CreateLocalSmapiTask(archivePath, gameBase, "Test Instance");
                vm.DownloadTasks.Add(task);

                await vm.ExecuteTaskAsync(task);

                Assert.AreEqual(DownloadTaskState.Completed, task.TaskState, task.FailedDetails);
                Assert.AreEqual(100, task.Progress);
                Assert.IsTrue(File.Exists(Path.Combine(gameBase, "versions", "Test Instance", "StardewModdingAPI.dll")));
                Assert.AreEqual(string.Empty, task.SourceUrl);
                CollectionAssert.AreEqual(before, SHA256.HashData(File.ReadAllBytes(archivePath)));
                return true;
            }, CancellationToken.None);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void CompletedTask_ShouldUseTheFinishSprite()
    {
        var task = new DownloadTaskItem();
        task.SetState(DownloadTaskState.Completed, "完成");
        Assert.AreEqual("finish", task.StatusIconName);
        Assert.IsTrue(SVL.Avalonia.Controls.ThemeIcon.IsIconMapped(task.StatusIconName));
        task.SetState(DownloadTaskState.Failed, "失败");
        Assert.AreNotEqual("finish", task.StatusIconName);
    }

    private sealed class FixedGameLocator(string gamePath) : IGameInstallPathLocator
    {
        public string TryLocateSteamStardewPath() => gamePath;
        public string TryLocateGogStardewPath() => null;
    }
}
