using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SVL.Avalonia.Models;
using SVL.Avalonia.Services;

namespace SVL.Migration.Tests;

[TestClass]
public sealed class LauncherUpdateSafetyTests
{
    [TestMethod]
    public void AutomaticUpdate_ShouldSelectOnlyTheCurrentRuntimeMode()
    {
        var release = new LauncherReleaseInfo();
        var lightweight = new LauncherReleaseAsset { Name = "SVL_v1.2.0.0_release_Windows_x64_requires_dotnet10.zip" };
        var bundled = new LauncherReleaseAsset { Name = "SVL_v1.2.0.0_release_Windows_x64.zip" };
        var bundledExe = new LauncherReleaseAsset { Name = "SVL.Desktop_v1.2.0.0_Release.exe" };
        release.Assets.Add(lightweight);
        release.Assets.Add(bundled);
        release.Assets.Add(bundledExe);

        Assert.AreSame(lightweight, LauncherUpdateService.SelectPreferredAsset(release, LauncherRuntimeMode.FrameworkDependent, true));
        Assert.AreSame(bundledExe, LauncherUpdateService.SelectPreferredAsset(release, LauncherRuntimeMode.SelfContained, true));
        Assert.AreSame(bundled, LauncherUpdateService.SelectPreferredAsset(release, LauncherRuntimeMode.SelfContained, false));
        Assert.IsNull(LauncherUpdateService.SelectPreferredAsset(release, LauncherRuntimeMode.Unknown, true));
        release.Assets.Remove(bundled);
        release.Assets.Remove(bundledExe);
        Assert.IsNull(LauncherUpdateService.SelectPreferredAsset(release, LauncherRuntimeMode.SelfContained, true));
        release.Assets.Clear();
        release.Assets.Add(bundled);
        Assert.IsNull(LauncherUpdateService.SelectPreferredAsset(release, LauncherRuntimeMode.FrameworkDependent, true));
    }

    [TestMethod]
    public void RuntimeMode_ShouldRejectUnrecognizedMetadata()
    {
        Assert.AreEqual(LauncherRuntimeMode.SelfContained, LauncherUpdateService.ParseRuntimeMode("self-contained"));
        Assert.AreEqual(LauncherRuntimeMode.FrameworkDependent, LauncherUpdateService.ParseRuntimeMode("framework-dependent"));
        Assert.AreEqual(LauncherRuntimeMode.Unknown, LauncherUpdateService.ParseRuntimeMode(null));
        Assert.AreEqual(LauncherRuntimeMode.Unknown, LauncherUpdateService.ParseRuntimeMode("other"));
    }

    [DataTestMethod]
    [DataRow("SVL.Desktop_v1.2.1_Release.exe", "windows", "X64", true)]
    [DataRow("SVL.Desktop_v1.2.0.0_Debug_Release.exe", "windows", "X64", true)]
    [DataRow("SVL.Desktop_v1.2.1_Debug.exe", "windows", "X64", false)]
    [DataRow("SVL_v1.2.1_Windows_x64_requires_dotnet10.exe", "windows", "X64", true)]
    [DataRow("SVL_v1.2.1_Windows_x64_requires_dotnet10_diagnostic.exe", "windows", "X64", false)]
    [DataRow("SVL_v1.2.1_release_macOS_arm64.dmg", "macos", "Arm64", true)]
    [DataRow("SVL_v1.2.0.0_debug_release_macOS_arm64.dmg", "macos", "Arm64", true)]
    [DataRow("SVL_v1.2.1_release_macOS_arm64.dmg", "macos", "X64", false)]
    [DataRow("SVL_v1.2.1_debug_macOS_arm64.dmg", "macos", "Arm64", false)]
    [DataRow("SVL_v1.2.1_release_Linux_x64", "linux", "X64", true)]
    [DataRow("SVL_v1.2.0.0_debug_release_Linux_x64.zip", "linux", "X64", true)]
    [DataRow("svl-launcher_1.2.1_amd64_requires_dotnet10.deb", "linux", "X64", true)]
    [DataRow("svl-launcher_1.2.1_amd64.deb", "linux", "Arm64", false)]
    [DataRow("SVL_v1.2.1_debug_Linux_x64.zip", "linux", "X64", false)]
    public void UpdateAssets_ShouldAlwaysSelectReleaseForTheCurrentPlatform(string name, string platform, string arch, bool expected)
    {
        Assert.AreEqual(expected, LauncherUpdateService.IsCompatibleAsset(name, platform, arch));
    }

    [TestMethod]
    public void PreferredAsset_ShouldNeverSelectLegacyDebugCompatibilityFiles()
    {
        var release = new LauncherReleaseInfo();
        release.Assets.Add(new LauncherReleaseAsset { Name = "SVL.Desktop_v1.2.1_Debug.exe" });
        release.Assets.Add(new LauncherReleaseAsset { Name = "SVL.Desktop_v1.2.1_Release.exe" });
        release.Assets.Add(new LauncherReleaseAsset { Name = "SVL_v1.2.1_debug_Windows_x64_requires_dotnet10.zip" });
        release.Assets.Add(new LauncherReleaseAsset { Name = "SVL_v1.2.1_Windows_x64_requires_dotnet10.exe" });
        Assert.AreEqual("SVL.Desktop_v1.2.1_Release.exe", LauncherUpdateService.SelectPreferredAsset(release, LauncherRuntimeMode.SelfContained, true)?.Name);
        Assert.AreEqual("SVL_v1.2.1_Windows_x64_requires_dotnet10.exe", LauncherUpdateService.SelectPreferredAsset(release, LauncherRuntimeMode.FrameworkDependent, true)?.Name);
    }

