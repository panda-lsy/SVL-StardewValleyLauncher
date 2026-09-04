using Microsoft.VisualStudio.TestTools.UnitTesting;
using SVL.Core.Platform.Abstractions;

namespace SVL.Migration.Tests;

[TestClass]
public class NxmLinkParserTests
{
    [TestMethod]
    public void Parse_ValidModLink_ShouldSucceed()
    {
        var parser = new NxmLinkParser();
        var ok = parser.TryParse(
            "nxm://stardewvalley/mods/2400/files/12000?key=abc&expires=1910000000&user_id=123",
            out var info,
            out var error);

        Assert.IsTrue(ok, error);
        Assert.AreEqual("stardewvalley", info.GameDomain);
        Assert.AreEqual(2400, info.ModId);
        Assert.AreEqual(12000, info.FileId);
        Assert.AreEqual("abc", info.Key);
        Assert.AreEqual(1910000000, info.Expires);
        Assert.AreEqual(123, info.UserId);
    }

    [TestMethod]
    public void Parse_InvalidScheme_ShouldFail()
    {
        var parser = new NxmLinkParser();
        var ok = parser.TryParse(
            "https://example.com/mods/1/files/2",
            out _,
            out var error);

        Assert.IsFalse(ok);
        Assert.AreEqual("仅支持 nxm:// 协议链接", error);
    }

    [TestMethod]
    public void Parse_ValidCollectionLink_ShouldSucceed()
    {
        var parser = new NxmLinkParser();
        var ok = parser.TryParse(
            "nxm://stardewvalley/collections/demo/revisions/latest",
            out var info,
            out var error);

        Assert.IsTrue(ok, error);
        Assert.AreEqual(NxmResourceType.Collection, info.ResourceType);
        Assert.AreEqual("stardewvalley", info.GameDomain);
        Assert.AreEqual("demo", info.CollectionSlug);
        Assert.AreEqual(-1, info.RevisionNumber);
    }

    [TestMethod]
    public void Parse_UnsupportedPath_ShouldFail()
    {
        var parser = new NxmLinkParser();
        var ok = parser.TryParse(
            "nxm://stardewvalley/foo/bar",
            out _,
            out var error);

        Assert.IsFalse(ok);
        StringAssert.Contains(error, "仅支持两类链接");
    }
}