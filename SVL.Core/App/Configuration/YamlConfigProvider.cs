using System;
using SVL.Core.IO;
using System.IO;
using System.Threading.Tasks;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SVL.Core.App.Configuration;

public class YamlConfigProvider : IConfigProvider
{
    private readonly string _basePath;
    private readonly ISerializer _serializer;
    private readonly IDeserializer _deserializer;

    public YamlConfigProvider(string basePath)
    {
        _basePath = basePath;
        if (!Directory.Exists(_basePath))
        {
            Directory.CreateDirectory(_basePath);
        }

        _serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    public async Task<T> LoadAsync<T>(string key) where T : class, new()
    {
        var filePath = Path.Combine(_basePath, $"{key}.yaml");
        if (!File.Exists(filePath))
        {
            return new T();
        }

        var yaml = await FileEx.ReadAllTextAsync(filePath);
        return _deserializer.Deserialize<T>(yaml) ?? new T();
    }

    public async Task SaveAsync<T>(string key, T data) where T : class
    {
        var filePath = Path.Combine(_basePath, $"{key}.yaml");
        var yaml = _serializer.Serialize(data);
        await FileEx.WriteAllTextAsync(filePath, yaml);
    }

    public Task<bool> ExistsAsync(string key)
    {
        var filePath = Path.Combine(_basePath, $"{key}.yaml");
        return Task.FromResult(File.Exists(filePath));
    }

    public async Task RemoveAsync(string key)
    {
        var filePath = Path.Combine(_basePath, $"{key}.yaml");
        if (File.Exists(filePath))
        {
            await Task.Run(() => File.Delete(filePath));
        }
    }
}
