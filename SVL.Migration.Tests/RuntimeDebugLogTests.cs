using Avalonia.Headless;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SVL.Avalonia.Services;
using SVL.Core.Platform.Diagnostics;

namespace SVL.Migration.Tests;

[TestClass]
[DoNotParallelize]
public sealed class RuntimeDebugLogTests
{
    [TestMethod]
    public async Task RuntimeDiagnostics_ShouldFollowTheSettingInReleaseBuilds()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AvaloniaUiSmokeTests).Assembly);
        await session.Dispatch(() =>
        {
            var marker = "runtime-debug-" + Guid.NewGuid().ToString("N");
            try
            {
                DebugTraceBootstrapper.SetEnabled(false);
                RuntimeDebugLog.WriteLine(marker + "-off");
                Assert.IsFalse(DebugConsoleService.Instance.Snapshot().Any(x => x.Text.Contains(marker + "-off")));

                DebugTraceBootstrapper.SetEnabled(true);
                RuntimeDebugLog.WriteLine(marker + "-on");
                Assert.IsTrue(DebugConsoleService.Instance.Snapshot().Any(x => x.Text.Contains(marker + "-on")));

                DebugTraceBootstrapper.SetEnabled(false);
                RuntimeDebugLog.WriteLine(marker + "-disabled");
                Assert.IsFalse(DebugConsoleService.Instance.Snapshot().Any(x => x.Text.Contains(marker + "-disabled")));
            }
            finally { DebugTraceBootstrapper.SetEnabled(false); }
        }, CancellationToken.None);
    }
}
