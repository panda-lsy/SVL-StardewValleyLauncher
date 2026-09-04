using Microsoft.VisualStudio.TestTools.UnitTesting;
using SVL.Core.Platform.Services;

namespace SVL.Migration.Tests;

[TestClass]
public class ExternalProcessAndExplorerTests
{
    [TestMethod]
    public void RunCommand_ShouldReturnZero_ForSimpleEchoLikeCommand()
    {
        var processService = new ExternalProcessService();

        var isMac = System.Runtime.InteropServices.RuntimeInformation
            .IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX);
        var isLinux = System.Runtime.InteropServices.RuntimeInformation
            .IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux);

        int exitCode;
        if (isMac || isLinux)
        {
            exitCode = processService.RunCommand("/bin/sh", "-c \"echo ok\"");
        }
        else
        {
            exitCode = processService.RunCommand("cmd.exe", "/c echo ok");
        }

        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public void TryOpenFolder_ShouldReturnFalse_WhenFolderMissing()
    {
        var processService = new ExternalProcessService();
        var explorerService = new FileExplorerService(processService);

        var result = explorerService.TryOpenFolder("/this/path/should/not/exist");
        Assert.IsFalse(result);
    }

    [TestMethod]
    public void TryRevealFile_ShouldReturnFalse_WhenFileMissing()
    {
        var processService = new ExternalProcessService();
        var explorerService = new FileExplorerService(processService);

        var result = explorerService.TryRevealFile("/this/path/should/not/exist.txt");
        Assert.IsFalse(result);
    }

    [TestMethod]
    public void TryLaunchProcess_ShouldReturnFalse_WhenExecutableMissing()
    {
        var processService = new ExternalProcessService();

        var result = processService.TryLaunchProcess("/this/path/definitely/missing-executable", string.Empty);

        Assert.IsFalse(result);
    }
}
