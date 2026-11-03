using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SVL.Avalonia.Models;
using SVL.Avalonia.Services;

namespace SVL.Migration.Tests;

[TestClass]
public sealed class SteamCmdPlatformTests
{
    [TestMethod]
    public void LoginResult_ShouldDistinguishSuccessGuardAndRealFailureFromOsAssertions()
    {
        const string assertion = "enum_names.cpp (2184) : Assertion Failed: Missing String for EOSType (-73)";
        Assert.IsTrue(SteamCmdService.IsOsTypeEnumerationAssertion(assertion));
        Assert.IsFalse(SteamCmdService.IsOsTypeEnumerationAssertion("Assertion Failed: authentication error"));
        Assert.IsTrue(SteamCmdService.IsSteamMetadataStatusLine("FAILED login: Invalid Password"));
        Assert.IsTrue(SteamCmdService.IsSteamMetadataStatusLine("Connecting anonymously to Steam Public..."));
        Assert.IsFalse(SteamCmdService.IsSteamMetadataStatusLine("\"download\"    \"453699840\""));
        Assert.AreEqual(SteamCmdLoginStatus.Success,
            SteamCmdService.ClassifyLoginResult(0, assertion + "\nWaiting for user info...OK").Status);
        Assert.AreEqual(SteamCmdLoginStatus.Error,
            SteamCmdService.ClassifyLoginResult(5, assertion + "\nWaiting for user info...FAILED login").Status);
        Assert.AreEqual(SteamCmdLoginStatus.NeedsGuardCode,
            SteamCmdService.ClassifyLoginResult(5, "Steam Guard: enter two-factor code").Status);
        Assert.AreEqual(SteamCmdLoginStatus.InvalidCredentials,
            SteamCmdService.ClassifyLoginResult(5, "FAILED login: Invalid Password").Status);
        Assert.AreEqual(SteamCmdLoginStatus.Error,
            SteamCmdService.ClassifyLoginResult(0, assertion).Status);
    }