    [TestMethod]
    public void PreferredAsset_ShouldAcceptTheCombinedReleaseTransitionFile()
    {
        var release = new LauncherReleaseInfo();
        release.Assets.Add(new LauncherReleaseAsset { Name = "SVL.Desktop_v1.2.0.0_Debug.exe" });
        release.Assets.Add(new LauncherReleaseAsset { Name = "SVL.Desktop_v1.2.0.0_Debug_Release.exe" });
        Assert.AreEqual("SVL.Desktop_v1.2.0.0_Debug_Release.exe", LauncherUpdateService.SelectPreferredAsset(release, LauncherRuntimeMode.SelfContained, true)?.Name);
    }

    [TestMethod]
    public void Replacement_ShouldKeepBackupAndRestoreWhenNewVersionCannotStart()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-update-replace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var target = Path.Combine(root, "SVL.exe");
            var payload = Path.Combine(root, "new.exe");
            File.WriteAllText(target, "old");
            File.WriteAllText(payload, "new");
            var success = LauncherUpdateApplier.ReplaceAndLaunch(target, payload, path => File.ReadAllText(path) == "new");
            Assert.IsTrue(success.Success, success.Error);
            Assert.AreEqual("new", File.ReadAllText(target));
            Assert.AreEqual("old", File.ReadAllText(success.BackupPath));

            File.WriteAllText(payload, "broken");
            var failed = LauncherUpdateApplier.ReplaceAndLaunch(target, payload, _ => false);
            Assert.IsFalse(failed.Success);
            Assert.AreEqual("new", File.ReadAllText(target), "启动失败后必须恢复替换前的程序");
            Assert.IsTrue(Directory.GetFiles(root, "*.failed-*.exe").Any(), "失败的程序也应保留供诊断");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void Replacement_WhenPayloadIsMissing_ShouldLeaveOriginalUntouched()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-update-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var target = Path.Combine(root, "SVL.exe");
            File.WriteAllText(target, "old");
            var result = LauncherUpdateApplier.ReplaceAndLaunch(target, Path.Combine(root, "missing.exe"), _ => true);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("old", File.ReadAllText(target));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void UpdateArchive_ShouldExtractOnlyOneNamedExecutableAndRejectAmbiguity()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-update-zip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var zipPath = Path.Combine(root, "update.zip");
            var escapeName = "svl-escape-" + Guid.NewGuid().ToString("N") + ".txt";
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("folder/SVL.Avalonia.exe").Open())) writer.Write("payload");
                using (var writer = new StreamWriter(archive.CreateEntry("../" + escapeName).Open())) writer.Write("must not extract");
            }
            var prepared = LauncherUpdateApplier.PrepareExecutable(zipPath, root);
            Assert.AreEqual("payload", File.ReadAllText(prepared));
            Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(root)!, escapeName)));
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Update))
            using (var writer = new StreamWriter(archive.CreateEntry("another/SVL.Avalonia.exe").Open())) writer.Write("second");
            Assert.ThrowsException<InvalidDataException>(() => LauncherUpdateApplier.PrepareExecutable(zipPath, root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task Download_ShouldReuseOnlyCompleteFilesAndRemovePartialFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-update-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var payload = Encoding.UTF8.GetBytes("complete test update");
            var handler = new FakeHandler(payload);
            var service = new LauncherUpdateService(new HttpClient(handler), root);
            var asset = new LauncherReleaseAsset { Name = "SVL_Release.exe", BrowserDownloadUrl = "https://example.test/update.exe", Size = payload.Length,
                Sha256 = "sha256:" + Convert.ToHexString(SHA256.HashData(payload)) };
            var first = await service.DownloadAssetAsync(asset, null, CancellationToken.None);
            var second = await service.DownloadAssetAsync(asset, null, CancellationToken.None);
            Assert.AreEqual(first, second);
            Assert.AreEqual(1, handler.Requests, "后台下载后打开更新窗口应复用缓存");
            CollectionAssert.AreEqual(payload, File.ReadAllBytes(first));
            File.WriteAllBytes(first, new byte[payload.Length]);
            await service.DownloadAssetAsync(asset, null, CancellationToken.None);
            Assert.AreEqual(2, handler.Requests, "长度相同但哈希不符的缓存必须重新下载");
            CollectionAssert.AreEqual(payload, File.ReadAllBytes(first));

            var incomplete = new LauncherReleaseAsset { Name = "other_Release.exe", BrowserDownloadUrl = "https://example.test/other.exe", Size = payload.Length + 5 };
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => service.DownloadAssetAsync(incomplete, null, CancellationToken.None));
            var wrongHash = new LauncherReleaseAsset { Name = "bad_Release.exe", BrowserDownloadUrl = "https://example.test/bad.exe", Size = payload.Length, Sha256 = new string('0', 64) };
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => service.DownloadAssetAsync(wrongHash, null, CancellationToken.None));
            Assert.IsFalse(Directory.GetFiles(root, "*.part").Any());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class FakeHandler(byte[] payload) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        }
    }
}
