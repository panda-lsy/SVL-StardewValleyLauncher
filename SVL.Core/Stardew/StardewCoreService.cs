using System.Threading.Tasks;
using SVL.Core.App;
using SVL.Core.App.Configuration;
using SVL.Core.IO;
using SVL.Core.Logging;

namespace SVL.Core.Stardew;

[LifecycleService(LifecycleState.Loading, Priority = 1919810)]
[LifecycleScope("stardew", "星露谷核心")]
public sealed partial class StardewCoreService
{
    [LifecycleStart]
    private static void Start()
    {
        ConfigService.Initialize();
        FileService.Initialize();

        Log.Info("Stardew Valley核心服务已启动");
    }

    [LifecycleStop]
    private static void Stop()
    {
        FileService.Shutdown();
        Log.Info("Stardew Valley核心服务已停止");
    }
}
