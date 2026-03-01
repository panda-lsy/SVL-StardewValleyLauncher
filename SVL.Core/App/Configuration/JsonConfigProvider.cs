using System;
using SVL.Core.IO;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace SVL.Core.App.Configuration;

public class JsonConfigProvider : IConfigProvider
{
    private readonly string _basePath;

    public JsonConfigProvider(string basePath)
    {
        _basePath = basePath;
        if (!Directory.Exists(_basePath))
        {
            Directory.CreateDirectory(_basePath);
        }
    }

    public async Task<T> LoadAsync<T>(string key) where T : class, new()
    {
        var filePath = Path.Combine(_basePath, $"{key}.json");
        if (!File.Exists(filePath))
        {
            return new T();
        }

        var json = await FileEx.ReadAllTextAsync(filePath);
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        }) ?? new T();
    }

    public async Task SaveAsync<T>(string key, T data) where T : class
    {
        var filePath = Path.Combine(_basePath, $"{key}.json");
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        await FileEx.WriteAllTextAsync(filePath, json);
    }

    public Task<bool> ExistsAsync(string key)
    {
        var filePath = Path.Combine(_basePath, $"{key}.json");
        return Task.FromResult(File.Exists(filePath));
    }

    public async Task RemoveAsync(string key)
    {
        var filePath = Path.Combine(_basePath, $"{key}.json");
        if (File.Exists(filePath))
        {
            await Task.Run(() => File.Delete(filePath));
        }
    }
}
