using System;
using SVL.Core.IO;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using SVL.Core.Logging;
using SVL.Core.Stardew.Mod.SMAPI;
using SVL.Core.Stardew.Instance;

namespace SVL.Core.Stardew.Launch;

public class LaunchOrchestrator
{
    public static async Task<bool> LaunchInstanceAsync(IStardewInstance instance, string? customArguments = null)
    {
        try
        {
            if (!Directory.Exists(instance.Path))
            {
                Logging.Log.Error($"Instance path does not exist: {instance.Path}");
                return false;
            }

            var smapiInstalled = await SmapApiService.CheckInstalledVersionAsync(instance.Path);
            if (!smapiInstalled)
            {
                Logging.Log.Warn("SMAPI not installed, installing...");
                var success = await SmapApiService.InstallAsync(instance.Path);
                if (!success)
                {
                    Logging.Log.Error("Failed to install SMAPI");
                    return false;
                }
            }

            Logging.Log.Info($"Launching instance: {instance.Name}");
            var process = await SmapApiService.LaunchGameAsync(instance.Path, Path.Combine(instance.Path, "Mods"), customArguments);

            var configKey = System.IO.Path.GetFileName(instance.Path);
            await UpdateLastPlayedAsync(configKey);

            Logging.Log.Info($"Game launched successfully (PID: {process.Id})");
            return true;
        }
        catch (Exception ex)
        {
            Logging.Log.Error(ex, $"Failed to launch instance: {instance.Name}");
            return false;
        }
    }

    private static async Task UpdateLastPlayedAsync(string instanceId)
    {
        try
        {
            var configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SVL",
                "instances",
                $"{instanceId}.json"
            );

            var instanceInfo = new StardewInstanceInfo { LastPlayed = DateTime.Now };
            var json = System.Text.Json.JsonSerializer.Serialize(instanceInfo);
            await FileEx.WriteAllTextAsync(configPath, json);
        }
        catch (Exception ex)
        {
            Logging.Log.Error(ex, "Failed to update last played time");
        }
    }
}
