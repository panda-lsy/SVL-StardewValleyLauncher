using SVL.Avalonia.Models;
using System.Text.Json;

namespace SVL.Avalonia.Services;

public sealed class AppUserSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _settingsPath;

    public AppUserSettingsStore(string? storageDirectory = null)
    {
        var basePath = string.IsNullOrWhiteSpace(storageDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SVL",
                "Avalonia")
            : Path.GetFullPath(storageDirectory);

        Directory.CreateDirectory(basePath);
        _settingsPath = Path.Combine(basePath, "settings.json");
    }

    public bool Exists => File.Exists(_settingsPath);

    public AppUserSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new AppUserSettings();
            }

            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<AppUserSettings>(json, JsonOptions) ?? new AppUserSettings();
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                !HasProperty(document.RootElement, nameof(AppUserSettings.BackgroundTransparencyPercent)) &&
                HasProperty(document.RootElement, nameof(AppUserSettings.EnableTransparency)))
            {
                // 旧版 Avalonia 配置只保存开/关：开启映射到原有最大透明效果，关闭映射到不透明。
                settings.BackgroundTransparencyPercent = settings.EnableTransparency ? 100 : 0;
            }

            settings.BackgroundTransparencyPercent = Math.Clamp(settings.BackgroundTransparencyPercent, 0, 100);
            settings.EnableTransparency = settings.BackgroundTransparencyPercent > 0;
            return settings;
        }
        catch
        {
            return new AppUserSettings();
        }
    }

    public void Save(AppUserSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        AtomicFileWriter.WriteUtf8(_settingsPath, json);
    }

    public string GetSettingsPath() => _settingsPath;

    internal bool HasStoredProperty(params string[] propertyNames)
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(_settingsPath));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   propertyNames.Any(name => HasProperty(document.RootElement, name));
        }
        catch
        {
            return false;
        }
    }

    private static bool HasProperty(JsonElement root, string propertyName)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
