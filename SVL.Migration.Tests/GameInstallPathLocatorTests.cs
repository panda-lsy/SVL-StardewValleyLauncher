using SVL.Core.Platform.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SVL.Migration.Tests;

[TestClass]
public class GameInstallPathLocatorTests
{
    [TestMethod]
    public void SteamPathLookup_ShouldNotThrow()
    {
        var locator = new GameInstallPathLocator();
        _ = locator.TryLocateSteamStardewPath();
    }

    [TestMethod]
    public void GogPathLookup_ShouldNotThrow()
    {
        var locator = new GameInstallPathLocator();
        _ = locator.TryLocateGogStardewPath();
    }
}
