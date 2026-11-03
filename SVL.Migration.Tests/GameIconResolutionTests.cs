using Microsoft.VisualStudio.TestTools.UnitTesting;
using SVL.Avalonia.Services;
using SVL.Avalonia.ViewModels;

namespace SVL.Migration.Tests;

[TestClass]
public sealed class GameIconResolutionTests
{
    [TestMethod]
    public void ModToggleAction_ShouldTrackTheActionAvailableForTheCurrentState()
    {
        var item = new ModManageItem { IsEnabled = true };
        var notified = false;
        item.PropertyChanged += (_, args) => notified |= args.PropertyName == nameof(ModManageItem.ToggleActionText);
        Assert.AreEqual("禁用", item.ToggleActionText);
        item.IsEnabled = false;
        Assert.AreEqual("启用", item.ToggleActionText);
        Assert.IsTrue(notified);
    }

    [TestMethod]
    public void GeneratedSmapiIcon_ShouldUseCurrentDefaultWithoutOverwritingCustomImages()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-icon-display-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var generated = Path.Combine(root, ".svl-instance-icon-smapi.png");
            var marker = Path.Combine(root, ".svl-instance-icon-smapi.generated");
            var custom = Path.Combine(root, ".svl-instance-icon.png");
            File.WriteAllText(generated, "previous generated image");
            File.WriteAllText(marker, "generated-by-svl");

            Assert.AreEqual(generated, InstanceIconResolver.ResolveIconPath(root, true));
            Assert.AreEqual(string.Empty, InstanceIconResolver.ResolveDisplayIconPath(root, true));
            Assert.AreEqual("previous generated image", File.ReadAllText(generated));

            File.WriteAllText(custom, "user selected image");
            Assert.AreEqual(custom, InstanceIconResolver.ResolveDisplayIconPath(root, true));
            Assert.AreEqual(custom, InstanceIconResolver.ResolveDisplayIconPath(root, false));

            File.Delete(marker);
            Assert.AreEqual(generated, InstanceIconResolver.ResolveDisplayIconPath(root, true),
                "没有自动生成标记时，SMAPI 图标可能是用户主动选择的，必须保留");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
