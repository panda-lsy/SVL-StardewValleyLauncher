using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SVL.Core.App.Configuration;

namespace SVL.Core.App;

public static class ConfigService
{
    private static readonly Dictionary<ConfigSource, IConfigProvider> _providers = new();

    public static void Initialize()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var basePath = Path.Combine(appDataPath, "SVL");

        _providers[ConfigSource.Shared] = new JsonConfigProvider(basePath);
        _providers[ConfigSource.SharedEncrypt] = new JsonConfigProvider(basePath);
        _providers[ConfigSource.Local] = new YamlConfigProvider(Path.Combine(basePath, "local"));
        _providers[ConfigSource.GameInstance] = new JsonConfigProvider(Path.Combine(basePath, "instances"));
    }

    public static IConfigProvider GetProvider(ConfigSource source)
    {
        return _providers.TryGetValue(source, out var provider) ? provider : throw new ArgumentException($"Invalid config source: {source}");
    }

    public static async Task<T> LoadConfigAsync<T>(ConfigSource source, string key) where T : class, new()
    {
        var provider = GetProvider(source);
        return await provider.LoadAsync<T>(key);
    }

    public static async Task SaveConfigAsync<T>(ConfigSource source, string key, T data) where T : class
    {
        var provider = GetProvider(source);
        await provider.SaveAsync(key, data);
    }

    public static Task<bool> ConfigExistsAsync(ConfigSource source, string key)
    {
        var provider = GetProvider(source);
        return provider.ExistsAsync(key);
    }
}
