using Microsoft.VisualStudio.TestTools.UnitTesting;
using SVL.Avalonia.Models;
using SVL.Avalonia.Services;

namespace SVL.Migration.Tests;

[TestClass]
public sealed class AvaloniaMigrationHardeningTests
{
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
                Status = "下载失败（可重试）"
            };

            var store = new DownloadTaskStateStore();
            store.Save(statePath, [task]);
            var records = store.Load(statePath, out var brokenBackupPath);

            Assert.IsTrue(string.IsNullOrEmpty(brokenBackupPath));
            Assert.AreEqual(1, records.Count);
            Assert.AreEqual(DownloadTaskState.Failed, records[0].TaskState);
            Assert.AreEqual(2400, records[0].SourceModId);
            Assert.AreEqual(898372, records[0].SourceFileId);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