    [TestMethod]
    public async Task LoginPreparation_ShouldFinishSelfUpdateBeforeCredentialsAndCollapseRepeatedAssertions()
    {
        if (OperatingSystem.IsWindows()) { Assert.Inconclusive("使用 Unix 脚本模拟 SteamCMD"); return; }
        var root = Path.Combine(Path.GetTempPath(), "svl-steamcmd-login-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var script = Path.Combine(root, "steamcmd.sh");
            await File.WriteAllTextAsync(script, """
                #!/bin/sh
                if [ "$1" = "+quit" ]; then
                  echo prepare >> calls.txt
                  if [ ! -f prepared ]; then
                    touch prepared
                    echo 'Downloading update'
                    echo 'enum_names.cpp (2184) : Assertion Failed: Missing String for EOSType (-73)' >&2
                    echo 'enum_names.cpp (2184) : Assertion Failed: Missing String for EOSType (-72)' >&2
                  fi
                  exit 0
                fi
                echo login >> calls.txt
                if [ "$2" = "bad user" ]; then
                  echo 'FAILED login: Invalid Password' >&2
                  exit 5
                fi
                echo 'Waiting for user info...OK'
                exit 0
                """.Replace("\r\n", "\n"));
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (OperatingSystem.IsMacOS()) File.WriteAllText(Path.Combine(root, "steamcmd"), "fixture");
            else
            {
                Directory.CreateDirectory(Path.Combine(root, "linux32"));
                File.WriteAllText(Path.Combine(root, "linux32", "steamcmd"), "fixture");
            }
            var store = new AppUserSettingsStore(Path.Combine(root, "settings"));
            var trace = Path.Combine(root, "trace.log");
            Directory.CreateDirectory(Path.Combine(root, "logs"));
            File.WriteAllText(Path.Combine(root, "logs", "console.log"), "stale log\n");
            var service = new SteamCmdService(new HttpDownloadService(store), root, trace);
            var messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
            await service.PrepareForLoginAsync(messages.Enqueue);
            await service.PrepareForLoginAsync(messages.Enqueue);
            var result = await service.LoginAsync("dummy user", "test+password", null, messages.Enqueue);
            Assert.AreEqual(SteamCmdLoginStatus.Success, result.Status);
            var invalidResult = await service.LoginAsync("bad user", "wrong password", null, messages.Enqueue);
            Assert.AreEqual(SteamCmdLoginStatus.InvalidCredentials, invalidResult.Status);
            CollectionAssert.AreEqual(new[] { "prepare", "prepare", "login", "login" }, File.ReadAllLines(Path.Combine(root, "calls.txt")));
            Assert.AreEqual(1, messages.Count(message => message.Contains("已折叠重复")));
            Assert.IsFalse(messages.Any(message => message.Contains("Missing String for EOSType")));
            Assert.IsTrue(messages.Any(message => message.Contains("Invalid Password")), "旧日志存在时也不能丢失 stderr");
            var traceText = File.ReadAllText(trace);
            Assert.IsFalse(traceText.Contains("test+password"));
            Assert.IsFalse(traceText.Contains("dummy user"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void InstallerAndDepot_ShouldMatchTheOperatingSystem()
    {
        Assert.AreEqual("steamcmd.zip", SteamCmdService.GetInstallerFileName(OSPlatform.Windows));
        Assert.AreEqual("steamcmd_osx.tar.gz", SteamCmdService.GetInstallerFileName(OSPlatform.OSX));
        Assert.AreEqual("steamcmd_linux.tar.gz", SteamCmdService.GetInstallerFileName(OSPlatform.Linux));
        Assert.AreEqual(413151, SteamCmdService.GetDepotId(OSPlatform.Windows));
        Assert.AreEqual(413152, SteamCmdService.GetDepotId(OSPlatform.OSX));
        Assert.AreEqual(413153, SteamCmdService.GetDepotId(OSPlatform.Linux));
        Assert.ThrowsException<PlatformNotSupportedException>(() => SteamCmdService.GetInstallerFileName(OSPlatform.Create("OTHER")));
    }

    [TestMethod]
    public void InstallDirectory_ShouldPreserveWrongPlatformFilesAndReuseValidLegacyInstall()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-steamcmd-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var windowsFile = Path.Combine(root, "steamcmd.exe");
            File.WriteAllText(windowsFile, "old windows bootstrap");
            Assert.AreEqual(Path.Combine(root, "macos"), SteamCmdService.ResolveInstallDirectory(root, OSPlatform.OSX));
            Assert.AreEqual("old windows bootstrap", File.ReadAllText(windowsFile));
            File.WriteAllText(Path.Combine(root, "steamcmd.sh"), "script");
            File.WriteAllText(Path.Combine(root, "steamcmd"), "mac binary");
            Assert.AreEqual(root, SteamCmdService.ResolveInstallDirectory(root, OSPlatform.OSX));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void Versions_ShouldOnlyReadTheSelectedDepotEvenWhenIdsAreNotConsecutive()
    {
        const string output = """
        "depots"
        {
          "413151" { "manifests" { "public" { "gid" "111" } } }
          "413152" { "name" "value with } brace" "manifests" {
             "public" { "size" "10" "gid" "222" }
             "legacy_1.6.8" { "gid" "333" }
          } }
          "413170" { "manifests" { "public" { "gid" "999" } } }
        }
        """;
        var mac = SteamCmdService.ParseDepotVersions(output, 413152);
        CollectionAssert.AreEquivalent(new[] { "222", "333" }, mac.Select(item => item.ManifestId).ToArray());
        Assert.AreEqual("旧版 1.6.8", mac.Single(item => item.ManifestId == "333").DisplayName);
        var windows = SteamCmdService.ParseDepotVersions(output, 413151);
        Assert.AreEqual("111", windows.Single().ManifestId);
        Assert.AreEqual(0, SteamCmdService.ParseDepotVersions(output, 413153).Count);
    }

    [TestMethod]
    public void GameCache_ShouldSeparatePlatformsAndPreserveLegacyWindowsPath()
    {
        var win = SteamCmdService.GetGameCacheDir(null, OSPlatform.Windows);
        var mac = SteamCmdService.GetGameCacheDir(null, OSPlatform.OSX);
        var linux = SteamCmdService.GetGameCacheDir(null, OSPlatform.Linux);
        Assert.AreEqual(Path.Combine(SteamCmdService.GameCacheRoot, "latest"), win);
        Assert.AreEqual(Path.Combine(SteamCmdService.GameCacheRoot, "macos", "latest"), mac);
        Assert.AreEqual(Path.Combine(SteamCmdService.GameCacheRoot, "linux", "latest"), linux);
    }

    [TestMethod]
    public void DepotCompletion_ShouldSelectReportedDirectoryAndRejectWrongManifestOrPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-depot-path-" + Guid.NewGuid().ToString("N"));
        try
        {
            var candidates = SteamCmdService.GetDepotContentCandidates(root, 413153, OSPlatform.Linux);
            var oldPath = candidates[0];
            var linuxPath = candidates[1];
            Directory.CreateDirectory(oldPath);
            Directory.CreateDirectory(linuxPath);
            File.WriteAllText(Path.Combine(oldPath, "old-game"), "old");
            File.WriteAllText(Path.Combine(linuxPath, "game"), "current");
            var reported = OperatingSystem.IsWindows() ? linuxPath : linuxPath.Replace('/', '\\');
            var output = $"Depot download complete : \"{reported}\" (manifest 12345)";

            Assert.AreEqual(linuxPath, SteamCmdService.ResolveCompletedDepotPath(output, candidates, "12345", out _));
            Assert.IsNull(SteamCmdService.ResolveCompletedDepotPath(output, candidates, "99999", out _));
            Assert.IsNull(SteamCmdService.ResolveCompletedDepotPath("Error! Download failed", candidates, null, out _));
            var outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "game"), "unrelated");
            Assert.IsNull(SteamCmdService.ResolveCompletedDepotPath(
                $"Depot download complete : \"{outside}\" (manifest 12345)", candidates, "12345", out _));
            Assert.AreEqual("old", File.ReadAllText(Path.Combine(oldPath, "old-game")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task LinuxDepotDownload_ShouldCopyReportedLinux32FilesAndPreserveExistingDepot()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("验证 Linux SteamCMD 的真实目录布局"); return; }
        var root = Path.Combine(Path.GetTempPath(), "svl-depot-linux-" + Guid.NewGuid().ToString("N"));
        var steam = Path.Combine(root, "steam cmd");
        var depot = Path.Combine(steam, "linux32", "steamapps", "content", "app_413150", "depot_413153");
        var target = Path.Combine(root, "chosen game path");
        var manifest = Random.Shared.NextInt64(8_000_000_000_000_000_000L, 9_000_000_000_000_000_000L).ToString();
        var cache = SteamCmdService.GetGameCacheDir(manifest, OSPlatform.Linux);
        try
        {
            Directory.CreateDirectory(depot);
            File.WriteAllText(Path.Combine(steam, "linux32", "steamcmd"), "test executable");
            File.WriteAllText(Path.Combine(depot, "StardewValley"), "cached game");
            File.SetUnixFileMode(Path.Combine(depot, "StardewValley"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var reported = depot.Replace('/', '\\');
            var message = $"Depot download complete : \"{reported}\" (manifest {manifest})".Replace("'", "'\\''");
            var script = Path.Combine(steam, "steamcmd.sh");
            await File.WriteAllTextAsync(script, $"#!/bin/sh\nprintf '%s\\n' '{message}'\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var settings = new AppUserSettingsStore(Path.Combine(root, "settings"));
            var service = new SteamCmdService(new HttpDownloadService(settings), steam, Path.Combine(root, "trace.log"));

            var result = await service.DownloadGameDepotAsync("test-account", manifest, target);

            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual("cached game", File.ReadAllText(Path.Combine(target, "StardewValley")));
            Assert.AreEqual("cached game", File.ReadAllText(Path.Combine(depot, "StardewValley")));
            Assert.AreEqual("cached game", File.ReadAllText(Path.Combine(cache, "StardewValley")));
            Assert.IsTrue((File.GetUnixFileMode(Path.Combine(target, "StardewValley")) & UnixFileMode.UserExecute) != 0);
        }
        finally
        {
            if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task LinuxDepotDownload_ShouldNotTreatOldFilesAsSuccessfulWhenSteamReportsFailure()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("验证 Linux SteamCMD 的真实目录布局"); return; }
        var root = Path.Combine(Path.GetTempPath(), "svl-depot-failure-" + Guid.NewGuid().ToString("N"));
        var steam = Path.Combine(root, "steamcmd");
        var depot = Path.Combine(steam, "linux32", "steamapps", "content", "app_413150", "depot_413153");
        var target = Path.Combine(root, "target");
        var manifest = Random.Shared.NextInt64(8_000_000_000_000_000_000L, 9_000_000_000_000_000_000L).ToString();
        var cache = SteamCmdService.GetGameCacheDir(manifest, OSPlatform.Linux);
        try
        {
            Directory.CreateDirectory(depot);
            File.WriteAllText(Path.Combine(steam, "linux32", "steamcmd"), "test executable");
            File.WriteAllText(Path.Combine(depot, "previous-game"), "keep old game");
            var script = Path.Combine(steam, "steamcmd.sh");
            await File.WriteAllTextAsync(script, "#!/bin/sh\nprintf '%s\\n' 'Error! Depot download failed'\nexit 0\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var settings = new AppUserSettingsStore(Path.Combine(root, "settings"));
            var service = new SteamCmdService(new HttpDownloadService(settings), steam, Path.Combine(root, "trace.log"));

            var result = await service.DownloadGameDepotAsync("test-account", manifest, target);

            Assert.IsFalse(result.Success);
            Assert.AreEqual("keep old game", File.ReadAllText(Path.Combine(depot, "previous-game")));
            Assert.IsFalse(Directory.Exists(target));
            Assert.IsFalse(Directory.Exists(cache));
        }
        finally
        {
            if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void UnixArchive_ShouldRejectTraversalOutsideTheFreshDestination()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-steamcmd-traversal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var archive = Path.Combine(root, "fixture.tar.gz");
            WriteArchive(archive, ("../escaped.txt", "unexpected"));
            try
            {
                SteamCmdService.ExtractUnixBootstrap(archive, Path.Combine(root, "payload"));
                Assert.Fail("不应解压到目标目录之外");
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException) { }
            Assert.IsFalse(File.Exists(Path.Combine(root, "escaped.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task ExistingUnixInstallDirectory_ShouldNotBeOverwritten()
    {
        if (OperatingSystem.IsWindows()) { Assert.Inconclusive("仅适用于 Unix 安装目录"); return; }
        var root = Path.Combine(Path.GetTempPath(), "svl-steamcmd-preserve-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "steamcmd");
        Directory.CreateDirectory(target);
        try
        {
            var original = Path.Combine(target, "existing.txt");
            File.WriteAllText(original, "keep");
            var store = new AppUserSettingsStore(Path.Combine(root, "settings"));
            store.Save(new AppUserSettings { EnableDownloadCache = false });
            var service = new SteamCmdService(new HttpDownloadService(store), target);
            var error = await Assert.ThrowsExceptionAsync<IOException>(() => service.EnsureSteamCmdAsync());
            StringAssert.Contains(error.Message, "保留原有文件");
            Assert.AreEqual("keep", File.ReadAllText(original));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void UnixArchive_ShouldExtractScriptAndExecutable()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-steamcmd-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var archive = Path.Combine(root, "fixture.tar.gz");
            WriteArchive(archive, ("steamcmd.sh", "#!/bin/sh\n"), ("steamcmd", "binary"));
            var output = Path.Combine(root, "payload");
            SteamCmdService.ExtractUnixBootstrap(archive, output);
            Assert.AreEqual("#!/bin/sh\n", File.ReadAllText(Path.Combine(output, "steamcmd.sh")));
            Assert.AreEqual("binary", File.ReadAllText(Path.Combine(output, "steamcmd")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void UnixGameCopy_ShouldPreserveExecutableModeAndInternalLinks()
    {
        if (OperatingSystem.IsWindows()) { Assert.Inconclusive("符号链接和 Unix 权限需要 Unix 系统"); return; }
        var root = Path.Combine(Path.GetTempPath(), "svl-steamcmd-copy-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(Path.Combine(source, "resources"));
        try
        {
            File.WriteAllText(Path.Combine(source, "game"), "executable");
            File.SetUnixFileMode(Path.Combine(source, "game"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.WriteAllText(Path.Combine(source, "resources", "content"), "data");
            Directory.CreateSymbolicLink(Path.Combine(source, "linked"), "resources");

            SteamCmdService.CopyDirectoryContents(source, output);
            Assert.AreEqual("resources", new DirectoryInfo(Path.Combine(output, "linked")).LinkTarget);
            Assert.AreEqual("data", File.ReadAllText(Path.Combine(output, "linked", "content")));
            Assert.IsTrue((File.GetUnixFileMode(Path.Combine(output, "game")) & UnixFileMode.UserExecute) != 0);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void UnixGameCopy_ShouldNotFollowExternalLinks()
    {
        if (OperatingSystem.IsWindows()) { Assert.Inconclusive("符号链接需要 Unix 系统"); return; }
        var root = Path.Combine(Path.GetTempPath(), "svl-steamcmd-escape-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(source, "external"), "..");
            Assert.ThrowsException<InvalidDataException>(() => SteamCmdService.CopyDirectoryContents(source, Path.Combine(root, "out")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void WriteArchive(string path, params (string Name, string Content)[] entries)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionMode.Compress);
        using var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false);
        foreach (var (name, content) in entries)
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content))
            };
            writer.WriteEntry(entry);
        }
    }
}
