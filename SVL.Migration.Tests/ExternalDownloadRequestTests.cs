using Microsoft.VisualStudio.TestTools.UnitTesting;
using SVL.Avalonia.Models;

namespace SVL.Migration.Tests;

[TestClass]
public class ExternalDownloadRequestTests
{
    [TestMethod]
    public void ToTaskDisplayName_ShouldIncludeSourceAndOption_WhenBothProvided()
    {
        var request = new ExternalDownloadRequest
        {
            ResourceName = "UI Info Suite 2",
            ResourceSource = "NexusMods",
            SelectedDownloadOption = "v2.3.1"
        };

        var displayName = request.ToTaskDisplayName();

        Assert.AreEqual("[安装] UI Info Suite 2 [NexusMods] | v2.3.1", displayName);
    }

    [TestMethod]
    public void ToTaskDisplayName_ShouldSkipOption_WhenOptionMissing()
    {
        var request = new ExternalDownloadRequest
        {
            ResourceName = "Lookup Anything",
            ResourceSource = "Curseforge",
            SelectedDownloadOption = string.Empty
        };

        var displayName = request.ToTaskDisplayName();

        Assert.AreEqual("[安装] Lookup Anything [Curseforge]", displayName);
    }

    [TestMethod]
    public void ToTaskDisplayName_ShouldUseNameOnly_WhenSourceAndOptionMissing()
    {
        var request = new ExternalDownloadRequest
        {
            ResourceName = "SMAPI",
            ResourceSource = string.Empty,
            SelectedDownloadOption = string.Empty
        };

        var displayName = request.ToTaskDisplayName();

        Assert.AreEqual("[安装] SMAPI", displayName);
    }
}
