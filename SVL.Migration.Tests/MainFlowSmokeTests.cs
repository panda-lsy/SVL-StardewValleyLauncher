using Microsoft.VisualStudio.TestTools.UnitTesting;
using SVL.Avalonia.Models;
using SVL.Avalonia.Services;
using SVL.Avalonia.ViewModels;

namespace SVL.Migration.Tests;

[TestClass]
[DoNotParallelize]
public class MainFlowSmokeTests
{
    [TestMethod]
    public void CollectionTaskStatus_ShouldExposeCurrentModAndClearItAfterCompletion()
    {
        var taskStatus = new TaskStatusPageViewModel();
        var task = new DownloadTaskItem
        {
            Name = "测试 Collection",
            TaskState = DownloadTaskState.Installing,
            Status = "Collection 安装中"
        };

        taskStatus.SetCurrentTask(task);
        task.SyncCollectionModProgress(
            "测试 Mod",
            phase: 2,
            optional: true,
            state: CollectionModTaskState.Downloading,
            message: "正在处理");

        Assert.IsTrue(taskStatus.SelectedTaskHasCurrentCollectionMod);
        StringAssert.Contains(taskStatus.SelectedTaskCurrentCollectionModText, "测试 Mod");
        StringAssert.Contains(taskStatus.SelectedTaskCurrentCollectionModText, "Phase 2");
        StringAssert.Contains(taskStatus.SelectedTaskCurrentCollectionModText, "可选");

        task.SyncCollectionModProgress(
            "测试 Mod",
            phase: 2,
            optional: true,
            state: CollectionModTaskState.Installed,
            message: "安装成功");

        Assert.IsFalse(taskStatus.SelectedTaskHasCurrentCollectionMod);
    }

    [TestMethod]
    public void CollectionManualSource_ShouldOfferLocalArchiveRecoveryOnlyForFailedOrSkippedItems()
    {
        var item = new CollectionModTaskItem
        {
            Name = "手动来源 Mod",
            RequiresManualAction = true,
            SourceUrl = "https://www.nexusmods.com/stardewvalley/mods/123"
        };

        Assert.IsFalse(item.CanInstallFromLocal);

        item.State = CollectionModTaskState.Failed;
        Assert.IsTrue(item.CanInstallFromLocal);

        item.State = CollectionModTaskState.Installed;
        Assert.IsFalse(item.CanInstallFromLocal);

        item.State = CollectionModTaskState.Skipped;
        Assert.IsTrue(item.CanInstallFromLocal);

        item.RequiresManualAction = false;
        Assert.IsFalse(item.CanInstallFromLocal);
    }

    [TestMethod]
    public void CollectionOptionalFailure_ShouldRequireExplicitDecisionAndPersistState()
    {
        var item = new CollectionModTaskItem
        {
            Name = "可选但下载失败的 Mod",
            Optional = true,
            RequiresManualAction = true,
            State = CollectionModTaskState.NeedsDecision,
            Message = "请补充来源"
        };

        Assert.IsFalse(item.IsFinished);
        Assert.IsTrue(item.CanInstallFromLocal);
        Assert.IsTrue(item.CanSkipOptional);
        Assert.AreEqual("待处理", item.DisplayStateText);

        var statePath = Path.Combine(
            Path.GetTempPath(),
            "svl-collection-decision-" + Guid.NewGuid().ToString("N") + ".json.gz");
        var task = new DownloadTaskItem
        {
            Name = "待处理 Collection",
            TaskKind = DownloadTaskKind.NexusCollection,
            TaskAction = DownloadTaskAction.InstallCollection
        };
        task.CollectionModItems.Add(item);
        var store = new DownloadTaskStateStore();

        try
        {
            store.Save(statePath, [task]);
            var records = store.Load(statePath, out var corruptedBackupPath);

            Assert.AreEqual(string.Empty, corruptedBackupPath);
            Assert.AreEqual(1, records.Count);
            Assert.AreEqual(CollectionModTaskState.NeedsDecision, records[0].CollectionModItems[0].State);
            Assert.IsTrue(records[0].CollectionModItems[0].Optional);
        }
        finally
        {
            try
            {
                if (File.Exists(statePath))
                {
                    File.Delete(statePath);
                }
            }
            catch
            {
                // 测试清理失败不应覆盖状态持久化断言结果。
            }
        }

        item.State = CollectionModTaskState.Skipped;
        item.RequiresManualAction = false;
        Assert.IsTrue(item.IsFinished);
        Assert.IsFalse(item.CanSkipOptional);
    }

