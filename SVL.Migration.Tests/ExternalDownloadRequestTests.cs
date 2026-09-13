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

    [TestMethod]
    public void ResolveSuggestedFileName_ShouldStripLegacyNexusFilePrefix()
    {
        var request = new ExternalDownloadRequest
        {
            ResourceName = "Content Patcher",
            SelectedDownloadOption = "File 7448774_ Content Patcher 2.9.0 2.9.0.zip"
        };

        Assert.AreEqual(
            "Content Patcher 2.9.0.zip",
            request.ResolveSuggestedFileName());
    }

    [TestMethod]
    public void ResolveSuggestedFileName_ShouldStripLegacyNexusFilePrefixBeforeUrl()
    {
        var request = new ExternalDownloadRequest
        {
            ResourceName = "Content Patcher",
            SelectedDownloadOption = "File 7448774_ Content Patcher.zip | https://example.invalid/download"
        };

        Assert.AreEqual(
            "Content Patcher.zip",
            request.ResolveSuggestedFileName());
    }

    [TestMethod]
    public void ResolveSuggestedFileName_ShouldKeepGeneratedTokenWithoutReadableName()
    {
        var request = new ExternalDownloadRequest
        {
            ResourceName = "Content Patcher",
            SelectedDownloadOption = "cf-1012214-5312529.zip"
        };

        Assert.AreEqual("cf-1012214-5312529.zip", request.ResolveSuggestedFileName());
    }

    [TestMethod]
    public void ResolveSuggestedFileName_ShouldUseFileNameFromDirectUrl()
    {
        var request = new ExternalDownloadRequest
        {
            ResourceName = "Content Patcher",
            SelectedDownloadOption = "https://cdn.example.invalid/files/Content%20Patcher.zip"
        };

        Assert.AreEqual("Content Patcher.zip", request.ResolveSuggestedFileName());
    }

    [TestMethod]
    public void ResolveSuggestedFileName_ShouldDecodeEncodedLegacyFileName()
    {
        var request = new ExternalDownloadRequest
        {
            ResourceName = "Content Patcher",
            SelectedDownloadOption = "File%207448774_%20Content%20Patcher%202.9.0%202.9.0.zip%20|%20https://example.invalid/download"
        };

        Assert.AreEqual(
            "Content Patcher 2.9.0.zip",
            request.ResolveSuggestedFileName());
    }

    [TestMethod]
    public void ResolveSuggestedFileName_ShouldFallbackWhenDirectUrlHasNoFileName()
    {
        var request = new ExternalDownloadRequest
        {
            ResourceName = "Content Patcher",
            SelectedDownloadOption = "https://cdn.example.invalid/download?id=123"
        };

        Assert.AreEqual("Content Patcher", request.ResolveSuggestedFileName());
    }
}
