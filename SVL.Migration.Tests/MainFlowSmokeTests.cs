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
            Assert.AreEqual(2, modPage.Mods.Count);

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

            var queued = await mainWindow.DownloadPage.AddTaskFromExternalAsync(new ExternalDownloadRequest
            {
                ResourceName = "Smoke Mod",
                ResourceSource = "NexusMods",
                SelectedDownloadOption = "v1.0.0"
            });

            Assert.IsFalse(queued);
            Assert.AreEqual(0, mainWindow.DownloadPage.DownloadTasks.Count);

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