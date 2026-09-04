using Microsoft.VisualStudio.TestTools.UnitTesting;
using SVL.Core.Platform.Services;

namespace SVL.Migration.Tests;

[TestClass]
public class NxmProtocolRegistrationServiceTests
{
    [TestMethod]
    public void GetStatus_ShouldReturnUnsupported_OnNonWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("该用例仅验证非 Windows 平台行为。");
            return;
        }

        var service = new NxmProtocolRegistrationService();
        var status = service.GetStatus();

        Assert.IsTrue(status.IsSuccess);
        Assert.IsFalse(status.IsSupported);
        Assert.IsFalse(status.IsRegistered);
    }

    [TestMethod]
    public void TryRegister_ShouldReturnUnsupported_OnNonWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("该用例仅验证非 Windows 平台行为。");
            return;
        }

        var service = new NxmProtocolRegistrationService();
        var result = service.TryRegister("/tmp/svl");

        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse(result.IsSupported);
        Assert.IsFalse(result.IsRegistered);
    }
}