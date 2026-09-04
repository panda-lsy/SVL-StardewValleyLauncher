using SVL.Core.Platform.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SVL.Migration.Tests;

[TestClass]
public class PlatformInfoServiceTests
{
    [TestMethod]
    public void CurrentPlatform_ShouldNotBeUnknown_OnSupportedRuntime()
    {
        var service = new PlatformInfoService();
        var platform = service.CurrentPlatform;

        Assert.AreNotEqual(PlatformKind.Unknown, platform);
    }

    [TestMethod]
    public void PlatformDisplayName_ShouldReturnNonEmptyString()
    {
        var service = new PlatformInfoService();
        var display = service.GetPlatformDisplayName();

        Assert.IsFalse(string.IsNullOrWhiteSpace(display));
    }
}
