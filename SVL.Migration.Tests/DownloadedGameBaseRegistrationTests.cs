using Microsoft.VisualStudio.TestTools.UnitTesting;
using SVL.Avalonia.Services;
using SVL.Avalonia.ViewModels;

namespace SVL.Migration.Tests;

[TestClass]
public sealed class DownloadedGameBaseRegistrationTests
{
    [TestMethod]
    public void MacDepotRoot_ShouldRegisterRuntimeAndAvoidLegacyDuplicates()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-download-base-" + Guid.NewGuid().ToString("N"));
        var downloadRoot = Path.Combine(root, "未命名文件夹");
        var runtime = Path.Combine(downloadRoot, "Contents", "MacOS");
        Directory.CreateDirectory(runtime);
        File.WriteAllText(Path.Combine(runtime, "StardewValley"), "fixture");
        try
        {
            var store = new InstanceRegistryStore(Path.Combine(root, "registry"));
            var result = InstancesPageViewModel.RegisterDownloadedBase(store, downloadRoot);
            Assert.IsTrue(result.Success, result.Message);
            Assert.IsFalse(result.AlreadyRegistered);
            Assert.AreEqual(runtime, result.Path);
            var saved = store.LoadManualInstances();
            Assert.AreEqual(1, saved.Count);
            Assert.AreEqual(runtime, saved[0].Path);
            Assert.AreEqual("未命名文件夹", saved[0].Name);

            var duplicate = InstancesPageViewModel.RegisterDownloadedBase(store, runtime);
            Assert.IsTrue(duplicate.Success);
            Assert.IsTrue(duplicate.AlreadyRegistered);
            Assert.AreEqual(1, store.LoadManualInstances().Count);

            // A previous version may already have saved the parent that it could not display.
            store.SaveManualInstances([new ManualInstanceRecord { Name = "原有名称", Path = downloadRoot }]);
            var legacy = InstancesPageViewModel.RegisterDownloadedBase(store, downloadRoot);
            Assert.IsTrue(legacy.Success);
            Assert.IsTrue(legacy.AlreadyRegistered);
            Assert.AreEqual("原有名称", store.LoadManualInstances().Single().Name);
            Assert.AreEqual(runtime, InstancesPageViewModel.ResolveGameRootPath(downloadRoot));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void MissingGameFiles_ShouldNotRegisterAnInvalidBase()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-invalid-base-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new InstanceRegistryStore(Path.Combine(root, "registry"));
            var result = InstancesPageViewModel.RegisterDownloadedBase(store, root);
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Message.Contains("未在下载目录中找到游戏核心文件"));
            Assert.AreEqual(0, store.LoadManualInstances().Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
