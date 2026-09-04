using System.Text.Json;

namespace SVL.Avalonia.Services;

public sealed class InstanceRegistryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _registryPath;

    public InstanceRegistryStore()
    {
        var basePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SVL",
            "Avalonia");

        Directory.CreateDirectory(basePath);
        _registryPath = Path.Combine(basePath, "instances-registry.json");
    }

    public List<ManualInstanceRecord> LoadManualInstances()
    {
        try
        {
            if (!File.Exists(_registryPath))
            {
                return [];
            }

            var json = File.ReadAllText(_registryPath);
            return JsonSerializer.Deserialize<List<ManualInstanceRecord>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void SaveManualInstances(IReadOnlyList<ManualInstanceRecord> records)
    {
        var json = JsonSerializer.Serialize(records, JsonOptions);
        File.WriteAllText(_registryPath, json);
    }
}

public sealed class ManualInstanceRecord
{
    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;
}
