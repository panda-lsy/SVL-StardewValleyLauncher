using System.IO;
using System.Text.Json.Serialization;
using SVL.Core.App.Configuration;
using SVL.Core.IO;

namespace SVL.Core.Stardew.Instance;

public class StardewInstance : IStardewInstance
{
    private readonly string _basePath;

    public string Path { get; private set; }

    public string Name { get; private set; }

    public StardewInstanceCardType CardType { get; set; }

    public string Description { get; set; } = string.Empty;

    public string Logo { get; set; } = string.Empty;

    public bool IsStarred { get; set; }

    [JsonIgnore]
    public StardewInstanceInfo InstanceInfo { get; set; } = new();

    public bool IsSMAPIInstance { get; set; }

    public bool EnableIsolation { get; set; }

    public StardewInstance(string instancePath, string basePath)
    {
        _basePath = basePath;
        Path = instancePath;
        Name = new DirectoryInfo(instancePath).Name;
    }

    public void Load()
    {
        var configPath = System.IO.Path.Combine(Path, "instance.json");
        if (File.Exists(configPath))
        {
            var json = File.ReadAllText(configPath);
            InstanceInfo = System.Text.Json.JsonSerializer.Deserialize<StardewInstanceInfo>(json) ?? new StardewInstanceInfo();
        }

        var modsPath = System.IO.Path.Combine(Path, "Mods");
        InstanceInfo.ModCount = Directory.Exists(modsPath) ? Directory.GetDirectories(modsPath).Length : 0;
    }
}