    [TestMethod]
    public void TaskStatus_ShouldExposeExplicitSkipActionForOptionalCollectionMod()
    {
        var taskStatus = new TaskStatusPageViewModel();
        var task = new DownloadTaskItem
        {
            Name = "需要处理可选项的 Collection",
            TaskAction = DownloadTaskAction.InstallCollection,
            TaskState = DownloadTaskState.Failed,
            Status = "部分完成"
        };
        var item = new CollectionModTaskItem
        {
            Name = "待确认可选 Mod",
            Optional = true,
            RequiresManualAction = true,
            State = CollectionModTaskState.NeedsDecision
        };
        task.CollectionModItems.Add(item);
        taskStatus.SetCurrentTask(task);

        DownloadTaskItem requestedTask = task;
        CollectionModTaskItem requestedItem = item;
        taskStatus.SkipCollectionModRequested += (receivedTask, receivedItem) =>
        {
            requestedTask = receivedTask;
            requestedItem = receivedItem;
        };

        taskStatus.SkipCollectionModCommand.Execute(item);

        Assert.AreSame(task, requestedTask);
        Assert.AreSame(item, requestedItem);
    }

    [TestMethod]
    public async Task MainFlow_ShouldNavigateThroughCorePages_AndQueueTask()
    {
        var settingsStore = new AppUserSettingsStore();
        var settingsPath = settingsStore.GetSettingsPath();
        var hasOriginalSettings = File.Exists(settingsPath);
        var originalSettingsJson = hasOriginalSettings ? File.ReadAllText(settingsPath) : null;

        var tempInstancePath = Path.Combine(Path.GetTempPath(), "svl-smoke-instance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempInstancePath);
        Directory.CreateDirectory(Path.Combine(tempInstancePath, "Mods"));
        File.WriteAllText(Path.Combine(tempInstancePath, "StardewModdingAPI"), string.Empty);

        var enabledModPath = Path.Combine(tempInstancePath, "Mods", "QualitySprinklers");
        Directory.CreateDirectory(enabledModPath);
        File.WriteAllText(
            Path.Combine(enabledModPath, "manifest.json"),
            "{\"Name\":\"Quality Sprinklers\",\"Version\":\"1.0.0\"}");

        var disabledModPath = Path.Combine(tempInstancePath, "Mods", "LookupAnything.disabled");
        Directory.CreateDirectory(disabledModPath);
        File.WriteAllText(
            Path.Combine(disabledModPath, "manifest.json"),
            "{\"Name\":\"Lookup Anything\",\"Version\":\"2.0.0\"}");

        // CurseForge/SVL 整合包中常见带 BOM 的 manifest.json；页面应和普通清单一样读取。
        var bomModPath = Path.Combine(tempInstancePath, "Mods", "BomManifestMod");
        Directory.CreateDirectory(bomModPath);
        File.WriteAllText(
            Path.Combine(bomModPath, "manifest.json"),
            "{\"Name\":\"BOM Manifest Mod\",\"Version\":\"3.0.0\"}",
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        // 少数旧 Mod 使用 UTF-16 BOM；版本列表不能因此退回“未知版本”。
        var utf16ModPath = Path.Combine(tempInstancePath, "Mods", "Utf16ManifestMod");
        Directory.CreateDirectory(utf16ModPath);
        File.WriteAllText(
            Path.Combine(utf16ModPath, "manifest.json"),
            "{\"Name\":\"UTF16 Manifest Mod\",\"Version\":\"4.0.0\"}",
            System.Text.Encoding.Unicode);

        try
        {
            settingsStore.Save(new AppUserSettings
            {
                InstanceName = "SmokeInstance",
                PreferredInstancePath = tempInstancePath,
                PreferredLaunchMode = "SMAPI",
                EnableSafeLaunch = false
            });

            var mainWindow = new MainWindowViewModel();

            Assert.AreEqual("启动", mainWindow.CurrentPage);

            mainWindow.LaunchPage.NavigateToVersionSelectCommand.Execute(null);
            Assert.AreEqual("实例", mainWindow.CurrentPage);

            mainWindow.NavigateBackCommand.Execute(null);
            Assert.AreEqual("启动", mainWindow.CurrentPage);

            mainWindow.LaunchPage.OpenModManageCommand.Execute(null);
            Assert.AreEqual("版本设置", mainWindow.CurrentPage);
            Assert.IsTrue(mainWindow.VersionSettingsPage.IsModManageSection);

            var modPage = mainWindow.VersionSettingsPage;
            Assert.IsTrue(modPage.HasMods);
            Assert.AreEqual(4, modPage.Mods.Count);
            var bomMod = modPage.Mods.Single(item => string.Equals(item.DisplayName, "BOM Manifest Mod", StringComparison.Ordinal));
            Assert.AreEqual("3.0.0", bomMod.Version);
            var utf16Mod = modPage.Mods.Single(item => string.Equals(item.DisplayName, "UTF16 Manifest Mod", StringComparison.Ordinal));
            Assert.AreEqual("4.0.0", utf16Mod.Version);

            var disabledMod = modPage.Mods.First(item => !item.IsEnabled);
            modPage.SelectedMod = disabledMod;
            modPage.EnableSelectedModCommand.Execute(null);
            Assert.IsTrue(disabledMod.IsEnabled);
            StringAssert.Contains(disabledMod.UpdateStatus, "已启用");
            Assert.IsTrue(Directory.Exists(Path.Combine(tempInstancePath, "Mods", "LookupAnything")));

            var enabledMod = modPage.Mods.First(item => item.IsEnabled &&
                string.Equals(item.DisplayName, "Quality Sprinklers", StringComparison.Ordinal));
            modPage.SelectedMod = enabledMod;
            modPage.DisableSelectedModCommand.Execute(null);
            Assert.IsFalse(enabledMod.IsEnabled);
            StringAssert.Contains(enabledMod.UpdateStatus, "已禁用");
            var disabledCandidates = new[]
            {
                Path.Combine(tempInstancePath, "Mods", ".QualitySprinklers"),
                Path.Combine(tempInstancePath, "Mods", ".QualitySprinklers."),
                Path.Combine(tempInstancePath, "Mods", "QualitySprinklers.disabled")
            };
            Assert.IsTrue(disabledCandidates.Any(Directory.Exists));

            modPage.SelectedMod = modPage.Mods.First();
            modPage.CheckUpdateSelectedModCommand.Execute(null);
            var updateStatus = modPage.SelectedMod!.UpdateStatus;
            var hasCheckedStatus = updateStatus.Contains("已检查", StringComparison.Ordinal) ||
                                   updateStatus.Contains("缺少来源信息", StringComparison.Ordinal);
            Assert.IsTrue(hasCheckedStatus, $"Unexpected update status: {updateStatus}");

            var beforeUninstallCount = modPage.Mods.Count;
            modPage.SelectedMod = modPage.Mods.First(item => string.Equals(item.DisplayName, "Lookup Anything", StringComparison.Ordinal));
            modPage.UninstallSelectedModCommand.Execute(null);
            Assert.AreEqual(beforeUninstallCount - 1, modPage.Mods.Count);
            StringAssert.Contains(modPage.ModManageHint, "已卸载 Lookup Anything");

            mainWindow.NavigateToDownloadCommand.Execute(null);
            Assert.AreEqual("下载", mainWindow.CurrentPage);

            var taskCountBeforeUnresolvedDownload = mainWindow.DownloadPage.DownloadTasks.Count;
            var queued = await mainWindow.DownloadPage.AddTaskFromExternalAsync(new ExternalDownloadRequest
            {
                ResourceName = "Smoke Mod",
                ResourceSource = "NexusMods",
                SelectedDownloadOption = "v1.0.0"
            });

            Assert.IsFalse(queued);
            Assert.AreEqual(taskCountBeforeUnresolvedDownload, mainWindow.DownloadPage.DownloadTasks.Count);

            mainWindow.NavigateToTasksCommand.Execute(null);
            Assert.AreEqual("任务", mainWindow.CurrentPage);
            Assert.IsTrue(mainWindow.IsTasksPage);
        }
        finally
        {
            if (hasOriginalSettings)
            {
                File.WriteAllText(settingsPath, originalSettingsJson!);
            }
            else if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }

            if (Directory.Exists(tempInstancePath))
            {
                Directory.Delete(tempInstancePath, true);
            }
        }
    }
}
