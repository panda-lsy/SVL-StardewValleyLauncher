using Microsoft.VisualStudio.TestTools.UnitTesting;
using SVL.Core.Platform.Services;

namespace SVL.Migration.Tests;

[TestClass]
public sealed class SmapiUpdateRecycleBinTests
{
    [TestMethod]
    public void TryMoveToRecycleBin_UsesRecoveryDirectoryOnNonWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("该用例验证非 Windows 平台的应用回收目录。");
            return;
        }

        var root = CreateTemporaryDirectory();
        var sourceFile = Path.Combine(root, $"svl-recycle-{Guid.NewGuid():N}.txt");
        var recoveryRoot = RecycleBinService.GetRecoveryRootPath();
        var recoveredFile = string.Empty;
        try
        {
            File.WriteAllText(sourceFile, "recoverable through public API");

            var moved = RecycleBinService.TryMoveToRecycleBin(sourceFile, out var message);

            Assert.IsTrue(moved, message);
            Assert.IsFalse(File.Exists(sourceFile));
            recoveredFile = Directory.GetFiles(recoveryRoot, $"*{Path.GetFileName(sourceFile)}_*")
                .SingleOrDefault();
            Assert.IsNotNull(recoveredFile, "回收站服务没有在应用恢复目录中留下文件。");
            Assert.AreEqual("recoverable through public API", File.ReadAllText(recoveredFile!));
        }
        finally
        {
            if (!string.IsNullOrEmpty(recoveredFile) && File.Exists(recoveredFile))
            {
                File.Delete(recoveredFile);
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void TryMoveToRecoveryDirectory_MovesFileAndPreservesContents()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var sourceDirectory = Path.Combine(root, "source");
            var recoveryDirectory = Path.Combine(root, "recovery");
            Directory.CreateDirectory(sourceDirectory);
            var sourceFile = Path.Combine(sourceDirectory, "runtime.dll");
            File.WriteAllText(sourceFile, "recoverable runtime data");

            var moved = RecycleBinService.TryMoveToRecoveryDirectory(sourceFile, recoveryDirectory, out var message);

            Assert.IsTrue(moved, message);
            Assert.IsFalse(File.Exists(sourceFile));
            var recoveredFile = Directory.GetFiles(recoveryDirectory).Single();
            Assert.AreEqual("recoverable runtime data", File.ReadAllText(recoveredFile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void CleanupRuntimeDirectoryForUpdate_RecyclesRuntimeAndBundledFilesButPreservesUserData()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var runtime = CreateRuntimeFixture(root);
            var recycled = Path.Combine(root, "recycled-runtime");

            SmapiInstallService.CleanupRuntimeDirectoryForUpdate(
                runtime,
                logger: null,
                moveToRecycleBin: path =>
                {
                    Directory.Move(path, recycled);
                    return (true, string.Empty);
                });

            Assert.IsFalse(File.Exists(Path.Combine(runtime, "StardewModdingAPI.dll")));
            Assert.IsFalse(Directory.Exists(Path.Combine(runtime, "Content")));
            Assert.IsFalse(Directory.Exists(Path.Combine(runtime, "Mods", "ConsoleCommands")));
            Assert.IsTrue(File.Exists(Path.Combine(runtime, ".svl-instance-icon-smapi.png")));
            Assert.IsTrue(File.Exists(Path.Combine(runtime, "Mods", "UserMod", "manifest.json")));

            Assert.IsTrue(File.Exists(Path.Combine(recycled, "StardewModdingAPI.dll")));
            Assert.IsTrue(File.Exists(Path.Combine(recycled, "Content", "content.xnb")));
            Assert.IsTrue(File.Exists(Path.Combine(recycled, "Mods", "ConsoleCommands", "manifest.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void CleanupRuntimeDirectoryForUpdate_RestoresFilesAndAbortsWhenRecycleBinFails()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var runtime = CreateRuntimeFixture(root);

            var error = Assert.ThrowsException<IOException>(() =>
                SmapiInstallService.CleanupRuntimeDirectoryForUpdate(
                    runtime,
                    logger: null,
                    moveToRecycleBin: _ => (false, "simulated recycle-bin failure")));

            StringAssert.Contains(error.Message, "已恢复原文件");
            Assert.IsTrue(File.Exists(Path.Combine(runtime, "StardewModdingAPI.dll")));
            Assert.IsTrue(File.Exists(Path.Combine(runtime, "Content", "content.xnb")));
            Assert.IsTrue(File.Exists(Path.Combine(runtime, "Mods", "ConsoleCommands", "manifest.json")));
            Assert.IsTrue(File.Exists(Path.Combine(runtime, ".svl-instance-icon-smapi.png")));
            Assert.IsTrue(File.Exists(Path.Combine(runtime, "Mods", "UserMod", "manifest.json")));
            Assert.AreEqual(0, Directory.GetDirectories(root, ".svl-smapi-update-*", SearchOption.TopDirectoryOnly).Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRuntimeFixture(string root)
    {
        var runtime = Path.Combine(root, "runtime");
        var userMod = Path.Combine(runtime, "Mods", "UserMod");
        var bundledMod = Path.Combine(runtime, "Mods", "ConsoleCommands");
        Directory.CreateDirectory(Path.Combine(runtime, "Content"));
        Directory.CreateDirectory(userMod);
        Directory.CreateDirectory(bundledMod);
        File.WriteAllText(Path.Combine(runtime, "StardewModdingAPI.dll"), "old runtime");
        File.WriteAllText(Path.Combine(runtime, ".svl-instance-icon-smapi.png"), "custom icon");
        File.WriteAllText(Path.Combine(runtime, "Content", "content.xnb"), "old content");
        File.WriteAllText(Path.Combine(userMod, "manifest.json"), "user mod");
        File.WriteAllText(Path.Combine(bundledMod, "manifest.json"), "bundled mod");
        return runtime;
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"svl-smapi-recycle-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
